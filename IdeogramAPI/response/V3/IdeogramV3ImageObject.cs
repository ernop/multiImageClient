using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    public class IdeogramV3ImageObject
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonProperty("resolution")]
        public string Resolution { get; set; } = string.Empty;

        [JsonProperty("is_image_safe")]
        public bool IsImageSafe { get; set; }

        [JsonProperty("seed")]
        public int Seed { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; } = string.Empty;

        [JsonProperty("style_type")]
        public string StyleType { get; set; } = string.Empty;
    }
}

