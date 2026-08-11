#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GrokWebImagineGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly ImageGeneratorApiType _apiType;
        private readonly string _aspectRatio;
        private readonly bool _enablePro;
        private readonly bool _enableSideBySide;
        private readonly TimeSpan _timeout;
        private readonly string? _captureBaseFolder;

        public ImageGeneratorApiType ApiType => _apiType;

        public GrokWebImagineGenerator(
            GrokWebClient client,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool pro,
            string aspectRatio = "2:3",
            bool enableSideBySide = true,
            int timeoutMinutes = 10,
            Settings? settings = null,
            bool captureSessions = true)
        {
            _client = client;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _apiType = pro ? ImageGeneratorApiType.GrokWebImaginePro : ImageGeneratorApiType.GrokWebImagine;
            _aspectRatio = aspectRatio;
            _enablePro = pro;
            _enableSideBySide = enableSideBySide;
            _timeout = TimeSpan.FromMinutes(timeoutMinutes);
            _captureBaseFolder = captureSessions && settings != null
                ? settings.ImageDownloadBaseFolder
                : null;
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var tier = _enablePro ? "grokweb-pro" : "grokweb";
            var ar = _aspectRatio.Replace(':', 'x');
            return $"{tier}_{ar}";
        }

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                _enablePro ? "Grok Web Imagine Pro" : "Grok Web Imagine",
                "imagine-x-1",
                "grok.com/ws/imagine/listen",
                $"AR {_aspectRatio}",
                _enableSideBySide ? "side-by-side" : "single",
            };
        }

        public string GetGeneratorSpecPart()
        {
            var line = _enablePro ? "Grok Web Imagine Pro" : "Grok Web Imagine";
            line += $"  {_aspectRatio}";
            if (_enableSideBySide) line += "  side-by-side";
            return line;
        }

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    Logger.Log($"\t-> Grok Web Imagine AR={_aspectRatio} pro={_enablePro}: WARNING empty prompt text");
                }
                else
                {
                    var head = prompt.Length <= 120 ? prompt : prompt[..117] + "...";
                    Logger.Log($"\t-> Grok Web Imagine AR={_aspectRatio} pro={_enablePro} [{prompt.Length} chars]: {head}");
                }

                var result = await _client.GenerateImageAsync(
                    prompt,
                    _aspectRatio,
                    _enablePro,
                    _enableSideBySide,
                    _timeout,
                    _captureBaseFolder);

                sw.Stop();
                if (result.Images.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail("Grok web returned no images.", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                _stats.GrokImageGenerationSuccessCount++;
                var newModelFlag = !string.IsNullOrWhiteSpace(result.ModelName)
                    && !GrokWebClient.IsBaselineServedModel(result.ModelName)
                        ? "  ** NEW MODEL **"
                        : "";
                Logger.Log($"\t<- Grok Web Imagine OK in {sw.ElapsedMilliseconds} ms; {result.Images.Count} image(s) model={result.ModelName ?? "?"} mode={result.Mode ?? "?"}{newModelFlag}");
                if (!string.IsNullOrWhiteSpace(result.CaptureDirectory))
                {
                    Logger.Log($"\t   capture: {result.CaptureDirectory}");
                }

                var contentType = GuessContentTypeFromBytes(result.Images[0]);

                var images = result.Images
                    .Select((bytes, index) => new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    })
                    .ToList();

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = contentType,
                    PromptDetails = promptDetails,
                    ImageGenerator = _apiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                    ServedModelName = result.ModelName,
                    ServedModelMode = result.Mode,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                Logger.Log($"\t<- Grok Web Imagine FAIL: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static string GuessContentTypeFromBytes(byte[] bytes)
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

            return "image/jpeg";
        }

        private TaskProcessResult Fail(string message, PromptDetails pd, IImageGenerator generator, long elapsedMs)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = _apiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
        }
    }
}
