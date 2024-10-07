using Newtonsoft.Json;

using System.Text.Json;

namespace BFLAPIClient
{
    public class FluxPro11Request
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("prompt_upsampling")]
        public bool PromptUpsampling { get; set; }

        [JsonProperty("safety_tolerance")]
        public int SafetyTolerance { get; set; }

        [JsonProperty("seed")]
        public int? Seed { get; set; }
    }
}
