using System.Reflection;
using System.Text.Json;
using RecraftAPIClient;

namespace MultiImageClient.Tests;

public sealed class PromptLimitTests
{
    // The gpt-image extra text that production appended to the 34,874-character
    // prompt rejected on 2026-10-12.
    private const string DefaultGptExtraText =
        "Render in normal daytime lighting. Absolutely do not make the image dim, murky, grimy, "
        + "muddy, gloomy, shadow-choked, underexposed, hazy, dusk-like, night-like, or dark. "
        + "No smudged, muddy, overly fine micro-texture.";

    // Every target key the runner declares, so a new key constant is covered
    // without editing this test.
    private static IReadOnlyList<string> EveryUiKey()
        => typeof(UiJobRunner)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Key", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat(UiJobRunner.ImageGeneratorKeys)
            .Concat(UiJobRunner.DescribeKeys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EveryUiTargetDeclaresAPromptLimit()
    {
        var keys = EveryUiKey();
        Assert.Contains(UiJobRunner.KeyLayoutMap, keys);
        Assert.Contains(UiJobRunner.KeyGrokWebVideo, keys);

        var undeclared = keys
            .Where(key => Record.Exception(() => UiPromptLimits.For(key)) != null)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Theory]
    [InlineData("gpt-image-9")]
    [InlineData("GPT2")]
    [InlineData("")]
    public void UnknownKeyIsAnError(string key)
    {
        Assert.Throws<ArgumentException>(() => UiPromptLimits.For(key));
    }

    [Theory]
    [InlineData(UiJobRunner.KeyGpt2, 32_000)]
    [InlineData(UiJobRunner.KeyGpt25Sunburst, 32_000)]
    [InlineData(UiJobRunner.KeyGpt25Flare, 32_000)]
    [InlineData(UiJobRunner.KeyGpt1, 32_000)]
    [InlineData(UiJobRunner.KeyGpt1Mini, 32_000)]
    [InlineData(UiJobRunner.KeyIdeogramV45, 10_000)]
    [InlineData(UiJobRunner.KeyRecraft, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Utility, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Pro, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Vector, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV4, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV4Pro, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV3, 1_000)]
    public void PublishedCharacterLimitsRejectLongerPrompts(string key, int max)
    {
        Assert.Equal(
            new UiPromptLimit(max, UiPromptLimitUnit.Characters, UiPromptLimitPolicy.Reject, TextToImageOnly: false),
            UiPromptLimits.For(key));
    }

    [Fact]
    public void RejectingTargetsAreExactlyTheListedOnes()
    {
        var rejecting = EveryUiKey()
            .Where(key => UiPromptLimits.For(key)?.Policy == UiPromptLimitPolicy.Reject)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                UiJobRunner.KeyGpt1,
                UiJobRunner.KeyGpt1Mini,
                UiJobRunner.KeyGpt2,
                UiJobRunner.KeyGpt25Flare,
                UiJobRunner.KeyGpt25Sunburst,
                UiJobRunner.KeyIdeogramV45,
                UiJobRunner.KeyRecraft,
                UiJobRunner.KeyRecraftV3,
                UiJobRunner.KeyRecraftV4,
                UiJobRunner.KeyRecraftV4Pro,
                UiJobRunner.KeyRecraftV41Pro,
                UiJobRunner.KeyRecraftV41Utility,
                UiJobRunner.KeyRecraftV41Vector,
            }.OrderBy(key => key, StringComparer.Ordinal),
            rejecting);
    }

    [Fact]
    public void OnlyGrokWebTruncatesAndOnlyOnItsTextToImagePath()
    {
        var truncating = EveryUiKey()
            .Where(key => UiPromptLimits.For(key)?.Policy == UiPromptLimitPolicy.Truncate)
            .ToArray();

        Assert.Equal(new[] { UiJobRunner.KeyGrokWeb }, truncating);
        Assert.Equal(
            new UiPromptLimit(
                GrokWebClient.MaxPromptUtf8Bytes,
                UiPromptLimitUnit.Utf8Bytes,
                UiPromptLimitPolicy.Truncate,
                TextToImageOnly: true),
            UiPromptLimits.For(UiJobRunner.KeyGrokWeb));
        Assert.Null(UiPromptLimits.FindRejection(UiJobRunner.KeyGrokWeb, new string('a', 20_000), extraText: null));
    }

    [Fact]
    public void AnalysisTargetsDeclareNoLimit()
    {
        // Describe and layout-map calls append a reply contract to the
        // instruction and return before the per-target check, so a limit for
        // them needs its own measurement first.
        foreach (var key in UiJobRunner.DescribeKeys.Append(UiJobRunner.KeyLayoutMap))
        {
            Assert.Null(UiPromptLimits.For(key));
        }
    }

    [Fact]
    public void ProductionPromptOf20261012IsRefusedForEveryOverLimitTarget()
    {
        Assert.Equal(214, DefaultGptExtraText.Length);
        var prompt = new string('a', 34_874);
        var keys = new[]
        {
            UiJobRunner.KeyGpt25Sunburst,
            UiJobRunner.KeyGpt25Flare,
            UiJobRunner.KeyIdeogramV45,
            UiJobRunner.KeyGrokWeb,
            UiJobRunner.KeyGoogle,
        };
        var extraTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UiJobRunner.KeyGpt25Sunburst] = DefaultGptExtraText,
            [UiJobRunner.KeyGpt25Flare] = DefaultGptExtraText,
        };

        var overruns = UiPromptLimits.FindRejections(keys, prompt, extraTexts);

        Assert.Equal(
            new[]
            {
                new UiPromptOverrun(UiJobRunner.KeyGpt25Sunburst, 35_090, 32_000, UiPromptLimitUnit.Characters, IncludesExtraText: true),
                new UiPromptOverrun(UiJobRunner.KeyGpt25Flare, 35_090, 32_000, UiPromptLimitUnit.Characters, IncludesExtraText: true),
                new UiPromptOverrun(UiJobRunner.KeyIdeogramV45, 34_874, 10_000, UiPromptLimitUnit.Characters, IncludesExtraText: false),
            },
            overruns);
        Assert.Equal(
            "Prompt too long for gpt-image-2.5 sunburst (35,090 of 32,000 characters with its extra text), "
            + "gpt-image-2.5 flare (35,090 of 32,000 characters with its extra text), "
            + "Ideogram 4.5 (34,874 of 10,000 characters). "
            + "Shorten the prompt or deselect these models. Nothing was sent.",
            UiPromptLimits.DescribeJobRejection(overruns));
        Assert.Equal(
            "Prompt too long for gpt-image-2.5 sunburst: 35,090 characters including its extra text; "
            + "the limit is 32,000. The request was not sent.",
            UiPromptLimits.DescribeTargetRejection(overruns[0]));
    }

    [Fact]
    public void AJobRejectionNeedsAnOverrun()
    {
        Assert.Throws<ArgumentException>(() => UiPromptLimits.DescribeJobRejection([]));
    }

    [Theory]
    [InlineData(10_000, false)]
    [InlineData(10_001, true)]
    public void TheLimitItselfIsAllowed(int length, bool rejected)
    {
        var overrun = UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, new string('a', length), extraText: null);

        Assert.Equal(rejected, overrun != null);
    }

    [Fact]
    public void ExtraTextAndItsSeparatorCount()
    {
        // 9,990 + "\n\n" + 8 is exactly the 10,000-character limit.
        Assert.Null(UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, new string('a', 9_990), " daylight\n"));

        Assert.Equal(
            new UiPromptOverrun(UiJobRunner.KeyIdeogramV45, 10_001, 10_000, UiPromptLimitUnit.Characters, IncludesExtraText: true),
            UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, new string('a', 9_991), " daylight\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void BlankExtraTextAddsNothing(string? extraText)
    {
        Assert.Equal("prompt", UiPromptLimits.ComposeWirePrompt("prompt", extraText));
        Assert.Null(UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, new string('a', 10_000), extraText));
    }

    [Fact]
    public void WirePromptAppendsTheTrimmedExtraTextAfterABlankLine()
    {
        Assert.Equal("prompt\n\ndaylight", UiPromptLimits.ComposeWirePrompt("prompt", "  daylight \n"));
    }

    [Fact]
    public void CharactersAreCodePointsAndFormLineBreaksCountTwice()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 10_000));

        Assert.Equal(10_000, UiPromptLimits.Measure(emoji, UiPromptLimitUnit.Characters));
        Assert.Equal(40_000, UiPromptLimits.Measure(emoji, UiPromptLimitUnit.Utf8Bytes));
        Assert.Null(UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, emoji, extraText: null));
        Assert.NotNull(UiPromptLimits.FindRejection(UiJobRunner.KeyIdeogramV45, emoji + "😀", extraText: null));
        // Browsers submit each textarea line break as CRLF.
        Assert.Equal(4, UiPromptLimits.Measure("a\r\nb", UiPromptLimitUnit.Characters));
    }

    [Fact]
    public void ConfigPublishesTheLimitTheComposerReads()
    {
        Assert.Equal(
            """{"max":32000,"unit":"characters","policy":"reject","textToImageOnly":false}""",
            JsonSerializer.Serialize(UiPromptLimits.Publish(UiJobRunner.KeyGpt25Sunburst)));
        Assert.Equal(
            """{"max":8192,"unit":"utf8Bytes","policy":"truncate","textToImageOnly":true}""",
            JsonSerializer.Serialize(UiPromptLimits.Publish(UiJobRunner.KeyGrokWeb)));
        Assert.Null(UiPromptLimits.Publish(UiJobRunner.KeyGoogle));
    }

    [Fact]
    public async Task RunnerRefusesAnOverLimitTargetWithoutBuildingIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "multi-image-client-prompt-limit-" + Guid.NewGuid().ToString("N"));
        try
        {
            // No Recraft key is configured, so reaching the generator would
            // report a missing key instead of the prompt length.
            var settings = new Settings
            {
                ImageDownloadBaseFolder = root,
                LogFilePath = Path.Combine(root, "test.log"),
                EnableGenerationArchive = false,
            };
            var registry = new UiJobRegistry(settings);
            var job = new UiJob
            {
                Prompt = new string('a', 1_001),
                CreatedBy = "tester",
                GeneratorKeys = [UiJobRunner.KeyRecraftV3],
            };
            registry.Add(job);
            await using var runner = new UiJobRunner(settings, new MultiClientRunStats(), new RunOptions());

            await runner.RunJobAsync(job, new UiJobSpec { GeneratorKeys = [UiJobRunner.KeyRecraftV3] });

            var (events, done) = job.ReadFrom(0);
            Assert.True(done);
            var result = Assert.Single(
                events.Select(e => JsonSerializer.Deserialize<JsonElement>(e)),
                e => e.GetProperty("type").GetString() == "gen-result");
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "Prompt too long for Recraft V3: 1,001 characters; the limit is 1,000. The request was not sent.",
                result.GetProperty("error").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("errorHint").ValueKind);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(RecraftModel.recraftv2, 1_000)]
    [InlineData(RecraftModel.recraftv3, 1_000)]
    [InlineData(RecraftModel.recraftv4, 10_000)]
    [InlineData(RecraftModel.recraftv4_pro, 10_000)]
    [InlineData(RecraftModel.recraftv4_1, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_pro, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_vector, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_utility, 10_000)]
    public void RecraftLimitFollowsTheModel(RecraftModel model, int max)
    {
        Assert.Equal(max, RecraftClient.MaxPromptChars(model));
    }

    [Fact]
    public void EveryRecraftModelHasALimit()
    {
        foreach (var model in Enum.GetValues<RecraftModel>())
        {
            Assert.InRange(RecraftClient.MaxPromptChars(model), 1_000, 10_000);
        }
    }

    [Fact]
    public async Task RecraftClientRefusesOverLimitPromptsBeforeSending()
    {
        var client = new RecraftClient("unused-test-key");

        var v3 = await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateImageAsync(
            new string('a', 1_001), artistic_level: "", substyle: "", style: "any", size: "",
            model: RecraftModel.recraftv3));
        var v41 = await Assert.ThrowsAsync<ArgumentException>(() => client.ImageToImageAsync(
            [1, 2, 3], new string('a', 10_001), 0.1f, RecraftModel.recraftv4_1));

        Assert.StartsWith("Recraft recraftv3 prompts are limited to 1,000 characters; this prompt has 1,001.", v3.Message);
        Assert.StartsWith("Recraft recraftv4_1 prompts are limited to 10,000 characters; this prompt has 10,001.", v41.Message);
    }
}
