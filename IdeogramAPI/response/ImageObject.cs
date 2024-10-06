using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    public class ImageObject
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("prompt")]
        public string Prompt { get; set; }

        [JsonProperty("resolution")]
        public string Resolution { get; set; }

        [JsonProperty("is_image_safe")]
        public bool IsImageSafe { get; set; }

        [JsonProperty("seed")]
        public int Seed { get; set; }
    }

}
