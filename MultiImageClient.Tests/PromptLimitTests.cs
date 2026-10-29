using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using RecraftAPIClient;

namespace MultiImageClient.Tests;

public class PromptLimitTests
{
    // Every UI target without a limit in UiJobRunner.MaxPromptCharsFor, with
    // the reason. A new target fails EveryUiTargetHasALimitOrAListedReason
    // until it gets one or the other. See docs/prompt-length-limits.md.
    private static readonly string[] KeysWithoutAPublishedLimit =
    [
        // xAI publishes no limit for grok-imagine-image-2.0.
        UiJobRunner.KeyGrokApiPro,
        // grok-web truncates text-to-image prompts to
        // GrokWebClient.MaxPromptUtf8Bytes instead. Its app-chat routes (edit,
        // chat, video) have no known limit.
        UiJobRunner.KeyGrokWeb,
        UiJobRunner.KeyGrokWebChat,
        UiJobRunner.KeyGrokWebVideo,
        // Ideogram's API pages state no limit for 4.0, 3.0, or 2.0.
        UiJobRunner.KeyIdeogram,
        UiJobRunner.KeyIdeogramV3,
        UiJobRunner.KeyIdeogramV2,
        // BFL states token limits, not character limits.
        UiJobRunner.KeyBfl,
        UiJobRunner.KeyBflFlux2Pro,
        UiJobRunner.KeyBflFlux2Max,
        UiJobRunner.KeyBflFlux2Flex,
        UiJobRunner.KeyBflFlux2Klein4b,
        UiJobRunner.KeyBflFlux2Klein9bPreview,
        UiJobRunner.KeyBflFlux2Klein9b,
        UiJobRunner.KeyBflKontextPro,
        UiJobRunner.KeyBflKontextMax,
        UiJobRunner.KeyBflFlux11Ultra,
        UiJobRunner.KeyBflFlux11,
        UiJobRunner.KeyBflFluxPro,
        UiJobRunner.KeyBflFluxDev,
        UiJobRunner.KeyKrea,
        UiJobRunner.KeyKreaTurbo,
        UiJobRunner.KeyKreaLarge,
        // Google's token limits exceed every prompt the composer accepts.
        UiJobRunner.KeyGoogle,
        UiJobRunner.KeyGooglePro,
        // Local text encoders truncate internally; meta-web is disabled in the UI.
        UiJobRunner.KeyLocalKlein,
        UiJobRunner.KeyLocalZImage,
        UiJobRunner.KeyMetaWeb,
        // Analysis models limit input in tokens, far above any composer prompt.
        // Ideogram describe sends no prompt.
        UiJobRunner.KeyDescribeIdeogram,
        UiJobRunner.KeyDescribeOpenAi,
        UiJobRunner.KeyDescribeClaude,
        UiJobRunner.KeyDescribeGemini,
        UiJobRunner.KeyDescribeGeminiArgon,
        UiJobRunner.KeyDescribeGrok,
        UiJobRunner.KeyLayoutMap,
    ];

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
    public void EveryUiTargetHasALimitOrAListedReason()
    {
        var keys = EveryUiKey();
        Assert.Contains(UiJobRunner.KeyGpt25Sunburst, keys);
        Assert.Contains(UiJobRunner.KeyLayoutMap, keys);

        var unclassified = keys
            .Where(key => UiJobRunner.MaxPromptCharsFor(key, imageSent: false) is null
                && UiJobRunner.MaxPromptCharsFor(key, imageSent: true) is null
                && !KeysWithoutAPublishedLimit.Contains(key))
            .ToList();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void ListedTargetsHaveNoLimitOnEitherRoute()
    {
        foreach (var key in KeysWithoutAPublishedLimit)
        {
            Assert.Null(UiJobRunner.MaxPromptCharsFor(key, imageSent: false));
            Assert.Null(UiJobRunner.MaxPromptCharsFor(key, imageSent: true));
        }
    }

    // Browsers send each prompt line break as CR LF in multipart forms (Chrome
    // 154, checked 2026-10-12), and ReadFormAsync keeps it. prompt-limits.js
    // counts one character per line break, which is right only while the job
    // stores the prompt with LF.
    [Fact]
    public async Task FormPromptReachesTheJobWithLfLineBreaks()
    {
        const string body = "--b\r\nContent-Disposition: form-data; name=\"prompt\"\r\n\r\n"
            + "  line one\r\nline two\rline three\r\n\r\n--b--\r\n";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=b";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var form = await context.Request.ReadFormAsync();

        Assert.Equal("  line one\r\nline two\rline three\r\n", form["prompt"].ToString());
        Assert.Equal("line one\nline two\nline three", UiJobRunner.PromptFromFormField(form["prompt"].ToString()));
    }

    [Fact]
    public void FormLineBreaksCountOneCharacterEach()
    {
        // 500 lines of one character: 999 characters in the text box, 1,498 as sent.
        var sent = string.Join("\r\n", Enumerable.Repeat("a", 500));

        var prompt = UiJobRunner.PromptFromFormField(sent);

        Assert.Equal(999, prompt.Length);
        Assert.Null(UiJobRunner.DescribePromptLimitProblem(UiJobRunner.KeyRecraftV3, prompt, imageSent: false));
    }

    [Theory]
    [InlineData(UiJobRunner.KeyGpt2, false, 32_000)]
    [InlineData(UiJobRunner.KeyGpt2, true, 32_000)]
    [InlineData(UiJobRunner.KeyGpt25Sunburst, false, 32_000)]
    [InlineData(UiJobRunner.KeyGpt25Flare, true, 32_000)]
    [InlineData(UiJobRunner.KeyGpt1, false, 32_000)]
    [InlineData(UiJobRunner.KeyGpt1Mini, false, 32_000)]
    [InlineData(UiJobRunner.KeyIdeogramV45, false, 10_000)]
    [InlineData(UiJobRunner.KeyIdeogramV45, true, 10_000)]
    [InlineData(UiJobRunner.KeyRecraft, false, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Utility, true, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Pro, false, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV41Vector, false, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV4, false, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV4Pro, true, 10_000)]
    [InlineData(UiJobRunner.KeyRecraftV3, false, 1_000)]
    [InlineData(UiJobRunner.KeyRecraftV3, true, 1_000)]
    [InlineData(UiJobRunner.KeyGrokApi, false, 16_000)]
    public void PublishedLimitsApplyToTheirTargets(string key, bool imageSent, int expected)
    {
        Assert.Equal(expected, UiJobRunner.MaxPromptCharsFor(key, imageSent));
    }

    [Theory]
    [InlineData(UiJobRunner.KeyGrokApi, true)]
    [InlineData(UiJobRunner.KeyGrokApiPro, false)]
    [InlineData(UiJobRunner.KeyGrokWeb, false)]
    [InlineData(UiJobRunner.KeyIdeogram, false)]
    [InlineData(UiJobRunner.KeyIdeogramV3, true)]
    [InlineData(UiJobRunner.KeyBfl, false)]
    [InlineData(UiJobRunner.KeyGoogle, false)]
    [InlineData(UiJobRunner.KeyDescribeOpenAi, true)]
    public void TargetsWithoutAPublishedLimitAreNotChecked(string key, bool imageSent)
    {
        Assert.Null(UiJobRunner.MaxPromptCharsFor(key, imageSent));
        Assert.Null(UiJobRunner.DescribePromptLimitProblem(key, new string('a', 50_000), imageSent));
    }

    [Fact]
    public void LimitIsInclusiveAndCountsUnicodeCharacters()
    {
        // 1,000 characters but 1,001 UTF-16 units: the emoji is one character.
        var atLimit = new string('a', 999) + "😀";
        Assert.Null(UiJobRunner.DescribePromptLimitProblem(UiJobRunner.KeyRecraftV3, atLimit, imageSent: false));

        var problem = UiJobRunner.DescribePromptLimitProblem(UiJobRunner.KeyRecraftV3, atLimit + "b", imageSent: false);
        Assert.Equal(
            $"Recraft V3 accepts at most {1_000:N0} prompt characters. Its prompt has {1_001:N0}. Nothing was sent.",
            problem);
    }

    [Fact]
    public void GrokApiGenerationLimitDoesNotApplyToEdits()
    {
        var prompt = new string('a', 16_001);
        Assert.NotNull(UiJobRunner.DescribePromptLimitProblem(UiJobRunner.KeyGrokApi, prompt, imageSent: false));
        Assert.Null(UiJobRunner.DescribePromptLimitProblem(UiJobRunner.KeyGrokApi, prompt, imageSent: true));
    }

    [Theory]
    [InlineData(RecraftModel.recraftv2, 1_000)]
    [InlineData(RecraftModel.recraftv3, 1_000)]
    [InlineData(RecraftModel.recraftv4, 10_000)]
    [InlineData(RecraftModel.recraftv4_pro, 10_000)]
    [InlineData(RecraftModel.recraftv4_1, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_pro, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_utility, 10_000)]
    [InlineData(RecraftModel.recraftv4_1_vector, 10_000)]
    public void RecraftRejectsOverLimitPromptsInsteadOfTruncating(RecraftModel model, int max)
    {
        Assert.Null(RecraftGenerator.DescribePromptLimitProblem(model, new string('a', max)));
        Assert.Equal(
            $"{model} accepts at most {max:N0} prompt characters. This prompt has {max + 1:N0}. Nothing was sent.",
            RecraftGenerator.DescribePromptLimitProblem(model, new string('a', max + 1)));
    }

    [Fact]
    public void EveryRecraftModelHasALimit()
    {
        foreach (var model in Enum.GetValues<RecraftModel>())
        {
            Assert.True(RecraftGenerator.MaxPromptChars(model) > 0);
        }
    }
}
