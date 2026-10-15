namespace MultiImageClient;

public sealed class GenerationPipelineTests
{
    [Fact]
    public async Task UntimedGeneratorGetsMeasuredCreateTime()
    {
        var result = await GenerationArchive.ExecuteAsync(
            new DelayedGenerator(TimeSpan.FromMilliseconds(120), reportedCreateMs: 0),
            new PromptDetails { Prompt = "timing" });

        Assert.InRange(result.CreateTotalMs, 100, 60_000);
    }

    [Fact]
    public async Task GeneratorReportedCreateTimeIsKept()
    {
        var result = await GenerationArchive.ExecuteAsync(
            new DelayedGenerator(TimeSpan.FromMilliseconds(5), reportedCreateMs: 98_765),
            new PromptDetails { Prompt = "timing" });

        Assert.Equal(98_765, result.CreateTotalMs);
    }

    [Fact]
    public async Task SavingReleasesBase64TextButKeepsExactBytes()
    {
        var root = Directory.CreateTempSubdirectory("mic-b64-release-").FullName;
        try
        {
            var settings = new Settings
            {
                ImageDownloadBaseFolder = root,
                LogFilePath = Path.Combine(root, "test.log"),
                EnableGenerationArchive = false,
            };
            var generator = new MockWorkflowImageGenerator();
            var result = await generator.ProcessPromptAsync(generator, new PromptDetails { Prompt = "release" });
            var entry = Assert.Single(result.Base64ImageDatas);
            var expected = Convert.FromBase64String(entry.bytesBase64);

            await new ImageManager(settings, new MultiClientRunStats())
                .ProcessAndSaveAsync(result, generator, saveAnnotatedVariants: false);

            Assert.True(result.IsSuccess);
            Assert.Null(entry.bytesBase64);
            Assert.Equal(expected, Assert.Single(result.GetAllImages));
            Assert.Equal(expected, await File.ReadAllBytesAsync(result.GetSavedRawImagePath(0)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DelayedGenerator(TimeSpan delay, long reportedCreateMs) : IImageGenerator
    {
        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.WorkflowMock;

        public string GetFilenamePart(PromptDetails pd) => "timing";

        public List<string> GetRightParts() => ["timing"];

        public string GetGeneratorSpecPart() => "timing test";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await Task.Delay(delay);
            return new TaskProcessResult
            {
                IsSuccess = true,
                PromptDetails = promptDetails,
                ImageGeneratorDescription = GetGeneratorSpecPart(),
                CreateTotalMs = reportedCreateMs,
            };
        }
    }
}
