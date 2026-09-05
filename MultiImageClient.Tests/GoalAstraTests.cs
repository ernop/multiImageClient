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
        [InlineData(272000, 1000, 2.77)]
        [InlineData(272001, 1000, 5.51502)]
        public void AstraPriceUsesTheLongContextThreshold(int input, int output, double expected)
        {
            Assert.Equal((decimal)expected, ManagerCatalog.EstimateCostUsd(
                ManagerCatalog.Find(ManagerCatalog.KeyGpt6Astra)!, input, output));
        }
    }
}
