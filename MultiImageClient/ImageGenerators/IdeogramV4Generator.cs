using MultiImageClient;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace IdeogramAPIClient
{
    /// Image generator backed by Ideogram 4.0 (POST /v1/ideogram-v4/generate,
    /// released 2026-06-03). 2K-native output; rendering_speed trades latency
    /// against fidelity (FLASH cheapest/fastest -> QUALITY best).
    ///
    /// v4 has no style_type/magic_prompt knobs — plain text_prompt is
    /// auto-expanded into a structured JSON prompt server-side, and that
    /// expansion comes back in data[0].prompt (as serialized JSON).
    public class IdeogramV4Generator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IdeogramClient _client;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly MultiClientRunStats _stats;
        private readonly string _resolution;
        private readonly IdeogramRenderingSpeed _renderingSpeed;
        private readonly string _name;
        private readonly int _imageCount;
        private readonly int? _seed;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.IdeogramV4;

        /// resolution — one of the documented v4 2K strings ("2048x2048",
        ///   "2304x1728", "2560x1440", ...), or null/empty to let the API
        ///   default to 2048x2048.
        /// imageCount — num_images per call (API range 1-8).
        public IdeogramV4Generator(
            string apiKey,
            int maxConcurrency,
            string resolution,
            IdeogramRenderingSpeed renderingSpeed,
            MultiClientRunStats stats,
            string name,
            int imageCount = 1,
            int? seed = null)
        {
            _client = new IdeogramClient(apiKey);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _resolution = resolution ?? string.Empty;
            _renderingSpeed = renderingSpeed;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _imageCount = Math.Clamp(imageCount, 1, 8);
            _seed = seed;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var parts = new List<string> { "IdeogramV4" };
            if (!string.IsNullOrEmpty(_name))
            {
                parts.Add(_name);
            }
            if (!string.IsNullOrEmpty(_resolution))
            {
                parts.Add(_resolution);
            }
            parts.Add(_renderingSpeed.ToString());
            return string.Join("_", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public List<string> GetRightParts()
        {
            var contents = new List<string> { "ideogram v4" };
            if (!string.IsNullOrEmpty(_name))
            {
                contents.Add(_name);
            }
            if (!string.IsNullOrEmpty(_resolution))
            {
                contents.Add(_resolution);
            }
            contents.Add(_renderingSpeed.ToString());
            return contents;
        }

        public string GetGeneratorSpecPart()
        {
            if (!string.IsNullOrEmpty(_name))
            {
                return _name;
            }
            return $"ideogram-v4 {_renderingSpeed}";
        }

        public decimal GetCost()
        {
            // Per-image pricing varies by rendering_speed; Ideogram's pricing
            // page lists v4 DEFAULT around $0.08 with FLASH/TURBO cheaper and
            // QUALITY pricier. Rough estimates until we wire exact rates.
            // NOT multiplied by _imageCount: as of 2026-07-20 the v4 endpoint
            // silently ignores num_images (HTTP 200, one image back), so one
            // image is what actually gets billed. The multi-image handling in
            // ProcessPromptAsync is ready for when Ideogram turns it on.
            return _renderingSpeed switch
            {
                IdeogramRenderingSpeed.FLASH => 0.025m,
                IdeogramRenderingSpeed.TURBO => 0.04m,
                IdeogramRenderingSpeed.QUALITY => 0.12m,
                _ => 0.08m,
            };
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            try
            {
                _stats.IdeogramV4RequestCount++;

                var request = new IdeogramV4GenerateRequest(promptDetails.Prompt)
                {
                    Resolution = string.IsNullOrWhiteSpace(_resolution) ? null : _resolution,
                    RenderingSpeed = _renderingSpeed,
                    NumImages = _imageCount > 1 ? _imageCount : null,
                    Seed = _seed,
                };

                var response = await _client.GenerateImageV4Async(request);

                if (response?.Data == null || response.Data.Count == 0)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "No images generated",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                        GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                var imageObject = response.Data[0];

                if (!imageObject.IsImageSafe)
                {
                    _stats.IdeogramV4RefusedCount++;
                }

                // v4 returns the server-side structured-JSON prompt expansion in
                // `prompt`. We do NOT ReplacePrompt with it (it's JSON, not a
                // human-readable rewrite) — log it via the history instead.
                if (!string.IsNullOrWhiteSpace(imageObject.Prompt) &&
                    !string.Equals(imageObject.Prompt, promptDetails.Prompt, StringComparison.OrdinalIgnoreCase))
                {
                    promptDetails.AddStep(imageObject.Prompt, TransformationType.IdeogramRewrite);
                }

                if (response.Data.Count == 1)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Url = imageObject.Url,
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                // num_images > 1: ImageManager's Url path only saves one image,
                // so download them all here and return base64 entries.
                var images = new List<CreatedBase64Image>();
                string contentType = null;
                foreach (var item in response.Data)
                {
                    if (string.IsNullOrEmpty(item.Url)) continue;
                    var download = await DownloadImageAsync(item.Url);
                    contentType ??= download.ContentType;
                    var bytes = download.Bytes;
                    images.Add(new CreatedBase64Image { bytesBase64 = Convert.ToBase64String(bytes), newPrompt = "" });
                }

                return new TaskProcessResult
                {
                    IsSuccess = images.Count > 0,
                    ErrorMessage = images.Count > 0 ? null : "No downloadable image URLs in multi-image response",
                    Base64ImageDatas = images,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            catch (HttpRequestException ex)
            {
                _stats.IdeogramV4RefusedCount++;
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                    GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            catch (Exception ex)
            {
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                    GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<(byte[] Bytes, string ContentType)> DownloadImageAsync(string url)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            byte[] bytes = null;
            Exception traceError = null;
            try
            {
                response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                return (bytes, response.Content.Headers.ContentType?.MediaType);
            }
            catch (Exception ex)
            {
                traceError = ex;
                throw;
            }
            finally
            {
                GenerationTrace.RecordProviderCall(
                    "ideogram",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    response: BinaryResponseMetadata(response, bytes),
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: traceError,
                    metadata: new { operation = "image-download" });
                response?.Dispose();
            }
        }

        private static object BinaryResponseMetadata(HttpResponseMessage response, byte[] bytes)
            => new
            {
                contentType = response?.Content.Headers.ContentType?.MediaType,
                contentLength = response?.Content.Headers.ContentLength,
                byteLength = bytes?.LongLength ?? 0,
                sha256 = bytes == null
                    ? ""
                    : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            };
    }
}
