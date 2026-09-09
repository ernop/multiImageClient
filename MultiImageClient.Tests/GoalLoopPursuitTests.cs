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
    public class GoalLoopPursuitTests
    {
        private const string Goal = "A lovely puppy surprise with kind eyes. She likes piano.";
        private static readonly UiGoalLoopRenderKey[] Shown = { new("refine", "A"), new("fresh", "A") };
        private static readonly List<UiGoalCriterion> Rubric = new()
        {
            new() { Id = "surprise", Description = "Puppy surprise", Importance = "required", Basis = "puppy surprise" },
            new() { Id = "eyes", Description = "Kind eyes", Importance = "required", Basis = "kind eyes" },
            new() { Id = "piano", Description = "Personal inspiration", Importance = "context", Basis = "likes piano" },
        };

        private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, UiGoalLoopJson.Options)!;
        internal static JsonObject Payload(bool review = false)
        {
            var components = Rubric.Select(r => new UiGoalComponent
            {
                Criterion = r.Id, Status = r.Importance == "context" ? "missing" : "met",
                Evidence = "The puppy and her open eyes form the focal pair.", Confidence = "medium",
            }).ToList();
            var plan = new UiGoalSearchPlan
            {
                Objective = "Compare the emotional connection without extra props.",
                Options = new()
                {
                    new() { Description = "Piano basket", Reason = "Connects to her interests." },
                    new() { Description = "Garden basket", Reason = "Isolates color harmony." },
                },
                Candidates = Shown.Select(k => new UiGoalExperiment
                {
                    Variant = k.Variant!, Mode = "explore", Question = "Does the encounter read immediately?",
                    Expected = "Two visible faces with clear eye contact.", Changes = new() { "Setting" },
                    Holds = new() { "Woman and puppy" }, RestoreNext = "No required outcomes deferred.",
                }).ToList(),
            };
            var root = new JsonObject
            {
                ["reasoning"] = "Compare images against the goal.", ["goalKind"] = "open-ended",
                ["decision"] = "render", ["rubric"] = Node(Rubric), ["plan"] = Node(plan),
                ["prompt"] = "A puppy in a piano basket.", ["freshPrompt"] = "A puppy in a garden basket.",
                ["evaluations"] = null, ["continueFrom"] = null,
            };
            if (review)
            {
                root["evaluations"] = Node(Shown.Select(k => new
                {
                    variant = k.Variant, source = k.Source, score = 7, goalMet = true,
                    assessment = "Warm connection.", components, experimentAssessment = "The core composition works.",
                }));
                root["continueFrom"] = Node(new { variant = "refine", source = "A" });
                root["bestTurn"] = 1; root["bestVariant"] = "refine"; root["bestSource"] = "A";
                root["findings"] = Node(new UiGoalFindings
                {
                    Observation = "Eyes are visible.", Inference = "Tighter framing may help.",
                    Uncertainty = "One sample per source.", CriticDisagreements = "No critics configured.",
                    NextTest = "Repeat the preferred prompt.",
                });
            }
            return root;
        }

        private static UiGoalLoopManagerReply Parse(JsonObject root, bool review = false)
            => UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), review, 7, shownRenders: review ? Shown : null);

        private static List<UiGoalLoopEntry> History()
        {
            var entries = new List<UiGoalLoopEntry>
            {
                new() { Kind = "goal", Text = Goal, Index = 0 },
                new() { Kind = "design", Turn = 1, Index = 1, Manager = new() { Parsed = Parse(Payload()) } },
            };
            foreach (var turn in new[] { 1, 2 })
            {
                foreach (var k in Shown)
                    entries.Add(new() { Kind = "render-result", Turn = turn, Index = entries.Count,
                        Render = new() { Variant = k.Variant, Source = k.Source, Ok = true } });
                if (turn == 1) entries.Add(new() { Kind = "review", Turn = 1, Index = entries.Count,
                    Manager = new() { Parsed = Parse(Payload(true), true) } });
            }
            return entries;
        }

        private static JsonObject Stop(string outcome = "achieved")
        {
            var root = Payload(true);
            root["decision"] = "done"; root["doneStatement"] = "Reviewed the full goal and remaining tradeoffs.";
            root["completion"] = Node(new UiGoalCompletion
            {
                Outcome = outcome, EvidenceTurns = new() { 1, 2 }, Rationale = "Two directions were compared.",
            });
            return root;
        }

        [Fact]
        public void SimplificationPreservesDeferredRequirementAndRestorationPlan()
        {
            var root = Payload();
            root["plan"]!["candidates"]![0]!["mode"] = "simplify";
            root["plan"]!["candidates"]![0]!["deferredCriteria"] = Node(new[] { "eyes" });
            root["plan"]!["candidates"]![0]!["restoreNext"] = "Restore visible eyes after choosing a palette.";
            var parsed = Parse(root);
            Assert.Equal("eyes", parsed.Plan!.Candidates[0].DeferredCriteria.Single());
            UiGoalLoopPursuit.ValidateContext(parsed, new[] { new UiGoalLoopEntry { Kind = "goal", Text = Goal } }, 1);
            var clone = new UiGoalLoopEntry { Manager = new() { Parsed = parsed } }.Clone();
            Assert.Equal(parsed.Plan.Candidates[0].RestoreNext, clone.Manager!.Parsed!.Plan!.Candidates[0].RestoreNext);
        }

        [Fact]
        public void RepeatedPromptsRequireDeclaredVerification()
        {
            var root = Payload(); root["freshPrompt"] = root["prompt"]!.GetValue<string>();
            Assert.Throws<InvalidDataException>(() => Parse(root));
            foreach (var c in root["plan"]!["candidates"]!.AsArray()) c!["mode"] = "verify";
            Assert.Equal(Parse(root).Prompt, Parse(root).FreshPrompt);
            Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(root.ToJsonString(), false, 6));
        }

        [Theory]
        [InlineData("partial")]
        [InlineData("uncertain")]
        [InlineData("missing")]
        public void ScalarScoreCannotHideMissingRequiredOutcome(string status)
        {
            var root = Payload(true);
            root["evaluations"]![0]!["score"] = 10;
            root["evaluations"]![0]!["components"]![0]!["status"] = status;
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
        }

        [Fact]
        public void ContextCanRemainAbsentWithoutBlockingGoalMet()
        {
            var reply = Parse(Payload(true), true);
            Assert.True(reply.Evaluations().First().Evaluation.GoalMet);
            Assert.Equal("missing", reply.Evaluations().First().Evaluation.Components!.Last().Status);
        }

        [Fact]
        public void CriteriaCannotDisappearOrChangeMeaning()
        {
            var root = Payload(true);
            root["evaluations"]![0]!["components"]!.AsArray().RemoveAt(0);
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
            root = Payload(true); root["rubric"]![0]!["description"] = "Count all props instead.";
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 2));
        }

        [Fact]
        public void CompletionRequiresReviewedEvidenceAndActualSuccessfulImage()
        {
            UiGoalLoopPursuit.ValidateContext(Parse(Stop(), true), History(), 2);
            var root = Stop(); root["completion"]!["evidenceTurns"] = Node(new[] { 1, 9 });
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 2));
            root = Stop(); root["bestTurn"] = 9;
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 2));
            root = Stop(); root["completion"]!["evidenceTurns"] = Node(new[] { 1 });
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
        }

        [Fact]
        public void CoreExperimentCannotBeDeclaredFullGoalCompletion()
        {
            var entries = History();
            entries[1].Manager!.Parsed!.Plan!.Candidates[0].DeferredCriteria.Add("eyes");
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(Stop(), true), entries, 2));
            UiGoalLoopPursuit.ValidateContext(Parse(Stop("plateau"), true), entries, 2);
        }

        [Fact]
        public void PlateauIsDistinctFromSuccessWithoutManufacturedDegradation()
        {
            var entries = History();
            var reply = Parse(Stop("plateau"), true);
            entries.Add(new() { Kind = "review", Turn = 2, Manager = new() { Parsed = reply } });
            var generators = new[] { new UiGoalLoopGenerator { Key = "test", Source = "A" } };
            var next = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 7, generators);
            Assert.Equal(UiGoalLoopStepKind.Plateau, next.Kind);
            entries.Add(new() { Kind = "objection", Turn = 2, Text = "Operator resumed." });
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview,
                UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 7, generators).Kind);
        }

        [Fact]
        public void BestSelectionUsesDeclaredTradeoffInsteadOfMaximumScalar()
        {
            var entries = History();
            entries[4].Manager!.Parsed!.RenderEvaluations![1].Evaluation.Score = 10;
            Assert.Equal("refine", UiGoalLoopPlanner.BestReview(entries)!.Value.Variant);
        }

        [Fact]
        public void CriticsMustExposeComponentsAndAuditGoalInterpretation()
        {
            var root = new JsonObject { ["critiques"] = Payload(true)["evaluations"]!.DeepClone(),
                ["overall"] = "Prefer clear eye contact.", ["rubricConcerns"] = "none" };
            Assert.Equal(3, UiGoalLoopProtocol.ParseCritiqueReply(root.ToJsonString(), Shown, Rubric).Critiques[0].Components!.Count);
            root.Remove("rubricConcerns");
            Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseCritiqueReply(root.ToJsonString(), Shown, Rubric));
        }

        [Fact]
        public void CriticCannotClaimVisualSuccessForFailedRender()
        {
            var root = new JsonObject { ["critiques"] = Payload(true)["evaluations"]!.DeepClone(),
                ["overall"] = "No images returned.", ["rubricConcerns"] = "none" };
            var reply = UiGoalLoopProtocol.ParseCritiqueReply(root.ToJsonString(), Shown, Rubric);
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateCriticContext(reply, Array.Empty<UiGoalLoopEntry>()));
            foreach (var c in reply.Critiques)
            {
                c.GoalMet = false; c.Score = 0;
                foreach (var component in c.Components!) component.Status = "uncertain";
            }
            UiGoalLoopPursuit.ValidateCriticContext(reply, Array.Empty<UiGoalLoopEntry>());
        }

        [Fact]
        public void LongSearchRetainsOnlyCurrentCandidatesAndExactIncumbentPixels()
        {
            var entries = History().Take(2).ToList();
            for (var turn = 1; turn <= 30; turn++)
            {
                entries.Add(new() { Kind = "review-request", Turn = turn, Index = entries.Count,
                    Images = UiGoalLoopVariants.All.SelectMany(v => new[] { "A", "B", "C" }.Select(s =>
                        new UiGoalLoopSentImage { Variant = v, Source = s, JobId = $"{turn}-{v}-{s}" })).ToList() });
                entries.Add(new() { Kind = "review", Turn = turn, Index = entries.Count,
                    Manager = new() { Parsed = Parse(Payload(true), true) } });
            }
            var selected = UiGoalLoopPursuit.VisualContext(entries);
            Assert.Equal(7, selected.Values.Sum(images => images.Count));
            Assert.Equal("1-refine-A", selected[2].Single().JobId);
            Assert.All(selected[entries[^2].Index], image => Assert.StartsWith("30-", image.JobId));
            entries[2].Images!.RemoveAt(0);
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.VisualContext(entries));
        }

        [Fact]
        public void CandidateOptionsRequireSeparateDescriptionsAndReasons()
        {
            var root = Payload();
            root["plan"]!["options"] = Node(new[] { "First idea", "Second idea" });
            Assert.Throws<InvalidDataException>(() => Parse(root));
            root = Payload(); root["plan"]!["options"]![0]!.AsObject().Remove("reason");
            Assert.Throws<InvalidDataException>(() => Parse(root));
        }

        [Fact]
        public void CompletionCannotHideMaterialGapsAndResumeExplainsTheRejection()
        {
            var root = Stop(); root["completion"]!["remainingGaps"] = Node(new[] { "Puppy is absent." });
            var error = Assert.Throws<InvalidDataException>(() =>
                UiGoalLoopPursuit.ValidateContext(Parse(root, true), History(), 2));
            var feedback = UiGoalLoopPursuit.ContractRepairMessage(new UiGoalLoopEntry
            { Error = error.Message, Manager = new() { ParseError = error.Message } });
            Assert.Contains(error.Message, feedback);
            Assert.Contains("No new images were generated", feedback);
            Assert.Contains("Do not erase material gaps", feedback);
        }

        [Fact]
        public void PursuitInstructionsDoNotDemandPrivateThoughtOrIncreasingPropCounts()
        {
            Assert.Contains("TEXT-TO-IMAGE", UiGoalLoopPursuit.ManagerSystemPrompt);
            Assert.DoesNotContain("your full considerations", UiGoalLoopPursuit.ManagerSystemPrompt);
            Assert.DoesNotContain("Every refine render must push", UiGoalLoopPursuit.ManagerSystemPrompt);
            var goal = UiGoalLoopProtocol.BuildGoalMessage(Goal, 6, 7, "open-ended", 3);
            Assert.DoesNotContain("pushed past", goal);
        }

        [Fact]
        public void ComponentStudySelectsOnlyItsSourcesAndPreservesCheaperSettings()
        {
            var root = Payload();
            foreach (var c in root["plan"]!["candidates"]!.AsArray())
            {
                c!["sources"] = Node(new[] { "B" }); c["scope"] = "component";
                c["componentGoal"] = "Table perspective"; c["quality"] = "low"; c["detail"] = "standard";
            }
            var reply = Parse(root);
            var generators = new[] { new UiGoalLoopGenerator { Key = "one", Source = "A" }, new UiGoalLoopGenerator { Key = "two", Source = "B" } };
            var loop = new UiGoalLoop { Quality = "high", Detail = "max", Generators = generators.ToList() };
            var entries = new List<UiGoalLoopEntry> { new() { Kind = "goal", Text = Goal } };
            UiGoalLoopPursuit.ValidateContext(reply, entries, 1, loop);
            entries.Add(new() { Kind = "design", Turn = 1, Manager = new() { Parsed = reply } });
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 7, generators);
            Assert.Equal(2, step.Renders.Count);
            Assert.All(step.Renders, r => { Assert.Equal("B", r.Source); Assert.Equal("low", r.Quality); Assert.Equal("standard", r.Detail); });
            entries.Add(new() { Kind = "render-request", Turn = 1, Index = 2, Text = "Table perspective",
                Render = new() { Source = "B", Variant = "refine", GeneratorKey = "two", Quality = "low", Detail = "standard" } });
            var pending = UiGoalLoopPlanner.DetermineNextStep(entries, 6, "open-ended", 7, generators);
            Assert.Equal("low", pending.Renders.Single().Quality);
            root["plan"]!["candidates"]![0]!["sources"] = Node(new[] { "C" });
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root), entries.Take(1).ToList(), 1, loop));
        }

        [Fact]
        public void ExperimentCannotIncreaseTheOperatorsQualityBudget()
        {
            var root = Payload(); root["plan"]!["candidates"]![0]!["quality"] = "max";
            var loop = new UiGoalLoop { Quality = "low", Detail = "standard" };
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPursuit.ValidateContext(Parse(root),
                new[] { new UiGoalLoopEntry { Kind = "goal", Text = Goal } }, 1, loop));
        }

        [Theory]
        [InlineData("components")]
        [InlineData("plan")]
        [InlineData("rubric")]
        public void NullStructuredFieldsFailWithRecordedContractErrors(string field)
        {
            var root = Payload(true);
            if (field == "components") root["evaluations"]![0]![field] = null;
            else root[field] = null;
            Assert.Throws<InvalidDataException>(() => Parse(root, true));
        }
    }
}
