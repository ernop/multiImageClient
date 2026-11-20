using MultiImageClient;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace IdeogramAPIClient
{
    /// Image generator backed by Ideogram 4.5 (released 2026-09-30). Text
    /// prompts use Generate. With input images it uses Precise Edit: the first
    /// image is edited at its own width and height, unchanged pixels are copied
    /// exactly, and later images are sent as edit references.
    ///
    /// Results are downloaded here rather than through ImageManager's URL path,
    /// so a failed download, an unexpected format, or a size that differs from
    /// the reported resolution fails this generation visibly.
    public class IdeogramV45Generator : IImageGenerator
    {
        public const int MaxInputImages = 1 + IdeogramClient.IdeogramV45MaxReferenceImages;

        // A high-quality Precise Edit of a 2048x2048 source took 50 s on
        // 2026-10-07, half of HttpClient's default 100 s timeout.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(6);
        private static readonly HttpClient SharedDownloadClient = new() { Timeout = TimeSpan.FromMinutes(2) };

        private readonly SemaphoreSlim _semaphore;
        private readonly IdeogramClient _client;
        private readonly HttpClient _downloadClient;
        private readonly MultiClientRunStats _stats;
        private readonly string _size;
        private readonly IdeogramV45Quality? _quality;
        private readonly string _name;
        private readonly IReadOnlyList<string> _inputImagePaths;
        private readonly int _imageCount;
        private readonly ImageGeneratorApiType _apiType;

        public ImageGeneratorApiType ApiType => _apiType;

        private bool IsV46 => _apiType == ImageGeneratorApiType.IdeogramV46;
        private string DisplayVersion => IsV46 ? "Ideogram 4.6" : "Ideogram 4.5";

        /// size — a published preset (4.5: 1K/2K; 4.6: auto/auto_2k/auto_4k or
        ///   WIDTHxHEIGHT up to 4096x4096), or null/empty for Ideogram's
        ///   prompt-aware auto size. Must be empty with input images, because
        ///   Precise Edit keeps the source dimensions.
        /// quality — null omits the field and bills the provider default:
        ///   high for 4.5 Generate, medium for 4.6 Generate and Precise Edit.
        /// inputImagePaths — empty for Generate; otherwise 1 to 5 ordered
        ///   PNG/JPEG/WEBP files, the first edited and the rest references.
        /// httpHandler — replaces the API and download transports in tests.
        public IdeogramV45Generator(
            string apiKey,
            int maxConcurrency,
            string size,
            IdeogramV45Quality? quality,
            MultiClientRunStats stats,
            string name,
            IReadOnlyList<string> inputImagePaths = null,
            int imageCount = 1,
            HttpMessageHandler httpHandler = null,
            ImageGeneratorApiType apiType = ImageGeneratorApiType.IdeogramV45)
        {
            if (apiType is not (ImageGeneratorApiType.IdeogramV45 or ImageGeneratorApiType.IdeogramV46))
            {
                throw new ArgumentOutOfRangeException(nameof(apiType), apiType, "Expected IdeogramV45 or IdeogramV46.");
            }
            _apiType = apiType;
            _inputImagePaths = inputImagePaths ?? Array.Empty<string>();
            _size = size ?? string.Empty;
            if (_inputImagePaths.Count > MaxInputImages)
            {
                throw new ArgumentException(
                    $"{DisplayVersion} Precise Edit accepts at most {MaxInputImages} input images (one edited, {IdeogramClient.IdeogramV45MaxReferenceImages} references); received {_inputImagePaths.Count}.",
                    nameof(inputImagePaths));
            }
            if (_inputImagePaths.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"{DisplayVersion} input image paths must not be blank.", nameof(inputImagePaths));
            }
            if (_inputImagePaths.Count > 0 && _size.Length > 0)
            {
                throw new ArgumentException(
                    $"{DisplayVersion} Precise Edit keeps the source dimensions and has no size field.",
                    nameof(size));
            }
            if (_inputImagePaths.Count == 0 && _size.Length > 0)
            {
                var validSize = IsV46
                    ? IdeogramClient.IsIdeogramV46TextSize(_size)
                    : IdeogramClient.IsIdeogramV45TextSize(_size);
                if (!validSize)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(size),
                        size,
                        IsV46
                            ? "Ideogram 4.6 text-to-image size must be auto, auto_2k, auto_4k, or WIDTHxHEIGHT (multiples of 32, up to 4096x4096 pixels)."
                            : "Ideogram 4.5 text-to-image size must be one of the published 1K/2K presets, or empty for auto.");
                }
            }
            if (_inputImagePaths.Count == 0 && quality == IdeogramV45Quality.very_low)
            {
                if (!IsV46)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(quality),
                        quality,
                        "Ideogram 4.5 accepts quality=very_low only with source images.");
                }
                if (IdeogramClient.IsIdeogramV464KSize(_size))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(quality),
                        quality,
                        "Ideogram 4.6 quality=very_low does not support 4K sizes.");
                }
            }
            if (imageCount < 1 || imageCount > IdeogramClient.IdeogramV45MaxNumImages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(imageCount),
                    imageCount,
                    $"{DisplayVersion} returns 1 to {IdeogramClient.IdeogramV45MaxNumImages} images per request.");
            }

            _client = httpHandler == null
                ? new IdeogramClient(apiKey, RequestTimeout)
                : new IdeogramClient(apiKey, httpHandler);
            _downloadClient = httpHandler == null
                ? SharedDownloadClient
                : new HttpClient(httpHandler, disposeHandler: false);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _quality = quality;
            _name = name ?? string.Empty;
            _imageCount = imageCount;
        }

        /// Maps the UI and REPL quality vocabulary onto Ideogram 4.5. xhigh
        /// and max are GPT Image 2.5 tiers above high, so they send high.
        /// auto omits the field so Ideogram applies its own default.
        public static IdeogramV45Quality? QualityFromOption(string quality)
            => (quality ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "low" => IdeogramV45Quality.low,
                "medium" => IdeogramV45Quality.medium,
                "high" or "xhigh" or "max" => IdeogramV45Quality.high,
                "auto" => null,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(quality),
                    quality,
                    "Unknown quality for Ideogram 4.5. Expected low, medium, high, xhigh, max, or auto."),
            };

        private bool IsEdit => _inputImagePaths.Count > 0;

        private string QualityLabel => _quality?.ToString() ?? "provider default";

        public string GetFilenamePart(PromptDetails pd)
        {
            var parts = new List<string>
            {
                IsV46 ? "IdeogramV46" : "IdeogramV45",
                _name,
                IsEdit ? "precise-edit" : (_size.Length > 0 ? _size : "auto"),
                _quality?.ToString() ?? "default",
            };
            return string.Join("_", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        public List<string> GetRightParts()
        {
            var contents = new List<string> { DisplayVersion };
            if (!string.IsNullOrEmpty(_name))
            {
                contents.Add(_name);
            }
            contents.Add(IsEdit ? "precise edit" : (_size.Length > 0 ? _size : "auto size"));
            contents.Add($"quality {QualityLabel}");
            return contents;
        }

        public string GetGeneratorSpecPart()
        {
            var parts = new List<string>
            {
                IsEdit ? $"{DisplayVersion} precise edit" : $"{DisplayVersion} generate",
                $"quality {QualityLabel}",
            };
            // Contact sheets append the returned pixel size, so only auto is named here.
            if (!IsEdit && _size.Length == 0)
            {
                parts.Add("auto size");
            }
            if (_inputImagePaths.Count > 1)
            {
                parts.Add(_inputImagePaths.Count == 2 ? "1 reference image" : $"{_inputImagePaths.Count - 1} reference images");
            }
            return string.Join(" · ", parts);
        }

        public decimal GetCost()
        {
            // Per-image prices from live dry_run quotes (4.5 on 2026-10-07,
            // 4.6 on 2026-11-19). 4.6 adds 4K Generate tiers at 2x 2K prices.
            decimal perImage;
            if (IsEdit)
            {
                perImage = (_quality ?? IdeogramV45Quality.medium) switch
                {
                    IdeogramV45Quality.very_low => 0.008m,
                    IdeogramV45Quality.low => 0.03m,
                    IdeogramV45Quality.medium => 0.06m,
                    _ => 0.22m,
                };
            }
            else if (IsV46 && IdeogramClient.IsIdeogramV464KSize(_size))
            {
                perImage = (_quality ?? IdeogramV45Quality.medium) switch
                {
                    IdeogramV45Quality.low => 0.06m,
                    IdeogramV45Quality.medium => 0.12m,
                    _ => 0.20m,
                };
            }
            else
            {
                var defaultGenQuality = IsV46 ? IdeogramV45Quality.medium : IdeogramV45Quality.high;
                perImage = (_quality ?? defaultGenQuality) switch
                {
                    IdeogramV45Quality.very_low => 0.008m,
                    IdeogramV45Quality.low => 0.03m,
                    IdeogramV45Quality.medium => 0.06m,
                    _ => 0.10m,
                };
            }
            return perImage * _imageCount;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                if (IsV46) _stats.IdeogramV46RequestCount++;
                else _stats.IdeogramV45RequestCount++;
                var numImages = _imageCount == 1 ? (int?)null : _imageCount;
                IdeogramV45Response response;
                if (IsEdit)
                {
                    var files = new List<IdeogramFile>();
                    for (var i = 0; i < _inputImagePaths.Count; i++)
                    {
                        files.Add(await ReadInputFileAsync(_inputImagePaths[i], i));
                    }
                    var editReq = new IdeogramV45PreciseEditRequest(promptDetails.Prompt, files[0])
                    {
                        ReferenceImages = files.Skip(1).ToList(),
                        Quality = _quality,
                        NumImages = numImages,
                    };
                    response = IsV46
                        ? await _client.PreciseEditImageV46Async(editReq)
                        : await _client.PreciseEditImageV45Async(editReq);
                }
                else
                {
                    var genReq = new IdeogramV45GenerateRequest(promptDetails.Prompt)
                    {
                        Size = _size.Length > 0 ? _size : null,
                        Quality = _quality,
                        NumImages = numImages,
                    };
                    response = IsV46
                        ? await _client.GenerateImageV46Async(genReq)
                        : await _client.GenerateImageV45Async(genReq);
                }

                // The client guarantees exactly one data entry per requested image.
                var data = response.Data!;
                if (data.Any(item => !item.IsImageSafe))
                {
                    if (IsV46) _stats.IdeogramV46RefusedCount++;
                    else _stats.IdeogramV45RefusedCount++;
                    return Fail(
                        $"{DisplayVersion} rejected the generated image as unsafe.",
                        GenericImageGenerationErrorType.ContentModerated,
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }

                // The returned prompt is Ideogram's structured JSON expansion.
                // Record it in the history; the sent prompt stays unchanged.
                var providerPrompt = data[0].Prompt;
                if (!string.IsNullOrWhiteSpace(providerPrompt)
                    && !string.Equals(providerPrompt, promptDetails.Prompt, StringComparison.OrdinalIgnoreCase))
                {
                    promptDetails.AddStep(providerPrompt, TransformationType.IdeogramRewrite);
                }

                var images = new List<CreatedBase64Image>();
                string contentType = null;
                for (var i = 0; i < data.Count; i++)
                {
                    var (bytes, itemContentType) = await DownloadVerifiedAsync(data[i], i);
                    if (contentType != null && contentType != itemContentType)
                    {
                        throw new InvalidDataException(
                            $"Ideogram 4.5 returned mixed image formats ({contentType} and {itemContentType}) in one response.");
                    }
                    contentType = itemContentType;
                    images.Add(new CreatedBase64Image { bytesBase64 = Convert.ToBase64String(bytes), newPrompt = "" });
                }

                Logger.Log(
                    $"\t<- {DisplayVersion} {(IsEdit ? "precise edit" : "generate")} OK in {sw.ElapsedMilliseconds} ms: "
                    + $"{string.Join(", ", data.Select(item => item.Resolution))} ({response.GenerationKind} {response.GenerationId})");
                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    if (IsV46) _stats.IdeogramV46RefusedCount++;
                    else _stats.IdeogramV45RefusedCount++;
                }
                var errorType = ex.StatusCode switch
                {
                    HttpStatusCode.PaymentRequired => GenericImageGenerationErrorType.NoMoneyLeft,
                    HttpStatusCode.UnprocessableEntity => GenericImageGenerationErrorType.RequestModerated,
                    _ => GenericImageGenerationErrorType.Unknown,
                };
                return Fail(ex.Message, errorType, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, GenericImageGenerationErrorType.Unknown, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static async Task<IdeogramFile> ReadInputFileAsync(string path, int index)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var contentType = IdeogramClient.DetectImageContentType(bytes)
                ?? throw new InvalidDataException(
                    $"Ideogram 4.5 input image {index + 1} ('{Path.GetFileName(path)}') is not PNG, JPEG, or WEBP.");
            var baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = $"ideogram-v45-input{index}";
            }
            var extension = contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                _ => ".webp",
            };
            return new IdeogramFile(bytes, baseName + extension, contentType);
        }

        private async Task<(byte[] Bytes, string ContentType)> DownloadVerifiedAsync(IdeogramV45ImageObject item, int index)
        {
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException($"Ideogram 4.5 image {index} has no valid HTTPS URL.");
            }
            var (expectedWidth, expectedHeight) = ParseResolution(item.Resolution, index);

            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage response = null;
            byte[] bytes = null;
            Exception traceError = null;
            try
            {
                response = await _downloadClient.GetAsync(uri);
                if (!response.IsSuccessStatusCode)
                {
                    // No status code on the exception: the billing and moderation
                    // classification applies to the generation call only.
                    throw new HttpRequestException(
                        $"Ideogram 4.5 image {index} download returned HTTP {(int)response.StatusCode}.");
                }
                bytes = await response.Content.ReadAsByteArrayAsync();
                var detected = IdeogramClient.DetectImageContentType(bytes)
                    ?? throw new InvalidDataException($"Ideogram 4.5 image {index} is not PNG, JPEG, or WEBP.");
                var declared = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Ideogram 4.5 image {index} was served as '{declared}' but its bytes are {detected}.");
                }
                var info = Raster.Identify(bytes);
                if (info.Width != expectedWidth || info.Height != expectedHeight)
                {
                    throw new InvalidDataException(
                        $"Ideogram 4.5 image {index} is {info.Width}x{info.Height}, but the response reported {item.Resolution}.");
                }
                return (bytes, detected);
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
                    uri.ToString(),
                    startedAtUtc,
                    response: new
                    {
                        contentType = response?.Content.Headers.ContentType?.MediaType,
                        contentLength = response?.Content.Headers.ContentLength,
                        byteLength = bytes?.LongLength ?? 0,
                        sha256 = bytes == null
                            ? ""
                            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                    },
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: traceError,
                    metadata: new { operation = "image-download", model = "ideogram-4-5", imageIndex = index });
                response?.Dispose();
            }
        }

        private static (int Width, int Height) ParseResolution(string resolution, int index)
        {
            var parts = (resolution ?? string.Empty).Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
                && width > 0
                && height > 0)
            {
                return (width, height);
            }
            throw new InvalidDataException(
                $"Ideogram 4.5 image {index} has malformed resolution '{resolution}'.");
        }

        private TaskProcessResult Fail(
            string message,
            GenericImageGenerationErrorType errorType,
            PromptDetails promptDetails,
            IImageGenerator generator,
            long elapsedMs)
            => new()
            {
                IsSuccess = false,
                ErrorMessage = message,
                GenericImageErrorType = errorType,
                PromptDetails = promptDetails,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
    }
}
