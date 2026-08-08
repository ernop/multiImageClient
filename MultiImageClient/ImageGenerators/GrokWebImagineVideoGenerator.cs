#nullable enable
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GrokWebImagineVideoGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly Settings _settings;
        private readonly GrokWebAsset? _sourceAsset;
        private readonly string _aspectRatio;
        private readonly string _resolution;
        private readonly int _durationSeconds;
        private readonly bool _enableSideBySide;
        private readonly string _videoMode;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _pollTimeout;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineVideo;

        public GrokWebImagineVideoGenerator(
            GrokWebClient client,
            Settings settings,
            MultiClientRunStats stats,
            int maxConcurrency,
            GrokWebAsset? sourceAsset,
            string aspectRatio = "2:3",
            string resolution = "480p",
            int durationSeconds = 15,
            bool enableSideBySide = true,
            string videoMode = "normal",
            int pollSeconds = 5,
            int pollTimeoutSeconds = 0)
        {
            _client = client;
            _settings = settings;
            _stats = stats;
            _sourceAsset = sourceAsset;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _aspectRatio = aspectRatio;
            _resolution = resolution;
            _durationSeconds = durationSeconds;
            _enableSideBySide = enableSideBySide;
            _videoMode = GrokWebClient.NormalizeVideoMode(videoMode);
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
            _pollTimeout = TimeSpan.FromSeconds(
                pollTimeoutSeconds > 0
                    ? pollTimeoutSeconds
                    : Math.Max(30, settings.GrokWebVideoPollTimeoutSeconds));
        }

        public static async Task<GrokWebImagineVideoGenerator> CreateFromImageAsync(
            GrokWebClient client,
            Settings settings,
            MultiClientRunStats stats,
            string inputImagePath,
            int maxConcurrency,
            string aspectRatio,
            string resolution,
            int durationSeconds,
            bool enableSideBySide,
            string videoMode = "normal")
        {
            var uploaded = await client.UploadImageAsync(inputImagePath);
            var post = await client.CreateImagePostAsync(uploaded.MediaUrl);
            var asset = new GrokWebAsset
            {
                AssetId = uploaded.AssetId,
                MediaUrl = uploaded.MediaUrl,
                PostId = post.PostId ?? post.AssetId,
            };
            return new GrokWebImagineVideoGenerator(
                client, settings, stats, maxConcurrency, asset,
                aspectRatio, resolution, durationSeconds, enableSideBySide, videoMode);
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var ar = _aspectRatio.Replace(':', 'x');
            return $"grokweb-video_{ar}_{_resolution}_{_durationSeconds}s";
        }

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "Grok Web Imagine Video",
                "imagine-video-gen",
                "grok.com app-chat",
                $"AR {_aspectRatio}",
                _resolution,
                $"{_durationSeconds}s",
                $"mode {_videoMode}",
            };
        }

        public string GetGeneratorSpecPart()
            => $"Grok Web Imagine Video  {_aspectRatio}  {_resolution}  {_durationSeconds}s  {_videoMode}";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokVideoGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                Logger.Log($"\t-> Grok Web Video AR={_aspectRatio} res={_resolution} dur={_durationSeconds}s method={_videoMode}: {prompt}");

                string parentPostId;
                if (_sourceAsset != null)
                {
                    parentPostId = _sourceAsset.PostId ?? _sourceAsset.AssetId;
                }
                else
                {
                    parentPostId = await _client.CreateVideoPostPlaceholderAsync(prompt);
                }

                var chat = await _client.RunVideoGenerationAsync(
                    prompt,
                    parentPostId,
                    _aspectRatio,
                    _durationSeconds,
                    _resolution,
                    _sourceAsset,
                    _enableSideBySide,
                    _videoMode);

                var videoUrl = chat.GeneratedVideoUrls.Count > 0 ? chat.GeneratedVideoUrls[0] : null;
                if (!string.IsNullOrWhiteSpace(chat.ErrorMessage))
                {
                    _stats.GrokVideoGenerationErrorCount++;
                    return Fail(
                        $"Grok web video generation failed: {chat.ErrorMessage}",
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }
                if (string.IsNullOrEmpty(videoUrl))
                {
                    var pollResult = await _client.PollForVideoResultAsync(
                        parentPostId,
                        _pollInterval,
                        _pollTimeout);
                    videoUrl = pollResult.VideoUrl;
                    if (string.IsNullOrEmpty(videoUrl)
                        && !string.IsNullOrWhiteSpace(pollResult.ErrorMessage))
                    {
                        _stats.GrokVideoGenerationErrorCount++;
                        return Fail(
                            pollResult.ErrorMessage,
                            promptDetails,
                            generator,
                            sw.ElapsedMilliseconds);
                    }
                }

                if (string.IsNullOrEmpty(videoUrl))
                {
                    _stats.GrokVideoGenerationErrorCount++;
                    var hint = string.IsNullOrWhiteSpace(chat.ModelMessage) ? "no mp4 URL" : chat.ModelMessage;
                    return Fail($"Grok web video completed without a downloadable mp4 ({hint}).", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                var mp4Bytes = await _client.DownloadBytesAsync(videoUrl, expectVideo: true);
                var mp4Path = SaveVideo(mp4Bytes, prompt);
                DlMirror.Copy(mp4Path, _settings.FlatImageMirrorPath);

                sw.Stop();
                _stats.GrokVideoGenerationSuccessCount++;
                Logger.Log($"\t<- Grok Web Video OK in {sw.ElapsedMilliseconds} ms; {mp4Bytes.Length / 1024} KB -> {mp4Path}");

                var card = RenderVideoCard(prompt, mp4Path, _durationSeconds, mp4Bytes.Length);
                var processResult = new TaskProcessResult
                {
                    IsSuccess = true,
                    ContentType = "image/png",
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                    GeneratedMediaPath = mp4Path,
                    GeneratedMediaContentType = "video/mp4",
                };
                processResult.SetImageBytes(0, card);
                return processResult;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokVideoGenerationErrorCount++;
                Logger.Log($"\t<- Grok Web Video FAIL: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private string SaveVideo(byte[] mp4Bytes, string prompt)
        {
            var dayFolder = Path.Combine(_settings.ImageDownloadBaseFolder, DateTime.Now.ToString("yyyy-MM-dd-dddd"));
            var videoFolder = Path.Combine(dayFolder, "Video");
            Directory.CreateDirectory(videoFolder);

            var stem = FilenameGenerator.SanitizeFilename(
                $"{DateTime.Now:yyyyMMddHHmmss}_{GetFilenamePart(null!)}_{FilenameGenerator.TruncatePrompt(prompt, 90)}");
            var path = Path.Combine(videoFolder, $"{stem}.mp4");
            var count = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(videoFolder, $"{stem}_{count:D4}.mp4");
                count++;
            }

            File.WriteAllBytes(path, mp4Bytes);
            return path;
        }

        private byte[] RenderVideoCard(string prompt, string mp4Path, int durationSeconds, long sizeBytes)
        {
            const int width = 1024;
            const int height = 576;

            using var image = ImageUtils.CreateStandardImage(width, height, UIConstants.White);
            var titleFont = ImageUtils.AutoSizeFont("VIDEO — Grok Web Imagine", width, 34, 16, FontStyle.Bold);
            var bodyFont = FontUtils.CreateFont(22, FontStyle.Regular);
            var pathFont = FontUtils.CreateFont(15, FontStyle.Regular);

            image.Mutate(ctx =>
            {
                ctx.ApplyStandardGraphicsOptions();
                ctx.DrawTextWithBackground(new RectangleF(0, 0, width, 70),
                    "VIDEO — Grok Web Imagine", titleFont, UIConstants.Black, UIConstants.Gold);
                var facts = $"imagine-video-gen   {_aspectRatio}   {_resolution}   {durationSeconds}s   {_videoMode}   {sizeBytes / 1024} KB";
                ctx.DrawTextWithBackground(new RectangleF(0, 90, width, 50),
                    facts, bodyFont, UIConstants.Black, UIConstants.White);
                ctx.DrawTextWithBackground(new RectangleF(0, 160, width, 230),
                    Truncate(prompt, 320), bodyFont, UIConstants.Black, UIConstants.White,
                    SixLabors.Fonts.HorizontalAlignment.Left);
                ctx.DrawTextWithBackground(new RectangleF(0, height - 160, width, 150),
                    $"saved to:\n{mp4Path}", pathFont, UIConstants.SuccessGreen, UIConstants.White,
                    SixLabors.Fonts.HorizontalAlignment.Left);
            });

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
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

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s.Substring(0, max) + "...";
    }
}
