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
        private GoogleImageSize _imageSize;
        private string _aspectRatio;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GoogleNanoBanana;

        public GoogleGenerator(ImageGeneratorApiType apiType, string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name = "", 
            GoogleImageSize imageSize = GoogleImageSize.Size1K,
            string aspectRatio = "1:1")
        {
            _apiKey = apiKey;
            _googleSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;
            _apiType = apiType;
            _imageSize = imageSize;
            _aspectRatio = aspectRatio;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : $"-{_name}";
            return $"{_apiType}{namePart}_{_imageSize.ToApiString()}_{_aspectRatio.Replace(":", "x")}";
        }

        public decimal GetCost()
        {
            // Gemini 2.5 Flash Image uses token-based pricing
            // $30 per 1 million tokens for image output (1290 tokens per image up to 1024x1024px)
            // Higher resolutions consume proportionally more tokens
            if (_apiType == ImageGeneratorApiType.GoogleNanoBanana)
            {
                var baseTokens = 1290m;
                var multiplier = _imageSize switch
                {
                    GoogleImageSize.Size1K => 1.0m,
                    GoogleImageSize.Size2K => 4.0m,   // 2x2 = 4x pixels
                    GoogleImageSize.Size4K => 16.0m,  // 4x4 = 16x pixels
                    _ => 1.0m
                };
                return (30m / 1000000m) * baseTokens * multiplier;
            }
            else if (_apiType == ImageGeneratorApiType.GoogleImagen4)
            {
                return 0.04m;
            }
            else
            {
                throw new Exception("E");
            }
        }

        public List<string> GetRightParts()
        {
            var namePart = string.IsNullOrEmpty(_name) ? "" : _name;
            return new List<string> { _apiType.ToString(), namePart, _imageSize.ToApiString(), _aspectRatio };
        }

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"google-{_apiType.ToString()}\n{_imageSize.ToApiString()} {_aspectRatio}";
            }
            else
            {
                return $"{_name}\n{_imageSize.ToApiString()} {_aspectRatio}";
            }
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _googleSemaphore.WaitAsync();
            try
            {
                _stats.GoogleRequestCount++;

                // Google Gemini API endpoint for native image generation (Nano Banana)
                var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-image-preview:generateContent";

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
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = new
                        {
                            imageSize = _imageSize.ToApiString(),
                            aspectRatio = _aspectRatio
                        }
                    }
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
                            ContentType = "image/png",
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
                var errorMessage = $"Google Imagen Generator error: {ex.Message}";
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