using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // OpenAI `gpt-image-2` — released 2026-04-21. Uses the standard
    // Images API at /v1/images/generations. Accepts `size`, `quality`
    // (low/medium/high/auto), `n`, and `moderation` (auto/low).
    //
    // On THIS endpoint (/generations) the `input_fidelity` parameter must
    // not be sent — gpt-image-2 always renders at high fidelity and the
    // generations endpoint rejects the field. Note the OpenAI cookbook's
    // gpt-image-2 edits examples do pass `input_fidelity="high"`, so the
    // restriction may be generations-specific; re-test when wiring the
    // /edits endpoint. Transparent backgrounds are not supported. Pricing is
    // token-based ($30/1M output tokens); GetCost() returns a rough
    // per-quality estimate for reporting only.
    //
    // Popular sizes: 1024x1024, 1536x1024, 1024x1536, 2048x2048, 2048x1152,
    // 2560x1440 (QHD / 2K — cookbook's "recommended upper reliability
    // boundary"), 3824x2144 (near-4K; see below), or "auto". Arbitrary
    // resolutions are also allowed under the constraints: edges multiple of
    // 16, max edge STRICTLY less than 3840 (cookbook 1.1), total pixels in
    // [655360, 8294400], long:short edge ratio <= 3:1. 3840x2160 is listed
    // in some places as "experimental" and may be rejected on some accounts
    // — 3824x2144 is the safe canonical near-4K.
    public class GptImage2Generator : IImageGenerator
    {
        private const string ModelId = "gpt-image-2";

        private readonly SemaphoreSlim _semaphore;
        // Typical gpt-image-2 latency is 10-60s, but OpenAI's own docs warn
        // "complex prompts may take up to 2 minutes" and the launch-day tail
        // can be worse. Default HttpClient.Timeout is 100s, which cuts into
        // that envelope. 10 min is a safety buffer, not a claim about normal
        // behavior.
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        private readonly MultiClientRunStats _stats;
        private readonly string _moderation;
        private readonly string _name;
        // Pools from which size and quality are randomly chosen per call. Single-
        // element pools behave exactly like a fixed size/quality generator, so
        // the old "one fixed variant" usage still works without special cases.
        private readonly string[] _sizePool;
        private readonly OpenAIGPTImageOneQuality[] _qualityPool;

        // How many images to request per call (`n` in the request body). 1 is
        // the common case; >1 is useful for logo/variant exploration (cookbook
        // 4.5) where a single round-trip returns multiple candidates. The
        // streaming handler collects all N images and returns them as separate
        // CreatedBase64Image entries so ImageManager's per-index save path
        // ("...img0", "...img1", ...) produces distinct files automatically.
        private readonly int _imageCount;

        // When non-empty, each streamed partial PNG is written under this
        // folder (in a per-day "PartialsLive" subfolder) as it arrives. When
        // _popUpPartials is also true, each saved partial is opened in the
        // system default image viewer via Process.Start. This is the
        // --quick-test interactive-feedback path; for normal runs both are
        // off and partials are logged but not persisted.
        private readonly string _partialSaveFolder;
        private readonly bool _popUpPartials;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GptImage2;

        public GptImage2Generator(string apiKey, int maxConcurrency, string size, string moderation, OpenAIGPTImageOneQuality quality, MultiClientRunStats stats, string name)
            : this(apiKey, maxConcurrency, new[] { size }, moderation, new[] { quality }, stats, name)
        {
        }

        public GptImage2Generator(
            string apiKey,
            int maxConcurrency,
            string[] sizePool,
            string moderation,
            OpenAIGPTImageOneQuality[] qualityPool,
            MultiClientRunStats stats,
            string name,
            string partialSaveFolder = null,
            bool popUpPartials = false,
            int imageCount = 1)
        {
            if (sizePool == null || sizePool.Length == 0) throw new ArgumentException("sizePool must be non-empty", nameof(sizePool));
            if (qualityPool == null || qualityPool.Length == 0) throw new ArgumentException("qualityPool must be non-empty", nameof(qualityPool));
            if (imageCount < 1) throw new ArgumentException("imageCount must be >= 1", nameof(imageCount));
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _sizePool = sizePool;
            _moderation = moderation;
            _qualityPool = qualityPool;
            _name = name ?? "";
            _stats = stats;
            _partialSaveFolder = partialSaveFolder ?? "";
            _popUpPartials = popUpPartials;
            _imageCount = imageCount;
        }

        // Log tag + fallback label if an outer exception prevents us from
        // building the richer per-call label. Always leads with ModelId so
        // "gpt-image-2" is present even for named variants like "fast".
        public string GetGeneratorSpecPart() => string.IsNullOrEmpty(_name) ? ModelId : $"{ModelId} {_name}";

        public string GetFilenamePart(PromptDetails pd)
        {
            // Prefer the per-call choices written into PromptDetails.RuntimeMeta by
            // ProcessPromptAsync so the filename reflects the actual request.
            var size = _sizePool[0];
            var quality = _qualityPool[0].ToString();
            if (pd?.RuntimeMeta != null)
            {
                if (pd.RuntimeMeta.TryGetValue("size", out var s) && !string.IsNullOrEmpty(s)) size = s;
                if (pd.RuntimeMeta.TryGetValue("quality", out var q) && !string.IsNullOrEmpty(q)) quality = q;
            }
            var modpt = string.IsNullOrEmpty(_moderation) || _moderation == "low" ? "" : $" mod{_moderation}";
            return $"gpt-2_{_name}{modpt}{size} qual{quality}";
        }

        // Token-based pricing. Until per-image averages are published this
        // returns a conservative estimate derived from the documented
        // $30/1M output-token rate and typical token counts seen for
        // gpt-image-1 at the same size; treat as a ceiling, not a bill.
        public decimal GetCost()
        {
            // Random pools make per-call cost unknowable at instance level; report
            // the ceiling of the active quality pool, scaled by `n` since we
            // always return exactly `n` images per call.
            var worst = _qualityPool.Max();
            var perImage = worst switch
            {
                OpenAIGPTImageOneQuality.low => 0.02m,
                OpenAIGPTImageOneQuality.medium => 0.08m,
                OpenAIGPTImageOneQuality.high => 0.25m,
                _ => 0.25m,
            };
            return perImage * _imageCount;
        }

        public List<string> GetRightParts()
        {
            var modpt = $" moderation {_moderation}";
            var qualitypt = _qualityPool.Length == 1
                ? $"quality {_qualityPool[0]}"
                : $"quality RANDOM({string.Join("/", _qualityPool)})";
            var sizept = _sizePool.Length == 1
                ? $"size {_sizePool[0]}"
                : $"size RANDOM({string.Join("/", _sizePool)})";
            var parts = new List<string> { ModelId, _name, sizept, qualitypt, modpt };
            if (_imageCount != 1) parts.Add($"n {_imageCount}");
            return parts;
        }

        // How many partial images to request mid-stream. 0-3. Each partial
        // costs +100 output tokens (~fractions of a cent), cheap relative to
        // the value of seeing progress. We pick 2 so we get ~33% and ~66%
        // snapshots along the way.
        private const int PartialImageCount = 2;

        // Log a "still waiting..." line this often when the stream is quiet.
        // The server does emit partials, but the pre-first-partial gap can
        // be 10-30s and the final gap before `completed` can be similar.
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();

            // Pick size + quality for this single call from the configured pools.
            // For single-element pools this is a deterministic pick and behaves
            // just like the fixed-variant generator did previously.
            var chosenSize = _sizePool[Random.Shared.Next(_sizePool.Length)];
            var chosenQuality = _qualityPool[Random.Shared.Next(_qualityPool.Length)];
            var arKeyword = SizeToAspectKeyword(chosenSize);
            // Rich per-call label — ends up in the combined-image overlay and in
            // per-save filenames via RuntimeMeta. GetGeneratorSpecPart() still
            // returns just ModelId for top-level log lines, so the log stays terse.
            // Always lead with the model id so the combined-image label makes
            // it obvious which API produced this panel. Any per-variant `_name`
            // tag gets appended after, never substituted for the model id.
            var richLabel = string.IsNullOrEmpty(_name)
                ? $"{ModelId}  {chosenQuality}  {arKeyword}"
                : $"{ModelId} {_name}  {chosenQuality}  {arKeyword}";
            var genTag = richLabel;

            if (promptDetails != null)
            {
                promptDetails.RuntimeMeta["size"] = chosenSize;
                promptDetails.RuntimeMeta["quality"] = chosenQuality.ToString();
                promptDetails.RuntimeMeta["label"] = richLabel;
            }

            try
            {
                _stats.GptImage2RequestCount++;

                var bodyDict = new Dictionary<string, object>
                {
                    ["model"] = ModelId,
                    ["prompt"] = promptDetails.Prompt,
                    ["quality"] = chosenQuality.ToString(),
                    ["n"] = _imageCount,
                    ["size"] = chosenSize,
                    ["stream"] = true,
                    ["partial_images"] = PartialImageCount,
                };
                if (!string.IsNullOrEmpty(_moderation))
                {
                    bodyDict["moderation"] = _moderation;
                }

                var bodyJson = JsonSerializer.Serialize(bodyDict);
                Logger.Log($"    [{genTag}] POST /v1/images/generations body: {bodyJson}");

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var heartbeatCts = new CancellationTokenSource();
                var heartbeatTask = RunHeartbeatAsync(genTag, sw, heartbeatCts.Token);

                try
                {
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    Logger.Log($"    [{genTag}] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} (HTTP/{resp.Version})");

                    if (!resp.IsSuccessStatusCode)
                    {
                        _stats.GptImage2RefusedCount++;
                        var errBody = await resp.Content.ReadAsStringAsync();
                        string errorMessage;
                        try
                        {
                            using var errDoc = JsonDocument.Parse(errBody);
                            errorMessage = errDoc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? errBody;
                        }
                        catch
                        {
                            errorMessage = errBody;
                        }
                        var cleanedMessage = errorMessage.Split("If you believe").First().Trim();
                        Logger.Log($"    [{genTag}] HTTP {(int)resp.StatusCode} after {sw.ElapsedMilliseconds} ms: {cleanedMessage}");
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = cleanedMessage,
                            PromptDetails = promptDetails,
                            ImageGenerator = ImageGeneratorApiType.GptImage2,
                            CreateTotalMs = sw.ElapsedMilliseconds,
                            ImageGeneratorDescription = genTag,
                        };
                    }

                    Logger.Log($"    [{genTag}] connected, streaming (partial_images={PartialImageCount}, n={_imageCount})");

                    // When n>1 the server streams events for each image in
                    // parallel; each event can carry an `image_index` (or
                    // `output_index` on some event shapes) distinguishing
                    // which of the N images it belongs to. Collect completed
                    // images into a SortedDictionary keyed by that index so
                    // the final list comes back in a stable 0..N-1 order.
                    // When no index is present (n=1 or older event shape),
                    // we assign sequentially so insertion order wins.
                    var finalImages = new SortedDictionary<int, (string b64, string revisedPrompt)>();
                    int nextFallbackIdx = 0;
                    string streamErrorMessage = null;
                    long lastEventMs = 0;

                    await using var rawStream = await resp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(rawStream, Encoding.UTF8);

                    while (!reader.EndOfStream)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (line.Length == 0) continue;                         // event boundary
                        if (!line.StartsWith("data:")) continue;                // ignore `event:` / comment lines
                        var payload = line.Substring(5).TrimStart();
                        if (payload == "[DONE]") break;

                        using var evt = JsonDocument.Parse(payload);
                        var root = evt.RootElement;
                        var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : "(no type)";
                        var nowMs = sw.ElapsedMilliseconds;
                        var sinceLast = nowMs - lastEventMs;
                        lastEventMs = nowMs;

                        switch (type)
                        {
                            case "image_generation.partial_image":
                            {
                                var pidx = root.TryGetProperty("partial_image_index", out var iEl) ? iEl.GetInt32() : -1;
                                var imgIdx = ExtractImageIndex(root);
                                var imgTag = _imageCount > 1 ? $" img{imgIdx}" : "";
                                Logger.Log($"    [{genTag}] partial #{pidx}{imgTag} at {nowMs} ms (+{sinceLast} ms since last event)");
                                if (!string.IsNullOrEmpty(_partialSaveFolder)
                                    && root.TryGetProperty("b64_json", out var pbEl)
                                    && pbEl.ValueKind == JsonValueKind.String)
                                {
                                    TrySavePartial(pbEl.GetString(), pidx, imgIdx, promptDetails, genTag);
                                }
                                break;
                            }
                            case "image_generation.completed":
                            {
                                var imgIdx = ExtractImageIndex(root);
                                if (imgIdx < 0) imgIdx = nextFallbackIdx;
                                nextFallbackIdx = Math.Max(nextFallbackIdx, imgIdx + 1);

                                string b64 = root.TryGetProperty("b64_json", out var bEl) ? bEl.GetString() : null;
                                string revisedPrompt = root.TryGetProperty("revised_prompt", out var rpEl) ? rpEl.GetString() : null;
                                if (!string.IsNullOrEmpty(b64))
                                {
                                    finalImages[imgIdx] = (b64, revisedPrompt);
                                }

                                string usageSummary = ExtractUsageSummary(root);
                                var imgTag = _imageCount > 1 ? $" img{imgIdx}" : "";
                                Logger.Log($"    [{genTag}] completed{imgTag} at {nowMs} ms (+{sinceLast} ms since last event).{usageSummary}");

                                if (!string.IsNullOrEmpty(revisedPrompt))
                                {
                                    Logger.Log($"    [{genTag}] revised_prompt{imgTag}: {revisedPrompt}");
                                }
                                break;
                            }
                            case "error":
                            case "image_generation.error":
                            {
                                var (msg, code) = ExtractErrorDetails(root);
                                streamErrorMessage = msg ?? payload;
                                var codePart = string.IsNullOrEmpty(code) ? "" : $" [code={code}]";
                                Logger.Log($"    [{genTag}] ERROR event at {nowMs} ms{codePart}: {streamErrorMessage}");
                                break;
                            }
                            default:
                                // Unknown event types: log the type so we notice but don't dump payload.
                                Logger.Log($"    [{genTag}] event '{type}' at {nowMs} ms (no handler)");
                                break;
                        }
                    }

                    if (finalImages.Count == 0)
                    {
                        _stats.GptImage2RefusedCount++;
                        var msg = streamErrorMessage ?? "stream ended without an image_generation.completed event";
                        Logger.Log($"    [{genTag}] {msg} after {sw.ElapsedMilliseconds} ms");
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = msg,
                            PromptDetails = promptDetails,
                            ImageGenerator = ImageGeneratorApiType.GptImage2,
                            CreateTotalMs = sw.ElapsedMilliseconds,
                            ImageGeneratorDescription = genTag,
                        };
                    }

                    if (finalImages.Count < _imageCount)
                    {
                        // Partial success — we asked for N but only got M<N back.
                        // Surface the shortfall in logs but still save what we
                        // have; a partial batch is usually more useful than
                        // nothing, and the user can re-roll.
                        Logger.Log($"    [{genTag}] WARNING: requested n={_imageCount} but only {finalImages.Count} image(s) completed");
                    }

                    var b64s = finalImages.Values
                        .Select(v => new CreatedBase64Image { bytesBase64 = v.b64, newPrompt = v.revisedPrompt ?? "" })
                        .ToList();

                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Base64ImageDatas = b64s,
                        Url = "",
                        ErrorMessage = "",
                        PromptDetails = promptDetails,
                        ImageGeneratorDescription = genTag,
                        ImageGenerator = ImageGeneratorApiType.GptImage2,
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* ignored */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"    [{genTag}] EXCEPTION after {sw.ElapsedMilliseconds} ms: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGeneratorDescription = genTag,
                    ImageGenerator = ImageGeneratorApiType.GptImage2,
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // When n>1 the server distinguishes per-image events by one of a
        // couple possible fields. Try the documented "image_index" first,
        // then fall back to "output_index" which some streaming builds emit
        // instead. Returns -1 when neither is present (n=1 case; caller
        // assigns insertion order).
        private static int ExtractImageIndex(JsonElement root)
        {
            if (root.TryGetProperty("image_index", out var ii) && ii.ValueKind == JsonValueKind.Number)
            {
                return ii.GetInt32();
            }
            if (root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number)
            {
                return oi.GetInt32();
            }
            return -1;
        }

        // Pretty-prints the `usage` block if present, including the detailed
        // breakdown (image_tokens vs text_tokens) for both input and output.
        // Returns leading-space so it composes directly into a one-liner.
        private static string ExtractUsageSummary(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            {
                return "";
            }
            var inTot = u.TryGetProperty("input_tokens", out var x1) ? x1.GetInt32() : 0;
            var outTot = u.TryGetProperty("output_tokens", out var x2) ? x2.GetInt32() : 0;
            var total = u.TryGetProperty("total_tokens", out var x3) ? x3.GetInt32() : 0;
            var inText = 0; var inImg = 0; var outText = 0; var outImg = 0;
            if (u.TryGetProperty("input_tokens_details", out var inD) && inD.ValueKind == JsonValueKind.Object)
            {
                if (inD.TryGetProperty("text_tokens", out var t)) inText = t.GetInt32();
                if (inD.TryGetProperty("image_tokens", out var i)) inImg = i.GetInt32();
            }
            if (u.TryGetProperty("output_tokens_details", out var outD) && outD.ValueKind == JsonValueKind.Object)
            {
                if (outD.TryGetProperty("text_tokens", out var t)) outText = t.GetInt32();
                if (outD.TryGetProperty("image_tokens", out var i)) outImg = i.GetInt32();
            }
            return $" usage: in={inTot} (text={inText},img={inImg}) out={outTot} (text={outText},img={outImg}) total={total}";
        }

        // gpt-image-2 size constraints, cribbed from the class-level doc:
        //   - "auto" is always valid (server picks)
        //   - WxH format, each edge a positive multiple of 16
        //   - each edge <= 3840
        //   - total pixels in [655360, 8294400]
        //   - long:short edge ratio <= 3:1
        // These are the constants we compare against; keep them in sync with
        // the doc at the top of the class if OpenAI changes the envelope.
        public const int SizeEdgeMultiple = 16;
        public const int SizeMaxEdge = 3840;
        public const int SizeMinPixels = 655360;
        public const int SizeMaxPixels = 8294400;
        public const double SizeMaxAspectRatio = 3.0;

        // Validate and gently autocorrect a user-supplied "WxH" size string.
        //
        // Returns true when `normalized` is safe to send to the API. Emits the
        // possibly-snapped value (e.g. "1526x2048" -> "1520x2048") and a
        // non-null `note` whenever the caller should surface something to the
        // user. Returns false with a human-readable `error` when no amount of
        // snapping can fit the constraints (over 3840, out of pixel range,
        // ratio too extreme, unparseable).
        //
        // "auto" passes through unchanged. Case-insensitive separators 'x' and
        // 'X' are both accepted. Whitespace around the input is trimmed.
        public static bool TryNormalizeSize(string input, out string normalized, out string note, out string error)
        {
            normalized = null;
            note = null;
            error = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "size is empty";
                return false;
            }
            var s = input.Trim();
            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "auto";
                return true;
            }

            var parts = s.Split(new[] { 'x', 'X' }, 2);
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), out var w)
                || !int.TryParse(parts[1].Trim(), out var h))
            {
                error = $"'{input}' is not a WxH size (e.g. 1024x1024) or 'auto'";
                return false;
            }
            if (w <= 0 || h <= 0)
            {
                error = $"edges must be positive (got {w}x{h})";
                return false;
            }

            // Snap each edge to the nearest multiple of 16. Report what we did.
            var snappedW = RoundToMultiple(w, SizeEdgeMultiple);
            var snappedH = RoundToMultiple(h, SizeEdgeMultiple);
            if (snappedW != w || snappedH != h)
            {
                note = $"snapped {w}x{h} -> {snappedW}x{snappedH} (each edge must be a multiple of {SizeEdgeMultiple})";
                w = snappedW;
                h = snappedH;
            }

            // Cookbook (2026-04-21) is explicit that the max edge rule is
            // strict: "Maximum edge length must be less than 3840px". The
            // popular size 3840x2160 is flagged as experimental and may 400
            // on some accounts; safer canonical near-miss is 3824x2144.
            if (w >= SizeMaxEdge || h >= SizeMaxEdge)
            {
                error = $"edge must be < {SizeMaxEdge} (got {w}x{h}; try 3824x2144)";
                return false;
            }

            long pixels = (long)w * h;
            if (pixels < SizeMinPixels)
            {
                error = $"total pixels {pixels:N0} < {SizeMinPixels:N0} minimum ({w}x{h})";
                return false;
            }
            if (pixels > SizeMaxPixels)
            {
                error = $"total pixels {pixels:N0} > {SizeMaxPixels:N0} maximum ({w}x{h})";
                return false;
            }

            double ratio = w >= h ? (double)w / h : (double)h / w;
            if (ratio > SizeMaxAspectRatio + 1e-9)
            {
                error = $"aspect ratio {ratio:F2}:1 exceeds {SizeMaxAspectRatio}:1 cap ({w}x{h})";
                return false;
            }

            normalized = $"{w}x{h}";
            return true;
        }

        private static int RoundToMultiple(int value, int step)
        {
            if (step <= 0) return value;
            // Banker-style "nearest" rounding, with ties going up so that
            // halfway typos (e.g. 1528 between 1520 and 1536) bias toward the
            // larger, more common canonical sizes that users usually meant.
            int rem = value % step;
            if (rem == 0) return value;
            if (rem * 2 >= step) return value + (step - rem);
            return value - rem;
        }

        // "1024x1024" -> "square", "1024x1536" -> "portrait", "1536x1024" -> "landscape".
        // "auto" passes through. Unknown sizes return the string itself so the label
        // still carries useful information.
        private static string SizeToAspectKeyword(string size)
        {
            if (string.IsNullOrEmpty(size)) return "";
            if (size == "auto") return "auto";
            var parts = size.Split('x');
            if (parts.Length != 2) return size;
            if (!int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return size;
            if (w == h) return "square";
            return w > h ? "landscape" : "portrait";
        }

        // OpenAI error events in the SSE stream can be shaped two ways:
        //   { "type": "error", "message": "...", "code": "...", ... }
        // or nested:
        //   { "type": "error", "error": { "message": "...", "code": "..." } }
        // Try both, fall back to null if neither shape matches.
        private static (string message, string code) ExtractErrorDetails(JsonElement root)
        {
            string message = null;
            string code = null;
            if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                if (errEl.TryGetProperty("message", out var m1) && m1.ValueKind == JsonValueKind.String)
                {
                    message = m1.GetString();
                }
                if (errEl.TryGetProperty("code", out var c1) && c1.ValueKind == JsonValueKind.String)
                {
                    code = c1.GetString();
                }
                else if (errEl.TryGetProperty("type", out var t1) && t1.ValueKind == JsonValueKind.String)
                {
                    code = t1.GetString();
                }
            }
            if (message == null && root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
            {
                message = m2.GetString();
            }
            if (!string.IsNullOrEmpty(message))
            {
                message = message.Split("If you believe").First().Trim();
            }
            return (message, code);
        }

        // Decode and write one partial PNG under {base}/{today}/PartialsLive/
        // and (if configured) open it in the default image viewer. Best-effort:
        // a partial save failure never interrupts the generation.
        //
        // `partialIdx` is the partial-within-image index (0..PartialImageCount-1,
        // from the `partial_image_index` event field). `imageIdx` is which of
        // the N images this partial belongs to when n>1; pass -1 when n=1 and
        // the server didn't send an image index.
        private void TrySavePartial(string b64, int partialIdx, int imageIdx, PromptDetails pd, string genTag)
        {
            try
            {
                var bytes = Convert.FromBase64String(b64);
                var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
                var folder = Path.Combine(_partialSaveFolder, today, "PartialsLive");
                Directory.CreateDirectory(folder);

                var promptPart = FilenameGenerator.TruncatePrompt(pd?.Prompt ?? "partial", 60);
                // Zero-padded timestamp keeps files sorted by arrival order
                // across runs; partial index disambiguates within a single call.
                // When n>1, image index distinguishes per-output streams.
                var ts = DateTime.Now.ToString("HHmmss_fff");
                var imgPart = imageIdx >= 0 && _imageCount > 1 ? $"_img{imageIdx}" : "";
                var file = $"{ts}_partial{Math.Max(0, partialIdx):D2}{imgPart}_{promptPart}.png";
                var full = Path.Combine(folder, file);
                File.WriteAllBytes(full, bytes);
                Logger.Log($"    [{genTag}] saved partial #{partialIdx}{imgPart} -> {full}");

                if (_popUpPartials)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"    [{genTag}] pop-up failed for partial #{partialIdx}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"    [{genTag}] failed to save partial #{partialIdx}: {ex.Message}");
            }
        }

        private static async Task RunHeartbeatAsync(string genTag, Stopwatch sw, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (ct.IsCancellationRequested) return;
                Logger.Log($"    [{genTag}] ...still waiting, {sw.ElapsedMilliseconds / 1000}s elapsed");
            }
        }
    }
}
