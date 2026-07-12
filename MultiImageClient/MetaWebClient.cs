#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class MetaWebException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public MetaWebException(string message, int statusCode = 0, string responseBody = "")
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }
    }

    public sealed class MetaWebImageGenerationResult
    {
        public required List<byte[]> Images { get; init; }
        public required List<string> ImageUrls { get; init; }
        public string? ConversationId { get; init; }
    }

    /// Reverse-engineered client for the meta.ai consumer web app (Muse Image).
    /// This is the cookie-session sibling of GrokWebClient: no API key, just the
    /// browser jar. It speaks Meta's persisted-query GraphQL endpoint the same
    /// way the web app does.
    ///
    /// IMPORTANT — this is best-effort and fragile by nature:
    ///   * Meta uses PERSISTED queries: the client sends only a `doc_id` hash,
    ///     never the query text. Meta rotates these ids and mutates the schema
    ///     frequently. When the stored query no longer validates against the
    ///     live schema the server returns HTTP 200 with a
    ///     GRAPHQL_VALIDATION_FAILED error (not a 4xx). We detect that and throw
    ///     a clear "capture a fresh doc_id" message rather than silently
    ///     succeeding on an empty result.
    ///   * The default `doc_id` below is the last publicly documented
    ///     TEXT_TO_IMAGE id (from the community metaai-api project). Override it
    ///     without recompiling via settings.json `MetaWebImageDocId` or
    ///     `--meta-web-doc-id`; grab the current value from DevTools > Network on
    ///     https://www.meta.ai while generating an image.
    ///   * Meta has been migrating chat/image to a WebSocket ("DGW") transport
    ///     with server-side message integrity checks. If the HTTP GraphQL path
    ///     is fully retired this client will need a WebSocket rewrite; the
    ///     three-layer split (loader / client / generator) keeps that contained.
    public sealed class MetaWebClient : IDisposable
    {
        public const string DefaultEndpoint = "https://www.meta.ai/api/graphql/";

        // Last publicly documented persisted-query ids for the imagine flow.
        // These WILL drift; treat them as seed defaults, overridable via settings.
        public const string DefaultImageDocId = "d4e29678d29dc9703db0df437793864c";
        public const string DefaultPollMediaDocId = "335a1ff137a82e22e0a9724d4bf70b6f";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";

        private static readonly Regex MediaUrlRegex = new(
            @"https:\\?/\\?/[^""'\s\\]*(?:fbcdn\.net|cdninstagram\.com|scontent)[^""'\s\\]*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly HttpClient _http;
        private readonly string _cookieHeader;
        private readonly string _endpoint;
        private readonly string _imageDocId;
        private readonly string _pollDocId;

        public MetaWebClient(string cookieHeader, string? imageDocId = null, string? endpoint = null, string? pollDocId = null)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                throw new ArgumentException("Cookie header is empty.", nameof(cookieHeader));
            }

            _cookieHeader = cookieHeader.Trim();
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
            _imageDocId = string.IsNullOrWhiteSpace(imageDocId) ? DefaultImageDocId : imageDocId.Trim();
            _pollDocId = string.IsNullOrWhiteSpace(pollDocId) ? DefaultPollMediaDocId : pollDocId.Trim();

            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.meta.ai");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.meta.ai/");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", _cookieHeader);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        }

        public string ImageDocId => _imageDocId;

        public static MetaWebClient FromCookieFile(string cookieFilePath, Settings? settings = null)
            => new(
                MetaWebCookieLoader.LoadCookieHeader(cookieFilePath),
                settings?.MetaWebImageDocId,
                settings?.MetaWebGraphqlEndpoint);

        public async Task<MetaWebImageGenerationResult> GenerateImageAsync(
            string prompt,
            string orientation,
            int numImages,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var ct = timeoutCts.Token;

            var conversationId = Guid.NewGuid().ToString();
            var variables = new
            {
                conversationId,
                content = "Imagine " + prompt,
                imagineOperationRequest = new
                {
                    operation = "TEXT_TO_IMAGE",
                    textToImageParams = new
                    {
                        prompt,
                        orientation = NormalizeOrientation(orientation),
                        numImages = Math.Max(1, numImages),
                    },
                },
            };

            var body = await PostPersistedQueryAsync(
                _imageDocId,
                "useAbraSendMessageMutation",
                variables,
                ct);

            var (urls, isStillGenerating, mediaSetId) = ExtractImageUrls(body);

            // Some responses stream a media/card id first and fill the CDN urls in
            // a subsequent poll. If we got an id but no final urls, poll for them.
            if (urls.Count == 0 && isStillGenerating && !string.IsNullOrEmpty(mediaSetId))
            {
                urls = await PollForMediaUrlsAsync(mediaSetId, TimeSpan.FromSeconds(4), timeout, ct);
            }

            if (urls.Count == 0)
            {
                throw new MetaWebException(
                    "Meta web returned no image URLs. The prompt may have been refused, or the persisted-query "
                    + $"doc_id ({_imageDocId}) is stale — capture a fresh one from DevTools and set MetaWebImageDocId / --meta-web-doc-id. "
                    + $"body={Truncate(body, 600)}");
            }

            var images = new List<byte[]>();
            foreach (var url in urls.Take(Math.Max(1, numImages)))
            {
                images.Add(await DownloadBytesAsync(url, ct));
            }

            return new MetaWebImageGenerationResult
            {
                Images = images,
                ImageUrls = urls,
                ConversationId = conversationId,
            };
        }

        private async Task<List<string>> PollForMediaUrlsAsync(
            string mediaSetId,
            TimeSpan interval,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var docId = _pollDocId;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var body = await PostPersistedQueryAsync(
                        docId,
                        "AbraMediaFetchQuery",
                        new { mediaSetID = mediaSetId, mediaSetId },
                        cancellationToken);
                    var (urls, _, _) = ExtractImageUrls(body);
                    if (urls.Count > 0)
                    {
                        return urls;
                    }
                }
                catch (MetaWebException)
                {
                    // Poll doc_id may itself be stale; keep trying until timeout.
                }

                await Task.Delay(interval, cancellationToken);
            }

            return new List<string>();
        }

        private async Task<string> PostPersistedQueryAsync(
            string docId,
            string friendlyName,
            object variables,
            CancellationToken cancellationToken)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("fb_api_caller_class", "RelayModern"),
                new("fb_api_req_friendly_name", friendlyName),
                new("variables", JsonSerializer.Serialize(variables)),
                new("server_timestamps", "true"),
                new("doc_id", docId),
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };

            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new MetaWebException(
                    $"Meta web GraphQL request failed ({(int)response.StatusCode}). Cookies may be expired — re-export from DevTools.",
                    (int)response.StatusCode,
                    body);
            }

            // Meta returns HTTP 200 even for persisted-query validation failures.
            ThrowIfGraphqlError(body, docId);
            return body;
        }

        private static void ThrowIfGraphqlError(string body, string docId)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            // The response may be multipart/newline-delimited JSON. Inspect each
            // JSON chunk for a top-level "errors" array or a validation code.
            foreach (var chunk in EnumerateJsonChunks(body))
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(chunk);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (root.TryGetProperty("errors", out var errors)
                        && errors.ValueKind == JsonValueKind.Array
                        && errors.GetArrayLength() > 0)
                    {
                        var first = errors[0];
                        var message = first.TryGetProperty("message", out var m) ? m.GetString() : "unknown GraphQL error";
                        if (body.Contains("GRAPHQL_VALIDATION_FAILED", StringComparison.Ordinal))
                        {
                            throw new MetaWebException(
                                $"Meta web persisted query {docId} failed validation (GRAPHQL_VALIDATION_FAILED). "
                                + "Meta rotated the schema/doc_id — capture the current TEXT_TO_IMAGE doc_id from DevTools > Network on "
                                + $"https://www.meta.ai and set MetaWebImageDocId / --meta-web-doc-id. Server said: {message}");
                        }

                        throw new MetaWebException($"Meta web GraphQL error: {message}");
                    }
                }
            }
        }

        private static (List<string> Urls, bool IsStillGenerating, string? MediaSetId) ExtractImageUrls(string body)
        {
            var urls = new HashSet<string>(StringComparer.Ordinal);
            var stillGenerating = false;
            string? mediaSetId = null;

            foreach (Match match in MediaUrlRegex.Matches(body))
            {
                var url = match.Value.Replace("\\/", "/").Replace("\\u0025", "%");
                // Skip obvious avatar/emoji/sticker assets; keep generated media.
                if (url.Contains("emoji", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("static", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                urls.Add(url);
            }

            foreach (var chunk in EnumerateJsonChunks(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(chunk);
                    ScanElement(doc.RootElement, urls, ref stillGenerating, ref mediaSetId);
                }
                catch (JsonException)
                {
                    // Regex already covered non-parseable streamed fragments.
                }
            }

            return (urls.ToList(), stillGenerating || urls.Count == 0, mediaSetId);
        }

        private static void ScanElement(JsonElement element, HashSet<string> urls, ref bool stillGenerating, ref string? mediaSetId)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var name = prop.Name;
                        if ((name.Equals("uri", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("url", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("imageURI", StringComparison.OrdinalIgnoreCase))
                            && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var url = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(url)
                                && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                && (url.Contains("fbcdn", StringComparison.OrdinalIgnoreCase)
                                    || url.Contains("scontent", StringComparison.OrdinalIgnoreCase)
                                    || url.Contains("cdninstagram", StringComparison.OrdinalIgnoreCase)))
                            {
                                urls.Add(url);
                            }
                        }

                        if ((name.Equals("mediaSetID", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("mediaSetId", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("card_id", StringComparison.OrdinalIgnoreCase)
                             || name.Equals("cardId", StringComparison.OrdinalIgnoreCase))
                            && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            mediaSetId ??= prop.Value.GetString();
                        }

                        if (name.Contains("state", StringComparison.OrdinalIgnoreCase)
                            && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var state = prop.Value.GetString() ?? "";
                            if (state.Contains("GENERATING", StringComparison.OrdinalIgnoreCase)
                                || state.Contains("PENDING", StringComparison.OrdinalIgnoreCase)
                                || state.Contains("IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
                            {
                                stillGenerating = true;
                            }
                        }

                        ScanElement(prop.Value, urls, ref stillGenerating, ref mediaSetId);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        ScanElement(item, urls, ref stillGenerating, ref mediaSetId);
                    }
                    break;
            }
        }

        private static IEnumerable<string> EnumerateJsonChunks(string body)
        {
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || (line[0] != '{' && line[0] != '['))
                {
                    continue;
                }

                yield return line;
            }
        }

        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.meta.ai/");
            using var response = await _http.SendAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new MetaWebException(
                    $"Meta web image download failed ({(int)response.StatusCode}) for {url}.",
                    (int)response.StatusCode,
                    Encoding.UTF8.GetString(bytes.Take(500).ToArray()));
            }

            return bytes;
        }

        private static string NormalizeOrientation(string orientation)
        {
            var o = (orientation ?? "").Trim().ToUpperInvariant();
            return o switch
            {
                "H" or "HORIZONTAL" or "LANDSCAPE" or "WIDE" => "HORIZONTAL",
                "S" or "SQUARE" => "SQUARE",
                _ => "VERTICAL",
            };
        }

        public void Dispose() => _http.Dispose();

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s.Substring(0, max) + "...";
    }
}
