namespace MultiImageClient.Tests;

public sealed class ComposerInputRoutingTests
{
    private static async Task WithRunnerAsync(Action<Settings> configure, Action<UiJobRunner> check)
    {
        var folder = Directory.CreateTempSubdirectory("mic-input-routing-").FullName;
        try
        {
            var settings = new Settings
            {
                ImageDownloadBaseFolder = folder,
                LogFilePath = Path.Combine(folder, "test.log"),
                EnableGenerationArchive = false,
            };
            configure(settings);
            await using var runner = new UiJobRunner(settings, new MultiClientRunStats(), new RunOptions());
            check(runner);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(UiJobRunner.KeyGpt2, 4)]
    [InlineData(UiJobRunner.KeyGpt25Sunburst, 4)]
    [InlineData(UiJobRunner.KeyGpt25Flare, 4)]
    [InlineData(UiJobRunner.KeyGrokApiPro, 5)]
    [InlineData(UiJobRunner.KeyIdeogramV45, 5)]
    [InlineData(UiJobRunner.KeyGrokApi, 1)]
    [InlineData(UiJobRunner.KeyGrokWeb, 1)]
    [InlineData(UiJobRunner.KeyGoogle, 1)]
    [InlineData(UiJobRunner.KeyGooglePro, 1)]
    [InlineData(UiJobRunner.KeyBfl, 1)]
    [InlineData(UiJobRunner.KeyIdeogram, 1)]
    [InlineData(UiJobRunner.KeyIdeogramV3, 1)]
    [InlineData(UiJobRunner.KeyRecraft, 1)]
    [InlineData(UiJobRunner.KeyKrea, 1)]
    [InlineData(UiJobRunner.KeyGpt1, 0)]
    [InlineData(UiJobRunner.KeyIdeogramV2, 0)]
    [InlineData(UiJobRunner.KeyLocalKlein, 0)]
    [InlineData(UiJobRunner.KeyDescribeClaude, 5)]
    [InlineData(UiJobRunner.KeyLayoutMap, 5)]
    public Task EachTargetReceivesItsPublishedShareOfTheInputs(string key, int expected)
        => WithRunnerAsync(_ => { }, runner => Assert.Equal(expected, runner.InputImageLimit(key)));

    [Fact]
    public Task GrokWebReceivesNoInputWhileSigningIsUnusable()
        => WithRunnerAsync(
            settings => settings.GrokWebStatsigVerificationKey = "incomplete-pair",
            runner =>
            {
                Assert.False(runner.IsImageCapableForCurrentSettings(UiJobRunner.KeyGrokWeb));
                Assert.Equal(0, runner.InputImageLimit(UiJobRunner.KeyGrokWeb));
            });
}
