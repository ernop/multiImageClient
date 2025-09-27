using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;

namespace IdeogramAPIClient
{
    public class IdeogramClient
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://api.ideogram.ai";

        public IdeogramClient(string apiKey)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Api-Key", apiKey);
            _httpClient.BaseAddress = new Uri(BaseUrl);
        }

        public async Task<GenerateResponse> GenerateImageAsync(IdeogramGenerateRequest request)
        {
            var jsonRequest = JsonConvert.SerializeObject(new { image_request = request }, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Converters = new List<JsonConverter> { new StringEnumConverter(camelCaseText: false) }
            });

            var httpContent = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/generate", httpContent);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var generateResponse = JsonConvert.DeserializeObject<GenerateResponse>(content);
            return generateResponse;
        }        

        public async Task<IdeogramV3GenerateResponse> GenerateImageV3Async(IdeogramV3GenerateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for Ideogram v3 generation.", nameof(request));

            using (var formData = new MultipartFormDataContent())
            {
                formData.Add(new StringContent(request.Prompt), "prompt");

                AddStringPart(formData, "aspect_ratio", request.AspectRatio.ToString());
                AddStringPart(formData, "rendering_speed", request.RenderingSpeed.ToString());
                AddStringPart(formData, "magic_prompt", request.MagicPrompt.ToString());
                AddStringPart(formData, "style_type", request.StyleType.ToString());
                //AddStringPart(formData, "style_preset", request.StylePreset);
                //AddStringPart(formData, "negative_prompt", request.NegativePrompt);
                //AddIntPart(formData, "num_images", request.NumImages);
                //AddIntPart(formData, "seed", request.Seed);

                //if (request.StyleCodes != null)
                //{
                //    foreach (var styleCode in request.StyleCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
                //    {
                //        formData.Add(new StringContent(styleCode), "style_codes");
                //    }
                //}

                //AddFileParts(formData, "style_reference_images", request.StyleReferenceImages);
                //AddFileParts(formData, "character_reference_images", request.CharacterReferenceImages);
                //AddFileParts(formData, "character_reference_images_mask", request.CharacterReferenceImageMasks);

                var response = await _httpClient.PostAsync("/v1/ideogram-v3/generate", formData);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var generateResponse = JsonConvert.DeserializeObject<IdeogramV3GenerateResponse>(content);
                if (generateResponse == null)
                {
                    throw new InvalidDataException("Failed to deserialize Ideogram v3 response.");
                }

                return generateResponse;
            }
        }

        public async Task<IdeogramDescribeResponse> DescribeImageAsync(IdeogramDescribeRequest request)
        {
            using (var formData = new MultipartFormDataContent())
            {
                var imageContent = new ByteArrayContent(request.ImageFile);
                formData.Add(imageContent, "image_file", "image.png"); // Assuming image.png as a default filename

                if (!string.IsNullOrEmpty(request.DescribeModelVersion))
                {
                    formData.Add(new StringContent(request.DescribeModelVersion), "describe_model_version");
                }

                var response = await _httpClient.PostAsync("/describe", formData);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {errorContent}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var describeResponse = JsonConvert.DeserializeObject<IdeogramDescribeResponse>(content);
                return describeResponse;
            }
        }

        private static void AddStringPart(MultipartFormDataContent formData, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                formData.Add(new StringContent(value), name);
            }
        }

        private static void AddIntPart(MultipartFormDataContent formData, string name, int? value)
        {
            if (value.HasValue)
            {
                formData.Add(new StringContent(value.Value.ToString()), name);
            }
        }

        private static void AddEnumPart<T>(MultipartFormDataContent formData, string name, T? value) where T : struct, Enum
        {
            if (value.HasValue)
            {
                formData.Add(new StringContent(value.Value.ToString()), name);
            }
        }

        private static void AddFileParts(MultipartFormDataContent formData, string fieldName, IEnumerable<IdeogramFile> files)
        {
            if (files == null)
                return;

            foreach (var file in files)
            {
                if (file?.Content == null || file.Content.Length == 0)
                    continue;

                var fileContent = new ByteArrayContent(file.Content);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                formData.Add(fileContent, fieldName, file.FileName);
            }
        }
    }
}
