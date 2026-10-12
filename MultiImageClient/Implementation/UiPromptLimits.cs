#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using IdeogramAPIClient;
using RecraftAPIClient;

namespace MultiImageClient
{
    public enum UiPromptLimitUnit
    {
        Characters,
        Utf8Bytes,
    }

    public enum UiPromptLimitPolicy
    {
        // The provider rejects longer prompts, so the server refuses to send them.
        Reject,
        // Owner-approved send-stage truncation (grok-web only, 2026-09-09).
        Truncate,
    }

    /// TextToImageOnly marks a limit that binds only the target's text-to-image
    /// transport; its image path sends the full prompt.
    public sealed record UiPromptLimit(
        int Max,
        UiPromptLimitUnit Unit,
        UiPromptLimitPolicy Policy,
        bool TextToImageOnly);

    public sealed record UiPromptOverrun(
        string Key,
        int Measured,
        int Max,
        UiPromptLimitUnit Unit,
        bool IncludesExtraText);

    /// One registry of published prompt-length limits for every UI target.
    /// The composer (via /api/config), the job POST, and the per-target call
    /// path all measure the same wire prompt against these values, so a prompt
    /// the provider would reject never leaves the server.
    /// See docs/prompt-length-limits.md for sources and dates.
    public static class UiPromptLimits
    {
        // OpenAI Images API reference (generations and edits): "The maximum
        // length is 32000 characters for the GPT image models." Production
        // rejected a 35,090-character gpt-image-2.5 prompt on 2026-10-12.
        private const int OpenAIImageMaxPromptChars = 32_000;

        /// Null means the target has no published hard limit at composer scale.
        /// Unknown keys throw, so a new target cannot ship without an entry.
        public static UiPromptLimit? For(string key) => key switch
        {
            UiJobRunner.KeyGpt2 or UiJobRunner.KeyGpt25Sunburst
                or UiJobRunner.KeyGpt25Flare or UiJobRunner.KeyGpt1
                or UiJobRunner.KeyGpt1Mini
                => Reject(OpenAIImageMaxPromptChars),
            UiJobRunner.KeyIdeogramV45
                => Reject(IdeogramClient.IdeogramV45MaxPromptChars),
            UiJobRunner.KeyRecraft or UiJobRunner.KeyRecraftV41Utility
                or UiJobRunner.KeyRecraftV41Pro or UiJobRunner.KeyRecraftV41Vector
                or UiJobRunner.KeyRecraftV3 or UiJobRunner.KeyRecraftV4
                or UiJobRunner.KeyRecraftV4Pro
                => Reject(RecraftClient.MaxPromptChars(UiJobRunner.RecraftModelForKey(key))),
            UiJobRunner.KeyGrokWeb
                => new UiPromptLimit(
                    GrokWebClient.MaxPromptUtf8Bytes,
                    UiPromptLimitUnit.Utf8Bytes,
                    UiPromptLimitPolicy.Truncate,
                    TextToImageOnly: true),

            // Checked 2026-10-12: no published prompt limit.
            UiJobRunner.KeyIdeogram or UiJobRunner.KeyIdeogramV3
                or UiJobRunner.KeyIdeogramV2
                or UiJobRunner.KeyBfl or UiJobRunner.KeyBflFlux2Pro
                or UiJobRunner.KeyBflFlux2Max or UiJobRunner.KeyBflFlux2Flex
                or UiJobRunner.KeyBflFlux2Klein4b or UiJobRunner.KeyBflFlux2Klein9bPreview
                or UiJobRunner.KeyBflFlux2Klein9b or UiJobRunner.KeyBflKontextPro
                or UiJobRunner.KeyBflKontextMax or UiJobRunner.KeyBflFlux11Ultra
                or UiJobRunner.KeyBflFlux11 or UiJobRunner.KeyBflFluxPro
                or UiJobRunner.KeyBflFluxDev
                or UiJobRunner.KeyKrea or UiJobRunner.KeyKreaTurbo or UiJobRunner.KeyKreaLarge
                => null,
            // xAI publishes max_prompt_length in tokens (documented example:
            // 1,024 for grok-imagine-image 1.0.0). The server cannot count xAI
            // tokens exactly, so the xAI error stays visible instead.
            UiJobRunner.KeyGrokApi or UiJobRunner.KeyGrokApiPro
                => null,
            // grok.com app-chat (edit door, chat door, video) has no known
            // limit; only the imagine WebSocket enforces 8,192 bytes.
            UiJobRunner.KeyGrokWebChat or UiJobRunner.KeyGrokWebVideo
                => null,
            // Gemini image and the describe/layout-map LLMs limit input in
            // tokens, far above any composer prompt. Ideogram describe sends
            // no prompt at all.
            UiJobRunner.KeyGoogle or UiJobRunner.KeyGooglePro
                or UiJobRunner.KeyDescribeIdeogram or UiJobRunner.KeyDescribeOpenAi
                or UiJobRunner.KeyDescribeClaude or UiJobRunner.KeyDescribeGemini
                or UiJobRunner.KeyDescribeGrok or UiJobRunner.KeyLayoutMap
                => null,
            // Local ComfyUI text encoders truncate internally instead of
            // rejecting; meta-web is disabled in the UI.
            UiJobRunner.KeyLocalKlein or UiJobRunner.KeyLocalZImage
                or UiJobRunner.KeyMetaWeb
                => null,
            _ => throw new ArgumentException($"Unknown UI generator key '{key}'.", nameof(key)),
        };

        private static UiPromptLimit Reject(int maxCharacters)
            => new(maxCharacters, UiPromptLimitUnit.Characters, UiPromptLimitPolicy.Reject, TextToImageOnly: false);

        // Characters are Unicode code points, as the providers count them.
        public static int Measure(string text, UiPromptLimitUnit unit) => unit switch
        {
            UiPromptLimitUnit.Characters => text.EnumerateRunes().Count(),
            UiPromptLimitUnit.Utf8Bytes => Encoding.UTF8.GetByteCount(text),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown prompt-limit unit."),
        };

        /// The exact text a target receives: the job prompt, then its trimmed
        /// endpoint extra text after a blank line.
        public static string ComposeWirePrompt(string prompt, string? extraText)
            => string.IsNullOrWhiteSpace(extraText)
                ? prompt
                : $"{prompt}\n\n{extraText.Trim()}";

        public static UiPromptOverrun? FindRejection(string key, string prompt, string? extraText)
        {
            var limit = For(key);
            if (limit is not { Policy: UiPromptLimitPolicy.Reject })
            {
                return null;
            }
            var measured = Measure(ComposeWirePrompt(prompt, extraText), limit.Unit);
            return measured > limit.Max
                ? new UiPromptOverrun(
                    key,
                    measured,
                    limit.Max,
                    limit.Unit,
                    IncludesExtraText: !string.IsNullOrWhiteSpace(extraText))
                : null;
        }

        public static IReadOnlyList<UiPromptOverrun> FindRejections(
            IEnumerable<string> keys,
            string prompt,
            IReadOnlyDictionary<string, string> extraTexts)
            => keys
                .Select(key => FindRejection(
                    key,
                    prompt,
                    extraTexts.TryGetValue(key, out var extraText) ? extraText : null))
                .OfType<UiPromptOverrun>()
                .ToList();

        public static string DescribeTargetRejection(UiPromptOverrun overrun)
            => $"Prompt too long for {GeneratorPresentation.UiDisplayName(overrun.Key)}: "
                + $"{Count(overrun.Measured)} {UnitName(overrun.Unit)}"
                + (overrun.IncludesExtraText ? " including its extra text" : "")
                + $"; the limit is {Count(overrun.Max)}. The request was not sent.";

        public static string DescribeJobRejection(IReadOnlyList<UiPromptOverrun> overruns)
        {
            if (overruns.Count == 0)
            {
                throw new ArgumentException("A job rejection needs at least one overrun.", nameof(overruns));
            }
            var parts = overruns.Select(o =>
                $"{GeneratorPresentation.UiDisplayName(o.Key)} "
                + $"({Count(o.Measured)} of {Count(o.Max)} {UnitName(o.Unit)}"
                + (o.IncludesExtraText ? " with its extra text" : "")
                + ")");
            return $"Prompt too long for {string.Join(", ", parts)}. "
                + "Shorten the prompt or deselect these models. Nothing was sent.";
        }

        /// /api/config projection consumed by prompt-limits.js.
        public static object? Publish(string key)
        {
            var limit = For(key);
            return limit == null
                ? null
                : new
                {
                    max = limit.Max,
                    unit = limit.Unit == UiPromptLimitUnit.Utf8Bytes ? "utf8Bytes" : "characters",
                    policy = limit.Policy == UiPromptLimitPolicy.Truncate ? "truncate" : "reject",
                    textToImageOnly = limit.TextToImageOnly,
                };
        }

        private static string Count(int value)
            => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string UnitName(UiPromptLimitUnit unit) => unit switch
        {
            UiPromptLimitUnit.Characters => "characters",
            UiPromptLimitUnit.Utf8Bytes => "UTF-8 bytes",
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown prompt-limit unit."),
        };
    }
}
