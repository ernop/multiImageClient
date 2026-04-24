using Newtonsoft.Json;

namespace BFLAPIClient
{
    /// Request payload for FLUX.1 Kontext [pro] / [max] — the text+image editing
    /// models. Kontext takes an input image (URL preferred, base64 also accepted)
    /// plus a natural-language edit instruction. It does NOT take width/height;
    /// output dimensions follow aspect_ratio.
    ///
    /// Use FLUX.2 instead when possible — BFL recommends FLUX.2 over Kontext for
    /// new editing workflows — but Kontext still ships at a lower price.
    public class FluxKontextRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        /// URL or base64 payload of the image to edit. Required.
        [JsonProperty("input_image")]
        public string InputImage { get; set; }

        [JsonProperty("aspect_ratio")]
        public string AspectRatio { get; set; }

        [JsonProperty("seed")]
        public int? Seed { get; set; }

        [JsonProperty("prompt_upsampling")]
        public bool? PromptUpsampling { get; set; }

        [JsonProperty("safety_tolerance")]
        public int? SafetyTolerance { get; set; }

        [JsonProperty("output_format")]
        public string OutputFormat { get; set; } = "png";

        [JsonProperty("webhook_url")]
        public string WebhookUrl { get; set; }

        [JsonProperty("webhook_secret")]
        public string WebhookSecret { get; set; }
    }
}
