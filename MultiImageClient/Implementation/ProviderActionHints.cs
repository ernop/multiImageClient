#nullable enable
using System;

namespace MultiImageClient
{
    /// <summary>
    /// Turns a failed generation's raw provider error into the concrete next
    /// step for the person at the keyboard: what happened in plain words, and
    /// the one URL where it gets fixed (billing page, key console, or cookie
    /// re-export). Consulted on the UI job FAIL path so the hint rides the
    /// gen-result event to the job card and lands in the log line.
    /// </summary>
    public static class ProviderActionHints
    {
        public sealed record Hint(string Text, string Url);
        private sealed record ProviderAccount(
            string Name,
            string SettingsField,
            string BillingUrl,
            string KeysUrl,
            string BillingAction);

        // Matched case-insensitively against the raw error message. Billing is
        // checked before auth: a "payment required" body can also mention the
        // token, and topping up is the likelier fix.
        private static readonly string[] BillingMarkers =
        {
            "paymentrequired", "payment required", "insufficient balance",
            "insufficient credit", "not enough credit", "out of credit",
            "credit balance", "billing_error", "billing error", "billing",
            "recharge", "exceeded your current quota", "usage limit",
            "spend limit", "402",
        };

        private static readonly string[] AuthMarkers =
        {
            "unauthorized", "api token", "api key", "access denied",
            "invalid_api_key", "authentication", "401",
        };

        public static Hint? For(string generatorKey, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(generatorKey) || string.IsNullOrWhiteSpace(errorMessage))
            {
                return null;
            }
            var error = errorMessage.ToLowerInvariant();
            if (IsGrokWebKey(generatorKey)
                && error.Contains("request rejected by anti-bot rules", StringComparison.Ordinal))
            {
                return new Hint(
                    "grok.com rejected automated consumer-web access — use grok-api or grok-api 2.0 through the official api.x.ai image-edit endpoint, or use grok.com interactively",
                    "https://docs.x.ai/developers/model-capabilities/images/editing");
            }
            var billing = ContainsAny(error, BillingMarkers);
            var auth = !billing && ContainsAny(error, AuthMarkers);
            if (!billing && !auth)
            {
                return null;
            }

            // Consumer-session billing is separate from API billing. Grok has
            // consumer usage credits; Meta exposes no Muse Image payment page.
            switch (generatorKey)
            {
                case UiJobRunner.KeyGrokWeb:
                case UiJobRunner.KeyGrokWebChat:
                case UiJobRunner.KeyGrokWebVideo:
                    return billing
                        ? new Hint(
                            "grok.com consumer usage credits are exhausted — buy Extra Usage Credits or enable automatic top-up, then resend",
                            "https://grok.com/?_s=usage")
                        : new Hint(
                            "grok.com session cookies look expired/invalid — log in at grok.com and re-export cookies to the GrokWebCookiePath file, then restart",
                            "https://grok.com");
                case UiJobRunner.KeyMetaWeb:
                    return auth
                        ? new Hint(
                            "meta.ai session looks expired/invalid — re-export cookies (MetaWebCookiePath) or re-run --meta-web --meta-web-headed to log in again",
                            "https://www.meta.ai")
                        : null;
            }

            var account = AccountFor(generatorKey);
            if (account == null)
            {
                return null;
            }

            return billing
                ? new Hint(
                    $"{account.Name} API billing or quota blocked this request — {account.BillingAction}, then resend; no restart is needed",
                    account.BillingUrl)
                : new Hint(
                    $"{account.Name} rejected the API key — if it was regenerated, put the new key in settings.json ({account.SettingsField}) and restart the server",
                    account.KeysUrl);
        }

        private static ProviderAccount? AccountFor(string generatorKey) => generatorKey switch
        {
            UiJobRunner.KeyGpt2 or UiJobRunner.KeyGpt25Sunburst
                or UiJobRunner.KeyGpt25Flare
                or UiJobRunner.KeyGpt1 or UiJobRunner.KeyGpt1Mini
                or UiJobRunner.KeyDescribeOpenAi =>
                new(
                    "OpenAI",
                    nameof(Settings.OpenAIApiKey),
                    "https://platform.openai.com/settings/organization/billing/overview",
                    "https://platform.openai.com/api-keys",
                    "add API credits or enable auto-recharge"),
            UiJobRunner.KeyIdeogram or UiJobRunner.KeyIdeogramV3
                or UiJobRunner.KeyIdeogramV2 or UiJobRunner.KeyDescribeIdeogram =>
                new(
                    "Ideogram",
                    nameof(Settings.IdeogramApiKey),
                    "https://ideogram.ai/manage-api",
                    "https://ideogram.ai/manage-api",
                    "add prepaid API credits or enable auto-recharge"),
            UiJobRunner.KeyRecraft or UiJobRunner.KeyRecraftV41Utility
                or UiJobRunner.KeyRecraftV41Pro or UiJobRunner.KeyRecraftV41Vector
                or UiJobRunner.KeyRecraftV3 or UiJobRunner.KeyRecraftV4
                or UiJobRunner.KeyRecraftV4Pro =>
                new(
                    "Recraft",
                    nameof(Settings.RecraftApiKey),
                    "https://app.recraft.ai/profile/api",
                    "https://app.recraft.ai/profile/api",
                    "buy more API units"),
            UiJobRunner.KeyBfl or UiJobRunner.KeyBflFlux2Pro
                or UiJobRunner.KeyBflFlux2Max or UiJobRunner.KeyBflFlux2Flex
                or UiJobRunner.KeyBflFlux2Klein4b or UiJobRunner.KeyBflFlux2Klein9bPreview
                or UiJobRunner.KeyBflFlux2Klein9b or UiJobRunner.KeyBflKontextPro
                or UiJobRunner.KeyBflKontextMax or UiJobRunner.KeyBflFlux11Ultra
                or UiJobRunner.KeyBflFlux11 or UiJobRunner.KeyBflFluxPro
                or UiJobRunner.KeyBflFluxDev =>
                new(
                    "Black Forest Labs",
                    nameof(Settings.BFLApiKey),
                    "https://dashboard.bfl.ai",
                    "https://dashboard.bfl.ai",
                    "open API → Credits and add credits"),
            UiJobRunner.KeyKrea or UiJobRunner.KeyKreaTurbo or UiJobRunner.KeyKreaLarge =>
                new(
                    "Krea",
                    nameof(Settings.KreaApiKey),
                    "https://www.krea.ai/app/api/",
                    "https://www.krea.ai/app/api/tokens",
                    "add workspace API balance"),
            UiJobRunner.KeyGoogle or UiJobRunner.KeyGooglePro
                or UiJobRunner.KeyDescribeGemini or UiJobRunner.KeyLayoutMap =>
                new(
                    "Google AI Studio",
                    nameof(Settings.GoogleGeminiApiKey),
                    "https://aistudio.google.com/billing",
                    "https://aistudio.google.com/apikey",
                    "buy credits or restore billing for the key's project"),
            UiJobRunner.KeyGrokApi or UiJobRunner.KeyGrokApiPro
                or UiJobRunner.KeyDescribeGrok =>
                new(
                    "xAI",
                    nameof(Settings.XAIGrokApiKey),
                    "https://console.x.ai/team/default/billing",
                    "https://console.x.ai/team/default/api-keys",
                    "buy prepaid API credits or raise the invoiced billing limit"),
            UiJobRunner.KeyDescribeClaude =>
                new(
                    "Anthropic",
                    nameof(Settings.AnthropicApiKey),
                    "https://platform.claude.com/settings/billing",
                    "https://platform.claude.com/settings/keys",
                    "buy API credits, fix payment details, or raise the API spend limit"),
            _ => null,
        };

        private static bool IsGrokWebKey(string generatorKey)
            => generatorKey is UiJobRunner.KeyGrokWeb
                or UiJobRunner.KeyGrokWebChat
                or UiJobRunner.KeyGrokWebVideo;

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
