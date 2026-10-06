using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GoalAstraTests
    {
        [Fact]
        public void AstraUsesExactOpenAiIdentityAndSharedImageTransport()
        {
            var model = ManagerCatalog.Find(ManagerCatalog.KeyGpt6Astra);
            Assert.NotNull(model);
            Assert.Equal("gpt-6-astra", model.Model);
            Assert.Equal(nameof(Settings.OpenAIApiKey), model.SettingsKeyName);
            Assert.Same(ManagerCatalog.OpenAiLimits, model.ImageLimits);
            Assert.NotNull(ManagerCatalog.DescribeAvailabilityProblem(model, new Settings()));
        }

        [Theory]
        [InlineData(ManagerCatalog.KeyClaudeOpus55, "claude-opus-5-5", "Opus 5.5", 24.0)]
        [InlineData(ManagerCatalog.KeyClaudeSonnet55, "claude-sonnet-5-5", "Sonnet 5.5", 12.0)]
        public void Claude55ManagersUseExactAnthropicIdentity(string key, string modelId, string label, double usdPerMillionEach)
        {
            var model = ManagerCatalog.Find(key);
            Assert.NotNull(model);
            Assert.Equal(modelId, model.Model);
            Assert.Equal(label, model.Label);
            Assert.Equal(nameof(Settings.AnthropicApiKey), model.SettingsKeyName);
            Assert.Same(ManagerCatalog.AnthropicLimits, model.ImageLimits);
            Assert.Equal((decimal)usdPerMillionEach, ManagerCatalog.EstimateCostUsd(model, 1_000_000, 1_000_000));
        }

        [Fact]
        public void DefaultManagerIsSonnet55()
        {
            Assert.Equal(ManagerCatalog.KeyClaudeSonnet55, ManagerCatalog.DefaultKey);
            Assert.NotNull(ManagerCatalog.Find(ManagerCatalog.DefaultKey));
        }

        [Theory]
        [InlineData(272000, 1000, 2.77)]
        [InlineData(272001, 1000, 5.51502)]
        public void AstraPriceUsesTheLongContextThreshold(int input, int output, double expected)
        {
            Assert.Equal((decimal)expected, ManagerCatalog.EstimateCostUsd(
                ManagerCatalog.Find(ManagerCatalog.KeyGpt6Astra)!, input, output));
        }
    }
}
