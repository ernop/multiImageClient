using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using MultiImageClient;

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
                Converters = new List<JsonConverter> { new StringEnumConverter() }
            });

            var httpContent = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");
            const string endpoint = "/generate";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? responseContent = null;
            Exception? error = null;
            try
            {
                response = await _httpClient.PostAsync(endpoint, httpContent);
                responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}");
                }

                var generateResponse = JsonConvert.DeserializeObject<GenerateResponse>(responseContent);
                if (generateResponse == null)
                {
                    throw new InvalidDataException("Failed to deserialize Ideogram generate response.");
                }
                return generateResponse;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "ideogram",
                    "http",
                    "POST",
                    BaseUrl + endpoint,
                    startedAtUtc,
                    request: jsonRequest,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "generate-image", apiVersion = "legacy" });
            }
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
                AddIntPart(formData, "num_images", request.NumImages);
                AddIntPart(formData, "seed", request.Seed);

                //if (request.StyleCodes != null)
                //{
                //    foreach (var styleCode in request.StyleCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
                //    {
                //        formData.Add(new StringContent(styleCode), "style_codes");
                //    }
                //}

                AddFileParts(formData, "style_reference_images", request.StyleReferenceImages);
                //AddFileParts(formData, "character_reference_images", request.CharacterReferenceImages);
                //AddFileParts(formData, "character_reference_images_mask", request.CharacterReferenceImageMasks);
                const string endpoint = "/v1/ideogram-v3/generate";
                var traceRequest = new Dictionary<string, object>
                {
                    ["prompt"] = request.Prompt,
                };
                AddTraceString(traceRequest, "aspect_ratio", request.AspectRatio.ToString());
                AddTraceString(traceRequest, "rendering_speed", request.RenderingSpeed.ToString());
                AddTraceString(traceRequest, "magic_prompt", request.MagicPrompt.ToString());
                AddTraceString(traceRequest, "style_type", request.StyleType.ToString());
                if (request.NumImages.HasValue)
                {
                    traceRequest["num_images"] = request.NumImages.Value;
                }
                if (request.Seed.HasValue)
                {
                    traceRequest["seed"] = request.Seed.Value;
                }
                var styleReferenceImages = DescribeFiles(request.StyleReferenceImages);
                if (styleReferenceImages.Length > 0)
                {
                    traceRequest["style_reference_images"] = styleReferenceImages;
                }
                var startedAtUtc = DateTime.UtcNow;
                HttpResponseMessage? response = null;
                string? responseContent = null;
                Exception? error = null;
                try
                {
                    response = await _httpClient.PostAsync(endpoint, formData);
                    responseContent = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}");
                    }

                    var generateResponse = JsonConvert.DeserializeObject<IdeogramV3GenerateResponse>(responseContent);
                    if (generateResponse == null)
                    {
                        throw new InvalidDataException("Failed to deserialize Ideogram v3 response.");
                    }

                    return generateResponse;
                }
                catch (Exception ex)
                {
                    error = ex;
                    throw;
                }
                finally
                {
                    GenerationTrace.RecordProviderCall(
                        "ideogram",
                        "http-multipart",
                        "POST",
                        BaseUrl + endpoint,
                        startedAtUtc,
                        request: traceRequest,
                        response: responseContent,
                        statusCode: response == null ? null : (int)response.StatusCode,
                        error: error,
                        metadata: new { operation = "generate-image", apiVersion = "v3" });
                }
            }
        }

        /// Ideogram 4.0 (2026-06-03): plain JSON POST, unlike v3's multipart
        /// form. 2K-native output, rendering_speed FLASH|TURBO|DEFAULT|QUALITY.
        public async Task<IdeogramV4GenerateResponse> GenerateImageV4Async(IdeogramV4GenerateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.TextPrompt))
                throw new ArgumentException("TextPrompt is required for Ideogram v4 generation.", nameof(request));

            var jsonRequest = JsonConvert.SerializeObject(request, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            });
            var httpContent = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");
            const string endpoint = "/v1/ideogram-v4/generate";
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? responseContent = null;
            Exception? error = null;
            try
            {
                response = await _httpClient.PostAsync(endpoint, httpContent);
                responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}");
                }

                var generateResponse = JsonConvert.DeserializeObject<IdeogramV4GenerateResponse>(responseContent);
                if (generateResponse == null)
                {
                    throw new InvalidDataException("Failed to deserialize Ideogram v4 response.");
                }

                return generateResponse;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "ideogram",
                    "http",
                    "POST",
                    BaseUrl + endpoint,
                    startedAtUtc,
                    request: jsonRequest,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = "generate-image", apiVersion = "v4" });
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

                const string endpoint = "/describe";
                var traceRequest = new Dictionary<string, object>
                {
                    ["image_file"] = new
                    {
                        name = "image.png",
                        size = request.ImageFile?.LongLength ?? 0,
                        content_type = imageContent.Headers.ContentType?.ToString(),
                        source = "memory",
                    },
                };
                if (!string.IsNullOrEmpty(request.DescribeModelVersion))
                {
                    traceRequest["describe_model_version"] = request.DescribeModelVersion;
                }
                var startedAtUtc = DateTime.UtcNow;
                HttpResponseMessage? response = null;
                string? responseContent = null;
                Exception? error = null;
                try
                {
                    response = await _httpClient.PostAsync(endpoint, formData);
                    responseContent = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}");
                    }

                    var describeResponse = JsonConvert.DeserializeObject<IdeogramDescribeResponse>(responseContent);
                    if (describeResponse == null)
                    {
                        throw new InvalidDataException("Failed to deserialize Ideogram describe response.");
                    }
                    return describeResponse;
                }
                catch (Exception ex)
                {
                    error = ex;
                    throw;
                }
                finally
                {
                    GenerationTrace.RecordProviderCall(
                        "ideogram",
                        "http-multipart",
                        "POST",
                        BaseUrl + endpoint,
                        startedAtUtc,
                        request: traceRequest,
                        response: responseContent,
                        statusCode: response == null ? null : (int)response.StatusCode,
                        error: error,
                        metadata: new { operation = "describe-image" });
                }
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

        private static void AddTraceString(Dictionary<string, object> request, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                request[name] = value;
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

        private static object[] DescribeFiles(IEnumerable<IdeogramFile> files)
        {
            return files?
                .Where(file => file?.Content != null && file.Content.Length > 0)
                .Select(file => (object)new
                {
                    name = file.FileName,
                    size = file.Content.LongLength,
                    content_type = file.ContentType,
                    source = "memory",
                })
                .ToArray()
                ?? Array.Empty<object>();
        }
    }
}
