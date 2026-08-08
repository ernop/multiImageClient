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
}
