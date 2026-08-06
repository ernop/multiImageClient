using IdeogramAPIClient;
using RecraftAPIClient;

namespace MultiImageClient;

public class UiShapeMappingTests
{
    [Fact]
    public void AutoWithoutInputPreservesTextToImageBehavior()
    {
        Assert.Equal("auto", UiShapeMapping.Gpt2Size("auto", "standard"));
        Assert.Equal("", UiShapeMapping.GrokAspect("auto"));
        Assert.Equal("", UiShapeMapping.GoogleAspect("auto"));
        Assert.Equal("1:1", UiShapeMapping.KreaAspect("auto"));
    }

    [Theory]
    [InlineData(1200, 900, "standard")]
    [InlineData(900, 1200, "high")]
    [InlineData(1122, 1402, "max")]
    [InlineData(3000, 1000, "standard")]
    public void Gpt2AutoMatchesInputWithinProviderEnvelope(
        int inputWidth,
        int inputHeight,
        string detail)
    {
        var size = UiShapeMapping.Gpt2Size(
            "auto",
            detail,
            inputWidth,
            inputHeight);
        var dimensions = ParseSize(size);

        Assert.True(
            GptImage2Generator.TryNormalizeSize(size, out var normalized, out _, out var error),
            error);
        Assert.Equal(size, normalized);
        AssertRatioClose(inputWidth, inputHeight, dimensions.Width, dimensions.Height, 0.02);
    }

    [Fact]
    public void Gpt2ExtremeInputUsesDocumentedThreeToOneCeiling()
    {
        var size = UiShapeMapping.Gpt2Size("auto", "max", 4000, 500);
        var dimensions = ParseSize(size);

        Assert.InRange((double)dimensions.Width / dimensions.Height, 2.95, 3.0);
    }

    [Fact]
    public void ExplicitShapeOverridesInputDimensions()
    {
        Assert.Equal(
            "1536x1024",
            UiShapeMapping.Gpt2Size("landscape", "standard", 900, 1600));
        Assert.Equal("16:9", UiShapeMapping.GrokAspect("wide", 900, 1600));
        Assert.Equal("1:1", UiShapeMapping.GoogleAspect("square", 1600, 900));
        Assert.Equal("3:2", UiShapeMapping.KreaAspect("landscape", 900, 1600));
        Assert.Equal(
            IdeogramAspectRatio.ASPECT_2_3,
            UiShapeMapping.IdeogramV3Aspect("portrait", 1600, 900));
    }

    [Theory]
    [InlineData(400, 300, "4:3")]
    [InlineData(300, 400, "3:4")]
    [InlineData(1600, 900, "16:9")]
    public void GrokAutoUsesClosestSupportedInputAspect(
        int width,
        int height,
        string expected)
    {
        Assert.Equal(expected, UiShapeMapping.GrokAspect("auto", width, height));
    }

    [Theory]
    [InlineData(2100, 900, "21:9")]
    [InlineData(800, 1000, "4:5")]
    [InlineData(1000, 800, "5:4")]
    public void GoogleAutoUsesFullSupportedAspectSet(
        int width,
        int height,
        string expected)
    {
        Assert.Equal(expected, UiShapeMapping.GoogleAspect("auto", width, height));
    }

    [Theory]
    [InlineData(2350, 1000, "2.35:1")]
    [InlineData(800, 1000, "4:5")]
    [InlineData(900, 1600, "9:16")]
    public void KreaAutoUsesFullSupportedAspectSet(
        int width,
        int height,
        string expected)
    {
        Assert.Equal(expected, UiShapeMapping.KreaAspect("auto", width, height));
    }

    [Theory]
    [InlineData(1000, 1600, IdeogramAspectRatio.ASPECT_10_16)]
    [InlineData(300, 900, IdeogramAspectRatio.ASPECT_1_3)]
    [InlineData(900, 300, IdeogramAspectRatio.ASPECT_3_1)]
    public void IdeogramAutoUsesClosestV3Aspect(
        int width,
        int height,
        IdeogramAspectRatio expected)
    {
        Assert.Equal(
            expected,
            UiShapeMapping.IdeogramV3Aspect("auto", width, height));
    }

    [Theory]
    [InlineData(1200, 900, "standard")]
    [InlineData(900, 1200, "high")]
    [InlineData(2100, 900, "max")]
    public void BflAutoBuildsSourceMatchingMultiplesOfThirtyTwo(
        int inputWidth,
        int inputHeight,
        string detail)
    {
        var dimensions = UiShapeMapping.BflSize(
            "auto",
            detail,
            inputWidth,
            inputHeight);

        Assert.Equal(0, dimensions.Width % 32);
        Assert.Equal(0, dimensions.Height % 32);
        Assert.True((long)dimensions.Width * dimensions.Height <= 4_000_000);
        AssertRatioClose(inputWidth, inputHeight, dimensions.Width, dimensions.Height, 0.035);
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(900, 1600)]
    [InlineData(1200, 1200)]
    public void BflLegacyAutoMatchesInputWithinLegacyLimits(int inputWidth, int inputHeight)
    {
        var dimensions = UiShapeMapping.BflLegacySize("auto", inputWidth, inputHeight);

        Assert.Equal(0, dimensions.Width % 32);
        Assert.Equal(0, dimensions.Height % 32);
        Assert.InRange(dimensions.Width, 256, 1440);
        Assert.InRange(dimensions.Height, 256, 1440);
        AssertRatioClose(inputWidth, inputHeight, dimensions.Width, dimensions.Height, 0.035);
    }

    [Fact]
    public void MissingInputDimensionsFailClosed()
    {
        Assert.Throws<InvalidOperationException>(
            () => UiShapeMapping.GrokAspect("auto", 100, 0));
        Assert.Throws<InvalidOperationException>(
            () => UiShapeMapping.Gpt2Size("auto", "standard", 0, 100));
    }

    [Fact]
    public void ShapeValidationRejectsUnknownValues()
    {
        Assert.True(UiShapeMapping.IsKnownShape("wide"));
        Assert.False(UiShapeMapping.IsKnownShape("match-ish"));
    }

    private static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split('x');
        Assert.Equal(2, parts.Length);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static void AssertRatioClose(
        int expectedWidth,
        int expectedHeight,
        int actualWidth,
        int actualHeight,
        double tolerance)
    {
        var expected = Math.Clamp(
            (double)expectedWidth / expectedHeight,
            1.0 / GptImage2Generator.SizeMaxAspectRatio,
            GptImage2Generator.SizeMaxAspectRatio);
        var actual = (double)actualWidth / actualHeight;
        Assert.InRange(Math.Abs(Math.Log(actual / expected)), 0, tolerance);
    }
}

public class Krea2GeneratorTests
{
    [Theory]
    [InlineData(Krea2Variant.MediumTurbo, "0.015")]
    [InlineData(Krea2Variant.Medium, "0.03")]
    [InlineData(Krea2Variant.Large, "0.06")]
    public void TextToImageCostsMatchPublishedPrices(Krea2Variant variant, string expected)
    {
        var generator = new Krea2Generator(
            "unused",
            1,
            variant,
            "1:1",
            new MultiClientRunStats());

        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            generator.GetCost());
    }

    [Theory]
    [InlineData(Krea2Variant.MediumTurbo, "0.0175")]
    [InlineData(Krea2Variant.Medium, "0.035")]
    [InlineData(Krea2Variant.Large, "0.065")]
    public void StyleReferenceCostsMatchPublishedPrices(Krea2Variant variant, string expected)
    {
        var generator = new Krea2Generator(
            "unused",
            1,
            variant,
            "1:1",
            new MultiClientRunStats(),
            inputImagePath: "unused.png");

        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            generator.GetCost());
    }
}

public class RecraftVariantTests
{
    [Theory]
    [InlineData(RecraftModel.recraftv4_1, "recraftv4_1")]
    [InlineData(RecraftModel.recraftv4_1_utility, "recraftv4_1_utility")]
    [InlineData(RecraftModel.recraftv4_1_pro, "recraftv4_1_pro")]
    [InlineData(RecraftModel.recraftv4_1_vector, "recraftv4_1_vector")]
    [InlineData(RecraftModel.recraftv3, "recraftv3")]
    [InlineData(RecraftModel.recraftv4, "recraftv4")]
    [InlineData(RecraftModel.recraftv4_pro, "recraftv4_pro")]
    public void ModelNamesMatchExactApiIds(RecraftModel model, string expected)
    {
        Assert.Equal(expected, model.ToString());
    }

    [Theory]
    [InlineData(RecraftModel.recraftv4_1, "0.035")]
    [InlineData(RecraftModel.recraftv4_1_utility, "0.035")]
    [InlineData(RecraftModel.recraftv4_1_pro, "0.21")]
    [InlineData(RecraftModel.recraftv4_1_vector, "0.08")]
    [InlineData(RecraftModel.recraftv3, "0.04")]
    [InlineData(RecraftModel.recraftv4, "0.04")]
    [InlineData(RecraftModel.recraftv4_pro, "0.25")]
    public void CostsMatchPublishedRasterAndVectorPrices(
        RecraftModel model,
        string expected)
    {
        var generator = new RecraftGenerator(
            "unused",
            1,
            RecraftImageSize._1024x1024,
            RecraftStyle.any,
            null,
            null,
            null,
            new MultiClientRunStats(),
            "test",
            model: model);

        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            generator.GetCost());
    }

    [Fact]
    public void SvgRawFilenameKeepsSvgExtension()
    {
        var filename = FilenameGenerator.GenerateUniqueFilename(
            "recraft-vector",
            0,
            "image/svg+xml",
            Path.GetTempPath(),
            SaveType.Raw);

        Assert.EndsWith(".svg", filename, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextFailureIsNotReplacedByJsonParserFailure()
    {
        var exception = new InvalidDataException(
            "Recraft content-type probe returned unsupported or missing content type 'application/octet-stream'.");

        Assert.Equal(exception.Message, RecraftGenerator.ExtractErrorMessage(exception));
    }

    [Fact]
    public void ProviderJsonFailureIncludesCodeAndMessage()
    {
        var exception = new HttpRequestException(
            "API request failed: BadRequest - {\"code\":\"invalid_model\",\"message\":\"Model is unavailable\"}");

        Assert.Equal(
            "invalid_model: Model is unavailable",
            RecraftGenerator.ExtractErrorMessage(exception));
    }
}

public class UiVisibilityStoreTests
{
    [Fact]
    public void VisibilityAuthorizationUsesAuthenticatedCreatorAndOverrideOnly()
    {
        var method = typeof(UiWorkflow).GetMethod(
            "CanManageVisibility",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var current = new UiJob
        {
            Prompt = "test",
            CreatedBy = "display alias",
            CreatorLogin = "creator-login",
        };
        var legacy = new UiJob
        {
            Prompt = "test",
            CreatedBy = "creator-login",
        };

        Assert.True((bool)method.Invoke(null, new object?[] { current, "creator-login" })!);
        Assert.False((bool)method.Invoke(null, new object?[] { current, "display alias" })!);
        Assert.True((bool)method.Invoke(null, new object?[] { current, "ernieMultiZone" })!);
        Assert.False((bool)method.Invoke(null, new object?[] { legacy, "creator-login" })!);
        Assert.True((bool)method.Invoke(null, new object?[] { legacy, "ernieMultiZone" })!);
    }

    [Fact]
    public void HiddenPromptAndImagePersistByExactIdentity()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-visibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = root };
            var store = new UiVisibilityStore(settings);
            store.Hide(new UiHiddenResource
            {
                Kind = "prompt",
                JobId = "job-a",
                HiddenByLogin = "creator",
                HiddenAtUnixMs = 1,
            });
            store.Hide(new UiHiddenResource
            {
                Kind = "image",
                JobId = "job-b",
                Generator = "gpt2",
                ImageIndex = 2,
                HiddenByLogin = "creator",
                HiddenAtUnixMs = 2,
            });

            var reloaded = new UiVisibilityStore(settings);

            Assert.True(reloaded.IsPromptHidden("job-a"));
            Assert.False(reloaded.IsPromptHidden("job-b"));
            Assert.True(reloaded.IsImageHidden("job-b", "gpt2", 2));
            Assert.False(reloaded.IsImageHidden("job-b", "gpt2", 1));
            Assert.True(reloaded.HasHiddenImages("job-b"));
            Assert.Equal(2, reloaded.Snapshot().Records.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
