#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiImageClient
{
    public static class UiGoalLoopSampling
    {
        public const int DefaultPolicy = 0;
        public const int MaxImagesPerRound = 24;
        public static bool IsSampleVariant(string? value) => value is { Length: 5 }
            && value[0] == 'c' && value[1] is >= '1' and <= '3' && value[2] == '-'
            && value[3] == 's' && value[4] is >= '1' and <= '4';
        public static string? CandidateOf(string? variant) => IsSampleVariant(variant) ? variant![..2] : variant;
        public static int Count(int policy, string generator)
        {
            if (policy is not (0 or 1 or 2 or 4)) throw new InvalidDataException("sample policy must be auto, 1, 2, or 4");
            return policy == 0 ? generator == "grok-web" ? 2 : 1 : policy;
        }
        public static string PolicyText(UiGoalLoop loop) => "\n\nSAMPLES PER CANDIDATE: "
            + string.Join(", ", loop.GeneratorList().Select(g => $"source {g.Source}: {Count(loop.SamplesPerPrompt, g.Key)}"))
            + $". At most {MaxImagesPerRound} images may be scheduled in a round; select source subsets when necessary.";
        public static void ValidatePlan(UiGoalLoopManagerReply reply, UiGoalLoop loop)
        {
            var count = reply.Plan?.Candidates.Sum(c => loop.GeneratorList()
                .Where(g => c.Sources == null || c.Sources.Contains(g.Source!)).Sum(g => Count(loop.SamplesPerPrompt, g.Key))) ?? 0;
            if (count > MaxImagesPerRound)
                throw new InvalidDataException($"sampling plan exceeds {MaxImagesPerRound} images; select fewer sources or candidates");
        }
        public static readonly string ManagerSystemPrompt = UiGoalLoopFanout.ManagerSystemPrompt
            .Replace("Each selected source produces one image for that candidate.",
                "Each selected source produces the configured number of independent samples for that candidate.")
            .Replace("evaluations: null initially, otherwise [{variant:c1/c2/c3,", "evaluations: null initially, otherwise [{variant:exact sample id such as c1-s1,")
            .Replace("parents:[{turn:integer,variant:c1/c2/c3,", "parents:[{turn:integer,variant:exact sample id such as c1-s2,")
            + """

Protocol 9 sample identities:
Candidate plans and candidateDecisions still use c1, c2, c3.
Every rendered sample uses variant c1-s1, c1-s2, etc. The sample suffix starts at s1, even for a single sample.
Evaluations, parent references, and bestVariant MUST use the exact sample identity shown by the server.
Turn + sample variant + source identifies one actual image. Never substitute a sibling sample.
Evaluate every sample separately. Candidate dispositions consider all its samples across sources.
The operator chooses sample counts; do not add a sample-count field or encode sample suffixes in plan candidate identifiers.
The source policy states sample counts. Stay within 24 total images per round by selecting source subsets when necessary.
Repeated samples within one candidate need no verify mode; deliberate repeated candidate prompts still do.
These are independent generation requests, not seed-controlled replications or an assurance of generator consistency.

Operator points:
The server supplies a current feedback snapshot with separate image points and prompt points.
These are the owner's preference signals, not critic scores or proof that requirements are satisfied.
Repeated +1/-1 clicks adjust a target's cumulative points. Zero means neutral, not an implicit rejection.
Use positive feedback to reconsider useful earlier images or prompts; use negative feedback to reconsider spending on their directions.
Do not silently transfer an image vote to its prompt, source, siblings, or descendants.
Prompt feedback expresses preference for that exact prompt; image feedback expresses preference for that exact image.
Explain how material feedback affected the next selection, including any conflict with required criteria.
Votes can arrive during your call. Your snapshot is fixed for this call; later calls receive updated totals.
Feedback never erases requirements, changes critics' independent judgments, or claims that an absent image is visible.
""";
        public static readonly string CriticSystemPrompt = UiGoalLoopFanout.CriticSystemPrompt
            .Replace("variant:c1/c2/c3", "variant:exact sample id such as c1-s1")
            + "\nEvery image has an exact sample identity such as c1-s1 or c1-s2, including s1 for single-sample candidates. Evaluate every shown sample separately.";
    }
}
