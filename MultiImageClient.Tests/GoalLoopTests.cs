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
            Assert.Equal(3, UiGoalLoopProtocol.Version);
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
        private const string FirstDesign =
            "{\"reasoning\":\"Start with a literal reading of the goal.\",\"goalKind\":\"bounded\",\"evaluation\":null,"
            + "\"decision\":\"render\",\"designNotes\":\"Literal first pass.\","
            + "\"prompt\":\"A bright daytime photo of a red bicycle leaning on a white wall.\","
            + "\"doneStatement\":null,\"bestTurn\":null}";

        [Fact]
        public void FirstReplyParsesWithoutEvaluation()
        {
            var reply = UiGoalLoopProtocol.ParseManagerReply(FirstDesign, expectEvaluation: false);
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
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false));
            Assert.Contains("nothing has been rendered yet", ex.Message);
        }

        [Fact]
        public void FirstReplyMustRender()
        {
            var raw = "{\"reasoning\":\"x\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"done\",\"doneStatement\":\"nothing to do\"}";
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false));
            Assert.Contains("first reply must have decision \"render\"", ex.Message);
        }

        [Fact]
        public void ReviewReplyRequiresEvaluationWithScoreAndGoalMet()
        {
            var raw = "{\"reasoning\":\"Looks close.\",\"decision\":\"render\",\"prompt\":\"again\"}";
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true));
            Assert.Contains("\"evaluation\" must be an object", ex.Message);

            var missingGoalMet = "{\"reasoning\":\"r\",\"decision\":\"render\",\"prompt\":\"p\","
                + "\"evaluation\":{\"score\":7,\"assessment\":\"ok\"}}";
            ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(missingGoalMet, expectEvaluation: true));
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
            var reply = UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true);
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
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: true));
            Assert.Contains("within 0-10", ex.Message);
        }

        [Fact]
        public void RenderWithoutPromptAndDoneWithoutStatementAreRejected()
        {
            var noPrompt = "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"decision\":\"render\",\"evaluation\":null}";
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(noPrompt, expectEvaluation: false));
            var noStatement = "{\"reasoning\":\"r\",\"decision\":\"done\","
                + "\"evaluation\":{\"score\":9,\"goalMet\":true,\"assessment\":\"a\"}}";
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(noStatement, expectEvaluation: true));
        }

        [Fact]
        public void ProseIsNeverAcceptedAsADesign()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply("Sure! Here is a prompt: a red bicycle.", expectEvaluation: false));
            Assert.Contains("did not follow the required JSON contract", ex.Message);
            Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply("   ", expectEvaluation: false));
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
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false));
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
                () => UiGoalLoopProtocol.ParseManagerReply(raw, expectEvaluation: false));
            Assert.Contains("\"goalKind\" must be", ex.Message);
            var open = UiGoalLoopProtocol.ParseManagerReply(
                FirstDesign.Replace("\"goalKind\":\"bounded\"", "\"goalKind\":\"Open-Ended\""), expectEvaluation: false);
            Assert.Equal(UiGoalLoopGoalKinds.OpenEnded, open.GoalKind);
        }

        [Fact]
        public void DoneIsAContractViolationAfterAnObjection()
        {
            var done = "{\"reasoning\":\"r\",\"decision\":\"done\",\"doneStatement\":\"d\","
                + "\"evaluation\":{\"score\":9,\"goalMet\":true,\"assessment\":\"a\"}}";
            Assert.Equal("done", UiGoalLoopProtocol.ParseManagerReply(done, expectEvaluation: true).Decision);
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopProtocol.ParseManagerReply(done, expectEvaluation: true, permitDone: false));
            Assert.Contains("barred", ex.Message);
        }

        [Fact]
        public void GoalMessageCarriesTheOperatorsClassificationOrAsksForOne()
        {
            var auto = UiGoalLoopProtocol.BuildGoalMessage("as many fish as possible", 6);
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
            var text = UiGoalLoopProtocol.BuildObjectionText(2, 2, bestTurn: 2, bestScore: 9, latestScore: 9);
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
            var step = UiGoalLoopPlanner.DetermineNextStep(new[] { Entry(0, 0, UiGoalLoopKinds.Goal) }, 6);
            Assert.Equal(UiGoalLoopStepKind.AskManagerDesign, step.Kind);
            Assert.Equal(1, step.Turn);
        }

        [Fact]
        public void DesignLeadsToRenderOfItsPrompt()
        {
            var entries = OneFullTurn().Take(2).ToList();
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(1, step.Turn);
            Assert.Equal("prompt one", step.Prompt);
        }

        [Fact]
        public void UnrenderedRequestIsRenderedAgainWithItsOwnText()
        {
            var entries = OneFullTurn().Take(3).ToList();
            entries[2].Text = "edited prompt";
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("edited prompt", step.Prompt);
        }

        [Fact]
        public void RenderResultAndReviewRequestBothAskForReview()
        {
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview,
                UiGoalLoopPlanner.DetermineNextStep(OneFullTurn().Take(4).ToList(), 6).Kind);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview,
                UiGoalLoopPlanner.DetermineNextStep(OneFullTurn().Take(5).ToList(), 6).Kind);
        }

        [Fact]
        public void ReviewWantingAnotherRenderAdvancesTheTurn()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn(), 6);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Equal("prompt two", step.Prompt);
        }

        [Fact]
        public void ReviewSayingDoneEndsTheLoop()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn("done"), 6);
            Assert.Equal(UiGoalLoopStepKind.Done, step.Kind);
            Assert.Equal("done", step.Reason);
        }

        [Fact]
        public void TurnBudgetIsEnforcedOnTheNextRender()
        {
            var step = UiGoalLoopPlanner.DetermineNextStep(OneFullTurn(), maxTurns: 1);
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
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, step.Kind);
            Assert.Equal(1, step.Turn);
        }

        [Fact]
        public void EmptyEntriesAreInvalid()
        {
            Assert.Equal(UiGoalLoopStepKind.Invalid,
                UiGoalLoopPlanner.DetermineNextStep(new List<UiGoalLoopEntry>(), 6).Kind);
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
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded);
            Assert.Equal(UiGoalLoopStepKind.Objection, step.Kind);
            Assert.Equal(2, step.Turn);
            Assert.Contains("no later render scored below", step.Reason);
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            // The budget being exhausted (maxTurns 2) changes nothing.
            Assert.Equal(UiGoalLoopStepKind.Objection,
                UiGoalLoopPlanner.DetermineNextStep(entries, 30, UiGoalLoopGoalKinds.OpenEnded).Kind);
        }

        [Fact]
        public void TheSameLoopIsDoneWhenBoundedOrUnclassified()
        {
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(FishLoop(), 2, UiGoalLoopGoalKinds.Bounded).Kind);
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(FishLoop(), 2, null).Kind);
        }

        [Fact]
        public void DoneIsPermittedOnceALaterRenderScoredBelowTheBest()
        {
            // Turn 2 overshot (30 fish degraded to 7): the limit is shown.
            var entries = FishLoop(turn2Score: 7);
            Assert.True(UiGoalLoopPlanner.IsDonePermitted(entries, UiGoalLoopGoalKinds.OpenEnded));
            Assert.Equal(UiGoalLoopStepKind.Done,
                UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded).Kind);
            // An equal score is not degradation.
            Assert.False(UiGoalLoopPlanner.IsDonePermitted(FishLoop(turn2Score: 9), UiGoalLoopGoalKinds.OpenEnded));
        }

        [Fact]
        public void BestReviewIsTheFirstTurnHoldingTheMaximum()
        {
            var best = UiGoalLoopPlanner.BestReview(FishLoop(turn2Score: 9));
            Assert.Equal((1, 9d), best);
            Assert.Equal((2, 9.5d), UiGoalLoopPlanner.BestReview(FishLoop(turn2Score: 9.5)));
        }

        [Fact]
        public void AfterTheObjectionTheReviewIsAskedAgainAndARenderProceeds()
        {
            var entries = FishLoop();
            entries.Add(Entry(10, 2, UiGoalLoopKinds.Objection, "not accepted"));
            var step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded);
            Assert.Equal(UiGoalLoopStepKind.AskManagerReview, step.Kind);
            Assert.Equal(2, step.Turn);
            entries.Add(ManagerEntry(11, 2, UiGoalLoopKinds.Review, "render", "45 fish", 9));
            step = UiGoalLoopPlanner.DetermineNextStep(entries, 6, UiGoalLoopGoalKinds.OpenEnded);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal(3, step.Turn);
            Assert.Equal("45 fish", step.Prompt);
            // With the budget spent the pending prompt waits for the operator.
            Assert.Equal(UiGoalLoopStepKind.Exhausted,
                UiGoalLoopPlanner.DetermineNextStep(entries, 2, UiGoalLoopGoalKinds.OpenEnded).Kind);
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
                        "{\"reasoning\":\"r\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"prompt one\"}", false),
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
            var step = UiGoalLoopPlanner.DetermineNextStep(copies, 6);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("prompt one, but at dusk", step.Prompt);
        }

        [Fact]
        public void EditingAManagerReplyReparsesAndDropsProviderAccounting()
        {
            var newReply = "{\"reasoning\":\"operator rewrite\",\"goalKind\":\"bounded\",\"evaluation\":null,\"decision\":\"render\",\"prompt\":\"prompt zero\"}";
            var copies = UiGoalLoopPlanner.BuildForkEntries(Parent(), 1, newReply, out _);
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
            var step = UiGoalLoopPlanner.DetermineNextStep(copies, 6);
            Assert.Equal(UiGoalLoopStepKind.Render, step.Kind);
            Assert.Equal("prompt zero", step.Prompt);
        }

        [Fact]
        public void EditedManagerReplyMustStillFollowTheContract()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => UiGoalLoopPlanner.BuildForkEntries(Parent(), 1, "just make it bluer", out _));
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
}
