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
    ///
    /// Docs: https://docs.x.ai/developers/model-capabilities/images/generation
    ///       https://docs.x.ai/developers/rest-api-reference/inference/images
    public class XAIGrokClient
    {
        public const string BaseUrl = "https://api.x.ai/v1";
        public const string ModelGrokImagine = "grok-imagine-image";
        public const string ModelGrokImaginePro = "grok-imagine-image-pro";

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

        private async Task<XAIGrokImageResponse> PostAsync<TReq>(string path, TReq body, CancellationToken ct)
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

            var parsed = JsonConvert.DeserializeObject<XAIGrokImageResponse>(text)
                ?? throw new XAIGrokException(
                    $"xAI {path} returned an empty/unparseable body.",
                    (int)res.StatusCode,
                    text);
            parsed.RawBody = text;
            return parsed;
        }
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

    public class XAIGrokImageResponse
    {
        [JsonProperty("data")]
        public List<XAIGrokImageData> Data { get; set; } = new();

        [JsonProperty("usage")]
        public XAIGrokUsage? Usage { get; set; }

        /// Server-populated only on some xAI responses; Unix seconds.
        [JsonProperty("created")]
        public long? Created { get; set; }

        /// Original JSON body as returned by xAI. Handy for debugging pricing
        /// or schema drift. Not part of the wire format.
        [JsonIgnore]
        public string? RawBody { get; set; }
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
