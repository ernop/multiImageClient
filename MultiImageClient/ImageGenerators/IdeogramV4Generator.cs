using MultiImageClient;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace IdeogramAPIClient
{
    /// Image generator backed by Ideogram 4.0. Text prompts use /generate;
    /// prompts with a bound input image use /remix.
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
        private readonly string _inputImagePath;
        private readonly int? _imageWeight;
        private const long MaxInputImageBytes = 10_000_000 - 65536;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.IdeogramV4;

        /// resolution — one of the documented v4 2K strings ("2048x2048",
        ///   "2304x1728", "2560x1440", ...), or null/empty to let the API
        ///   default to 2048x2048.
        /// inputImagePath — when set, route through Ideogram 4.0 Remix.
        /// imageWeight — optional provider-native source influence. Null lets
        ///   Ideogram choose a weight from the edit instruction.
        public IdeogramV4Generator(
            string apiKey,
            int maxConcurrency,
            string resolution,
            IdeogramRenderingSpeed renderingSpeed,
            MultiClientRunStats stats,
            string name,
            string inputImagePath = null,
            int? imageWeight = null)
        {
            if (renderingSpeed == IdeogramRenderingSpeed.FLASH)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(renderingSpeed),
                    "Ideogram 4.0 currently rejects rendering_speed=FLASH.");
            }
            _client = new IdeogramClient(apiKey);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _resolution = resolution ?? string.Empty;
            _renderingSpeed = renderingSpeed;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _inputImagePath = inputImagePath;
            _imageWeight = imageWeight;
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
            if (!string.IsNullOrEmpty(_inputImagePath))
            {
                parts.Add("remix");
            }
            parts.Add(_renderingSpeed.ToString());
            return string.Join("_", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public List<string> GetRightParts()
        {
            var contents = new List<string> { "Ideogram 4.0" };
            if (!string.IsNullOrEmpty(_name))
            {
                contents.Add(_name);
            }
            if (!string.IsNullOrEmpty(_resolution))
            {
                contents.Add(_resolution);
            }
            if (!string.IsNullOrEmpty(_inputImagePath))
            {
                contents.Add("remix");
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
            return $"Ideogram 4.0 {_renderingSpeed}";
        }

        public decimal GetCost()
        {
            // Published Ideogram API rates, revised 2025-08-06. Generate and
            // Remix share the same per-output-image prices.
            return _renderingSpeed switch
            {
                IdeogramRenderingSpeed.TURBO => 0.03m,
                IdeogramRenderingSpeed.QUALITY => 0.10m,
                _ => 0.06m,
            };
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            try
            {
                _stats.IdeogramV4RequestCount++;

                IdeogramV4GenerateResponse response;
                if (string.IsNullOrWhiteSpace(_inputImagePath))
                {
                    var request = new IdeogramV4GenerateRequest(promptDetails.Prompt)
                    {
                        Resolution = string.IsNullOrWhiteSpace(_resolution) ? null : _resolution,
                        RenderingSpeed = _renderingSpeed,
                    };
                    response = await _client.GenerateImageV4Async(request);
                }
                else
                {
                    var sourceBytes = File.ReadAllBytes(_inputImagePath);
                    var sourceContentType = DetectInputContentType(sourceBytes, _inputImagePath);
                    var uploadBytes = ConformRemixInput(sourceBytes, _inputImagePath);
                    var uploadContentType = ReferenceEquals(sourceBytes, uploadBytes)
                        ? sourceContentType
                        : "image/png";
                    var request = new IdeogramV4RemixRequest(
                        promptDetails.Prompt,
                        new IdeogramFile(
                            uploadBytes,
                            BuildUploadFileName(_inputImagePath, uploadContentType),
                            uploadContentType))
                    {
                        ImageWeight = _imageWeight,
                        Resolution = string.IsNullOrWhiteSpace(_resolution) ? null : _resolution,
                        RenderingSpeed = _renderingSpeed,
                    };
                    response = await _client.RemixImageV4Async(request);
                }

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

                if (response.Data.Any(item => !item.IsImageSafe))
                {
                    _stats.IdeogramV4RefusedCount++;
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Ideogram 4.0 rejected the generated image as unsafe.",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                        GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }
                if (response.Data.Any(item => string.IsNullOrWhiteSpace(item.Url)))
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Ideogram 4.0 returned an image entry without a URL.",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV4,
                        GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                var imageObject = response.Data[0];

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

                // The current request contract has no num_images field, but
                // preserve every correlated image if the provider returns more
                // than one entry.
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

        private static string DetectInputContentType(byte[] bytes, string sourcePath)
        {
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
            throw new InvalidOperationException(
                $"Ideogram 4.0 Remix requires PNG, JPEG, or WEBP; '{sourcePath}' is none of those.");
        }

        private static byte[] ConformRemixInput(byte[] bytes, string sourcePath)
        {
            if (bytes.LongLength <= MaxInputImageBytes)
            {
                return bytes;
            }

            using var image = Image.Load(bytes);
            var scale = Math.Min(
                0.95,
                Math.Sqrt((double)MaxInputImageBytes / bytes.LongLength) * 0.95);
            while (true)
            {
                var width = Math.Max(1, (int)Math.Floor(image.Width * scale));
                var height = Math.Max(1, (int)Math.Floor(image.Height * scale));
                using var resized = image.Clone(ctx => ctx.Resize(width, height));
                using var stream = new MemoryStream();
                resized.SaveAsPng(stream);
                var encoded = stream.ToArray();
                if (encoded.LongLength <= MaxInputImageBytes)
                {
                    Logger.Log(
                        $"Ideogram 4.0 Remix input '{sourcePath}' ({image.Width}x{image.Height}, "
                        + $"{bytes.LongLength:N0} bytes) exceeded the 10 MB input limit; "
                        + $"downscaled to {width}x{height} PNG ({encoded.LongLength:N0} bytes) before upload.");
                    return encoded;
                }
                if (width == 1 && height == 1)
                {
                    throw new InvalidOperationException(
                        $"Could not fit '{sourcePath}' under Ideogram 4.0 Remix's "
                        + $"{MaxInputImageBytes:N0}-byte input cap.");
                }
                scale *= 0.85;
            }
        }

        private static string BuildUploadFileName(string sourcePath, string contentType)
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "ideogram-v4-remix";
            }
            var extension = contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => throw new InvalidOperationException(
                    $"Unsupported Ideogram 4.0 Remix content type '{contentType}'."),
            };
            return baseName + extension;
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
