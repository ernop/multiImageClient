using MultiImageClient;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


namespace IdeogramAPIClient
{
    public class IdeogramV3Generator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly IdeogramClient _client;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly MultiClientRunStats _stats;
        private readonly IdeogramV3StyleType _styleType;
        private readonly IdeogramMagicPromptOption? _magicPromptOption;
        private readonly string _aspectRatio;
        private readonly IdeogramRenderingSpeed _renderingSpeed;
        private readonly string _negativePrompt;
        private readonly string _name;
        private readonly string _inputImagePath;
        private readonly int _imageCount;
        private readonly int? _seed;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.IdeogramV3;

        /// inputImagePath: when set, the image is sent as a style_reference_images
        ///   part — Ideogram uses it as a style/subject guide, not a literal edit.
        /// imageCount: num_images per call (API range 1-8).
        public IdeogramV3Generator(
            string apiKey,
            int maxConcurrency,
            IdeogramV3StyleType styleType,
            IdeogramMagicPromptOption? magicPromptOption,
            IdeogramAspectRatio aspectRatio,
            IdeogramRenderingSpeed renderingSpeed,
            string negativePrompt,
            MultiClientRunStats stats,
            string name,
            string inputImagePath = null,
            int imageCount = 1,
            int? seed = null)
        {
            _client = new IdeogramClient(apiKey);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _styleType = styleType;
            _magicPromptOption = magicPromptOption;
            var convertedAspectRatio = aspectRatio.ToString().Replace("ASPECT_", "").Replace("_", "x");
            _aspectRatio = convertedAspectRatio;
            _renderingSpeed = renderingSpeed;
            _negativePrompt = negativePrompt ?? string.Empty;
            _name = string.IsNullOrEmpty(name) ? "" : name;
            _inputImagePath = inputImagePath;
            _imageCount = Math.Clamp(imageCount, 1, 8);
            _seed = seed;
        }

        

        public string GetFilenamePart(PromptDetails pd)
        {
            var parts = new List<string> { ApiType.ToString() };
            if (!string.IsNullOrEmpty(_name))
            {
                parts.Add(_name);
            }

            parts.Add(_styleType.ToString());
            parts.Add(_aspectRatio.ToString().Replace(":", "x"));
            parts.Add(_renderingSpeed.ToString());
            return string.Join("_", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public List<string> GetRightParts()
        {
            var contents = new List<string> { "ideogram v3" };
            if (!string.IsNullOrEmpty(_name))
            {
                contents.Add(_name);
            }

            contents.Add(_styleType.ToString());
            contents.Add(_aspectRatio.ToString().Replace(":", "x"));
            contents.Add(_renderingSpeed.ToString());

            return contents;
        }

        public string GetGeneratorSpecPart()
        {
            if (!string.IsNullOrEmpty(_name))
            {
                return _name;
            }
            return "ideogram-v3";
        }

        public decimal GetCost()
        {
            // Pricing is not yet documented; leave a placeholder number until official rates available.
            return 0.08m * _imageCount;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            try
            {
                _stats.IdeogramV3RequestCount++;

                var request = new IdeogramV3GenerateRequest(promptDetails.Prompt)
                {
                    AspectRatio = _aspectRatio,
                    RenderingSpeed = _renderingSpeed,
                    StyleType = _styleType,
                    MagicPrompt = _magicPromptOption,
                    NegativePrompt = string.IsNullOrWhiteSpace(_negativePrompt) ? null : _negativePrompt,
                    NumImages = _imageCount > 1 ? _imageCount : (int?)null,
                    Seed = _seed
                };
                if (!string.IsNullOrEmpty(_inputImagePath))
                {
                    var refBytes = File.ReadAllBytes(_inputImagePath);
                    request.StyleReferenceImages.Add(new IdeogramFile(
                        refBytes,
                        Path.GetFileName(_inputImagePath),
                        DetectReferenceContentType(refBytes, _inputImagePath)));
                }

                var response = await _client.GenerateImageV3Async(request);

                if (response?.Data == null || response.Data.Count == 0)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "No images generated",
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                        GenericImageErrorType = GenericImageGenerationErrorType.NoImagesGenerated,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                    };
                }

                var imageObject = response.Data[0];
                if (!string.IsNullOrWhiteSpace(imageObject.Prompt) &&
                    !string.Equals(imageObject.Prompt, promptDetails.Prompt, StringComparison.OrdinalIgnoreCase))
                {
                    promptDetails.ReplacePrompt(imageObject.Prompt, imageObject.Prompt, TransformationType.IdeogramRewrite);
                }

                if (response.Data.Count == 1)
                {
                    return new TaskProcessResult
                    {
                        IsSuccess = true,
                        Url = imageObject.Url,
                        PromptDetails = promptDetails,
                        ImageGenerator = ImageGeneratorApiType.IdeogramV3,
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
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            catch (HttpRequestException ex)
            {
                _stats.IdeogramV3RefusedCount++;
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = promptDetails,
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
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
                    ImageGenerator = ImageGeneratorApiType.IdeogramV3,
                    GenericImageErrorType = GenericImageGenerationErrorType.Unknown,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart()
                };
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // Ideogram's generate endpoint only accepts PNG/JPEG/WEBP reference
        // images and sniffs the actual bytes, so the multipart part must be
        // labeled with the file's true type — a mislabeled part gets the whole
        // request rejected. Anything else is a hard error here rather than a
        // guessed label.
        private static string DetectReferenceContentType(byte[] bytes, string sourcePath)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            {
                return "image/webp";
            }
            throw new InvalidOperationException(
                $"Ideogram style reference images must be PNG, JPEG, or WEBP; '{sourcePath}' is none of those.");
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

