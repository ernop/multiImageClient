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
        private string _model;

        public GoogleGenerator(string apiKey, int maxConcurrency,
            MultiClientRunStats stats, string name = "", string model = "gemini-2.5-flash-image")
        {
            _apiKey = apiKey;
            _googleSemaphore = new SemaphoreSlim(maxConcurrency);
            _httpClient = new HttpClient();
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _stats = stats;
            _model = model;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var modelPart = _model.Replace("gemini-", "").Replace("-", "");
            var namePart = string.IsNullOrEmpty(_name) ? "" : $"-{_name}";
            return $"google-{modelPart}{namePart}";
        }

        public decimal GetCost()
        {
            // Based on Google's pricing: approximately $0.039 per image for Gemini 2.5 Flash Image
            return 0.039m;
        }

        public List<string> GetRightParts()
        {
            var modelPart = _model.Replace("gemini-", "").Replace("-", "");
            var namePart = string.IsNullOrEmpty(_name) ? "" : _name;
            return new List<string> { "google", modelPart, namePart };
        }

        public string GetGeneratorSpecPart()
        {
            if (string.IsNullOrEmpty(_name))
            {
                return $"google-{_model.Replace("gemini-", "").Replace("-", "")}";
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

                // Google Gemini API endpoint for image generation
                var apiUrl = $"https://generativelanguage.googleapis.com/v1/models/{_model}:generateContent?key={_apiKey}";

                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new
                                {
                                    text = promptDetails.Prompt
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 8192
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(apiUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorMessage = $"Google API error: {response.StatusCode} - {responseContent}";
                    return new TaskProcessResult 
                    { 
                        IsSuccess = false, 
                        ErrorMessage = errorMessage, 
                        PromptDetails = promptDetails, 
                        ImageGenerator = ImageGeneratorApiType.GoogleGemini, 
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                    };
                }

                var responseData = JsonSerializer.Deserialize<GoogleGeminiResponse>(responseContent);
                
                if (responseData?.candidates?.Length > 0 && 
                    responseData.candidates[0]?.content?.parts?.Length > 0)
                {
                    var part = responseData.candidates[0].content.parts[0];
                    
                    if (!string.IsNullOrEmpty(part.inline_data?.data))
                    {
                        // Image returned as base64 data
                        var base64Images = new List<string> { part.inline_data.data };
                        return new TaskProcessResult 
                        { 
                            IsSuccess = true, 
                            Base64ImageDatas = base64Images,
                            ContentType = part.inline_data.mime_type ?? "image/png",
                            ErrorMessage = "", 
                            PromptDetails = promptDetails, 
                            ImageGenerator = ImageGeneratorApiType.GoogleGemini, 
                            ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                        };
                    }
                    else if (!string.IsNullOrEmpty(part.text))
                    {
                        // Sometimes the API returns a URL or other text response
                        var errorMessage = $"Google Gemini returned text instead of image: {part.text}";
                        return new TaskProcessResult 
                        { 
                            IsSuccess = false, 
                            ErrorMessage = errorMessage, 
                            PromptDetails = promptDetails, 
                            ImageGenerator = ImageGeneratorApiType.GoogleGemini, 
                            ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                        };
                    }
                }

                return new TaskProcessResult 
                { 
                    IsSuccess = false, 
                    ErrorMessage = "No image data returned from Google Gemini API", 
                    PromptDetails = promptDetails, 
                    ImageGenerator = ImageGeneratorApiType.GoogleGemini, 
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                };
            }
            catch (Exception ex)
            {
                var errorMessage = $"Google Generator error: {ex.Message}";
                return new TaskProcessResult 
                { 
                    IsSuccess = false, 
                    ErrorMessage = errorMessage, 
                    PromptDetails = promptDetails, 
                    ImageGenerator = ImageGeneratorApiType.GoogleGemini, 
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart() 
                };
            }
            finally
            {
                _googleSemaphore.Release();
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _googleSemaphore?.Dispose();
        }
    }

    // Response models for Google Gemini API
    public class GoogleGeminiResponse
    {
        public GoogleGeminiCandidate[] candidates { get; set; }
    }

    public class GoogleGeminiCandidate
    {
        public GoogleGeminiContent content { get; set; }
    }

    public class GoogleGeminiContent
    {
        public GoogleGeminiPart[] parts { get; set; }
    }

    public class GoogleGeminiPart
    {
        public string text { get; set; }
        public GoogleGeminiInlineData inline_data { get; set; }
    }

    public class GoogleGeminiInlineData
    {
        public string mime_type { get; set; }
        public string data { get; set; }
    }
}