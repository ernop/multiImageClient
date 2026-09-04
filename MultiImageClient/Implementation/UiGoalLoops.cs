#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MultiImageClient
{
    // Goal loop: a text/vision "manager" model iteratively designs image
    // prompts toward a user-stated goal, ONE image generator renders each
    // design as a normal UI job, the rendered image goes back to the manager
    // in one growing conversation, and the manager scores it and either
    // redesigns or declares the goal met. Everything sent and received is an
    // append-only entry on disk (UiGoalLoops/{id}/entries.jsonl); the manager
    // conversation is rebuilt from those entries on every call, which is what
    // makes stop / resume / fork-with-edits exact rather than approximate.
    // See docs/goal-loop-prd.md.

    public static class UiGoalLoopJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
    }

    public static class UiGoalLoopKinds
    {
        public const string Goal = "goal";
        public const string Design = "design";
        public const string RenderRequest = "render-request";
        public const string RenderResult = "render-result";
        public const string ReviewRequest = "review-request";
        public const string Review = "review";
        // system → manager: a parsed reply was recorded but its decision is
        // not accepted under the loop's rules (today: "done" on an
        // open-ended goal before the generator's limit was demonstrated).
        // The manager sees its own rejected reply and this message, and
        // must answer the same review again.
        public const string Objection = "objection";
        public const string Note = "note";

        public static bool IsManagerReply(string kind) => kind == Design || kind == Review;
        public static bool IsTextEditable(string kind)
            => kind == Goal || kind == Design || kind == RenderRequest || kind == ReviewRequest || kind == Review;
    }

    public static class UiGoalLoopParties
    {
        public const string User = "user";
        public const string Manager = "manager";
        public const string Generator = "generator";
        public const string System = "system";
    }

    public static class UiGoalLoopStatus
    {
        public const string Running = "running";
        public const string Stopped = "stopped";
        public const string Done = "done";
        public const string Exhausted = "exhausted";
        public const string Failed = "failed";
    }

    public sealed class UiGoalLoopEvaluation
    {
        public double Score { get; set; }
        public bool GoalMet { get; set; }
        public string Assessment { get; set; } = "";
        public List<string> Problems { get; set; } = new();
        public List<string> Keep { get; set; } = new();
    }

    public static class UiGoalLoopGoalKinds
    {
        // Operator setting on the loop: let the manager classify.
        public const string Auto = "auto";
        // A goal with a recognizable finished state ("a red apple on a
        // white table"): done when met.
        public const string Bounded = "bounded";
        // A goal that asks for a maximum / superlative ("as many fish as
        // possible"): there is no finished state to recognize from one good
        // image. The loop may end only after a render pushed past the best
        // result and degraded, demonstrating the generator's limit.
        public const string OpenEnded = "open-ended";

        public static bool IsManagerValue(string? value) => value == Bounded || value == OpenEnded;
        public static bool IsOperatorValue(string? value) => value == Auto || IsManagerValue(value);
    }

    // Protocol 4: every turn renders two prompts. "refine" continues the
    // lineage (an improvement of the render the manager chose to continue
    // from; on turn 1 simply the primary design). "fresh" is a from-scratch
    // re-attempt at the goal — new composition, new staging, new style —
    // written with everything learned so far, so the loop can leave a rut
    // instead of iterating on one image forever. The manager evaluates both
    // and names which one the next refine builds on (continueFrom).
    public static class UiGoalLoopVariants
    {
        public const string Refine = "refine";
        public const string Fresh = "fresh";
        public static readonly string[] All = { Refine, Fresh };
        public static bool IsValid(string? value) => value == Refine || value == Fresh;
    }

    // Protocol 5: several image generators ("sources") may render every
    // prompt. The manager sees only a stable letter per source (A, B, …),
    // never a name, so it can learn each source's behavior across turns
    // without being told what it is. The letter is the generator's index in
    // the loop's generator list.
    public static class UiGoalLoopSources
    {
        public const int MaxGenerators = 8;

        public static string Label(int index)
        {
            if (index < 0 || index >= 26)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return ((char)('A' + index)).ToString();
        }

        public static bool IsValid(string? value)
            => value != null && value.Length == 1 && value[0] >= 'A' && value[0] <= 'Z';
    }

    public sealed class UiGoalLoopGenerator
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        // "A", "B", … under protocol 5; null on single-generator loops of
        // earlier protocols (their contract had no sources).
        public string? Source { get; set; }
    }

    // One scored render on a manager reply: which variant, which source
    // (null on protocols without sources), and the evaluation.
    public sealed record UiGoalLoopScoredRender(string? Variant, string? Source, UiGoalLoopEvaluation Evaluation);

    // Identity of one render the manager was shown, for parser validation.
    public sealed record UiGoalLoopRenderKey(string? Variant, string? Source);

    public sealed class UiGoalLoopManagerReply
    {
        public string Reasoning { get; set; } = "";
        // Protocol 3: the manager's classification of the GOAL, required on
        // the first design ("bounded" | "open-ended"); optional afterwards.
        public string? GoalKind { get; set; }
        // Evaluation of the turn's render; under protocol 4 this is the
        // REFINE render's evaluation and FreshEvaluation is the fresh one's.
        public UiGoalLoopEvaluation? Evaluation { get; set; }
        public UiGoalLoopEvaluation? FreshEvaluation { get; set; }
        // Protocol 5: one evaluation per render shown (variant × source);
        // Evaluation/FreshEvaluation stay null on such replies.
        public List<UiGoalLoopScoredRender>? RenderEvaluations { get; set; }
        // Protocol 4: which of this turn's renders the next refine builds
        // on ("refine" | "fresh"); required on a render decision after a
        // review, absent on the first design. Protocol 5 adds the source.
        public string? ContinueFrom { get; set; }
        public string? ContinueFromSource { get; set; }
        // "render" | "done"
        public string Decision { get; set; } = "";
        public string? Prompt { get; set; }
        // Protocol 4: the from-scratch prompt rendered beside Prompt.
        public string? FreshPrompt { get; set; }
        public string? DesignNotes { get; set; }
        public string? FreshDesignNotes { get; set; }
        public string? DoneStatement { get; set; }
        public int? BestTurn { get; set; }
        // Protocol 4: which render of BestTurn is the best ("refine" | "fresh").
        public string? BestVariant { get; set; }
        // Protocol 5: which source rendered the best image.
        public string? BestSource { get; set; }

        public UiGoalLoopEvaluation? EvaluationOf(string? variant, string? source = null)
        {
            if (RenderEvaluations != null)
            {
                return RenderEvaluations.FirstOrDefault(r => r.Variant == variant && r.Source == source)?.Evaluation;
            }
            return variant == UiGoalLoopVariants.Fresh ? FreshEvaluation : Evaluation;
        }

        // Every evaluation on this reply with the render it scores. A reply
        // carrying only Evaluation (pre-protocol-4, or a fresh render that
        // never happened) yields it under a null variant; sources are null
        // before protocol 5.
        public IEnumerable<UiGoalLoopScoredRender> Evaluations()
        {
            if (RenderEvaluations != null)
            {
                foreach (var r in RenderEvaluations)
                {
                    yield return r;
                }
                yield break;
            }
            if (Evaluation != null)
            {
                yield return new UiGoalLoopScoredRender(FreshEvaluation != null ? UiGoalLoopVariants.Refine : null, null, Evaluation);
            }
            if (FreshEvaluation != null)
            {
                yield return new UiGoalLoopScoredRender(UiGoalLoopVariants.Fresh, null, FreshEvaluation);
            }
        }
    }

    public sealed class UiGoalLoopManagerData
    {
        public string Model { get; set; } = "";
        public string? ProviderReasoning { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public decimal? CostUsd { get; set; }
        public UiGoalLoopManagerReply? Parsed { get; set; }
        public string? ParseError { get; set; }
        // Exact byte length of the request body sent for this reply.
        public long? RequestBytes { get; set; }
        // The provider's own refusal / block / cut-off statement, when the
        // response was not a normal completion. Distinct from ParseError:
        // the model did not answer, rather than answering off-contract.
        public string? ProviderStop { get; set; }
    }

    public sealed class UiGoalLoopRenderData
    {
        public string? JobId { get; set; }
        // Protocol 4: "refine" | "fresh"; null on single-render loops.
        public string? Variant { get; set; }
        // Protocol 5: the manager-facing source letter of this generator.
        public string? Source { get; set; }
        public string GeneratorKey { get; set; } = "";
        public string GeneratorLabel { get; set; } = "";
        public string Shape { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Quality { get; set; } = "";
        public string Moderation { get; set; } = "";
        public bool? Ok { get; set; }
        public string? ImageUrl { get; set; }
        public string? ThumbUrl { get; set; }
        public string? Size { get; set; }
        public decimal? Cost { get; set; }
        public string? Label { get; set; }
        public string? MediaType { get; set; }
        public string? ErrorHint { get; set; }
        public string? ErrorHintUrl { get; set; }
    }

    public sealed class UiGoalLoopSentImage
    {
        public string JobId { get; set; } = "";
        // Protocol 4: which of the turn's renders this is ("refine" | "fresh").
        public string? Variant { get; set; }
        // Protocol 5: source letter.
        public string? Source { get; set; }
        public string GeneratorKey { get; set; } = "";
        public int ImageIndex { get; set; }
        public string Url { get; set; } = "";
        public string? ThumbUrl { get; set; }
        public string Mime { get; set; } = "";
        public int Bytes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        // Legacy (protocol-1 loops written before 2026-09-04 full-resolution
        // transport): true when Width/Height/Mime/Bytes describe a 1024 px
        // JPEG copy rather than the original. New records are always false and
        // describe the original file.
        public bool Downscaled { get; set; }
        // How the image reaches the manager on the first call that carries
        // it, per the manager provider's published limits (see
        // UiGoalLoopImageTransport). Later calls recompute; each call's
        // exact per-image conformance is in that call's redacted request.
        public string? Transport { get; set; }
    }

    public sealed class UiGoalLoopEntry
    {
        public int Index { get; set; }
        public int Turn { get; set; }
        public string Kind { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public long At { get; set; }
        public long? Ms { get; set; }
        public string Text { get; set; } = "";
        public bool Edited { get; set; }
        public string? OriginalText { get; set; }
        public string? Error { get; set; }
        public UiGoalLoopManagerData? Manager { get; set; }
        public UiGoalLoopRenderData? Render { get; set; }
        public List<UiGoalLoopSentImage>? Images { get; set; }
        // Exact provider request (base64 image payloads replaced by
        // placeholders) and the raw provider response, on manager replies.
        public string? WireRequest { get; set; }
        public string? WireResponse { get; set; }

        public UiGoalLoopEntry Clone()
            => JsonSerializer.Deserialize<UiGoalLoopEntry>(
                JsonSerializer.Serialize(this, UiGoalLoopJson.Options), UiGoalLoopJson.Options)!;
    }

    public sealed class UiGoalLoop
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Goal { get; set; } = "";
        // The first (or only) generator; kept for stored loops and readers.
        public string GeneratorKey { get; set; } = "";
        public string GeneratorLabel { get; set; } = "";
        // Protocol 5: every generator, in source-letter order. Null on
        // loops stored before protocol 5.
        public List<UiGoalLoopGenerator>? Generators { get; set; }
        public string ManagerKey { get; set; } = "";
        public string ManagerLabel { get; set; } = "";
        public string ManagerModel { get; set; } = "";
        public int MaxTurns { get; set; } = UiGoalLoopPlanner.DefaultMaxTurns;
        public string Shape { get; set; } = "auto";
        public string Detail { get; set; } = "standard";
        public string Quality { get; set; } = "high";
        public string Moderation { get; set; } = "low";
        public string CreatedBy { get; set; } = "";
        public string CreatorLogin { get; set; } = "";
        public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string? ParentLoopId { get; set; }
        public int? ForkedAtEntry { get; set; }
        public int ProtocolVersion { get; set; } = UiGoalLoopProtocol.Version;
        public string SystemPrompt { get; set; } = "";
        // Operator's declaration at start: "auto" (the manager classifies in
        // its first design), "bounded", or "open-ended". Loops written before
        // protocol 3 have no such rule; see EffectiveGoalKind.
        public string GoalKind { get; set; } = UiGoalLoopGoalKinds.Auto;
        // The kind actually enforced: the operator's explicit choice, else
        // the manager's first-design classification; null until known, and
        // null for pre-protocol-3 loops whose contract had no goal kinds.
        public string? EffectiveGoalKind { get; set; }
        // How the manager's goal kind was decided ("operator" | "manager"),
        // for the page; null while unknown.
        public string? GoalKindSource { get; set; }
        // Path of the most recently built all-turns contact sheet, relative
        // to the loop folder, plus the entry count it covered, so the page
        // can offer "rebuild" once the loop has advanced past it.
        public string? SheetFile { get; set; }
        public int? SheetEntryCount { get; set; }
        public int? SheetTurns { get; set; }

        public string Status { get; set; } = UiGoalLoopStatus.Running;
        public string StatusDetail { get; set; } = "";
        // What the runner is doing right now ("waiting for manager",
        // "rendering with X"); blank when idle.
        public string Activity { get; set; } = "";
        public long? ActivitySinceUnixMs { get; set; }
        public long UpdatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public int TurnsRendered { get; set; }
        public int EntryCount { get; set; }
        // Bumped whenever an existing entry is rewritten in place (an
        // unrendered render-request being filled). Cursor-based readers
        // refetch from zero when it changes; appends do not bump it.
        public long Revision { get; set; }
        public double? LastScore { get; set; }
        public double? BestScore { get; set; }
        public int? BestTurn { get; set; }
        // Protocol 4: which render of BestTurn holds BestScore.
        public string? BestVariant { get; set; }
        // Protocol 5: which source rendered it.
        public string? BestSource { get; set; }
        // Number of renders in every turn: 2 (refine + fresh) × the number
        // of generators from protocol 5, 2 under protocol 4, 1 before.
        // Derived from ProtocolVersion; stored for readers.
        public int RendersPerTurn => ProtocolVersion >= 5
            ? 2 * GeneratorList().Count
            : (ProtocolVersion >= 4 ? 2 : 1);

        // Every generator of the loop. Loops stored before protocol 5 have
        // exactly one, without a source letter.
        public IReadOnlyList<UiGoalLoopGenerator> GeneratorList()
            => Generators is { Count: > 0 }
                ? Generators
                : new[] { new UiGoalLoopGenerator { Key = GeneratorKey, Label = GeneratorLabel, Source = null } };
        public decimal ManagerCostUsd { get; set; }
        public bool ManagerCostKnown { get; set; } = true;
        public decimal RenderCostUsd { get; set; }
        public int ManagerInputTokens { get; set; }
        public int ManagerOutputTokens { get; set; }

        public UiGoalLoop Clone()
            => JsonSerializer.Deserialize<UiGoalLoop>(
                JsonSerializer.Serialize(this, UiGoalLoopJson.Options), UiGoalLoopJson.Options)!;
    }

    /// The manager-facing contract: system prompt, per-turn message builders,
    /// and the strict fail-closed parser for the manager's JSON replies.
    public static class UiGoalLoopProtocol
    {
        // Version 2 (2026-09-04): the "reasoning" field description was
        // reworded. The version-1 text ("your full considerations: what you
        // see, what worked and failed, what you will try and why,
        // alternatives you considered") made Claude Fable 5.1 return
        // stop_reason "refusal" / category "reasoning_extraction" with zero
        // output tokens on every call (2/2 replays); Sonnet 5 and Opus 5
        // accepted it. Bisected live: only that one line triggers it; the
        // field name does not. The current wording passed 2/2 on Fable 5.1.
        //
        // Version 3 (2026-09-04): goal kinds and stopping rules. A live loop
        // on "as many different fish as possible" scored 20 labeled fish
        // 9/10 with goalMet true, pushed to 30 with no defect, and then
        // declared done because the budget was exhausted ("with renders
        // exhausted and a strong result in hand, stopping is the right
        // call"). Both moves contradict an open-ended goal: no ceiling had
        // been demonstrated, and the budget belongs to the operator. The
        // prompt now defines bounded vs open-ended goals, requires the
        // manager to classify the goal, forbids budget-motivated stopping,
        // requires evidence for "unlikely to improve", and the server
        // enforces the open-ended rule mechanically (UiGoalLoopPlanner
        // .IsDonePermitted → objection entry → re-ask with "done" barred).
        //
        // Version 4 (2026-09-04): two renders per turn. Loops kept
        // iterating on one image — every turn a small edit of the previous
        // prompt — and never left a weak composition. Now each turn renders
        // a REFINE prompt (improving the render the manager chose to
        // continue from) and a FRESH prompt (a from-scratch re-attempt with a
        // new composition, staging and style, informed by what has been
        // learned). The manager scores both and names which one the next
        // refine builds on, so the lineage can jump to the fresh image.
        //
        // Version 5 (2026-09-04): several generators per loop. The operator
        // picks 1..8 image generators; every prompt of a turn is rendered by
        // all of them. The manager sees each generator only as a stable
        // letter ("source A", "source B", …), scores every render, and names
        // the exact render (variant + source) the next refine builds on. The
        // goal remains ONE best image from any source. The reply carries an
        // "evaluations" array (one per render shown) instead of the
        // evaluation/freshEvaluation pair.
        public const int Version = 5;

        public const string SystemPrompt =
            "You are the MANAGER of an iterative image-making loop. You cannot draw. One or more image generators render the text prompts you write. They are called SOURCES and are labeled by letter (source A, source B, …). The same letter is the same generator on every turn. Their identities are withheld and you may not ask for them. The operator gives you a GOAL. Your job is to obtain ONE image, from any source, that best satisfies and covers the GOAL, by writing image prompts, studying every rendered result, scoring each against the goal, and deciding whether to render again with changed designs or to stop.\n\n"
            + "How the loop works:\n"
            + "1. The operator sends the GOAL and tells you how many sources there are. You reply with your first design: TWO prompts (see \"two renders per turn\").\n"
            + "2. The operator renders both prompts exactly as written on EVERY source (the operator may edit a prompt first and will tell you when that happened) and sends every resulting image back to you, or the generator's error text for any render that failed.\n"
            + "3. You evaluate each result against the GOAL, give each a score, explain your reasoning, choose exactly one render (variant + source) to continue from, and either provide the next two prompts to render or declare the loop done.\n"
            + "4. Steps 2-3 repeat. Each message tells you the current turn number and how many turns remain. When zero turns remain and you still want to render, say so with decision \"render\": the loop pauses and the operator may grant more turns.\n\n"
            + "Two renders per turn (per source):\n"
            + "- \"prompt\" is the REFINE render: an improvement of the render you chose to continue from (continueFrom). Keep what worked, fix what did not, push further toward the goal. On turn 1 it is simply your primary design.\n"
            + "- \"freshPrompt\" is the FRESH render: a from-scratch re-attempt at the GOAL. New composition, new subject staging, new camera and framing, new medium or rendering style, new palette — a different way to convey the same point, written with everything you have learned about the sources so far. It must not be a variant of \"prompt\": if a reader could mistake one for an edit of the other, the fresh prompt is not fresh. On turn 1 both prompts are new; make them two clearly different approaches.\n"
            + "- Every source renders both prompts. Learn what each source does well and badly and use that: you may write the refine prompt for the source you continue from (its strengths, its failure modes), while still reading the other sources' renders as information. A source that keeps scoring lower is still rendered; it costs you nothing to keep learning from it.\n"
            + "- After seeing all renders, set continueFrom to the exact render your next refine prompt builds on: { \"variant\": \"refine\" or \"fresh\", \"source\": letter }. Choosing a fresh render moves the lineage onto the new composition; choosing another source moves it onto that generator. Choose by score against the GOAL and by how much headroom each direction has, not by habit.\n\n"
            + "Goal kinds — classify the GOAL in your first design and report it in goalKind:\n"
            + "- \"bounded\": the GOAL names a finished state you can recognize in one image (a subject, a scene, a style, a specific composition). It is done when that state is met.\n"
            + "- \"open-ended\": the GOAL asks for a maximum, a superlative, or \"as ... as possible\" (as many as possible, the most detailed, the largest crowd, the longest legible list). No single good image satisfies it. What satisfies it is the best image at the sources' demonstrated limit.\n"
            + "Rules for open-ended goals:\n"
            + "- Every refine render must push the maximized quantity beyond the best acceptable result so far. When the last step succeeded cleanly, take a larger step next; a run of clean successes means the limit is still ahead of you. The fresh render may explore a different way of fitting more in.\n"
            + "- Continue until a refine render on the source that holds your best result overshoots and degrades (wrong counts, duplicates, illegible or misspelled text, merged or missing subjects, lost coherence). That degradation is the only evidence of the limit. Then bisect between the best acceptable result and the failed one while turns remain.\n"
            + "- goalMet stays false until the limit has been demonstrated. The score measures how far the maximized quantity was pushed, with defects as deductions, so a larger clean result outscores a smaller clean one.\n"
            + "- The loop does not accept decision \"done\" on an open-ended goal while the best source's latest refine render is also your best: it replies with an objection and asks for the review again with \"done\" barred. Do not attempt it.\n\n"
            + "The turn budget belongs to the operator, not to you. The number of turns remaining is never a reason to declare done. If you would render again given one more turn, answer decision \"render\" with both prompts — also when zero turns remain; the loop then pauses and the operator decides whether to grant more turns. Declaring done because the budget is exhausted is a contract violation.\n\n"
            + "Working method:\n"
            + "- Iterate deliberately. Change one or two things per turn when the result is close; change the whole approach when it is far off. Vary composition, subject staging, camera and framing language, medium and rendering style, level of detail, ordering and emphasis of clauses, explicit negative instructions, text-rendering instructions, color palette, and lighting language. Learn what each source responds to and exploit it.\n"
            + "- You may return to an earlier direction that worked better; say so explicitly in your reasoning and reference the turn number.\n"
            + "- Prompts must be complete and self-contained. The generators never see this conversation, only the prompt text.\n"
            + "- Default lighting and clarity unless the GOAL asks otherwise: clear, bright, full normal daytime lighting; not dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark. Prefer readable, coherent, visually organized images with clean composition, clear separation of subjects or groups, concise high-contrast text when text is needed, and attractive balanced color. State this preference inside your prompt text when it matters.\n"
            + "- Score honestly on a 0-10 scale: 10 = the goal is fully met with no visible defects; 7-8 = clearly on target with fixable defects; 4-6 = partially on target; 0-3 = wrong or unusable. Set goalMet true only at 9 or above (and, for open-ended goals, only once the limit is demonstrated).\n"
            + "- Declare done only when the goal is met (bounded goals), or when you have evidence from the renders you have seen that further renders will not improve on the best result: a change that failed to improve it, or a defect the sources have repeatedly failed to fix. A sequence of renders that each improved on the last is evidence that the next one would improve too. Name the best render in bestTurn, bestVariant, and bestSource.\n\n"
            + "Reply format — every reply must be exactly one JSON object and nothing else (no prose before or after, no markdown fence), with these fields:\n"
            + "{\n"
            + "  \"reasoning\": string — your design rationale for this turn: what each image shows, what to keep, what to change, why you continue from the render you chose;\n"
            + "  \"goalKind\": \"bounded\" or \"open-ended\" — required in your first design; repeat it in later replies;\n"
            + "  \"evaluations\": null on your first design (nothing rendered yet); otherwise an array with exactly one object per render you were shown: { \"variant\": \"refine\" or \"fresh\", \"source\": letter, \"score\": number 0-10, \"goalMet\": boolean, \"assessment\": string, \"problems\": [string], \"keep\": [string] } (a failed render scores 0 with the failure as its assessment);\n"
            + "  \"continueFrom\": { \"variant\": \"refine\" or \"fresh\", \"source\": letter } — required when decision is \"render\" after a review: the render your next refine prompt builds on; null on your first design;\n"
            + "  \"decision\": \"render\" or \"done\";\n"
            + "  \"prompt\": string — the complete REFINE prompt to render next; required when decision is \"render\";\n"
            + "  \"freshPrompt\": string — the complete FRESH from-scratch prompt to render next; required when decision is \"render\";\n"
            + "  \"designNotes\": string — what the refine design attempts and which controls you changed versus the render you continue from;\n"
            + "  \"freshDesignNotes\": string — what the fresh design attempts and how it differs in composition, staging, and style;\n"
            + "  \"doneStatement\": string — required when decision is \"done\": which render is best and why the loop should stop;\n"
            + "  \"bestTurn\": integer or null — the turn holding the best render so far;\n"
            + "  \"bestVariant\": \"refine\" or \"fresh\" or null — which render of bestTurn is the best;\n"
            + "  \"bestSource\": letter or null — which source rendered it.\n"
            + "}\n"
            + "The word JSON appears here so that JSON-only output modes engage: reply with JSON.";

        // Every user-role message names JSON explicitly: OpenAI's json_object
        // output mode rejects requests whose input messages never say "json"
        // (the instructions field does not count; observed 2026-09-04).
        // Pre-protocol-3 loops keep their original goal message wording so a
        // resumed conversation replays exactly what the manager saw.
        public static string BuildGoalMessage(
            string goal, int maxTurns, int protocolVersion = Version, string? declaredGoalKind = null, int sourceCount = 1)
        {
            var sb = new StringBuilder();
            sb.Append("GOAL:\n").Append(goal.Trim());
            if (protocolVersion >= 5)
            {
                if (sourceCount < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(sourceCount));
                }
                var letters = string.Join(", ", Enumerable.Range(0, sourceCount).Select(UiGoalLoopSources.Label));
                sb.Append($"\n\nThis is the start of the loop. Up to {maxTurns} turn(s) are allowed. There {(sourceCount == 1 ? "is 1 source" : $"are {sourceCount} sources")} ({letters}); every turn, each source renders your refine prompt and your fresh prompt, so you will see {2 * sourceCount} render(s) per turn. Your objective is one image, from any source, that best satisfies and covers the GOAL.");
            }
            else if (protocolVersion >= 4)
            {
                sb.Append($"\n\nThis is the start of the loop. Up to {maxTurns} turn(s) are allowed; every turn renders your refine prompt and your fresh prompt.");
            }
            else
            {
                sb.Append($"\n\nThis is the start of the loop. Up to {maxTurns} render(s) are allowed.");
            }
            if (protocolVersion < 3)
            {
                sb.Append(" Provide your first design now as the required JSON object (evaluation must be null).");
                return sb.ToString();
            }
            if (UiGoalLoopGoalKinds.IsManagerValue(declaredGoalKind))
            {
                sb.Append($" The operator has classified this GOAL as \"{declaredGoalKind}\"; set goalKind to that value and apply its rules.");
            }
            else
            {
                sb.Append(" Classify the GOAL as \"bounded\" or \"open-ended\" in goalKind.");
            }
            if (protocolVersion >= 5)
            {
                sb.Append(" Provide your first design now as the required JSON object: two clearly different prompts in \"prompt\" and \"freshPrompt\" (evaluations and continueFrom must be null).");
            }
            else if (protocolVersion >= 4)
            {
                sb.Append(" Provide your first design now as the required JSON object: two clearly different prompts in \"prompt\" and \"freshPrompt\" (evaluation, freshEvaluation, and continueFrom must be null).");
            }
            else
            {
                sb.Append(" Provide your first design now as the required JSON object (evaluation must be null).");
            }
            return sb.ToString();
        }

        // One render of a turn, as the review request describes it.
        public sealed record UiGoalLoopTurnRender(
            string Variant,
            UiGoalLoopRenderData Render,
            string RenderedPrompt,
            string? DesignPrompt,
            string? RenderError,
            UiGoalLoopSentImage? Sent,
            string? Source = null);

        // Protocol 5 review request: every render of the turn, refine renders
        // first then fresh, each in source-letter order, with the attached
        // images in that same order. Sources lists every letter of the loop
        // so a source whose render never happened is reported as such.
        public static string BuildMultiSourceReviewRequestText(
            int turn,
            int maxTurns,
            IReadOnlyList<UiGoalLoopTurnRender> renders,
            IReadOnlyList<string> sources,
            UiGoalLoopRenderKey? continuedFrom,
            string? effectiveGoalKind,
            int? bestTurnSoFar,
            string? bestVariantSoFar,
            string? bestSourceSoFar,
            double? bestScoreSoFar)
        {
            if (sources.Count == 0)
            {
                throw new ArgumentException("a review needs at least one source", nameof(sources));
            }
            var sb = new StringBuilder();
            var remaining = Math.Max(0, maxTurns - turn);
            sb.Append($"Turn {turn} of {maxTurns}. Both of your turn-{turn} prompts were rendered by {(sources.Count == 1 ? "source A" : $"all {sources.Count} sources ({string.Join(", ", sources)})")}.");
            if (continuedFrom != null)
            {
                sb.Append($" Your refine prompt built on the {continuedFrom.Variant} render of source {continuedFrom.Source} from turn {turn - 1}.");
            }
            var attached = 0;
            foreach (var variant in UiGoalLoopVariants.All)
            {
                foreach (var source in sources)
                {
                    var r = renders.FirstOrDefault(x => x.Variant == variant && x.Source == source);
                    sb.Append($"\n\n{variant.ToUpperInvariant()} render, source {source}: ");
                    if (r == null)
                    {
                        sb.Append("not rendered (no prompt was supplied for it).");
                        continue;
                    }
                    if (r.Render.Ok == true && r.Sent != null)
                    {
                        attached++;
                        sb.Append("the source returned an image");
                        if (!string.IsNullOrWhiteSpace(r.Render.Size))
                        {
                            sb.Append($" ({r.Render.Size} pixels)");
                        }
                        sb.Append($"; it is attached image #{attached} of this message at its full rendered resolution");
                        if (!string.IsNullOrWhiteSpace(r.Sent.Transport) && r.Sent.Transport.Contains("downscaled", StringComparison.Ordinal))
                        {
                            sb.Append(" except as your provider's published input limit requires: ").Append(r.Sent.Transport);
                        }
                        sb.Append('.');
                    }
                    else
                    {
                        sb.Append("the source FAILED; no image was produced. Generator error: ");
                        sb.Append(string.IsNullOrWhiteSpace(r.RenderError) ? "(no error text)" : r.RenderError.Trim());
                        sb.Append(" Score it as a failed attempt and diagnose what in the prompt likely caused it on this source.");
                    }
                    if (r.DesignPrompt != null
                        && !string.Equals(r.DesignPrompt.Trim(), r.RenderedPrompt.Trim(), StringComparison.Ordinal))
                    {
                        sb.Append(" Note: the operator edited this prompt before rendering. The exact prompt that was rendered is:\n\"\"\"\n");
                        sb.Append(r.RenderedPrompt.Trim());
                        sb.Append("\n\"\"\"");
                    }
                }
            }
            sb.Append($"\n\nEvaluate every render against the GOAL (one \"evaluations\" entry per render, each naming its variant and source), choose continueFrom (variant + source), and reply with the required JSON object with both next prompts. Turns remaining after this turn: {remaining}.");
            if (effectiveGoalKind == UiGoalLoopGoalKinds.OpenEnded)
            {
                sb.Append(" This GOAL is open-ended: the remaining budget is never a reason to stop, and \"done\" is accepted only after a refine render on the source holding your best result has pushed past that result and degraded.");
                if (bestTurnSoFar.HasValue && bestScoreSoFar.HasValue)
                {
                    sb.Append($" Best so far: turn {bestTurnSoFar.Value}");
                    if (bestVariantSoFar != null)
                    {
                        sb.Append($" {bestVariantSoFar} render");
                    }
                    if (bestSourceSoFar != null)
                    {
                        sb.Append($" of source {bestSourceSoFar}");
                    }
                    sb.Append($" (score {bestScoreSoFar.Value:0.#}).");
                }
            }
            if (remaining == 0)
            {
                sb.Append(" If you still want another turn, answer with decision \"render\" and both prompts anyway; the loop will pause and the operator may grant more turns.");
            }
            return sb.ToString();
        }

        // Protocol 4 review request: both renders of the turn, in variant
        // order (refine, then fresh), with the attached images in that same
        // order. A missing variant (never requested) is reported as such.
        public static string BuildPairReviewRequestText(
            int turn,
            int maxTurns,
            IReadOnlyList<UiGoalLoopTurnRender> renders,
            string? continuedFrom,
            string? effectiveGoalKind,
            int? bestTurnSoFar,
            string? bestVariantSoFar,
            double? bestScoreSoFar)
        {
            var sb = new StringBuilder();
            var remaining = Math.Max(0, maxTurns - turn);
            sb.Append($"Turn {turn} of {maxTurns}. Both of your turn-{turn} prompts were rendered.");
            if (continuedFrom != null)
            {
                sb.Append($" Your refine prompt built on the {continuedFrom} render of turn {turn - 1}.");
            }
            var attached = 0;
            foreach (var variant in UiGoalLoopVariants.All)
            {
                var r = renders.FirstOrDefault(x => x.Variant == variant);
                sb.Append($"\n\n{variant.ToUpperInvariant()} render: ");
                if (r == null)
                {
                    sb.Append("not rendered (no prompt was supplied for it).");
                    continue;
                }
                if (r.Render.Ok == true && r.Sent != null)
                {
                    attached++;
                    sb.Append($"the generator returned an image");
                    if (!string.IsNullOrWhiteSpace(r.Render.Size))
                    {
                        sb.Append($" ({r.Render.Size} pixels)");
                    }
                    sb.Append($"; it is attached image #{attached} of this message at its full rendered resolution");
                    if (!string.IsNullOrWhiteSpace(r.Sent.Transport) && r.Sent.Transport.Contains("downscaled", StringComparison.Ordinal))
                    {
                        sb.Append(" except as your provider's published input limit requires: ").Append(r.Sent.Transport);
                    }
                    sb.Append('.');
                }
                else
                {
                    sb.Append("the generator FAILED; no image was produced. Generator error: ");
                    sb.Append(string.IsNullOrWhiteSpace(r.RenderError) ? "(no error text)" : r.RenderError.Trim());
                    sb.Append(" Score it as a failed attempt and diagnose what in the prompt likely caused it.");
                }
                if (r.DesignPrompt != null
                    && !string.Equals(r.DesignPrompt.Trim(), r.RenderedPrompt.Trim(), StringComparison.Ordinal))
                {
                    sb.Append(" Note: the operator edited this prompt before rendering. The exact prompt that was rendered is:\n\"\"\"\n");
                    sb.Append(r.RenderedPrompt.Trim());
                    sb.Append("\n\"\"\"");
                }
            }
            sb.Append($"\n\nEvaluate each render against the GOAL (evaluation for the refine render, freshEvaluation for the fresh render), choose continueFrom, and reply with the required JSON object with both next prompts. Turns remaining after this turn: {remaining}.");
            if (effectiveGoalKind == UiGoalLoopGoalKinds.OpenEnded)
            {
                sb.Append(" This GOAL is open-ended: the remaining budget is never a reason to stop, and \"done\" is accepted only after a refine render has pushed past your best result and degraded.");
                if (bestTurnSoFar.HasValue && bestScoreSoFar.HasValue)
                {
                    sb.Append($" Best so far: turn {bestTurnSoFar.Value}");
                    if (bestVariantSoFar != null)
                    {
                        sb.Append($" {bestVariantSoFar} render");
                    }
                    sb.Append($" (score {bestScoreSoFar.Value:0.#}).");
                }
            }
            if (remaining == 0)
            {
                sb.Append(" If you still want another turn, answer with decision \"render\" and both prompts anyway; the loop will pause and the operator may grant more turns.");
            }
            return sb.ToString();
        }

        // The message that follows a rejected "done": names the rule, the
        // evidence on file, and exactly which decision is now required.
        public static string BuildObjectionText(
            int turn, int maxTurns, int bestTurn, double bestScore, double latestScore, int protocolVersion = Version,
            string? bestVariant = null, string? bestSource = null)
        {
            var remaining = Math.Max(0, maxTurns - turn);
            var pair = protocolVersion >= 4;
            var sourced = protocolVersion >= 5 && bestSource != null;
            var sb = new StringBuilder();
            sb.Append("Your reply was recorded, but its decision \"done\" is not accepted. ");
            sb.Append(sourced
                ? "This GOAL is open-ended: the loop may end only after a refine render on the source holding the best result has pushed past that result and degraded, demonstrating that source's limit. "
                : pair
                    ? "This GOAL is open-ended: the loop may end only after a refine render has pushed past the best result and degraded, demonstrating the generator's limit. "
                    : "This GOAL is open-ended: the loop may end only after a render has pushed past the best result and degraded, demonstrating the generator's limit. ");
            sb.Append($"So far the best result is turn {bestTurn}");
            if (pair && bestVariant != null)
            {
                sb.Append(sourced ? $" ({bestVariant} render of source {bestSource})" : $" ({bestVariant} render)");
            }
            sb.Append($" (score {bestScore:0.#}) and the latest {(pair ? "refine " : "")}render{(sourced ? $" of source {bestSource}" : "")}, turn {turn}, scored {latestScore:0.#}; no later {(pair ? "refine " : "")}render{(sourced ? " on that source" : "")} has scored below the best, so no limit has been demonstrated. ");
            sb.Append(pair
                ? $"Reply again to the same review with the required JSON object. Decision must be \"render\", with a refine prompt that pushes the maximized quantity further than turn {bestTurn} (take a larger step than last time) and a fresh prompt. \"done\" is barred for this reply. "
                : $"Reply again to the same review with the required JSON object. Decision must be \"render\", with a prompt that pushes the maximized quantity further than turn {bestTurn} (take a larger step than last time). \"done\" is barred for this reply. ");
            sb.Append($"{(pair ? "Turns" : "Renders")} remaining after turn {turn}: {remaining}.");
            if (remaining == 0)
            {
                sb.Append(" The budget is exhausted, and that is not a reason to stop: answer \"render\" anyway; the loop pauses and the operator decides whether to grant more turns.");
            }
            return sb.ToString();
        }

        public static string BuildReviewRequestText(
            int turn,
            int maxTurns,
            UiGoalLoopRenderData render,
            string renderedPrompt,
            string? designPrompt,
            string? renderError,
            UiGoalLoopSentImage? sent,
            string? effectiveGoalKind = null,
            int? bestTurnSoFar = null,
            double? bestScoreSoFar = null)
        {
            var sb = new StringBuilder();
            var remaining = Math.Max(0, maxTurns - turn);
            sb.Append($"Turn {turn} of {maxTurns}. ");
            if (render.Ok == true && sent != null)
            {
                sb.Append($"The image generator rendered your turn-{turn} prompt and returned an image");
                if (!string.IsNullOrWhiteSpace(render.Size))
                {
                    sb.Append($" ({render.Size} pixels");
                    if (sent.Downscaled)
                    {
                        sb.Append($"; attached here downscaled to {sent.Width}x{sent.Height} for transport");
                    }
                    sb.Append(')');
                }
                sb.Append(". The image is attached to this message at its full rendered resolution");
                if (!string.IsNullOrWhiteSpace(sent.Transport) && sent.Transport.Contains("downscaled", StringComparison.Ordinal))
                {
                    sb.Append(" except as your provider's published input limit requires: ").Append(sent.Transport);
                }
                sb.Append('.');
            }
            else
            {
                sb.Append($"The image generator FAILED to render your turn-{turn} prompt. No image was produced. Generator error: ");
                sb.Append(string.IsNullOrWhiteSpace(renderError) ? "(no error text)" : renderError.Trim());
                sb.Append(" Treat this as a failed attempt: diagnose what in the prompt likely caused it and adapt.");
            }
            if (designPrompt != null
                && !string.Equals(designPrompt.Trim(), renderedPrompt.Trim(), StringComparison.Ordinal))
            {
                sb.Append("\n\nNote: the operator edited your prompt before rendering. The exact prompt that was rendered is:\n\"\"\"\n");
                sb.Append(renderedPrompt.Trim());
                sb.Append("\n\"\"\"");
            }
            sb.Append($"\n\nEvaluate this result against the GOAL and reply with the required JSON object. Renders remaining after this turn: {remaining}.");
            if (effectiveGoalKind == UiGoalLoopGoalKinds.OpenEnded)
            {
                sb.Append(" This GOAL is open-ended: the remaining budget is never a reason to stop, and \"done\" is accepted only after a render has pushed past your best result and degraded.");
                if (bestTurnSoFar.HasValue && bestScoreSoFar.HasValue)
                {
                    sb.Append($" Best so far: turn {bestTurnSoFar.Value} (score {bestScoreSoFar.Value:0.#}).");
                }
            }
            if (remaining == 0)
            {
                sb.Append(" If you still want another render, answer with decision \"render\" and your prompt anyway; the loop will pause and the operator may grant more turns.");
            }
            return sb.ToString();
        }

        public static string StripMarkdownFence(string raw)
        {
            var s = raw.Trim();
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = s.IndexOf('\n');
                var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline >= 0 && lastFence > firstNewline)
                {
                    s = s.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
                }
            }
            return s;
        }

        /// Strict parse. Anything that is not the required object, or that
        /// lacks a required field for its position in the loop, throws
        /// InvalidDataException; the caller records the raw reply alongside
        /// the error and never invents a design from prose.
        /// protocolVersion is the loop's recorded protocol (a resumed older
        /// loop keeps its contract); permitDone is false when the loop has
        /// objected to a "done" and re-asked, so a repeated "done" is a
        /// contract violation rather than a decision.
        public static UiGoalLoopManagerReply ParseManagerReply(
            string raw, bool expectEvaluation, int protocolVersion = Version, bool permitDone = true,
            IReadOnlyList<UiGoalLoopRenderKey>? shownRenders = null)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidDataException("the manager returned no text");
            }
            if (protocolVersion >= 5 && expectEvaluation && (shownRenders == null || shownRenders.Count == 0))
            {
                throw new ArgumentException("protocol 5 review parsing needs the list of renders the manager was shown", nameof(shownRenders));
            }
            var s = StripMarkdownFence(raw);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the reply's JSON root is not an object");
                }
                var reply = new UiGoalLoopManagerReply
                {
                    Reasoning = RequireString(root, "reasoning"),
                    Decision = RequireString(root, "decision").Trim().ToLowerInvariant(),
                    DesignNotes = OptionalString(root, "designNotes"),
                    DoneStatement = OptionalString(root, "doneStatement"),
                    Prompt = OptionalString(root, "prompt"),
                    GoalKind = OptionalString(root, "goalKind")?.ToLowerInvariant(),
                };
                if (reply.Decision != "render" && reply.Decision != "done")
                {
                    throw new JsonException($"\"decision\" must be \"render\" or \"done\", got \"{reply.Decision}\"");
                }
                if (reply.Decision == "done" && !permitDone)
                {
                    throw new JsonException(
                        "decision \"done\" is barred for this reply: the loop objected to the previous \"done\" and asked for a render");
                }
                if (protocolVersion >= 4)
                {
                    reply.FreshPrompt = OptionalString(root, "freshPrompt");
                    reply.FreshDesignNotes = OptionalString(root, "freshDesignNotes");
                    reply.BestVariant = OptionalString(root, "bestVariant")?.ToLowerInvariant();
                    if (protocolVersion >= 5)
                    {
                        ParseContinueFromObject(root, reply);
                        reply.BestSource = OptionalString(root, "bestSource")?.ToUpperInvariant();
                        if (reply.BestSource != null && !UiGoalLoopSources.IsValid(reply.BestSource))
                        {
                            throw new JsonException($"\"bestSource\" must be a source letter or null, got \"{reply.BestSource}\"");
                        }
                    }
                    else
                    {
                        reply.ContinueFrom = OptionalString(root, "continueFrom")?.ToLowerInvariant();
                        if (reply.ContinueFrom != null && !UiGoalLoopVariants.IsValid(reply.ContinueFrom))
                        {
                            throw new JsonException($"\"continueFrom\" must be \"refine\" or \"fresh\", got \"{reply.ContinueFrom}\"");
                        }
                    }
                    if (reply.BestVariant != null && !UiGoalLoopVariants.IsValid(reply.BestVariant))
                    {
                        throw new JsonException($"\"bestVariant\" must be \"refine\", \"fresh\", or null, got \"{reply.BestVariant}\"");
                    }
                    if (reply.Decision == "render" && string.IsNullOrWhiteSpace(reply.FreshPrompt))
                    {
                        throw new JsonException("decision is \"render\" but \"freshPrompt\" (the from-scratch prompt) is missing or blank");
                    }
                    if (reply.Decision == "render" && reply.Prompt != null && reply.FreshPrompt != null
                        && string.Equals(reply.Prompt.Trim(), reply.FreshPrompt.Trim(), StringComparison.Ordinal))
                    {
                        throw new JsonException("\"freshPrompt\" must differ from \"prompt\": the fresh render is a from-scratch re-attempt");
                    }
                    if (reply.Decision == "render" && expectEvaluation && reply.ContinueFrom == null)
                    {
                        throw new JsonException(protocolVersion >= 5
                            ? "after a review, decision \"render\" requires \"continueFrom\" ({ \"variant\", \"source\" })"
                            : "after a review, decision \"render\" requires \"continueFrom\" (\"refine\" or \"fresh\")");
                    }
                    if (!expectEvaluation && reply.ContinueFrom != null)
                    {
                        throw new JsonException("nothing has been rendered yet, so \"continueFrom\" must be null");
                    }
                    if (protocolVersion >= 5)
                    {
                        ParseEvaluationsArray(root, reply, expectEvaluation, shownRenders);
                        if (reply.ContinueFrom != null
                            && !shownRenders!.Any(k => k.Variant == reply.ContinueFrom && k.Source == reply.ContinueFromSource))
                        {
                            throw new JsonException($"\"continueFrom\" names the {reply.ContinueFrom} render of source {reply.ContinueFromSource}, which was not among the renders shown");
                        }
                    }
                    else
                    {
                        var hasFresh = root.TryGetProperty("freshEvaluation", out var freshEvaluation)
                            && freshEvaluation.ValueKind == JsonValueKind.Object;
                        if (expectEvaluation)
                        {
                            if (!hasFresh)
                            {
                                throw new JsonException("two renders were shown, so \"freshEvaluation\" must be an object");
                            }
                            reply.FreshEvaluation = ParseEvaluation(freshEvaluation);
                        }
                        else if (hasFresh)
                        {
                            throw new JsonException("nothing has been rendered yet, so \"freshEvaluation\" must be null");
                        }
                    }
                }
                if (protocolVersion >= 3)
                {
                    if (reply.GoalKind != null && !UiGoalLoopGoalKinds.IsManagerValue(reply.GoalKind))
                    {
                        throw new JsonException($"\"goalKind\" must be \"bounded\" or \"open-ended\", got \"{reply.GoalKind}\"");
                    }
                    if (!expectEvaluation && reply.GoalKind == null)
                    {
                        throw new JsonException("the first design must classify the GOAL in \"goalKind\" (\"bounded\" or \"open-ended\")");
                    }
                }
                else if (reply.GoalKind != null && !UiGoalLoopGoalKinds.IsManagerValue(reply.GoalKind))
                {
                    // Older contracts had no such field; an unexpected value
                    // is dropped rather than failing a loop that never asked.
                    reply.GoalKind = null;
                }
                if (reply.Decision == "render" && string.IsNullOrWhiteSpace(reply.Prompt))
                {
                    throw new JsonException("decision is \"render\" but \"prompt\" is missing or blank");
                }
                if (reply.Decision == "done" && string.IsNullOrWhiteSpace(reply.DoneStatement))
                {
                    throw new JsonException("decision is \"done\" but \"doneStatement\" is missing or blank");
                }
                if (root.TryGetProperty("bestTurn", out var bestTurn) && bestTurn.ValueKind == JsonValueKind.Number)
                {
                    if (!bestTurn.TryGetInt32(out var bt) || bt < 1)
                    {
                        throw new JsonException("\"bestTurn\" must be a positive integer or null");
                    }
                    reply.BestTurn = bt;
                }
                var hasEvaluation = root.TryGetProperty("evaluation", out var evaluation)
                    && evaluation.ValueKind == JsonValueKind.Object;
                if (protocolVersion >= 5)
                {
                    // The per-render array is the only evaluation carrier.
                    if (hasEvaluation || (root.TryGetProperty("freshEvaluation", out var fe) && fe.ValueKind == JsonValueKind.Object))
                    {
                        throw new JsonException("this loop scores renders in the \"evaluations\" array; \"evaluation\" and \"freshEvaluation\" must be absent or null");
                    }
                    if (!expectEvaluation && reply.Decision != "render")
                    {
                        throw new JsonException("the first reply must have decision \"render\"");
                    }
                    return reply;
                }
                if (expectEvaluation)
                {
                    if (!hasEvaluation)
                    {
                        throw new JsonException("a rendered image was shown, so \"evaluation\" must be an object");
                    }
                    reply.Evaluation = ParseEvaluation(evaluation);
                }
                else
                {
                    if (hasEvaluation)
                    {
                        throw new JsonException("nothing has been rendered yet, so \"evaluation\" must be null");
                    }
                    if (reply.Decision != "render")
                    {
                        throw new JsonException("the first reply must have decision \"render\"");
                    }
                }
                return reply;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"manager reply did not follow the required JSON contract ({ex.Message}); reply starts: {Truncate(raw, 300)}");
            }
        }

        // Protocol 5 "continueFrom": null or { "variant", "source" }.
        private static void ParseContinueFromObject(JsonElement root, UiGoalLoopManagerReply reply)
        {
            if (!root.TryGetProperty("continueFrom", out var cf) || cf.ValueKind == JsonValueKind.Null)
            {
                return;
            }
            if (cf.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("\"continueFrom\" must be null or an object { \"variant\": \"refine\"|\"fresh\", \"source\": letter }");
            }
            var variant = OptionalString(cf, "variant")?.ToLowerInvariant();
            var source = OptionalString(cf, "source")?.ToUpperInvariant();
            if (!UiGoalLoopVariants.IsValid(variant))
            {
                throw new JsonException($"\"continueFrom.variant\" must be \"refine\" or \"fresh\", got \"{variant}\"");
            }
            if (!UiGoalLoopSources.IsValid(source))
            {
                throw new JsonException($"\"continueFrom.source\" must be a source letter, got \"{source}\"");
            }
            reply.ContinueFrom = variant;
            reply.ContinueFromSource = source;
        }

        // Protocol 5 "evaluations": null before any render; afterwards
        // exactly one entry per render shown, each naming its variant and
        // source, with no duplicates and no entries for renders not shown.
        private static void ParseEvaluationsArray(
            JsonElement root, UiGoalLoopManagerReply reply, bool expectEvaluation, IReadOnlyList<UiGoalLoopRenderKey>? shownRenders)
        {
            var has = root.TryGetProperty("evaluations", out var arr) && arr.ValueKind != JsonValueKind.Null;
            if (!expectEvaluation)
            {
                if (has && !(arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 0))
                {
                    throw new JsonException("nothing has been rendered yet, so \"evaluations\" must be null");
                }
                return;
            }
            if (!has || arr.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"{shownRenders!.Count} render(s) were shown, so \"evaluations\" must be an array with one entry per render");
            }
            var list = new List<UiGoalLoopScoredRender>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("every \"evaluations\" entry must be an object");
                }
                var variant = OptionalString(item, "variant")?.ToLowerInvariant();
                var source = OptionalString(item, "source")?.ToUpperInvariant();
                if (!UiGoalLoopVariants.IsValid(variant))
                {
                    throw new JsonException($"\"evaluations[].variant\" must be \"refine\" or \"fresh\", got \"{variant}\"");
                }
                if (!UiGoalLoopSources.IsValid(source))
                {
                    throw new JsonException($"\"evaluations[].source\" must be a source letter, got \"{source}\"");
                }
                if (!shownRenders!.Any(k => k.Variant == variant && k.Source == source))
                {
                    throw new JsonException($"\"evaluations\" scores the {variant} render of source {source}, which was not among the renders shown");
                }
                if (list.Any(l => l.Variant == variant && l.Source == source))
                {
                    throw new JsonException($"\"evaluations\" scores the {variant} render of source {source} twice");
                }
                list.Add(new UiGoalLoopScoredRender(variant, source, ParseEvaluation(item)));
            }
            var missing = shownRenders!.Where(k => !list.Any(l => l.Variant == k.Variant && l.Source == k.Source)).ToList();
            if (missing.Count > 0)
            {
                throw new JsonException("\"evaluations\" is missing the " + string.Join(", ",
                    missing.Select(m => $"{m.Variant} render of source {m.Source}")));
            }
            // Fixed order: refine then fresh, sources alphabetical.
            reply.RenderEvaluations = list
                .OrderBy(l => Array.IndexOf(UiGoalLoopVariants.All, l.Variant))
                .ThenBy(l => l.Source, StringComparer.Ordinal)
                .ToList();
        }

        private static UiGoalLoopEvaluation ParseEvaluation(JsonElement e)
        {
            if (!e.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number)
            {
                throw new JsonException("\"evaluation.score\" must be a number");
            }
            var value = score.GetDouble();
            if (double.IsNaN(value) || value < 0 || value > 10)
            {
                throw new JsonException($"\"evaluation.score\" must be within 0-10, got {value}");
            }
            if (!e.TryGetProperty("goalMet", out var goalMet)
                || (goalMet.ValueKind != JsonValueKind.True && goalMet.ValueKind != JsonValueKind.False))
            {
                throw new JsonException("\"evaluation.goalMet\" must be a boolean");
            }
            return new UiGoalLoopEvaluation
            {
                Score = value,
                GoalMet = goalMet.ValueKind == JsonValueKind.True,
                Assessment = RequireString(e, "assessment"),
                Problems = OptionalStrings(e, "problems"),
                Keep = OptionalStrings(e, "keep"),
            };
        }

        private static string RequireString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(v.GetString()))
            {
                throw new JsonException($"the reply lacks a non-empty string \"{name}\" field");
            }
            return v.GetString()!.Trim();
        }

        private static string? OptionalString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (v.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"\"{name}\" must be a string or null");
            }
            return v.GetString()!.Trim();
        }

        private static List<string> OptionalStrings(JsonElement parent, string name)
        {
            var list = new List<string>();
            if (!parent.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return list;
            }
            if (v.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"\"{name}\" must be an array of strings or null");
            }
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException($"\"{name}\" must contain only strings");
                }
                var text = item.GetString()!.Trim();
                if (text.Length > 0)
                {
                    list.Add(text);
                }
            }
            return list;
        }

        internal static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    public enum UiGoalLoopStepKind
    {
        AskManagerDesign,
        Render,
        AskManagerReview,
        // The manager said "done" on an open-ended goal without a
        // demonstrated limit: write the objection, then re-ask.
        Objection,
        Done,
        Exhausted,
        Invalid,
    }

    // One render the runner must perform for a Render step. ReplaceIndex is
    // the index of an existing unrendered render-request entry to fill in
    // place (fork with an edited prompt, interrupted render); null appends.
    // GeneratorKey/Source are set from protocol 5 (one planned render per
    // variant per generator); null means the loop's single generator.
    public sealed record UiGoalLoopPlannedRender(
        string? Variant, string Prompt, int? ReplaceIndex, string? GeneratorKey = null, string? Source = null);

    // Prompt is the first planned render's prompt (the single render before
    // protocol 4, the refine render from protocol 4); Renders lists every
    // render of the step in variant order.
    public sealed record UiGoalLoopStep(UiGoalLoopStepKind Kind, int Turn, string? Prompt, string? Reason)
    {
        public IReadOnlyList<UiGoalLoopPlannedRender> Renders { get; init; } = Array.Empty<UiGoalLoopPlannedRender>();
    }

    /// Pure planning + fork logic over the entry list, kept free of I/O so it
    /// is unit-testable and so resume/fork share one definition of "what
    /// happens next" with the live runner.
    public static class UiGoalLoopPlanner
    {
        public const int DefaultMaxTurns = 6;
        public const int MaxTurnsCap = 30;

        public static int ValidateMaxTurns(int value)
        {
            if (value < 1 || value > MaxTurnsCap)
            {
                throw new InvalidDataException($"max turns must be between 1 and {MaxTurnsCap}; got {value}");
            }
            return value;
        }

        /// The last entry that participates in control flow: notes are
        /// annotations, and a manager reply that failed the contract is kept
        /// visible but does not advance the loop (the request before it is
        /// re-asked on resume).
        public static UiGoalLoopEntry? EffectiveTail(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Kind == UiGoalLoopKinds.Note)
                {
                    continue;
                }
                if (UiGoalLoopKinds.IsManagerReply(e.Kind) && (e.Manager?.Parsed == null || e.Error != null))
                {
                    continue;
                }
                return e;
            }
            return null;
        }

        /// effectiveGoalKind is the loop's enforced kind (see
        /// ResolveEffectiveGoalKind); null means no stopping rule applies.
        /// protocolVersion decides how many renders a turn has: from
        /// protocol 4 a manager reply plans a refine and a fresh render, and
        /// the review is asked only once every requested render of the turn
        /// has a result.
        public static UiGoalLoopStep DetermineNextStep(
            IReadOnlyList<UiGoalLoopEntry> entries, int maxTurns, string? effectiveGoalKind = null,
            int protocolVersion = UiGoalLoopProtocol.Version, IReadOnlyList<UiGoalLoopGenerator>? generators = null)
        {
            if (protocolVersion >= 5 && (generators == null || generators.Count == 0))
            {
                throw new ArgumentException("protocol 5 planning needs the loop's generator list", nameof(generators));
            }
            var tail = EffectiveTail(entries);
            if (tail == null)
            {
                return new UiGoalLoopStep(UiGoalLoopStepKind.Invalid, 0, null, "the loop has no goal entry");
            }
            switch (tail.Kind)
            {
                case UiGoalLoopKinds.Goal:
                    return new UiGoalLoopStep(UiGoalLoopStepKind.AskManagerDesign, 1, null, null);
                case UiGoalLoopKinds.Design:
                {
                    var parsed = tail.Manager!.Parsed!;
                    if (parsed.Decision == "done")
                    {
                        return new UiGoalLoopStep(UiGoalLoopStepKind.Done, 0, null, parsed.DoneStatement);
                    }
                    return RenderOrExhausted(1, PlannedRenders(parsed, protocolVersion, generators), maxTurns, protocolVersion);
                }
                case UiGoalLoopKinds.RenderRequest:
                case UiGoalLoopKinds.RenderResult:
                {
                    // Every render-request of this turn without a result of
                    // the same variant is still owed (crash mid-turn, or a
                    // fork that edited one prompt): render those in place.
                    var pending = PendingRenders(entries, tail.Turn);
                    if (pending.Count > 0)
                    {
                        return new UiGoalLoopStep(UiGoalLoopStepKind.Render, tail.Turn, pending[0].Prompt, null) { Renders = pending };
                    }
                    return new UiGoalLoopStep(UiGoalLoopStepKind.AskManagerReview, tail.Turn, null, null);
                }
                case UiGoalLoopKinds.ReviewRequest:
                case UiGoalLoopKinds.Objection:
                    return new UiGoalLoopStep(UiGoalLoopStepKind.AskManagerReview, tail.Turn, null, null);
                case UiGoalLoopKinds.Review:
                {
                    var parsed = tail.Manager!.Parsed!;
                    if (parsed.Decision == "done")
                    {
                        if (!IsDonePermitted(entries, effectiveGoalKind))
                        {
                            var best = BestReview(entries)!.Value;
                            return new UiGoalLoopStep(UiGoalLoopStepKind.Objection, tail.Turn, null,
                                $"\"done\" on an open-ended goal without a demonstrated limit: best is turn {best.Turn}{(best.Variant != null ? $" {best.Variant} render" : "")}{(best.Source != null ? $" of source {best.Source}" : "")} (score {best.Score:0.#}) and no later {(protocolVersion >= 4 ? "refine " : "")}render{(best.Source != null ? " on that source" : "")} scored below it");
                        }
                        return new UiGoalLoopStep(UiGoalLoopStepKind.Done, tail.Turn, null, parsed.DoneStatement);
                    }
                    return RenderOrExhausted(tail.Turn + 1, PlannedRenders(parsed, protocolVersion, generators), maxTurns, protocolVersion);
                }
                default:
                    return new UiGoalLoopStep(UiGoalLoopStepKind.Invalid, tail.Turn, null, $"unexpected tail entry kind '{tail.Kind}'");
            }
        }

        // The renders a manager reply asks for: one unlabeled render before
        // protocol 4; refine + fresh from protocol 4 (a reply that carries
        // only one prompt plans only that one — the parser is what enforces
        // the pair on live replies).
        // Protocol 5 plans every variant on every generator: refine on A, B,
        // …, then fresh on A, B, ….
        private static List<UiGoalLoopPlannedRender> PlannedRenders(
            UiGoalLoopManagerReply parsed, int protocolVersion, IReadOnlyList<UiGoalLoopGenerator>? generators)
        {
            var list = new List<UiGoalLoopPlannedRender>(2);
            if (protocolVersion < 4)
            {
                list.Add(new UiGoalLoopPlannedRender(null, parsed.Prompt!, null));
                return list;
            }
            var prompts = new List<(string Variant, string Prompt)>(2);
            if (!string.IsNullOrWhiteSpace(parsed.Prompt))
            {
                prompts.Add((UiGoalLoopVariants.Refine, parsed.Prompt));
            }
            if (!string.IsNullOrWhiteSpace(parsed.FreshPrompt))
            {
                prompts.Add((UiGoalLoopVariants.Fresh, parsed.FreshPrompt));
            }
            foreach (var (variant, prompt) in prompts)
            {
                if (protocolVersion >= 5)
                {
                    foreach (var g in generators!)
                    {
                        list.Add(new UiGoalLoopPlannedRender(variant, prompt, null, g.Key, g.Source));
                    }
                }
                else
                {
                    list.Add(new UiGoalLoopPlannedRender(variant, prompt, null));
                }
            }
            return list;
        }

        // Render-requests of the turn that have no render-result of the same
        // variant and generator after them, in variant then source order.
        public static List<UiGoalLoopPlannedRender> PendingRenders(IReadOnlyList<UiGoalLoopEntry> entries, int turn)
        {
            var pending = new List<UiGoalLoopPlannedRender>();
            foreach (var request in entries.Where(e => e.Kind == UiGoalLoopKinds.RenderRequest && e.Turn == turn))
            {
                var variant = request.Render?.Variant;
                var generator = request.Render?.GeneratorKey;
                var rendered = entries.Any(e => e.Kind == UiGoalLoopKinds.RenderResult && e.Turn == turn
                    && e.Index > request.Index && e.Render?.Variant == variant
                    && string.Equals(e.Render?.GeneratorKey, generator, StringComparison.Ordinal));
                if (!rendered)
                {
                    pending.Add(new UiGoalLoopPlannedRender(
                        variant, request.Text, request.Index, request.Render?.Source == null ? null : generator, request.Render?.Source));
                }
            }
            return pending
                .OrderBy(p => p.Variant == null ? 0 : Array.IndexOf(UiGoalLoopVariants.All, p.Variant))
                .ThenBy(p => p.Source ?? "", StringComparer.Ordinal)
                .ToList();
        }

        private static UiGoalLoopStep RenderOrExhausted(int turn, List<UiGoalLoopPlannedRender> renders, int maxTurns, int protocolVersion)
        {
            var prompt = renders.Count > 0 ? renders[0].Prompt : null;
            if (turn > maxTurns)
            {
                return new UiGoalLoopStep(UiGoalLoopStepKind.Exhausted, turn, prompt,
                    $"the manager wants to render turn {turn} but the loop allows {maxTurns} {(protocolVersion >= 4 ? "turn" : "render")}(s)")
                { Renders = renders };
            }
            return new UiGoalLoopStep(UiGoalLoopStepKind.Render, turn, prompt, null) { Renders = renders };
        }

        /// The kind enforced on this loop: the operator's explicit choice,
        /// else the manager's first-design classification, else null (not
        /// yet classified). Pre-protocol-3 loops return null: their contract
        /// defined no goal kinds, so objecting to them would be incoherent.
        public static string? ResolveEffectiveGoalKind(UiGoalLoop loop, IReadOnlyList<UiGoalLoopEntry> entries, out string? source)
        {
            source = null;
            if (loop.ProtocolVersion < 3)
            {
                return null;
            }
            if (UiGoalLoopGoalKinds.IsManagerValue(loop.GoalKind))
            {
                source = "operator";
                return loop.GoalKind;
            }
            foreach (var e in entries)
            {
                if (e.Kind == UiGoalLoopKinds.Design && e.Manager?.Parsed?.GoalKind != null && e.Error == null)
                {
                    source = "manager";
                    return e.Manager.Parsed.GoalKind;
                }
            }
            return null;
        }

        /// Best review so far: the first turn holding the maximum score. Every
        /// parsed review counts, including one whose "done" was objected to
        /// (its evaluation of the image stands).
        /// Every render of a protocol-4 turn counts; Variant names which one
        /// holds the maximum (null on single-render loops) and Source which
        /// generator rendered it (null before protocol 5).
        public static (int Turn, string? Variant, string? Source, double Score)? BestReview(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            (int Turn, string? Variant, string? Source, double Score)? best = null;
            foreach (var e in entries)
            {
                var parsed = e.Kind == UiGoalLoopKinds.Review ? e.Manager?.Parsed : null;
                if (parsed == null)
                {
                    continue;
                }
                foreach (var scored in parsed.Evaluations())
                {
                    if (best == null || scored.Evaluation.Score > best.Value.Score)
                    {
                        best = (e.Turn, scored.Variant, scored.Source, scored.Evaluation.Score);
                    }
                }
            }
            return best;
        }

        /// The open-ended stopping rule, enforced mechanically: "done" is
        /// permitted only after some later render scored strictly below the
        /// best one (the generator's limit has been demonstrated). Bounded
        /// and unclassified goals leave the decision to the manager. Under
        /// protocol 4 only the REFINE render's score counts as the push: a
        /// fresh render is exploration and a low score there says nothing
        /// about the limit. Under protocol 5 the refine must be on the SAME
        /// source as the best render: a weaker source scoring low says
        /// nothing about the best source's limit.
        public static bool IsDonePermitted(IReadOnlyList<UiGoalLoopEntry> entries, string? effectiveGoalKind)
        {
            if (effectiveGoalKind != UiGoalLoopGoalKinds.OpenEnded)
            {
                return true;
            }
            var best = BestReview(entries);
            if (best == null)
            {
                return true;
            }
            foreach (var e in entries)
            {
                if (e.Kind != UiGoalLoopKinds.Review || e.Manager?.Parsed == null || e.Turn <= best.Value.Turn)
                {
                    continue;
                }
                foreach (var scored in e.Manager.Parsed.Evaluations())
                {
                    if (scored.Variant == UiGoalLoopVariants.Fresh)
                    {
                        continue;
                    }
                    if (scored.Source == best.Value.Source && scored.Evaluation.Score < best.Value.Score)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// The latest refine score on the given source (any source when
        /// null), for the objection text; null when no such review exists.
        public static double? LatestRefineScore(IReadOnlyList<UiGoalLoopEntry> entries, string? source)
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Kind != UiGoalLoopKinds.Review || e.Manager?.Parsed == null)
                {
                    continue;
                }
                var scored = e.Manager.Parsed.Evaluations()
                    .FirstOrDefault(s => s.Variant != UiGoalLoopVariants.Fresh && s.Source == source);
                if (scored != null)
                {
                    return scored.Evaluation.Score;
                }
            }
            return null;
        }

        /// Entries [0..entryIndex] copied for a fork; when newText is given the
        /// fork-point entry's text is replaced and everything derived from the
        /// old text (parsed reply, job link, wire copies) is dropped so the
        /// runner re-derives it. Later entries are not copied.
        public static List<UiGoalLoopEntry> BuildForkEntries(
            IReadOnlyList<UiGoalLoopEntry> parent,
            int entryIndex,
            string? newText,
            out string? newGoal,
            int protocolVersion = UiGoalLoopProtocol.Version)
        {
            newGoal = null;
            if (entryIndex < 0 || entryIndex >= parent.Count)
            {
                throw new InvalidDataException($"entry index {entryIndex} is out of range (0..{parent.Count - 1})");
            }
            var point = parent[entryIndex];
            if (point.Kind == UiGoalLoopKinds.Note)
            {
                throw new InvalidDataException("a note is not a fork point; pick a message entry");
            }
            if (newText != null && !UiGoalLoopKinds.IsTextEditable(point.Kind))
            {
                throw new InvalidDataException($"a '{point.Kind}' entry has no editable text");
            }
            var copies = new List<UiGoalLoopEntry>(entryIndex + 2);
            for (var i = 0; i <= entryIndex; i++)
            {
                var copy = parent[i].Clone();
                copy.Index = i;
                copies.Add(copy);
            }
            var edited = copies[entryIndex];
            if (newText != null)
            {
                var trimmed = newText.Trim();
                if (trimmed.Length == 0)
                {
                    throw new InvalidDataException("the edited text is blank");
                }
                edited.OriginalText = edited.Text;
                edited.Text = trimmed;
                edited.Edited = true;
                edited.Error = null;
                edited.WireRequest = null;
                edited.WireResponse = null;
                switch (edited.Kind)
                {
                    case UiGoalLoopKinds.Goal:
                        newGoal = trimmed;
                        break;
                    case UiGoalLoopKinds.Design:
                    case UiGoalLoopKinds.Review:
                    {
                        var data = edited.Manager ?? new UiGoalLoopManagerData();
                        data.ProviderReasoning = null;
                        data.InputTokens = null;
                        data.OutputTokens = null;
                        data.CostUsd = null;
                        data.ProviderStop = null;
                        data.RequestBytes = null;
                        data.Model = data.Model.Length == 0 ? "operator edit" : data.Model + " (edited by operator)";
                        try
                        {
                            data.Parsed = UiGoalLoopProtocol.ParseManagerReply(
                                trimmed, expectEvaluation: edited.Kind == UiGoalLoopKinds.Review, protocolVersion: protocolVersion);
                            data.ParseError = null;
                        }
                        catch (InvalidDataException ex)
                        {
                            throw new InvalidDataException(
                                $"the edited manager reply must itself follow the JSON contract: {ex.Message}");
                        }
                        edited.Manager = data;
                        break;
                    }
                    case UiGoalLoopKinds.RenderRequest:
                    {
                        // The old job rendered the old text; this prompt has
                        // not been rendered. Keep the option identity, drop
                        // the job link so the runner renders it.
                        if (edited.Render != null)
                        {
                            edited.Render.JobId = null;
                            edited.Render.Ok = null;
                            edited.Render.ImageUrl = null;
                            edited.Render.ThumbUrl = null;
                            edited.Render.Size = null;
                            edited.Render.Cost = null;
                            edited.Render.Label = null;
                        }
                        break;
                    }
                    case UiGoalLoopKinds.ReviewRequest:
                        // Images stay: the same rendered image is re-sent
                        // with the operator's wording.
                        break;
                }
            }
            return copies;
        }

        public static int CountRenders(IReadOnlyList<UiGoalLoopEntry> entries)
            => entries.Count(e => e.Kind == UiGoalLoopKinds.RenderResult);

        // Turns that produced at least one render result (a protocol-4 turn
        // has two results; the budget counts turns).
        public static int CountRenderedTurns(IReadOnlyList<UiGoalLoopEntry> entries)
            => entries.Where(e => e.Kind == UiGoalLoopKinds.RenderResult).Select(e => e.Turn).Distinct().Count();
    }

    internal sealed class UiGoalLoopStorage
    {
        private readonly string _dir;
        private readonly object _fileLock = new();

        public const string MetadataFile = "loop.json";
        public const string EntriesFile = "entries.jsonl";

        public UiGoalLoopStorage(string root, string id)
        {
            _dir = Path.Combine(root, id);
        }

        public string Directory => _dir;

        public void Initialize()
        {
            System.IO.Directory.CreateDirectory(_dir);
        }

        private static readonly JsonSerializerOptions IndentedOptions =
            new(UiGoalLoopJson.Options) { WriteIndented = true };

        public void SaveMetadata(UiGoalLoop loop)
        {
            var path = Path.Combine(_dir, MetadataFile);
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(loop, IndentedOptions);
            lock (_fileLock)
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
        }

        public static UiGoalLoop? TryLoadMetadata(string dir)
        {
            var path = Path.Combine(dir, MetadataFile);
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<UiGoalLoop>(File.ReadAllText(path), UiGoalLoopJson.Options);
        }

        public void AppendEntry(UiGoalLoopEntry entry)
        {
            var line = JsonSerializer.Serialize(entry, UiGoalLoopJson.Options);
            lock (_fileLock)
            {
                File.AppendAllText(Path.Combine(_dir, EntriesFile), line + "\n");
            }
        }

        public void WriteAllEntries(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                sb.Append(JsonSerializer.Serialize(entry, UiGoalLoopJson.Options)).Append('\n');
            }
            var path = Path.Combine(_dir, EntriesFile);
            var tmp = path + ".tmp";
            lock (_fileLock)
            {
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, path, overwrite: true);
            }
        }

        public List<UiGoalLoopEntry> ReadEntries()
        {
            var path = Path.Combine(_dir, EntriesFile);
            var list = new List<UiGoalLoopEntry>();
            if (!File.Exists(path))
            {
                return list;
            }
            string[] lines;
            lock (_fileLock)
            {
                lines = File.ReadAllLines(path);
            }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var entry = JsonSerializer.Deserialize<UiGoalLoopEntry>(line, UiGoalLoopJson.Options);
                if (entry == null)
                {
                    throw new InvalidDataException($"{path} contains an unreadable entry line");
                }
                list.Add(entry);
            }
            return list;
        }
    }

    public sealed record UiGoalLoopImageInfo(string Mime, int Width, int Height, long RawBytes);

    /// Decides and applies how a rendered image reaches the manager. Owner
    /// requirement (2026-09-04): the manager sees the renderer's full
    /// resolution; nothing here prefers a smaller image. The ONLY changes are
    /// those a provider's published hard limit forces, applied in this order
    /// and each recorded in the image's label:
    ///   1. MIME not accepted by the provider → lossless PNG re-encode.
    ///   2. Pixel-dimension cap (or 32x32-patch cap) → downscale to the cap.
    ///      This is the only step that reduces resolution; today it can fire
    ///      only for Anthropic above 8000 px (2000 px past 20 images) and for
    ///      OpenAI above 30,000 patches (~30 MP), both beyond any current UI
    ///      render size.
    ///   3. Per-image byte cap → JPEG at full resolution (q92, then q85);
    ///      only if still over, progressive downscale until it fits.
    ///   4. Request-total cap under pressure → JPEG at full resolution for
    ///      every non-JPEG image in that call. If the finished body is still
    ///      over the cap the call fails closed (ManagerChatHttp.SendAsync).
    public static class UiGoalLoopImageTransport
    {
        public sealed record Plan(string Mode, int Width, int Height, string Reason)
        {
            public bool ChangesResolution => Mode == "downscale";
            public string Describe(UiGoalLoopImageInfo info)
                => Mode switch
                {
                    "verbatim" => $"verbatim {info.Mime} {info.Width}x{info.Height} ({info.RawBytes:N0} bytes)",
                    "png" => $"re-encoded losslessly as PNG at full {info.Width}x{info.Height} — {Reason}",
                    "jpeg" => $"re-encoded as JPEG at full {info.Width}x{info.Height} — {Reason}",
                    "downscale" => $"downscaled to {Width}x{Height} from {info.Width}x{info.Height} — {Reason}",
                    _ => Mode,
                };
        }

        public const int JpegQualityFirst = 92;
        public const int JpegQualitySecond = 85;

        // Whether the images in a call, sent verbatim, would exceed the
        // provider's request cap (text is small next to images; a 1 MiB
        // allowance covers it).
        public static bool RequestBudgetPressure(ManagerImageLimits limits, IEnumerable<long> rawByteSizes)
        {
            if (limits.MaxRequestBytes == null)
            {
                return false;
            }
            long total = 1L << 20;
            foreach (var raw in rawByteSizes)
            {
                total += ManagerImageLimits.Base64Length(raw);
            }
            return total > limits.MaxRequestBytes.Value;
        }

        public static Plan Make(ManagerImageLimits limits, UiGoalLoopImageInfo info, int imagesInRequest, bool budgetPressure)
        {
            var reasons = new List<string>();
            var mode = "verbatim";
            var width = info.Width;
            var height = info.Height;

            if (!limits.AcceptedMimes.Contains(info.Mime, StringComparer.Ordinal))
            {
                mode = "png";
                reasons.Add($"provider does not accept {info.Mime}");
            }

            var edgeCap = limits.EffectiveMaxEdgePx(imagesInRequest);
            if (edgeCap != null && Math.Max(width, height) > edgeCap.Value)
            {
                var scale = (double)edgeCap.Value / Math.Max(width, height);
                width = Math.Max(1, (int)Math.Floor(width * scale));
                height = Math.Max(1, (int)Math.Floor(height * scale));
                mode = "downscale";
                var many = limits.ManyImagesThreshold != null && imagesInRequest > limits.ManyImagesThreshold.Value
                    && limits.MaxEdgePxWhenMany == edgeCap;
                reasons.Add($"provider pixel cap {edgeCap.Value} px per side"
                    + (many ? $" for requests with more than {limits.ManyImagesThreshold} images (this request has {imagesInRequest})" : ""));
            }
            if (limits.MaxPatches32 != null && ManagerImageLimits.Patches32(width, height) > limits.MaxPatches32.Value)
            {
                var scale = Math.Sqrt((32.0 * 32.0 * limits.MaxPatches32.Value) / ((double)width * height));
                while (ManagerImageLimits.Patches32((int)Math.Floor(width * scale), (int)Math.Floor(height * scale)) > limits.MaxPatches32.Value)
                {
                    scale *= 0.99;
                }
                width = Math.Max(1, (int)Math.Floor(width * scale));
                height = Math.Max(1, (int)Math.Floor(height * scale));
                mode = "downscale";
                reasons.Add($"provider patch cap {limits.MaxPatches32.Value:N0} 32x32 patches per image");
            }

            if (mode == "verbatim" && info.Mime != "image/jpeg")
            {
                if (limits.MaxImageBase64Bytes != null && ManagerImageLimits.Base64Length(info.RawBytes) > limits.MaxImageBase64Bytes.Value)
                {
                    mode = "jpeg";
                    reasons.Add($"provider per-image cap {limits.MaxImageBase64Bytes.Value:N0} base64 bytes ({ManagerImageLimits.Base64Length(info.RawBytes):N0} as {info.Mime})");
                }
                else if (limits.MaxImageRawBytes != null && info.RawBytes > limits.MaxImageRawBytes.Value)
                {
                    mode = "jpeg";
                    reasons.Add($"provider per-image cap {limits.MaxImageRawBytes.Value:N0} bytes ({info.RawBytes:N0} as {info.Mime})");
                }
                else if (budgetPressure)
                {
                    mode = "jpeg";
                    reasons.Add($"this call's images would exceed the provider's {limits.MaxRequestBytes:N0}-byte request cap as sent");
                }
            }
            return new Plan(mode, width, height, string.Join("; ", reasons));
        }

        public static ManagerChatImagePayload Apply(Plan plan, ManagerImageLimits limits, UiGoalLoopImageInfo info, byte[] raw, string identity)
        {
            var description = plan.Describe(info);
            if (plan.Mode == "verbatim")
            {
                return new ManagerChatImagePayload { Bytes = raw, Mime = info.Mime, Label = $"{identity}: {description}" };
            }

            using var image = Image.Load<Rgba32>(raw);
            if (plan.Mode == "png")
            {
                using var ms = new MemoryStream();
                image.SaveAsPng(ms);
                return new ManagerChatImagePayload { Bytes = ms.ToArray(), Mime = "image/png", Label = $"{identity}: {description}" };
            }

            if (plan.Mode == "downscale" && (image.Width != plan.Width || image.Height != plan.Height))
            {
                image.Mutate(x => x.Resize(plan.Width, plan.Height));
            }
            // JPEG has no alpha; composite onto white so transparent regions
            // do not turn black.
            image.Mutate(x => x.BackgroundColor(Color.White));

            var extra = new List<string>();
            foreach (var quality in new[] { JpegQualityFirst, JpegQualitySecond })
            {
                var encoded = EncodeJpeg(image, quality);
                if (FitsByteCaps(limits, encoded.Length))
                {
                    if (quality != JpegQualityFirst)
                    {
                        extra.Add($"JPEG q{quality}");
                    }
                    return new ManagerChatImagePayload
                    {
                        Bytes = encoded,
                        Mime = "image/jpeg",
                        Label = $"{identity}: {description}" + (extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : ""),
                    };
                }
            }
            // Full-resolution JPEG still over the per-image byte cap:
            // progressive downscale until it fits (recorded).
            var w = image.Width;
            var h = image.Height;
            for (var step = 0; step < 12; step++)
            {
                w = Math.Max(1, (int)Math.Floor(w * 0.85));
                h = Math.Max(1, (int)Math.Floor(h * 0.85));
                image.Mutate(x => x.Resize(w, h));
                var encoded = EncodeJpeg(image, JpegQualitySecond);
                if (FitsByteCaps(limits, encoded.Length))
                {
                    return new ManagerChatImagePayload
                    {
                        Bytes = encoded,
                        Mime = "image/jpeg",
                        Label = $"{identity}: downscaled to {w}x{h} from {info.Width}x{info.Height} — JPEG q{JpegQualitySecond} at full resolution still exceeded the provider per-image byte cap; {plan.Reason}",
                    };
                }
            }
            throw new InvalidOperationException(
                $"{identity}: cannot fit the image within the provider's per-image byte cap even at {w}x{h}; the loop stops rather than drop the image");
        }

        private static bool FitsByteCaps(ManagerImageLimits limits, long rawBytes)
        {
            if (limits.MaxImageRawBytes != null && rawBytes > limits.MaxImageRawBytes.Value)
            {
                return false;
            }
            if (limits.MaxImageBase64Bytes != null && ManagerImageLimits.Base64Length(rawBytes) > limits.MaxImageBase64Bytes.Value)
            {
                return false;
            }
            return true;
        }

        private static byte[] EncodeJpeg(Image<Rgba32> image, int quality)
        {
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
            return ms.ToArray();
        }
    }

    internal sealed class UiGoalLoopState
    {
        public required UiGoalLoop Loop { get; init; }
        public required List<UiGoalLoopEntry> Entries { get; init; }
        public required UiGoalLoopStorage Storage { get; init; }
        public readonly object Lock = new();
        public CancellationTokenSource? Cts;
        public Task? RunTask;
        public long LastAccessTicks = Environment.TickCount64;

        public bool IsRunning
        {
            get { lock (Lock) return RunTask != null && !RunTask.IsCompleted; }
        }
    }

    /// Disk-backed registry. loop.json for every loop is indexed at startup
    /// (small); entries hydrate on demand and idle loops beyond a cap are
    /// evicted from RAM, running loops always stay resident.
    internal sealed class UiGoalLoopRegistry
    {
        private const int MaxHydratedIdleLoops = 48;

        private readonly string _root;
        private readonly ConcurrentDictionary<string, UiGoalLoop> _index = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, UiGoalLoopState> _hydrated = new(StringComparer.Ordinal);

        public UiGoalLoopRegistry(Settings settings)
        {
            _root = Path.Combine(settings.ImageDownloadBaseFolder, "UiGoalLoops");
            Directory.CreateDirectory(_root);
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                UiGoalLoop? loop;
                try
                {
                    loop = UiGoalLoopStorage.TryLoadMetadata(dir);
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI goal loops: skipping unreadable {dir}: {ex.Message}");
                    continue;
                }
                if (loop == null)
                {
                    continue;
                }
                if (loop.Status == UiGoalLoopStatus.Running)
                {
                    // The process that was running it is gone. Nothing
                    // guesses where it got to; the operator resumes.
                    loop.Status = UiGoalLoopStatus.Stopped;
                    loop.StatusDetail = "the server restarted while this loop was running; resume to continue from the last recorded entry";
                    loop.Activity = "";
                    loop.ActivitySinceUnixMs = null;
                    try
                    {
                        new UiGoalLoopStorage(_root, loop.Id).SaveMetadata(loop);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UI goal loops: could not persist restart stop for {loop.Id}: {ex.Message}");
                    }
                }
                _index[loop.Id] = loop;
            }
            Logger.Log($"UI goal loops: indexed {_index.Count} loop(s) under {_root}.");
        }

        public UiGoalLoopState Create(UiGoalLoop loop, List<UiGoalLoopEntry> entries)
        {
            var storage = new UiGoalLoopStorage(_root, loop.Id);
            storage.Initialize();
            loop.EntryCount = entries.Count;
            storage.WriteAllEntries(entries);
            storage.SaveMetadata(loop);
            var state = new UiGoalLoopState { Loop = loop, Entries = entries, Storage = storage };
            _index[loop.Id] = loop;
            _hydrated[loop.Id] = state;
            EvictIdleIfNeeded();
            return state;
        }

        public UiGoalLoopState? Get(string id)
        {
            if (_hydrated.TryGetValue(id, out var state))
            {
                state.LastAccessTicks = Environment.TickCount64;
                return state;
            }
            if (!_index.TryGetValue(id, out var loop))
            {
                return null;
            }
            var storage = new UiGoalLoopStorage(_root, id);
            var entries = storage.ReadEntries();
            var created = new UiGoalLoopState { Loop = loop, Entries = entries, Storage = storage };
            var actual = _hydrated.GetOrAdd(id, created);
            actual.LastAccessTicks = Environment.TickCount64;
            EvictIdleIfNeeded();
            return actual;
        }

        public List<UiGoalLoop> ListSummaries()
            => _index.Values.OrderByDescending(l => l.CreatedAtUnixMs).ToList();

        private void EvictIdleIfNeeded()
        {
            var idle = _hydrated.Values.Where(s => !s.IsRunning).ToList();
            if (idle.Count <= MaxHydratedIdleLoops)
            {
                return;
            }
            foreach (var victim in idle.OrderBy(s => s.LastAccessTicks).Take(idle.Count - MaxHydratedIdleLoops))
            {
                _hydrated.TryRemove(victim.Loop.Id, out _);
            }
        }
    }

    internal sealed class UiGoalLoopRunner
    {
        public const int DefaultMaxTurns = UiGoalLoopPlanner.DefaultMaxTurns;
        public const int MaxTurnsCap = UiGoalLoopPlanner.MaxTurnsCap;
        public const int MaxGoalChars = 20000;
        public const int MaxEditedTextChars = 60000;
        private const int MaxRunningLoops = 6;

        private readonly Settings _settings;
        private readonly UiJobRegistry _jobs;
        private readonly UiJobRunner _jobRunner;
        private readonly UiGoalLoopRegistry _loops;
        private readonly Action<UiJob>? _onRenderJobCreated;
        private readonly SemaphoreSlim _managerCalls = new(3);
        private readonly object _runningLock = new();
        private int _running;

        public UiGoalLoopRunner(
            Settings settings,
            UiJobRegistry jobs,
            UiJobRunner jobRunner,
            UiGoalLoopRegistry loops,
            Action<UiJob>? onRenderJobCreated = null)
        {
            _settings = settings;
            _jobs = jobs;
            _jobRunner = jobRunner;
            _loops = loops;
            _onRenderJobCreated = onRenderJobCreated;
        }

        public UiGoalLoopRegistry Registry => _loops;

        public int RunningCount
        {
            get { lock (_runningLock) return _running; }
        }

        public void EnsureCapacity()
        {
            lock (_runningLock)
            {
                if (_running >= MaxRunningLoops)
                {
                    throw new InvalidOperationException(
                        $"{MaxRunningLoops} goal loops are already running on this server; try again after one finishes");
                }
            }
        }

        public static string GeneratorProblem(UiJobRunner runner, string key)
        {
            if (UiJobRunner.IsAnalysisKey(key))
            {
                return $"{key} is an analysis target, not an image generator";
            }
            if (string.Equals(key, UiJobRunner.KeyGrokWebVideo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, UiJobRunner.KeyGrokWebChat, StringComparison.OrdinalIgnoreCase))
            {
                return $"{key} requires an input image or produces video; goal loops render text prompts only";
            }
            var problem = runner.DescribeAvailabilityProblem(key);
            return problem ?? "";
        }

        public UiGoalLoopState Start(UiGoalLoop loop)
        {
            EnsureCapacity();
            loop.SystemPrompt = UiGoalLoopProtocol.SystemPrompt;
            loop.ProtocolVersion = UiGoalLoopProtocol.Version;
            if (loop.Generators == null || loop.Generators.Count == 0)
            {
                throw new InvalidOperationException("a goal loop needs at least one image generator");
            }
            if (loop.Generators.Count > UiGoalLoopSources.MaxGenerators)
            {
                throw new InvalidOperationException($"a goal loop allows at most {UiGoalLoopSources.MaxGenerators} image generators");
            }
            for (var i = 0; i < loop.Generators.Count; i++)
            {
                loop.Generators[i].Source = UiGoalLoopSources.Label(i);
            }
            loop.GeneratorKey = loop.Generators[0].Key;
            loop.GeneratorLabel = loop.Generators[0].Label;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entries = new List<UiGoalLoopEntry>
            {
                new()
                {
                    Index = 0,
                    Turn = 0,
                    Kind = UiGoalLoopKinds.Goal,
                    From = UiGoalLoopParties.User,
                    To = UiGoalLoopParties.Manager,
                    At = now,
                    Text = loop.Goal,
                },
            };
            var state = _loops.Create(loop, entries);
            Launch(state);
            return state;
        }

        public UiGoalLoopState Fork(
            UiGoalLoopState parent,
            int entryIndex,
            string? newText,
            int? maxTurns,
            string createdBy,
            string creatorLogin)
        {
            EnsureCapacity();
            List<UiGoalLoopEntry> parentEntries;
            UiGoalLoop parentLoop;
            lock (parent.Lock)
            {
                parentEntries = parent.Entries.Select(e => e.Clone()).ToList();
                parentLoop = parent.Loop.Clone();
            }
            var entries = UiGoalLoopPlanner.BuildForkEntries(
                parentEntries, entryIndex, newText, out var newGoal, parentLoop.ProtocolVersion);
            var child = parentLoop.Clone();
            child.Id = Guid.NewGuid().ToString("N")[..12];
            child.ParentLoopId = parentLoop.Id;
            child.ForkedAtEntry = entryIndex;
            child.CreatedBy = createdBy;
            child.CreatorLogin = creatorLogin;
            child.CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            child.UpdatedAtUnixMs = child.CreatedAtUnixMs;
            if (newGoal != null)
            {
                child.Goal = newGoal;
            }
            if (maxTurns.HasValue)
            {
                child.MaxTurns = ValidateMaxTurns(maxTurns.Value);
            }
            child.Status = UiGoalLoopStatus.Running;
            child.StatusDetail = "";
            child.Activity = "";
            child.ActivitySinceUnixMs = null;
            RecomputeTotals(child, entries);
            entries.Add(new UiGoalLoopEntry
            {
                Index = entries.Count,
                Turn = entries[^1].Turn,
                Kind = UiGoalLoopKinds.Note,
                From = UiGoalLoopParties.System,
                To = UiGoalLoopParties.User,
                At = child.CreatedAtUnixMs,
                Text = newText == null
                    ? $"Forked from loop {parentLoop.Id} at entry {entryIndex} (resumed from here without edits)."
                    : $"Forked from loop {parentLoop.Id} at entry {entryIndex} with edited text.",
            });
            var state = _loops.Create(child, entries);
            Launch(state);
            return state;
        }

        public void Stop(UiGoalLoopState state, string actor)
        {
            lock (state.Lock)
            {
                if (!state.IsRunning)
                {
                    throw new InvalidOperationException("this loop is not running");
                }
                state.Loop.Status = UiGoalLoopStatus.Stopped;
                state.Loop.StatusDetail = $"stop requested by {actor}; finishing the in-flight step";
                state.Storage.SaveMetadata(state.Loop);
                state.Cts?.Cancel();
            }
        }

        public void Resume(UiGoalLoopState state, int? maxTurns)
        {
            lock (state.Lock)
            {
                if (state.IsRunning)
                {
                    throw new InvalidOperationException("this loop is already running");
                }
                if (maxTurns.HasValue)
                {
                    state.Loop.MaxTurns = ValidateMaxTurns(maxTurns.Value);
                }
                var step = NextStepLocked(state);
                if (step.Kind == UiGoalLoopStepKind.Done)
                {
                    throw new InvalidOperationException(
                        "the manager declared this loop done; fork from an earlier entry to continue a different way");
                }
                if (step.Kind == UiGoalLoopStepKind.Exhausted)
                {
                    throw new InvalidOperationException(
                        $"{step.Reason}; resume with a larger max turns value");
                }
                if (step.Kind == UiGoalLoopStepKind.Invalid)
                {
                    throw new InvalidOperationException(step.Reason ?? "the loop cannot continue");
                }
                state.Loop.Status = UiGoalLoopStatus.Running;
                state.Loop.StatusDetail = "";
            }
            Launch(state);
        }

        public static int ValidateMaxTurns(int value) => UiGoalLoopPlanner.ValidateMaxTurns(value);

        private void Launch(UiGoalLoopState state)
        {
            lock (_runningLock)
            {
                if (_running >= MaxRunningLoops)
                {
                    lock (state.Lock)
                    {
                        state.Loop.Status = UiGoalLoopStatus.Stopped;
                        state.Loop.StatusDetail = $"{MaxRunningLoops} goal loops are already running on this server; resume this one after another finishes";
                        state.Storage.SaveMetadata(state.Loop);
                    }
                    throw new InvalidOperationException(state.Loop.StatusDetail);
                }
                _running++;
            }
            lock (state.Lock)
            {
                state.Cts = new CancellationTokenSource();
                state.Loop.Status = UiGoalLoopStatus.Running;
                state.Loop.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                state.Storage.SaveMetadata(state.Loop);
                var ct = state.Cts.Token;
                state.RunTask = Task.Run(() => RunAsync(state, ct));
            }
        }

        private async Task RunAsync(UiGoalLoopState state, CancellationToken ct)
        {
            var loop = state.Loop;
            var tag = $"[goal #{loop.Id}]";
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    UiGoalLoopStep step;
                    lock (state.Lock)
                    {
                        step = NextStepLocked(state);
                    }
                    switch (step.Kind)
                    {
                        case UiGoalLoopStepKind.AskManagerDesign:
                        case UiGoalLoopStepKind.AskManagerReview:
                            await AskManagerAsync(state, step, ct);
                            break;
                        case UiGoalLoopStepKind.Objection:
                            AppendObjection(state, step);
                            break;
                        case UiGoalLoopStepKind.Render:
                            await RenderAsync(state, step, ct);
                            break;
                        case UiGoalLoopStepKind.Done:
                            Finish(state, UiGoalLoopStatus.Done, step.Reason ?? "the manager declared the goal reached",
                                $"Loop done: {step.Reason}");
                            return;
                        case UiGoalLoopStepKind.Exhausted:
                            Finish(state, UiGoalLoopStatus.Exhausted, step.Reason ?? "turn limit reached",
                                $"Turn limit reached: {step.Reason}. Resume with more turns to render the manager's pending prompt.");
                            return;
                        case UiGoalLoopStepKind.Invalid:
                            Finish(state, UiGoalLoopStatus.Failed, step.Reason ?? "invalid loop state", $"Loop cannot continue: {step.Reason}");
                            return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Finish(state, UiGoalLoopStatus.Stopped, "stopped by the operator", "Stopped by the operator.");
            }
            catch (Exception ex)
            {
                Logger.Log($"{tag} failed: {ex}");
                Finish(state, UiGoalLoopStatus.Failed, ex.Message, $"Step failed: {ex.Message} — resume retries this step.");
            }
            finally
            {
                lock (_runningLock) _running--;
                lock (state.Lock)
                {
                    state.Cts?.Dispose();
                    state.Cts = null;
                }
            }
        }

        private void Finish(UiGoalLoopState state, string status, string detail, string noteText)
        {
            lock (state.Lock)
            {
                var loop = state.Loop;
                // A stop requested mid-step wins over the step's own outcome
                // unless the step reached a terminal decision.
                if (loop.Status == UiGoalLoopStatus.Stopped && status == UiGoalLoopStatus.Failed)
                {
                    status = UiGoalLoopStatus.Stopped;
                    detail = "stopped by the operator";
                    noteText = "Stopped by the operator.";
                }
                loop.Status = status;
                loop.StatusDetail = detail;
                loop.Activity = "";
                loop.ActivitySinceUnixMs = null;
                AppendEntryLocked(state, new UiGoalLoopEntry
                {
                    Kind = UiGoalLoopKinds.Note,
                    From = UiGoalLoopParties.System,
                    To = UiGoalLoopParties.User,
                    Turn = state.Entries.Count > 0 ? state.Entries[^1].Turn : 0,
                    Text = noteText,
                    Error = status == UiGoalLoopStatus.Failed ? detail : null,
                });
            }
        }

        private void SetActivity(UiGoalLoopState state, string activity)
        {
            lock (state.Lock)
            {
                state.Loop.Activity = activity;
                state.Loop.ActivitySinceUnixMs = activity.Length == 0 ? null : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                state.Loop.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                state.Storage.SaveMetadata(state.Loop);
            }
        }

        private void AppendEntryLocked(UiGoalLoopState state, UiGoalLoopEntry entry)
        {
            entry.Index = state.Entries.Count;
            if (entry.At == 0)
            {
                entry.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            state.Entries.Add(entry);
            state.Storage.AppendEntry(entry);
            RecomputeTotals(state.Loop, state.Entries);
            state.Loop.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state.Storage.SaveMetadata(state.Loop);
        }

        private void ReplaceEntryLocked(UiGoalLoopState state, int index, UiGoalLoopEntry entry)
        {
            entry.Index = index;
            state.Entries[index] = entry;
            state.Loop.Revision++;
            state.Storage.WriteAllEntries(state.Entries);
            RecomputeTotals(state.Loop, state.Entries);
            state.Loop.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state.Storage.SaveMetadata(state.Loop);
        }

        private static void RecomputeTotals(UiGoalLoop loop, IReadOnlyList<UiGoalLoopEntry> entries)
        {
            loop.EntryCount = entries.Count;
            loop.TurnsRendered = UiGoalLoopPlanner.CountRenderedTurns(entries);
            double? last = null, best = null;
            int? bestTurn = null;
            string? bestVariant = null, bestSource = null;
            decimal managerCost = 0, renderCost = 0;
            var managerCostKnown = true;
            int inTok = 0, outTok = 0;
            foreach (var e in entries)
            {
                if (e.Kind == UiGoalLoopKinds.Review && e.Manager?.Parsed != null)
                {
                    double? reviewRefine = null;
                    foreach (var scored in e.Manager.Parsed.Evaluations())
                    {
                        // "Last" is the latest review's best primary (refine)
                        // score across sources.
                        if (scored.Variant != UiGoalLoopVariants.Fresh
                            && (reviewRefine == null || scored.Evaluation.Score > reviewRefine))
                        {
                            reviewRefine = scored.Evaluation.Score;
                        }
                        if (best == null || scored.Evaluation.Score > best)
                        {
                            best = scored.Evaluation.Score;
                            bestTurn = e.Turn;
                            bestVariant = scored.Variant;
                            bestSource = scored.Source;
                        }
                    }
                    if (reviewRefine != null)
                    {
                        last = reviewRefine;
                    }
                }
                if (UiGoalLoopKinds.IsManagerReply(e.Kind) && e.Manager != null)
                {
                    inTok += e.Manager.InputTokens ?? 0;
                    outTok += e.Manager.OutputTokens ?? 0;
                    if (e.Manager.CostUsd.HasValue)
                    {
                        managerCost += e.Manager.CostUsd.Value;
                    }
                    else if (e.Manager.InputTokens.HasValue || e.Manager.OutputTokens.HasValue)
                    {
                        // Tokens were billed but no verified price is on
                        // file for this model: the total is a lower bound.
                        managerCostKnown = false;
                    }
                }
                if (e.Kind == UiGoalLoopKinds.RenderResult && e.Render?.Cost.HasValue == true)
                {
                    renderCost += e.Render.Cost.Value;
                }
            }
            loop.LastScore = last;
            loop.BestScore = best;
            loop.BestTurn = bestTurn;
            loop.BestVariant = bestVariant;
            loop.BestSource = bestSource;
            loop.ManagerCostUsd = managerCost;
            loop.ManagerCostKnown = managerCostKnown;
            loop.RenderCostUsd = renderCost;
            loop.ManagerInputTokens = inTok;
            loop.ManagerOutputTokens = outTok;
            loop.EffectiveGoalKind = UiGoalLoopPlanner.ResolveEffectiveGoalKind(loop, entries, out var kindSource);
            loop.GoalKindSource = kindSource;
        }

        private static UiGoalLoopStep NextStepLocked(UiGoalLoopState state)
            => UiGoalLoopPlanner.DetermineNextStep(
                state.Entries, state.Loop.MaxTurns, state.Loop.EffectiveGoalKind, state.Loop.ProtocolVersion,
                state.Loop.GeneratorList());

        // ---- manager turns ----

        // The manager's "done" stands on file (its evaluation of the image is
        // kept and counts toward best-so-far); this system message rejects
        // the decision and the next AskManager call bars "done".
        private void AppendObjection(UiGoalLoopState state, UiGoalLoopStep step)
        {
            lock (state.Lock)
            {
                var best = UiGoalLoopPlanner.BestReview(state.Entries)
                    ?? throw new InvalidOperationException("objection without any scored review");
                var latest = UiGoalLoopPlanner.LatestRefineScore(state.Entries, best.Source)
                    ?? throw new InvalidOperationException("objection without a latest refine review");
                AppendEntryLocked(state, new UiGoalLoopEntry
                {
                    Turn = step.Turn,
                    Kind = UiGoalLoopKinds.Objection,
                    From = UiGoalLoopParties.System,
                    To = UiGoalLoopParties.Manager,
                    Text = UiGoalLoopProtocol.BuildObjectionText(
                        step.Turn, state.Loop.MaxTurns, best.Turn, best.Score, latest,
                        state.Loop.ProtocolVersion, best.Variant, best.Source),
                });
                Logger.Log($"[goal #{state.Loop.Id}] objected to \"done\" at turn {step.Turn}: {step.Reason}");
            }
        }

        private async Task AskManagerAsync(UiGoalLoopState state, UiGoalLoopStep step, CancellationToken ct)
        {
            var loop = state.Loop;
            var definition = ManagerCatalog.Find(loop.ManagerKey)
                ?? throw new InvalidOperationException($"unknown manager '{loop.ManagerKey}'");
            var client = ManagerCatalog.Build(definition, _settings);

            // A render-result tail means the review request has not been
            // written yet: compose it now (with the image conformed for
            // transport) so the entry list carries exactly what is sent.
            UiGoalLoopEntry? tail;
            lock (state.Lock) tail = UiGoalLoopPlanner.EffectiveTail(state.Entries);
            if (tail != null && tail.Kind == UiGoalLoopKinds.RenderResult)
            {
                await AppendReviewRequestAsync(state, tail.Turn, ct);
            }
            // After an objection the same review is asked again with "done"
            // barred; a second "done" is then a contract violation.
            var permitDone = tail == null || tail.Kind != UiGoalLoopKinds.Objection;

            SetActivity(state, $"waiting for {loop.ManagerLabel}");
            List<ManagerChatMessage> history;
            List<UiGoalLoopEntry> snapshot;
            lock (state.Lock) snapshot = state.Entries.Select(e => e.Clone()).ToList();
            history = await BuildManagerHistoryAsync(state, snapshot, definition.ImageLimits, ct);
            var expectEvaluation = snapshot.Any(e => e.Kind == UiGoalLoopKinds.ReviewRequest);
            // Protocol 5: the renders the latest review request describes
            // (every render result of its turn), so the parser can demand
            // exactly one evaluation per render shown.
            IReadOnlyList<UiGoalLoopRenderKey>? shownRenders = null;
            if (expectEvaluation && loop.ProtocolVersion >= 5)
            {
                var latestRequest = snapshot.Last(e => e.Kind == UiGoalLoopKinds.ReviewRequest);
                shownRenders = snapshot
                    .Where(e => e.Kind == UiGoalLoopKinds.RenderResult && e.Turn == latestRequest.Turn && e.Index < latestRequest.Index)
                    .Select(e => new UiGoalLoopRenderKey(e.Render?.Variant, e.Render?.Source))
                    .Distinct()
                    .ToList();
            }

            ManagerChatReply reply;
            await _managerCalls.WaitAsync(ct);
            try
            {
                Logger.Log($"[goal #{loop.Id}] asking {definition.Model} ({history.Count} message(s) in history, turn {step.Turn})");
                reply = await client.CompleteAsync(loop.SystemPrompt, history, ct);
            }
            finally
            {
                _managerCalls.Release();
            }

            var data = new UiGoalLoopManagerData
            {
                Model = reply.Model,
                ProviderReasoning = string.IsNullOrWhiteSpace(reply.ProviderReasoning) ? null : reply.ProviderReasoning,
                InputTokens = reply.InputTokens,
                OutputTokens = reply.OutputTokens,
                CostUsd = ManagerCatalog.EstimateCostUsd(definition, reply.InputTokens, reply.OutputTokens),
                ProviderStop = string.IsNullOrWhiteSpace(reply.ProviderStop) ? null : reply.ProviderStop,
                RequestBytes = reply.RequestBytes,
            };
            string? error = null;
            if (data.ProviderStop != null)
            {
                // The provider itself declared the reply abnormal (refusal,
                // safety block, length cut-off). Record that statement as the
                // step error; never parse a refused or truncated body as a
                // design, and never switch managers on the user's behalf.
                error = $"the manager did not answer — {data.ProviderStop}";
            }
            else
            {
                try
                {
                    data.Parsed = UiGoalLoopProtocol.ParseManagerReply(reply.Text, expectEvaluation, loop.ProtocolVersion, permitDone, shownRenders);
                }
                catch (InvalidDataException ex)
                {
                    data.ParseError = ex.Message;
                    error = ex.Message;
                }
            }
            var entry = new UiGoalLoopEntry
            {
                Turn = step.Turn,
                Kind = step.Kind == UiGoalLoopStepKind.AskManagerDesign ? UiGoalLoopKinds.Design : UiGoalLoopKinds.Review,
                From = UiGoalLoopParties.Manager,
                To = UiGoalLoopParties.System,
                Ms = reply.Ms,
                Text = reply.Text,
                Error = error,
                Manager = data,
                WireRequest = reply.RedactedRequest,
                WireResponse = reply.RawResponse,
            };
            lock (state.Lock)
            {
                AppendEntryLocked(state, entry);
            }
            SetActivity(state, "");
            if (error != null)
            {
                throw new InvalidDataException(error);
            }
            Logger.Log($"[goal #{loop.Id}] manager turn {step.Turn}: decision={data.Parsed!.Decision}"
                + string.Concat(data.Parsed.Evaluations().Select(s =>
                    $" {s.Variant ?? "score"}{(s.Source != null ? $"/{s.Source}" : "")}={s.Evaluation.Score:0.#}")));
        }

        // Writes the review request for a turn whose renders are all on
        // file: one render before protocol 4, the refine + fresh pair from
        // protocol 4. Images are attached in variant order.
        private async Task AppendReviewRequestAsync(UiGoalLoopState state, int turn, CancellationToken ct)
        {
            var loop = state.Loop;
            List<UiGoalLoopEntry> results;
            List<UiGoalLoopEntry> requests;
            UiGoalLoopEntry? design;
            List<UiGoalLoopSentImage> prior;
            lock (state.Lock)
            {
                results = state.Entries.Where(e => e.Kind == UiGoalLoopKinds.RenderResult && e.Turn == turn).ToList();
                requests = state.Entries.Where(e => e.Kind == UiGoalLoopKinds.RenderRequest && e.Turn == turn).ToList();
                var firstResultIndex = results.Count > 0 ? results.Min(r => r.Index) : int.MaxValue;
                design = state.Entries
                    .LastOrDefault(e => UiGoalLoopKinds.IsManagerReply(e.Kind) && e.Manager?.Parsed != null
                        && e.Error == null && e.Index < firstResultIndex);
                prior = state.Entries
                    .Where(e => e.Kind == UiGoalLoopKinds.ReviewRequest && e.Turn < turn)
                    .SelectMany(e => e.Images ?? new List<UiGoalLoopSentImage>())
                    .ToList();
            }
            if (results.Count == 0)
            {
                throw new InvalidOperationException($"turn {turn} has no render result to review");
            }
            var definition = ManagerCatalog.Find(loop.ManagerKey)
                ?? throw new InvalidOperationException($"unknown manager '{loop.ManagerKey}'");

            // Sizes of every image this request will carry, so each plan
            // accounts for the whole request.
            var infos = new Dictionary<UiGoalLoopEntry, UiGoalLoopImageInfo>();
            foreach (var result in results)
            {
                var render = result.Render ?? throw new InvalidOperationException("render result entry has no render data");
                if (render.Ok != true)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(render.JobId))
                {
                    throw new InvalidOperationException("successful render result lacks its job id");
                }
                // Describe the ORIGINAL file; the bytes are not retained.
                var (info, _) = await LoadRenderImageAsync(render.JobId, render.GeneratorKey, 0, ct);
                infos[result] = info;
            }
            var imagesInRequest = prior.Count + infos.Count;
            var sizes = prior.Select(i => (long)i.Bytes).Concat(infos.Values.Select(i => i.RawBytes)).ToList();
            var pressure = UiGoalLoopImageTransport.RequestBudgetPressure(definition.ImageLimits, sizes);

            var turnRenders = new List<UiGoalLoopProtocol.UiGoalLoopTurnRender>();
            var sentImages = new List<UiGoalLoopSentImage>();
            foreach (var result in results
                .OrderBy(r => r.Render!.Variant == null ? 0 : Array.IndexOf(UiGoalLoopVariants.All, r.Render!.Variant))
                .ThenBy(r => r.Render!.Source ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.Index))
            {
                var render = result.Render!;
                var request = requests.LastOrDefault(e => e.Index < result.Index && e.Render?.Variant == render.Variant
                        && string.Equals(e.Render?.GeneratorKey, render.GeneratorKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"render result {result.Index} has no preceding render request");
                UiGoalLoopSentImage? sent = null;
                if (infos.TryGetValue(result, out var info))
                {
                    var plan = UiGoalLoopImageTransport.Make(definition.ImageLimits, info, imagesInRequest, pressure);
                    sent = new UiGoalLoopSentImage
                    {
                        JobId = render.JobId!,
                        Variant = render.Variant,
                        Source = render.Source,
                        GeneratorKey = render.GeneratorKey,
                        ImageIndex = 0,
                        Url = render.ImageUrl ?? "",
                        ThumbUrl = render.ThumbUrl,
                        Mime = info.Mime,
                        Bytes = (int)Math.Min(int.MaxValue, info.RawBytes),
                        Width = info.Width,
                        Height = info.Height,
                        OriginalWidth = info.Width,
                        OriginalHeight = info.Height,
                        Downscaled = false,
                        Transport = plan.Describe(info),
                    };
                    sentImages.Add(sent);
                }
                var designPrompt = render.Variant == UiGoalLoopVariants.Fresh
                    ? design?.Manager?.Parsed?.FreshPrompt
                    : design?.Manager?.Parsed?.Prompt;
                turnRenders.Add(new UiGoalLoopProtocol.UiGoalLoopTurnRender(
                    render.Variant ?? "", render, request.Text, designPrompt, result.Error, sent, render.Source));
            }

            (int Turn, string? Variant, string? Source, double Score)? bestSoFar;
            string? effectiveKind;
            lock (state.Lock)
            {
                bestSoFar = UiGoalLoopPlanner.BestReview(state.Entries);
                effectiveKind = loop.EffectiveGoalKind;
            }
            string text;
            if (loop.ProtocolVersion >= 5)
            {
                var parsedDesign = design?.Manager?.Parsed;
                var continuedFrom = parsedDesign?.ContinueFrom != null
                    ? new UiGoalLoopRenderKey(parsedDesign.ContinueFrom, parsedDesign.ContinueFromSource)
                    : null;
                text = UiGoalLoopProtocol.BuildMultiSourceReviewRequestText(
                    turn, loop.MaxTurns, turnRenders, loop.GeneratorList().Select(g => g.Source!).ToList(), continuedFrom,
                    effectiveKind, bestSoFar?.Turn, bestSoFar?.Variant, bestSoFar?.Source, bestSoFar?.Score);
            }
            else if (loop.ProtocolVersion >= 4)
            {
                text = UiGoalLoopProtocol.BuildPairReviewRequestText(
                    turn, loop.MaxTurns, turnRenders, design?.Manager?.Parsed?.ContinueFrom,
                    effectiveKind, bestSoFar?.Turn, bestSoFar?.Variant, bestSoFar?.Score);
            }
            else
            {
                var single = turnRenders[0];
                text = UiGoalLoopProtocol.BuildReviewRequestText(
                    turn, loop.MaxTurns, single.Render, single.RenderedPrompt, single.DesignPrompt, single.RenderError,
                    single.Sent, effectiveKind, bestSoFar?.Turn, bestSoFar?.Score);
            }
            lock (state.Lock)
            {
                AppendEntryLocked(state, new UiGoalLoopEntry
                {
                    Turn = turn,
                    Kind = UiGoalLoopKinds.ReviewRequest,
                    From = UiGoalLoopParties.System,
                    To = UiGoalLoopParties.Manager,
                    Text = text,
                    Images = sentImages.Count == 0 ? null : sentImages,
                });
            }
        }

        private Task<List<ManagerChatMessage>> BuildManagerHistoryAsync(
            UiGoalLoopState state, IReadOnlyList<UiGoalLoopEntry> entries, ManagerImageLimits limits, CancellationToken ct)
        {
            // Every image in this call, so each image's transport plan can
            // account for the request's image count and total size.
            var allSent = entries
                .Where(e => e.Kind == UiGoalLoopKinds.ReviewRequest)
                .SelectMany(e => e.Images ?? new List<UiGoalLoopSentImage>())
                .ToList();
            var pressure = UiGoalLoopImageTransport.RequestBudgetPressure(limits, allSent.Select(i => (long)i.Bytes));
            var imagesInRequest = allSent.Count;

            var history = new List<ManagerChatMessage>();
            foreach (var e in entries)
            {
                switch (e.Kind)
                {
                    case UiGoalLoopKinds.Goal:
                        history.Add(new ManagerChatMessage
                        {
                            Role = "user",
                            Text = UiGoalLoopProtocol.BuildGoalMessage(
                                e.Text, state.Loop.MaxTurns, state.Loop.ProtocolVersion,
                                UiGoalLoopGoalKinds.IsManagerValue(state.Loop.GoalKind) ? state.Loop.GoalKind : null,
                                state.Loop.GeneratorList().Count),
                        });
                        break;
                    case UiGoalLoopKinds.Objection:
                        history.Add(new ManagerChatMessage { Role = "user", Text = e.Text });
                        break;
                    case UiGoalLoopKinds.Design:
                    case UiGoalLoopKinds.Review:
                        if (e.Error == null && e.Manager?.Parsed != null)
                        {
                            history.Add(new ManagerChatMessage { Role = "assistant", Text = e.Text });
                        }
                        break;
                    case UiGoalLoopKinds.ReviewRequest:
                    {
                        var images = new List<ManagerChatImage>();
                        foreach (var sent in e.Images ?? new List<UiGoalLoopSentImage>())
                        {
                            var captured = sent;
                            var turn = e.Turn;
                            // Loaded only when the request body reaches this
                            // part, then released: one image resident at a time.
                            images.Add(new ManagerChatImage
                            {
                                Load = async token =>
                                {
                                    var (info, raw) = await LoadRenderImageAsync(captured.JobId, captured.GeneratorKey, captured.ImageIndex, token);
                                    var plan = UiGoalLoopImageTransport.Make(limits, info, imagesInRequest, pressure);
                                    return UiGoalLoopImageTransport.Apply(
                                        plan, limits, info, raw,
                                        $"turn {turn} render {captured.JobId}/{captured.GeneratorKey}/{captured.ImageIndex}");
                                },
                            });
                        }
                        history.Add(new ManagerChatMessage { Role = "user", Text = e.Text, Images = images });
                        break;
                    }
                }
            }
            if (history.Count == 0 || history[^1].Role != "user")
            {
                throw new InvalidOperationException("the conversation does not end with a message to the manager");
            }
            return Task.FromResult(history);
        }

        // Reads the exact rendered original (local disk, or the recorded B2
        // object with SHA-256 verification when the local raw was evicted)
        // and identifies it without decoding pixels. Nothing is cached: disk
        // is the source of truth and a full-resolution history must not stay
        // resident in the shared-site process.
        private async Task<(UiGoalLoopImageInfo Info, byte[] Raw)> LoadRenderImageAsync(
            string jobId, string gen, int index, CancellationToken ct)
        {
            var job = _jobs.Get(jobId)
                ?? throw new InvalidOperationException($"render job {jobId} no longer exists; its image cannot be sent to the manager");
            var bytes = await _jobRunner.TryGetImageBytesIncludingHostedAsync(job, gen, index)
                ?? throw new InvalidOperationException($"render job {jobId} has no readable image for {gen}/{index}");
            ct.ThrowIfCancellationRequested();
            string mime;
            try
            {
                mime = DescriberImageFormat.DetectMime(bytes.Bytes);
            }
            catch (InvalidOperationException)
            {
                // Decodable but not PNG/JPEG/WEBP (e.g. a rasterized SVG
                // preview); the transport plan re-encodes it losslessly.
                mime = "image/unknown";
            }
            var identified = Image.Identify(bytes.Bytes)
                ?? throw new InvalidOperationException($"render job {jobId} image {gen}/{index} is not a decodable raster image");
            return (new UiGoalLoopImageInfo(mime, identified.Width, identified.Height, bytes.Bytes.LongLength), bytes.Bytes);
        }

        // ---- all-turns contact sheet ----

        public const string SheetFileName = "sheet.png";
        // One sheet build at a time per process: every turn image is decoded
        // for the duration of one build (capped to a 1024 px long edge).
        private readonly SemaphoreSlim _sheetBuilds = new(1, 1);

        /// Builds (or rebuilds) the loop's all-turns sheet from the entries
        /// on file right now and records it on the loop metadata. Runs
        /// against a snapshot, so a running loop can be sheeted mid-way.
        public async Task<string> BuildSheetAsync(UiGoalLoopState state, CancellationToken ct)
        {
            UiGoalLoop loop;
            List<UiGoalLoopEntry> entries;
            lock (state.Lock)
            {
                loop = state.Loop.Clone();
                entries = state.Entries.Select(e => e.Clone()).ToList();
            }
            var cells = new List<ImageCombiner.LoopSheetCell>();
            var cellTurns = new HashSet<int>();
            // Turn order, refine before fresh (results land in completion
            // order on file).
            foreach (var result in entries.Where(e => e.Kind == UiGoalLoopKinds.RenderResult)
                .OrderBy(e => e.Turn)
                .ThenBy(e => e.Render?.Variant == null ? 0 : Array.IndexOf(UiGoalLoopVariants.All, e.Render.Variant))
                .ThenBy(e => e.Render?.Source ?? "", StringComparer.Ordinal)
                .ThenBy(e => e.Index))
            {
                var render = result.Render
                    ?? throw new InvalidOperationException($"entry {result.Index} is a render result without render data");
                var request = entries.LastOrDefault(e => e.Kind == UiGoalLoopKinds.RenderRequest && e.Index < result.Index
                        && e.Turn == result.Turn && e.Render?.Variant == render.Variant
                        && string.Equals(e.Render?.GeneratorKey, render.GeneratorKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"render result {result.Index} has no preceding render request");
                // The review of THIS render: the first accepted review after
                // it for the same turn (an objected-to review still holds the
                // image's evaluation; the re-asked review carries the same
                // score of the same image, so the first one is exact).
                var review = entries.FirstOrDefault(e => e.Kind == UiGoalLoopKinds.Review && e.Index > result.Index
                    && e.Turn == result.Turn && e.Error == null && e.Manager?.Parsed?.EvaluationOf(render.Variant, render.Source) != null);
                var evaluation = review?.Manager?.Parsed?.EvaluationOf(render.Variant, render.Source);
                cellTurns.Add(result.Turn);
                var (w, h) = ParseSize(render.Size);
                string primary;
                var secondaryParts = new List<string>();
                if (render.Ok == true)
                {
                    primary = evaluation != null ? $"{evaluation.Score:0.#}/10" : "unscored";
                }
                else
                {
                    primary = "failed";
                }
                secondaryParts.Add($"turn {result.Turn} of {loop.MaxTurns}");
                if (render.Variant != null)
                {
                    secondaryParts.Add(render.Variant == UiGoalLoopVariants.Fresh ? "FRESH (from scratch)" : "REFINE");
                }
                if (render.Source != null)
                {
                    secondaryParts.Add($"source {render.Source}: {render.GeneratorLabel}");
                }
                if (!string.IsNullOrWhiteSpace(render.Size))
                {
                    secondaryParts.Add(render.Size!);
                }
                if (evaluation != null && review!.Manager!.Parsed!.Decision == "done")
                {
                    secondaryParts.Add("manager: done");
                }
                else if (evaluation != null && render.Variant != null && review!.Manager!.Parsed!.ContinueFrom == render.Variant
                    && review.Manager.Parsed.ContinueFromSource == render.Source)
                {
                    secondaryParts.Add("manager: continue from this one");
                }
                var text = new StringBuilder();
                text.Append("PROMPT: ").Append(request.Text.Trim());
                if (evaluation != null)
                {
                    text.Append("\n\nMANAGER: ").Append(evaluation.Assessment.Trim());
                    if (evaluation.Problems.Count > 0)
                    {
                        text.Append("\nProblems: ").Append(string.Join("; ", evaluation.Problems));
                    }
                }
                else if (render.Ok == true)
                {
                    text.Append("\n\nMANAGER: (not reviewed)");
                }
                Func<CancellationToken, Task<byte[]>>? load = null;
                if (render.Ok == true)
                {
                    var jobId = render.JobId
                        ?? throw new InvalidOperationException($"successful render at entry {result.Index} lacks its job id");
                    var gen = render.GeneratorKey;
                    load = async token => (await LoadRenderImageAsync(jobId, gen, 0, token)).Raw;
                }
                cells.Add(new ImageCombiner.LoopSheetCell(
                    load, primary, string.Join("  ", secondaryParts), text.ToString(),
                    render.Ok == true ? null : (result.Error ?? "render failed"), w, h));
            }
            if (cells.Count == 0)
            {
                throw new InvalidOperationException("this loop has not rendered anything yet");
            }

            var header = new StringBuilder();
            header.Append("GOAL: ").Append(loop.Goal.Trim());
            var generatorList = loop.GeneratorList();
            var generatorText = generatorList.Count == 1 && generatorList[0].Source == null
                ? $"Generator: {loop.GeneratorLabel}"
                : $"Generators ({generatorList.Count}): " + string.Join("   ", generatorList.Select(g => $"{g.Source}: {g.Label}"));
            header.Append($"\n\n{generatorText}   Manager: {loop.ManagerLabel} ({loop.ManagerModel})   Started by: {loop.CreatedBy}");
            header.Append(loop.RendersPerTurn > 1
                ? $"\nRendered {loop.TurnsRendered} turn(s) of {loop.MaxTurns} allowed, {cells.Count} images (each turn: REFINE of the chosen lineage + FRESH from-scratch re-attempt{(generatorList.Count > 1 ? $", on each of {generatorList.Count} sources" : "")})"
                : $"\nRendered {cells.Count} turn(s) of {loop.MaxTurns} allowed");
            if (loop.EffectiveGoalKind != null)
            {
                header.Append($"   Goal kind: {loop.EffectiveGoalKind} ({loop.GoalKindSource})");
            }
            if (loop.BestTurn.HasValue && loop.BestScore.HasValue)
            {
                header.Append($"   Best: turn {loop.BestTurn}{(loop.BestVariant != null ? $" {loop.BestVariant}" : "")}{(loop.BestSource != null ? $" source {loop.BestSource}" : "")} (score {loop.BestScore:0.#}/10)");
            }
            header.Append($"\nStatus: {loop.Status}");
            if (!string.IsNullOrWhiteSpace(loop.StatusDetail))
            {
                header.Append(" — ").Append(loop.StatusDetail.Trim());
            }
            header.Append($"\nLoop {loop.Id}, sheet built {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");

            var path = Path.Combine(state.Storage.Directory, SheetFileName);
            await _sheetBuilds.WaitAsync(ct);
            try
            {
                await ImageCombiner.CreateGoalLoopSheetAsync(cells, header.ToString(), path, ct);
            }
            finally
            {
                _sheetBuilds.Release();
            }
            DlMirror.Copy(path, _settings.FlatImageMirrorPath);
            lock (state.Lock)
            {
                state.Loop.SheetFile = SheetFileName;
                state.Loop.SheetEntryCount = entries.Count;
                // Distinct turns, not cells: a protocol-4 turn contributes two cells.
                state.Loop.SheetTurns = cellTurns.Count;
                state.Loop.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                state.Storage.SaveMetadata(state.Loop);
            }
            Logger.Log($"[goal #{loop.Id}] sheet built: {cells.Count} turn(s) → {path}");
            return path;
        }

        public static string? SheetPath(UiGoalLoopState state)
        {
            lock (state.Lock)
            {
                if (state.Loop.SheetFile == null)
                {
                    return null;
                }
                var path = Path.Combine(state.Storage.Directory, state.Loop.SheetFile);
                return File.Exists(path) ? path : null;
            }
        }

        private static (int Width, int Height) ParseSize(string? size)
        {
            if (!string.IsNullOrWhiteSpace(size))
            {
                var parts = size.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
                {
                    return (w, h);
                }
            }
            return (1024, 1024);
        }

        // ---- render turns ----

        // Renders every planned render of the step: one before protocol 4,
        // the refine + fresh pair from protocol 4, run concurrently as two
        // ordinary UiJobs. Request entries are written first, in variant
        // order, so indices and the main-page lineage badge are exact; each
        // result is appended as its job finishes, so the page shows the
        // first image without waiting for the second.
        private async Task RenderAsync(UiGoalLoopState state, UiGoalLoopStep step, CancellationToken ct)
        {
            var loop = state.Loop;
            IReadOnlyList<UiGoalLoopPlannedRender> renders = step.Renders.Count > 0
                ? step.Renders
                : new[] { new UiGoalLoopPlannedRender(null, step.Prompt ?? "", null) };
            foreach (var r in renders)
            {
                if (string.IsNullOrWhiteSpace(r.Prompt))
                {
                    throw new InvalidOperationException(
                        $"the manager's {r.Variant ?? "render"} prompt for turn {step.Turn} is blank");
                }
            }
            foreach (var key in renders.Select(r => r.GeneratorKey ?? loop.GeneratorKey).Distinct(StringComparer.Ordinal))
            {
                var problem = GeneratorProblem(_jobRunner, key);
                if (problem.Length > 0)
                {
                    throw new InvalidOperationException($"generator {key} is not available: {problem}");
                }
            }
            var admissions = new List<IDisposable>(renders.Count);
            try
            {
                foreach (var _ in renders)
                {
                    if (!_jobRunner.TryAcquireJobAdmission(out var admission))
                    {
                        throw new InvalidOperationException(
                            $"the UI job queue is full ({_jobRunner.MaxPendingJobs} pending jobs); resume after a job finishes");
                    }
                    admissions.Add(admission!);
                }
                var started = new List<(UiGoalLoopPlannedRender Planned, UiJob Job, UiJobSpec Spec)>(renders.Count);
                foreach (var planned in renders)
                {
                    started.Add(StartRenderJob(state, step.Turn, planned));
                }
                var generatorNames = string.Join(" + ", loop.GeneratorList()
                    .Where(g => renders.Any(r => (r.GeneratorKey ?? loop.GeneratorKey) == g.Key))
                    .Select(g => g.Source != null ? $"{g.Source}: {g.Label}" : g.Label));
                SetActivity(state, renders.Count == 1
                    ? $"rendering turn {step.Turn} with {generatorNames} (job {started[0].Job.Id})"
                    : $"rendering turn {step.Turn} ({string.Join(" + ", renders.Select(r => r.Variant).Distinct())}; {renders.Count} renders) with {generatorNames}");
                // The provider calls themselves are not cancellable through
                // the job runner; a stop takes effect once the renders return.
                await Task.WhenAll(started.Select(s => RunRenderJobAsync(state, step.Turn, s.Planned, s.Job, s.Spec)));
            }
            finally
            {
                foreach (var a in admissions)
                {
                    a.Dispose();
                }
            }
            SetActivity(state, "");
            ct.ThrowIfCancellationRequested();
        }

        // Creates the UiJob for one render and writes its render-request
        // entry (in place when the plan names an unrendered request to fill).
        private (UiGoalLoopPlannedRender Planned, UiJob Job, UiJobSpec Spec) StartRenderJob(
            UiGoalLoopState state, int turn, UiGoalLoopPlannedRender planned)
        {
            var loop = state.Loop;
            var prompt = planned.Prompt.Trim();
            var generator = ResolveGenerator(loop, planned);
            var genKeys = new List<string> { generator.Key };
            var job = new UiJob
            {
                Prompt = prompt,
                CreatedBy = loop.CreatedBy,
                CreatorLogin = loop.CreatorLogin,
                GeneratorKeys = genKeys,
            };
            var spec = new UiJobSpec
            {
                GeneratorKeys = genKeys,
                Quality = loop.Quality,
                Moderation = loop.Moderation,
                ImageCount = 1,
                Shape = loop.Shape,
                Detail = loop.Detail,
                // Deliberately no per-endpoint extra text: the manager
                // owns the entire prompt and must see exactly what the
                // generator saw.
                GeneratorExtraTexts = new Dictionary<string, string>(StringComparer.Ordinal),
            };
            _jobs.Add(job);
            _onRenderJobCreated?.Invoke(job);
            var requestEntry = new UiGoalLoopEntry
            {
                Turn = turn,
                Kind = UiGoalLoopKinds.RenderRequest,
                From = UiGoalLoopParties.System,
                To = UiGoalLoopParties.Generator,
                Text = prompt,
                Render = new UiGoalLoopRenderData
                {
                    JobId = job.Id,
                    Variant = planned.Variant,
                    Source = generator.Source,
                    GeneratorKey = generator.Key,
                    GeneratorLabel = generator.Label,
                    Shape = spec.Shape,
                    Detail = spec.Detail,
                    Quality = spec.Quality,
                    Moderation = spec.Moderation,
                },
            };
            int requestIndex;
            lock (state.Lock)
            {
                if (planned.ReplaceIndex.HasValue)
                {
                    var old = state.Entries[planned.ReplaceIndex.Value];
                    requestEntry.Edited = old.Edited;
                    requestEntry.OriginalText = old.OriginalText;
                    requestEntry.At = old.At;
                    ReplaceEntryLocked(state, planned.ReplaceIndex.Value, requestEntry);
                }
                else
                {
                    AppendEntryLocked(state, requestEntry);
                }
                requestIndex = requestEntry.Index;
            }
            job.Emit(new
            {
                type = "accepted",
                gens = genKeys,
                hasImage = false,
                inputCount = 0,
                inputWidth = (int?)null,
                inputHeight = (int?)null,
                shape = spec.Shape,
                detail = spec.Detail,
                quality = spec.Quality,
                moderation = spec.Moderation,
                n = spec.ImageCount,
                generatorExtraTexts = spec.GeneratorExtraTexts,
                sketchComposer = (object?)null,
                gpt2GuidanceEnabled = false,
                gpt2GuidanceText = "",
                // Exact lineage for the main-page card badge and for
                // tracing a render back to its loop turn.
                goalLoop = new
                {
                    id = loop.Id, turn, entryIndex = requestIndex, variant = planned.Variant, source = generator.Source, manager = loop.ManagerLabel,
                },
                prompt,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Logger.Log($"[goal #{loop.Id}] turn {turn}{(planned.Variant != null ? $" {planned.Variant}" : "")}{(generator.Source != null ? $" source {generator.Source}" : "")}: rendering with {generator.Key} as job {job.Id}");
            return (planned, job, spec);
        }

        // The generator a planned render names, by exact key; a plan without
        // a key means the loop's single generator (pre-protocol-5 loops).
        private static UiGoalLoopGenerator ResolveGenerator(UiGoalLoop loop, UiGoalLoopPlannedRender planned)
        {
            var list = loop.GeneratorList();
            if (planned.GeneratorKey == null)
            {
                return list[0];
            }
            return list.FirstOrDefault(g => string.Equals(g.Key, planned.GeneratorKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"planned render names generator '{planned.GeneratorKey}', which is not one of this loop's generators");
        }

        private async Task RunRenderJobAsync(UiGoalLoopState state, int turn, UiGoalLoopPlannedRender planned, UiJob job, UiJobSpec spec)
        {
            var loop = state.Loop;
            var generator = ResolveGenerator(loop, planned);
            await _jobRunner.RunJobAsync(job, spec);
            var result = ReadGenResult(job, generator.Key);
            var render = new UiGoalLoopRenderData
            {
                JobId = job.Id,
                Variant = planned.Variant,
                Source = generator.Source,
                GeneratorKey = generator.Key,
                GeneratorLabel = generator.Label,
                Shape = spec.Shape,
                Detail = spec.Detail,
                Quality = spec.Quality,
                Moderation = spec.Moderation,
                Ok = result.Ok,
                ImageUrl = result.ImageUrl,
                ThumbUrl = result.ThumbUrl,
                Size = result.Size,
                Cost = result.Cost,
                Label = result.Label,
                MediaType = result.MediaType,
                ErrorHint = result.ErrorHint,
                ErrorHintUrl = result.ErrorHintUrl,
            };
            var variantLabel = planned.Variant != null
                ? $"{planned.Variant} render{(generator.Source != null ? $", source {generator.Source}" : "")}: "
                : "";
            lock (state.Lock)
            {
                AppendEntryLocked(state, new UiGoalLoopEntry
                {
                    Turn = turn,
                    Kind = UiGoalLoopKinds.RenderResult,
                    From = UiGoalLoopParties.Generator,
                    To = UiGoalLoopParties.System,
                    Ms = result.Ms,
                    Text = result.Ok
                        ? $"{variantLabel}{generator.Label} returned {result.Size ?? "an image"}"
                        : $"{variantLabel}{generator.Label} failed: {result.Error}",
                    Error = result.Ok ? null : (result.Error ?? "generator failed without error text"),
                    Render = render,
                    WireResponse = result.EventJson,
                });
            }
        }

        private sealed record GenResult(
            bool Ok, string? Error, string? ErrorHint, string? ErrorHintUrl, long Ms, string? ImageUrl, string? ThumbUrl,
            string? Size, decimal? Cost, string? Label, string? MediaType, string EventJson);

        // The finished job's persisted gen-result for our single generator
        // is the exact record of what happened; nothing is inferred from
        // the filesystem.
        private static GenResult ReadGenResult(UiJob job, string gen)
        {
            var (events, _) = job.ReadFrom(0);
            string? found = null;
            foreach (var json in events)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "gen-result"
                    && root.TryGetProperty("gen", out var g) && string.Equals(g.GetString(), gen, StringComparison.OrdinalIgnoreCase)
                    && !(root.TryGetProperty("provisional", out var prov) && prov.ValueKind == JsonValueKind.True))
                {
                    found = json;
                }
            }
            if (found == null)
            {
                throw new InvalidOperationException($"job {job.Id} finished without a gen-result for {gen}");
            }
            using var result = JsonDocument.Parse(found);
            var r = result.RootElement;
            var ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            string? firstImage = null, firstThumb = null;
            if (r.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0
                && images[0].ValueKind == JsonValueKind.String)
            {
                firstImage = images[0].GetString();
            }
            if (r.TryGetProperty("thumbs", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0
                && thumbs[0].ValueKind == JsonValueKind.String)
            {
                firstThumb = thumbs[0].GetString();
            }
            if (ok && string.IsNullOrWhiteSpace(firstImage))
            {
                throw new InvalidOperationException($"job {job.Id} reported success for {gen} but recorded no image URL");
            }
            decimal? cost = null;
            if (r.TryGetProperty("cost", out var costEl) && costEl.ValueKind == JsonValueKind.Number)
            {
                cost = costEl.GetDecimal();
            }
            long ms = 0;
            if (r.TryGetProperty("ms", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
            {
                ms = msEl.GetInt64();
            }
            return new GenResult(
                ok,
                Str(r, "error"),
                Str(r, "errorHint"),
                Str(r, "errorHintUrl"),
                ms,
                firstImage,
                firstThumb,
                Str(r, "size"),
                cost,
                Str(r, "label"),
                Str(r, "mediaType"),
                found);
        }

        private static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
