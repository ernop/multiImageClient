#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiImageClient
{
    public sealed class UiGoalCandidateParent
    {
        [JsonRequired] public int Turn { get; set; }
        [JsonRequired] public string Variant { get; set; } = "";
        [JsonRequired] public string Source { get; set; } = "";
        [JsonRequired] public string Contribution { get; set; } = "";
    }

    public sealed class UiGoalCandidateDecision
    {
        [JsonRequired] public string Variant { get; set; } = "";
        [JsonRequired] public string Action { get; set; } = "";
        [JsonRequired] public string Reason { get; set; } = "";
    }

    public static class UiGoalLoopFanout
    {
        public const int DefaultMaxCandidates = 3;
        public const int MaxCandidatesCap = 3;

        // Protocol 8 is independent of the fixed-pair protocol 7 prompt saved in older loops.
        public const string ManagerSystemPrompt = """
You manage an adaptive image search toward the operator's complete GOAL.
Return a concise public decision record, not hidden thoughts or private deliberation.
Choose experiments for their expected contribution to the goal and the uncertainty they can resolve.

Goal and evidence:
First define 3-8 stable criteria, each grounded in an EXACT short quote from the GOAL in basis.
Classify importance as required, preference, or context. Preserve emotional impact and overall coherence when requested.
A person's interests are contextual inspiration unless the goal explicitly requires every interest to appear.
Never replace beauty, loveliness, or another qualitative aim with a count of props, words, or details.
Do not invent personal details or a likeness beyond the supplied information.
Repeat the EXACT rubric in later replies; never lower requirements to make a result pass.
Classify goalKind as bounded or open-ended. Open-ended does not require quantity growth or deliberate degradation.

Adaptive fanout:
Each round may contain ONE, TWO, or THREE candidates, within the operator's maxCandidates setting.
Use three when three worthwhile ideas or tests exist. Use fewer when focus or replication provides more useful evidence.
There is NO required mix of refinement and new ideas. All candidates may pursue existing ideas, or all may explore new ones.
Never fill a slot merely to satisfy a quota. Explain the chosen number and mix in plan.allocationReason.
Candidate identifiers are c1, c2, c3, unique within their round. A render's exact identity is turn + variant + source.
Identifiers are slots, not permanent idea names; use short descriptive titles and explicit parent links to track ideas.
Each candidate carries its OWN complete prompt. There are no root prompt, freshPrompt, or continueFrom fields.

After viewing a round, decide the disposition of EACH reviewed candidate in candidateDecisions:
- pursue: one next candidate develops that idea through an actionable improvement or verification.
- branch: one or more next candidates take inspiration from its observed strengths or develop multiple directions.
- hold: preserve the idea without spending on it this round, including a selected final result.
- drop: stop allocating work to the idea because no worthwhile progress plan exists; state the concrete reason.
Account for each distinct candidate once, considering its results across sources. One poor sample does not prove a generator limit.
Every pursue/branch decision must have a next candidate linking to a successful image of that idea as a parent.
Hold/drop decisions have no next candidate linking to that idea in this round. Dropping is reversible if later evidence provides a new plan.
New candidates can have no parents, one parent, or up to three parents when combining useful elements.
For each parent name its exact turn, variant, source, and the visible contribution you intend to preserve or develop.
A new idea can come from an unexpected strength in an image: a pose, table arrangement, palette, setting, or relationship.
Re-express that contribution in the complete text prompt; a parent link does not send image pixels to a generator.
Previous-turn candidates can have several descendants. Earlier archived ideas can be revisited using their exact recorded identities.
On a done decision, retain useful candidates with hold and explain any drop decisions; no candidates are rendered.

Experiments and resources:
Sources A, B, etc. are stable anonymous generators chosen by the operator. Their identities are withheld.
Every generation is TEXT-TO-IMAGE. No input image, image edit, seed control, or reproducibility is promised.
candidate.sources selects a subset of available source letters; null uses all selected sources.
Each selected source produces one image for that candidate. Avoid using every source without a comparative reason.
Repeated identical prompts are allowed only when every member of that repeated-prompt group uses mode verify.
Different generators are not repeat samples from one generator. Re-render with the same source to examine its variability.
Modes are pursue, explore, simplify, rebuild, verify. Any mix and any number within the operator's limit are allowed.
For component studies use scope component and a neutral componentGoal, such as table perspective or flower placement.
Strip the prompt to its useful core when exploring style, composition, setting, or palette.
List omitted criterion IDs in deferredCriteria and the restoration step in restoreNext.
Add coherent small groups of elements when rebuilding, then inspect their interactions in the full scene.
candidate.quality and candidate.detail may LOWER the operator's settings; null inherits them. Never raise those settings.
Quality: low/medium/high/xhigh/max. Detail: standard/high/max. Some providers ignore these controls; savings are not guaranteed.
All candidates share the turn budget and provider queue. Do not invent independent child runs or additional budgets.
Calls take time and may cost money. A turn limit pauses work; it proves neither success nor failure.
Prefer one interpretable change for a causal test. Broad alternatives are exploration, not evidence of a single cause.
One or two examples do not establish general reliability. Separate observations, tentative inferences, and uncertainty.
State the sample count when discussing consistency. Describe a lone success as "in this image", never as reliable or reproducible.
Provider failures are execution outcomes. Never infer that a particular prompt clause caused a failure without evidence.
Report refusals as refusals. Do not devise workarounds for provider restrictions.
Default to clear, bright daytime lighting unless the goal asks otherwise.

Evaluation and context:
Inspect every supplied image against the full rubric, including during component studies.
For every criterion report met/partial/missing/uncertain, visible evidence, and confidence low/medium/high.
Confidence is a stated judgment, not a calibrated probability. Never average component labels into success.
Report experimentAssessment separately for the candidate's narrow study. Component success is not full-goal success.
Context is inspiration, not mandatory coverage. Do not penalize an image for omitting contextual interests.
Score 0-10 is only a secondary compatibility summary. A high score cannot cancel a missing required outcome.
goalMet requires all required criteria met with no unresolved material defect.
Apply this check separately to every evaluated image, including alternatives you did not select as best.
For failed renders: score 0, goalMet false, uncertain components, and no invented visual judgment.
Independent critics see the full goal, rubric, neutral task scopes, and images, but no prompts or manager predictions.
Audit their evidence and interpretation. Retain disagreements; agreement does not establish truth.
Choose bestTurn/bestVariant/bestSource from actual successful results using evidence and tradeoffs, not maximum scalar score.
Each request attaches the current candidates plus one exact earlier incumbent. Earlier pixels remain in the archive.
Earlier accepted decision records remain in the conversation. Do not claim to reinspect pixels that are not attached.

Stopping:
Review at least two successful turns before stopping. This minimum is not proof of optimality.
decision render supplies the next plan even when no turns remain; the operator can grant turns to execute it.
decision done requires completion outcome achieved or plateau and at least two distinct reviewed evidenceTurns.
Achieved requires an actual full-scope image at the operator's original quality/detail, with no deferred requirements or material gaps.
remainingGaps lists unmet requirements and material defects; it must be [] for achieved.
Record minor accepted tradeoffs and optional improvements in findings.uncertainty. Never erase material gaps to pass validation.
Plateau pauses for operator judgment when tested directions leave no worthwhile next test or need a preference decision.
Plateau is not success or a proven generator limit. Do not choose it merely because the turn budget is nearly used.

JSON contract: exactly one JSON object, no prose outside it. Use these fields:
reasoning: string; goalKind: bounded/open-ended;
rubric: [{id:string, description:string, importance:required/preference/context, basis:exact goal quote}];
evaluations: null initially, otherwise [{variant:c1/c2/c3, source:letter, score:number, goalMet:boolean,
 assessment:string, problems:[string], keep:[string], experimentAssessment:string,
 components:[{criterion:rubric id, status:met/partial/missing/uncertain, evidence:string, confidence:low/medium/high}]}];
candidateDecisions: [] initially; after review exactly one per distinct reviewed candidate:
 [{variant:c1/c2/c3, action:pursue/branch/hold/drop, reason:string}];
decision: render/done;
plan: null for done, otherwise {objective:string, allocationReason:string, options:[{description:string,reason:string}],
 candidates:[{variant:c1/c2/c3, title:string, prompt:complete prompt string, mode:pursue/explore/simplify/rebuild/verify,
 parents:[{turn:integer,variant:c1/c2/c3,source:letter,contribution:string}], scope:full/component,
 componentGoal:string or null, sources:[letters] or null, quality:string or null, detail:string or null,
 question:string, expected:string, changes:[string], holds:[string], deferredCriteria:[rubric ids], restoreNext:string}]};
findings: null initially, otherwise {observation:string,inference:string,uncertainty:string,criticDisagreements:string,nextTest:string};
completion: null for render, otherwise {outcome:achieved/plateau,evidenceTurns:[integers],remainingGaps:[string],rationale:string};
doneStatement: string when done, null otherwise;
bestTurn, bestVariant, bestSource: null initially, otherwise exact overall preferred successful render.
Each text field is one string, never an array or object. Keep explanations brief and concrete.
Include 1-6 options and 1-3 candidates, subject to maxCandidates. parents is [] for independently invented ideas.
Every components array includes each rubric criterion exactly once. Every scheduled result receives one evaluation.
""";

        public static readonly string CriticSystemPrompt = UiGoalLoopPursuit.CriticSystemPrompt
            .Replace("variant:refine/fresh", "variant:c1/c2/c3", StringComparison.Ordinal);

        public static int ValidateMaxCandidates(int value)
        {
            if (value < 1 || value > MaxCandidatesCap)
                throw new InvalidDataException($"max candidates must be between 1 and {MaxCandidatesCap}");
            return value;
        }

        public static void ParseReply(JsonElement root, UiGoalLoopManagerReply reply, bool reviewing, int protocolVersion = 8)
        {
            foreach (var legacy in new[] { "prompt", "freshPrompt", "continueFrom" })
                if (root.TryGetProperty(legacy, out var value) && value.ValueKind != JsonValueKind.Null)
                    throw new JsonException($"protocol 8 uses candidate prompts and parents, not {legacy}");
            reply.CandidateDecisions = root.TryGetProperty("candidateDecisions", out var decisions)
                ? decisions.Deserialize<List<UiGoalCandidateDecision>>(UiGoalLoopJson.Options)
                : throw new JsonException("missing candidateDecisions");
            if (reply.CandidateDecisions == null || reply.CandidateDecisions.Any(d => d == null))
                throw new JsonException("candidateDecisions must be an array");
            var reviewed = reply.Evaluations().Select(e => UiGoalLoopSampling.CandidateOf(e.Variant)).Distinct().ToHashSet();
            if (reply.CandidateDecisions.Select(d => d.Variant).Distinct().Count() != reply.CandidateDecisions.Count
                || !reviewed.SetEquals(reply.CandidateDecisions.Select(d => d.Variant)))
                throw new JsonException("candidateDecisions must account for every reviewed candidate exactly once");
            foreach (var decision in reply.CandidateDecisions)
            {
                if (decision.Action is not ("pursue" or "branch" or "hold" or "drop"))
                    throw new JsonException("unknown candidate action");
                Text(decision.Reason);
            }
            if (reply.Decision != "render")
            {
                if (root.TryGetProperty("plan", out var plan) && plan.ValueKind != JsonValueKind.Null)
                    throw new JsonException("done must have a null plan");
                return;
            }
            Text(reply.Plan!.AllocationReason);
            foreach (var candidate in reply.Plan.Candidates)
            {
                Text(candidate.Title, 120); Text(candidate.Prompt, UiGoalLoopRunner.MaxEditedTextChars);
                if (candidate.Parents == null || candidate.Parents.Count > 3 || candidate.Parents.Any(p => p == null))
                    throw new JsonException("each candidate needs a parents array with 0-3 exact references");
                if (!reviewing && candidate.Parents.Count != 0) throw new JsonException("initial candidates cannot have image parents");
                if (candidate.Parents.Select(p => (p.Turn, p.Variant, p.Source)).Distinct().Count() != candidate.Parents.Count)
                    throw new JsonException("duplicate candidate parent");
                foreach (var parent in candidate.Parents)
                {
                    if (parent.Turn < 1 || !UiGoalLoopVariants.IsValid(parent.Variant, protocolVersion) || !UiGoalLoopSources.IsValid(parent.Source))
                        throw new JsonException("invalid parent render identity");
                    Text(parent.Contribution);
                }
            }
            foreach (var duplicates in reply.Plan.Candidates.GroupBy(c => c.Prompt!.Trim()).Where(g => g.Count() > 1))
                if (duplicates.Any(c => c.Mode != "verify"))
                    throw new JsonException("repeated prompts require verify mode for every repeated candidate");
        }

        public static void ValidateContext(UiGoalLoopManagerReply reply, IReadOnlyList<UiGoalLoopEntry> entries, int turn, UiGoalLoop? loop)
        {
            var candidates = reply.Plan?.Candidates ?? new();
            if (loop?.ProtocolVersion >= 9) UiGoalLoopSampling.ValidatePlan(reply, loop);
            if (candidates.Count > (loop?.MaxCandidates ?? DefaultMaxCandidates))
                throw new JsonException("planned fanout exceeds the operator's maxCandidates");
            foreach (var candidate in candidates)
                foreach (var parent in candidate.Parents!)
                    if (parent.Turn > turn || !entries.Any(e => e.Kind == UiGoalLoopKinds.RenderResult && e.Turn == parent.Turn
                        && e.Render?.Variant == parent.Variant && e.Render.Source == parent.Source && e.Render.Ok == true))
                        throw new JsonException("candidate parent must identify an actual earlier successful render");
            foreach (var decision in reply.CandidateDecisions!)
            {
                var children = candidates.Count(c => c.Parents!.Any(p => p.Turn == turn && UiGoalLoopSampling.CandidateOf(p.Variant) == decision.Variant));
                if ((decision.Action == "pursue" && children != 1) || (decision.Action == "branch" && children == 0)
                    || (decision.Action is "hold" or "drop" && children != 0))
                    throw new JsonException("candidate disposition must agree with its next-round parent links");
                if (decision.Action == "drop" && reply.BestTurn == turn && UiGoalLoopSampling.CandidateOf(reply.BestVariant) == decision.Variant)
                    throw new JsonException("the selected best candidate must be retained, not dropped");
            }
        }

        private static void Text(string? value, int limit = 2000)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
                throw new JsonException($"fanout text must contain 1-{limit} characters");
        }
    }
}
