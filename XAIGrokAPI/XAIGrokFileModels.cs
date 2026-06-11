using Newtonsoft.Json;

using System.Collections.Generic;

namespace XAIGrokAPIClient
{
    // DTOs for xAI's Files API:
    //
    //   GET  /v1/files                    (paginated list, team-scoped)
    //   GET  /v1/files/{file_id}          (metadata)
    //   GET  /v1/files/{file_id}/content  (raw bytes)
    //
    // Video generations submitted with storage_options land here as stored
    // files (video.file_output.file_id), which makes the Files API the only
    // server-side enumerable "history" xAI offers. Docs:
    // https://docs.x.ai/developers/rest-api-reference/files

    public class XAIGrokFileListResponse : XAIGrokResponseBase
    {
        [JsonProperty("data")]
        public List<XAIGrokFileObject> Data { get; set; } = new();

        /// Always returned; pass back as ?pagination_token= for the next
        /// page. End of list when data.Count < requested limit.
        [JsonProperty("pagination_token")]
        public string? PaginationToken { get; set; }
    }

    public class XAIGrokFileObject
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("filename")]
        public string? Filename { get; set; }

        [JsonProperty("bytes")]
        public long Bytes { get; set; }

        /// Unix seconds.
        [JsonProperty("created_at")]
        public long CreatedAt { get; set; }

        /// Unix seconds; null = permanent.
        [JsonProperty("expires_at")]
        public long? ExpiresAt { get; set; }

        /// Always "file"; OpenAI-compat field.
        [JsonProperty("object")]
        public string? ObjectType { get; set; }

        [JsonProperty("purpose")]
        public string? Purpose { get; set; }
    }
}
