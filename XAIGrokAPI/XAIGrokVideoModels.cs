using Newtonsoft.Json;

namespace XAIGrokAPIClient
{
    // DTOs for xAI's asynchronous video endpoints:
    //
    //   POST https://api.x.ai/v1/videos/generations  -> { request_id }
    //   GET  https://api.x.ai/v1/videos/{request_id} -> { status, video, ... }
    //
    // status: pending | done | expired | failed. When done, video.url is a
    // TEMPORARY mp4 URL — callers must download promptly.
    //
    // Docs: https://docs.x.ai/developers/rest-api-reference/inference/videos

    /// Request body for POST /v1/videos/generations. Text-to-video uses
    /// Prompt alone; image-to-video adds Image (the source becomes the first
    /// frame). Reference-to-video would use reference_images (not exposed yet
    /// — add when needed).
    public class XAIGrokVideoGenerateRequest
    {
        [JsonProperty("prompt")]
        public string? Prompt { get; set; }

        /// e.g. XAIGrokClient.ModelGrokImagineVideo ("grok-imagine-video").
        [JsonProperty("model")]
        public string? Model { get; set; }

        /// Seconds, range [1, 15]. Default 8 when omitted.
        [JsonProperty("duration")]
        public int? Duration { get; set; }

        /// "1:1" | "16:9" | "9:16" | "4:3" | "3:4" | "3:2" | "2:3".
        /// Default 16:9 for text-to-video; image-to-video inherits the
        /// input image's AR unless overridden (which stretches).
        [JsonProperty("aspect_ratio")]
        public string? AspectRatio { get; set; }

        /// "480p" (default, faster) | "720p" | "1080p".
        [JsonProperty("resolution")]
        public string? Resolution { get; set; }

        /// Optional image-to-video source. Exactly one of Url / FileId.
        [JsonProperty("image")]
        public XAIGrokVideoImageInput? Image { get; set; }

        /// When set, xAI stores the finished mp4 in the team's Files API
        /// storage (durable, enumerable via GET /v1/files) instead of only a
        /// temporary URL. This is what makes server-side history/sync
        /// possible — always set it for archival workflows.
        [JsonProperty("storage_options")]
        public XAIGrokVideoStorageOptions? StorageOptions { get; set; }
    }

    public class XAIGrokVideoStorageOptions
    {
        /// Filename for the stored file (required when storing).
        [JsonProperty("filename")]
        public string Filename { get; set; } = string.Empty;

        /// Seconds until auto-expiry, max 2592000 (30 days). Null/omitted =
        /// the file never expires (preferred for archives).
        [JsonProperty("expires_after", NullValueHandling = NullValueHandling.Ignore)]
        public int? ExpiresAfter { get; set; }
    }

    /// Source image for image-to-video. Url is a public HTTPS URL; FileId
    /// references the xAI Files API. Mutually exclusive.
    public class XAIGrokVideoImageInput
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("file_id")]
        public string? FileId { get; set; }
    }

    /// Source video for extend-video (and edit-video). Url is an
    /// xAI-hosted or public HTTPS mp4 URL; FileId references the xAI Files
    /// API (what storage_options gives us). Mutually exclusive.
    public class XAIGrokVideoVideoInput
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("file_id")]
        public string? FileId { get; set; }
    }

    /// Request body for POST /v1/videos/extensions: continue an existing
    /// video from its last frame. xAI returns the ORIGINAL + EXTENSION
    /// combined into one clip. Same deferred flow as generations — the
    /// response is a request_id polled via GET /v1/videos/{id}.
    public class XAIGrokVideoExtendRequest
    {
        /// What should happen in the extension.
        [JsonProperty("prompt")]
        public string? Prompt { get; set; }

        [JsonProperty("model")]
        public string? Model { get; set; }

        /// The video to extend. Required.
        [JsonProperty("video")]
        public XAIGrokVideoVideoInput? Video { get; set; }

        /// Seconds of NEW footage to add, range [1, 15].
        [JsonProperty("duration")]
        public int? Duration { get; set; }

        [JsonProperty("resolution")]
        public string? Resolution { get; set; }

        /// Same semantics as on generate: store the combined clip durably
        /// in the Files API so sync can always recover it.
        [JsonProperty("storage_options")]
        public XAIGrokVideoStorageOptions? StorageOptions { get; set; }
    }

    public class XAIGrokVideoStartResponse : XAIGrokResponseBase
    {
        [JsonProperty("request_id")]
        public string? RequestId { get; set; }
    }

    /// Poll result from GET /v1/videos/{request_id}.
    public class XAIGrokVideoResult : XAIGrokResponseBase
    {
        public const string StatusPending = "pending";
        public const string StatusDone = "done";
        public const string StatusFailed = "failed";
        public const string StatusExpired = "expired";

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("video")]
        public XAIGrokVideoData? Video { get; set; }

        [JsonProperty("model")]
        public string? Model { get; set; }

        /// Populated when status == "failed".
        [JsonProperty("error")]
        public XAIGrokVideoError? Error { get; set; }

        /// Not part of the wire format; filled in by GenerateVideoAsync so
        /// callers can quote the id when contacting xAI about failures.
        [JsonIgnore]
        public string? RequestId { get; set; }

        [JsonIgnore]
        public bool IsDone => string.Equals(Status, StatusDone, System.StringComparison.OrdinalIgnoreCase);
    }

    public class XAIGrokVideoData
    {
        /// Temporary xAI-hosted mp4 URL.
        [JsonProperty("url")]
        public string? Url { get; set; }

        /// Seconds.
        [JsonProperty("duration")]
        public int? Duration { get; set; }

        /// False means xAI's moderation filtered the output.
        [JsonProperty("respect_moderation")]
        public bool? RespectModeration { get; set; }

        /// Unix seconds when the stored file expires, when reported.
        [JsonProperty("expires_at")]
        public long? ExpiresAt { get; set; }

        /// Present when the request included storage_options: the durable
        /// Files-API copy of the clip.
        [JsonProperty("file_output")]
        public XAIGrokVideoFileOutput? FileOutput { get; set; }
    }

    public class XAIGrokVideoFileOutput
    {
        [JsonProperty("file_id")]
        public string FileId { get; set; } = string.Empty;

        [JsonProperty("filename")]
        public string? Filename { get; set; }

        [JsonProperty("public_url")]
        public string? PublicUrl { get; set; }

        [JsonProperty("expires_at")]
        public long? ExpiresAt { get; set; }
    }

    /// error.code values: invalid_argument | permission_denied |
    /// failed_precondition | service_unavailable | internal_error.
    public class XAIGrokVideoError
    {
        [JsonProperty("code")]
        public string? Code { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }
    }
}
