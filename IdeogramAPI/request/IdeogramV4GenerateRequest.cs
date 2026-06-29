using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    /// Request body for POST /v1/ideogram-v4/generate (Ideogram 4.0,
    /// released 2026-06-03). Unlike v3 (multipart form), v4 takes a plain
    /// JSON body.
    ///
    /// Only TextPrompt is required. We deliberately expose the simple
    /// text_prompt path; the alternative structured `json_prompt` contract
    /// (mutually exclusive with text_prompt, disables magic-prompt) can be
    /// added later if we want compositional control.
    ///
    /// Docs: https://developer.ideogram.ai/api-reference/api-reference/generate-v4
    public class IdeogramV4GenerateRequest
    {
        public IdeogramV4GenerateRequest(string textPrompt)
        {
            TextPrompt = textPrompt;
        }

        [JsonProperty("text_prompt")]
        public string TextPrompt { get; set; }

        /// One of the documented 2K-class resolutions, e.g. "2048x2048"
        /// (square, the default), "2304x1728" (4:3), "1728x2304" (3:4),
        /// "2560x1440" (16:9), "1440x2560" (9:16), "2496x1664" (3:2), etc.
        /// Null lets the API pick (2048x2048).
        [JsonProperty("resolution", NullValueHandling = NullValueHandling.Ignore)]
        public string? Resolution { get; set; }

        /// FLASH | TURBO | DEFAULT | QUALITY. Null = DEFAULT.
        [JsonProperty("rendering_speed", NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public IdeogramRenderingSpeed? RenderingSpeed { get; set; }

        [JsonProperty("num_images", NullValueHandling = NullValueHandling.Ignore)]
        public int? NumImages { get; set; }

        [JsonProperty("seed", NullValueHandling = NullValueHandling.Ignore)]
        public int? Seed { get; set; }
    }
}
