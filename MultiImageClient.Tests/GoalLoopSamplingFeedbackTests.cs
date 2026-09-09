using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GoalLoopSamplingFeedbackTests
    {
        private static readonly UiGoalLoopGenerator[] Sources = { new() { Key = "grok-web", Source = "A" }, new() { Key = "other", Source = "B" } };
        private static UiGoalLoopManagerReply Design() => UiGoalLoopProtocol.ParseManagerReply(GoalLoopFanoutTests.Payload(3).ToJsonString(), false, 9);
        private static List<UiGoalLoopEntry> History() => new()
        {
            new() { Index = 0, Kind = "goal", Text = "A lovely puppy surprise with kind eyes. She likes piano." },
            new() { Index = 1, Kind = "design", Turn = 1, Manager = new() { Parsed = Design(), FeedbackThroughEntry = -1 } },
            new() { Index = 2, Kind = "render-request", Turn = 1, Text = "Exact puppy prompt.", Render = new() { JobId = "a", GeneratorKey = "grok-web", Variant = "c1-s1", Source = "A" } },
            new() { Index = 3, Kind = "render-result", Turn = 1, Render = new() { JobId = "a", GeneratorKey = "grok-web", Variant = "c1-s1", Source = "A", Ok = true } },
            new() { Index = 4, Kind = "render-request", Turn = 1, Text = "Exact puppy prompt.", Render = new() { JobId = "b", GeneratorKey = "grok-web", Variant = "c1-s2", Source = "A" } },
            new() { Index = 5, Kind = "render-result", Turn = 1, Render = new() { JobId = "b", GeneratorKey = "grok-web", Variant = "c1-s2", Source = "A", Ok = true } },
        };
        private static UiGoalFeedback Vote(List<UiGoalLoopEntry> entries, string scope, int target, int delta, string? id = null)
        {
            var f = UiGoalLoopFeedback.Create(entries, id ?? Guid.NewGuid().ToString(), scope, target, delta, "owner");
            entries.Add(new() { Index = entries.Count, Turn = 1, Kind = UiGoalLoopFeedback.Kind, Feedback = f });
            return f;
        }
        [Fact]
        public void AutoSamplesGrokTwiceAndOthersOnceWithDistinctIdentities()
        {
            var root = GoalLoopFanoutTests.Payload(3);
            foreach (var c in root["plan"]!["candidates"]!.AsArray()) c!["sources"] = null;
            var entries = new[] { new UiGoalLoopEntry { Kind = "design", Turn = 1, Manager = new() { Parsed = UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), false, 9) } } };
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 9, Sources);
            Assert.Equal(9, step.Renders.Count);
            Assert.Equal(6, step.Renders.Count(r => r.Source == "A"));
            Assert.Equal(3, step.Renders.Count(r => r.Source == "B"));
            Assert.Contains(step.Renders, r => r.Variant == "c3-s2" && r.Source == "A");
            Assert.DoesNotContain(step.Renders, r => r.Variant == "c3-s2" && r.Source == "B");
            var one = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 9, Sources, samplesPerPrompt: 1);
            Assert.Equal(6, one.Renders.Count);
            var four = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 9, Sources, samplesPerPrompt: 4);
            Assert.Equal(24, four.Renders.Count);
        }
        [Fact]
        public void EvaluationsRequireExactSamplesAndDispositionUsesTheCandidate()
        {
            var root = GoalLoopFanoutTests.Payload(1, true);
            foreach (var e in root["evaluations"]!.AsArray()) e!["variant"] = e["variant"]!.GetValue<string>() + "-s1";
            root["bestVariant"] = "c1-s1";
            var shown = new[] { new UiGoalLoopRenderKey("c1-s1", "A"), new("c2-s1", "A"), new("c3-s1", "A") };
            var reply = UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), true, 9, shownRenders: shown);
            Assert.Equal("c1", reply.CandidateDecisions![0].Variant);
            root["evaluations"]![0]!["variant"] = "c1-s2";
            Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), true, 9, shownRenders: shown));
        }
        [Fact]
        public void SamplingBudgetAndOldRecordsRemainBounded()
        {
            var loop = new UiGoalLoop { ProtocolVersion = 9, SamplesPerPrompt = 4,
                Generators = Enumerable.Range(0, 8).Select(i => new UiGoalLoopGenerator { Key = $"g{i}", Source = ((char)('A' + i)).ToString() }).ToList() };
            var design = Design(); foreach (var c in design.Plan!.Candidates) c.Sources = null;
            Assert.Throws<InvalidDataException>(() => UiGoalLoopSampling.ValidatePlan(design, loop));
            Assert.Equal(24, loop.RendersPerTurn);
            Assert.Equal(1, new UiGoalLoop().SamplesPerPrompt);
            Assert.Throws<InvalidDataException>(() => UiGoalLoopSampling.Count(3, "grok-web"));
        }
        [Fact]
        public void ImageAndPromptPointsRemainSeparateAndRepeatedClicksAccumulate()
        {
            var entries = History();
            Assert.Equal(1, Vote(entries, "image", 3, 1).Total);
            Assert.Equal(2, Vote(entries, "image", 3, 1).Total);
            Assert.Equal(1, Vote(entries, "image", 3, -1).Total);
            Assert.Equal(-1, Vote(entries, "image", 5, -1).Total);
            var first = Vote(entries, "prompt", 2, 1);
            var second = Vote(entries, "prompt", 4, 1);
            Assert.Equal(first.TargetKey, second.TargetKey);
            Assert.Equal(2, second.Total);
            entries[4].Text += " Changed.";
            Assert.Equal(1, Vote(entries, "prompt", 4, 1).Total);
            Assert.Contains("CURRENT OPERATOR FEEDBACK", UiGoalLoopFeedback.Snapshot(entries));
        }
        [Fact]
        public void VoteRetriesAreIdempotentAndConflictingIdsFail()
        {
            var entries = History(); var id = Guid.NewGuid().ToString();
            var vote = Vote(entries, "image", 3, 1, id);
            Assert.Same(vote, UiGoalLoopFeedback.Create(entries, id, "image", 3, 1, "owner"));
            Assert.Throws<InvalidDataException>(() => UiGoalLoopFeedback.Create(entries, id, "image", 5, 1, "owner"));
            Assert.Throws<InvalidDataException>(() => UiGoalLoopFeedback.Create(entries, id, "image", 3, -1, "owner"));
        }
        [Theory]
        [InlineData("image", 2, 1)] [InlineData("image", 99, 1)] [InlineData("prompt", 3, 1)] [InlineData("image", 3, 4)]
        public void VotesRejectWrongTargetsOrDeltas(string scope, int target, int delta)
            => Assert.Throws<InvalidDataException>(() => Vote(History(), scope, target, delta));
        [Fact]
        public void FeedbackDoesNotInterruptRenderingAndForcesReconsiderationOfUnstartedPlans()
        {
            var entries = History(); Vote(entries, "image", 3, 1);
            var pending = new UiGoalLoopStep(UiGoalLoopStepKind.Render, 1, "p", null);
            Assert.False(UiGoalLoopFeedback.NeedsPlanning(entries, pending));
            Assert.Equal("render-result", UiGoalLoopPlanner.EffectiveTail(entries)!.Kind);
            entries.Add(new() { Index = entries.Count, Kind = "review", Turn = 1,
                Manager = new() { Parsed = Design(), FeedbackThroughEntry = -1 } });
            var next = new UiGoalLoopStep(UiGoalLoopStepKind.Render, 2, "p", null);
            Assert.True(UiGoalLoopFeedback.NeedsPlanning(entries, next));
            entries[^1].Manager!.FeedbackThroughEntry = UiGoalLoopFeedback.Revision(entries);
            Assert.False(UiGoalLoopFeedback.NeedsPlanning(entries, next));
        }
    }
}
