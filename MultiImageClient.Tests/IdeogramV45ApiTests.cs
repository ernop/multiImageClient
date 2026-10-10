using System.Net;
using System.Text;
using IdeogramAPIClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MultiImageClient;

public class IdeogramV45ApiTests
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

    [Fact]
    public async Task GenerateUsesV2MultipartContract()
    {
        var handler = new FakeIdeogram
        {
            GenerationBody = """{"generation_kind":"sampling","generation_id":"g","seed":7,"data":[{"seed":7,"resolution":"2496x1664","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/a.png"},{"seed":8,"resolution":"2496x1664","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/b.png"}]}""",
        };
        var client = new IdeogramClient("test-key", handler);

        await client.GenerateImageV45Async(new IdeogramV45GenerateRequest("make a poster")
        {
            Size = "2496x1664",
            Quality = IdeogramV45Quality.medium,
            NumImages = 2,
            Seed = 7,
        });

        var post = Assert.Single(handler.Posts);
        Assert.Equal("/v2/image/generate/ideogram-4-5", post.Uri.AbsolutePath);
        Assert.Equal("test-key", post.ApiKey);
        Assert.StartsWith("multipart/form-data", post.ContentType);
        Assert.Equal(
            new[] { "prompt", "size", "quality", "num_images", "seed" },
            post.Parts.Select(part => part.Name));
        Assert.Equal("make a poster", post.Text("prompt"));
        Assert.Equal("2496x1664", post.Text("size"));
        Assert.Equal("medium", post.Text("quality"));
        Assert.Equal("2", post.Text("num_images"));
        Assert.Equal("7", post.Text("seed"));
    }

    [Fact]
    public async Task GenerateOmitsUnsetOptionalFields()
    {
        var handler = new FakeIdeogram();
        var client = new IdeogramClient("test-key", handler);

        await client.GenerateImageV45Async(new IdeogramV45GenerateRequest("make a poster"));

        var post = Assert.Single(handler.Posts);
        Assert.Equal(new[] { "prompt" }, post.Parts.Select(part => part.Name));
    }

    [Fact]
    public async Task PreciseEditSendsSourceAndOrderedReferences()
    {
        var handler = new FakeIdeogram();
        var client = new IdeogramClient("test-key", handler);
        var firstReference = JpegHeader.Append((byte)1).ToArray();
        var secondReference = PngHeader.Append((byte)2).ToArray();

        await client.PreciseEditImageV45Async(new IdeogramV45PreciseEditRequest(
            "make the bicycle blue",
            new IdeogramFile(PngHeader, "source.png", "image/png"))
        {
            ReferenceImages = new[]
            {
                new IdeogramFile(firstReference, "first.jpg", "image/jpeg"),
                new IdeogramFile(secondReference, "second.png", "image/png"),
            },
            Quality = IdeogramV45Quality.very_low,
        });

        var post = Assert.Single(handler.Posts);
        Assert.Equal("/v2/image/precise-edit/ideogram-4-5", post.Uri.AbsolutePath);
        Assert.Equal(
            new[] { "prompt", "image", "reference_images", "reference_images", "quality" },
            post.Parts.Select(part => part.Name));
        var image = post.Parts[1];
        Assert.Equal("source.png", image.FileName);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(PngHeader, image.Bytes);
        Assert.Equal(("first.jpg", "image/jpeg"), (post.Parts[2].FileName, post.Parts[2].ContentType));
        Assert.Equal(firstReference, post.Parts[2].Bytes);
        Assert.Equal(("second.png", "image/png"), (post.Parts[3].FileName, post.Parts[3].ContentType));
        Assert.Equal(secondReference, post.Parts[3].Bytes);
        Assert.Equal("very_low", post.Text("quality"));
    }

    [Fact]
    public async Task ClientRejectsRequestsOutsideThePublishedContractBeforeSending()
    {
        var handler = new FakeIdeogram();
        var client = new IdeogramClient("test-key", handler);
        var source = new IdeogramFile(PngHeader, "source.png", "image/png");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { Size = "1000x1000" }));
        // 4.0 accepts 512x1536; 4.5 Generate rejected it on 2026-10-07.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { Size = "512x1536" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { Quality = IdeogramV45Quality.very_low }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { NumImages = 9 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { NumImages = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("p") { Seed = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest(new string('a', 10_001))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PreciseEditImageV45Async(
            new IdeogramV45PreciseEditRequest("p", source)
            {
                ReferenceImages = Enumerable.Repeat(source, 5).ToArray(),
            }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PreciseEditImageV45Async(
            new IdeogramV45PreciseEditRequest("p", new IdeogramFile(JpegHeader, "lie.png", "image/png"))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PreciseEditImageV45Async(
            new IdeogramV45PreciseEditRequest("p", new IdeogramFile(Encoding.ASCII.GetBytes("GIF89a"), "a.gif", "image/gif"))));

        Assert.Empty(handler.Posts);
    }

    [Fact]
    public async Task PromptLimitCountsUnicodeCharactersNotUtf16Units()
    {
        var handler = new FakeIdeogram();
        var client = new IdeogramClient("test-key", handler);
        var prompt = string.Concat(Enumerable.Repeat("\U0001F3A8", 10_000));
        Assert.Equal(20_000, prompt.Length);

        await client.GenerateImageV45Async(new IdeogramV45GenerateRequest(prompt));

        Assert.Single(handler.Posts);
    }

    [Theory]
    [InlineData("""{"generation_id":"g","seed":1,"data":[]}""", "generation_kind")]
    [InlineData("""{"generation_kind":"sampling","seed":1,"data":[{"seed":1,"is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/a.png"}]}""", "resolution")]
    [InlineData("""{"generation_kind":"sampling","seed":1,"data":[{"seed":1,"resolution":"64x48","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/a.png"}]}""", "generation_id")]
    [InlineData("""{"generation_kind":"sampling","generation_id":"g","seed":1,"data":[]}""", "0 image entries")]
    public async Task GenerateRejectsRepliesOutsideThePublishedContract(string body, string expectedMessagePart)
    {
        var handler = new FakeIdeogram { GenerationBody = body };
        var client = new IdeogramClient("test-key", handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("make a poster")));

        Assert.Contains(expectedMessagePart, error.Message);
    }

    [Fact]
    public async Task PreciseEditAcceptsReplyWithoutGenerationId()
    {
        var handler = new FakeIdeogram
        {
            GenerationBody = """{"generation_kind":"workflow","data":[{"seed":1,"resolution":"64x48","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/a.png"}]}""",
        };
        var client = new IdeogramClient("test-key", handler);

        var response = await client.PreciseEditImageV45Async(new IdeogramV45PreciseEditRequest(
            "make it blue",
            new IdeogramFile(PngHeader, "source.png", "image/png")));

        Assert.Equal("workflow", response.GenerationKind);
        Assert.Null(response.GenerationId);
        Assert.Single(response.Data!);
    }

    [Fact]
    public async Task HttpFailureKeepsStatusAndProviderBody()
    {
        var handler = new FakeIdeogram
        {
            GenerationStatus = HttpStatusCode.PaymentRequired,
            GenerationBody = """{"error":"Insufficient credits","reject_reason":"insufficient_funds"}""",
        };
        var client = new IdeogramClient("test-key", handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateImageV45Async(
            new IdeogramV45GenerateRequest("make a poster")));

        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Contains("insufficient_funds", error.Message);
    }

    [Fact]
    public async Task GeneratorReturnsVerifiedImageBytesAndRecordsProviderPrompt()
    {
        var png = Png(64, 48);
        var handler = new FakeIdeogram
        {
            GenerationBody = ImageReply("64x48"),
            DownloadBytes = png,
        };
        var stats = new MultiClientRunStats();
        var generator = new IdeogramV45Generator(
            "test-key", 1, "2048x2048", IdeogramV45Quality.high, stats, "ideogram-v45 ui",
            httpHandler: handler);
        var prompt = Prompt("make a poster");

        var result = await generator.ProcessPromptAsync(generator, prompt);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(png, Convert.FromBase64String(Assert.Single(result.Base64ImageDatas).bytesBase64));
        Assert.Equal(ImageGeneratorApiType.IdeogramV45, result.ImageGenerator);
        Assert.Equal("Ideogram 4.5 generate · quality high", result.ImageGeneratorDescription);
        Assert.Equal("make a poster", prompt.Prompt);
        Assert.Equal(TransformationType.IdeogramRewrite, prompt.TransformationSteps.Last().TransformationType);
        Assert.Equal(1, stats.IdeogramV45RequestCount);
        var post = Assert.Single(handler.Posts);
        Assert.Equal("2048x2048", post.Text("size"));
        Assert.Equal("high", post.Text("quality"));
        Assert.Equal(1, handler.DownloadCount);
    }

    [Fact]
    public async Task GeneratorFailsWhenDownloadedSizeDiffersFromReportedResolution()
    {
        var handler = new FakeIdeogram
        {
            GenerationBody = ImageReply("2048x2048"),
            DownloadBytes = Png(64, 48),
        };
        var generator = Generator(handler);

        var result = await generator.ProcessPromptAsync(generator, Prompt("make a poster"));

        Assert.False(result.IsSuccess);
        Assert.Contains("64x48", result.ErrorMessage);
        Assert.Contains("2048x2048", result.ErrorMessage);
        Assert.Empty(result.Base64ImageDatas);
    }

    [Fact]
    public async Task GeneratorFailsWhenServedTypeDiffersFromBytes()
    {
        var handler = new FakeIdeogram
        {
            GenerationBody = ImageReply("64x48"),
            DownloadBytes = Png(64, 48),
            DownloadContentType = "image/jpeg",
        };
        var generator = Generator(handler);

        var result = await generator.ProcessPromptAsync(generator, Prompt("make a poster"));

        Assert.False(result.IsSuccess);
        Assert.Contains("image/jpeg", result.ErrorMessage);
    }

    [Fact]
    public async Task GeneratorFailsWhenDownloadIsRejected()
    {
        var handler = new FakeIdeogram
        {
            GenerationBody = ImageReply("64x48"),
            DownloadStatus = HttpStatusCode.Forbidden,
        };
        var generator = Generator(handler);

        var result = await generator.ProcessPromptAsync(generator, Prompt("make a poster"));

        Assert.False(result.IsSuccess);
        Assert.Equal(GenericImageGenerationErrorType.Unknown, result.GenericImageErrorType);
        Assert.Contains("403", result.ErrorMessage);
    }

    [Fact]
    public async Task GeneratorReportsUnsafeImageAsModerationWithoutDownloading()
    {
        var stats = new MultiClientRunStats();
        var handler = new FakeIdeogram
        {
            GenerationBody = """{"generation_kind":"sampling","generation_id":"g","seed":1,"data":[{"seed":1,"resolution":"2048x2048","is_image_safe":false,"prompt":"p","url":null}]}""",
        };
        var generator = new IdeogramV45Generator(
            "test-key", 1, "", null, stats, "", httpHandler: handler);

        var result = await generator.ProcessPromptAsync(generator, Prompt("make a poster"));

        Assert.False(result.IsSuccess);
        Assert.Equal(GenericImageGenerationErrorType.ContentModerated, result.GenericImageErrorType);
        Assert.Equal(1, stats.IdeogramV45RefusedCount);
        Assert.Equal(0, handler.DownloadCount);
    }

    [Fact]
    public async Task GeneratorClassifiesPaymentRequiredAsBillingFailure()
    {
        var handler = new FakeIdeogram
        {
            GenerationStatus = HttpStatusCode.PaymentRequired,
            GenerationBody = """{"error":"Insufficient credits","reject_reason":"insufficient_funds"}""",
        };
        var generator = Generator(handler);

        var result = await generator.ProcessPromptAsync(generator, Prompt("make a poster"));

        Assert.False(result.IsSuccess);
        Assert.Equal(GenericImageGenerationErrorType.NoMoneyLeft, result.GenericImageErrorType);
        var hint = ProviderActionHints.For(UiJobRunner.KeyIdeogramV45, result.ErrorMessage);
        Assert.NotNull(hint);
        Assert.Equal("https://ideogram.ai/manage-api", hint.Url);
    }

    [Fact]
    public async Task GeneratorPreciseEditSendsFirstInputAsImageAndLaterInputsAsReferences()
    {
        var directory = Directory.CreateTempSubdirectory("ideogram45-test-");
        try
        {
            var sourcePath = Path.Combine(directory.FullName, "source.png");
            var referencePath = Path.Combine(directory.FullName, "reference.jpeg");
            var sourceBytes = Png(64, 48);
            var referenceBytes = JpegHeader.Append((byte)9).ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            await File.WriteAllBytesAsync(referencePath, referenceBytes);
            var handler = new FakeIdeogram
            {
                GenerationBody = """{"generation_kind":"sampling","data":[{"seed":3,"resolution":"64x48","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/api/images/a.png"}]}""",
                DownloadBytes = Png(64, 48),
            };
            var generator = new IdeogramV45Generator(
                "test-key", 1, "", IdeogramV45Quality.medium, new MultiClientRunStats(), "",
                inputImagePaths: new[] { sourcePath, referencePath },
                httpHandler: handler);

            var result = await generator.ProcessPromptAsync(generator, Prompt("make it blue"));

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(
                "Ideogram 4.5 precise edit · quality medium · 1 reference image",
                result.ImageGeneratorDescription);
            var post = Assert.Single(handler.Posts);
            Assert.Equal("/v2/image/precise-edit/ideogram-4-5", post.Uri.AbsolutePath);
            Assert.Equal(
                new[] { "prompt", "image", "reference_images", "quality" },
                post.Parts.Select(part => part.Name));
            Assert.Equal(("source.png", "image/png"), (post.Parts[1].FileName, post.Parts[1].ContentType));
            Assert.Equal(sourceBytes, post.Parts[1].Bytes);
            Assert.Equal(("reference.jpg", "image/jpeg"), (post.Parts[2].FileName, post.Parts[2].ContentType));
            Assert.Equal(referenceBytes, post.Parts[2].Bytes);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CostsMatchTheProviderPriceQuotes()
    {
        Assert.Equal(0.03m, Cost(IdeogramV45Quality.low));
        Assert.Equal(0.06m, Cost(IdeogramV45Quality.medium));
        Assert.Equal(0.10m, Cost(IdeogramV45Quality.high));
        Assert.Equal(0.10m, Cost(null));
        Assert.Equal(0.008m, Cost(IdeogramV45Quality.very_low, edit: true));
        Assert.Equal(0.03m, Cost(IdeogramV45Quality.low, edit: true));
        Assert.Equal(0.06m, Cost(IdeogramV45Quality.medium, edit: true));
        Assert.Equal(0.22m, Cost(IdeogramV45Quality.high, edit: true));
        Assert.Equal(0.06m, Cost(null, edit: true));
        Assert.Equal(0.30m, new IdeogramV45Generator(
            "unused", 1, "", IdeogramV45Quality.high, new MultiClientRunStats(), "", imageCount: 3).GetCost());
    }

    [Fact]
    public void SpecLabelNamesAutoSizeButLeavesExplicitSizeToTheSheet()
    {
        var stats = new MultiClientRunStats();
        Assert.Equal(
            "Ideogram 4.5 generate · quality provider default · auto size",
            new IdeogramV45Generator("k", 1, "", null, stats, "").GetGeneratorSpecPart());
        Assert.Equal(
            "Ideogram 4.5 generate · quality low",
            new IdeogramV45Generator("k", 1, "2496x1664", IdeogramV45Quality.low, stats, "").GetGeneratorSpecPart());
    }

    [Theory]
    [InlineData("low", IdeogramV45Quality.low)]
    [InlineData("medium", IdeogramV45Quality.medium)]
    [InlineData("high", IdeogramV45Quality.high)]
    [InlineData("xhigh", IdeogramV45Quality.high)]
    [InlineData("MAX", IdeogramV45Quality.high)]
    public void QualityOptionsMapToIdeogramTiers(string option, IdeogramV45Quality expected)
    {
        Assert.Equal(expected, IdeogramV45Generator.QualityFromOption(option));
    }

    [Fact]
    public void AutoQualityOmitsTheFieldAndUnknownQualityFails()
    {
        Assert.Null(IdeogramV45Generator.QualityFromOption("auto"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdeogramV45Generator.QualityFromOption(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdeogramV45Generator.QualityFromOption("ultra"));
    }

    [Fact]
    public void GeneratorRejectsConfigurationsTheEndpointsCannotHonor()
    {
        var stats = new MultiClientRunStats();
        var inputs = new[] { "a.png" };
        Assert.Throws<ArgumentException>(() => new IdeogramV45Generator(
            "k", 1, "2048x2048", null, stats, "", inputImagePaths: inputs));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdeogramV45Generator(
            "k", 1, "1000x1000", null, stats, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdeogramV45Generator(
            "k", 1, "", IdeogramV45Quality.very_low, stats, ""));
        Assert.Throws<ArgumentException>(() => new IdeogramV45Generator(
            "k", 1, "", null, stats, "", inputImagePaths: Enumerable.Repeat("a.png", 6).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdeogramV45Generator(
            "k", 1, "", null, stats, "", imageCount: 9));
    }

    [Fact]
    public void UiShapesUsePublishedTextSizes()
    {
        Assert.Equal("", UiShapeMapping.IdeogramV45Size("auto"));
        foreach (var shape in UiShapeMapping.Shapes.Where(shape => shape != "auto"))
        {
            Assert.True(
                IdeogramClient.IsIdeogramV45TextSize(UiShapeMapping.IdeogramV45Size(shape)),
                $"{shape} maps outside the published 4.5 sizes");
        }
    }

    [Fact]
    public void UiCatalogTreatsIdeogram45AsImageCapableButNotSketchCapable()
    {
        Assert.True(UiJobRunner.IsImageGeneratorKey(UiJobRunner.KeyIdeogramV45));
        Assert.True(UiJobRunner.IsImageCapable(UiJobRunner.KeyIdeogramV45));
        Assert.False(UiJobRunner.IsSketchCapable(UiJobRunner.KeyIdeogramV45));
        Assert.Equal("Ideogram 4.5", GeneratorPresentation.UiDisplayName(UiJobRunner.KeyIdeogramV45));
        Assert.Equal(
            "Ideogram 4.5 — Ideogram · generate · quality high · auto size",
            GeneratorPresentation.UiContactSheetLabel(
                UiJobRunner.KeyIdeogramV45,
                "Ideogram 4.5 generate · quality high · auto size"));
        Assert.Equal(1 + IdeogramClient.IdeogramV45MaxReferenceImages, UiJobRunner.MaxInputImages);
    }

    private static decimal Cost(IdeogramV45Quality? quality, bool edit = false)
        => new IdeogramV45Generator(
            "unused", 1, "", quality, new MultiClientRunStats(), "",
            inputImagePaths: edit ? new[] { "unused.png" } : null).GetCost();

    private static IdeogramV45Generator Generator(HttpMessageHandler handler)
        => new("test-key", 1, "", IdeogramV45Quality.low, new MultiClientRunStats(), "", httpHandler: handler);

    private static PromptDetails Prompt(string text)
    {
        var prompt = new PromptDetails();
        prompt.ReplacePrompt(text, text, TransformationType.InitialPrompt);
        return prompt;
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(30, 140, 220));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static string ImageReply(string resolution)
        => "{\"generation_kind\":\"sampling\",\"generation_id\":\"gen-1\",\"seed\":5,\"data\":[{"
            + "\"seed\":5,\"resolution\":\"" + resolution + "\",\"is_image_safe\":true,"
            + "\"prompt\":\"{\\\"high_level_description\\\":\\\"a poster\\\"}\","
            + "\"url\":\"https://ideogram.ai/api/images/ephemeral/a.png\"}]}";

    private sealed class FakeIdeogram : HttpMessageHandler
    {
        public string GenerationBody { get; init; } =
            """{"generation_kind":"sampling","generation_id":"g","seed":1,"data":[{"seed":1,"resolution":"64x48","is_image_safe":true,"prompt":"p","url":"https://ideogram.ai/a.png"}]}""";
        public HttpStatusCode GenerationStatus { get; init; } = HttpStatusCode.OK;
        public byte[] DownloadBytes { get; init; } = Array.Empty<byte>();
        public string DownloadContentType { get; init; } = "image/png";
        public HttpStatusCode DownloadStatus { get; init; } = HttpStatusCode.OK;
        public List<RecordedPost> Posts { get; } = new();
        public int DownloadCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                DownloadCount++;
                var content = new ByteArrayContent(DownloadBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(DownloadContentType);
                return new HttpResponseMessage(DownloadStatus) { Content = content };
            }

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = new List<RecordedPart>();
            foreach (var part in multipart)
            {
                parts.Add(new RecordedPart(
                    part.Headers.ContentDisposition?.Name?.Trim('"')
                        ?? throw new InvalidOperationException("Multipart part has no name."),
                    part.Headers.ContentDisposition?.FileName?.Trim('"'),
                    part.Headers.ContentType?.MediaType,
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }
            Posts.Add(new RecordedPost(
                request.RequestUri!,
                request.Headers.TryGetValues("Api-Key", out var keys) ? keys.Single() : null,
                multipart.Headers.ContentType?.ToString() ?? "",
                parts));
            return new HttpResponseMessage(GenerationStatus)
            {
                Content = new StringContent(GenerationBody),
            };
        }
    }

    private sealed record RecordedPost(
        Uri Uri,
        string? ApiKey,
        string ContentType,
        IReadOnlyList<RecordedPart> Parts)
    {
        public string Text(string name) => Encoding.UTF8.GetString(Parts.Single(part => part.Name == name).Bytes);
    }

    private sealed record RecordedPart(
        string Name,
        string? FileName,
        string? ContentType,
        byte[] Bytes);
}
