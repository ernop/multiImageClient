using System.Collections.Generic;
using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    /// Synchronous response shared by Ideogram 4.5 Generate and Precise Edit.
    /// Required members follow the published OpenAPI contract, so a reply
    /// without them fails deserialization instead of yielding defaults.
    public class IdeogramV45Response
    {
        /// "sampling" or "workflow".
        [JsonProperty("generation_kind", Required = Required.Always)]
        public string GenerationKind { get; set; } = string.Empty;

        /// Required for Generate; optional for Precise Edit.
        [JsonProperty("generation_id")]
        public string? GenerationId { get; set; }

        [JsonProperty("seed")]
        public long? Seed { get; set; }

        [JsonProperty("data")]
        public List<IdeogramV45ImageObject>? Data { get; set; }
    }

    public class IdeogramV45ImageObject
    {
        /// Null or empty when the image did not pass safety checks.
        [JsonProperty("url")]
        public string? Url { get; set; }

        /// The structured JSON prompt Ideogram rendered from.
        [JsonProperty("prompt", Required = Required.Always)]
        public string Prompt { get; set; } = string.Empty;

        /// "WIDTHxHEIGHT".
        [JsonProperty("resolution", Required = Required.Always)]
        public string Resolution { get; set; } = string.Empty;

        [JsonProperty("is_image_safe", Required = Required.Always)]
        public bool IsImageSafe { get; set; }

        [JsonProperty("seed", Required = Required.Always)]
        public long Seed { get; set; }
    }
}
