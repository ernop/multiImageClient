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

        // Matched case-insensitively against the raw error message. Billing is
        // checked before auth: a "payment required" body can also mention the
        // token, and topping up is the likelier fix.
        private static readonly string[] BillingMarkers =
        {
            "paymentrequired", "payment required", "insufficient balance",
            "insufficient credit", "out of credit", "billing", "quota",
            "recharge", "exceeded your current", "402",
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
            var billing = ContainsAny(error, BillingMarkers);
            var auth = !billing && ContainsAny(error, AuthMarkers);
            if (!billing && !auth)
            {
                return null;
            }

            // Cookie-session providers have no billing page — expired or
            // revoked cookies are the fix for any 401/403-shaped failure.
            switch (generatorKey)
            {
                case UiJobRunner.KeyGrokWeb:
                case UiJobRunner.KeyGrokWebVideo:
                    return new Hint(
                        "grok.com session cookies look expired/invalid — log in at grok.com and re-export cookies to the GrokWebCookiePath file, then restart",
                        "https://grok.com");
                case UiJobRunner.KeyMetaWeb:
                    return new Hint(
                        "meta.ai session looks expired/invalid — re-export cookies (MetaWebCookiePath) or re-run --meta-web --meta-web-headed to log in again",
                        "https://www.meta.ai");
            }

            var (provider, settingsField, billingUrl, keysUrl) = generatorKey switch
            {
                UiJobRunner.KeyGpt2 or UiJobRunner.KeyGpt1 or UiJobRunner.KeyGpt1Mini =>
                    ("OpenAI", "OpenAIApiKey",
                     "https://platform.openai.com/settings/organization/billing/overview",
                     "https://platform.openai.com/api-keys"),
                UiJobRunner.KeyIdeogram or UiJobRunner.KeyIdeogramV3
                    or UiJobRunner.KeyIdeogramV2 =>
                    ("Ideogram", "IdeogramApiKey",
                     "https://ideogram.ai/manage-api",
                     "https://ideogram.ai/manage-api"),
                UiJobRunner.KeyRecraft or UiJobRunner.KeyRecraftV41Utility
                    or UiJobRunner.KeyRecraftV41Pro or UiJobRunner.KeyRecraftV41Vector
                    or UiJobRunner.KeyRecraftV3 or UiJobRunner.KeyRecraftV4
                    or UiJobRunner.KeyRecraftV4Pro =>
                    ("Recraft", "RecraftApiKey",
                     "https://www.recraft.ai/profile/api",
                     "https://www.recraft.ai/profile/api"),
                UiJobRunner.KeyBfl =>
                    ("Black Forest Labs", "BFLApiKey",
                     "https://dashboard.bfl.ai",
                     "https://dashboard.bfl.ai"),
                UiJobRunner.KeyKrea or UiJobRunner.KeyKreaTurbo or UiJobRunner.KeyKreaLarge =>
                    ("Krea", "KreaApiKey",
                     "https://www.krea.ai/app/api",
                     "https://www.krea.ai/app/api/tokens"),
                UiJobRunner.KeyGoogle or UiJobRunner.KeyGooglePro =>
                    ("Google AI Studio", "GoogleGeminiApiKey",
                     "https://aistudio.google.com/apikey",
                     "https://aistudio.google.com/apikey"),
                UiJobRunner.KeyGrokApi or UiJobRunner.KeyGrokApiPro =>
                    ("xAI", "XAIGrokApiKey",
                     "https://console.x.ai",
                     "https://console.x.ai"),
                _ => (null, null, null, null),
            };
            if (provider == null)
            {
                return null;
            }

            return billing
                ? new Hint(
                    $"{provider} balance/quota is exhausted — top up or enable auto-recharge, then just resend (no restart needed)",
                    billingUrl)
                : new Hint(
                    $"{provider} rejected the API key — if it was regenerated, put the new key in settings.json ({settingsField}) and restart the server",
                    keysUrl);
        }

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
