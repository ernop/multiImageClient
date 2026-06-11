using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        public GoogleGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name = "",
            string aspectRatio = null, string imageSize = null)
        {
            if (apiType != ImageGeneratorApiType.GoogleNanoBanana && apiType != ImageGeneratorApiType.GoogleNanoBananaPro)
            {
                throw new ArgumentException(
                    $"GoogleGenerator only supports GoogleNanoBanana or GoogleNanoBananaPro, got {apiType}.",
                    nameof(apiType));
            }
            _apiKey = apiKey;
            _googleSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;
            _apiType = apiType;
            _aspectRatio = aspectRatio;
            _imageSize = imageSize;
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
                    ["responseModalities"] = new[] { "TEXT", "IMAGE" }
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

                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = promptDetails.Prompt }
                            }
                        }
                    },
                    generationConfig
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Set API key in header as required by Gemini API
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = content
                };
                request.Headers.Add("x-goog-api-key", _apiKey);


                var response = await _httpClient.SendAsync(request);
                var responseContent = await response.Content.ReadAsStringAsync();

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

                // Parse Gemini native image generation response
                var responseData = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(responseContent);

                if (responseData?.candidates?.Length > 0)
                {
                    var base64Images = new List<CreatedBase64Image>();
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
                                    Console.WriteLine($"Gemini text response: {part.text}");
                                    //throw new Exception("qq");
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
                }

                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = "No image data returned from Google Gemini API",
                    PromptDetails = promptDetails,
                    ImageGenerator = GetImageGeneratorType(),
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
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