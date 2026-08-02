#nullable enable
using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Edits an existing image through the consumer grok.com app-chat path
    /// (imagine-image-edit), using the same Playwright integrity-signed
    /// transport as image-to-video. The source is uploaded, posted, then
    /// referenced as mediaGenInput.imageToImage.inputAssets=[assetId].
    ///
    /// Do not use wss://grok.com/ws/imagine/listen with properties.image_uri
    /// for edits: that transport accepts the field but ignores the source
    /// image and invents a new scene from the prompt alone (observed
    /// 2026-07-31). Standalone HTTP to /rest/app-chat/conversations/new
    /// still 403s without a real Edit-button click that attaches x-statsig-id.
    public class GrokWebImagineEditGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly GrokWebAsset _sourceAsset;
        private readonly bool _enablePro;
        private readonly string _aspectRatio;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _pollTimeout;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineEdit;

        public GrokWebImagineEditGenerator(
            GrokWebClient client,
            GrokWebAsset sourceAsset,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool pro = true,
            string aspectRatio = "1:1",
            int pollSeconds = 5,
            int timeoutMinutes = 10)
        {
            _client = client;
            _sourceAsset = sourceAsset;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _enablePro = pro;
            _aspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "1:1" : aspectRatio;
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
            _pollTimeout = TimeSpan.FromMinutes(timeoutMinutes);
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
            // enableSideBySide / captureSessions kept on the signature so UI/CLI
            // call sites compile; the live Edit control currently returns one
            // image and capture is unused on the app-chat path.
            _ = enableSideBySide;
            _ = settings;
            _ = captureSessions;

            var uploaded = await client.UploadImageAsync(inputImagePath);
            var post = await client.CreateImagePostAsync(uploaded.MediaUrl);
            var asset = new GrokWebAsset
            {
                AssetId = uploaded.AssetId,
                MediaUrl = uploaded.MediaUrl,
                PostId = post.PostId ?? post.AssetId,
            };
            var resolvedAspect = string.IsNullOrWhiteSpace(aspectRatio)
                ? DeriveAspectRatio(inputImagePath)
                : aspectRatio;
            return new GrokWebImagineEditGenerator(
                client, asset, maxConcurrency, stats,
                pro: pro, aspectRatio: resolvedAspect);
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
                "imagine-image-edit",
                "grok.com browser app-chat",
                $"AR {_aspectRatio}",
            };
        }

        public string GetGeneratorSpecPart()
        {
            var line = _enablePro ? "Grok Web Imagine Edit Pro" : "Grok Web Imagine Edit";
            line += $"  {_aspectRatio}";
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
                var parentPostId = _sourceAsset.PostId ?? _sourceAsset.AssetId;
                Logger.Log($"\t-> Grok Web Edit parent={parentPostId} AR={_aspectRatio} pro={_enablePro}: {prompt}");

                var chat = await _client.RunImageEditAsync(prompt, _sourceAsset);
                if (!string.IsNullOrWhiteSpace(chat.ErrorMessage))
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail(
                        $"Grok web edit failed: {chat.ErrorMessage}",
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }

                var urls = chat.GeneratedImageUrls;
                if (urls.Count == 0)
                {
                    urls = await _client.PollForImageUrlsAsync(
                        parentPostId,
                        _pollInterval,
                        _pollTimeout);
                }

                if (urls.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    var hint = string.IsNullOrWhiteSpace(chat.ModelMessage) ? "no image URLs" : chat.ModelMessage;
                    return Fail(
                        $"Grok web edit completed without downloadable images ({hint}).",
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }

                var images = new List<CreatedBase64Image>();
                foreach (var url in urls)
                {
                    var bytes = await _client.DownloadBytesAsync(url);
                    images.Add(new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    });
                }

                sw.Stop();
                _stats.GrokImageGenerationSuccessCount++;
                Logger.Log($"\t<- Grok Web Edit OK in {sw.ElapsedMilliseconds} ms; {images.Count} image(s)");

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = GuessContentType(urls[0], images[0].bytesBase64),
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

        private static string GuessContentType(string url, string b64)
        {
            var lower = url.ToLowerInvariant();
            if (lower.Contains(".webp")) return "image/webp";
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            try
            {
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length >= 8
                    && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                {
                    return "image/png";
                }
            }
            catch (FormatException)
            {
                // Fall through to jpeg default for grok asset URLs.
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
