using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using MultiImageClient;

using SixLabors.ImageSharp;

using Xunit;

namespace MultiImageClient.Tests
{
    public class ManagerProviderStopTests
    {
        // Verbatim shape of the live Claude Fable 5.1 refusal recorded on
        // 2026-09-04 (explanation shortened).
        private const string AnthropicRefusal =
            "{\"model\":\"claude-fable-5-1\",\"id\":\"msg_x\",\"type\":\"message\",\"role\":\"assistant\","
            + "\"content\":[],\"stop_reason\":\"refusal\",\"stop_sequence\":null,"
            + "\"stop_details\":{\"type\":\"refusal\",\"category\":\"reasoning_extraction\","
            + "\"explanation\":\"This request was blocked.\"},"
            + "\"usage\":{\"input_tokens\":1317,\"output_tokens\":0}}";

        [Fact]
        public void AnthropicRefusalIsReportedWithCategoryAndExplanation()
        {
            using var doc = JsonDocument.Parse(AnthropicRefusal);
            var stop = AnthropicMessagesManagerClient.DescribeStop(doc.RootElement);
            Assert.Equal("provider stop_reason \"refusal\" (reasoning_extraction): This request was blocked.", stop);
        }

        [Fact]
        public void AnthropicMaxTokensIsAStop()
        {
            using var doc = JsonDocument.Parse("{\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"reas\"}]}");
            Assert.Equal("provider stop_reason \"max_tokens\"", AnthropicMessagesManagerClient.DescribeStop(doc.RootElement));
        }

        [Fact]
        public void AnthropicEndTurnIsNotAStop()
        {
            using var doc = JsonDocument.Parse("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}]}");
            Assert.Null(AnthropicMessagesManagerClient.DescribeStop(doc.RootElement));
        }

        [Fact]
        public void ResponsesIncompleteAndRefusalPartsAreStops()
        {
            using var incomplete = JsonDocument.Parse(
                "{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[]}");
            Assert.Equal("provider status \"incomplete\" (max_output_tokens)",
                ResponsesApiManagerClient.DescribeStop(incomplete.RootElement));

            using var refusal = JsonDocument.Parse(
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]}");
            Assert.Equal("provider refusal: no", ResponsesApiManagerClient.DescribeStop(refusal.RootElement));

            using var ok = JsonDocument.Parse(
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}]}");
            Assert.Null(ResponsesApiManagerClient.DescribeStop(ok.RootElement));
        }

        [Fact]
        public void GeminiBlockedPromptAndNonStopFinishAreStops()
        {
            using var blocked = JsonDocument.Parse("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}");
            Assert.Equal("provider blocked the prompt (SAFETY)", GeminiManagerClient.DescribeStop(blocked.RootElement));

            using var cut = JsonDocument.Parse("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\",\"content\":{\"parts\":[]}}]}");
            Assert.Equal("provider finishReason \"MAX_TOKENS\"", GeminiManagerClient.DescribeStop(cut.RootElement));

            using var none = JsonDocument.Parse("{\"candidates\":[]}");
            Assert.Equal("provider returned no candidates", GeminiManagerClient.DescribeStop(none.RootElement));

            using var ok = JsonDocument.Parse("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}");
            Assert.Null(GeminiManagerClient.DescribeStop(ok.RootElement));
        }

        [Fact]
        public void SystemPromptAvoidsTheFableRefusalTrigger()
        {
            // The version-1 field description made Fable 5.1 refuse every
            // call (reasoning_extraction). Keep it out of the prompt.
            Assert.DoesNotContain("alternatives you considered", UiGoalLoopProtocol.SystemPrompt);
            Assert.DoesNotContain("your full considerations", UiGoalLoopProtocol.SystemPrompt);
            Assert.Equal(5, UiGoalLoopProtocol.Version);
        }
    }

    public class GoalLoopImageTransportTests
    {
        private static readonly UiGoalLoopImageInfo Png2048 = new("image/png", 2048, 2048, 6_000_000);

        [Fact]
        public void FullResolutionVerbatimWhenNoPublishedLimitBinds()
        {
            foreach (var limits in new[] { ManagerCatalog.OpenAiLimits, ManagerCatalog.GeminiLimits, ManagerCatalog.XaiLimits, ManagerCatalog.AnthropicLimits })
            {
                var plan = UiGoalLoopImageTransport.Make(limits, Png2048, imagesInRequest: 1, budgetPressure: false);
                Assert.Equal("verbatim", plan.Mode);
                Assert.Equal(2048, plan.Width);
                Assert.False(plan.ChangesResolution);
            }
        }

        [Fact]
        public void AnthropicPerImageBase64CapForcesFullResolutionJpegNotDownscale()
        {
            // 8,380,887 raw bytes = 11,174,516 base64 bytes, the exact live
            // rejection observed 2026-09-04.
            var info = new UiGoalLoopImageInfo("image/png", 2496, 1664, 8_380_887);
            Assert.Equal(11_174_516, ManagerImageLimits.Base64Length(info.RawBytes));
            var plan = UiGoalLoopImageTransport.Make(ManagerCatalog.AnthropicLimits, info, 1, false);
            Assert.Equal("jpeg", plan.Mode);
            Assert.Equal(2496, plan.Width);
            Assert.Equal(1664, plan.Height);
            Assert.Contains("10,485,760", plan.Reason);
        }

        [Fact]
        public void AnthropicManyImageRuleDownscalesOnlyAbove2000Px()
        {
            var big = new UiGoalLoopImageInfo("image/png", 2880, 2880, 5_000_000);
            Assert.Equal("verbatim", UiGoalLoopImageTransport.Make(ManagerCatalog.AnthropicLimits, big, 20, false).Mode);
            var plan = UiGoalLoopImageTransport.Make(ManagerCatalog.AnthropicLimits, big, 21, false);
            Assert.Equal("downscale", plan.Mode);
            Assert.Equal(2000, plan.Width);
            Assert.Contains("more than 20 images", plan.Reason);
        }

        [Fact]
        public void OpenAiPatchCapIsFarAboveEveryUiRenderSize()
        {
            var max = new UiGoalLoopImageInfo("image/png", 2880, 2880, 9_000_000);
            Assert.Equal("verbatim", UiGoalLoopImageTransport.Make(ManagerCatalog.OpenAiLimits, max, 30, false).Mode);
            var huge = new UiGoalLoopImageInfo("image/png", 8000, 8000, 50_000_000);
            var plan = UiGoalLoopImageTransport.Make(ManagerCatalog.OpenAiLimits, huge, 1, false);
            Assert.Equal("downscale", plan.Mode);
            Assert.True(ManagerImageLimits.Patches32(plan.Width, plan.Height) <= 30_000);
        }

        [Fact]
        public void XaiRejectsWebpSoItIsReencodedLosslessly()
        {
            var webp = new UiGoalLoopImageInfo("image/webp", 1024, 1024, 500_000);
            Assert.Equal("png", UiGoalLoopImageTransport.Make(ManagerCatalog.XaiLimits, webp, 1, false).Mode);
            Assert.Equal("verbatim", UiGoalLoopImageTransport.Make(ManagerCatalog.OpenAiLimits, webp, 1, false).Mode);
        }

        [Fact]
        public void RequestBudgetPressureSwitchesPngToFullResolutionJpeg()
        {
            var sizes = Enumerable.Repeat(6_000_000L, 5).ToList();
            Assert.True(UiGoalLoopImageTransport.RequestBudgetPressure(ManagerCatalog.AnthropicLimits, sizes));
            Assert.False(UiGoalLoopImageTransport.RequestBudgetPressure(ManagerCatalog.OpenAiLimits, sizes));
            Assert.False(UiGoalLoopImageTransport.RequestBudgetPressure(ManagerCatalog.XaiLimits, sizes));
            var plan = UiGoalLoopImageTransport.Make(ManagerCatalog.AnthropicLimits, Png2048, 5, budgetPressure: true);
            Assert.Equal("jpeg", plan.Mode);
            Assert.Equal(2048, plan.Width);
            var jpeg = new UiGoalLoopImageInfo("image/jpeg", 2048, 2048, 1_000_000);
            Assert.Equal("verbatim", UiGoalLoopImageTransport.Make(ManagerCatalog.AnthropicLimits, jpeg, 5, true).Mode);
        }

        [Fact]
        public void ApplyKeepsPixelDimensionsWhenReencoding()
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(300, 200);
            var rng = new System.Random(1);
            for (var y = 0; y < 200; y++)
            {
                for (var x = 0; x < 300; x++)
                {
                    image[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
                }
            }
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var raw = ms.ToArray();
            var info = new UiGoalLoopImageInfo("image/png", 300, 200, raw.Length);

            var verbatim = UiGoalLoopImageTransport.Apply(new UiGoalLoopImageTransport.Plan("verbatim", 300, 200, ""), ManagerCatalog.OpenAiLimits, info, raw, "t");
            Assert.Same(raw, verbatim.Bytes);
            Assert.Equal("image/png", verbatim.Mime);

            var jpeg = UiGoalLoopImageTransport.Apply(new UiGoalLoopImageTransport.Plan("jpeg", 300, 200, "cap"), ManagerCatalog.AnthropicLimits, info, raw, "t");
            Assert.Equal("image/jpeg", jpeg.Mime);
            var decoded = SixLabors.ImageSharp.Image.Identify(jpeg.Bytes);
            Assert.Equal(300, decoded.Width);
            Assert.Equal(200, decoded.Height);
            Assert.Contains("full 300x200", jpeg.Label);
        }
    }

    public class GoalLoopProtocolTests
    {
        // Single-render contract; protocol 4 adds the fresh render (below).
        private const int V3 = 3;

        private const string FirstDesign =
            "{\"reasoning\":\"Start with a literal reading of the goal.\",\"goalKind\":\"bounded\",\"evaluation\":null,"
            + "\"decision\":\"render\",\"designNotes\":\"Literal first pass.\","
            + "\"prompt\":\"A bright daytime photo of a red bicycle leaning on a white wall.\","
            + "\"doneStatement\":null,\"bestTurn\":null}";

        [Fact]
        public void FirstReplyParsesWithoutEvaluation()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(FirstDesign, expectEvaluation: false, protocolVersion: V3);
            Assert.Equal("render", reply.Decision);
            Assert.Null(reply.Evaluation);
            Assert.Equal("Literal first pass.", reply.DesignNotes);
            Assert.StartsWith("A bright daytime photo", reply.Prompt);
            Assert.Null(reply.BestTurn);
        }

        [Fact]
        public void FirstReplyMayNotCarryAnEvaluation()
        {
            var raw = FirstDesign.Replace("\"evaluation\":null",
                "\"evaluation\":{\"score\":5,\"goalMet\":false,\"assessment\":\"n/a\"}");
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false, protocolVersion: V3));
            Assert.Contains("nothing has been rendered yet", ex.Message);
        }

        [Fact]
        public void FirstReplyMustRender()
        {
            var raw = "{\"reasoning\":\"x\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"done\",\"doneStatement\":\"nothing to do\"}";
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false, protocolVersion: V3));
            Assert.Contains("first reply must have decision \"render\"", ex.Message);
        }

        [Fact]
        public void ReviewReplyRequiresEvaluationWithScoreAndGoalMet()
        {
            var raw = "{\"reasoning\":\"Looks close.\",\"decision\":\"render\",\"prompt\":\"again\"}";
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true, protocolVersion: V3));
            Assert.Contains("\"evaluation\" must be an object", ex.Message);

            var missingGoalMet = "{\"reasoning\":\"r\",\"decision\":\"render\",\"prompt\":\"p\","
                + "\"evaluation\":{\"score\":7,\"assessment\":\"ok\"}}";
            ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(missingGoalMet, expectEvaluation: true, protocolVersion: V3));
            Assert.Contains("goalMet", ex.Message);
        }

        [Fact]
        public void ReviewReplyParsesFullEvaluationAndFence()
        {
            var raw = "```json\n{\"reasoning\":\"The wall is beige, not white.\","
                + "\"evaluation\":{\"score\":6.5,\"goalMet\":false,\"assessment\":\"Bicycle correct; wall tint wrong.\","
                + "\"problems\":[\"beige wall\",\"\"],\"keep\":[\"framing\"]},"
                + "\"decision\":\"render\",\"designNotes\":\"Force pure white.\","
                + "\"prompt\":\"...\",\"doneStatement\":null,\"bestTurn\":1}\n```";
            var reply = UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true, protocolVersion: V3);
            Assert.NotNull(reply.Evaluation);
            Assert.Equal(6.5, reply.Evaluation!.Score);
            Assert.False(reply.Evaluation.GoalMet);
            Assert.Equal(new[] { "beige wall" }, reply.Evaluation.Problems);
            Assert.Equal(new[] { "framing" }, reply.Evaluation.Keep);
            Assert.Equal(1, reply.BestTurn);
        }

        [Fact]
        public void ScoreOutsideZeroToTenIsRejected()
        {
            var raw = "{\"reasoning\":\"r\",\"decision\":\"done\",\"doneStatement\":\"d\","
                + "\"evaluation\":{\"score\":11,\"goalMet\":true,\"assessment\":\"a\"}}";
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true, protocolVersion: V3));
            Assert.Contains("within 0-10", ex.Message);
        }

        [Fact]
        public void RenderWithoutPromptAndDoneWithoutStatementAreRejected()
        {
            var noPrompt = "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"decision\":\"render\",\"evaluation\":null}";
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(noPrompt, expectEvaluation: false, protocolVersion: V3));
            var noStatement = "{\"reasoning\":\"r\",\"decision\":\"done\","
                + "\"evaluation\":{\"score\":9,\"goalMet\":true,\"assessment\":\"a\"}}";
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(noStatement, expectEvaluation: true, protocolVersion: V3));
        }

        [Fact]
        public void ProseIsNeverAcceptedAsADesign()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply("Sure! Here is a prompt: a red bicycle.", expectEvaluation: false, protocolVersion: V3));
            Assert.Contains("did not follow the required JSON contract", ex.Message);
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply("   ", expectEvaluation: false, protocolVersion: V3));
        }

        [Fact]
        public void GoalMessageStatesTheTurnBudget()
        {
            var text = UiGoalLoopProtocol.BuildGoalMessage("A poster.", 6);
            Assert.Contains("A poster.", text);
            Assert.Contains("6", text);
            // OpenAI json_object mode requires the word in an input message.
            Assert.Contains("JSON", text);
            Assert.Contains("JSON", UiGoalLoopProtocol.BuildReviewRequestText(
                1, 6, new UiGoalLoopRenderData { Ok = false }, "p", "p", "boom", null));
        }

        // ---- protocol 3: goal kinds and the open-ended stopping rule ----

        [Fact]
        public void FirstDesignMustClassifyTheGoalUnderProtocol3()
        {
            var raw = FirstDesign.Replace("\"goalKind\":\"bounded\",", "");
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false, protocolVersion: V3));
            Assert.Contains("goalKind", ex.Message);
            // A protocol-2 loop never asked for the field: unchanged contract.
            var v2 = UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false, protocolVersion: 2);
            Assert.Null(v2.GoalKind);
        }

        [Fact]
        public void GoalKindMustBeOneOfTheTwoValues()
        {
            var raw = FirstDesign.Replace("\"goalKind\":\"bounded\"", "\"goalKind\":\"maximal\"");
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false, protocolVersion: V3));
            Assert.Contains("\"goalKind\" must be", ex.Message);
            var open = UiGoalLoopProtocol.ParseManagerReply(
                FirstDesign.Replace("\"goalKind\":\"bounded\"", "\"goalKind\":\"Open-Ended\""), expectEvaluation: false, protocolVersion: V3);
            Assert.Equal(UiGoalLoopGoalKinds.OpenEnded, open.GoalKind);
        }

        [Fact]
        public void DoneIsAContractViolationAfterAnObjection()
        {
            var done = "{\"reasoning\":\"r\",\"decision\":\"done\",\"doneStatement\":\"d\","
                + "\"evaluation\":{\"score\":9,\"goalMet\":true,\"assessment\":\"a\"}}";
            Assert.Equal("done", UiGoalLoopProtocol.ParseManagerReply(done, expectEvaluation: true, protocolVersion: V3).Decision);
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(done, expectEvaluation: true, permitDone: false, protocolVersion: V3));
            Assert.Contains("barred", ex.Message);
        }

        [Fact]
        public void GoalMessageCarriesTheOperatorsClassificationOrAsksForOne()
        {
            var auto = UiGoalLoopProtocol.BuildGoalMessage("as many fish as possible", 6, V3);
            Assert.Contains("Classify the GOAL", auto);
            var declared = UiGoalLoopProtocol.BuildGoalMessage("as many fish as possible", 6, 3, UiGoalLoopGoalKinds.OpenEnded);
            Assert.Contains("classified this GOAL as \"open-ended\"", declared);
            Assert.DoesNotContain("Classify the GOAL", declared);
            // Replaying a protocol-2 loop reproduces its original wording.
            var v2 = UiGoalLoopProtocol.BuildGoalMessage("g", 6, 2);
            Assert.DoesNotContain("goalKind", v2);
            Assert.DoesNotContain("Classify", v2);
        }

        [Fact]
        public void SystemPromptStatesTheBudgetIsNeverAReasonToStop()
        {
            var p = UiGoalLoopProtocol.SystemPrompt;
            Assert.Contains("never a reason to declare done", p);
            Assert.Contains("open-ended", p);
            Assert.Contains("goalKind", p);
            Assert.Contains("larger step", p);
        }

        [Fact]
        public void ReviewRequestRestatesTheOpenEndedRuleWithBestSoFar()
        {
            var text = UiGoalLoopProtocol.BuildReviewRequestText(
                2, 6, new UiGoalLoopRenderData { Ok = false }, "p", "p", "boom", null,
                UiGoalLoopGoalKinds.OpenEnded, 1, 9);
            Assert.Contains("open-ended", text);
            Assert.Contains("Best so far: turn 1 (score 9)", text);
            var bounded = UiGoalLoopProtocol.BuildReviewRequestText(
                2, 6, new UiGoalLoopRenderData { Ok = false }, "p", "p", "boom", null,
                UiGoalLoopGoalKinds.Bounded, 1, 9);
            Assert.DoesNotContain("Best so far", bounded);
        }

        [Fact]
        public void ObjectionNamesTheRuleTheEvidenceAndTheRequiredDecision()
        {
            var text = UiGoalLoopProtocol.BuildObjectionText(2, 2, bestTurn: 2, bestScore: 9, latestScore: 9, protocolVersion: V3);
            Assert.Contains("not accepted", text);
            Assert.Contains("best result is turn 2 (score 9)", text);
            Assert.Contains("\"done\" is barred", text);
            Assert.Contains("Renders remaining after turn 2: 0", text);
            Assert.Contains("not a reason to stop", text);
        }
    }

    public class GoalLoopPlannerTests
    {
        private static UiGoalLoopEntry Entry(int index, int turn, string kind, string text = "t")
            => new UiGoalLoopEntry { Index = index, Turn = turn, Kind = kind, Text = text };

        private static UiGoalLoopEntry ManagerEntry(int index, int turn, string kind, string decision, string? prompt, double? score)
        {
            var e = Entry(index, turn, kind, "{...}");
            e.Manager = new UiGoalLoopManagerData
            {
                Model = "test-model",
                Parsed = new UiGoalLoopManagerReply
                {
                    Reasoning = "r",
                    Decision = decision,
                    Prompt = prompt,
                    DoneStatement = decision == "done" ? "done" : null,
                    Evaluation = score.HasValue
                        ? new UiGoalLoopEvaluation { Score = score.Value, GoalMet = decision == "done", Assessment = "a" }
                        : null,
                },
            };
            return e;
        }

        private static List<UiGoalLoopEntry> OneFullTurn(string reviewDecision = "render")
            => new()
            {
                Entry(0, 0, UiGoalLoopKinds.Goal, "goal"),
                ManagerEntry(1, 1, UiGoalLoopKinds.Design, "render", "prompt one", null),
                Entry(2, 1, UiGoalLoopKinds.RenderRequest, "prompt one"),
                Entry(3, 1, UiGoalLoopKinds.RenderResult, "rendered"),
                Entry(4, 1, UiGoalLoopKinds.ReviewRequest, "please review"),
                ManagerEntry(5, 1, UiGoalLoopKinds.Review, reviewDecision, reviewDecision == "render" ? "prompt two" : null, 6),
            };

        [Fact]
        public void GoalOnlyAsksForTheFirstDesign()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(new[] { Entry(0, 0, UiGoalLoopKinds.Goal) }, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.AskManagerDesign, step.Kind);
            Assert.Equal(1, step.Turn);
        }

        [Fact]
        public void DesignLeadsToRenderOfItsPrompt()
        {
            var entries = OneFullTurn().Take(2).ToList();
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(1, step.Turn);
            Assert.Equal("prompt one", step.Prompt);
        }

        [Fact]
        public void UnrenderedRequestIsRenderedAgainWithItsOwnText()
        {
            var entries = OneFullTurn().Take(3).ToList();
            entries[2].Text = "edited prompt";
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("edited prompt", step.Prompt);
        }

        [Fact]
        public void RenderResultAndReviewRequestBothAskForReview()
        {
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview,
                UiGoalLoopPlanner.DetermineNextStep(OneFullTurn().Take(4).ToList(), 6, protocolVersion: 3).Kind);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview,
                UiGoalLoopPlanner.DetermineNextStep(OneFullTurn().Take(5).ToList(), 6, protocolVersion: 3).Kind);
        }

        [Fact]
        public void ReviewWantingAnotherRenderAdvancesTheTurn()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn(), 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Equal("prompt two", step.Prompt);
        }

        [Fact]
        public void ReviewSayingDoneEndsTheLoop()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn("done"), 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Done, step.Kind);
            Assert.Equal("done", step.Reason);
        }

        [Fact]
        public void TurnBudgetIsEnforcedOnTheNextRender()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn(), maxTurns: 1, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Exhausted, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Contains("allows 1", step.Reason);
        }

        [Fact]
        public void NotesAndFailedManagerRepliesDoNotAdvanceTheLoop()
        {
            var entries = OneFullTurn().Take(5).ToList();
            var failed = Entry(5, 1, UiGoalLoopKinds.Review, "not json");
            failed.Error = "manager reply did not follow the required JSON contract";
            failed.Manager = new UiGoalLoopManagerData { Model = "m", ParseError = failed.Error };
            entries.Add(failed);
            entries.Add(Entry(6, 1, UiGoalLoopKinds.Note, "stopped by operator"));
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, step.Kind);
            Assert.Equal(1, step.Turn);
        }

        [Fact]
        public void EmptyEntriesAreInvalid()
        {
            Assert.Equal(UiGoalLoopStepKind.Invalid,
                UiGoalLoopPlanner.DetermineNextStep(new List<UiGoalLoopEntry>(), 6, protocolVersion: 3).Kind);
        }

        // ---- the open-ended stopping rule, as the live fish loop exposed it ----

        // Turn 1: 20 fish, 9/10. Turn 2: 30 fish, 9/10, manager says done.
        private static List<UiGoalLoopEntry> FishLoop(double turn2Score = 9, string turn2Decision = "done")
        {
            var entries = new List<UiGoalLoopEntry>
            {
                Entry(0, 0, UiGoalLoopKinds.Goal, "as many different fish as possible"),
                ManagerEntry(1, 1, UiGoalLoopKinds.Design, "render", "20 fish", null),
                Entry(2, 1, UiGoalLoopKinds.RenderRequest, "20 fish"),
                Entry(3, 1, UiGoalLoopKinds.RenderResult, "rendered"),
                Entry(4, 1, UiGoalLoopKinds.ReviewRequest, "review"),
                ManagerEntry(5, 1, UiGoalLoopKinds.Review, "render", "30 fish", 9),
                Entry(6, 2, UiGoalLoopKinds.RenderRequest, "30 fish"),
                Entry(7, 2, UiGoalLoopKinds.RenderResult, "rendered"),
                Entry(8, 2, UiGoalLoopKinds.ReviewRequest, "review"),
                ManagerEntry(9, 2, UiGoalLoopKinds.Review, turn2Decision, turn2Decision == "render" ? "45 fish" : null, turn2Score),
            };
            entries[1].Manager!.Parsed!.GoalKind = UiGoalLoopGoalKinds.OpenEnded;
            return entries;
        }

        [Fact]
        public void OpenEndedDoneWithoutADemonstratedLimitIsObjectedTo()
        {
            var entries = FishLoop();
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Objection, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Contains("no later render scored below", step.Reason);
            Assert.Equal(UiGoalLoopStepKind.Objection,
                UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3).Kind);
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            // The budget being exhausted (maxTurns 2) changes nothing.
            Assert.Equal(UiGoalLoopStepKind.Objection,
                UiGoalLoopPlanner.DetermineNextStep(entries, 30, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3).Kind);
        }

        [Fact]
        public void TheSameLoopIsDoneWhenBoundedOrUnclassified()
        {
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(FishLoop(), 2, UiGoalLoopGoalKinds.Bounded, protocolVersion: 3).Kind);
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(FishLoop(), 2, null, protocolVersion: 3).Kind);
        }

        [Fact]
        public void DoneIsPermittedOnceALaterRenderScoredBelowTheBest()
        {
            // Turn 2 overshot (30 fish degraded to 7): the limit is shown.
            var entries = FishLoop(turn2Score: 7);
            Assert.True(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3).Kind);
            // An equal score is not degradation.
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(FishLoop(turn2Score: 9), UiGoalLoopGoalKinds.OpenEnded));
        }

        [Fact]
        public void BestReviewIsTheFirstTurnHoldingTheMaximum()
        {
            var best = UiGoalLoopPlanner.BestReview(FishLoop(turn2Score: 9));
            Assert.Equal((1, (string?)null, (string?)null, 9d), best);
            Assert.Equal((2, (string?)null, (string?)null, 9.5d), UiGoalLoopPlanner.BestReview(FishLoop(turn2Score: 9.5)));
        }

        [Fact]
        public void AfterTheObjectionTheReviewIsAskedAgainAndARenderProceeds()
        {
            var entries = FishLoop();
            entries.Add(Entry(10, 2, UiGoalLoopKinds.Objection, "not accepted"));
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, step.Kind);
            Assert.Equal(2, step.Turn);
            entries.Add(ManagerEntry(11, 2, UiGoalLoopKinds.Review, "render", "45 fish", 9));
            step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(3, step.Turn);
            Assert.Equal("45 fish", step.Prompt);
            // With the budget spent the pending prompt waits for the operator.
            Assert.Equal(UiGoalLoopStepKind.Exhausted,
                UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 3).Kind);
        }

        [Fact]
        public void EffectiveGoalKindPrefersTheOperatorThenTheManagerAndIgnoresOldProtocols()
        {
            var entries = FishLoop();
            var loop = new UiGoalLoop { ProtocolVersion = 3, GoalKind = UiGoalLoopGoalKinds.Auto };
            Assert.Equal(UiGoalLoopGoalKinds.OpenEnded, UiGoalLoopPlanner.ResolveEffectiveGoalKind(loop, entries, out var source));
            Assert.Equal("manager", source);
            loop.GoalKind = UiGoalLoopGoalKinds.Bounded;
            Assert.Equal(UiGoalLoopGoalKinds.Bounded, UiGoalLoopPlanner.ResolveEffectiveGoalKind(loop, entries, out source));
            Assert.Equal("operator", source);
            loop.ProtocolVersion = 2;
            Assert.Null(UiGoalLoopPlanner.ResolveEffectiveGoalKind(loop, entries, out source));
            Assert.Null(source);
            Assert.Null(UiGoalLoopPlanner.ResolveEffectiveGoalKind(
                new UiGoalLoop { ProtocolVersion = 3 }, new[] { Entry(0, 0, UiGoalLoopKinds.Goal) }, out _));
        }
    }

    public class GoalLoopForkTests
    {
        private static List<UiGoalLoopEntry> Parent()
        {
            var design = new UiGoalLoopEntry
            {
                Index = 1, Turn = 1, Kind = UiGoalLoopKinds.Design,
                Text = "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"prompt one\"}",
                WireRequest = "req", WireResponse = "resp",
                Manager = new UiGoalLoopManagerData
                {
                    Model = "test-model", InputTokens = 10, OutputTokens = 20, CostUsd = 0.01m,
                    Parsed = UiGoalLoopProtocol.ParseManagerReply(
                        "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"prompt one\"}", false, protocolVersion: 3),
                },
            };
            var request = new UiGoalLoopEntry
            {
                Index = 2, Turn = 1, Kind = UiGoalLoopKinds.RenderRequest, Text = "prompt one",
                Render = new UiGoalLoopRenderData
                {
                    JobId = "job-1", GeneratorKey = "gpt2", GeneratorLabel = "gpt-image-2",
                    Shape = "square", Detail = "standard", Quality = "low", Moderation = "low",
                    Ok = true, ImageUrl = "/api/jobs/job-1/images/gpt2/0", Size = "1024x1024", Cost = 0.02m,
                },
            };
            return new List<UiGoalLoopEntry>
            {
                new UiGoalLoopEntry { Index = 0, Turn = 0, Kind = UiGoalLoopKinds.Goal, Text = "the goal" },
                design,
                request,
                new UiGoalLoopEntry { Index = 3, Turn = 1, Kind = UiGoalLoopKinds.RenderResult, Text = "rendered" },
                new UiGoalLoopEntry { Index = 4, Turn = 1, Kind = UiGoalLoopKinds.ReviewRequest, Text = "review it" },
                new UiGoalLoopEntry { Index = 5, Turn = 1, Kind = UiGoalLoopKinds.Note, Text = "a note" },
            };
        }

        [Fact]
        public void ForkCopiesEntriesUpToAndIncludingThePoint()
        {
            var copies = UiGoalLoopPlanner.BuildForkEntries(Parent(), 3, null, out var newGoal);
            Assert.Null(newGoal);
            Assert.Equal(4, copies.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, copies.Select(c => c.Index));
            Assert.All(copies, c => Assert.False(c.Edited));
            // Deep copies: mutating the fork does not touch the parent.
            var parent = Parent();
            var forked = UiGoalLoopPlanner.BuildForkEntries(parent, 2, null, out _);
            forked[2].Render!.JobId = "changed";
            Assert.Equal("job-1", parent[2].Render!.JobId);
        }

        [Fact]
        public void EditingTheGoalReportsTheNewGoal()
        {
            var copies = UiGoalLoopPlanner.BuildForkEntries(Parent(), 0, "  a sharper goal ", out var newGoal);
            Assert.Equal("a sharper goal", newGoal);
            Assert.Single(copies);
            Assert.True(copies[0].Edited);
            Assert.Equal("the goal", copies[0].OriginalText);
            Assert.Equal("a sharper goal", copies[0].Text);
        }

        [Fact]
        public void EditingARenderRequestDropsTheOldJobLinkButKeepsOptions()
        {
            var copies = UiGoalLoopPlanner.BuildForkEntries(Parent(), 2, "prompt one, but at dusk", out _);
            var edited = copies[2];
            Assert.True(edited.Edited);
            Assert.Equal("prompt one", edited.OriginalText);
            Assert.Null(edited.Render!.JobId);
            Assert.Null(edited.Render.Ok);
            Assert.Null(edited.Render.ImageUrl);
            Assert.Equal("gpt2", edited.Render.GeneratorKey);
            Assert.Equal("low", edited.Render.Quality);
            var step = UiGoalLoopPlanner.DetermineNextStep(copies, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("prompt one, but at dusk", step.Prompt);
        }

        [Fact]
        public void EditingAManagerReplyReparsesAndDropsProviderAccounting()
        {
            var newReply = "{\"reasoning\":\"operator rewrite\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"prompt zero\"}";
            var copies = UiGoalLoopPlanner.BuildForkEntries(Parent(), 1, newReply, out _, protocolVersion: 3);
            // An edited first design in a protocol-2 loop needs no goalKind.
            var v2 = UiGoalLoopPlanner.BuildForkEntries(Parent(), 1,
                "{\"reasoning\":\"r\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"p\"}", out _, protocolVersion: 2);
            Assert.Equal("p", v2[1].Manager!.Parsed!.Prompt);
            var edited = copies[1];
            Assert.Equal("prompt zero", edited.Manager!.Parsed!.Prompt);
            Assert.Null(edited.Manager.InputTokens);
            Assert.Null(edited.Manager.CostUsd);
            Assert.Null(edited.WireRequest);
            Assert.Null(edited.WireResponse);
            Assert.Equal("test-model (edited by operator)", edited.Manager.Model);
            var step = UiGoalLoopPlanner.DetermineNextStep(copies, 6, protocolVersion: 3);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("prompt zero", step.Prompt);
        }

        [Fact]
        public void EditedManagerReplyMustStillFollowTheContract()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 1, "just make it bluer", out _, protocolVersion: 3));
            Assert.Contains("must itself follow the JSON contract", ex.Message);
        }

        [Fact]
        public void NotesAndResultsAreNotEditableForkPoints()
        {
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 5, null, out _));
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 3, "new text", out _));
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 0, "   ", out _));
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 9, null, out _));
        }

        [Fact]
        public void MaxTurnsIsBounded()
        {
            Assert.Equal(6, UiGoalLoopPlanner.ValidateMaxTurns(6));
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPlanner.ValidateMaxTurns(0));
            Assert.Throws<InvalidDataException>(() => UiGoalLoopPlanner.ValidateMaxTurns(UiGoalLoopPlanner.MaxTurnsCap + 1));
        }
    }

    // ---- protocol 4: two renders per turn (refine + fresh) ----
    public class GoalLoopPairTests
    {
        private const string FirstDesign =
            "{\"reasoning\":\"Two approaches.\",\"goalKind\":\"open-ended\",\"evaluation\":null,\"freshEvaluation\":null,\"continueFrom\":null,"
            + "\"decision\":\"render\",\"prompt\":\"A poster grid of 20 labeled fish.\",\"freshPrompt\":\"A coral reef teeming with 20 species, each labeled.\","
            + "\"designNotes\":\"grid\",\"freshDesignNotes\":\"scene\",\"doneStatement\":null,\"bestTurn\":null,\"bestVariant\":null}";

        private const string Review =
            "{\"reasoning\":\"The reef shows more and reads better.\",\"goalKind\":\"open-ended\","
            + "\"evaluation\":{\"score\":7,\"goalMet\":false,\"assessment\":\"grid ok\",\"problems\":[],\"keep\":[]},"
            + "\"freshEvaluation\":{\"score\":8.5,\"goalMet\":false,\"assessment\":\"reef better\",\"problems\":[\"two labels merged\"],\"keep\":[\"depth\"]},"
            + "\"continueFrom\":\"fresh\",\"decision\":\"render\",\"prompt\":\"Reef with 30 species.\",\"freshPrompt\":\"An aquarium wall of 30 tanks.\","
            + "\"bestTurn\":1,\"bestVariant\":\"fresh\"}";

        [Fact]
        public void FirstDesignCarriesTwoDifferentPrompts()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(FirstDesign, expectEvaluation: false, protocolVersion: 4);
            Assert.StartsWith("A poster grid", reply.Prompt);
            Assert.StartsWith("A coral reef", reply.FreshPrompt);
            Assert.Null(reply.ContinueFrom);
            Assert.Equal("scene", reply.FreshDesignNotes);

            var noFresh = FirstDesign.Replace(",\"freshPrompt\":\"A coral reef teeming with 20 species, each labeled.\"", "");
            var ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(noFresh, expectEvaluation: false, protocolVersion: 4));
            Assert.Contains("freshPrompt", ex.Message);

            var same = FirstDesign.Replace("A coral reef teeming with 20 species, each labeled.", "A poster grid of 20 labeled fish.");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(same, expectEvaluation: false, protocolVersion: 4));
            Assert.Contains("must differ", ex.Message);

            var early = FirstDesign.Replace("\"continueFrom\":null", "\"continueFrom\":\"refine\"");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(early, expectEvaluation: false, protocolVersion: 4));
            Assert.Contains("continueFrom", ex.Message);
        }

        [Fact]
        public void ReviewCarriesBothEvaluationsAndTheLineageChoice()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(Review, expectEvaluation: true, protocolVersion: 4);
            Assert.Equal(7, reply.Evaluation!.Score);
            Assert.Equal(8.5, reply.FreshEvaluation!.Score);
            Assert.Equal(UiGoalLoopVariants.Fresh, reply.ContinueFrom);
            Assert.Equal(UiGoalLoopVariants.Fresh, reply.BestVariant);
            Assert.Equal(2, reply.Evaluations().Count());
            Assert.Equal(UiGoalLoopVariants.Refine, reply.Evaluations().First().Variant);

            var noFreshEval = Review.Replace("\"freshEvaluation\":{\"score\":8.5,\"goalMet\":false,\"assessment\":\"reef better\",\"problems\":[\"two labels merged\"],\"keep\":[\"depth\"]},", "");
            var ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(noFreshEval, expectEvaluation: true, protocolVersion: 4));
            Assert.Contains("freshEvaluation", ex.Message);

            var noChoice = Review.Replace("\"continueFrom\":\"fresh\",", "");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(noChoice, expectEvaluation: true, protocolVersion: 4));
            Assert.Contains("continueFrom", ex.Message);

            var badChoice = Review.Replace("\"continueFrom\":\"fresh\"", "\"continueFrom\":\"both\"");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(badChoice, expectEvaluation: true, protocolVersion: 4));
            Assert.Contains("\"continueFrom\" must be", ex.Message);
        }

        [Fact]
        public void DoneAfterAReviewNeedsNeitherPromptsNorALineageChoice()
        {
            var done = "{\"reasoning\":\"r\",\"decision\":\"done\",\"doneStatement\":\"turn 1 fresh is best\","
                + "\"evaluation\":{\"score\":5,\"goalMet\":false,\"assessment\":\"a\"},"
                + "\"freshEvaluation\":{\"score\":4,\"goalMet\":false,\"assessment\":\"b\"},\"bestTurn\":1,\"bestVariant\":\"fresh\"}";
            var reply = UiGoalLoopProtocol.ParseManagerReply(done, expectEvaluation: true, protocolVersion: 4);
            Assert.Equal("done", reply.Decision);
            Assert.Null(reply.ContinueFrom);
        }

        [Fact]
        public void OlderLoopsNeverDemandTheFreshFields()
        {
            var v3 = "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"p\"}";
            var reply = UiGoalLoopProtocol.ParseManagerReply(v3, expectEvaluation: false, protocolVersion: 3);
            Assert.Null(reply.FreshPrompt);
            Assert.Empty(reply.Evaluations());
            var v3Review = "{\"reasoning\":\"r\",\"decision\":\"render\",\"prompt\":\"p\",\"evaluation\":{\"score\":6,\"goalMet\":false,\"assessment\":\"a\"}}";
            var reviewed = UiGoalLoopProtocol.ParseManagerReply(v3Review, expectEvaluation: true, protocolVersion: 3);
            Assert.Equal(((string?)null, 6d), reviewed.Evaluations().Select(x => (x.Variant, x.Evaluation.Score)).Single());
        }

        [Fact]
        public void GoalMessageAndSystemPromptDescribeTheTwoRenders()
        {
            var goal = UiGoalLoopProtocol.BuildGoalMessage("as many fish as possible", 6, 4);
            Assert.Contains("freshPrompt", goal);
            Assert.Contains("turn(s)", goal);
            Assert.Contains("freshPrompt", UiGoalLoopProtocol.SystemPrompt);
            Assert.Contains("continueFrom", UiGoalLoopProtocol.SystemPrompt);
            Assert.Contains("from-scratch", UiGoalLoopProtocol.SystemPrompt);
        }

        private static UiGoalLoopProtocol.UiGoalLoopTurnRender TurnRender(string variant, bool ok, string prompt = "p")
            => new(variant,
                new UiGoalLoopRenderData { Variant = variant, Ok = ok, Size = ok ? "1024x1024" : null },
                prompt, prompt, ok ? null : "boom",
                ok ? new UiGoalLoopSentImage { Variant = variant, Transport = "verbatim" } : null);

        [Fact]
        public void PairReviewRequestNamesEachRenderAndItsAttachmentNumber()
        {
            var text = UiGoalLoopProtocol.BuildPairReviewRequestText(
                2, 6, new[] { TurnRender("refine", true), TurnRender("fresh", true) }, "fresh",
                UiGoalLoopGoalKinds.OpenEnded, 1, "fresh", 8.5);
            Assert.Contains("REFINE render: the generator returned an image (1024x1024 pixels); it is attached image #1", text);
            Assert.Contains("FRESH render: the generator returned an image (1024x1024 pixels); it is attached image #2", text);
            Assert.Contains("built on the fresh render of turn 1", text);
            Assert.Contains("Best so far: turn 1 fresh render (score 8.5)", text);
            Assert.Contains("JSON", text);

            // A failed refine keeps the fresh image as attachment #1.
            var oneFailed = UiGoalLoopProtocol.BuildPairReviewRequestText(
                1, 6, new[] { TurnRender("refine", false), TurnRender("fresh", true) }, null, null, null, null, null);
            Assert.Contains("REFINE render: the generator FAILED", oneFailed);
            Assert.Contains("FRESH render: the generator returned an image (1024x1024 pixels); it is attached image #1", oneFailed);
        }

        [Fact]
        public void ObjectionUnderProtocol4SpeaksOfTheRefineRender()
        {
            var text = UiGoalLoopProtocol.BuildObjectionText(2, 6, 1, 8.5, 8, protocolVersion: 4, bestVariant: "fresh");
            Assert.Contains("refine render has pushed past", text);
            Assert.Contains("turn 1 (fresh render) (score 8.5)", text);
            Assert.Contains("and a fresh prompt", text);
            Assert.Contains("Turns remaining after turn 2: 4", text);
        }

        // ---- planner ----

        private static UiGoalLoopEntry Entry(int index, int turn, string kind, string? variant = null, string text = "t")
        {
            var e = new UiGoalLoopEntry { Index = index, Turn = turn, Kind = kind, Text = text };
            if (variant != null || kind == UiGoalLoopKinds.RenderRequest || kind == UiGoalLoopKinds.RenderResult)
            {
                e.Render = new UiGoalLoopRenderData { Variant = variant, Ok = kind == UiGoalLoopKinds.RenderResult };
            }
            return e;
        }

        private static UiGoalLoopEntry Reply(int index, int turn, string kind, string decision, string? prompt, string? fresh,
            double? refineScore, double? freshScore, string? continueFrom)
        {
            var e = Entry(index, turn, kind, text: "{...}");
            e.Manager = new UiGoalLoopManagerData
            {
                Model = "m",
                Parsed = new UiGoalLoopManagerReply
                {
                    Reasoning = "r",
                    Decision = decision,
                    Prompt = prompt,
                    FreshPrompt = fresh,
                    ContinueFrom = continueFrom,
                    DoneStatement = decision == "done" ? "done" : null,
                    GoalKind = UiGoalLoopGoalKinds.OpenEnded,
                    Evaluation = refineScore.HasValue ? new UiGoalLoopEvaluation { Score = refineScore.Value, Assessment = "a" } : null,
                    FreshEvaluation = freshScore.HasValue ? new UiGoalLoopEvaluation { Score = freshScore.Value, Assessment = "b" } : null,
                },
            };
            return e;
        }

        // Turn 1: refine 7, fresh 8.5 → continue from fresh. Turn 2 pending.
        private static List<UiGoalLoopEntry> PairLoop() => new()
        {
            Entry(0, 0, UiGoalLoopKinds.Goal, text: "as many fish as possible"),
            Reply(1, 1, UiGoalLoopKinds.Design, "render", "grid 20", "reef 20", null, null, null),
            Entry(2, 1, UiGoalLoopKinds.RenderRequest, "refine", "grid 20"),
            Entry(3, 1, UiGoalLoopKinds.RenderRequest, "fresh", "reef 20"),
            Entry(4, 1, UiGoalLoopKinds.RenderResult, "fresh"),
            Entry(5, 1, UiGoalLoopKinds.RenderResult, "refine"),
            Entry(6, 1, UiGoalLoopKinds.ReviewRequest),
            Reply(7, 1, UiGoalLoopKinds.Review, "render", "reef 30", "tanks 30", 7, 8.5, "fresh"),
        };

        [Fact]
        public void ADesignPlansBothRendersRefineFirst()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(PairLoop().Take(2).ToList(), 6, protocolVersion: 4);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(2, step.Renders.Count);
            Assert.Equal(("refine", "grid 20"), (step.Renders[0].Variant, step.Renders[0].Prompt));
            Assert.Equal(("fresh", "reef 20"), (step.Renders[1].Variant, step.Renders[1].Prompt));
            Assert.Equal("grid 20", step.Prompt);
            Assert.All(step.Renders, r => Assert.Null(r.ReplaceIndex));
        }

        [Fact]
        public void TheReviewWaitsUntilEveryRequestedRenderHasAResult()
        {
            // Both requested, only fresh rendered: the refine is still owed,
            // filled in place at its request index.
            var step = UiGoalLoopPlanner.DetermineNextStep(PairLoop().Take(5).ToList(), 6, protocolVersion: 4);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Single(step.Renders);
            Assert.Equal("refine", step.Renders[0].Variant);
            Assert.Equal(2, step.Renders[0].ReplaceIndex);
            Assert.Equal("grid 20", step.Renders[0].Prompt);
            // Both rendered (in either order): ask for the review.
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, UiGoalLoopPlanner.DetermineNextStep(PairLoop().Take(6).ToList(), 6, protocolVersion: 4).Kind);
            // Both requested, neither rendered: both owed, refine first.
            var both = UiGoalLoopPlanner.DetermineNextStep(PairLoop().Take(4).ToList(), 6, protocolVersion: 4);
            Assert.Equal(new[] { "refine", "fresh" }, both.Renders.Select(r => r.Variant));
            Assert.Equal(new int?[] { 2, 3 }, both.Renders.Select(r => r.ReplaceIndex));
        }

        [Fact]
        public void AReviewPlansTheNextPairAndTheBudgetCountsTurns()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(PairLoop(), 6, protocolVersion: 4);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Equal(new[] { "reef 30", "tanks 30" }, step.Renders.Select(r => r.Prompt));
            var exhausted = UiGoalLoopPlanner.DetermineNextStep(PairLoop(), 1, protocolVersion: 4);
            Assert.Equal(UiGoalLoopStepKind.Exhausted, exhausted.Kind);
            Assert.Contains("allows 1 turn(s)", exhausted.Reason);
            Assert.Equal(1, UiGoalLoopPlanner.CountRenderedTurns(PairLoop()));
            Assert.Equal(2, UiGoalLoopPlanner.CountRenders(PairLoop()));
        }

        [Fact]
        public void BestReviewSeesBothRendersAndOnlyRefineDegradationPermitsDone()
        {
            var entries = PairLoop();
            Assert.Equal((1, "fresh", (string?)null, 8.5), UiGoalLoopPlanner.BestReview(entries));
            // Turn 2: refine 9 (new best), fresh 3 (exploration flopped) → no limit shown.
            entries.Add(Entry(8, 2, UiGoalLoopKinds.RenderRequest, "refine", "reef 30"));
            entries.Add(Entry(9, 2, UiGoalLoopKinds.RenderRequest, "fresh", "tanks 30"));
            entries.Add(Entry(10, 2, UiGoalLoopKinds.RenderResult, "refine"));
            entries.Add(Entry(11, 2, UiGoalLoopKinds.RenderResult, "fresh"));
            entries.Add(Entry(12, 2, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(13, 2, UiGoalLoopKinds.Review, "done", null, null, 9, 3, null));
            Assert.Equal((2, "refine", (string?)null, 9d), UiGoalLoopPlanner.BestReview(entries));
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 4);
            Assert.Equal(UiGoalLoopStepKind.Objection, step.Kind);
            Assert.Contains("no later refine render scored below", step.Reason);
            // Turn 3: refine 6 (overshot), fresh 9.5 (a new best elsewhere) → the
            // refine push degraded below the best-at-that-time... but the best is
            // now the fresh 9.5 at turn 3, and nothing after it has degraded.
            entries.Add(Entry(14, 2, UiGoalLoopKinds.Objection));
            entries.Add(Reply(15, 2, UiGoalLoopKinds.Review, "render", "reef 45", "mural 45", 9, 3, "refine"));
            entries.Add(Entry(16, 3, UiGoalLoopKinds.RenderRequest, "refine", "reef 45"));
            entries.Add(Entry(17, 3, UiGoalLoopKinds.RenderRequest, "fresh", "mural 45"));
            entries.Add(Entry(18, 3, UiGoalLoopKinds.RenderResult, "refine"));
            entries.Add(Entry(19, 3, UiGoalLoopKinds.RenderResult, "fresh"));
            entries.Add(Entry(20, 3, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(21, 3, UiGoalLoopKinds.Review, "done", null, null, 6, 9.5, null));
            Assert.Equal((3, "fresh", (string?)null, 9.5), UiGoalLoopPlanner.BestReview(entries));
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            // Turn 4: refine (continuing from the 9.5 fresh) scores 8 → degraded past the best.
            entries.Add(Entry(22, 3, UiGoalLoopKinds.Objection));
            entries.Add(Reply(23, 3, UiGoalLoopKinds.Review, "render", "mural 60", "atlas 60", 6, 9.5, "fresh"));
            entries.Add(Entry(24, 4, UiGoalLoopKinds.RenderRequest, "refine", "mural 60"));
            entries.Add(Entry(25, 4, UiGoalLoopKinds.RenderRequest, "fresh", "atlas 60"));
            entries.Add(Entry(26, 4, UiGoalLoopKinds.RenderResult, "refine"));
            entries.Add(Entry(27, 4, UiGoalLoopKinds.RenderResult, "fresh"));
            entries.Add(Entry(28, 4, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(29, 4, UiGoalLoopKinds.Review, "done", null, null, 8, 9.5, null));
            Assert.True(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            Assert.Equal(UiGoalLoopStepKind.Done, UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, protocolVersion: 4).Kind);
        }

        [Fact]
        public void SingleRenderLoopsStillPlanOneRenderUnderTheirOwnVersion()
        {
            var entries = new List<UiGoalLoopEntry>
            {
                Entry(0, 0, UiGoalLoopKinds.Goal),
                Reply(1, 1, UiGoalLoopKinds.Design, "render", "only prompt", null, null, null, null),
            };
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, null, protocolVersion: 3);
            Assert.Single(step.Renders);
            Assert.Null(step.Renders[0].Variant);
            Assert.Equal("only prompt", step.Prompt);
        }
    }

    // Protocol 5: several generators ("sources" A, B, …) render every prompt.
    public class GoalLoopMultiSourceTests
    {
        private static readonly List<UiGoalLoopGenerator> TwoSources = new()
        {
            new UiGoalLoopGenerator { Key = "gpt2", Label = "gpt-image-2", Source = "A" },
            new UiGoalLoopGenerator { Key = "grok-web", Label = "grok-web pro", Source = "B" },
        };

        private static readonly UiGoalLoopRenderKey[] FourShown =
        {
            new("refine", "A"), new("refine", "B"), new("fresh", "A"), new("fresh", "B"),
        };

        private const string FirstDesign =
            "{\"reasoning\":\"Two approaches.\",\"goalKind\":\"open-ended\",\"evaluations\":null,\"continueFrom\":null,"
            + "\"decision\":\"render\",\"prompt\":\"A poster grid of 20 labeled fish.\",\"freshPrompt\":\"A coral reef teeming with 20 species, each labeled.\","
            + "\"designNotes\":\"grid\",\"freshDesignNotes\":\"scene\",\"doneStatement\":null,\"bestTurn\":null,\"bestVariant\":null,\"bestSource\":null}";

        private static string Eval(string variant, string source, double score, string assessment = "a")
            => $"{{\"variant\":\"{variant}\",\"source\":\"{source}\",\"score\":{score},\"goalMet\":false,\"assessment\":\"{assessment}\",\"problems\":[],\"keep\":[]}}";

        private static readonly string Review =
            "{\"reasoning\":\"B's reef reads best.\",\"goalKind\":\"open-ended\",\"evaluations\":["
            + Eval("refine", "A", 7) + "," + Eval("fresh", "A", 6) + "," + Eval("refine", "B", 8) + "," + Eval("fresh", "B", 8.5, "reef better")
            + "],\"continueFrom\":{\"variant\":\"fresh\",\"source\":\"B\"},\"decision\":\"render\","
            + "\"prompt\":\"Reef with 30 species.\",\"freshPrompt\":\"An aquarium wall of 30 tanks.\",\"bestTurn\":1,\"bestVariant\":\"fresh\",\"bestSource\":\"B\"}";

        [Fact]
        public void FirstDesignHasNoEvaluationsAndNoLineage()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(FirstDesign, expectEvaluation: false);
            Assert.StartsWith("A poster grid", reply.Prompt);
            Assert.StartsWith("A coral reef", reply.FreshPrompt);
            Assert.Null(reply.ContinueFrom);
            Assert.Null(reply.ContinueFromSource);
            Assert.Empty(reply.Evaluations());

            var early = FirstDesign.Replace("\"evaluations\":null", "\"evaluations\":[" + Eval("refine", "A", 5) + "]");
            var ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(early, expectEvaluation: false));
            Assert.Contains("evaluations", ex.Message);

            // The protocol-4 fields are not this contract.
            var oldShape = FirstDesign.Replace("\"evaluations\":null", "\"evaluation\":null,\"freshEvaluation\":null");
            var ok = UiGoalLoopProtocol.ParseManagerReply(oldShape, expectEvaluation: false);
            Assert.Equal("render", ok.Decision);
        }

        [Fact]
        public void ReviewScoresEveryShownRenderExactlyOnce()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(Review, expectEvaluation: true, shownRenders: FourShown);
            var scored = reply.Evaluations().ToList();
            Assert.Equal(4, scored.Count);
            // Fixed order: refine A, refine B, fresh A, fresh B.
            Assert.Equal(new[] { ("refine", "A"), ("refine", "B"), ("fresh", "A"), ("fresh", "B") }, scored.Select(s => (s.Variant!, s.Source!)));
            Assert.Equal(8.5, reply.EvaluationOf("fresh", "B")!.Score);
            Assert.Equal(7, reply.EvaluationOf("refine", "A")!.Score);
            Assert.Null(reply.Evaluation);
            Assert.Null(reply.FreshEvaluation);
            Assert.Equal(("fresh", "B"), (reply.ContinueFrom, reply.ContinueFromSource));
            Assert.Equal("B", reply.BestSource);

            var missing = Review.Replace("," + Eval("refine", "B", 8), "");
            var ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(missing, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("missing the refine render of source B", ex.Message);

            var duplicate = Review.Replace("," + Eval("refine", "B", 8), "," + Eval("refine", "B", 8) + "," + Eval("refine", "B", 8));
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(duplicate, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("twice", ex.Message);

            var extra = Review.Replace("," + Eval("refine", "B", 8), "," + Eval("refine", "B", 8) + "," + Eval("refine", "C", 8));
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(extra, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("source C, which was not among the renders shown", ex.Message);

            var notShown = Review.Replace("{\"variant\":\"fresh\",\"source\":\"B\"}", "{\"variant\":\"fresh\",\"source\":\"C\"}");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(notShown, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("continueFrom", ex.Message);

            var stringChoice = Review.Replace("{\"variant\":\"fresh\",\"source\":\"B\"}", "\"fresh\"");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(stringChoice, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("\"continueFrom\" must be null or an object", ex.Message);

            var pairShape = Review.Replace("\"evaluations\":[", "\"evaluation\":" + Eval("refine", "A", 7) + ",\"evaluations\":[");
            ex = Assert.Throws<InvalidDataException>(() => UiGoalLoopProtocol.ParseManagerReply(pairShape, expectEvaluation: true, shownRenders: FourShown));
            Assert.Contains("must be absent or null", ex.Message);
        }

        [Fact]
        public void AReviewParseNeedsTheShownRenders()
        {
            Assert.Throws<System.ArgumentException>(() => UiGoalLoopProtocol.ParseManagerReply(Review, expectEvaluation: true));
        }

        [Fact]
        public void GoalMessageNamesTheSources()
        {
            var text = UiGoalLoopProtocol.BuildGoalMessage("as many fish as possible", 6, sourceCount: 2);
            Assert.Contains("There are 2 sources (A, B)", text);
            Assert.Contains("4 render(s) per turn", text);
            Assert.Contains("one image, from any source", text);
            Assert.Contains("evaluations and continueFrom must be null", text);
            var one = UiGoalLoopProtocol.BuildGoalMessage("g", 6, sourceCount: 1);
            Assert.Contains("There is 1 source (A)", one);
            Assert.Contains("source", UiGoalLoopProtocol.SystemPrompt);
            Assert.Contains("\"evaluations\"", UiGoalLoopProtocol.SystemPrompt);
            Assert.Contains("bestSource", UiGoalLoopProtocol.SystemPrompt);
            Assert.Contains("ONE image, from any source", UiGoalLoopProtocol.SystemPrompt);
        }

        private static UiGoalLoopProtocol.UiGoalLoopTurnRender TurnRender(string variant, string source, bool ok)
            => new(variant,
                new UiGoalLoopRenderData { Variant = variant, Source = source, Ok = ok, Size = ok ? "1024x1024" : null },
                "p", "p", ok ? null : "boom",
                ok ? new UiGoalLoopSentImage { Variant = variant, Source = source, Transport = "verbatim" } : null,
                source);

        [Fact]
        public void ReviewRequestListsRefineRendersThenFreshInSourceOrder()
        {
            var text = UiGoalLoopProtocol.BuildMultiSourceReviewRequestText(
                2, 6,
                new[] { TurnRender("fresh", "B", true), TurnRender("refine", "A", true), TurnRender("fresh", "A", false), TurnRender("refine", "B", true) },
                new[] { "A", "B" }, new UiGoalLoopRenderKey("fresh", "B"), UiGoalLoopGoalKinds.OpenEnded, 1, "fresh", "B", 8.5);
            Assert.Contains("rendered by all 2 sources (A, B)", text);
            Assert.Contains("built on the fresh render of source B from turn 1", text);
            Assert.Contains("REFINE render, source A: the source returned an image (1024x1024 pixels); it is attached image #1", text);
            Assert.Contains("REFINE render, source B: the source returned an image (1024x1024 pixels); it is attached image #2", text);
            Assert.Contains("FRESH render, source A: the source FAILED", text);
            Assert.Contains("FRESH render, source B: the source returned an image (1024x1024 pixels); it is attached image #3", text);
            Assert.Contains("Best so far: turn 1 fresh render of source B (score 8.5)", text);
            Assert.Contains("JSON", text);
            Assert.True(text.IndexOf("REFINE render, source B", System.StringComparison.Ordinal) < text.IndexOf("FRESH render, source A", System.StringComparison.Ordinal));
        }

        [Fact]
        public void ObjectionNamesTheBestSource()
        {
            var text = UiGoalLoopProtocol.BuildObjectionText(2, 6, 1, 8.5, 8, protocolVersion: 5, bestVariant: "fresh", bestSource: "B");
            Assert.Contains("on the source holding the best result", text);
            Assert.Contains("turn 1 (fresh render of source B) (score 8.5)", text);
            Assert.Contains("latest refine render of source B, turn 2, scored 8", text);
        }

        // ---- planner ----

        private static UiGoalLoopEntry Entry(int index, int turn, string kind, string? variant = null, string? source = null, string text = "t")
        {
            var e = new UiGoalLoopEntry { Index = index, Turn = turn, Kind = kind, Text = text };
            if (kind == UiGoalLoopKinds.RenderRequest || kind == UiGoalLoopKinds.RenderResult)
            {
                var key = source == "A" ? "gpt2" : "grok-web";
                e.Render = new UiGoalLoopRenderData { Variant = variant, Source = source, GeneratorKey = key, Ok = kind == UiGoalLoopKinds.RenderResult };
            }
            return e;
        }

        private static UiGoalLoopEntry Reply(int index, int turn, string kind, string decision, string? prompt, string? fresh,
            (string Variant, string Source, double Score)[]? scores, (string Variant, string Source)? continueFrom)
        {
            var e = Entry(index, turn, kind, text: "{...}");
            e.Manager = new UiGoalLoopManagerData
            {
                Model = "m",
                Parsed = new UiGoalLoopManagerReply
                {
                    Reasoning = "r",
                    Decision = decision,
                    Prompt = prompt,
                    FreshPrompt = fresh,
                    ContinueFrom = continueFrom?.Variant,
                    ContinueFromSource = continueFrom?.Source,
                    DoneStatement = decision == "done" ? "done" : null,
                    GoalKind = UiGoalLoopGoalKinds.OpenEnded,
                    RenderEvaluations = scores?.Select(s => new UiGoalLoopScoredRender(s.Variant, s.Source, new UiGoalLoopEvaluation { Score = s.Score, Assessment = "a" })).ToList(),
                },
            };
            return e;
        }

        private static void AddTurn(List<UiGoalLoopEntry> entries, int turn, string refinePrompt, string freshPrompt, params string[] renderedOnly)
        {
            var i = entries.Count;
            foreach (var (variant, prompt) in new[] { ("refine", refinePrompt), ("fresh", freshPrompt) })
            {
                foreach (var source in new[] { "A", "B" })
                {
                    entries.Add(Entry(i++, turn, UiGoalLoopKinds.RenderRequest, variant, source, prompt));
                }
            }
            foreach (var variant in new[] { "refine", "fresh" })
            {
                foreach (var source in new[] { "A", "B" })
                {
                    if (renderedOnly.Length == 0 || renderedOnly.Contains($"{variant}/{source}"))
                    {
                        entries.Add(Entry(i++, turn, UiGoalLoopKinds.RenderResult, variant, source));
                    }
                }
            }
        }

        // Turn 1 fully rendered and reviewed: best is fresh/B 8.5; continue from it.
        private static List<UiGoalLoopEntry> TwoSourceLoop()
        {
            var entries = new List<UiGoalLoopEntry>
            {
                Entry(0, 0, UiGoalLoopKinds.Goal, text: "as many fish as possible"),
                Reply(1, 1, UiGoalLoopKinds.Design, "render", "grid 20", "reef 20", null, null),
            };
            AddTurn(entries, 1, "grid 20", "reef 20");
            entries.Add(Entry(entries.Count, 1, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(entries.Count, 1, UiGoalLoopKinds.Review, "render", "reef 30", "tanks 30",
                new[] { ("refine", "A", 7d), ("refine", "B", 8d), ("fresh", "A", 6d), ("fresh", "B", 8.5) }, ("fresh", "B")));
            return entries;
        }

        [Fact]
        public void ADesignPlansBothPromptsOnEverySource()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(TwoSourceLoop().Take(2).ToList(), 6, null, 5, TwoSources);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(4, step.Renders.Count);
            Assert.Equal(new[] { ("refine", "A", "gpt2", "grid 20"), ("refine", "B", "grok-web", "grid 20"), ("fresh", "A", "gpt2", "reef 20"), ("fresh", "B", "grok-web", "reef 20") },
                step.Renders.Select(r => (r.Variant!, r.Source!, r.GeneratorKey!, r.Prompt)));
            Assert.Throws<System.ArgumentException>(() => UiGoalLoopPlanner.DetermineNextStep(TwoSourceLoop().Take(2).ToList(), 6, null, 5, null));
        }

        [Fact]
        public void OnlyTheMissingSourceRenderIsReRendered()
        {
            var entries = new List<UiGoalLoopEntry>
            {
                Entry(0, 0, UiGoalLoopKinds.Goal),
                Reply(1, 1, UiGoalLoopKinds.Design, "render", "grid 20", "reef 20", null, null),
            };
            AddTurn(entries, 1, "grid 20", "reef 20", "refine/A", "fresh/A", "fresh/B");
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, null, 5, TwoSources);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            var owed = Assert.Single(step.Renders);
            Assert.Equal(("refine", "B", "grok-web", 3), (owed.Variant, owed.Source, owed.GeneratorKey, owed.ReplaceIndex));
            entries.Add(Entry(entries.Count, 1, UiGoalLoopKinds.RenderResult, "refine", "B"));
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, UiGoalLoopPlanner.DetermineNextStep(entries, 6, null, 5, TwoSources).Kind);
        }

        [Fact]
        public void BestReviewCarriesTheSourceAndDoneNeedsThatSourceToDegrade()
        {
            var entries = TwoSourceLoop();
            Assert.Equal((1, "fresh", "B", 8.5), UiGoalLoopPlanner.BestReview(entries));
            var next = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, 5, TwoSources);
            Assert.Equal(2, next.Turn);
            Assert.Equal(4, next.Renders.Count);

            // Turn 2: A's refine collapses (3) but B's refine improves (9): no
            // limit shown for the best source.
            AddTurn(entries, 2, "reef 30", "tanks 30");
            entries.Add(Entry(entries.Count, 2, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(entries.Count, 2, UiGoalLoopKinds.Review, "done", null, null,
                new[] { ("refine", "A", 3d), ("refine", "B", 9d), ("fresh", "A", 4d), ("fresh", "B", 5d) }, null));
            Assert.Equal((2, "refine", "B", 9d), UiGoalLoopPlanner.BestReview(entries));
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, 5, TwoSources);
            Assert.Equal(UiGoalLoopStepKind.Objection, step.Kind);
            Assert.Contains("of source B", step.Reason);
            Assert.Equal(9d, UiGoalLoopPlanner.LatestRefineScore(entries, "B"));
            Assert.Equal(3d, UiGoalLoopPlanner.LatestRefineScore(entries, "A"));

            // Turn 3: A's refine scores 2 (irrelevant: not the best source);
            // B's refine scores 9 again — equal is not below. Still barred.
            entries.Add(Entry(entries.Count, 2, UiGoalLoopKinds.Objection));
            entries.Add(Reply(entries.Count, 2, UiGoalLoopKinds.Review, "render", "reef 45", "mural 45",
                new[] { ("refine", "A", 3d), ("refine", "B", 9d), ("fresh", "A", 4d), ("fresh", "B", 5d) }, ("refine", "B")));
            AddTurn(entries, 3, "reef 45", "mural 45");
            entries.Add(Entry(entries.Count, 3, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(entries.Count, 3, UiGoalLoopKinds.Review, "done", null, null,
                new[] { ("refine", "A", 2d), ("refine", "B", 9d), ("fresh", "A", 1d), ("fresh", "B", 2d) }, null));
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));

            // Turn 4: B's refine degrades to 7 → the best source's limit is demonstrated.
            entries.Add(Entry(entries.Count, 3, UiGoalLoopKinds.Objection));
            entries.Add(Reply(entries.Count, 3, UiGoalLoopKinds.Review, "render", "reef 60", "atlas 60",
                new[] { ("refine", "A", 2d), ("refine", "B", 9d), ("fresh", "A", 1d), ("fresh", "B", 2d) }, ("refine", "B")));
            AddTurn(entries, 4, "reef 60", "atlas 60");
            entries.Add(Entry(entries.Count, 4, UiGoalLoopKinds.ReviewRequest));
            entries.Add(Reply(entries.Count, 4, UiGoalLoopKinds.Review, "done", null, null,
                new[] { ("refine", "A", 2d), ("refine", "B", 7d), ("fresh", "A", 1d), ("fresh", "B", 9.5) }, null));
            // A fresh 9.5 on B is now the best; nothing later degraded it.
            Assert.Equal((4, "fresh", "B", 9.5), UiGoalLoopPlanner.BestReview(entries));
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            // Rewrite the fresh B score to 5: then B's refine 7 < best 9 (turn 2 refine B) → permitted.
            entries[^1].Manager!.Parsed!.RenderEvaluations![3] = new UiGoalLoopScoredRender("fresh", "B", new UiGoalLoopEvaluation { Score = 5, Assessment = "a" });
            Assert.Equal((2, "refine", "B", 9d), UiGoalLoopPlanner.BestReview(entries));
            Assert.True(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            Assert.Equal(UiGoalLoopStepKind.Done, UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded, 5, TwoSources).Kind);
        }

        [Fact]
        public void LoopModelDerivesRendersPerTurnFromItsGenerators()
        {
            var loop = new UiGoalLoop { ProtocolVersion = 5, Generators = TwoSources.ToList() };
            Assert.Equal(4, loop.RendersPerTurn);
            var stored = new UiGoalLoop { ProtocolVersion = 4, GeneratorKey = "gpt2", GeneratorLabel = "gpt-image-2" };
            Assert.Equal(2, stored.RendersPerTurn);
            var only = Assert.Single(stored.GeneratorList());
            Assert.Equal(("gpt2", (string?)null), (only.Key, only.Source));
            Assert.Equal("C", UiGoalLoopSources.Label(2));
            Assert.True(UiGoalLoopSources.IsValid("H"));
            Assert.False(UiGoalLoopSources.IsValid("a"));
            Assert.False(UiGoalLoopSources.IsValid("AB"));
        }
    }
}
