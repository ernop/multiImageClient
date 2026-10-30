using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class GoogleModelUpgradeTests
    {
        private static readonly DateTimeOffset LastFlashIntroductoryMoment = new(2027, 1, 1, 7, 59, 59, TimeSpan.Zero);
        private static readonly DateTimeOffset FlashFullPriceStart = new(2027, 1, 1, 8, 0, 0, TimeSpan.Zero);

        [Fact]
        public void FlashImageTierSendsNanoBanana21AndProIsUnchanged()
        {
            Assert.Equal("gemini-nano-banana-2.1", GoogleGenerator.ModelFor(ImageGeneratorApiType.GoogleNanoBanana));
            Assert.Equal("gemini-3-pro-image", GoogleGenerator.ModelFor(ImageGeneratorApiType.GoogleNanoBananaPro));
            Assert.Equal("Nano Banana 2.1", GeneratorPresentation.UiDisplayName(UiJobRunner.KeyGoogle));
        }

        [Theory]
        [InlineData(null, "0.0411")]
        [InlineData("1K", "0.0411")]
        [InlineData("2K", "0.0579")]
        [InlineData("4K", "0.1209")]
        public void NanoBanana21CostIsImageTokensPlusAThinkingAllowance(string? imageSize, string expected)
        {
            var generator = new GoogleGenerator(
                ImageGeneratorApiType.GoogleNanoBanana, "unused", 1, new MultiClientRunStats(), imageSize: imageSize);
            Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), generator.GetCost());
            generator.Dispose();
        }

        [Fact]
        public void FlashManagerKeepsItsStoredKeyAndRunsGemini38Flash()
        {
            Assert.Equal("manager-gemini-3.5-flash", ManagerCatalog.KeyGeminiFlash);
            var model = ManagerCatalog.Find(ManagerCatalog.KeyGeminiFlash);
            Assert.NotNull(model);
            Assert.Equal("gemini-3.8-flash", model.Model);
            Assert.Equal("Gemini 3.8 Flash", model.Label);
            Assert.Equal(GeminiVisionDescriber.DefaultModel, model.Model);
            Assert.Equal(64_000, model.MaxOutputTokens);
        }

        [Fact]
        public void FlashPriceDoublesAtPacificMidnightOnJanuaryFirst()
        {
            var model = ManagerCatalog.Find(ManagerCatalog.KeyGeminiFlash)!;
            Assert.Equal(((decimal?)0.75m, (decimal?)3.75m), model.PricesAt(LastFlashIntroductoryMoment));
            Assert.Equal(((decimal?)1.50m, (decimal?)7.50m), model.PricesAt(FlashFullPriceStart));
        }

        [Fact]
        public void FlashDescribeEstimateFollowsThePriceChange()
        {
            Assert.Equal(0.006m, UiJobRunner.FlashDescribeCostEstimate(LastFlashIntroductoryMoment));
            Assert.Equal(0.012m, UiJobRunner.FlashDescribeCostEstimate(FlashFullPriceStart));
        }
    }
}
