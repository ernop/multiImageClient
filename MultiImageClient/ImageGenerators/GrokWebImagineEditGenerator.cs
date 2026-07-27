#nullable enable
using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Edits an existing image through the consumer grok.com imagine WebSocket
    /// (wss://grok.com/ws/imagine/listen), the same transport the text-to-image
    /// generator uses. The uploaded source image's media URL is passed as
    /// properties.image_uri on the generate message and Grok edits that image
    /// instead of generating from scratch.
    ///
    /// This deliberately avoids /rest/app-chat/conversations/new (the old
    /// imagine-image-edit path), which 403s with "Request rejected by anti-bot
    /// rules" for non-browser callers. Upload (/http/upload-file-v2/direct) and
    /// asset lookup (/rest/assets/{id}) are not behind that gate, so the media
    /// URL is obtainable; the WS honors image_uri without any anti-bot header.
    public class GrokWebImagineEditGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly string _imageReferenceUrl;
        private readonly bool _enablePro;
        private readonly string _aspectRatio;
        private readonly bool _enableSideBySide;
        private readonly TimeSpan _timeout;
        private readonly string? _captureBaseFolder;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineEdit;

        public GrokWebImagineEditGenerator(
            GrokWebClient client,
            string imageReferenceUrl,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool pro = true,
            string aspectRatio = "1:1",
            bool enableSideBySide = true,
            int timeoutMinutes = 10,
            Settings? settings = null,
            bool captureSessions = false)
        {
            _client = client;
            _imageReferenceUrl = imageReferenceUrl;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _enablePro = pro;
            _aspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "1:1" : aspectRatio;
            _enableSideBySide = enableSideBySide;
            _timeout = TimeSpan.FromMinutes(timeoutMinutes);
            _captureBaseFolder = captureSessions && settings != null
                ? settings.ImageDownloadBaseFolder
                : null;
        }

        public static async Task<GrokWebImagineEditGenerator> CreateAsync(
            GrokWebClient client,
            string inputImagePath,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool pro = true,
            string aspectRatio = "",
            bool enableSideBySide = true,
            Settings? settings = null,
            bool captureSessions = false)
        {
            var uploaded = await client.UploadImageAsync(inputImagePath);
            // With shape=auto (empty AR) the edit should keep the source image's
            // shape; derive the nearest supported AR from the file itself.
            var resolvedAspect = string.IsNullOrWhiteSpace(aspectRatio)
                ? DeriveAspectRatio(inputImagePath)
                : aspectRatio;
            return new GrokWebImagineEditGenerator(
                client, uploaded.MediaUrl, maxConcurrency, stats,
                pro: pro, aspectRatio: resolvedAspect, enableSideBySide: enableSideBySide,
                settings: settings, captureSessions: captureSessions);
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var tier = _enablePro ? "grokweb-edit-pro" : "grokweb-edit";
            var ar = _aspectRatio.Replace(':', 'x');
            return $"{tier}_{ar}";
        }

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                _enablePro ? "Grok Web Imagine Edit Pro" : "Grok Web Imagine Edit",
                "imagine-x-1",
                "grok.com/ws/imagine/listen",
                $"AR {_aspectRatio}",
                _enableSideBySide ? "side-by-side" : "single",
            };
        }

        public string GetGeneratorSpecPart()
        {
            var line = _enablePro ? "Grok Web Imagine Edit Pro" : "Grok Web Imagine Edit";
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
                Logger.Log($"\t-> Grok Web Edit AR={_aspectRatio} pro={_enablePro}: {prompt}");

                var result = await _client.GenerateImageAsync(
                    prompt,
                    _aspectRatio,
                    _enablePro,
                    _enableSideBySide,
                    _timeout,
                    _captureBaseFolder,
                    imageReferenceUrl: _imageReferenceUrl);

                sw.Stop();
                if (result.Images.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail("Grok web edit returned no images.", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                _stats.GrokImageGenerationSuccessCount++;
                Logger.Log($"\t<- Grok Web Edit OK in {sw.ElapsedMilliseconds} ms; {result.Images.Count} image(s) model={result.ModelName ?? "?"} mode={result.Mode ?? "?"}");

                var images = result.Images
                    .Select(bytes => new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    })
                    .ToList();

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = GuessContentTypeFromBytes(result.Images[0]),
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                var detail = ex is GrokWebException gwe && !string.IsNullOrEmpty(gwe.ResponseBody)
                    ? $"{ex.Message} body={Truncate(gwe.ResponseBody, 400)}"
                    : ex.Message;
                Logger.Log($"\t<- Grok Web Edit FAIL: {detail}");
                return Fail(detail, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // Grok's imagine WS wants an aspect_ratio string. Invalid or unreadable
        // input is a hard error because silently substituting a square can change
        // the requested composition.
        internal static string DeriveAspectRatio(string imagePath)
        {
            var info = Image.Identify(imagePath);
            if (info == null || info.Width <= 0 || info.Height <= 0)
            {
                throw new InvalidDataException(
                    $"Could not read source image dimensions from '{imagePath}'.");
            }
            return UiShapeMapping.GrokAspectForInput(info.Width, info.Height);
        }

        private static string GuessContentTypeFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            return "image/jpeg";
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        private TaskProcessResult Fail(string message, PromptDetails pd, IImageGenerator generator, long elapsedMs)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
        }
    }
}
