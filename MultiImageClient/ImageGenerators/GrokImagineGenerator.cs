#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using XAIGrokAPIClient;

namespace MultiImageClient
{
    /// Image generator backed by xAI's /v1/images/generations endpoint (Grok
    /// Imagine family). Uses our hand-rolled XAIGrokClient so every wire
    /// parameter is explicit — no OpenAI SDK indirection.
    ///
    /// Supports both `grok-imagine-image` ($0.02/img) and
    /// `grok-imagine-image-pro` ($0.07/img), selected at construction time via
    /// ImageGeneratorApiType.GrokImagine / GrokImaginePro.
    public class GrokImagineGenerator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly XAIGrokClient _client;
        private readonly HttpClient _httpClient;
        private readonly MultiClientRunStats _stats;
        private readonly ImageGeneratorApiType _apiType;
        private readonly string _model;
        private readonly string _aspectRatio;
        private readonly string _quality;
        private readonly string _resolution;
        private readonly string _responseFormat;
        private readonly string _name;

        public ImageGeneratorApiType ApiType => _apiType;

        /// apiType         — GrokImagine or GrokImaginePro.
        /// aspectRatio     — one of the xAI-documented AR strings ("1:1", "3:4",
        ///                    "4:3", "16:9", etc.), or "auto" to let xAI pick.
        /// quality         — "low" | "medium" | "high".
        /// resolution      — "1k" | "2k".
        /// responseFormat  — "url" (default) or "b64_json". "url" is fine for
        ///                    batch runs since we download immediately.
        public GrokImagineGenerator(
            string apiKey,
            int maxConcurrency,
            ImageGeneratorApiType apiType,
            MultiClientRunStats stats,
            string name = "",
            string aspectRatio = "1:1",
            string quality = "high",
            string resolution = "1k",
            string responseFormat = "url")
        {
            if (apiType != ImageGeneratorApiType.GrokImagine && apiType != ImageGeneratorApiType.GrokImaginePro)
            {
                throw new ArgumentException(
                    $"GrokImagineGenerator only supports GrokImagine or GrokImaginePro, got {apiType}.",
                    nameof(apiType));
            }
            _apiType = apiType;
            _model = apiType == ImageGeneratorApiType.GrokImaginePro
                ? XAIGrokClient.ModelGrokImaginePro
                : XAIGrokClient.ModelGrokImagine;

            _client = new XAIGrokClient(apiKey);
            _httpClient = new HttpClient();
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _name = name ?? string.Empty;
            _aspectRatio = aspectRatio;
            _quality = quality;
            _resolution = resolution;
            _responseFormat = responseFormat;
        }

        /// Short human-readable tier label used in the combined-grid panel
        /// header ("xAI Grok Imagine" vs "xAI Grok Imagine Pro").
        private string TierLabel => _apiType == ImageGeneratorApiType.GrokImaginePro
            ? "xAI Grok Imagine Pro"
            : "xAI Grok Imagine";

        /// Very rough pixel-dimensions estimate for a given resolution+AR,
        /// using the xAI convention that "1k"/"2k" sets the longer edge to
        /// ~1024 / ~2048 pixels respectively and the other edge follows the
        /// aspect ratio. Used for on-image annotation so a viewer can tell
        /// at a glance whether they're looking at a 1 MP or 4 MP render.
        /// Returns the empty string when we can't parse the AR.
        private string EstimatePixelsLabel()
        {
            var longEdge = string.Equals(_resolution, "2k", StringComparison.OrdinalIgnoreCase) ? 2048
                         : string.Equals(_resolution, "1k", StringComparison.OrdinalIgnoreCase) ? 1024
                         : 0;
            if (longEdge == 0) return "";
            if (string.IsNullOrWhiteSpace(_aspectRatio) ||
                _aspectRatio.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return $"~{longEdge}px long edge";
            }
            var parts = _aspectRatio.Split(':', 2);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b) ||
                a <= 0 || b <= 0)
            {
                return $"~{longEdge}px long edge";
            }
            double wRatio = a, hRatio = b;
            int w, h;
            if (wRatio >= hRatio)
            {
                w = longEdge;
                h = (int)System.Math.Round(longEdge * hRatio / wRatio);
            }
            else
            {
                h = longEdge;
                w = (int)System.Math.Round(longEdge * wRatio / hRatio);
            }
            return $"~{w}x{h}";
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var shortModel = _apiType == ImageGeneratorApiType.GrokImaginePro ? "grok-pro" : "grok";
            var arLabel = string.IsNullOrWhiteSpace(_aspectRatio) ? "auto" : _aspectRatio.Replace(':', 'x');
            var qLabel = string.IsNullOrWhiteSpace(_quality) ? "q" : $"q{_quality}";
            var resLabel = string.IsNullOrWhiteSpace(_resolution) ? "" : $"_{_resolution}";
            var nameLabel = string.IsNullOrEmpty(_name) ? "" : $"_{_name}";
            return $"{shortModel}_{arLabel}_{qLabel}{resLabel}{nameLabel}";
        }

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                TierLabel,
                _model,
            };
            parts.Add(string.IsNullOrWhiteSpace(_aspectRatio)
                ? "AR auto"
                : $"AR {_aspectRatio}");
            if (!string.IsNullOrWhiteSpace(_quality)) parts.Add($"quality {_quality}");
            if (!string.IsNullOrWhiteSpace(_resolution)) parts.Add($"res {_resolution}");
            var px = EstimatePixelsLabel();
            if (!string.IsNullOrEmpty(px)) parts.Add(px);
            if (!string.IsNullOrEmpty(_name)) parts.Add(_name);
            return parts;
        }

        public string GetGeneratorSpecPart()
        {
            // Always lead with the tier label so the combined-grid panel
            // header credits xAI explicitly — "grok-imagine-image" alone
            // reads like a random slug in a multi-provider grid. The user
            // name, if any, is appended so custom variants still show up.
            var px = EstimatePixelsLabel();
            var line = TierLabel;
            if (!string.IsNullOrWhiteSpace(_aspectRatio)) line += $"  {_aspectRatio}";
            if (!string.IsNullOrWhiteSpace(_quality)) line += $"  q:{_quality}";
            if (!string.IsNullOrWhiteSpace(_resolution)) line += $"  {_resolution}";
            if (!string.IsNullOrEmpty(px)) line += $"  ({px})";
            if (!string.IsNullOrEmpty(_name)) line += $"  [{_name}]";
            return line;
        }

        // https://docs.x.ai/developers/models — flat per-image pricing.
        public decimal GetCost() => _apiType == ImageGeneratorApiType.GrokImaginePro ? 0.07m : 0.02m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;

                var prompt = promptDetails.Prompt ?? string.Empty;

                var req = new XAIGrokGenerateRequest
                {
                    Prompt = prompt,
                    Model = _model,
                    AspectRatio = string.IsNullOrWhiteSpace(_aspectRatio) ? null : _aspectRatio,
                    Quality = string.IsNullOrWhiteSpace(_quality) ? null : _quality,
                    Resolution = string.IsNullOrWhiteSpace(_resolution) ? null : _resolution,
                    ResponseFormat = string.IsNullOrWhiteSpace(_responseFormat) ? null : _responseFormat,
                    N = 1,
                };

                var pxHint = EstimatePixelsLabel();
                var pxSuffix = string.IsNullOrEmpty(pxHint) ? "" : $" ({pxHint})";
                Logger.Log($"\t-> {TierLabel} [{_model}] AR={_aspectRatio} q={_quality} res={_resolution}{pxSuffix}: {prompt}");
                var response = await _client.GenerateAsync(req);
                sw.Stop();

                if (response.Data == null || response.Data.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Grok returned empty data[] with no images.",
                        PromptDetails = promptDetails,
                        ImageGenerator = _apiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                _stats.GrokImageGenerationSuccessCount++;

                var first = response.Data[0];
                var usd = response.Usage?.CostUsd;
                var usdLabel = usd.HasValue ? $" cost=${usd:0.####}" : string.Empty;
                Logger.Log($"\t<- {TierLabel} [{_model}] OK in {sw.ElapsedMilliseconds} ms{usdLabel}");

                // URL path — typical response. We HEAD it to capture content-type
                // so downstream conversion logic (webp/jpg -> png) kicks in.
                if (!string.IsNullOrEmpty(first.Url))
                {
                    string? contentType = first.MimeType;
                    if (string.IsNullOrEmpty(contentType))
                    {
                        try
                        {
                            var head = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, first.Url));
                            contentType = head.Content.Headers.ContentType?.MediaType;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"\t(Grok) HEAD on image url failed ({ex.Message}); leaving content-type null.");
                        }
                    }

                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Url = first.Url,
                        ContentType = contentType,
                        PromptDetails = promptDetails,
                        ImageGenerator = _apiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                // b64_json path — when the caller asked for inline base64.
                if (!string.IsNullOrEmpty(first.Base64Json))
                {
                    var b64Image = new CreatedBase64Image
                    {
                        bytesBase64 = first.Base64Json,
                        newPrompt = prompt,
                    };
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Base64ImageDatas = new List<CreatedBase64Image> { b64Image },
                        ContentType = first.MimeType ?? "image/png",
                        PromptDetails = promptDetails,
                        ImageGenerator = _apiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                _stats.GrokImageGenerationErrorCount++;
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Grok returned data[0] with neither url nor b64_json.",
                    PromptDetails = promptDetails,
                    ImageGenerator = _apiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (XAIGrokException ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                Logger.Log($"\t<- Grok {_model} FAIL http={ex.StatusCode}: {Truncate(ex.ResponseBody, 500)}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"{ex.StatusCode}: {Truncate(ex.ResponseBody, 300)}",
                    PromptDetails = promptDetails,
                    ImageGenerator = _apiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                Logger.Log($"\t<- Grok {_model} EXCEPTION: {ex.Message}");
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = _apiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
