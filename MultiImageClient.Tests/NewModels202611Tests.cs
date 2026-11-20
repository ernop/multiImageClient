using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BFLAPIClient;
using IdeogramAPIClient;
using RecraftAPIClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class NewModels202611Tests
    {
        private static readonly DateTimeOffset LastIntroductoryMoment = new(2026, 12, 1, 7, 59, 59, TimeSpan.Zero);
        private static readonly DateTimeOffset FullPriceStart = new(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);

        [Fact]
        public void GptImage26SupportsExtendedQualityAndMultiInput()
        {
            Assert.Equal("gpt-image-2.6-sunburst", GptImage2Generator.Sunburst26ModelId);
            Assert.Equal("gpt-image-2.6-flare", GptImage2Generator.Flare26ModelId);
            Assert.True(GptImage2Generator.ModelSupportsQuality(GptImage2Generator.Sunburst26ModelId, OpenAIGPTImageOneQuality.xhigh));
            Assert.True(GptImage2Generator.ModelSupportsQuality(GptImage2Generator.Flare26ModelId, OpenAIGPTImageOneQuality.max));
            Assert.False(GptImage2Generator.ModelSupportsQuality(GptImage2Generator.DefaultModelId, OpenAIGPTImageOneQuality.xhigh));
            Assert.Contains(UiJobRunner.KeyGpt26Sunburst, UiJobRunner.ImageCapableKeys);
            Assert.Contains(UiJobRunner.KeyGpt26Flare, UiJobRunner.ImageCapableKeys);
            Assert.Contains(UiJobRunner.KeyGpt26Sunburst, UiJobRunner.SketchCapableKeys);
            Assert.Contains(UiJobRunner.KeyGpt26Flare, UiJobRunner.SketchCapableKeys);
            Assert.Equal(32_000, UiJobRunner.MaxPromptCharsFor(UiJobRunner.KeyGpt26Sunburst, false));
            Assert.Equal(32_000, UiJobRunner.MaxPromptCharsFor(UiJobRunner.KeyGpt26Flare, true));
        }

        [Fact]
        public void GptImage1IsMarkedRetiredByOpenAi()
        {
            var runner = new UiJobRunner(
                new Settings
                {
                    OpenAIApiKey = "sk-test",
                    ImageDownloadBaseFolder = Path.GetTempPath(),
                    EnableGenerationArchive = false,
                },
                new MultiClientRunStats(),
                new RunOptions());

            var problem = runner.DescribeAvailabilityProblem(UiJobRunner.KeyGpt1);
            Assert.NotNull(problem);
            Assert.Contains("2026-10-23", problem);
            Assert.Null(runner.DescribeAvailabilityProblem(UiJobRunner.KeyGpt1Mini));
        }

        [Fact]
        public void IdeogramV46SizesAndPricingMatchVerifiedQuotes()
        {
            Assert.Equal("", UiShapeMapping.IdeogramV46Size("auto", "high"));
            Assert.Equal("2048x2048", UiShapeMapping.IdeogramV46Size("square", "high"));
            Assert.Equal("auto_4k", UiShapeMapping.IdeogramV46Size("auto", "max"));
            Assert.Equal("4096x4096", UiShapeMapping.IdeogramV46Size("square", "max"));
            Assert.Equal("4800x3200", UiShapeMapping.IdeogramV46Size("landscape", "max"));
            Assert.Equal("3200x4800", UiShapeMapping.IdeogramV46Size("portrait", "max"));
            Assert.Equal("5120x2880", UiShapeMapping.IdeogramV46Size("wide", "max"));
            Assert.Equal("2880x5120", UiShapeMapping.IdeogramV46Size("tall", "max"));

            Assert.True(IdeogramClient.IsIdeogramV464KSize("auto_4k"));
            Assert.True(IdeogramClient.IsIdeogramV464KSize("5120x2880"));
            Assert.False(IdeogramClient.IsIdeogramV464KSize("2048x2048"));

            var gen2k = new IdeogramV45Generator("key", 1, "2048x2048", IdeogramV45Quality.medium, new MultiClientRunStats(), "v46", apiType: ImageGeneratorApiType.IdeogramV46);
            Assert.Equal(0.06m, gen2k.GetCost());
            var gen4kLow = new IdeogramV45Generator("key", 1, "auto_4k", IdeogramV45Quality.low, new MultiClientRunStats(), "v46", apiType: ImageGeneratorApiType.IdeogramV46);
            Assert.Equal(0.06m, gen4kLow.GetCost());
            var gen4kMed = new IdeogramV45Generator("key", 1, "4096x4096", IdeogramV45Quality.medium, new MultiClientRunStats(), "v46", apiType: ImageGeneratorApiType.IdeogramV46);
            Assert.Equal(0.12m, gen4kMed.GetCost());
            var gen4kHigh = new IdeogramV45Generator("key", 1, "5120x2880", IdeogramV45Quality.high, new MultiClientRunStats(), "v46", apiType: ImageGeneratorApiType.IdeogramV46);
            Assert.Equal(0.20m, gen4kHigh.GetCost());
            var editHigh = new IdeogramV45Generator("key", 1, "", IdeogramV45Quality.high, new MultiClientRunStats(), "v46", inputImagePaths: new[] { "a.png" }, apiType: ImageGeneratorApiType.IdeogramV46);
            Assert.Equal(0.22m, editHigh.GetCost());
        }

        [Fact]
        public void BflFlux3UsesStrictOpenApiSchemaAndCapsReferenceEdits()
        {
            Assert.Equal("1k", UiShapeMapping.BflFlux3Resolution("standard", "square", hasInputImage: false));
            Assert.Equal("2k", UiShapeMapping.BflFlux3Resolution("high", "square", hasInputImage: false));
            Assert.Equal("4k", UiShapeMapping.BflFlux3Resolution("max", "square", hasInputImage: false));
            Assert.Equal("match_input", UiShapeMapping.BflFlux3Resolution("high", "auto", hasInputImage: true));
            Assert.Equal("2k", UiShapeMapping.BflFlux3Resolution("max", "auto", hasInputImage: true));
            Assert.Equal("1k", UiShapeMapping.BflFlux3Resolution("standard", "auto", hasInputImage: true));

            var request = new Flux3Request
            {
                Prompt = "A bright wooden lighthouse.",
                AspectRatio = "16:9",
                Resolution = "2k",
            };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                request,
                new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            using var doc = JsonDocument.Parse(json);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "prompt", "images", "aspect_ratio", "resolution",
                "width", "height", "safety_tolerance", "grounding", "version", "user",
            };
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                Assert.Contains(prop.Name, allowed);
            }
            Assert.False(doc.RootElement.TryGetProperty("output_format", out _));
            Assert.False(doc.RootElement.TryGetProperty("seed", out _));

            var flux3 = new BFLGenerator(ImageGeneratorApiType.BFLFlux3, "key", 1, "1:1", false, 1024, 1024, new MultiClientRunStats(), "f3", flux3Resolution: "2k");
            Assert.Equal(0.100m, flux3.GetCost());
            var flux3Max = new BFLGenerator(ImageGeneratorApiType.BFLFlux3, "key", 1, "1:1", false, 1024, 1024, new MultiClientRunStats(), "f3", flux3Resolution: "4k");
            Assert.Equal(0.607m, flux3Max.GetCost());
            var klein1k = new BFLGenerator(ImageGeneratorApiType.BFLFlux3Klein, "key", 1, "1:1", false, 1024, 1024, new MultiClientRunStats(), "f3k", flux3Resolution: "1k");
            Assert.Equal(0.009m, klein1k.GetCost());
            var klein4k = new BFLGenerator(ImageGeneratorApiType.BFLFlux3Klein, "key", 1, "1:1", false, 1024, 1024, new MultiClientRunStats(), "f3k", flux3Resolution: "4k");
            Assert.Equal(0.036m, klein4k.GetCost());
        }

        [Fact]
        public void RecraftV41FlashV2IsTextOnlyWithVerifiedPricingAndLimit()
        {
            Assert.DoesNotContain(UiJobRunner.KeyRecraftV41Flash, UiJobRunner.ImageCapableKeys);
            Assert.Equal(10_000, UiJobRunner.MaxPromptCharsFor(UiJobRunner.KeyRecraftV41Flash, false));
            Assert.Equal(10_000, RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_flash_v2));

            var gen = new RecraftGenerator(
                "key", 1, RecraftImageSize._1024x1024, RecraftStyle.any, null, null, null,
                new MultiClientRunStats(), "flash", model: RecraftModel.recraftv4_1_flash_v2);
            Assert.Equal(0.007m, gen.GetCost());
            Assert.Equal(ImageGeneratorApiType.RecraftV41FlashV2, gen.ApiType);

            Assert.Throws<NotSupportedException>(() => new RecraftGenerator(
                "key", 1, RecraftImageSize._1024x1024, RecraftStyle.any, null, null, null,
                new MultiClientRunStats(), "flash", model: RecraftModel.recraftv4_1_flash_v2, inputImagePath: "x.png"));
        }

        [Fact]
        public void ClaudeVisionDescriberUsesBetweenToolsThinkingForSonnet55AndOmitsForFable()
        {
            byte[] pngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            using var sonnet55 = JsonDocument.Parse(JsonSerializer.Serialize(
                ClaudeVisionDescriber.BuildPayload("claude-sonnet-5-5", pngHeader, "Describe.", 1200, 0.2f)));
            Assert.Equal("between_tools", sonnet55.RootElement.GetProperty("thinking").GetProperty("type").GetString());

            using var sonnet5 = JsonDocument.Parse(JsonSerializer.Serialize(
                ClaudeVisionDescriber.BuildPayload("claude-sonnet-5", pngHeader, "Describe.", 1200, 0.2f)));
            Assert.Equal("disabled", sonnet5.RootElement.GetProperty("thinking").GetProperty("type").GetString());

            using var fable52 = JsonDocument.Parse(JsonSerializer.Serialize(
                ClaudeVisionDescriber.BuildPayload("claude-fable-5-2", pngHeader, "Describe.", 1200, 0.2f)));
            Assert.False(fable52.RootElement.TryGetProperty("thinking", out _));
            Assert.Equal(16_000, fable52.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void GeminiVisionDescriberCapsNeonAt64kAndCarbonAt32k()
        {
            var neon = GeminiVisionDescriber.GenerationConfig(GeminiVisionDescriber.NeonModel, 1200, 0.2f, requestJsonOutput: true);
            Assert.Equal(64_000, neon["maxOutputTokens"]);
            Assert.False(neon.ContainsKey("temperature"));

            var carbon = GeminiVisionDescriber.GenerationConfig(GeminiVisionDescriber.CarbonModel, 1200, 0.2f, requestJsonOutput: true);
            Assert.Equal(32_768, carbon["maxOutputTokens"]);
            Assert.False(carbon.ContainsKey("temperature"));

            var hydrogen = GeminiVisionDescriber.GenerationConfig(GeminiVisionDescriber.HydrogenModel, 1200, 0.2f, requestJsonOutput: true);
            Assert.Equal(64_000, hydrogen["maxOutputTokens"]);
        }

        [Fact]
        public void NewCatalogModelsHaveExpectedPricingAndCaps()
        {
            var neon = ManagerCatalog.Find(ManagerCatalog.KeyGemini4Neon)!;
            Assert.Equal("gemini-4-neon", neon.Model);
            Assert.Equal(64_000, neon.MaxOutputTokens);
            Assert.Equal(((decimal?)3m, (decimal?)15m), neon.PricesAt(LastIntroductoryMoment));

            var carbon = ManagerCatalog.Find(ManagerCatalog.KeyGemini4Carbon)!;
            Assert.Equal("gemini-4-carbon", carbon.Model);
            Assert.Equal(32_768, carbon.MaxOutputTokens);
            Assert.Equal(((decimal?)2m, (decimal?)10m), carbon.PricesAt(LastIntroductoryMoment));
            Assert.Equal(((decimal?)4m, (decimal?)20m), carbon.PricesAt(FullPriceStart));

            var hydrogen = ManagerCatalog.Find(ManagerCatalog.KeyGemini4Hydrogen)!;
            Assert.Equal("gemini-4-hydrogen", hydrogen.Model);
            Assert.Equal(64_000, hydrogen.MaxOutputTokens);
            Assert.Equal(((decimal?)1.50m, (decimal?)7.50m), hydrogen.PricesAt(LastIntroductoryMoment));

            var astra62 = ManagerCatalog.Find(ManagerCatalog.KeyGpt62Astra)!;
            Assert.Equal("gpt-6.2-astra", astra62.Model);
            Assert.Equal(4.80m, ManagerCatalog.EstimateCostUsd(astra62, 100_000, 100_000, LastIntroductoryMoment));
            Assert.Equal(10.80m, ManagerCatalog.EstimateCostUsd(astra62, 300_000, 100_000, LastIntroductoryMoment));

            var sol62 = ManagerCatalog.Find(ManagerCatalog.KeyGpt62Sol)!;
            Assert.Equal("gpt-6.2-sol", sol62.Model);
            Assert.Equal(((decimal?)2m, (decimal?)10m), sol62.PricesAt(LastIntroductoryMoment));

            var fable52 = ManagerCatalog.Find(ManagerCatalog.KeyClaudeFable52)!;
            Assert.Equal("claude-fable-5-2", fable52.Model);
            Assert.Equal(((decimal?)8m, (decimal?)40m), fable52.PricesAt(LastIntroductoryMoment));

            var grok47 = ManagerCatalog.Find(ManagerCatalog.KeyGrok47)!;
            Assert.Equal("grok-4.7", grok47.Model);
            Assert.Equal(((decimal?)2m, (decimal?)6m), grok47.PricesAt(LastIntroductoryMoment));

            Assert.Same(neon, ManagerCatalog.FindByKeyOrModel(ManagerCatalog.KeyGemini4Neon));
            Assert.Same(neon, ManagerCatalog.FindByKeyOrModel("gemini-4-neon"));
            Assert.Same(neon, PromptSplit.ResolveModel(ManagerCatalog.KeyGemini4Neon));
            Assert.Null(PromptSplit.ResolveModel("unknown-split-model"));
        }

        [Fact]
        public void PromptSplitActionHintRoutesToActualProvider()
        {
            var outcome = new PromptSplitOutcome { Error = "HTTP 402 Payment Required" };
            var googleHint = PromptSplit.ActionHint(outcome, "google");
            Assert.NotNull(googleHint);
            Assert.Equal("https://aistudio.google.com/billing", googleHint.Url);

            var openAiHint = PromptSplit.ActionHint(outcome, "openai");
            Assert.NotNull(openAiHint);
            Assert.Equal("https://platform.openai.com/settings/organization/billing/overview", openAiHint.Url);

            var declined = new PromptSplitOutcome { Error = "Declined.", Declined = true };
            Assert.Null(PromptSplit.ActionHint(declined, "google"));
        }
    }
}
