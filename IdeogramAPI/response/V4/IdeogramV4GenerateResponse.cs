using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace IdeogramAPIClient
{
    /// Response from POST /v1/ideogram-v4/generate. The per-image objects
    /// share the v3 wire shape (url / prompt / resolution / is_image_safe /
    /// seed) so we reuse IdeogramV3ImageObject. Note: in v4, `prompt` comes
    /// back as the model's expanded STRUCTURED JSON prompt (a serialized
    /// object), not a plain rewritten sentence.
    public class IdeogramV4GenerateResponse
    {
        /// Always "url" for this endpoint shape.
        [JsonProperty("response_type")]
        public string ResponseType { get; set; } = string.Empty;

        [JsonProperty("created")]
        public DateTime Created { get; set; }

        [JsonProperty("data")]
        public List<IdeogramV3ImageObject> Data { get; set; } = new List<IdeogramV3ImageObject>();
    }
}
