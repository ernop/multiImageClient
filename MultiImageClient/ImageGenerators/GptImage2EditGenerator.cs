#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // OpenAI gpt-image-2 image editing via POST /v1/images/edits (multipart).
    // C# port of tools/vid2img/vid2img/generate.py. Input images are bound at
    // construction (same pattern as GrokWebImagineEditGenerator /
    // GrokImagineEditGenerator); the prompt arrives per-call through
    // PromptDetails. Non-streaming: /edits does not support SSE partials.
    //
    // Do NOT send `input_fidelity` — gpt-image-2 rejects it on both
    // /generations and /edits (confirmed 2026-07-06,
    // `invalid_input_fidelity_model`). The OpenAI cookbook examples that pass
    // input_fidelity="high" are wrong for this model.
    public class GptImage2EditGenerator : IImageGenerator
    {
        private const string ModelId = "gpt-image-2";
        private const string EditsUrl = "https://api.openai.com/v1/images/edits";

        private static readonly HttpClient _http = new HttpClient
        {
            // Same rationale as GptImage2Generator: complex edits can take
            // minutes; the default 100s HttpClient timeout is too tight.
            Timeout = TimeSpan.FromMinutes(10)
        };

        private readonly string _apiKey;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly IReadOnlyList<string> _inputImagePaths;
        private readonly string _size;
        private readonly OpenAIGPTImageOneQuality _quality;
        private readonly int _imageCount;
        private readonly string _name;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GptImage2Edit;

        public GptImage2EditGenerator(
            string apiKey,
            int maxConcurrency,
            IEnumerable<string> inputImagePaths,
            string size,
            OpenAIGPTImageOneQuality quality,
            MultiClientRunStats stats,
            string name,
            int imageCount = 1)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey required", nameof(apiKey));
            _inputImagePaths = (inputImagePaths ?? throw new ArgumentNullException(nameof(inputImagePaths)))
                .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (_inputImagePaths.Count == 0) throw new ArgumentException("at least one input image required", nameof(inputImagePaths));
            if (imageCount < 1) throw new ArgumentException("imageCount must be >= 1", nameof(imageCount));
            _apiKey = apiKey;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _size = string.IsNullOrWhiteSpace(size) ? "auto" : size;
            _quality = quality;
            _stats = stats;
            _name = name ?? "";
            _imageCount = imageCount;
        }

        public string GetFilenamePart(PromptDetails pd) => $"gpt-2-edit_{_name}{_size} qual{_quality}";

        public string GetGeneratorSpecPart() => string.IsNullOrEmpty(_name) ? $"{ModelId} edit" : $"{ModelId} edit {_name}";

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                ModelId,
                "edit",
                _name,
                $"size {_size}",
                $"quality {_quality}",
                $"input images {_inputImagePaths.Count}",
            };
            if (_imageCount != 1) parts.Add($"n {_imageCount}");
            return parts;
        }

        public decimal GetCost()
        {
            var perImage = _quality switch
            {
                OpenAIGPTImageOneQuality.low => 0.02m,
                OpenAIGPTImageOneQuality.medium => 0.08m,
                OpenAIGPTImageOneQuality.high => 0.25m,
                _ => 0.25m,
            };
            return perImage * _imageCount;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            var genTag = string.IsNullOrEmpty(_name)
                ? $"{ModelId} edit  {_quality}  {_size}"
                : $"{ModelId} edit {_name}  {_quality}  {_size}";
            object? traceRequest = null;
            object? traceResponse = null;
            DateTime? callStartedAtUtc = null;
            int? traceStatusCode = null;
            bool traceRecorded = false;

            if (promptDetails != null)
            {
                promptDetails.RuntimeMeta["size"] = _size;
                promptDetails.RuntimeMeta["quality"] = _quality.ToString();
                promptDetails.RuntimeMeta["label"] = genTag;
            }

            try
            {
                _stats.GptImage2RequestCount++;

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(ModelId), "model");
                form.Add(new StringContent(promptDetails!.Prompt ?? ""), "prompt");
                form.Add(new StringContent(_size), "size");
                form.Add(new StringContent(_quality.ToString()), "quality");
                form.Add(new StringContent(_imageCount.ToString()), "n");

                var traceFiles = new List<object>();
                foreach (var path in _inputImagePaths)
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    var contentType = ContentTypeForFile(path);
                    var part = new ByteArrayContent(bytes);
                    part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    form.Add(part, "image[]", Path.GetFileName(path));
                    traceFiles.Add(new
                    {
                        fieldName = "image[]",
                        filename = Path.GetFileName(path),
                        path,
                        contentType,
                        byteLength = bytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    });
                }
                traceRequest = new
                {
                    model = ModelId,
                    prompt = promptDetails.Prompt ?? "",
                    size = _size,
                    quality = _quality.ToString(),
                    n = _imageCount,
                    files = traceFiles,
                };

                Logger.Log($"    [{genTag}] POST /v1/images/edits ({_inputImagePaths.Count} input image(s), n={_imageCount})");

                using var req = new HttpRequestMessage(HttpMethod.Post, EditsUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = form;

                callStartedAtUtc = DateTime.UtcNow;
                using var resp = await _http.SendAsync(req);
                traceStatusCode = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync();
                traceResponse = body;

                if (!resp.IsSuccessStatusCode)
                {
                    var providerError = new HttpRequestException(
                        $"OpenAI API error: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    GenerationTrace.RecordProviderCall(
                        "openai",
                        "http-multipart",
                        "POST",
                        EditsUrl,
                        callStartedAtUtc.Value,
                        traceRequest,
                        traceResponse,
                        traceStatusCode,
                        providerError,
                        new { model = ModelId, operation = "edit-image" });
                    traceRecorded = true;
                    _stats.GptImage2RefusedCount++;
                    string errorMessage;
                    try
                    {
                        using var errDoc = JsonDocument.Parse(body);
                        errorMessage = errDoc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
                    }
                    catch
                    {
                        errorMessage = body;
                    }
                    var cleaned = errorMessage.Split("If you believe").First().Trim();
                    Logger.Log($"    [{genTag}] HTTP {(int)resp.StatusCode} after {sw.ElapsedMilliseconds} ms: {cleaned}");
                    return Fail(cleaned, promptDetails, sw.ElapsedMilliseconds, genTag);
                }

                var images = new List<CreatedBase64Image>();
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            var b64 = item.TryGetProperty("b64_json", out var bEl) ? bEl.GetString() : null;
                            if (string.IsNullOrEmpty(b64)) continue;
                            var revised = item.TryGetProperty("revised_prompt", out var rEl) ? rEl.GetString() : null;
                            images.Add(new CreatedBase64Image
                            {
                                bytesBase64 = b64,
                                newPrompt = revised ?? "",
                            });
                        }
                    }
                }

                if (images.Count == 0)
                {
                    _stats.GptImage2RefusedCount++;
                    const string message = "edits response contained no images";
                    GenerationTrace.RecordProviderCall(
                        "openai",
                        "http-multipart",
                        "POST",
                        EditsUrl,
                        callStartedAtUtc.Value,
                        traceRequest,
                        traceResponse,
                        traceStatusCode,
                        new InvalidOperationException(message),
                        new { model = ModelId, operation = "edit-image" });
                    traceRecorded = true;
                    return Fail(message, promptDetails, sw.ElapsedMilliseconds, genTag);
                }

                GenerationTrace.RecordProviderCall(
                    "openai",
                    "http-multipart",
                    "POST",
                    EditsUrl,
                    callStartedAtUtc.Value,
                    traceRequest,
                    traceResponse,
                    traceStatusCode,
                    metadata: new { model = ModelId, operation = "edit-image" });
                traceRecorded = true;
                sw.Stop();
                Logger.Log($"    [{genTag}] OK in {sw.ElapsedMilliseconds} ms; {images.Count} image(s)");
                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = "image/png",
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = genTag,
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                if (callStartedAtUtc.HasValue && !traceRecorded)
                {
                    GenerationTrace.RecordProviderCall(
                        "openai",
                        "http-multipart",
                        "POST",
                        EditsUrl,
                        callStartedAtUtc.Value,
                        traceRequest,
                        traceResponse,
                        traceStatusCode,
                        ex,
                        new { model = ModelId, operation = "edit-image" });
                }
                Logger.Log($"    [{genTag}] EXCEPTION after {sw.ElapsedMilliseconds} ms: {ex.Message}");
                return Fail(ex.Message, promptDetails, sw.ElapsedMilliseconds, genTag);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private TaskProcessResult Fail(string message, PromptDetails? pd, long elapsedMs, string genTag)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = genTag,
                CreateTotalMs = elapsedMs,
            };
        }

        private static string ContentTypeForFile(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png",
            };
        }
    }
}
