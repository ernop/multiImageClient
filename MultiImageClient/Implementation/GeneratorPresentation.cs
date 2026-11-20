#nullable enable
using System;
namespace MultiImageClient
{
    /// Keeps stable endpoint identity separate from the complete names rendered
    /// for people. Contact sheets lead with the same catalog name as the UI,
    /// then add the provider and any non-duplicate per-call detail.
    public static class GeneratorPresentation
    {
        public static string UiDisplayName(string key)
            => UiTargetCatalog.GetRequired(key).DisplayName;

        public static string UiContactSheetLabel(string key, string? technicalLabel = null)
        {
            var descriptor = UiTargetCatalog.GetRequired(key);
            return AppendTechnicalDetail(
                $"{descriptor.DisplayName} — {descriptor.ContactSheetProvider}",
                descriptor.DisplayName,
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
            ImageGeneratorApiType.GoogleNanoBanana => "Nano Banana 2.1",
            ImageGeneratorApiType.GoogleImagen4 => "Imagen 4 (retired)",
            ImageGeneratorApiType.IdeogramV3 => "Ideogram V3",
            ImageGeneratorApiType.GptImage1Mini => "gpt-image-1-mini",
            ImageGeneratorApiType.GptImage2 => "gpt-image-2",
            ImageGeneratorApiType.GptImage25Sunburst => "gpt-image-2.5 sunburst",
            ImageGeneratorApiType.GptImage25Flare => "gpt-image-2.5 flare",
            ImageGeneratorApiType.GptImage25SunburstEdit => "gpt-image-2.5 sunburst edit",
            ImageGeneratorApiType.GptImage25FlareEdit => "gpt-image-2.5 flare edit",
            ImageGeneratorApiType.GptImage26Sunburst => "gpt-image-2.6 sunburst",
            ImageGeneratorApiType.GptImage26Flare => "gpt-image-2.6 flare",
            ImageGeneratorApiType.GptImage26SunburstEdit => "gpt-image-2.6 sunburst edit",
            ImageGeneratorApiType.GptImage26FlareEdit => "gpt-image-2.6 flare edit",
            ImageGeneratorApiType.BFLFlux3 => "FLUX 3 Image",
            ImageGeneratorApiType.BFLFlux3Klein => "FLUX 3 Image Klein",
            ImageGeneratorApiType.BFLFlux2Pro => "FLUX.2 Pro",
            ImageGeneratorApiType.BFLFlux2Max => "FLUX.2 Max",
            ImageGeneratorApiType.BFLFlux2Flex => "FLUX.2 Flex",
            ImageGeneratorApiType.BFLFlux2Klein4b => "FLUX.2 Klein 4B",
            ImageGeneratorApiType.BFLFlux2Klein9b => "FLUX.2 Klein 9B",
            ImageGeneratorApiType.BFLFluxKontextPro => "FLUX.1 Kontext Pro",
            ImageGeneratorApiType.BFLFluxKontextMax => "FLUX.1 Kontext Max",
            ImageGeneratorApiType.RecraftV4 => "Recraft V4",
            ImageGeneratorApiType.RecraftV4Pro => "Recraft V4 Pro",
            ImageGeneratorApiType.GrokImagine => "Grok Imagine 1.0",
            ImageGeneratorApiType.GrokImaginePro => "Grok Imagine 2.0",
            ImageGeneratorApiType.GrokImagineVideo => "Grok Imagine Video",
            ImageGeneratorApiType.IdeogramV4 => "Ideogram 4.0",
            ImageGeneratorApiType.IdeogramV45 => "Ideogram 4.5",
            ImageGeneratorApiType.IdeogramV46 => "Ideogram 4.6",
            ImageGeneratorApiType.RecraftV41 => "Recraft V4.1",
            ImageGeneratorApiType.RecraftV41FlashV2 => "Recraft V4.1 Flash V2",
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
                or ImageGeneratorApiType.GptImage25Sunburst or ImageGeneratorApiType.GptImage25SunburstEdit
                or ImageGeneratorApiType.GptImage25Flare or ImageGeneratorApiType.GptImage25FlareEdit
                or ImageGeneratorApiType.GptImage26Sunburst or ImageGeneratorApiType.GptImage26SunburstEdit
                or ImageGeneratorApiType.GptImage26Flare or ImageGeneratorApiType.GptImage26FlareEdit
                or ImageGeneratorApiType.Dalle3 => "OpenAI",
            ImageGeneratorApiType.GoogleNanoBanana => "Google · gemini-nano-banana-2.1",
            ImageGeneratorApiType.GoogleNanoBananaPro => "Google · gemini-3-pro-image",
            ImageGeneratorApiType.GoogleImagen4 => "Google",
            ImageGeneratorApiType.GrokImagine or ImageGeneratorApiType.GrokImaginePro
                or ImageGeneratorApiType.GrokImagineVideo or ImageGeneratorApiType.GrokImagineEdit
                => "xAI API",
            ImageGeneratorApiType.GrokWebImagine or ImageGeneratorApiType.GrokWebImaginePro
                or ImageGeneratorApiType.GrokWebImagineVideo or ImageGeneratorApiType.GrokWebImagineEdit
                or ImageGeneratorApiType.GrokWebImagineChat => "xAI via grok.com",
            ImageGeneratorApiType.BFLFlux3 or ImageGeneratorApiType.BFLFlux3Klein
                or ImageGeneratorApiType.BFLv11 or ImageGeneratorApiType.BFLv11Ultra
                or ImageGeneratorApiType.BFLFlux2Pro or ImageGeneratorApiType.BFLFlux2Max
                or ImageGeneratorApiType.BFLFlux2Flex or ImageGeneratorApiType.BFLFlux2Klein4b
                or ImageGeneratorApiType.BFLFlux2Klein9b or ImageGeneratorApiType.BFLFluxKontextPro
                or ImageGeneratorApiType.BFLFluxKontextMax or ImageGeneratorApiType.BFLFlux2ProPreview
                or ImageGeneratorApiType.BFLFlux2Klein9bPreview or ImageGeneratorApiType.BFLFluxPro
                or ImageGeneratorApiType.BFLFluxDev => "Black Forest Labs",
            ImageGeneratorApiType.LocalFlux2Klein => "Black Forest Labs model via local ComfyUI",
            ImageGeneratorApiType.LocalZImage => "Tongyi-MAI model via local ComfyUI",
            ImageGeneratorApiType.Ideogram or ImageGeneratorApiType.IdeogramV3
                or ImageGeneratorApiType.IdeogramV4 or ImageGeneratorApiType.IdeogramV45
                or ImageGeneratorApiType.IdeogramV46 => "Ideogram",
            ImageGeneratorApiType.Recraft or ImageGeneratorApiType.RecraftV4
                or ImageGeneratorApiType.RecraftV4Pro or ImageGeneratorApiType.RecraftV41
                or ImageGeneratorApiType.RecraftV41FlashV2
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
