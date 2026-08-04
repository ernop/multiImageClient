using Newtonsoft.Json;

namespace BFLAPIClient
{
    public abstract class Flux2RequestBase
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

        // Every hosted FLUX.2 model accepts at least four references.
        [JsonProperty("input_image")]
        public string InputImage { get; set; }

        [JsonProperty("input_image_2")]
        public string InputImage2 { get; set; }

        [JsonProperty("input_image_3")]
        public string InputImage3 { get; set; }

        [JsonProperty("input_image_4")]
        public string InputImage4 { get; set; }

        [JsonProperty("webhook_url")]
        public string WebhookUrl { get; set; }

        [JsonProperty("webhook_secret")]
        public string WebhookSecret { get; set; }
    }

    /// FLUX.2 Pro/Pro Preview/Max contract: up to eight references and the
    /// inverse disable_pup prompt-upsampling control.
    public class Flux2Request : Flux2RequestBase
    {
        [JsonProperty("disable_pup")]
        public bool? DisablePromptUpsampling { get; set; }

        [JsonProperty("input_image_5")]
        public string InputImage5 { get; set; }

        [JsonProperty("input_image_6")]
        public string InputImage6 { get; set; }

        [JsonProperty("input_image_7")]
        public string InputImage7 { get; set; }

        [JsonProperty("input_image_8")]
        public string InputImage8 { get; set; }
    }

    /// FLUX.2 Flex contract: up to eight references plus positive
    /// prompt_upsampling, steps, and guidance controls.
    public class Flux2FlexRequest : Flux2RequestBase
    {
        [JsonProperty("prompt_upsampling")]
        public bool? PromptUpsampling { get; set; }

        [JsonProperty("input_image_5")]
        public string InputImage5 { get; set; }

        [JsonProperty("input_image_6")]
        public string InputImage6 { get; set; }

        [JsonProperty("input_image_7")]
        public string InputImage7 { get; set; }

        [JsonProperty("input_image_8")]
        public string InputImage8 { get; set; }

        [JsonProperty("steps")]
        public int? Steps { get; set; }

        [JsonProperty("guidance")]
        public float? Guidance { get; set; }
    }

    /// FLUX.2 Klein contract. Klein accepts at most four references and has no
    /// prompt-upsample, steps, or guidance fields.
    public class Flux2KleinRequest : Flux2RequestBase
    {
    }
}
