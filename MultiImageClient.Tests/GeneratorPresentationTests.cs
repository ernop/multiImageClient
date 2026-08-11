using System;
using System.Linq;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public class GeneratorPresentationTests
    {
        [Fact]
        public void GoogleContactSheetLabelsKeepCatalogProviderAndExactModel()
        {
            Assert.Equal(
                "Nano Banana 2 — Google · gemini-3.1-flash-image",
                GeneratorPresentation.UiContactSheetLabel(UiJobRunner.KeyGoogle, "google ui"));
            Assert.Equal(
                "Nano Banana Pro — Google · gemini-3-pro-image",
                GeneratorPresentation.UiContactSheetLabel(UiJobRunner.KeyGooglePro, "googlepro ui"));
        }

        [Fact]
        public void UiCatalogAndContactSheetMappingsCoverEveryTarget()
        {
            var keys = UiJobRunner.ImageGeneratorKeys
                .Concat(UiJobRunner.DescribeKeys)
                .Append(UiJobRunner.KeyLayoutMap)
                .Append(UiJobRunner.KeyGrokWebVideo)
                .Distinct(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var displayName = GeneratorPresentation.UiDisplayName(key);
                var contactSheetLabel = GeneratorPresentation.UiContactSheetLabel(key);

                Assert.False(string.IsNullOrWhiteSpace(displayName));
                Assert.StartsWith(displayName + " — ", contactSheetLabel);
                Assert.False(contactSheetLabel.Contains(" ui", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void ApiMappingsCoverEveryPersistedGeneratorType()
        {
            foreach (var apiType in Enum.GetValues<ImageGeneratorApiType>())
            {
                var result = new TaskProcessResult
                {
                    ImageGenerator = apiType,
                    ImageGeneratorDescription = apiType.ToString(),
                };

                var label = GeneratorPresentation.ContactSheetLabel(result);

                Assert.Contains(" — ", label);
                Assert.False(label.Contains(" ui", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void ContactSheetKeepsUsefulPerCallDetailAfterCanonicalName()
        {
            var result = new TaskProcessResult
            {
                GeneratorKey = UiJobRunner.KeyGpt2,
                ImageGenerator = ImageGeneratorApiType.GptImage2,
                ImageGeneratorDescription = "gpt-image-2 ui high landscape",
            };

            Assert.Equal(
                "gpt-image-2 — OpenAI · high landscape",
                GeneratorPresentation.ContactSheetLabel(result));
        }

        [Fact]
        public void CliGoogleInternalDescriptionDoesNotReplaceOrRepeatItsPublicName()
        {
            var result = new TaskProcessResult
            {
                ImageGenerator = ImageGeneratorApiType.GoogleNanoBananaPro,
                ImageGeneratorDescription = "google-GoogleNanoBananaPro",
            };

            Assert.Equal(
                "Nano Banana Pro — Google · gemini-3-pro-image",
                GeneratorPresentation.ContactSheetLabel(result));
        }

        [Fact]
        public void UnknownLegacyApiTypePreservesItsExactRecordedDescription()
        {
            var result = new TaskProcessResult
            {
                ImageGenerator = (ImageGeneratorApiType)999,
                ImageGeneratorDescription = "legacy custom producer",
            };

            Assert.Equal(
                "legacy custom producer",
                GeneratorPresentation.ContactSheetLabel(result));
        }
    }
}
