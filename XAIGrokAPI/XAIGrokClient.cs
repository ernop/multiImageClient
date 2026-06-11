using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XAIGrokAPIClient
{
    /// Hand-rolled REST client for xAI's image endpoints. Targets the two
    /// documented routes:
    ///
    ///   POST https://api.x.ai/v1/images/generations   (text -> image)
    ///   POST https://api.x.ai/v1/images/edits         (text + image -> image)
    ///
    /// We deliberately do NOT use the OpenAI SDK here — the user wanted full
    /// control over the wire shape. That means:
    ///   * every supported field in the xAI REST reference is exposed on the
    ///     request DTOs below (aspect_ratio, quality, resolution, n, user,
    ///     response_format, ...),
    ///   * unset/null fields are omitted from the JSON so we don't accidentally
    ///     send "size"/"style" (xAI explicitly rejects those as unsupported),
    ///   * we return the raw response shape including usage.cost_in_usd_ticks
    ///     so callers can track actual spend.
    ///
    /// Models supported (2026-04):
    ///   grok-imagine-image       — $0.02/image, 300 rpm
    ///   grok-imagine-image-pro   — $0.07/image, 30 rpm
    ///   grok-imagine-video       — async text/image -> video (mp4), 1-15s
    ///
    /// Docs: https://docs.x.ai/developers/model-capabilities/images/generation
    ///       https://docs.x.ai/developers/rest-api-reference/inference/images
    ///       https://docs.x.ai/developers/model-capabilities/video/generation
    ///       https://docs.x.ai/developers/rest-api-reference/inference/videos
    public class XAIGrokClient
    {
        public const string BaseUrl = "https://api.x.ai/v1";
        public const string ModelGrokImagine = "grok-imagine-image";
        public const string ModelGrokImaginePro = "grok-imagine-image-pro";
        public const string ModelGrokImagineVideo = "grok-imagine-video";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public XAIGrokClient(string apiKey, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("xAI API key is required.", nameof(apiKey));
            }
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
        }

        /// Text-to-image. Returns the full parsed response so callers can pull
        /// URLs or b64_json blobs and read usage.cost_in_usd_ticks.
        public Task<XAIGrokImageResponse> GenerateAsync(XAIGrokGenerateRequest request, CancellationToken ct = default)
            => PostAsync("/images/generations", request, ct);

        /// Text + image -> image. Mirrors GenerateAsync but hits /images/edits
        /// and expects request.Image (or request.Images) to be populated.
        public Task<XAIGrokImageResponse> EditAsync(XAIGrokEditRequest request, CancellationToken ct = default)
            => PostAsync("/images/edits", request, ct);

        // ---------- Video (asynchronous: start -> poll) ----------

        /// Step 1 of the video flow: POST /v1/videos/generations. Returns
        /// immediately with a request_id; the video is NOT ready yet.
        public Task<XAIGrokVideoStartResponse> StartVideoAsync(XAIGrokVideoGenerateRequest request, CancellationToken ct = default)
            => PostAsync<XAIGrokVideoGenerateRequest, XAIGrokVideoStartResponse>("/videos/generations", request, ct);

        /// POST /v1/videos/extensions: continue an existing video (by url or
        /// Files-API file_id) from its last frame. Deferred like generations;
        /// poll the returned request_id. The finished clip is the original
        /// PLUS the extension, combined.
        public Task<XAIGrokVideoStartResponse> StartVideoExtensionAsync(XAIGrokVideoExtendRequest request, CancellationToken ct = default)
            => PostAsync<XAIGrokVideoExtendRequest, XAIGrokVideoStartResponse>("/videos/extensions", request, ct);

        /// Step 2: GET /v1/videos/{request_id}. status is one of
        /// pending | done | expired | failed. When done, Video.Url holds a
        /// temporary mp4 URL — download promptly, xAI expires them. (When the
        /// request used storage_options, Video.FileOutput carries the durable
        /// Files-API file_id instead.)
        public Task<XAIGrokVideoResult> GetVideoAsync(string requestId, CancellationToken ct = default)
            => GetAsync<XAIGrokVideoResult>($"/videos/{requestId}", ct);

        // ---------- Files API (the only enumerable server-side history) ----------

        /// One page of GET /v1/files. The response always includes a
        /// pagination_token; keep calling with it until data.Count < limit.
        /// Server max limit is 100.
        public Task<XAIGrokFileListResponse> ListFilesPageAsync(
            int limit = 100,
            string? paginationToken = null,
            string sortBy = "created_at",
            string order = "asc",
            CancellationToken ct = default)
        {
            var path = $"/files?limit={limit}&sort_by={sortBy}&order={order}";
            if (!string.IsNullOrEmpty(paginationToken))
            {
                path += $"&pagination_token={Uri.EscapeDataString(paginationToken)}";
            }
            return GetAsync<XAIGrokFileListResponse>(path, ct);
        }

        /// Walks every page of GET /v1/files and returns the full inventory
        /// of stored files for the authenticated team, oldest first.
        public async Task<List<XAIGrokFileObject>> ListAllFilesAsync(CancellationToken ct = default)
        {
            var all = new List<XAIGrokFileObject>();
            string? token = null;
            const int pageSize = 100;
            while (true)
            {
                var page = await ListFilesPageAsync(pageSize, token, ct: ct).ConfigureAwait(false);
                all.AddRange(page.Data);
                if (page.Data.Count < pageSize || string.IsNullOrEmpty(page.PaginationToken))
                {
                    break;
                }
                token = page.PaginationToken;
            }
            return all;
        }

        /// GET /v1/files/{file_id}/content — raw bytes of a stored file.
        public async Task<byte[]> DownloadFileContentAsync(string fileId, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/files/{fileId}/content");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new XAIGrokException(
                    $"xAI /files/{fileId}/content returned {(int)res.StatusCode} {res.StatusCode}: {text}",
                    (int)res.StatusCode,
                    text);
            }
            return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        private async Task<TRes> GetAsync<TRes>(string path, CancellationToken ct)
            where TRes : XAIGrokResponseBase
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                throw new XAIGrokException(
                    $"xAI {path} returned {(int)res.StatusCode} {res.StatusCode}: {text}",
                    (int)res.StatusCode,
                    text);
            }

            var parsed = JsonConvert.DeserializeObject<TRes>(text)
                ?? throw new XAIGrokException(
                    $"xAI {path} returned an empty/unparseable body.",
                    (int)res.StatusCode,
                    text);
            parsed.RawBody = text;
            return parsed;
        }

        /// Convenience wrapper that does the whole start -> poll loop and only
        /// returns once the video reaches a terminal state (done/failed/expired)
        /// or the timeout elapses (throws TimeoutException). Defaults follow
        /// xAI's docs: 5s poll cadence, 10 minute ceiling.
        public async Task<XAIGrokVideoResult> GenerateVideoAsync(
            XAIGrokVideoGenerateRequest request,
            TimeSpan? pollInterval = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            var start = await StartVideoAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(start.RequestId))
            {
                throw new XAIGrokException("xAI /videos/generations returned no request_id.", 200, start.RawBody ?? "");
            }
            return await PollVideoToCompletionAsync(start.RequestId, pollInterval, timeout, ct).ConfigureAwait(false);
        }

        /// Extension counterpart of GenerateVideoAsync: start the extension
        /// and poll until terminal. Same defaults (5s cadence, 10 min ceiling).
        public async Task<XAIGrokVideoResult> ExtendVideoAsync(
            XAIGrokVideoExtendRequest request,
            TimeSpan? pollInterval = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            var start = await StartVideoExtensionAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(start.RequestId))
            {
                throw new XAIGrokException("xAI /videos/extensions returned no request_id.", 200, start.RawBody ?? "");
            }
            return await PollVideoToCompletionAsync(start.RequestId, pollInterval, timeout, ct).ConfigureAwait(false);
        }

        private async Task<XAIGrokVideoResult> PollVideoToCompletionAsync(
            string requestId,
            TimeSpan? pollInterval,
            TimeSpan? timeout,
            CancellationToken ct)
        {
            var interval = pollInterval ?? TimeSpan.FromSeconds(5);
            var ceiling = timeout ?? TimeSpan.FromMinutes(10);
            var deadline = DateTime.UtcNow + ceiling;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var result = await GetVideoAsync(requestId, ct).ConfigureAwait(false);
                if (!string.Equals(result.Status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    result.RequestId = requestId;
                    return result;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"xAI video request {requestId} still pending after {ceiling.TotalMinutes:0.#} minutes.");
                }
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }

        private Task<XAIGrokImageResponse> PostAsync<TReq>(string path, TReq body, CancellationToken ct)
            => PostAsync<TReq, XAIGrokImageResponse>(path, body, ct);

        private async Task<TRes> PostAsync<TReq, TRes>(string path, TReq body, CancellationToken ct)
            where TRes : XAIGrokResponseBase
        {
            var serializer = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
            };
            var json = JsonConvert.SerializeObject(body, serializer);

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                throw new XAIGrokException(
                    $"xAI {path} returned {(int)res.StatusCode} {res.StatusCode}: {text}",
                    (int)res.StatusCode,
                    text);
            }

            var parsed = JsonConvert.DeserializeObject<TRes>(text)
                ?? throw new XAIGrokException(
                    $"xAI {path} returned an empty/unparseable body.",
                    (int)res.StatusCode,
                    text);
            parsed.RawBody = text;
            return parsed;
        }
    }

    /// Base for all parsed xAI responses so the generic POST helper can stash
    /// the raw JSON body for debugging regardless of concrete shape.
    public abstract class XAIGrokResponseBase
    {
        /// Original JSON body as returned by xAI. Not part of the wire format.
        [JsonIgnore]
        public string? RawBody { get; set; }
    }

    // ---------- Request DTOs ----------

    /// Request body for POST /v1/images/generations. Only `Prompt` is required
    /// per the xAI REST reference; everything else is optional. We intentionally
    /// DO NOT expose `size` or `style` — xAI's docs mark both as unsupported and
    /// sending them is a footgun.
    public class XAIGrokGenerateRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonProperty("model")]
        public string? Model { get; set; }

        [JsonProperty("n")]
        public int? N { get; set; }

        /// "1:1" | "3:4" | "4:3" | "9:16" | "16:9" | "2:3" | "3:2" |
        /// "9:19.5" | "19.5:9" | "9:20" | "20:9" | "1:2" | "2:1" | "auto"
        [JsonProperty("aspect_ratio")]
        public string? AspectRatio { get; set; }

        /// "low" | "medium" | "high"
        [JsonProperty("quality")]
        public string? Quality { get; set; }

        /// "1k" | "2k"
        [JsonProperty("resolution")]
        public string? Resolution { get; set; }

        /// "url" (default) or "b64_json" for inline base64 (no data URI prefix).
        [JsonProperty("response_format")]
        public string? ResponseFormat { get; set; }

        /// Optional end-user identifier for xAI abuse monitoring.
        [JsonProperty("user")]
        public string? User { get; set; }
    }

    /// Request body for POST /v1/images/edits. xAI supports up to 5 input images
    /// per request; they can be supplied as public URLs or base64 data URIs via
    /// `XAIGrokImageInput`. The aspect ratio of the output defaults to the first
    /// input's AR; override with `AspectRatio` to force a specific shape.
    public class XAIGrokEditRequest
    {
        [JsonProperty("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonProperty("model")]
        public string? Model { get; set; }

        [JsonProperty("n")]
        public int? N { get; set; }

        /// Single-image convenience. If you need multiple inputs, populate
        /// `Images` instead (and leave this null).
        [JsonProperty("image")]
        public XAIGrokImageInput? Image { get; set; }

        /// Multi-image editing. Up to 5 entries per xAI docs.
        [JsonProperty("images")]
        public List<XAIGrokImageInput>? Images { get; set; }

        [JsonProperty("aspect_ratio")]
        public string? AspectRatio { get; set; }

        [JsonProperty("response_format")]
        public string? ResponseFormat { get; set; }

        [JsonProperty("user")]
        public string? User { get; set; }
    }

    /// One input image for /images/edits. Exactly one of Url / Base64Data
    /// should be populated. `Type` tells xAI how to interpret the payload
    /// ("image_url" for Url, "base64" for Base64Data).
    public class XAIGrokImageInput
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "image_url";

        /// Public HTTPS URL the xAI backend will fetch.
        [JsonProperty("url")]
        public string? Url { get; set; }

        /// Full base64 data-URI, e.g. "data:image/png;base64,AAAA..."
        [JsonProperty("data")]
        public string? Base64Data { get; set; }

        public static XAIGrokImageInput FromUrl(string url) => new()
        {
            Type = "image_url",
            Url = url,
        };

        public static XAIGrokImageInput FromBase64(string dataUri) => new()
        {
            Type = "base64",
            Base64Data = dataUri,
        };
    }

    // ---------- Response DTOs ----------

    public class XAIGrokImageResponse : XAIGrokResponseBase
    {
        [JsonProperty("data")]
        public List<XAIGrokImageData> Data { get; set; } = new();

        [JsonProperty("usage")]
        public XAIGrokUsage? Usage { get; set; }

        /// Server-populated only on some xAI responses; Unix seconds.
        [JsonProperty("created")]
        public long? Created { get; set; }
    }

    public class XAIGrokImageData
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("b64_json")]
        public string? Base64Json { get; set; }

        [JsonProperty("mime_type")]
        public string? MimeType { get; set; }

        /// Deprecated in current xAI docs — always returned as empty string —
        /// but we keep the field in case xAI reactivates it.
        [JsonProperty("revised_prompt")]
        public string? RevisedPrompt { get; set; }
    }

    public class XAIGrokUsage
    {
        /// Cost of this request in USD ticks. One tick == $1e-8 per xAI billing
        /// conventions; convert with `cost_in_usd_ticks / 1e8` for dollars.
        [JsonProperty("cost_in_usd_ticks")]
        public long? CostInUsdTicks { get; set; }

        /// Convenience conversion to USD. Returns null if ticks weren't reported.
        [JsonIgnore]
        public decimal? CostUsd => CostInUsdTicks.HasValue
            ? CostInUsdTicks.Value / 100_000_000m
            : (decimal?)null;
    }

    /// Thrown when xAI returns a non-2xx. Carries the raw response body so
    /// callers can surface detailed errors (content-policy refusals, 402
    /// insufficient credits, 422 validation, etc.).
    public class XAIGrokException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public XAIGrokException(string message, int statusCode, string responseBody)
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
