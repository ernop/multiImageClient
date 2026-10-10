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

        private const string IdeogramV45GeneratePath = "/v2/image/generate/ideogram-4-5";
        private const string IdeogramV45PreciseEditPath = "/v2/image/precise-edit/ideogram-4-5";
        public const int IdeogramV45MaxPromptChars = 10_000;
        public const int IdeogramV45MaxNumImages = 8;
        public const int IdeogramV45MaxReferenceImages = 4;
        public const long IdeogramV45MaxImageBytes = 50_000_000;

        // Text-only Generate accepts only these exact presets; 4.0's 512x1536
        // pair is rejected by 4.5. Source-image sizes follow other rules.
        private static readonly HashSet<string> IdeogramV45TextSizes = new(StringComparer.Ordinal)
        {
            "2048x2048",
            "1792x2240", "2240x1792",
            "1728x2304", "2304x1728",
            "1664x2496", "2496x1664",
            "1440x2560", "2560x1440",
            "1600x2560", "2560x1600",
            "1440x2880", "2880x1440",
            "1280x3072", "3072x1280",
            "1248x3328", "3328x1248",
            "1152x2944", "2944x1152",
            "1296x3168", "3168x1296",
            "1024x3072", "3072x1024",
            "1024x1024",
            "896x1120", "1120x896",
            "864x1152", "1152x864",
            "832x1248", "1248x832",
            "800x1280", "1280x800",
            "720x1280", "1280x720",
            "720x1440", "1440x720",
        };

        public static bool IsIdeogramV45TextSize(string size) => IdeogramV45TextSizes.Contains(size);

        public IdeogramClient(string apiKey)
            : this(apiKey, new HttpClient())
        {
        }

        public IdeogramClient(string apiKey, TimeSpan timeout)
            : this(apiKey, new HttpClient { Timeout = timeout })
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

        /// Ideogram 4.5 text-to-image Generate. The reply must contain exactly
        /// one data entry per requested image.
        public async Task<IdeogramV45Response> GenerateImageV45Async(IdeogramV45GenerateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateV45Common(request.Prompt, request.NumImages, request.Seed);
            if (request.Size != null && !IdeogramV45TextSizes.Contains(request.Size))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Size,
                    "Ideogram 4.5 text-to-image size must be one of the published 1K/2K presets, or omitted for auto.");
            }
            if (request.Quality == IdeogramV45Quality.very_low)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Quality,
                    "Ideogram 4.5 accepts quality=very_low only with source images.");
            }

            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(request.Prompt), "prompt");
            AddStringPart(formData, "size", request.Size);
            AddEnumPart(formData, "quality", request.Quality);
            AddIntPart(formData, "num_images", request.NumImages);
            AddIntPart(formData, "seed", request.Seed);

            var traceRequest = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt,
            };
            AddTraceV45Options(traceRequest, request.Size, request.Quality, request.NumImages, request.Seed);

            return await PostV45Async(
                IdeogramV45GeneratePath,
                formData,
                traceRequest,
                "generate-image",
                request.NumImages ?? 1,
                requireGenerationId: true);
        }

        /// Ideogram 4.5 Precise Edit. The edited image and each reference are
        /// sent as uploaded files; the trace records their metadata only.
        public async Task<IdeogramV45Response> PreciseEditImageV45Async(IdeogramV45PreciseEditRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.Image == null)
                throw new ArgumentException("Image is required for Ideogram 4.5 Precise Edit.", nameof(request));

            var referenceImages = request.ReferenceImages ?? Array.Empty<IdeogramFile>();
            ValidateV45Common(request.Prompt, request.NumImages, request.Seed);
            if (referenceImages.Count > IdeogramV45MaxReferenceImages)
            {
                throw new ArgumentException(
                    $"Ideogram 4.5 Precise Edit accepts at most {IdeogramV45MaxReferenceImages} reference images; received {referenceImages.Count}.",
                    nameof(request));
            }
            ValidateV45Image(request.Image, "edit image");
            for (var i = 0; i < referenceImages.Count; i++)
            {
                ValidateV45Image(referenceImages[i], $"reference image {i + 1}");
            }

            using var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(request.Prompt), "prompt");
            AddFilePart(formData, "image", request.Image);
            foreach (var reference in referenceImages)
            {
                AddFilePart(formData, "reference_images", reference);
            }
            AddEnumPart(formData, "quality", request.Quality);
            AddIntPart(formData, "num_images", request.NumImages);
            AddIntPart(formData, "seed", request.Seed);

            var traceRequest = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt,
                ["image"] = DescribeFiles(new[] { request.Image }).Single(),
            };
            if (referenceImages.Count > 0)
            {
                traceRequest["reference_images"] = DescribeFiles(referenceImages);
            }
            AddTraceV45Options(traceRequest, size: null, request.Quality, request.NumImages, request.Seed);

            return await PostV45Async(
                IdeogramV45PreciseEditPath,
                formData,
                traceRequest,
                "precise-edit-image",
                request.NumImages ?? 1,
                requireGenerationId: false);
        }

        private async Task<IdeogramV45Response> PostV45Async(
            string endpoint,
            HttpContent content,
            object traceRequest,
            string operation,
            int expectedImageCount,
            bool requireGenerationId)
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
                    throw new HttpRequestException(
                        $"API request failed with status code {response.StatusCode}. Response: {responseContent}",
                        null,
                        response.StatusCode);
                }

                IdeogramV45Response? parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<IdeogramV45Response>(responseContent);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Ideogram 4.5 response does not match the published contract: {ex.Message}",
                        ex);
                }
                if (parsed == null)
                {
                    throw new InvalidDataException("Ideogram 4.5 returned an empty response body.");
                }
                if (requireGenerationId && string.IsNullOrWhiteSpace(parsed.GenerationId))
                {
                    throw new InvalidDataException("Ideogram 4.5 Generate response has no generation_id.");
                }
                var count = parsed.Data?.Count ?? 0;
                if (count != expectedImageCount)
                {
                    throw new InvalidDataException(
                        $"Ideogram 4.5 returned {count} image entries for a request of {expectedImageCount}.");
                }

                return parsed;
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
                    metadata: new { operation, apiVersion = "v2", model = "ideogram-4-5" });
                response?.Dispose();
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
            if (DetectImageContentType(image.Content) != image.ContentType)
            {
                throw new ArgumentException(
                    $"Ideogram 4.0 remix image bytes do not match declared type '{image.ContentType}'.",
                    nameof(image));
            }
        }

        private static void ValidateV45Common(string prompt, int? numImages, int? seed)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("Ideogram 4.5 requires a non-blank prompt.", nameof(prompt));
            }
            // The published limit counts Unicode characters, not UTF-16 units.
            var promptChars = prompt.EnumerateRunes().Count();
            if (promptChars > IdeogramV45MaxPromptChars)
            {
                throw new ArgumentException(
                    $"Ideogram 4.5 prompts are limited to {IdeogramV45MaxPromptChars:N0} characters; this prompt has {promptChars:N0}.",
                    nameof(prompt));
            }
            if (numImages is < 1 or > IdeogramV45MaxNumImages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numImages),
                    numImages,
                    $"Ideogram 4.5 num_images must be between 1 and {IdeogramV45MaxNumImages}.");
            }
            if (seed is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seed), seed, "Ideogram 4.5 seed must be between 0 and 2147483647.");
            }
        }

        private static void ValidateV45Image(IdeogramFile image, string role)
        {
            if (image?.Content == null || image.Content.Length == 0)
            {
                throw new ArgumentException($"Ideogram 4.5 {role} is empty.");
            }
            if (image.Content.LongLength > IdeogramV45MaxImageBytes)
            {
                throw new ArgumentException(
                    $"Ideogram 4.5 {role} is {image.Content.LongLength:N0} bytes; the limit is {IdeogramV45MaxImageBytes:N0}.");
            }
            if (image.ContentType is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                throw new ArgumentException(
                    $"Ideogram 4.5 {role} must be PNG, JPEG, or WEBP; received '{image.ContentType}'.");
            }
            if (DetectImageContentType(image.Content) != image.ContentType)
            {
                throw new ArgumentException(
                    $"Ideogram 4.5 {role} bytes do not match declared type '{image.ContentType}'.");
            }
        }

        /// Returns image/png, image/jpeg, or image/webp from the file
        /// signature, or null for any other content.
        public static string? DetectImageContentType(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50
                && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }
            if (bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == 'R' && bytes[1] == 'I'
                && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E'
                && bytes[10] == 'B' && bytes[11] == 'P')
            {
                return "image/webp";
            }
            return null;
        }

        private static void AddFilePart(MultipartFormDataContent formData, string fieldName, IdeogramFile file)
        {
            var fileContent = new ByteArrayContent(file.Content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
            formData.Add(fileContent, fieldName, file.FileName);
        }

        private static void AddTraceV45Options(
            Dictionary<string, object> traceRequest,
            string? size,
            IdeogramV45Quality? quality,
            int? numImages,
            int? seed)
        {
            AddTraceString(traceRequest, "size", size);
            AddTraceString(traceRequest, "quality", quality?.ToString());
            if (numImages.HasValue)
            {
                traceRequest["num_images"] = numImages.Value;
            }
            if (seed.HasValue)
            {
                traceRequest["seed"] = seed.Value;
            }
        }
    }
}
