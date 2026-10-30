using System.Text.Json;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GeminiArgonTests
    {
        private static readonly DateTimeOffset LastIntroductoryMoment = new(2026, 12, 1, 7, 59, 59, TimeSpan.Zero);
        private static readonly DateTimeOffset FullPriceStart = new(2026, 12, 1, 8, 0, 0, TimeSpan.Zero);

        [Fact]
        public void ArgonManagerUsesExactGoogleIdentityAndNonStreamingCap()
        {
            var model = ManagerCatalog.Find(ManagerCatalog.KeyGemini4Argon);
            Assert.NotNull(model);
            Assert.Equal("gemini-4-argon", model.Model);
            Assert.Equal("Gemini 4 Argon", model.Label);
            Assert.Equal("google", model.Provider);
            Assert.Equal(nameof(Settings.GoogleGeminiApiKey), model.SettingsKeyName);
            Assert.Same(ManagerCatalog.GeminiLimits, model.ImageLimits);
            Assert.Equal(32_768, model.MaxOutputTokens);
            Assert.NotNull(ManagerCatalog.DescribeAvailabilityProblem(model, new Settings()));
        }

        [Fact]
        public void OtherManagersKeepTheSharedOutputCap()
        {
            Assert.All(
                ManagerCatalog.All.Where(m => m.Key != ManagerCatalog.KeyGemini4Argon),
                m => Assert.Equal(64_000, m.MaxOutputTokens));
        }

        [Fact]
        public void ArgonPriceDoublesAtPacificMidnightOnDecemberFirst()
        {
            var model = ManagerCatalog.Find(ManagerCatalog.KeyGemini4Argon)!;
            Assert.Equal(((decimal?)2m, (decimal?)10m), model.PricesAt(LastIntroductoryMoment));
            Assert.Equal(((decimal?)4m, (decimal?)20m), model.PricesAt(FullPriceStart));
            Assert.Equal(12m, ManagerCatalog.EstimateCostUsd(model, 1_000_000, 1_000_000, LastIntroductoryMoment));
            Assert.Equal(24m, ManagerCatalog.EstimateCostUsd(model, 1_000_000, 1_000_000, FullPriceStart));
        }

        [Fact]
        public void ArgonDescribeEstimateFollowsThePriceChange()
        {
            Assert.Equal(0.016m, UiJobRunner.ArgonDescribeCostEstimate(LastIntroductoryMoment));
            Assert.Equal(0.032m, UiJobRunner.ArgonDescribeCostEstimate(FullPriceStart));
        }

        [Theory]
        [InlineData("{\"promptTokenCount\":900,\"candidatesTokenCount\":120,\"thoughtsTokenCount\":3000}", 3120)]
        [InlineData("{\"promptTokenCount\":900,\"candidatesTokenCount\":120}", 120)]
        [InlineData("{\"promptTokenCount\":900}", 0)]
        public void GeminiOutputTokensIncludeThinking(string usageJson, int expected)
        {
            using var usage = JsonDocument.Parse(usageJson);
            Assert.Equal(expected, GeminiManagerClient.OutputTokens(usage.RootElement));
        }

        [Fact]
        public void GeminiOutputTokensAreUnknownWithoutUsage()
        {
            Assert.Null(GeminiManagerClient.OutputTokens(default));
        }

        [Fact]
        public void ArgonDescribeSendsLowThinkingTheFullCapAndNoTemperature()
        {
            var config = GeminiVisionDescriber.GenerationConfig(GeminiVisionDescriber.ArgonModel, 1200, 0.2f, requestJsonOutput: true);
            Assert.Equal(32_768, config["maxOutputTokens"]);
            Assert.False(config.ContainsKey("temperature"));
            Assert.Equal("{\"thinkingLevel\":\"low\"}", JsonSerializer.Serialize(config["thinkingConfig"]));
            Assert.Equal("application/json", config["responseMimeType"]);
        }

        [Fact]
        public void FlashDescribeSendsLowThinkingItsBudgetAndNoTemperature()
        {
            var config = GeminiVisionDescriber.GenerationConfig(GeminiVisionDescriber.DefaultModel, 1200, 0.2f, requestJsonOutput: true);
            Assert.Equal(2224, config["maxOutputTokens"]);
            Assert.False(config.ContainsKey("temperature"));
            Assert.Equal("{\"thinkingLevel\":\"low\"}", JsonSerializer.Serialize(config["thinkingConfig"]));
            Assert.Equal("application/json", config["responseMimeType"]);
        }

        [Fact]
        public void ArgonDescribeTargetIsAGoogleAnalysisTarget()
        {
            Assert.Contains(UiJobRunner.KeyDescribeGeminiArgon, UiJobRunner.DescribeKeys);
            Assert.True(UiJobRunner.IsDescribeKey(UiJobRunner.KeyDescribeGeminiArgon));
            Assert.Equal(
                "Gemini describe (gemini-4-argon) — Google",
                GeneratorPresentation.UiContactSheetLabel(UiJobRunner.KeyDescribeGeminiArgon));
        }
    }
}
