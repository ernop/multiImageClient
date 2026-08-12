using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GoogleGenerator : IImageGenerator
    {
        private SemaphoreSlim _googleSemaphore;
        private HttpClient _httpClient;
        private string _apiKey;
        private MultiClientRunStats _stats;
        private string _name;
        private ImageGeneratorApiType _apiType;
        private string _aspectRatio;
        private string _imageSize;
        private string _inputImagePath;

        public ImageGeneratorApiType ApiType => _apiType;

        /// Gemini image-model slug per tier. June 2026 status: all dedicated
        /// Imagen endpoints shut down 2026-06-24..30; Gemini Image ("Nano
        /// Banana") models are Google's official replacement.
        ///   GoogleNanoBanana    -> gemini-3.1-flash-image ("Nano Banana 2",
        ///                          fast/cheap tier, successor to 2.5-flash-image)
        ///   GoogleNanoBananaPro -> gemini-3-pro-image ("Nano Banana Pro",
        ///                          reasoning/"thinking" tier, hi-fi text, up to 4K)
        private static string ModelFor(ImageGeneratorApiType apiType) => apiType switch
        {
            ImageGeneratorApiType.GoogleNanoBananaPro => "gemini-3-pro-image",
            _ => "gemini-3.1-flash-image",
        };

        /// aspectRatio: "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4",
        ///   "9:16", "16:9", "21:9" — or null to let the model decide (1:1 default).
        /// imageSize: "512" (3.1-flash only), "1K", "2K", "4K" (K must be
        ///   uppercase) — or null for the API default (1K). Per the token
        ///   table, 2K costs the same tokens as 1K; only 4K is pricier.
        /// inputImagePath: when set, the image is sent as a visual reference/guide
        ///   part alongside the prompt (Gemini is natively multimodal), and the
        ///   instruction is reworded so the model treats it as guidance, not a
        ///   literal edit target.
        public GoogleGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name = "",
            string aspectRatio = null, string imageSize = null, string inputImagePath = null)
        {
            if (apiType != ImageGeneratorApiType.GoogleNanoBanana && apiType != ImageGeneratorApiType.GoogleNanoBananaPro)
            {
                throw new ArgumentException(
                    $"GoogleGenerator only supports GoogleNanoBanana or GoogleNanoBananaPro, got {apiType}.",
                    nameof(apiType));
            }
            _apiKey = apiKey;
            _googleSemaphore = new SemaphoreSlim(maxConcurrency);
            // Default HttpClient.Timeout is 100s. Nano Banana Pro at 4K often
            // needs longer than that under load; a short timeout aborts a still-
            // running provider call and wastes the work. Ten minutes matches the
            // B2 client's ceiling and still fails closed if the socket dies.
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;
            _apiType = apiType;
            _aspectRatio = aspectRatio;
            _imageSize = imageSize;
            _inputImagePath = inputImagePath;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            return $"{_apiType}";
        }

        public decimal GetCost()
        {
            // Gemini image models use token-based pricing ($30/1M output tokens).
            // Per the docs' token table, 1K and 2K outputs both cost ~1120
            // tokens; 4K costs ~2000. Pro additionally burns "thinking" tokens;
            // ~$0.13/image is a reasonable 1K/2K estimate until we wire usage
            // parsing.
            var sizeMultiplier = _imageSize == "4K" ? (2000m / 1120m) : 1m;
            if (_apiType == ImageGeneratorApiType.GoogleNanoBanana)
            {
                return (30m / 1000000m) * 1120m * sizeMultiplier;
            }
            else if (_apiType == ImageGeneratorApiType.GoogleNanoBananaPro)
            {
                return 0.13m * sizeMultiplier;
            }
            else
            {
                throw new Exception("E");
            }
        }

        public List<string> GetRightParts()
        {
            var parts = new List<string> { _apiType.ToString() };
            if (!string.IsNullOrEmpty(_aspectRatio))
            {
                parts.Add(_aspectRatio);
            }
            if (!string.IsNullOrEmpty(_imageSize))
            {
                parts.Add(_imageSize);
            }
            return parts;
        }

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"google-{_apiType.ToString()}";
            }
            else
            {
                return _name;
            }
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _googleSemaphore.WaitAsync();
            try
            {
                _stats.GoogleRequestCount++;

                // Google Gemini API endpoint for native image generation (Nano Banana).
                // Model slug per tier — see ModelFor(). The old
                // gemini-2.5-flash-image-preview slug was retired with the
                // June 2026 Imagen/preview shutdown wave.
                var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelFor(_apiType)}:generateContent";

                var generationConfig = new Dictionary<string, object>
                {
                    ["responseModalities"] = new[] { "IMAGE" }
                };
                var imageConfig = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(_aspectRatio))
                {
                    imageConfig["aspectRatio"] = _aspectRatio;
                }
                if (!string.IsNullOrEmpty(_imageSize))
                {
                    imageConfig["imageSize"] = _imageSize;
                }
                if (imageConfig.Count > 0)
                {
                    generationConfig["imageConfig"] = imageConfig;
                }

                var hasReference = !string.IsNullOrEmpty(_inputImagePath);
                var instruction = hasReference
                    ? "Using the attached image as a visual reference and guide (style, subject, and composition), "
                        + "create a new image that fulfills the following request. Return image output, not a prose explanation: "
                    : "Create an image that visually fulfills the following request. "
                        + "Return image output, not a prose explanation: ";

                var parts = new List<object>
                {
                    new { text = instruction + promptDetails.Prompt }
                };
                if (hasReference)
                {
                    var refBytes = await File.ReadAllBytesAsync(_inputImagePath);
                    parts.Add(new
                    {
                        inlineData = new
                        {
                            mimeType = MimeFromPath(_inputImagePath),
                            data = Convert.ToBase64String(refBytes)
                        }
                    });
                }

                var requestBody = new
                {
                    contents = new[]
                    {
                        new { parts = parts.ToArray() }
                    },
                    generationConfig
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var traceRequest = SanitizeGeminiJson(json);

                // Set API key in header as required by Gemini API
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = content
                };
                request.Headers.Add("x-goog-api-key", _apiKey);

                var startedAtUtc = DateTime.UtcNow;
                HttpResponseMessage response = null;
                byte[] responseBytes = null;
                string responseContent = null;
                try
                {
                    // Headers-read so the body can be buffered once as UTF-8 bytes.
                    // Default HttpClient.Timeout is only 100s; Pro@4K often needs
                    // longer (ctor sets 10 minutes). Still fail closed on stall.
                    response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead);
                    responseBytes = await response.Content.ReadAsByteArrayAsync();
                }
                catch (Exception ex)
                {
                    GenerationTrace.RecordProviderCall(
                        "google-gemini",
                        "http",
                        "POST",
                        apiUrl,
                        startedAtUtc,
                        request: traceRequest,
                        response: responseBytes == null
                            ? null
                            : SanitizeGeminiJson(Encoding.UTF8.GetString(responseBytes)),
                        statusCode: response == null ? null : (int)response.StatusCode,
                        error: ex,
                        metadata: new
                        {
                            model = ModelFor(_apiType),
                            apiType = _apiType.ToString(),
                            hasReference,
                        });
                    response?.Dispose();
                    throw;
                }

                using (response)
                {
                    try
                    {
                        responseContent = Encoding.UTF8.GetString(responseBytes ?? Array.Empty<byte>());
                    }
                    catch (DecoderFallbackException)
                    {
                        responseContent = "";
                    }
                    responseBytes = null;

                    var providerError = response.IsSuccessStatusCode
                        ? null
                        : new HttpRequestException(
                            $"Google Gemini API error: {response.StatusCode} - {responseContent}");
                    GenerationTrace.RecordProviderCall(
                        "google-gemini",
                        "http",
                        "POST",
                        apiUrl,
                        startedAtUtc,
                        request: traceRequest,
                        response: SanitizeGeminiJson(responseContent),
                        statusCode: (int)response.StatusCode,
                        error: providerError,
                        metadata: new
                        {
                            model = ModelFor(_apiType),
                            apiType = _apiType.ToString(),
                            hasReference,
                        });

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorMessage = $"Google Gemini API error: {response.StatusCode} - {responseContent}";
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = errorMessage,
                            PromptDetails = promptDetails,
                            ImageGenerator = GetImageGeneratorType(),
                            ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                        };
                    }

                    // Parse Gemini native image generation response. Keep only a
                    // short diagnostic prefix of the raw JSON for the no-image
                    // failure path; drop the multi-MB string afterward.
                    var responseData = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(responseContent);
                    var diagnosticPrefix = responseContent.Length > 700
                        ? responseContent[..700] + "..."
                        : responseContent;
                    responseContent = null;

                    if (responseData?.candidates?.Length > 0)
                    {
                        var base64Images = new List<CreatedBase64Image>();
                        var textResponses = new List<string>();
                        // Gemini image models typically return image/jpeg; trust the
                        // declared mime type so downstream conversion-to-png triggers.
                        string contentType = null;

                        foreach (var candidate in responseData.candidates)
                        {
                            if (candidate?.content?.parts != null)
                            {
                                foreach (var part in candidate.content.parts)
                                {
                                    // Check for image data in inline_data
                                    if (part.inlineData != null && !string.IsNullOrEmpty(part.inlineData.data))
                                    {
                                        var bd = new CreatedBase64Image
                                        {
                                            bytesBase64 = part.inlineData.data,
                                            newPrompt = promptDetails.Prompt
                                        };
                                        base64Images.Add(bd);
                                        contentType ??= part.inlineData.mimeType;
                                    }
                                    // Log any text responses for debugging
                                    else if (!string.IsNullOrEmpty(part.text))
                                    {
                                        textResponses.Add(part.text);
                                    }
                                }
                            }
                        }

                        if (base64Images.Count > 0)
                        {
                            return new TaskProcessResult
                            {
                                IsSuccess = true,
                                Base64ImageDatas = base64Images,
                                ContentType = contentType ?? "image/png",
                                ErrorMessage = "",
                                PromptDetails = promptDetails,
                                ImageGenerator = GetImageGeneratorType(),
                                ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                            };
                        }

                        if (textResponses.Count > 0)
                        {
                            var returnedText = string.Join(" ", textResponses);
                            if (returnedText.Length > 500)
                            {
                                returnedText = returnedText[..500] + "...";
                            }
                            Logger.Log($"Gemini image model {ModelFor(_apiType)} returned text instead of an image: {returnedText}");
                            return new TaskProcessResult
                            {
                                IsSuccess = false,
                                ErrorMessage = $"Google {ModelFor(_apiType)} returned text instead of image output: {returnedText}",
                                PromptDetails = promptDetails,
                                ImageGenerator = GetImageGeneratorType(),
                                ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                            };
                        }
                    }

                    // A blocked request comes back 200 with promptFeedback.blockReason
                    // set and no candidates — translate that into something readable
                    // instead of dumping the raw JSON.
                    var blockReason = responseData?.promptFeedback?.blockReason;
                    var finishReason = responseData?.candidates?
                        .Select(c => c?.finishReason)
                        .FirstOrDefault(r => !string.IsNullOrEmpty(r) && r != "STOP");
                    if (!string.IsNullOrEmpty(blockReason) || !string.IsNullOrEmpty(finishReason))
                    {
                        var reason = !string.IsNullOrEmpty(blockReason)
                            ? $"blockReason={blockReason}"
                            : $"finishReason={finishReason}";
                        var hint = blockReason == "OTHER"
                            ? " \"OTHER\" usually means the input image tripped a filter (recognizable real people/celebrities are the most common trigger, also children or watermarked content); rewording the prompt or using a different input image usually clears it."
                            : " The prompt and/or input image was refused by Gemini's safety filters; reword or swap the image and retry.";
                        var blockedMessage = $"Google {ModelFor(_apiType)} refused this request before generating anything ({reason}).{hint}";
                        Logger.Log(blockedMessage);
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = blockedMessage,
                            PromptDetails = promptDetails,
                            ImageGenerator = GetImageGeneratorType(),
                            ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                        };
                    }

                    Logger.Log($"Gemini image model {ModelFor(_apiType)} returned no image data. Response: {diagnosticPrefix}");
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Google {ModelFor(_apiType)} returned no image data. Response: {diagnosticPrefix}",
                        PromptDetails = promptDetails,
                        ImageGenerator = GetImageGeneratorType(),
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"Google Gemini image generator error: {ex.Message}";
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = errorMessage,
                    PromptDetails = promptDetails,
                    ImageGenerator = GetImageGeneratorType(),
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            finally
            {
                _googleSemaphore.Release();
            }
        }

        private static JsonNode SanitizeGeminiJson(string json)
        {
            JsonNode node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return JsonValue.Create(json);
            }

            ReplaceBinaryValues(node);
            return node;
        }

        private static void ReplaceBinaryValues(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToList())
                {
                    if (property.Value is JsonValue value
                        && IsBinaryField(property.Key)
                        && value.TryGetValue<string>(out var encoded))
                    {
                        obj[property.Key] = new JsonObject
                        {
                            ["_redacted"] = "binary",
                            ["encoding"] = "base64",
                            ["encodedLength"] = encoded.Length,
                            ["approximateByteLength"] = encoded.Length * 3L / 4L,
                        };
                    }
                    else if (property.Value != null)
                    {
                        ReplaceBinaryValues(property.Value);
                    }
                }
                return;
            }

            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item != null)
                    {
                        ReplaceBinaryValues(item);
                    }
                }
            }
        }

        private static bool IsBinaryField(string name)
            => name.Equals("data", StringComparison.OrdinalIgnoreCase)
                || name.Contains("base64", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bytesBase64Encoded", StringComparison.OrdinalIgnoreCase);

        private static string MimeFromPath(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/png",
            };
        }

        private ImageGeneratorApiType GetImageGeneratorType()
        {
            return _apiType;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _googleSemaphore?.Dispose();
        }
    }

}