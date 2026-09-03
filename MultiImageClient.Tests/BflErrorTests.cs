using BFLAPIClient;

using System.Net;

namespace MultiImageClient;

public class BflHttpErrorTests
{
    [Fact]
    public void ValidationErrorUsesMsgAndOmitsRequestBody()
    {
        var fakePng = new string('A', 8000);
        var body =
            "{\"detail\":[{\"type\":\"value_error\",\"loc\":[\"body\"],"
            + "\"msg\":\"Value error, Image_prompt dimensions must be at least 256x256 pixels\","
            + "\"input\":{\"prompt\":\"make an icon sheet\",\"image_prompt\":\"iVBORw0KGgo"
            + fakePng
            + "\",\"width\":1216,\"height\":864},\"ctx\":{\"error\":{}}}]}";

        var message = BFLHttpError.Format(
            HttpStatusCode.UnprocessableEntity,
            "Unprocessable Entity",
            body);

        Assert.StartsWith("BFL rejected this request (HTTP 422).", message);
        Assert.Contains("Image_prompt dimensions must be at least 256x256 pixels", message);
        Assert.DoesNotContain("iVBORw0KGgo", message, StringComparison.Ordinal);
        Assert.DoesNotContain(fakePng, message, StringComparison.Ordinal);
        Assert.DoesNotContain("make an icon sheet", message, StringComparison.Ordinal);
        Assert.True(message.Length < BFLHttpError.MaxMessageChars);
    }

    [Fact]
    public void FieldLocIsPrefixedOntoTheMessage()
    {
        var message = BFLHttpError.Format(
            HttpStatusCode.UnprocessableEntity,
            "Unprocessable Entity",
            "{\"detail\":[{\"loc\":[\"body\",\"width\"],\"msg\":\"ensure this value is a multiple of 32\"}]}");

        Assert.Equal(
            "BFL rejected this request (HTTP 422). width: ensure this value is a multiple of 32",
            message);
    }

    [Fact]
    public void RateLimitWithoutBodyAsksTheUserToWait()
    {
        var message = BFLHttpError.Format(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            "",
            TimeSpan.FromSeconds(8));

        Assert.Equal(
            "Black Forest Labs rate-limited this request (HTTP 429). Wait and resend. Retry-After 8s.",
            message);
    }

    [Fact]
    public void RateLimitBodyIsKeptWithoutClaimingBilling()
    {
        var message = BFLHttpError.Format(
            HttpStatusCode.TooManyRequests,
            "Too Many Requests",
            "{\"detail\":\"Rate limit exceeded\"}");

        Assert.Equal(
            "Black Forest Labs rate-limited this request (HTTP 429). Rate limit exceeded",
            message);
        Assert.Null(ProviderActionHints.For(UiJobRunner.KeyBflFlux11Ultra, message));
    }

    [Fact]
    public void PaymentRequiredStillMatchesBillingHint()
    {
        var message = BFLHttpError.Format(
            HttpStatusCode.PaymentRequired,
            "Payment Required",
            "{\"detail\":\"Payment required.\"}");

        Assert.StartsWith("Black Forest Labs requires payment (HTTP 402).", message);
        var hint = ProviderActionHints.For(UiJobRunner.KeyBflFlux11, message);
        Assert.NotNull(hint);
        Assert.Equal("https://dashboard.bfl.ai", hint.Url);
    }
}

public class BflImagePromptSizeTests
{
    // 1x1 PNG. BFL image_prompt requires 256x256, so this must fail closed.
    private static readonly byte[] OneByOnePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void UndersizedRemixImageReportsExactPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bfl-prompt-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, OneByOnePng);
            var ex = Assert.Throws<InvalidOperationException>(
                () => BFLGenerator.RequireImagePromptDimensions(path));
            Assert.Equal(BFLGenerator.ImagePromptTooSmallMessage(1, 1), ex.Message);
            Assert.DoesNotContain("iVBORw0KGgo", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingRemixImageFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bfl-missing-{Guid.NewGuid():N}.png");
        var ex = Assert.Throws<InvalidOperationException>(
            () => BFLGenerator.RequireImagePromptDimensions(path));
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
