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
        private const long IdeogramV4MaxInputImageBytes = 10_000_000;
        private static readonly HashSet<string> IdeogramV4Resolutions = new(StringComparer.Ordinal)
        {
            "2048x2048",
            "1440x2880", "2880x1440",
            "1664x2496", "2496x1664",
            "1792x2240", "2240x1792",
            "1440x2560", "2560x1440",
            "1600x2560", "2560x1600",
            "1728x2304", "2304x1728",
            "1296x3168", "3168x1296",
            "1152x2944", "2944x1152",
            "1248x3328", "3328x1248",
            "1280x3072", "3072x1280",
            "1024x3072", "3072x1024",
            "1024x1024",
            "896x1120", "1120x896",
            "864x1152", "1152x864",
            "832x1248", "1248x832",
            "800x1280", "1280x800",
            "720x1280", "1280x720",
            "720x1440", "1440x720",
            "512x1536", "1536x512",
        };

        public IdeogramClient(string apiKey)
            : this(apiKey, new HttpClient())
        {
        }

        public IdeogramClient(string apiKey, HttpMessageHandler handler)
            : this(apiKey, new HttpClient(handler))
        {
        }

        private IdeogramClient(string apiKey, HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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

        /// Ideogram 4.0 text generation. The current endpoint consumes
        /// multipart form data and does not expose seed or num_images.
        public async Task<IdeogramV4GenerateResponse> GenerateImageV4Async(IdeogramV4GenerateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.TextPrompt))
                throw new ArgumentException("TextPrompt is required for Ideogram 4.0 generation.", nameof(request));

            ValidateV4Options(request.Resolution, request.RenderingSpeed);

            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(request.TextPrompt), "text_prompt");
            AddStringPart(formData, "resolution", request.Resolution);
            AddEnumPart(formData, "rendering_speed", request.RenderingSpeed);
            AddBoolPart(formData, "enable_copyright_detection", request.EnableCopyrightDetection);

            var traceRequest = new Dictionary<string, object>
            {
                ["text_prompt"] = request.TextPrompt,
            };
            AddTraceString(traceRequest, "resolution", request.Resolution);
            AddTraceString(traceRequest, "rendering_speed", request.RenderingSpeed?.ToString());
            if (request.EnableCopyrightDetection.HasValue)
            {
                traceRequest["enable_copyright_detection"] = request.EnableCopyrightDetection.Value;
            }

            return await PostV4Async(
                "/v1/ideogram-v4/generate",
                formData,
                traceRequest,
                "generate-image");
        }

        /// Ideogram 4.0 image remix. The source image is the exact image whose
        /// metadata is recorded in the request trace; image bytes are never
        /// written to the trace.
        public async Task<IdeogramV4GenerateResponse> RemixImageV4Async(IdeogramV4RemixRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.TextPrompt))
                throw new ArgumentException("TextPrompt is required for Ideogram 4.0 Remix.", nameof(request));
            if (request.Image == null)
                throw new ArgumentException("Image is required for Ideogram 4.0 Remix.", nameof(request));

            ValidateV4Options(request.Resolution, request.RenderingSpeed);
            ValidateV4Image(request.Image);

            using var formData = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(request.Image.Content);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(request.Image.ContentType);
            formData.Add(imageContent, "image", request.Image.FileName);
            formData.Add(new StringContent(request.TextPrompt), "text_prompt");
            AddIntPart(formData, "image_weight", request.ImageWeight);
            AddStringPart(formData, "resolution", request.Resolution);
            AddEnumPart(formData, "rendering_speed", request.RenderingSpeed);
            AddBoolPart(formData, "enable_copyright_detection", request.EnableCopyrightDetection);

            var traceRequest = new Dictionary<string, object>
            {
                ["image"] = DescribeFiles(new[] { request.Image }).Single(),
                ["text_prompt"] = request.TextPrompt,
            };
            if (request.ImageWeight.HasValue)
            {
                traceRequest["image_weight"] = request.ImageWeight.Value;
            }
            AddTraceString(traceRequest, "resolution", request.Resolution);
            AddTraceString(traceRequest, "rendering_speed", request.RenderingSpeed?.ToString());
            if (request.EnableCopyrightDetection.HasValue)
            {
                traceRequest["enable_copyright_detection"] = request.EnableCopyrightDetection.Value;
            }

            return await PostV4Async(
                "/v1/ideogram-v4/remix",
                formData,
                traceRequest,
                "remix-image");
        }

        private async Task<IdeogramV4GenerateResponse> PostV4Async(
            string endpoint,
            HttpContent content,
            object traceRequest,
            string operation)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? responseContent = null;
            Exception? error = null;
            try
            {
                response = await _httpClient.PostAsync(endpoint, content);
                responseContent = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"API request failed with status code {response.StatusCode}. Response: {responseContent}");
                }

                var generateResponse = JsonConvert.DeserializeObject<IdeogramV4GenerateResponse>(responseContent);
                if (generateResponse == null)
                {
                    throw new InvalidDataException("Failed to deserialize Ideogram 4.0 response.");
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
                    request: traceRequest,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation, apiVersion = "v4" });
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

        private static void AddBoolPart(MultipartFormDataContent formData, string name, bool? value)
        {
            if (value.HasValue)
            {
                formData.Add(new StringContent(value.Value ? "true" : "false"), name);
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

        private static void ValidateV4Options(
            string? resolution,
            IdeogramRenderingSpeed? renderingSpeed)
        {
            if (!string.IsNullOrWhiteSpace(resolution)
                && !IdeogramV4Resolutions.Contains(resolution))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(resolution),
                    resolution,
                    "Resolution is not in Ideogram 4.0's current published resolution set.");
            }
            if (renderingSpeed == IdeogramRenderingSpeed.FLASH)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(renderingSpeed),
                    renderingSpeed,
                    "Ideogram 4.0 currently rejects rendering_speed=FLASH.");
            }
        }

        private static void ValidateV4Image(IdeogramFile image)
        {
            if (image.Content.Length == 0)
            {
                throw new ArgumentException("Ideogram 4.0 remix image is empty.", nameof(image));
            }
            if (image.Content.LongLength > IdeogramV4MaxInputImageBytes)
            {
                throw new ArgumentException(
                    $"Ideogram 4.0 remix image exceeds the {IdeogramV4MaxInputImageBytes:N0}-byte limit.",
                    nameof(image));
            }
            if (image.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                throw new ArgumentException(
                    $"Ideogram 4.0 remix only accepts PNG, JPEG, or WEBP; received '{image.ContentType}'.",
                    nameof(image));
            }
            var bytes = image.Content;
            var matchesDeclaredType = image.ContentType switch
            {
                "image/png" => bytes.Length >= 8
                    && bytes[0] == 0x89 && bytes[1] == 0x50
                    && bytes[2] == 0x4E && bytes[3] == 0x47,
                "image/jpeg" => bytes.Length >= 3
                    && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
                "image/webp" => bytes.Length >= 12
                    && bytes[0] == 'R' && bytes[1] == 'I'
                    && bytes[2] == 'F' && bytes[3] == 'F'
                    && bytes[8] == 'W' && bytes[9] == 'E'
                    && bytes[10] == 'B' && bytes[11] == 'P',
                _ => false,
            };
            if (!matchesDeclaredType)
            {
                throw new ArgumentException(
                    $"Ideogram 4.0 remix image bytes do not match declared type '{image.ContentType}'.",
                    nameof(image));
            }
        }
    }
}
