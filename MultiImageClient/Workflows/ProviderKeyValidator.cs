#nullable enable
using System;
using System.IO;

namespace MultiImageClient
{
    public static class ProviderKeyValidator
    {
        public static string? DescribeKeyProblem(ImageGeneratorApiType apiType, Settings settings)
        {
            if (apiType == ImageGeneratorApiType.LocalFlux2Klein)
            {
                if (string.IsNullOrWhiteSpace(settings.ComfyUIBaseUrl))
                {
                    return "settings.json: ComfyUIBaseUrl is empty - start ComfyUI and set it to http://127.0.0.1:8188";
                }

                if (string.IsNullOrWhiteSpace(settings.ComfyUIFlux2KleinWorkflowPath))
                {
                    return "settings.json: ComfyUIFlux2KleinWorkflowPath is empty - save an API-format FLUX.2 Klein ComfyUI workflow JSON with {{PROMPT}} in the prompt field";
                }

                if (!File.Exists(settings.ComfyUIFlux2KleinWorkflowPath))
                {
                    return $"settings.json: ComfyUIFlux2KleinWorkflowPath does not exist: {settings.ComfyUIFlux2KleinWorkflowPath}";
                }

                return null;
            }

            var (keyName, keyValue) = apiType switch
            {
                ImageGeneratorApiType.Dalle3 or ImageGeneratorApiType.GptImage1
                    or ImageGeneratorApiType.GptImage1Mini or ImageGeneratorApiType.GptImage2
                    => ("OpenAIApiKey", settings.OpenAIApiKey),
                ImageGeneratorApiType.Ideogram or ImageGeneratorApiType.IdeogramV3 or ImageGeneratorApiType.IdeogramV4
                    => ("IdeogramApiKey", settings.IdeogramApiKey),
                ImageGeneratorApiType.BFLv11 or ImageGeneratorApiType.BFLv11Ultra
                    or ImageGeneratorApiType.BFLFlux2Pro or ImageGeneratorApiType.BFLFlux2Max
                    or ImageGeneratorApiType.BFLFlux2Flex or ImageGeneratorApiType.BFLFlux2Klein4b
                    or ImageGeneratorApiType.BFLFlux2Klein9b or ImageGeneratorApiType.BFLFluxKontextPro
                    or ImageGeneratorApiType.BFLFluxKontextMax or ImageGeneratorApiType.BFLFlux2ProPreview
                    => ("BFLApiKey", settings.BFLApiKey),
                ImageGeneratorApiType.Recraft or ImageGeneratorApiType.RecraftV4
                    or ImageGeneratorApiType.RecraftV4Pro or ImageGeneratorApiType.RecraftV41
                    or ImageGeneratorApiType.RecraftV41Pro
                    => ("RecraftApiKey", settings.RecraftApiKey),
                ImageGeneratorApiType.GrokImagine or ImageGeneratorApiType.GrokImaginePro
                    or ImageGeneratorApiType.GrokImagineVideo
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
    }
}
