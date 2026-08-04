using Newtonsoft.Json;

using System.Text.Json;

namespace BFLAPIClient
{
    public class FluxDevRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("image_prompt")]
        public string ImagePrompt { get; set; }

        [JsonProperty("steps")]
        public int? Steps { get; set; }

        [JsonProperty("prompt_upsampling")]
        public bool PromptUpsampling { get; set; }

        [JsonProperty("seed")]
        public int? Seed { get; set; }

        [JsonProperty("guidance")]
        public float? Guidance { get; set; }

        [JsonProperty("safety_tolerance")]
        public int SafetyTolerance { get; set; }

        [JsonProperty("output_format")]
        public string OutputFormat { get; set; } = "png";
    }
}
