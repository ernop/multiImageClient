#nullable enable
using System;
using System.IO;

namespace MultiImageClient
{
    public static class ProviderKeyValidator
    {
        public static string? DescribeKeyProblem(ImageGeneratorApiType apiType, Settings settings)
        {
            if (apiType == ImageGeneratorApiType.LocalFlux2Klein || apiType == ImageGeneratorApiType.LocalZImage)
            {
                if (!settings.EnableLocalGenerators)
                {
                    return "not installed - local ComfyUI generators are off (EnableLocalGenerators=false in settings.json)";
                }

                var (workflowPath, workflowSettingName) = GetLocalComfyWorkflowPath(apiType, settings);

                if (string.IsNullOrWhiteSpace(settings.ComfyUIBaseUrl))
                {
                    return "settings.json: ComfyUIBaseUrl is empty - start ComfyUI and set it to http://127.0.0.1:8188";
                }

                if (string.IsNullOrWhiteSpace(workflowPath))
                {
                    return $"settings.json: {workflowSettingName} is empty - save an API-format ComfyUI workflow JSON with {{PROMPT}} in the prompt field";
                }

                if (!File.Exists(workflowPath))
                {
                    return $"settings.json: {workflowSettingName} does not exist: {workflowPath}";
                }

                return null;
            }

            if (apiType is ImageGeneratorApiType.GrokWebImagine or ImageGeneratorApiType.GrokWebImaginePro
                or ImageGeneratorApiType.GrokWebImagineVideo or ImageGeneratorApiType.GrokWebImagineEdit)
            {
                return DescribeGrokWebCookieProblem(settings);
            }

            if (apiType == ImageGeneratorApiType.MetaWebImagine)
            {
                return DescribeMetaWebCookieProblem(settings);
            }

            var (keyName, keyValue) = apiType switch
            {
                ImageGeneratorApiType.Dalle3 or ImageGeneratorApiType.GptImage1
                    or ImageGeneratorApiType.GptImage1Mini or ImageGeneratorApiType.GptImage2
                    => ("OpenAIApiKey", settings.OpenAIApiKey),
                ImageGeneratorApiType.Ideogram or ImageGeneratorApiType.IdeogramV3 or ImageGeneratorApiType.IdeogramV4
                    => ("IdeogramApiKey", settings.IdeogramApiKey),
                ImageGeneratorApiType.BFLv11 or ImageGeneratorApiType.BFLv11Ultra
                    or ImageGeneratorApiType.BFLFluxPro or ImageGeneratorApiType.BFLFluxDev
                    or ImageGeneratorApiType.BFLFlux2Pro or ImageGeneratorApiType.BFLFlux2Max
                    or ImageGeneratorApiType.BFLFlux2Flex or ImageGeneratorApiType.BFLFlux2Klein4b
                    or ImageGeneratorApiType.BFLFlux2Klein9b or ImageGeneratorApiType.BFLFlux2Klein9bPreview
                    or ImageGeneratorApiType.BFLFluxKontextPro
                    or ImageGeneratorApiType.BFLFluxKontextMax or ImageGeneratorApiType.BFLFlux2ProPreview
                    => ("BFLApiKey", settings.BFLApiKey),
                ImageGeneratorApiType.Recraft or ImageGeneratorApiType.RecraftV4
                    or ImageGeneratorApiType.RecraftV4Pro or ImageGeneratorApiType.RecraftV41
                    or ImageGeneratorApiType.RecraftV41Pro
                    => ("RecraftApiKey", settings.RecraftApiKey),
                ImageGeneratorApiType.GrokImagine or ImageGeneratorApiType.GrokImaginePro
                    or ImageGeneratorApiType.GrokImagineVideo
                    or ImageGeneratorApiType.GrokImagineEdit
                    => ("XAIGrokApiKey", settings.XAIGrokApiKey),
                ImageGeneratorApiType.GoogleNanoBanana or ImageGeneratorApiType.GoogleNanoBananaPro
                    or ImageGeneratorApiType.GoogleImagen4
                    => ("GoogleGeminiApiKey", settings.GoogleGeminiApiKey),
                _ => ((string?)null, (string?)null),
            };

            if (keyName == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(keyValue))
            {
                return $"settings.json: {keyName} is empty - paste a real key to enable this provider";
            }

            if (keyValue.Contains(' ') || keyValue.StartsWith("Optional", StringComparison.OrdinalIgnoreCase))
            {
                return $"settings.json: {keyName} still contains template placeholder text - paste a real key to enable this provider";
            }

            return null;
        }

        /// Same emptiness/placeholder rules as the image-provider keys, for
        /// text-model keys like AnthropicApiKey that have no ImageGeneratorApiType.
        public static string? DescribeTextKeyProblem(string keyName, string? keyValue)
        {
            if (string.IsNullOrWhiteSpace(keyValue))
            {
                return $"settings.json: {keyName} is empty - paste a real key to enable this feature";
            }

            if (keyValue.Contains(' ') || keyValue.StartsWith("Optional", StringComparison.OrdinalIgnoreCase))
            {
                return $"settings.json: {keyName} still contains template placeholder text - paste a real key to enable this feature";
            }

            return null;
        }

        private static string? DescribeGrokWebCookieProblem(Settings settings)
        {
            var path = settings.GrokWebCookiePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return "settings.json: GrokWebCookiePath is empty - export grok.com cookies to a file and set the path, or pass --grok-web-cookies";
            }

            if (!File.Exists(path))
            {
                return $"settings.json: GrokWebCookiePath does not exist: {path}";
            }

            return null;
        }

        private static string? DescribeMetaWebCookieProblem(Settings settings)
            => MetaWebClient.DescribeAvailabilityProblem(MetaWebClient.BuildOptions(settings));

        private static (string WorkflowPath, string SettingName) GetLocalComfyWorkflowPath(ImageGeneratorApiType apiType, Settings settings)
        {
            if (apiType == ImageGeneratorApiType.LocalZImage)
            {
                return (settings.ComfyUIZImageWorkflowPath, nameof(settings.ComfyUIZImageWorkflowPath));
            }

            if (!string.IsNullOrWhiteSpace(settings.ComfyUIWorkflowPath))
            {
                return (settings.ComfyUIWorkflowPath, nameof(settings.ComfyUIWorkflowPath));
            }

            return (settings.ComfyUIFlux2KleinWorkflowPath, nameof(settings.ComfyUIFlux2KleinWorkflowPath));
        }
    }
}
