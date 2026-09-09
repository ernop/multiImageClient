using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GoalLoopFanoutTests
    {
        private const string Goal = "A lovely puppy surprise with kind eyes. She likes piano.";
        private static readonly UiGoalLoopGenerator[] Generators = { new() { Key = "one", Source = "A" } };
        private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, UiGoalLoopJson.Options)!;
        private static UiGoalLoopRenderKey[] Shown(int count) => Enumerable.Range(1, count).Select(n => new UiGoalLoopRenderKey($"c{n}", "A")).ToArray();

        internal static JsonObject Payload(int count, bool review = false)
        {
            var root = GoalLoopPursuitTests.Payload(review);
            root.Remove("prompt"); root.Remove("freshPrompt"); root.Remove("continueFrom");
            root["plan"]!["allocationReason"] = "Test the independent ideas that have concrete next steps.";
            var template = root["plan"]!["candidates"]![0]!.DeepClone();
            var candidates = new JsonArray();
            for (var n = 1; n <= count; n++)
            {
                var candidate = template.DeepClone();
                candidate["variant"] = $"c{n}"; candidate["title"] = $"Idea {n}";
                candidate["prompt"] = $"Puppy surprise composition {n}.";
                candidate["parents"] = new JsonArray();
                candidates.Add(candidate);
            }
            root["plan"]!["candidates"] = candidates;
            root["candidateDecisions"] = new JsonArray();
            if (review)
            {
                var row = root["evaluations"]![0]!.DeepClone();
                var evaluations = new JsonArray();
                foreach (var key in Shown(3))
                {
                    var evaluation = row.DeepClone(); evaluation["variant"] = key.Variant; evaluations.Add(evaluation);
                }
                root["evaluations"] = evaluations;
                root["bestVariant"] = "c1";
                root["candidateDecisions"] = Node(Shown(3).Select(k => new UiGoalCandidateDecision
                    { Variant = k.Variant!, Action = "hold", Reason = "Keep this direction in the archive." }));
            }
            return root;
        }

        private static UiGoalLoopManagerReply Parse(JsonObject root, bool review = false)
            => UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), review, 8, shownRenders: review ? Shown(3) : null);

        private static List<UiGoalLoopEntry> History()
        {
            var entries = new List<UiGoalLoopEntry>
            {
                new() { Kind = "goal", Text = Goal, Index = 0 },
                new() { Kind = "design", Turn = 1, Index = 1, Manager = new() { Parsed = Parse(Payload(3)) } },
            };
            foreach (var key in Shown(3)) entries.Add(new() { Kind = "render-result", Turn = 1, Index = entries.Count,
                Render = new() { Variant = key.Variant, Source = key.Source, Ok = true } });
            return entries;
        }

        private static void Parent(JsonObject root, int candidate, string variant, int turn = 1, string source = "A")
            => root["plan"]!["candidates"]![candidate]!["parents"] = Node(new[] {
                new UiGoalCandidateParent { Turn = turn, Variant = variant, Source = source, Contribution = "Preserve the observed table perspective." } });

        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)]
        public void VariableFanoutSchedulesEveryIndependentPrompt(int count)
        {
            var reply = Parse(Payload(count));
            var entries = new List<UiGoalLoopEntry> { new() { Kind = "goal", Text = Goal } };
            UiGoalLoopPursuit.ValidateContext(reply, entries, 1, new() { Generators = Generators.ToList() });
            entries.Add(new() { Kind = "design", Turn = 1, Manager = new() { Parsed = reply } });
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 8, Generators);
            Assert.Equal(count, step.Renders.Count);
            Assert.Equal(Enumerable.Range(1, count).Select(n => $"Puppy surprise composition {n}."), step.Renders.Select(r => r.Prompt));
            Assert.Equal(Shown(count).Select(k => k.Variant), step.Renders.Select(r => r.Variant));
        }

        [Fact]
        public void BranchingCanProduceTwoChildrenAndAnIndependentIdeaWhileDroppingAnother()
        {
            var root = Payload(3, true);
            Parent(root, 0, "c1"); Parent(root, 1, "c1");
            root["candidateDecisions"]![0]!["action"] = "branch";
            root["candidateDecisions"]![1]!["action"] = "drop";
            var reply = Parse(root, true);
            UiGoalLoopPursuit.ValidateContext(reply, History(), 1);
            Assert.Empty(reply.Plan!.Candidates[2].Parents!);
            Assert.Equal("drop", reply.CandidateDecisions![1].Action);
            var cloned = new UiGoalLoopEntry { Manager = new() { Parsed = reply } }.Clone();
            Assert.Equal("c1", cloned.Manager!.Parsed!.Plan!.Candidates[1].Parents!.Single().Variant);
        }

        [Fact]
        public void ArchivedParentsCanBeRevisitedOrCombinedAcrossSources()
        {
            var root = Payload(1, true);
            Parent(root, 0, "c2");
            root["plan"]!["candidates"]![0]!["parents"]!.AsArray().Add(Node(new UiGoalCandidateParent
                { Turn = 1, Variant = "c1", Source = "B", Contribution = "Reuse the warm palette." }));
            var history = History();
            history.Add(new() { Kind = "render-result", Turn = 1, Index = history.Count,
                Render = new() { Variant = "c1", Source = "B", Ok = true } });
            foreach (var key in Shown(3)) history.Add(new() { Kind = "render-result", Turn = 2, Index = history.Count,
                Render = new() { Variant = key.Variant, Source = key.Source, Ok = true } });
            // Current ideas are held; the next candidate returns to two turn-one images.
            UiGoalLoopPursuit.ValidateContext(Parse(root, true), history, 2);
        }

        [Fact]
        public void ThreeCandidatesCanScheduleEightSourcesEach()
        {
            var root = Payload(3);
            foreach (var candidate in root["plan"]!["candidates"]!.AsArray()) candidate!["sources"] = null;
            var generators = Enumerable.Range(0, 8).Select(i => new UiGoalLoopGenerator
                { Key = $"generator-{i}", Source = ((char)('A' + i)).ToString() }).ToArray();
            var entries = new[] { new UiGoalLoopEntry { Kind = "design", Turn = 1, Manager = new() { Parsed = Parse(root) } } };
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 8, generators);
            Assert.Equal(24, step.Renders.Count);
            Assert.Equal(24, step.Renders.Select(r => (r.Variant, r.Source)).Distinct().Count());
        }

        [Fact]
        public void AllCandidatesMayPursueWithoutMandatoryNovelty()
        {
            var root = Payload(3, true);
            for (var i = 0; i < 3; i++)
            {
                Parent(root, i, $"c{i + 1}");
                root["plan"]!["candidates"]![i]!["mode"] = "pursue";
                root["candidateDecisions"]![i]!["action"] = "pursue";
            }
            UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 1);
        }

        [Fact]
        public void CandidateDispositionMustMatchActualLineage()
        {
            var root = Payload(1, true); Parent(root, 0, "c2");
            root["candidateDecisions"]![1]!["action"] = "drop";
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 1));
            root["candidateDecisions"]![1]!["action"] = "pursue";
            UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 1);
            root["candidateDecisions"]!.AsArray().RemoveAt(2);
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
        }

        [Fact]
        public void UnknownFutureOrFailedParentsAreRejected()
        {
            foreach (var parent in new[] { (2, "A"), (1, "B") })
            {
                var root = Payload(1, true); Parent(root, 0, "c1", parent.Item1, parent.Item2);
                root["candidateDecisions"]![0]!["action"] = "pursue";
                Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 1));
            }
            var failed = Payload(1, true); Parent(failed, 0, "c2");
            failed["candidateDecisions"]![1]!["action"] = "pursue";
            var history = History(); history[3].Render!.Ok = false;
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(failed, true), history, 1));
        }

        [Fact]
        public void OperatorLimitAndPromptContractAreEnforced()
        {
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(Payload(3)),
                new[] { new UiGoalLoopEntry { Kind = "goal", Text = Goal } }, 1, new() { MaxCandidates = 2 }));
            Assert.Throws<InvalidDataException>(() => Parse(Payload(0)));
            Assert.Throws<InvalidDataException>(() => Parse(Payload(4)));
            var root = Payload(1); root["prompt"] = "Legacy prompt carrier";
            Assert.Throws<InvalidDataException>(() => Parse(root));
            root = Payload(3); root["plan"]!["candidates"]![2]!["variant"] = "c1";
            Assert.Throws<InvalidDataException>(() => Parse(root));
        }

        [Fact]
        public void CompletionCannotCarryAnIgnoredRenderPlan()
        {
            var root = Payload(1, true);
            root["decision"] = "done"; root["doneStatement"] = "Pause for preference review.";
            root["completion"] = Node(new { outcome = "plateau", evidenceTurns = new[] { 1, 2 },
                remainingGaps = new[] { "No resolved next direction." }, rationale = "The compared directions need an operator preference." });
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
            root["plan"] = null;
            Assert.Equal("done", Parse(root, true).Decision);
        }

        [Fact]
        public void VerificationPairsCanCoexistWithAnExploratoryThirdCandidate()
        {
            var root = Payload(3);
            root["plan"]!["candidates"]![1]!["prompt"] = "Puppy surprise composition 1.";
            Assert.Throws<InvalidDataException>(() => Parse(root));
            root["plan"]!["candidates"]![0]!["mode"] = "verify";
            root["plan"]!["candidates"]![1]!["mode"] = "verify";
            Assert.Equal("explore", Parse(root).Plan!.Candidates[2].Mode);
        }

        [Fact]
        public void ThirdCandidateHasExactCriticIdentityAndRequestOrder()
        {
            var root = Payload(3, true);
            var critique = new JsonObject { ["critiques"] = root["evaluations"]!.DeepClone(),
                ["overall"] = "Compare the layouts.", ["rubricConcerns"] = "none" };
            var reply = UiGoalLoopProtocol.ParseCritiqueReply(critique.ToJsonString(), Shown(3), Parse(Payload(3)).Rubric, 8);
            Assert.Equal("c3", reply.Critiques.Last().Variant);
            Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseCritiqueReply(critique.ToJsonString(), Shown(3), null, 7));
            var targets = Shown(3).Reverse().Select(k => new UiGoalLoopProtocol.UiGoalLoopCritiqueTarget(k.Variant!, k.Source!, true, "512x512", null)).ToList();
            var text = UiGoalLoopProtocol.BuildCritiqueRequestText(Goal, null, 1, targets);
            Assert.Contains("C3 render, source A: attached image #3", text);
        }

        [Fact]
        public void ResumeRetainsAllPendingCandidatesAndTheirSettings()
        {
            var entries = History().Take(2).ToList();
            foreach (var key in Shown(3)) entries.Add(new() { Kind = "render-request", Turn = 1, Index = entries.Count,
                Text = $"Exact prompt {key.Variant}", Render = new() { Variant = key.Variant, Source = key.Source,
                    GeneratorKey = "one", Quality = "low", Detail = "standard" } });
            entries.Add(new() { Kind = "render-result", Turn = 1, Index = entries.Count,
                Render = new() { Variant = "c1", Source = "A", GeneratorKey = "one", Ok = true } });
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 8, Generators);
            Assert.Equal(new[] { "c2", "c3" }, step.Renders.Select(r => r.Variant));
            Assert.All(step.Renders, r => Assert.Equal("low", r.Quality));
        }
    }
}
