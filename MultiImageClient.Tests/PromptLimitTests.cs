using RecraftAPIClient;

namespace MultiImageClient.Tests;

public class PromptLimitTests
{
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
