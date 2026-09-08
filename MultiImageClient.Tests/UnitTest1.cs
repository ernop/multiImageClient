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
        Assert.Equal("", UiShapeMapping.IdeogramV4Resolution("auto"));
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
    [InlineData(1600, 900, "2560x1440")]
    [InlineData(900, 1600, "1440x2560")]
    [InlineData(800, 1600, "1440x2880")]
    [InlineData(1000, 3000, "1024x3072")]
    public void IdeogramV4RemixAutoUsesClosestPublishedResolution(
        int width,
        int height,
        string expected)
    {
        Assert.Equal(
            expected,
            UiShapeMapping.IdeogramV4Resolution("auto", width, height));
    }

    [Fact]
    public void Ideogram40IsImageCapable()
    {
        Assert.True(UiJobRunner.IsImageCapable(UiJobRunner.KeyIdeogram));
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
        Assert.Throws<InvalidOperationException>(
            () => UiShapeMapping.IdeogramV4Resolution("auto", 0, 100));
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
    [Theory]
    [InlineData(true, "creator-login", "creator-login", true)]
    [InlineData(true, "creator-login", "display alias", false)]
    [InlineData(true, "creator-login", "ernieMultiZone", true)]
    [InlineData(true, "creator-login", "", false)]
    [InlineData(true, "", "creator-login", false)]
    [InlineData(true, "", "ernieMultiZone", true)]
    [InlineData(true, "", "local-instance-owner", false)]
    [InlineData(false, "creator-login", "", true)]
    [InlineData(false, "", "", true)]
    public void VisibilityAuthorizationDistinguishesLocalAndSharedInstances(
        bool authenticationEnabled, string creatorLogin, string authUser, bool expected)
    {
        var method = typeof(UiWorkflow).GetMethod(
            "CanManageVisibility",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var job = new UiJob
        {
            Prompt = "test",
            CreatedBy = "display alias",
            CreatorLogin = creatorLogin,
        };
        Assert.Equal(expected, (bool)method.Invoke(
            null, new object?[] { job, authUser, authenticationEnabled })!);
        Assert.False((bool)method.Invoke(
            null, new object?[] { null, authUser, authenticationEnabled })!);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LiveReplayUsesConfiguredVisibilityAuthorization(bool authenticationEnabled, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "miic-visibility-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = root };
            var jobs = new UiJobRegistry(settings);
            var job = new UiJob { Prompt = "test", CreatedBy = "local display name" };
            jobs.Add(job);
            var envelopes = jobs.ReadEnvelopes(0).Envelopes;
            var method = typeof(UiWorkflow).GetMethod(
                "BuildVisibleEnvelopes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            var output = (List<string>)method.Invoke(null, new object?[]
            {
                envelopes, jobs, new UiVisibilityStore(settings), "", new UiProfileSnapshot(), authenticationEnabled,
            })!;
            Assert.NotEmpty(output);
            using var parsed = System.Text.Json.JsonDocument.Parse(output[0]);
            Assert.Equal(expected, parsed.RootElement.GetProperty("job").GetProperty("canHide").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
            Assert.Equal(2, store.ListPendingPurges().Count);
            var imageRecord = store.Snapshot().Records.Single(
                record => record.Kind == "image");
            store.MarkPurged(imageRecord, 3);

            var reloaded = new UiVisibilityStore(settings);

            Assert.True(reloaded.IsPromptHidden("job-a"));
            Assert.False(reloaded.IsPromptHidden("job-b"));
            Assert.True(reloaded.IsImageHidden("job-b", "gpt2", 2));
            Assert.False(reloaded.IsImageHidden("job-b", "gpt2", 1));
            Assert.True(reloaded.HasHiddenImages("job-b"));
            Assert.Equal(2, reloaded.Snapshot().Records.Count);
            Assert.Single(reloaded.ListPendingPurges());
            Assert.Equal(
                3,
                reloaded.Snapshot().Records.Single(
                    record => record.Kind == "image").PurgedAtUnixMs);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImageDeletionRemovesExactArtifactsAndRedactsPersistedEvents()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-deletion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = root };
            var registry = new UiJobRegistry(settings);
            var job = new UiJob
            {
                Id = "delete-test",
                Prompt = "test",
                CreatorLogin = "creator",
            };
            registry.Add(job);

            var files = new Dictionary<string, string>
            {
                ["gpt2/0"] = Path.Combine(root, "result-0.bin"),
                ["gpt2~p0/0"] = Path.Combine(root, "partial-0.bin"),
                ["gpt2/1"] = Path.Combine(root, "result-1.bin"),
                ["grid/0"] = Path.Combine(root, "grid.bin"),
            };
            foreach (var pair in files)
            {
                File.WriteAllBytes(pair.Value, [1, 2, 3]);
                var slash = pair.Key.LastIndexOf('/');
                job.StoreImagePath(
                    pair.Key.Substring(0, slash),
                    int.Parse(pair.Key.AsSpan(slash + 1)),
                    pair.Value,
                    "application/octet-stream");
            }
            job.Emit(new
            {
                type = "gen-result",
                gen = "gpt2",
                ok = true,
                images = new[] { "/deleted-zero", "/kept-one" },
                thumbs = new[] { "/deleted-thumb", "/kept-thumb" },
                partialImages = new string?[] { "/deleted-partial", null },
                partialThumbs = new string?[] { "/deleted-partial-thumb", null },
                progressImages = new[]
                {
                    new
                    {
                        partialIndex = 0,
                        imageIndex = 0,
                        url = "/deleted-progress",
                    },
                },
            });
            job.Emit(new { type = "grid", url = "/deleted-grid" });
            job.MarkDone();
            var (originalEvents, _) = job.ReadFrom(0);
            Assert.True(job.TryReplacePersistedEvents(originalEvents));
            var preB2Backup = Path.Combine(
                root,
                "UiHistory",
                job.Id,
                "events.jsonl.pre-b2");
            Assert.True(File.Exists(preB2Backup));

            var artifacts = job.ListImageDeletionArtifacts("gpt2", 0);
            Assert.Equal(
                ["gpt2/0", "gpt2~p0/0", "grid/0"],
                artifacts.Select(info => info.Key).OrderBy(key => key).ToArray());

            await using var runner = new UiJobRunner(
                settings,
                new MultiClientRunStats(),
                new RunOptions());
            Assert.Equal(
                3,
                await runner.DeleteHiddenArtifactsAsync(
                    job,
                    "image",
                    "gpt2",
                    0,
                    CancellationToken.None));

            Assert.False(File.Exists(files["gpt2/0"]));
            Assert.False(File.Exists(files["gpt2~p0/0"]));
            Assert.False(File.Exists(files["grid/0"]));
            Assert.True(File.Exists(files["gpt2/1"]));
            Assert.Equal(4, job.ListPersistedImages().Count);
            Assert.False(File.Exists(preB2Backup));
            var (redactedEvents, _) = job.ReadFrom(0);
            var redactedJson = string.Join("\n", redactedEvents);
            Assert.DoesNotContain("/deleted-zero", redactedJson);
            Assert.DoesNotContain("/deleted-progress", redactedJson);
            Assert.DoesNotContain("/deleted-grid", redactedJson);
            Assert.Contains("/kept-one", redactedJson);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ImageDeletionRemovesEveryUsersFavoriteForExactImage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-favorite-deletion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = root };
            var store = new UiFavoriteStore(settings);
            foreach (var user in new[] { "one", "two" })
            {
                store.Set(new UiFavoriteRecord
                {
                    User = user,
                    JobId = "job",
                    Generator = "gpt2",
                    ImageIndex = 0,
                    GeneratorImageCount = 2,
                    Prompt = "test",
                    CreatedBy = "creator",
                    JobCreatedAtUnixMs = 1,
                    ImageUrl = "/image/0",
                    ThumbUrl = "/thumb/0",
                    FavoritedAtUnixMs = 2,
                }, favorite: true);
            }
            store.Set(new UiFavoriteRecord
            {
                User = "one",
                JobId = "job",
                Generator = "gpt2",
                ImageIndex = 1,
                GeneratorImageCount = 2,
                Prompt = "test",
                CreatedBy = "creator",
                JobCreatedAtUnixMs = 1,
                ImageUrl = "/image/1",
                ThumbUrl = "/thumb/1",
                FavoritedAtUnixMs = 2,
            }, favorite: true);

            Assert.Equal(
                2,
                store.RemoveHiddenResource("image", "job", "gpt2", 0));
            var remaining = Assert.Single(store.Snapshot().Records);
            Assert.Equal(1, remaining.ImageIndex);
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

public class GrokWebVideoAvailabilityTests
{
    [Fact]
    public async Task VideoIsAvailableWithCookieAndCompleteSigningPair()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-grok-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cookiePath = Path.Combine(root, "cookies.json");
        await File.WriteAllTextAsync(cookiePath, "[]");
        try
        {
            var settings = CreateSettings(root, cookiePath);
            await using var runner = new UiJobRunner(
                settings,
                new MultiClientRunStats(),
                new RunOptions());

            Assert.Null(runner.DescribeAvailabilityProblem(UiJobRunner.KeyGrokWebVideo));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VideoFailsClosedWithoutSigningPair()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-grok-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cookiePath = Path.Combine(root, "cookies.json");
        await File.WriteAllTextAsync(cookiePath, "[]");
        try
        {
            var settings = CreateSettings(root, cookiePath);
            settings.GrokWebStatsigVerificationKey = "";
            settings.GrokWebStatsigAnimationKey = "";
            await using var runner = new UiJobRunner(
                settings,
                new MultiClientRunStats(),
                new RunOptions());

            Assert.Contains(
                "not configured",
                runner.DescribeAvailabilityProblem(UiJobRunner.KeyGrokWebVideo));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Settings CreateSettings(string root, string cookiePath)
    {
        return new Settings
        {
            ImageDownloadBaseFolder = root,
            LogFilePath = Path.Combine(root, "test.log"),
            GrokWebCookiePath = cookiePath,
            GrokWebStatsigVerificationKey = Convert.ToBase64String(new byte[48]),
            GrokWebStatsigAnimationKey = "0a",
        };
    }
}
