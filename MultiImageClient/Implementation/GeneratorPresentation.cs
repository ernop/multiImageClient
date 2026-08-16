#nullable enable
using System;
namespace MultiImageClient
{
    /// Keeps stable endpoint identity separate from the complete names rendered
    /// for people. Contact sheets lead with the same catalog name as the UI,
    /// then add the provider and any non-duplicate per-call detail.
    public static class GeneratorPresentation
    {
        public static string UiDisplayName(string key) => key switch
        {
            UiJobRunner.KeyGpt2 => "gpt-image-2",
            UiJobRunner.KeyGpt1 => "gpt-image-1",
            UiJobRunner.KeyGpt1Mini => "gpt-image-1-mini",
            UiJobRunner.KeyIdeogram => "Ideogram 4.0",
            UiJobRunner.KeyIdeogramV3 => "Ideogram V3",
            UiJobRunner.KeyIdeogramV2 => "Ideogram V2",
            UiJobRunner.KeyRecraft => "Recraft V4.1",
            UiJobRunner.KeyRecraftV41Utility => "Recraft V4.1 Utility",
            UiJobRunner.KeyRecraftV41Pro => "Recraft V4.1 Pro",
            UiJobRunner.KeyRecraftV41Vector => "Recraft V4.1 Vector Woke",
            UiJobRunner.KeyRecraftV3 => "Recraft V3",
            UiJobRunner.KeyRecraftV4 => "Recraft V4",
            UiJobRunner.KeyRecraftV4Pro => "Recraft V4 Pro",
            UiJobRunner.KeyBfl => "FLUX.2 Pro Preview",
            UiJobRunner.KeyBflFlux2Pro => "FLUX.2 Pro (pinned)",
            UiJobRunner.KeyBflFlux2Max => "FLUX.2 Max",
            UiJobRunner.KeyBflFlux2Flex => "FLUX.2 Flex",
            UiJobRunner.KeyBflFlux2Klein4b => "FLUX.2 Klein 4B",
            UiJobRunner.KeyBflFlux2Klein9bPreview => "FLUX.2 Klein 9B Preview",
            UiJobRunner.KeyBflFlux2Klein9b => "FLUX.2 Klein 9B (pinned)",
            UiJobRunner.KeyBflKontextPro => "FLUX.1 Kontext Pro",
            UiJobRunner.KeyBflKontextMax => "FLUX.1 Kontext Max",
            UiJobRunner.KeyBflFlux11Ultra => "FLUX1.1 Pro Ultra",
            UiJobRunner.KeyBflFlux11 => "FLUX1.1 Pro",
            UiJobRunner.KeyBflFluxPro => "FLUX.1 Pro (compatibility)",
            UiJobRunner.KeyBflFluxDev => "FLUX.1 Dev",
            UiJobRunner.KeyKrea => "Krea 2 Medium",
            UiJobRunner.KeyKreaTurbo => "Krea 2 Medium Turbo",
            UiJobRunner.KeyKreaLarge => "Krea 2 Large",
            UiJobRunner.KeyGoogle => "Nano Banana 2",
            UiJobRunner.KeyGooglePro => "Nano Banana Pro",
            UiJobRunner.KeyLocalKlein => "local FLUX.2 Klein",
            UiJobRunner.KeyLocalZImage => "local Z-Image Turbo",
            UiJobRunner.KeyGrokWeb => "grok-web pro",
            UiJobRunner.KeyGrokWebChat => "grok-web chat",
            UiJobRunner.KeyGrokWebVideo => "grok-web video",
            UiJobRunner.KeyGrokApi => "grok-api",
            UiJobRunner.KeyGrokApiPro => "grok-api pro",
            UiJobRunner.KeyMetaWeb => "Meta AI Muse Image (meta-web)",
            UiJobRunner.KeyDescribeIdeogram => "Ideogram describe",
            UiJobRunner.KeyDescribeOpenAi => "OpenAI describe (gpt-4.1)",
            UiJobRunner.KeyDescribeClaude => "Claude describe (Sonnet)",
            UiJobRunner.KeyDescribeGemini => "Gemini describe (2.5 Pro)",
            UiJobRunner.KeyDescribeGrok => "Grok describe (grok-4.3)",
            UiJobRunner.KeyLayoutMap => "Layout map (Gemini 2.5 Pro)",
            _ => throw new ArgumentException($"Unknown UI generator key '{key}'.", nameof(key)),
        };

        public static string UiContactSheetLabel(string key, string? technicalLabel = null)
        {
            var displayName = UiDisplayName(key);
            var provider = key switch
            {
                UiJobRunner.KeyGpt2 or UiJobRunner.KeyGpt1 or UiJobRunner.KeyGpt1Mini
                    or UiJobRunner.KeyDescribeOpenAi => "OpenAI",
                UiJobRunner.KeyGrokApi => "xAI API · grok-imagine-image",
                UiJobRunner.KeyGrokApiPro => "xAI API · grok-imagine-image-pro",
                UiJobRunner.KeyGrokWeb or UiJobRunner.KeyGrokWebChat
                    or UiJobRunner.KeyGrokWebVideo => "xAI via grok.com",
                UiJobRunner.KeyDescribeGrok => "xAI API",
                UiJobRunner.KeyGoogle => "Google · gemini-3.1-flash-image",
                UiJobRunner.KeyGooglePro => "Google · gemini-3-pro-image",
                UiJobRunner.KeyDescribeGemini or UiJobRunner.KeyLayoutMap => "Google",
                UiJobRunner.KeyIdeogram or UiJobRunner.KeyIdeogramV3
                    or UiJobRunner.KeyIdeogramV2 or UiJobRunner.KeyDescribeIdeogram => "Ideogram",
                UiJobRunner.KeyRecraft or UiJobRunner.KeyRecraftV41Utility
                    or UiJobRunner.KeyRecraftV41Pro or UiJobRunner.KeyRecraftV41Vector
                    or UiJobRunner.KeyRecraftV3 or UiJobRunner.KeyRecraftV4
                    or UiJobRunner.KeyRecraftV4Pro => "Recraft",
                UiJobRunner.KeyBfl or UiJobRunner.KeyBflFlux2Pro
                    or UiJobRunner.KeyBflFlux2Max or UiJobRunner.KeyBflFlux2Flex
                    or UiJobRunner.KeyBflFlux2Klein4b or UiJobRunner.KeyBflFlux2Klein9bPreview
                    or UiJobRunner.KeyBflFlux2Klein9b or UiJobRunner.KeyBflKontextPro
                    or UiJobRunner.KeyBflKontextMax or UiJobRunner.KeyBflFlux11Ultra
                    or UiJobRunner.KeyBflFlux11 or UiJobRunner.KeyBflFluxPro
                    or UiJobRunner.KeyBflFluxDev => "Black Forest Labs",
                UiJobRunner.KeyKrea or UiJobRunner.KeyKreaTurbo
                    or UiJobRunner.KeyKreaLarge => "Krea",
                UiJobRunner.KeyLocalKlein => "Black Forest Labs model via local ComfyUI",
                UiJobRunner.KeyLocalZImage => "Tongyi-MAI model via local ComfyUI",
                UiJobRunner.KeyMetaWeb => "Meta via meta.ai",
                UiJobRunner.KeyDescribeClaude => "Anthropic",
                _ => throw new ArgumentException($"Unknown UI generator key '{key}'.", nameof(key)),
            };
            return AppendTechnicalDetail(
                $"{displayName} — {provider}",
                displayName,
                technicalLabel,
                key);
        }

        public static string ContactSheetLabel(TaskProcessResult result)
        {
            if (!string.IsNullOrWhiteSpace(result.GeneratorKey))
            {
                return UiContactSheetLabel(
                    result.GeneratorKey,
                    result.ImageGeneratorDescription);
            }
            if (!Enum.IsDefined(result.ImageGenerator))
            {
                var recordedDescription = Flatten(result.ImageGeneratorDescription);
                if (recordedDescription.Length == 0)
                {
                    throw new InvalidOperationException(
                        "A contact-sheet result has neither a known generator API type nor a recorded generator description.");
                }
                return recordedDescription;
            }

            var displayName = ApiDisplayName(result.ImageGenerator);
            var provider = ApiProvider(result.ImageGenerator);
            return AppendTechnicalDetail(
                $"{displayName} — {provider}",
                displayName,
                result.ImageGeneratorDescription,
                result.ImageGenerator.ToString());
        }

        private static string ApiDisplayName(ImageGeneratorApiType apiType) => apiType switch
        {
            ImageGeneratorApiType.Midjourney => "Midjourney",
            ImageGeneratorApiType.Dalle3 => "DALL·E 3 (retired)",
            ImageGeneratorApiType.Ideogram => "Ideogram 2.0",
            ImageGeneratorApiType.BFLv11 => "FLUX1.1 Pro",
            ImageGeneratorApiType.Recraft => "Recraft V3",
            ImageGeneratorApiType.GptImage1 => "gpt-image-1",
            ImageGeneratorApiType.BFLv11Ultra => "FLUX1.1 Pro Ultra",
            ImageGeneratorApiType.GoogleNanoBanana => "Nano Banana 2",
            ImageGeneratorApiType.GoogleImagen4 => "Imagen 4 (retired)",
            ImageGeneratorApiType.IdeogramV3 => "Ideogram V3",
            ImageGeneratorApiType.GptImage1Mini => "gpt-image-1-mini",
            ImageGeneratorApiType.GptImage2 => "gpt-image-2",
            ImageGeneratorApiType.BFLFlux2Pro => "FLUX.2 Pro",
            ImageGeneratorApiType.BFLFlux2Max => "FLUX.2 Max",
            ImageGeneratorApiType.BFLFlux2Flex => "FLUX.2 Flex",
            ImageGeneratorApiType.BFLFlux2Klein4b => "FLUX.2 Klein 4B",
            ImageGeneratorApiType.BFLFlux2Klein9b => "FLUX.2 Klein 9B",
            ImageGeneratorApiType.BFLFluxKontextPro => "FLUX.1 Kontext Pro",
            ImageGeneratorApiType.BFLFluxKontextMax => "FLUX.1 Kontext Max",
            ImageGeneratorApiType.RecraftV4 => "Recraft V4",
            ImageGeneratorApiType.RecraftV4Pro => "Recraft V4 Pro",
            ImageGeneratorApiType.GrokImagine => "Grok Imagine",
            ImageGeneratorApiType.GrokImaginePro => "Grok Imagine Pro",
            ImageGeneratorApiType.GrokImagineVideo => "Grok Imagine Video",
            ImageGeneratorApiType.IdeogramV4 => "Ideogram 4.0",
            ImageGeneratorApiType.RecraftV41 => "Recraft V4.1",
            ImageGeneratorApiType.RecraftV41Pro => "Recraft V4.1 Pro",
            ImageGeneratorApiType.GoogleNanoBananaPro => "Nano Banana Pro",
            ImageGeneratorApiType.BFLFlux2ProPreview => "FLUX.2 Pro Preview",
            ImageGeneratorApiType.LocalFlux2Klein => "local FLUX.2 Klein",
            ImageGeneratorApiType.GrokImagineEdit => "Grok Imagine Edit",
            ImageGeneratorApiType.LocalZImage => "local Z-Image Turbo",
            ImageGeneratorApiType.GrokWebImagine => "grok-web imagine",
            ImageGeneratorApiType.GrokWebImaginePro => "grok-web imagine pro",
            ImageGeneratorApiType.GrokWebImagineVideo => "grok-web video",
            ImageGeneratorApiType.GrokWebImagineEdit => "grok-web edit",
            ImageGeneratorApiType.GptImage2Edit => "gpt-image-2 edit",
            ImageGeneratorApiType.MetaWebImagine => "Meta AI Muse Image (meta-web)",
            ImageGeneratorApiType.BFLFlux2Klein9bPreview => "FLUX.2 Klein 9B Preview",
            ImageGeneratorApiType.BFLFluxPro => "FLUX.1 Pro",
            ImageGeneratorApiType.BFLFluxDev => "FLUX.1 Dev",
            ImageGeneratorApiType.RecraftV41Utility => "Recraft V4.1 Utility",
            ImageGeneratorApiType.RecraftV41Vector => "Recraft V4.1 Vector",
            ImageGeneratorApiType.Krea2MediumTurbo => "Krea 2 Medium Turbo",
            ImageGeneratorApiType.Krea2Medium => "Krea 2 Medium",
            ImageGeneratorApiType.Krea2Large => "Krea 2 Large",
            ImageGeneratorApiType.GrokWebImagineChat => "grok-web chat",
            ImageGeneratorApiType.WorkflowMock => "workflow test generator",
            _ => throw new ArgumentOutOfRangeException(nameof(apiType), apiType, "Unknown image generator API type."),
        };

        private static string ApiProvider(ImageGeneratorApiType apiType) => apiType switch
        {
            ImageGeneratorApiType.GptImage1 or ImageGeneratorApiType.GptImage1Mini
                or ImageGeneratorApiType.GptImage2 or ImageGeneratorApiType.GptImage2Edit
                or ImageGeneratorApiType.Dalle3 => "OpenAI",
            ImageGeneratorApiType.GoogleNanoBanana => "Google · gemini-3.1-flash-image",
            ImageGeneratorApiType.GoogleNanoBananaPro => "Google · gemini-3-pro-image",
            ImageGeneratorApiType.GoogleImagen4 => "Google",
            ImageGeneratorApiType.GrokImagine or ImageGeneratorApiType.GrokImaginePro
                or ImageGeneratorApiType.GrokImagineVideo or ImageGeneratorApiType.GrokImagineEdit
                => "xAI API",
            ImageGeneratorApiType.GrokWebImagine or ImageGeneratorApiType.GrokWebImaginePro
                or ImageGeneratorApiType.GrokWebImagineVideo or ImageGeneratorApiType.GrokWebImagineEdit
                or ImageGeneratorApiType.GrokWebImagineChat => "xAI via grok.com",
            ImageGeneratorApiType.BFLv11 or ImageGeneratorApiType.BFLv11Ultra
                or ImageGeneratorApiType.BFLFlux2Pro or ImageGeneratorApiType.BFLFlux2Max
                or ImageGeneratorApiType.BFLFlux2Flex or ImageGeneratorApiType.BFLFlux2Klein4b
                or ImageGeneratorApiType.BFLFlux2Klein9b or ImageGeneratorApiType.BFLFluxKontextPro
                or ImageGeneratorApiType.BFLFluxKontextMax or ImageGeneratorApiType.BFLFlux2ProPreview
                or ImageGeneratorApiType.BFLFlux2Klein9bPreview or ImageGeneratorApiType.BFLFluxPro
                or ImageGeneratorApiType.BFLFluxDev => "Black Forest Labs",
            ImageGeneratorApiType.LocalFlux2Klein => "Black Forest Labs model via local ComfyUI",
            ImageGeneratorApiType.LocalZImage => "Tongyi-MAI model via local ComfyUI",
            ImageGeneratorApiType.Ideogram or ImageGeneratorApiType.IdeogramV3
                or ImageGeneratorApiType.IdeogramV4 => "Ideogram",
            ImageGeneratorApiType.Recraft or ImageGeneratorApiType.RecraftV4
                or ImageGeneratorApiType.RecraftV4Pro or ImageGeneratorApiType.RecraftV41
                or ImageGeneratorApiType.RecraftV41Pro or ImageGeneratorApiType.RecraftV41Utility
                or ImageGeneratorApiType.RecraftV41Vector => "Recraft",
            ImageGeneratorApiType.Krea2MediumTurbo or ImageGeneratorApiType.Krea2Medium
                or ImageGeneratorApiType.Krea2Large => "Krea",
            ImageGeneratorApiType.MetaWebImagine => "Meta via meta.ai",
            ImageGeneratorApiType.Midjourney => "Midjourney",
            ImageGeneratorApiType.WorkflowMock => "local test workflow",
            _ => throw new ArgumentOutOfRangeException(nameof(apiType), apiType, "Unknown image generator API type."),
        };

        private static string AppendTechnicalDetail(
            string baseLabel,
            string displayName,
            string? technicalLabel,
            string identity)
        {
            var technical = Flatten(technicalLabel);
            if (technical.Equals(baseLabel, StringComparison.OrdinalIgnoreCase)
                || technical.StartsWith(baseLabel + " · ", StringComparison.OrdinalIgnoreCase))
            {
                return technical;
            }
            if (technical.Length == 0 || baseLabel.Contains(technical, StringComparison.OrdinalIgnoreCase))
            {
                return baseLabel;
            }

            technical = RemovePrefix(technical, displayName);
            technical = RemovePrefix(technical, identity);
            technical = RemovePrefix(technical, $"google-{identity}");
            technical = RemovePrefix(technical, "ui");
            if (technical.Length == 0 || baseLabel.Contains(technical, StringComparison.OrdinalIgnoreCase))
            {
                return baseLabel;
            }

            return $"{baseLabel} · {technical}";
        }

        private static string Flatten(string? value)
            => string.Join(
                " ",
                (value ?? string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string RemovePrefix(string value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return value;
            }
            if (string.Equals(value, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return value.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)
                ? value[(prefix.Length + 1)..].Trim()
                : value;
        }
    }
}
