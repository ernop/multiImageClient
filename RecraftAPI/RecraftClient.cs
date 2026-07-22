using Newtonsoft.Json;
using MultiImageClient;
using System.Text;

namespace RecraftAPIClient
{
    public class RecraftClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string _baseUrl = "https://external.api.recraft.ai/v1";

        public RecraftClient(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }


        /// size: "WxH" (e.g. "1024x1024") or aspect "w:h" (e.g. "16:9"); null/empty
        /// omits the field entirely and Recraft auto-selects a size from the prompt.
        /// style/substyle/artistic_level are V2/V3-only concepts — V4/V4.1 models
        /// reject or ignore them, so callers should pass "any"/empty for those.
        public async Task<GenerationResponse> GenerateImageAsync(string prompt, string artistic_level, string substyle, string style, string size, RecraftModel model = RecraftModel.recraftv3, string styleId = null, int n = 1, int? randomSeed = null)
        {
            var body = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["model"] = model.ToString(),
                ["response_format"] = "url",
            };
            if (!string.IsNullOrEmpty(size))
            {
                body["size"] = size;
            }
            if (n > 1)
            {
                body["n"] = n;
            }
            if (randomSeed.HasValue)
            {
                body["random_seed"] = randomSeed.Value;
            }

            if (!string.IsNullOrEmpty(styleId))
            {
                // Custom style built from a reference image (see CreateStyleAsync):
                // style_id replaces style/substyle entirely. NOTE: API-created
                // custom styles are only valid with V3 / V3 vector models.
                body["style_id"] = styleId;
            }
            else if (style == "any")
            {
                body["style"] = style;
            }
            else
            {
                body["style"] = style;
                body["substyle"] = substyle;
                body["artistic_level"] = artistic_level;
            }

            var content = new StringContent(
                JsonConvert.SerializeObject(body),
                Encoding.UTF8,
                "application/json"
            );
            return await PostTracedAsync<GenerationResponse>(
                "/images/generations",
                content,
                body,
                "generate-image",
                "http");
        }

        /// POST /images/imageToImage — generate variations of an input image
        /// guided by a prompt. strength in [0,1]: 0 = almost identical to the
        /// source, 1 = minimal similarity. Supported by V3 and V4/V4.1 models
        /// (raster and vector); this is the correct reference-image path for
        /// V4.x, where custom styles (style_id) are not supported.
        /// The output size follows the input image; the endpoint takes no size.
        public async Task<GenerationResponse> ImageToImageAsync(byte[] imageData, string prompt, float strength, RecraftModel model = RecraftModel.recraftv4_1, int n = 1, int? randomSeed = null)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "image", "image.png");
            content.Add(new StringContent(prompt), "prompt");
            content.Add(new StringContent(strength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)), "strength");
            content.Add(new StringContent(model.ToString()), "model");
            content.Add(new StringContent("url"), "response_format");
            if (n > 1)
            {
                content.Add(new StringContent(n.ToString()), "n");
            }
            if (randomSeed.HasValue)
            {
                content.Add(new StringContent(randomSeed.Value.ToString()), "random_seed");
            }
            var traceRequest = new Dictionary<string, object>
            {
                ["image"] = DescribeMemoryFile("image.png", imageData.LongLength),
                ["prompt"] = prompt,
                ["strength"] = strength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                ["model"] = model.ToString(),
                ["response_format"] = "url",
            };
            if (n > 1)
            {
                traceRequest["n"] = n;
            }
            if (randomSeed.HasValue)
            {
                traceRequest["random_seed"] = randomSeed.Value;
            }
            return await PostTracedAsync<GenerationResponse>(
                "/images/imageToImage",
                content,
                traceRequest,
                "image-to-image",
                "http-multipart");
        }

        public async Task<StyleResponse> CreateStyleAsync(byte[] imageData, RecraftStyle style)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            content.Add(new StringContent(style.ToString().ToLower()), "style");
            var traceRequest = new
            {
                file = DescribeMemoryFile("image.png", imageData.LongLength),
                style = style.ToString().ToLower(),
            };
            return await PostTracedAsync<StyleResponse>(
                "/styles",
                content,
                traceRequest,
                "create-style",
                "http-multipart");
        }

        public async Task<ImageResponse> VectorizeImageAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }
            return await PostTracedAsync<ImageResponse>(
                "/images/vectorize",
                content,
                BuildFileRequest(imageData, responseFormat),
                "vectorize-image",
                "http-multipart");
        }

        public async Task<ImageResponse> RemoveBackgroundAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }
            return await PostTracedAsync<ImageResponse>(
                "/images/removeBackground",
                content,
                BuildFileRequest(imageData, responseFormat),
                "remove-background",
                "http-multipart");
        }

        public async Task<ImageResponse> ClarityUpscaleAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }
            return await PostTracedAsync<ImageResponse>(
                "/images/clarityUpscale",
                content,
                BuildFileRequest(imageData, responseFormat),
                "clarity-upscale",
                "http-multipart");
        }

        public async Task<ImageResponse> GenerativeUpscaleAsync(byte[] imageData, string responseFormat = "url")
        {
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(imageData), "file", "image.png");
            if (responseFormat != "url")
            {
                content.Add(new StringContent(responseFormat), "response_format");
            }
            return await PostTracedAsync<ImageResponse>(
                "/images/generativeUpscale",
                content,
                BuildFileRequest(imageData, responseFormat),
                "generative-upscale",
                "http-multipart");
        }

        private async Task<TResponse> PostTracedAsync<TResponse>(
            string path,
            HttpContent content,
            object request,
            string operation,
            string transport)
        {
            var endpoint = _baseUrl + path;
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
                    throw new HttpRequestException($"API request failed: {response.StatusCode} - {responseContent}");
                }
                return JsonConvert.DeserializeObject<TResponse>(responseContent)!;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "recraft",
                    transport,
                    "POST",
                    endpoint,
                    startedAtUtc,
                    request: request,
                    response: responseContent,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: error,
                    metadata: new { operation });
            }
        }

        private static Dictionary<string, object> BuildFileRequest(byte[] imageData, string responseFormat)
        {
            var request = new Dictionary<string, object>
            {
                ["file"] = DescribeMemoryFile("image.png", imageData.LongLength),
            };
            if (responseFormat != "url")
            {
                request["response_format"] = responseFormat;
            }
            return request;
        }

        private static object DescribeMemoryFile(string name, long size)
            => new
            {
                name,
                size,
                source = "memory",
            };
    }

    public class GenerationResponse
    {
        [JsonProperty("data")]
        public List<ImageData> Data { get; set; } = new();

        [JsonProperty("created")]
        public long Created { get; set; }
    }

    public class StyleResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;
    }

    public class ImageResponse
    {
        [JsonProperty("image")]
        public ImageData Image { get; set; } = new();
    }

    public class ImageData
    {
        [JsonProperty("url")]
        public string Url { get; set; } = string.Empty;

        [JsonProperty("b64_json")]
        public string Base64Json { get; set; } = string.Empty;
    }
}