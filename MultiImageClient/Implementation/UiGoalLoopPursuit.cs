#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiImageClient
{
    public sealed class UiGoalCriterion
    {
        [JsonRequired]
        public string Id { get; set; } = "";
        [JsonRequired]
        public string Description { get; set; } = "";
        [JsonRequired]
        public string Importance { get; set; } = "";
        [JsonRequired]
        public string Basis { get; set; } = "";
    }

    public sealed class UiGoalComponent
    {
        [JsonRequired]
        public string Criterion { get; set; } = "";
        [JsonRequired]
        public string Status { get; set; } = "";
        [JsonRequired]
        public string Evidence { get; set; } = "";
        [JsonRequired]
        public string Confidence { get; set; } = "";
    }

    public sealed class UiGoalExperiment
    {
        public string? Title { get; set; }
        public string? Prompt { get; set; }
        public List<UiGoalCandidateParent>? Parents { get; set; }
        public string Scope { get; set; } = "full";
        public string? ComponentGoal { get; set; }
        public List<string>? Sources { get; set; }
        public string? Quality { get; set; }
        public string? Detail { get; set; }
        [JsonRequired]
        public string Variant { get; set; } = "";
        [JsonRequired]
        public string Mode { get; set; } = "";
        [JsonRequired]
        public string Question { get; set; } = "";
        [JsonRequired]
        public string Expected { get; set; } = "";
        [JsonRequired]
        public List<string> Changes { get; set; } = new();
        [JsonRequired]
        public List<string> Holds { get; set; } = new();
        [JsonRequired]
        public List<string> DeferredCriteria { get; set; } = new();
        [JsonRequired]
        public string RestoreNext { get; set; } = "";
    }

    public sealed class UiGoalOption
    {
        [JsonRequired]
        public string Description { get; set; } = "";
        [JsonRequired]
        public string Reason { get; set; } = "";
    }

    public sealed class UiGoalSearchPlan
    {
        public string? AllocationReason { get; set; }
        [JsonRequired]
        public string Objective { get; set; } = "";
        [JsonRequired]
        public List<UiGoalOption> Options { get; set; } = new();
        [JsonRequired]
        public List<UiGoalExperiment> Candidates { get; set; } = new();
    }

    public sealed class UiGoalFindings
    {
        [JsonRequired]
        public string Observation { get; set; } = "";
        [JsonRequired]
        public string Inference { get; set; } = "";
        [JsonRequired]
        public string Uncertainty { get; set; } = "";
        [JsonRequired]
        public string CriticDisagreements { get; set; } = "";
        [JsonRequired]
        public string NextTest { get; set; } = "";
    }

    public sealed class UiGoalCompletion
    {
        [JsonRequired]
        public string Outcome { get; set; } = "";
        [JsonRequired]
        public List<int> EvidenceTurns { get; set; } = new();
        [JsonRequired]
        public List<string> RemainingGaps { get; set; } = new();
        [JsonRequired]
        public string Rationale { get; set; } = "";
    }

    public static class UiGoalLoopPursuit
    {
        // These prompts are versioned independently of the persisted v1-v6 conversations.
        public const string ManagerSystemPrompt = """
You manage an iterative image search toward the operator's full GOAL.
Return a concise decision record, not private deliberation or hidden thoughts.
Your job is to preserve the intended result, design useful experiments, inspect images, and decide what to test next.

Transport and budget:
Sources A, B, etc. are stable anonymous image generators. Every call is TEXT-TO-IMAGE with no image attachment.
The generator sees only the complete prompt. 'Refine' never means an image edit.
Each turn has two candidate slots, stored as 'refine' and 'fresh'. Each requested source renders its assigned slot once.
These are neutral identifiers, not mandatory strategies. Both slots can pursue, explore, simplify, rebuild, or verify.
Identical prompts are allowed when BOTH slots are explicitly 'verify': independent samples of one prompt.
No seed control or reproducibility is promised. Different sources are not repeated samples of one source.
Use candidate.sources to select a subset of the operator's sources; null uses all selected sources.
For a component study, set scope=component and a short componentGoal, such as 'table perspective and object placement'.
Candidate quality and detail can LOWER the operator's settings; null inherits them. Never exceed the operator's settings.
Quality choices are low/medium/high/xhigh/max; detail is standard/high/max (roughly 1K/2K/4K when supported).
Some providers ignore these controls. Lower settings are experiments, not guaranteed price or speed reductions.
Use simple text and minimal scene context for narrow studies. Select the same source whose behavior you need to test.
Test a component across several turns when useful, within the shared turn budget. Do not create unlimited child searches.
Then integrate it into the full scene at the operator's original settings. Recheck interactions, text, and composition there.
Calls consume time and may cost money. Never call them free.
The turn limit pauses work. It never proves success, failure, or a generator's limit.
Visual context contains the current turn's images and one earlier incumbent image, identified by exact turn, slot, and source.
Earlier images remain in the archive. Their written decision records stay in this conversation, but their pixels are not attached.
Do not claim to reinspect an archived image. Distinguish its earlier recorded assessment from current visual evidence.
Provider failures are execution results, not evidence that a particular prompt clause caused a failure.
Report a refusal as a refusal. Do not devise ways around provider restrictions.

Goal interpretation and stable rubric:
First define 3-8 criteria, each grounded in an EXACT short quote from the GOAL in 'basis'.
Distinguish required outcomes, preferences, and contextual inspiration using importance required/preference/context.
Do not turn a person's interests into a prop checklist unless the operator explicitly requires every one.
Do not invent personal details, an age, or a likeness beyond what the operator supplied.
Preserve emotional impact and overall coherence as explicit criteria when the goal calls for them.
Never replace 'most lovely' or another qualitative preference with a count of props, words, or details.
Repeat the EXACT rubric on later replies. Do not quietly lower standards or change criterion meanings.
Classify bounded or open-ended, but either can use comparative search. Only maximize a count explicitly requested by the operator.
There is no requirement to deliberately ruin a successful image or to claim a global optimum.

Pursuit and option exploration:
Make a small visible plan with 2-4 plausible options and the reason each is worth considering.
Each next candidate has a mode, one question, expected evidence, changed elements, and held elements.
Use a minimal core when the complete prompt prevents finding a strong style, composition, setting, or palette.
List temporarily omitted criteria in deferredCriteria and explain the restoration step in restoreNext.
Explore the core, choose an evidenced direction, then rebuild by adding coherent small groups of requirements.
Restore all required outcomes before declaring achieved. Do not mistake a successful subtask for the full goal.
Prefer one interpretable change when testing a cause. Broad alternatives are legitimate exploration, not causal tests.
Re-render a promising prompt to examine variability when a conclusion depends on one sample.
One or two examples do not establish a source's general reliability or a causal explanation of its behavior.
You may return to an earlier prompt by writing it in full and naming the earlier turn in your rationale.
Explain options using concise public rationales. Do not narrate private thought processes.
Default to clear daytime lighting unless the goal asks otherwise.

Evaluation:
Inspect every supplied image. For each criterion use met/partial/missing/uncertain, visible evidence, and confidence low/medium/high.
Judge against the full goal even during simplification. Separately state experimentAssessment for the narrow subtask.
Context is inspiration, not mandatory coverage. No image gets penalized just because every contextual interest is absent.
Retain score 0-10 only as a secondary rough summary for older displays. Never average component labels or infer success from score.
GoalMet requires every required criterion met and no unresolved material defect. A high preference cannot cancel a missing requirement.
For failures use score 0, goalMet false, uncertain components, and explain that no image exists.
Critics are separate clean instances. They see the same rubric and images but not your prompts, plan, conclusions, or each other.
Separate visible observation, tentative inference, and uncertainty. Critics can share mistakes; agreement is not proof.
Record disagreements, tradeoffs, and what evidence would settle them. Explain your selection with the component profile.
Choose bestTurn/bestVariant/bestSource from actual successful results, not just the highest scalar score.

Stopping:
Do not stop before reviewing at least two successful turns. This is a minimum comparison, not proof of optimality.
decision render supplies the next two prompts even at the turn limit.
decision done needs completion: outcome achieved or plateau, at least two distinct evidenceTurns, remainingGaps, rationale.
Achieved means an actual full-scope image at the operator's original settings meets the goal, with no deferred criteria or gaps.
remainingGaps lists unmet requirements or material defects; it must be [] for achieved.
Keep minor accepted tradeoffs, subjective alternatives, and optional improvements in findings.uncertainty instead.
Do not erase material gaps to satisfy a contract. Continue testing or declare plateau when material gaps remain.
Plateau means pause for the operator after tested options failed to resolve the remaining gaps. It is NOT success or a proven limit.
Do not declare plateau merely because the current budget is nearly used. Explain why the next test is not currently worth its cost.
Use plateau when tradeoffs require the operator's preference. Keep untested possibilities explicit.

JSON contract: reply with exactly one JSON object, no prose outside it.
Use these fields:
reasoning: concise decision rationale; goalKind: bounded/open-ended;
rubric: [{id, description, importance: required/preference/context, basis: exact goal quote}];
evaluations: null initially, then [{variant: refine/fresh, source: letter, score: 0-10, goalMet: boolean,
 assessment, problems: [string], keep: [string], experimentAssessment: string,
 components: [{criterion: rubric id, status: met/partial/missing/uncertain, evidence: string, confidence: low/medium/high}]}];
continueFrom: null initially, otherwise {variant, source} when rendering again, naming a result in the current review;
decision: render/done; prompt: complete first-slot prompt; freshPrompt: complete second-slot prompt;
designNotes, freshDesignNotes: brief explanations;
plan: required for render, null for done: {objective: string, options: [{description: string, reason: string}],
 candidates: [{variant, mode: pursue/explore/simplify/rebuild/verify, scope:full/component, componentGoal:string or null,
 sources:[source letters] or null, quality:string or null, detail:string or null, question, expected, changes:[string], holds:[string],
 deferredCriteria:[rubric ids], restoreNext:string}]}; exactly one candidate per slot;
findings: null initially, required after review: {observation, inference, uncertainty, criticDisagreements, nextTest};
completion: null when rendering, otherwise {outcome: achieved/plateau, evidenceTurns:[integers], remainingGaps:[string], rationale};
doneStatement: explanation when done;
bestTurn, bestVariant, bestSource: null initially, otherwise the exact successful image preferred overall.
All component arrays contain each rubric criterion exactly once. Keep each evidence statement brief and concrete.
Every option is an object with exactly description and reason strings. Include 2-4 options.
All fields named observation, inference, uncertainty, criticDisagreements, nextTest, assessment, experimentAssessment,
restoreNext, objective, question, expected, rationale, and reasoning contain one string, never an array or object.
Example options: [{"description":"Compare two table layouts","reason":"Isolates perspective before adding subjects"},
{"description":"Compare two palettes","reason":"Finds a coherent color direction before adding detail"}].
""";

        public const string CriticSystemPrompt = """
You independently evaluate image candidates against the operator's full GOAL and the supplied stable rubric.
You see images, the rubric, and neutral component task descriptions, not generation prompts, predictions, earlier scores, or other critics.
Treat the rubric as an interpretation to audit. Flag mistaken required/context classifications in rubricConcerns.
Judge visible results, not presumed intentions. Do not invent facts about the person or provider behavior.
Interests can inspire a scene without becoming mandatory props. Do not reward clutter as coverage.
For each rubric criterion report met/partial/missing/uncertain, concrete visible evidence, and confidence low/medium/high.
Keep required outcomes distinct from preferences and context. Context does not impose a coverage requirement.
Report experimentAssessment separately for the named component task. Component success is not full-goal success.
Separate technical defects from taste. A single number cannot express all tradeoffs.
Score 0-10 is only a secondary rough summary. Do not average criteria or treat a high score as proof.
GoalMet requires all required criteria met. For failed renders use score 0, goalMet false, and uncertain components.
Compare the strongest candidates in overall, explaining their tradeoffs and any uncertainty.
Suggest useful tests rather than unsupported causes. Never infer prompt wording, image-edit input, or safety workarounds.
Reply as one JSON object, without private deliberation:
{critiques:[{variant:refine/fresh,source:letter,score:number,goalMet:boolean,assessment:string,
 problems:[string],ideas:[string],experimentAssessment:string,components:[{criterion:rubric id,status:met/partial/missing/uncertain,evidence:string,
 confidence:low/medium/high}]}], overall:string, rubricConcerns:string}.
Include every listed render exactly once and every rubric criterion exactly once per render.
Use rubricConcerns='none' when the goal interpretation needs no correction.
""";

        public static List<UiGoalCriterion>? Rubric(IReadOnlyList<UiGoalLoopEntry> entries)
            => entries.FirstOrDefault(e => e.Kind == UiGoalLoopKinds.Design && e.Error == null)?.Manager?.Parsed?.Rubric;

        public static (UiGoalLoopEntry Request, UiGoalLoopSentImage Image)? IncumbentImage(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            var best = UiGoalLoopPlanner.BestReview(entries);
            if (best == null) return null;
            var request = entries.LastOrDefault(e => e.Kind == UiGoalLoopKinds.ReviewRequest && e.Turn == best.Value.Turn);
            var image = request?.Images?.SingleOrDefault(i => i.Variant == best.Value.Variant && i.Source == best.Value.Source);
            if (image == null) throw new InvalidDataException("the selected incumbent lacks its exact recorded image");
            return (request!, image);
        }

        // This declared protocol window is chosen before transport, not as recovery from an oversized request.
        public static Dictionary<int, List<UiGoalLoopSentImage>> VisualContext(IReadOnlyList<UiGoalLoopEntry> entries)
        {
            var selected = new Dictionary<int, List<UiGoalLoopSentImage>>();
            var current = entries.LastOrDefault(e => e.Kind == UiGoalLoopKinds.ReviewRequest);
            if (current != null) selected[current.Index] = current.Images ?? new();
            var incumbent = IncumbentImage(entries);
            if (incumbent != null && incumbent.Value.Request.Index != current?.Index)
                selected[incumbent.Value.Request.Index] = new() { incumbent.Value.Image };
            return selected;
        }

        public static string ContractRepairMessage(UiGoalLoopEntry rejected)
            => "The previous reply was rejected by the server contract: " + rejected.Manager!.ParseError
                + "\nReturn a corrected JSON decision for the same supplied images. No new images were generated. "
                + "Preserve the established rubric and evidence. Do not erase material gaps merely to pass validation. "
                + "remainingGaps means unmet requirements or material defects; achieved requires an empty list. "
                + "Record minor accepted tradeoffs and optional improvements in findings.uncertainty. "
                + "If material gaps remain, choose render or an evidenced plateau instead of achieved.";

        public static void ParseManager(JsonElement root, UiGoalLoopManagerReply reply, bool reviewing, int protocolVersion = 7)
        {
            reply.Rubric = Read<List<UiGoalCriterion>>(root, "rubric");
            ValidateRubric(reply.Rubric);
            if (reviewing)
            {
                reply.Findings = Read<UiGoalFindings>(root, "findings");
                Require(reply.Findings.Observation, reply.Findings.Inference, reply.Findings.Uncertainty,
                    reply.Findings.CriticDisagreements, reply.Findings.NextTest);
                foreach (var e in reply.Evaluations())
                {
                    ValidateComponents(e.Evaluation.Components, reply.Rubric, e.Evaluation.GoalMet);
                    Require(e.Evaluation.ExperimentAssessment);
                }
            }
            if (reply.Decision == "render")
            {
                reply.Plan = Read<UiGoalSearchPlan>(root, "plan");
                Require(reply.Plan.Objective);
                var fanout = protocolVersion >= 8;
                if (reply.Plan.Options == null || reply.Plan.Options.Count < (fanout ? 1 : 2) || reply.Plan.Options.Count > (fanout ? 6 : 4)
                    || reply.Plan.Options.Any(o => o == null))
                    throw new JsonException(fanout ? "plan options needs 1-6 description/reason objects" : "plan options needs 2-4 description/reason objects");
                foreach (var option in reply.Plan.Options) Require(option.Description, option.Reason);
                if (reply.Plan.Candidates == null || reply.Plan.Candidates.Count < (fanout ? 1 : 2)
                    || reply.Plan.Candidates.Count > (fanout ? UiGoalLoopFanout.MaxCandidatesCap : 2)
                    || reply.Plan.Candidates.Any(c => c == null))
                    throw new JsonException(fanout ? "plan needs 1-3 candidates" : "plan needs exactly two candidates");
                if (reply.Plan.Candidates.Select(c => c.Variant).Distinct().Count() != reply.Plan.Candidates.Count)
                    throw new JsonException("candidate identifiers must be unique within the round");
                foreach (var c in reply.Plan.Candidates)
                {
                    if (!UiGoalLoopVariants.IsValid(c.Variant, fanout ? 8 : protocolVersion)) throw new JsonException("unknown candidate identifier");
                    OneOf(c.Scope, "full", "component");
                    if (c.Scope == "component") Require(c.ComponentGoal);
                    if (c.Quality != null) OneOf(c.Quality, "low", "medium", "high", "xhigh", "max");
                    if (c.Detail != null) OneOf(c.Detail, "standard", "high", "max");
                    if (c.Sources != null)
                    {
                        Strings(c.Sources, 1, 8);
                        if (c.Sources.Distinct().Count() != c.Sources.Count || c.Sources.Any(s => !UiGoalLoopSources.IsValid(s)))
                            throw new JsonException("candidate sources must be distinct source letters");
                    }
                    OneOf(c.Mode, "pursue", "explore", "simplify", "rebuild", "verify");
                    Require(c.Question, c.Expected, c.RestoreNext);
                    Strings(c.Changes, 0, 12); Strings(c.Holds, 0, 12);
                    Strings(c.DeferredCriteria, 0, 8);
                    if (c.DeferredCriteria.Any(id => !reply.Rubric.Any(r => r.Id == id)))
                        throw new JsonException("deferredCriteria contains an unknown rubric id");
                }
                if (!fanout && reply.Prompt?.Trim() == reply.FreshPrompt?.Trim()
                    && reply.Plan.Candidates.Any(c => c.Mode != "verify"))
                    throw new JsonException("identical prompts require verify mode in both slots");
            }
            else
            {
                reply.Completion = Read<UiGoalCompletion>(root, "completion");
                OneOf(reply.Completion.Outcome, "achieved", "plateau");
                Require(reply.Completion.Rationale);
                Strings(reply.Completion.RemainingGaps, 0, 12);
                if (reply.Completion.EvidenceTurns == null || reply.Completion.EvidenceTurns.Count < 2 || reply.Completion.EvidenceTurns.Count > 30
                    || reply.Completion.EvidenceTurns.Distinct().Count() != reply.Completion.EvidenceTurns.Count)
                    throw new JsonException("completion needs at least two distinct evidence turns");
            }
        }

        public static List<UiGoalComponent>? ParseComponents(JsonElement item)
            => item.TryGetProperty("components", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.Deserialize<List<UiGoalComponent>>(UiGoalLoopJson.Options) : null;

        public static void ValidateComponents(List<UiGoalComponent>? components, List<UiGoalCriterion> rubric, bool goalMet)
        {
            if (components == null || components.Any(c => c == null) || components.Count != rubric.Count
                || components.Select(c => c.Criterion).Distinct().Count() != rubric.Count)
                throw new JsonException("components must contain each rubric criterion exactly once");
            foreach (var c in components)
            {
                if (!rubric.Any(r => r.Id == c.Criterion)) throw new JsonException("unknown component criterion");
                OneOf(c.Status, "met", "partial", "missing", "uncertain");
                OneOf(c.Confidence, "low", "medium", "high"); Require(c.Evidence);
            }
            if (goalMet && rubric.Where(r => r.Importance == "required")
                .Any(r => components.Single(c => c.Criterion == r.Id).Status != "met"))
                throw new JsonException("goalMet cannot hide a missing, partial, or uncertain requirement");
        }

        public static void ValidateCriticContext(UiGoalLoopCritiqueReply reply, IReadOnlyList<UiGoalLoopEntry> successfulResults)
        {
            foreach (var c in reply.Critiques)
                if (!successfulResults.Any(r => r.Render?.Variant == c.Variant && r.Render?.Source == c.Source)
                    && (c.Score != 0 || c.GoalMet || c.Components!.Any(component => component.Status != "uncertain")))
                    throw new InvalidDataException("pursuit contract: failed renders require zero score and uncertain critic components");
        }

        // Validate against durable render identities, not a model's claims about which images existed.
        public static void ValidateContext(UiGoalLoopManagerReply reply, IReadOnlyList<UiGoalLoopEntry> entries, int turn,
            UiGoalLoop? loop = null)
        {
            try
            {
                var rubric = Rubric(entries);
                if (reply.CandidateDecisions != null) UiGoalLoopFanout.ValidateContext(reply, entries, turn, loop);
                if (loop != null && reply.Plan != null)
                {
                    foreach (var plannedCandidate in reply.Plan.Candidates)
                    {
                        if (plannedCandidate.Sources?.Any(s => !loop.GeneratorList().Any(g => g.Source == s)) == true)
                            throw new JsonException("plannedCandidate selects a source outside this loop");
                        if (Rank(plannedCandidate.Quality, "low", "medium", "high", "xhigh", "max")
                            > Rank(loop.Quality, "low", "medium", "high", "xhigh", "max")
                            || Rank(plannedCandidate.Detail, "standard", "high", "max") > Rank(loop.Detail, "standard", "high", "max"))
                            throw new JsonException("experiments cannot exceed the operator's quality or detail settings");
                    }
                }
                if (rubric != null && JsonSerializer.Serialize(rubric, UiGoalLoopJson.Options)
                    != JsonSerializer.Serialize(reply.Rubric, UiGoalLoopJson.Options))
                    throw new JsonException("the established rubric must remain unchanged");
                if (rubric == null)
                {
                    var goal = entries.Single(e => e.Kind == UiGoalLoopKinds.Goal).Text;
                    if (reply.Rubric!.Any(r => !goal.Contains(r.Basis, StringComparison.Ordinal)))
                        throw new JsonException("each rubric basis must quote the goal exactly");
                    return;
                }
                var results = entries.Where(e => e.Kind == UiGoalLoopKinds.RenderResult).ToList();
                var chosen = results.SingleOrDefault(e => e.Turn == reply.BestTurn
                    && e.Render?.Variant == reply.BestVariant && e.Render?.Source == reply.BestSource);
                if (chosen?.Render?.Ok != true && results.Any(r => r.Render?.Ok == true))
                    throw new JsonException("best image must identify an actual successful render");
                foreach (var e in reply.Evaluations())
                {
                    var result = results.Single(r => r.Turn == turn && r.Render?.Variant == e.Variant && r.Render?.Source == e.Source);
                    if (result.Render?.Ok != true && (e.Evaluation.GoalMet || e.Evaluation.Score != 0
                        || e.Evaluation.Components!.Any(c => c.Status != "uncertain")))
                        throw new JsonException("failed renders require zero summary score and uncertain components");
                }
                if (chosen == null && (reply.BestTurn != null || reply.BestSource != null || reply.BestVariant != null || reply.Completion != null))
                    throw new JsonException("no successful image exists to select or declare complete");
                if (reply.Completion == null) return;
                var reviewed = entries.Where(e => e.Kind == UiGoalLoopKinds.Review && e.Error == null)
                    .Select(e => e.Turn).Append(turn).Distinct().ToHashSet();
                if (reply.Completion.EvidenceTurns.Any(t => !reviewed.Contains(t)
                    || !results.Any(r => r.Turn == t && r.Render?.Ok == true)))
                    throw new JsonException("completion evidence must name reviewed turns with successful images");
                if (reply.Completion.Outcome != "achieved") return;
                var evaluation = chosen!.Turn == turn ? reply.EvaluationOf(reply.BestVariant, reply.BestSource)
                    : entries.LastOrDefault(e => e.Kind == UiGoalLoopKinds.Review && e.Turn == chosen.Turn && e.Error == null)
                        ?.Manager?.Parsed?.EvaluationOf(reply.BestVariant, reply.BestSource);
                var design = entries.LastOrDefault(e => e.Index < chosen.Index && e.Manager?.Parsed?.Plan != null);
                var candidate = design?.Manager?.Parsed?.Plan?.Candidates.Single(c => c.Variant == UiGoalLoopSampling.CandidateOf(reply.BestVariant));
                if (evaluation?.GoalMet != true || candidate == null || candidate.Scope != "full" || candidate.DeferredCriteria.Count != 0
                    || (loop != null && (chosen.Render!.Quality != loop.Quality || chosen.Render.Detail != loop.Detail))
                    || reply.Completion.RemainingGaps.Count != 0)
                    throw new JsonException("achieved needs a full-goal image with no deferred criteria or remaining gaps");
            }
            catch (JsonException ex) { throw new InvalidDataException("pursuit contract: " + ex.Message); }
        }

        private static T Read<T>(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.Deserialize<T>(UiGoalLoopJson.Options) ?? throw new JsonException($"missing {name}")
                : throw new JsonException($"missing {name}");

        private static int Rank(string? value, params string[] options) => value == null ? -1 : Array.IndexOf(options, value);

        private static void ValidateRubric(List<UiGoalCriterion> rubric)
        {
            if (rubric.Any(r => r == null) || rubric.Count < 3 || rubric.Count > 8 || rubric.Select(r => r.Id).Distinct().Count() != rubric.Count)
                throw new JsonException("rubric needs 3-8 unique criteria");
            foreach (var r in rubric)
            {
                Require(r.Id, r.Description, r.Basis);
                OneOf(r.Importance, "required", "preference", "context");
            }
            if (!rubric.Any(r => r.Importance == "required")) throw new JsonException("rubric needs a required outcome");
        }

        private static void Require(params string?[] values)
        {
            if (values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 2000))
                throw new JsonException("pursuit text fields must contain 1-2000 characters");
        }
        private static void OneOf(string value, params string[] choices)
        {
            if (!choices.Contains(value)) throw new JsonException($"invalid pursuit value '{value}'");
        }
        private static void Strings(List<string>? values, int min, int max)
        {
            if (values == null || values.Count < min || values.Count > max) throw new JsonException($"expected {min}-{max} items");
            foreach (var value in values) Require(value);
        }
    }
}
