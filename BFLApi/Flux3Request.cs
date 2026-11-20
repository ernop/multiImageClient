using Newtonsoft.Json;

using System.Collections.Generic;

namespace BFLAPIClient
{
    /// FLUX 3 Image and FLUX 3 Image Klein request contract. BFL enforces
    /// additionalProperties: false on /v1/flux-3-image and /v1/flux-3-image-klein,
    /// so every property here matches the published OpenAPI schema.
    public class Flux3Request
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("images")]
        public List<string> Images { get; set; }

        [JsonProperty("aspect_ratio")]
        public string AspectRatio { get; set; }

        [JsonProperty("resolution")]
        public string Resolution { get; set; }

        [JsonProperty("width")]
        public int? Width { get; set; }

        [JsonProperty("height")]
        public int? Height { get; set; }

        [JsonProperty("safety_tolerance")]
        public int? SafetyTolerance { get; set; }

        [JsonProperty("grounding")]
        public bool? Grounding { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("user")]
        public string User { get; set; }
    }
}
