using IdeogramAPIClient;

namespace MultiImageClient;

public class UiShapeMappingTests
{
    [Fact]
    public void AutoWithoutInputPreservesTextToImageBehavior()
    {
        Assert.Equal("auto", UiShapeMapping.Gpt2Size("auto", "standard"));
        Assert.Equal("", UiShapeMapping.GrokAspect("auto"));
        Assert.Equal("", UiShapeMapping.GoogleAspect("auto"));
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
