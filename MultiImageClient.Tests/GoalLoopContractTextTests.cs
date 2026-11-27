using System;
using System.IO;
using System.Text.Json.Nodes;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GoalLoopContractTextTests
    {
        private static UiGoalLoopManagerReply Parse(JsonObject root, int version = 9)
            => UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), false, version);

        [Fact]
        public void FullCandidatesWithoutDeferredCriteriaNeedNoRestorationText()
        {
            var root = GoalLoopFanoutTests.Payload(3);
            foreach (var candidate in root["plan"]!["candidates"]!.AsArray())
            {
                candidate!["scope"] = "full";
                candidate["deferredCriteria"] = new JsonArray();
                candidate["restoreNext"] = "";
            }
            var reply = Parse(root);
            Assert.Equal(3, reply.Plan!.Candidates.Count);
            Assert.All(reply.Plan.Candidates, candidate => Assert.Equal("", candidate.RestoreNext));
            var legacyError = Assert.Throws<InvalidDataException>(() => Parse(root, 8));
            Assert.Contains("plan.candidates[0].restoreNext", legacyError.Message);
        }

        [Theory]
        [InlineData("component", false)]
        [InlineData("component", true)]
        [InlineData("full", true)]
        public void PartialWorkStillRequiresAnExplicitRestorationStep(string scope, bool deferred)
        {
            var root = GoalLoopFanoutTests.Payload(3);
            var candidate = root["plan"]!["candidates"]![1]!;
            candidate["scope"] = scope;
            candidate["componentGoal"] = "Study table perspective.";
            candidate["deferredCriteria"] = deferred ? new JsonArray("eyes") : new JsonArray();
            candidate["restoreNext"] = "";
            var error = Assert.Throws<InvalidDataException>(() => Parse(root));
            Assert.Contains("plan.candidates[1].restoreNext", error.Message);
            Assert.Contains("received 0 characters", error.Message);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("null")]
        [InlineData("whitespace")]
        [InlineData("oversized")]
        public void OptionalEmptyRestorationDoesNotPermitMissingOrMalformedFields(string kind)
        {
            var root = GoalLoopFanoutTests.Payload(1);
            var candidate = root["plan"]!["candidates"]![0]!.AsObject();
            candidate["scope"] = "full";
            candidate["deferredCriteria"] = new JsonArray();
            if (kind == "missing") candidate.Remove("restoreNext");
            else candidate["restoreNext"] = kind switch
            {
                "null" => null,
                "whitespace" => "   ",
                _ => new string('x', 2001),
            };
            var error = Assert.Throws<InvalidDataException>(() => Parse(root));
            Assert.Contains("restoreNext", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2001)]
        public void RequiredTextErrorsIdentifyTheFieldAndActualLength(int length)
        {
            var root = GoalLoopFanoutTests.Payload(1);
            root["plan"]!["objective"] = new string('x', length);
            var error = Assert.Throws<InvalidDataException>(() => Parse(root));
            Assert.Contains("plan.objective must contain 1-2000 characters", error.Message);
            Assert.Contains($"received {length} characters", error.Message);
        }

        [Fact]
        public void RequiredTextAtTheLengthLimitIsAccepted()
        {
            var root = GoalLoopFanoutTests.Payload(1);
            root["plan"]!["objective"] = new string('x', 2000);
            Assert.Equal(2000, Parse(root).Plan!.Objective.Length);
        }

        [Fact]
        public void InvalidCandidateModeNamesFieldAllowedValuesAndReceivedValue()
        {
            var root = GoalLoopFanoutTests.Payload(2);
            root["plan"]!["candidates"]![1]!["mode"] = "branch";
            var error = Assert.Throws<InvalidDataException>(() => Parse(root));
            Assert.Contains("plan.candidates[1].mode", error.Message);
            Assert.Contains("pursue, explore, simplify, rebuild, verify", error.Message);
            Assert.Contains("received 'branch'", error.Message);
        }

        [Fact]
        public void InvalidCandidateDecisionActionAndRubricImportanceIdentifyFieldAndChoices()
        {
            var review = GoalLoopFanoutTests.Payload(1, review: true);
            review["candidateDecisions"]![0]!["action"] = "explore";
            var shown = new[] { new UiGoalLoopRenderKey("c1", "A"), new UiGoalLoopRenderKey("c2", "A"), new UiGoalLoopRenderKey("c3", "A") };
            var actionError = Assert.Throws<InvalidDataException>(() =>
                UiGoalLoopProtocol.ParseManagerReply(review.ToJsonString(), true, 8, shownRenders: shown));
            Assert.Contains("candidateDecisions[0].action", actionError.Message);
            Assert.Contains("pursue, branch, hold, drop", actionError.Message);
            Assert.Contains("received 'explore'", actionError.Message);

            var badRubric = GoalLoopFanoutTests.Payload(1);
            badRubric["rubric"]![2]!["importance"] = "optional";
            var rubricError = Assert.Throws<InvalidDataException>(() => Parse(badRubric));
            Assert.Contains("rubric[2].importance", rubricError.Message);
            Assert.Contains("required, preference, context", rubricError.Message);
            Assert.Contains("received 'optional'", rubricError.Message);
        }

        [Fact]
        public void FanoutPromptDistinguishesCandidateActionsFromCandidateModes()
        {
            Assert.Contains("candidateDecisions[].action, never in plan.candidates[].mode", UiGoalLoopFanout.ManagerSystemPrompt);
            Assert.Contains("candidateDecisions[].action, never in plan.candidates[].mode", UiGoalLoopSampling.ManagerSystemPrompt);
        }
    }
}
