#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GrokWebImagineEditGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly GrokWebAsset _sourceAsset;
        private readonly bool _enableSideBySide;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _pollTimeout;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineEdit;

        public GrokWebImagineEditGenerator(
            GrokWebClient client,
            GrokWebAsset sourceAsset,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool enableSideBySide = true,
            int pollSeconds = 5,
            int timeoutMinutes = 10)
        {
            _client = client;
            _sourceAsset = sourceAsset;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _enableSideBySide = enableSideBySide;
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
            _pollTimeout = TimeSpan.FromMinutes(timeoutMinutes);
        }

        public static async Task<GrokWebImagineEditGenerator> CreateAsync(
            GrokWebClient client,
            string inputImagePath,
            int maxConcurrency,
            MultiClientRunStats stats,
            bool enableSideBySide = true)
        {
            var uploaded = await client.UploadImageAsync(inputImagePath);
            var post = await client.CreateImagePostAsync(uploaded.MediaUrl);
            var asset = new GrokWebAsset
            {
                AssetId = uploaded.AssetId,
                MediaUrl = uploaded.MediaUrl,
                PostId = post.PostId ?? post.AssetId,
            };
            return new GrokWebImagineEditGenerator(client, asset, maxConcurrency, stats, enableSideBySide);
        }

        public string GetFilenamePart(PromptDetails pd) => "grokweb-edit";

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "Grok Web Imagine Edit",
                "imagine-image-edit",
                "grok.com/rest/app-chat/conversations/new",
                _enableSideBySide ? "side-by-side" : "single",
            };
        }

        public string GetGeneratorSpecPart()
            => _enableSideBySide ? "Grok Web Imagine Edit  side-by-side" : "Grok Web Imagine Edit";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                Logger.Log($"\t-> Grok Web Edit parent={_sourceAsset.PostId}: {prompt}");

                var chat = await _client.RunImageEditAsync(prompt, _sourceAsset, _enableSideBySide);
                var urls = chat.GeneratedImageUrls;
                if (urls.Count == 0)
                {
                    urls = await _client.PollForImageUrlsAsync(_sourceAsset.PostId ?? _sourceAsset.AssetId, _pollInterval, _pollTimeout);
                }

                if (urls.Count == 0)
                {
                    _stats.GrokImageGenerationErrorCount++;
                    var hint = string.IsNullOrWhiteSpace(chat.ModelMessage) ? "no image URLs" : chat.ModelMessage;
                    return Fail($"Grok web edit completed without downloadable images ({hint}).", promptDetails, generator, sw.ElapsedMilliseconds);
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
                    ContentType = GuessContentType(urls[0]),
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
                // The server's response body is where the actual reason lives
                // (moderation verdict, Cloudflare block, etc).
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

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        private static string GuessContentType(string url)
        {
            var lower = url.ToLowerInvariant();
            if (lower.Contains(".webp")) return "image/webp";
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            return "image/png";
        }

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
