using Newtonsoft.Json;

namespace BFLAPIClient
{
    /// Unified request payload for every FLUX.2 endpoint
    /// (pro, pro-preview, max, flex, klein-4b, klein-9b).
    ///
    /// Resolution/seed/output knobs are common. Flex-specific knobs (steps,
    /// guidance) are nullable and simply omitted by the serializer when not set,
    /// so the same DTO is safe to pass to any FLUX.2 endpoint.
    ///
    /// Image editing: populate InputImage (and optionally InputImage2..8) with
    /// URLs or base64-encoded bytes. BFL prefers URLs — they'll fetch them for
    /// you. All FLUX.2 variants support image conditioning this way.
    public class Flux2Request
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("width")]
        public int? Width { get; set; }

        [JsonProperty("height")]
        public int? Height { get; set; }

        [JsonProperty("seed")]
        public int? Seed { get; set; }

        [JsonProperty("safety_tolerance")]
        public int? SafetyTolerance { get; set; }

        [JsonProperty("output_format")]
        public string OutputFormat { get; set; } = "png";

        [JsonProperty("prompt_upsampling")]
        public bool? PromptUpsampling { get; set; }

        // Multi-reference image editing. Up to 8 via the API (10 in playground).
        [JsonProperty("input_image")]
        public string InputImage { get; set; }

        [JsonProperty("input_image_2")]
        public string InputImage2 { get; set; }

        [JsonProperty("input_image_3")]
        public string InputImage3 { get; set; }

        [JsonProperty("input_image_4")]
        public string InputImage4 { get; set; }

        [JsonProperty("input_image_5")]
        public string InputImage5 { get; set; }

        [JsonProperty("input_image_6")]
        public string InputImage6 { get; set; }

        [JsonProperty("input_image_7")]
        public string InputImage7 { get; set; }

        [JsonProperty("input_image_8")]
        public string InputImage8 { get; set; }

        // flex-only: explicit inference steps (up to 50) and guidance (1.5-10).
        // Ignored by pro / max / klein endpoints.
        [JsonProperty("steps")]
        public int? Steps { get; set; }

        [JsonProperty("guidance")]
        public float? Guidance { get; set; }

        // Webhook delivery instead of polling.
        [JsonProperty("webhook_url")]
        public string WebhookUrl { get; set; }

        [JsonProperty("webhook_secret")]
        public string WebhookSecret { get; set; }
    }
}
