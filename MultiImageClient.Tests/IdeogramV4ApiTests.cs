using System.Net;
using IdeogramAPIClient;

namespace MultiImageClient;

public class IdeogramV4ApiTests
{
    [Fact]
    public async Task GenerateUsesCurrentMultipartContract()
    {
        var handler = new RecordingHandler();
        var client = new IdeogramClient("test-key", handler);

        await client.GenerateImageV4Async(new IdeogramV4GenerateRequest("make a poster")
        {
            Resolution = "2048x2048",
            RenderingSpeed = IdeogramRenderingSpeed.DEFAULT,
        });

        Assert.Equal("/v1/ideogram-v4/generate", handler.RequestUri?.AbsolutePath);
        Assert.StartsWith("multipart/form-data", handler.ContentType);
        Assert.Equal("make a poster", handler.Parts["text_prompt"].Text);
        Assert.Equal("2048x2048", handler.Parts["resolution"].Text);
        Assert.Equal("DEFAULT", handler.Parts["rendering_speed"].Text);
        Assert.DoesNotContain("num_images", handler.Parts.Keys);
        Assert.DoesNotContain("seed", handler.Parts.Keys);
    }

    [Fact]
    public async Task RemixSendsExactImageAndRemixFields()
    {
        var handler = new RecordingHandler();
        var client = new IdeogramClient("test-key", handler);
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        await client.RemixImageV4Async(new IdeogramV4RemixRequest(
            "turn it into a screen print",
            new IdeogramFile(pngHeader, "source.png", "image/png"))
        {
            ImageWeight = 50,
            Resolution = "2496x1664",
            RenderingSpeed = IdeogramRenderingSpeed.QUALITY,
        });

        Assert.Equal("/v1/ideogram-v4/remix", handler.RequestUri?.AbsolutePath);
        Assert.StartsWith("multipart/form-data", handler.ContentType);
        Assert.Equal("source.png", handler.Parts["image"].FileName);
        Assert.Equal("image/png", handler.Parts["image"].ContentType);
        Assert.Equal(pngHeader, handler.Parts["image"].Bytes);
        Assert.Equal("turn it into a screen print", handler.Parts["text_prompt"].Text);
        Assert.Equal("50", handler.Parts["image_weight"].Text);
        Assert.Equal("2496x1664", handler.Parts["resolution"].Text);
        Assert.Equal("QUALITY", handler.Parts["rendering_speed"].Text);
    }

    [Fact]
    public void PublishedPricingAndUnsupportedFlashAreEnforced()
    {
        var stats = new MultiClientRunStats();
        Assert.Equal(
            0.03m,
            new IdeogramV4Generator(
                "unused",
                1,
                "2048x2048",
                IdeogramRenderingSpeed.TURBO,
                stats,
                "").GetCost());
        Assert.Equal(
            0.06m,
            new IdeogramV4Generator(
                "unused",
                1,
                "2048x2048",
                IdeogramRenderingSpeed.DEFAULT,
                stats,
                "").GetCost());
        Assert.Equal(
            0.10m,
            new IdeogramV4Generator(
                "unused",
                1,
                "2048x2048",
                IdeogramRenderingSpeed.QUALITY,
                stats,
                "").GetCost());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IdeogramV4Generator(
                "unused",
                1,
                "2048x2048",
                IdeogramRenderingSpeed.FLASH,
                stats,
                ""));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string ContentType { get; private set; } = "";
        public Dictionary<string, RecordedPart> Parts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.ToString() ?? "";
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"')
                    ?? throw new InvalidOperationException("Multipart part has no name.");
                var bytes = await part.ReadAsByteArrayAsync(cancellationToken);
                Parts[name] = new RecordedPart(
                    part.Headers.ContentDisposition?.FileName?.Trim('"'),
                    part.Headers.ContentType?.MediaType,
                    bytes);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "created": "2026-08-09T00:00:00Z",
                      "data": [{
                        "prompt": "expanded prompt",
                        "resolution": "2048x2048",
                        "is_image_safe": true,
                        "seed": 1,
                        "url": "https://example.com/image.png"
                      }],
                      "response_type": "url"
                    }
                    """),
            };
        }
    }

    private sealed record RecordedPart(
        string? FileName,
        string? ContentType,
        byte[] Bytes)
    {
        public string Text => System.Text.Encoding.UTF8.GetString(Bytes);
    }
}
