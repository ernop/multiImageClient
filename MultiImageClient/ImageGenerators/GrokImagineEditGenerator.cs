#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using XAIGrokAPIClient;

namespace MultiImageClient
{
    public class GrokImagineEditGenerator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly XAIGrokClient _client;
        private readonly HttpClient _httpClient;
        private readonly MultiClientRunStats _stats;
        private readonly Settings _settings;
        private readonly string _inputImage;
        private readonly string _model;
        private readonly string _aspectRatio;
        private readonly string _responseFormat;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokImagineEdit;

        public GrokImagineEditGenerator(
            string apiKey,
            int maxConcurrency,
            MultiClientRunStats stats,
            Settings settings,
            string inputImage,
            bool pro = false,
            string aspectRatio = "",
            string responseFormat = "url")
        {
            _client = new XAIGrokClient(apiKey, baseUrl: settings.XAIBaseUrl);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _settings = settings;
            _inputImage = inputImage;
            _model = pro ? XAIGrokClient.ModelGrokImaginePro : XAIGrokClient.ModelGrokImagine;
            _aspectRatio = aspectRatio ?? string.Empty;
            _responseFormat = responseFormat;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var modelPart = _model == XAIGrokClient.ModelGrokImaginePro ? "grok-edit-pro" : "grok-edit";
            var arPart = string.IsNullOrWhiteSpace(_aspectRatio) ? "" : $"_{_aspectRatio.Replace(':', 'x')}";
            return $"{modelPart}{arPart}";
        }

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                "xAI Grok Imagine Edit",
                _model,
                _client.BaseUrl,
                InputImageLabel(),
            };
            if (!string.IsNullOrWhiteSpace(_aspectRatio)) parts.Add($"AR {_aspectRatio}");
            return parts;
        }

        public string GetGeneratorSpecPart()
        {
            var line = $"xAI Grok Imagine Edit  {_model}";
            if (!string.IsNullOrWhiteSpace(_aspectRatio)) line += $"  AR {_aspectRatio}";
            return line;
        }

        public decimal GetCost() => _model == XAIGrokClient.ModelGrokImaginePro ? 0.07m : 0.02m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                var imageInput = await BuildImageInputAsync(_inputImage);

                var req = new XAIGrokEditRequest
                {
                    Prompt = prompt,
                    Model = _model,
                    Image = imageInput,
                    AspectRatio = string.IsNullOrWhiteSpace(_aspectRatio) ? null : _aspectRatio,
                    ResponseFormat = string.IsNullOrWhiteSpace(_responseFormat) ? null : _responseFormat,
                    N = 1,
                };

                Logger.Log($"\t-> Grok Edit [{_model}] input={InputImageLabel()} AR={(_aspectRatio == "" ? "source" : _aspectRatio)}: {prompt}");
                var response = await _client.EditAsync(req);
                sw.Stop();
                LogModerationMetadata(response);

                if (TryBuildRefusalMessage(response, out var refusalMessage))
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail(refusalMessage, promptDetails, generator, sw.ElapsedMilliseconds);
                }

                if (response.Data == null || response.Data.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail("Grok edit returned empty data[] with no images.", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                var first = response.Data[0];
                _stats.GrokImageGenerationSuccessCount++;
                var usd = response.Usage?.CostUsd;
                var usdLabel = usd.HasValue ? $" cost=${usd:0.####}" : string.Empty;
                Logger.Log($"\t<- Grok Edit [{_model}] OK in {sw.ElapsedMilliseconds} ms{usdLabel}");

                GrokLedger.Append(_settings, new GrokLedgerEntry
                {
                    Kind = "image-edit",
                    Model = _model,
                    Prompt = prompt,
                    RemoteUrl = first.Url,
                    Source = "live",
                });

                if (!string.IsNullOrEmpty(first.Url))
                {
                    var contentType = await ResolveContentTypeAsync(first);
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Url = first.Url,
                        ContentType = contentType,
                        PromptDetails = promptDetails,
                        ImageGenerator = ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                if (!string.IsNullOrEmpty(first.Base64Json))
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Base64ImageDatas = new List<CreatedBase64Image>
                        {
                            new CreatedBase64Image
                            {
                                bytesBase64 = first.Base64Json,
                                newPrompt = prompt,
                            },
                        },
                        ContentType = first.MimeType ?? "image/png",
                        PromptDetails = promptDetails,
                        ImageGenerator = ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        CreateTotalMs = sw.ElapsedMilliseconds,
                    };
                }

                _stats.GrokImageGenerationErrorCount++;
                return Fail("Grok edit returned data[0] with neither url nor b64_json.", promptDetails, generator, sw.ElapsedMilliseconds);
            }
            catch (XAIGrokException ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                var detail = FormatGrokError(ex.ResponseBody);
                Logger.Log($"\t<- Grok Edit [{_model}] FAIL http={ex.StatusCode}: {Truncate(detail, 500)}");
                return Fail($"{ex.StatusCode}: {Truncate(detail, 300)}", promptDetails, generator, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                Logger.Log($"\t<- Grok Edit [{_model}] EXCEPTION: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<XAIGrokImageInput> BuildImageInputAsync(string inputImage)
        {
            if (Uri.TryCreate(inputImage, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return XAIGrokImageInput.FromUrl(inputImage);
            }

            var path = Settings.ExpandPath(inputImage);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Input image not found: {path}", path);
            }

            var bytes = await File.ReadAllBytesAsync(path);
            var mime = GuessImageMimeType(path);
            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            return XAIGrokImageInput.FromBase64(dataUri);
        }

        private async Task<string?> ResolveContentTypeAsync(XAIGrokImageData first)
        {
            if (!string.IsNullOrEmpty(first.MimeType)) return first.MimeType;
            if (string.IsNullOrEmpty(first.Url)) return "image/png";

            try
            {
                var head = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, first.Url));
                return head.Content.Headers.ContentType?.MediaType ?? "image/png";
            }
            catch (Exception ex)
            {
                Logger.Log($"\t(Grok Edit) HEAD on image url failed ({ex.Message}); defaulting content-type to image/png.");
                return "image/png";
            }
        }

        private string InputImageLabel()
        {
            if (Uri.TryCreate(_inputImage, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.Host;
            }

            return Path.GetFileName(Settings.ExpandPath(_inputImage));
        }

        private static string GuessImageMimeType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".png" => "image/png",
                _ => "image/png",
            };
        }

        private TaskProcessResult Fail(string message, PromptDetails pd, IImageGenerator generator, long elapsedMs)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
        }

        private static void LogModerationMetadata(XAIGrokImageResponse response)
        {
            var parts = new List<string>();
            if (response.RespectModeration.HasValue) parts.Add($"respect_moderation={response.RespectModeration.Value}");
            if (!string.IsNullOrWhiteSpace(response.BlockReason)) parts.Add($"block_reason={response.BlockReason}");
            if (!string.IsNullOrWhiteSpace(response.Model)) parts.Add($"model={response.Model}");
            if (response.Data != null)
            {
                for (var i = 0; i < response.Data.Count; i++)
                {
                    var item = response.Data[i];
                    if (item.RespectModeration.HasValue) parts.Add($"data[{i}].respect_moderation={item.RespectModeration.Value}");
                    if (!string.IsNullOrWhiteSpace(item.BlockReason)) parts.Add($"data[{i}].block_reason={item.BlockReason}");
                }
            }

            if (parts.Count > 0)
            {
                Logger.Log($"\t   Grok edit metadata: {string.Join("; ", parts)}");
            }
        }

        private static bool TryBuildRefusalMessage(XAIGrokImageResponse response, out string message)
        {
            var parts = new List<string>();
            if (response.RespectModeration == false) parts.Add("respect_moderation=false");
            if (!string.IsNullOrWhiteSpace(response.BlockReason)) parts.Add($"block_reason={response.BlockReason}");
            if (!string.IsNullOrWhiteSpace(response.Model)) parts.Add($"model={response.Model}");

            if (response.Data != null)
            {
                for (var i = 0; i < response.Data.Count; i++)
                {
                    var item = response.Data[i];
                    if (item.RespectModeration == false) parts.Add($"data[{i}].respect_moderation=false");
                    if (!string.IsNullOrWhiteSpace(item.BlockReason)) parts.Add($"data[{i}].block_reason={item.BlockReason}");
                }
            }

            if (parts.Count == 0)
            {
                message = string.Empty;
                return false;
            }

            message = "Grok edit refused or filtered the image edit. " + string.Join(" ", parts);
            return true;
        }

        private static string FormatGrokError(string responseBody)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                var parts = new List<string>();
                AddJsonProperty(root, parts, "code");
                AddJsonProperty(root, parts, "error");
                AddJsonProperty(root, parts, "block_reason");
                AddJsonProperty(root, parts, "respect_moderation");
                if (root.TryGetProperty("usage", out var usage)
                    && usage.TryGetProperty("cost_in_usd_ticks", out var costTicks))
                {
                    parts.Add($"usage.cost_in_usd_ticks={costTicks}");
                }
                if (parts.Count > 0)
                {
                    return string.Join("; ", parts) + $"; raw={responseBody}";
                }
            }
            catch
            {
                // Keep the original body if it was not JSON.
            }

            return responseBody;
        }

        private static void AddJsonProperty(System.Text.Json.JsonElement root, List<string> parts, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return;
            parts.Add($"{name}={value}");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
