namespace MultiImageClient;

public sealed class ProviderActionHintsTests
{
    [Theory]
    [InlineData(UiJobRunner.KeyRecraft)]
    [InlineData(UiJobRunner.KeyRecraftV41Utility)]
    [InlineData(UiJobRunner.KeyRecraftV41Pro)]
    [InlineData(UiJobRunner.KeyRecraftV41Vector)]
    [InlineData(UiJobRunner.KeyRecraftV3)]
    [InlineData(UiJobRunner.KeyRecraftV4)]
    [InlineData(UiJobRunner.KeyRecraftV4Pro)]
    public void RecraftNotEnoughCreditsLinksToApiUnitPurchasePage(string generatorKey)
    {
        var hint = ProviderActionHints.For(generatorKey, "Not enough credits");

        Assert.NotNull(hint);
        Assert.Contains("buy more API units", hint.Text);
        Assert.Equal("https://www.recraft.ai/profile/api", hint.Url);
    }

    [Theory]
    [InlineData(UiJobRunner.KeyGrokWeb)]
    [InlineData(UiJobRunner.KeyGrokWebChat)]
    [InlineData(UiJobRunner.KeyGrokWebVideo)]
    public void GrokWebAntiBotRejectionPointsToOfficialApi(string generatorKey)
    {
        var hint = ProviderActionHints.For(
            generatorKey,
            "Grok web app-chat failed (403). body={\"error\":{\"code\":7,\"message\":\"Request rejected by anti-bot rules.\"}}");

        Assert.NotNull(hint);
        Assert.Contains("official api.x.ai", hint.Text);
        Assert.DoesNotContain("cookies", hint.Text);
        Assert.Equal(
            "https://docs.x.ai/developers/model-capabilities/images/editing",
            hint.Url);
    }
}
