namespace MultiImageClient;

public sealed class ProviderActionHintsTests
{
    private static readonly IReadOnlyDictionary<string, string> BillingUrlByApiKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UiJobRunner.KeyGpt2] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyGpt25Sunburst] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyGpt25Flare] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyGpt1] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyGpt1Mini] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyDescribeOpenAi] = "https://platform.openai.com/settings/organization/billing/overview",
            [UiJobRunner.KeyIdeogram] = "https://ideogram.ai/manage-api",
            [UiJobRunner.KeyIdeogramV3] = "https://ideogram.ai/manage-api",
            [UiJobRunner.KeyIdeogramV2] = "https://ideogram.ai/manage-api",
            [UiJobRunner.KeyDescribeIdeogram] = "https://ideogram.ai/manage-api",
            [UiJobRunner.KeyRecraft] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV41Utility] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV41Pro] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV41Vector] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV3] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV4] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyRecraftV4Pro] = "https://app.recraft.ai/profile/api",
            [UiJobRunner.KeyBfl] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Pro] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Max] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Flex] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Klein4b] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Klein9bPreview] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux2Klein9b] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflKontextPro] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflKontextMax] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux11Ultra] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFlux11] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFluxPro] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyBflFluxDev] = "https://dashboard.bfl.ai",
            [UiJobRunner.KeyKrea] = "https://www.krea.ai/app/api/",
            [UiJobRunner.KeyKreaTurbo] = "https://www.krea.ai/app/api/",
            [UiJobRunner.KeyKreaLarge] = "https://www.krea.ai/app/api/",
            [UiJobRunner.KeyGoogle] = "https://aistudio.google.com/billing",
            [UiJobRunner.KeyGooglePro] = "https://aistudio.google.com/billing",
            [UiJobRunner.KeyDescribeGemini] = "https://aistudio.google.com/billing",
            [UiJobRunner.KeyLayoutMap] = "https://aistudio.google.com/billing",
            [UiJobRunner.KeyGrokApi] = "https://console.x.ai/team/default/billing",
            [UiJobRunner.KeyGrokApiPro] = "https://console.x.ai/team/default/billing",
            [UiJobRunner.KeyDescribeGrok] = "https://console.x.ai/team/default/billing",
            [UiJobRunner.KeyDescribeClaude] = "https://platform.claude.com/settings/billing",
        };

    [Fact]
    public void EveryApiBackedUiTargetHasAResearchedBillingRecoveryLink()
    {
        var catalogKeys = UiJobRunner.ImageGeneratorKeys
            .Concat(UiJobRunner.DescribeKeys)
            .Append(UiJobRunner.KeyLayoutMap)
            .Except(
                new[]
                {
                    UiJobRunner.KeyLocalKlein,
                    UiJobRunner.KeyLocalZImage,
                    UiJobRunner.KeyGrokWeb,
                    UiJobRunner.KeyGrokWebChat,
                    UiJobRunner.KeyMetaWeb,
                },
                StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var mappedKeys = BillingUrlByApiKey.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(catalogKeys, mappedKeys);
        foreach (var key in catalogKeys)
        {
            var hint = ProviderActionHints.For(key, "HTTP 402 Payment Required");

            Assert.NotNull(hint);
            Assert.Equal(BillingUrlByApiKey[key], hint.Url);
        }
    }

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
        Assert.Equal("https://app.recraft.ai/profile/api", hint.Url);
    }

    [Theory]
    [InlineData(UiJobRunner.KeyDescribeOpenAi, "https://platform.openai.com/api-keys")]
    [InlineData(UiJobRunner.KeyDescribeIdeogram, "https://ideogram.ai/manage-api")]
    [InlineData(UiJobRunner.KeyDescribeClaude, "https://platform.claude.com/settings/keys")]
    [InlineData(UiJobRunner.KeyDescribeGemini, "https://aistudio.google.com/apikey")]
    [InlineData(UiJobRunner.KeyDescribeGrok, "https://console.x.ai/team/default/api-keys")]
    [InlineData(UiJobRunner.KeyLayoutMap, "https://aistudio.google.com/apikey")]
    public void AnalysisTargetAuthenticationFailuresLinkToKeyManagement(
        string generatorKey,
        string expectedUrl)
    {
        var hint = ProviderActionHints.For(generatorKey, "HTTP 401 Unauthorized API key");

        Assert.NotNull(hint);
        Assert.Equal(expectedUrl, hint.Url);
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

    [Theory]
    [InlineData(UiJobRunner.KeyGrokWeb)]
    [InlineData(UiJobRunner.KeyGrokWebChat)]
    [InlineData(UiJobRunner.KeyGrokWebVideo)]
    public void GrokWebCreditFailuresLinkToConsumerUsageCredits(string generatorKey)
    {
        var hint = ProviderActionHints.For(generatorKey, "HTTP 402 Payment Required");

        Assert.NotNull(hint);
        Assert.Contains("Extra Usage Credits", hint.Text);
        Assert.Equal("https://grok.com/?_s=usage", hint.Url);
    }

    [Fact]
    public void MetaWebDoesNotInventABillingLink()
    {
        Assert.Null(ProviderActionHints.For(
            UiJobRunner.KeyMetaWeb,
            "HTTP 402 Payment Required"));
    }

    [Theory]
    [InlineData(UiJobRunner.KeyLocalKlein)]
    [InlineData(UiJobRunner.KeyLocalZImage)]
    public void LocalTargetsDoNotInventApiBillingLinks(string generatorKey)
    {
        Assert.Null(ProviderActionHints.For(generatorKey, "HTTP 402 Payment Required"));
    }

    [Theory]
    [InlineData("Prompt dimensions are invalid.")]
    [InlineData("HTTP 429 RESOURCE_EXHAUSTED")]
    public void UnrelatedProviderFailureDoesNotClaimBillingWasTheCause(string error)
    {
        Assert.Null(ProviderActionHints.For(UiJobRunner.KeyGpt2, error));
    }
}
