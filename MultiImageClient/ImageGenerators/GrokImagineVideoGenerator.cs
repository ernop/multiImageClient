#nullable enable
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using XAIGrokAPIClient;

namespace MultiImageClient
{
    /// VIDEO generator backed by xAI's asynchronous /v1/videos/generations
    /// endpoint (grok-imagine-video). Dispatches exactly like any image
    /// generator — it implements IImageGenerator and can sit in the same
    /// multiplex list — but the deliverable is an mp4, which does not fit
    /// the PNG-only save pipeline. So this generator:
    ///
    ///   1. starts the request, polls until done (5s cadence, bounded),
    ///   2. downloads the mp4 itself into {ImageDownloadBaseFolder}\{day}\Video\
    ///      (xAI's URLs are temporary, so we never rely on them later),
    ///   3. mirrors the clip via DlMirror,
    ///   4. renders a PNG "video card" (what/where/how long) and hands THAT
    ///      to the combined-grid pipeline so contact sheets stay all-PNG.
    ///
    /// Pricing (xAI, 2026-06, approximate): per-second billing, roughly
    /// $0.05/s at 480p and $0.10/s at 720p. GetCost() reflects that estimate.
    public class GrokImagineVideoGenerator : IImageGenerator
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly XAIGrokClient _client;
        private readonly HttpClient _httpClient;
        private readonly MultiClientRunStats _stats;
        private readonly Settings _settings;
        private readonly string _name;
        private readonly string _aspectRatio;
        private readonly string _resolution;
        private readonly int _durationSeconds;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _timeout;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokImagineVideo;

        /// durationSeconds — xAI range [1, 15]; default 6 keeps cost and
        ///   poll time low for smoke tests.
        /// resolution      — "480p" (default tier, fastest) | "720p" | "1080p".
        public GrokImagineVideoGenerator(
            string apiKey,
            int maxConcurrency,
            MultiClientRunStats stats,
            Settings settings,
            string name = "",
            string aspectRatio = "16:9",
            string resolution = "480p",
            int durationSeconds = 6,
            int pollSeconds = 5,
            int timeoutMinutes = 10)
        {
            if (durationSeconds < 1 || durationSeconds > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(durationSeconds), "xAI video duration must be 1-15 seconds.");
            }
            _client = new XAIGrokClient(apiKey);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _settings = settings;
            _name = name ?? string.Empty;
            _aspectRatio = aspectRatio;
            _resolution = resolution;
            _durationSeconds = durationSeconds;
            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
            _timeout = TimeSpan.FromMinutes(timeoutMinutes);
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var arLabel = string.IsNullOrWhiteSpace(_aspectRatio) ? "auto" : _aspectRatio.Replace(':', 'x');
            var nameLabel = string.IsNullOrEmpty(_name) ? "" : $"_{_name}";
            return $"grok-video_{arLabel}_{_resolution}_{_durationSeconds}s{nameLabel}";
        }

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                "xAI Grok Imagine Video",
                XAIGrokClient.ModelGrokImagineVideo,
                $"AR {_aspectRatio}",
                _resolution,
                $"{_durationSeconds}s",
            };
            if (!string.IsNullOrEmpty(_name)) parts.Add(_name);
            return parts;
        }

        public string GetGeneratorSpecPart()
        {
            var line = $"xAI Grok Imagine Video  {_aspectRatio}  {_resolution}  {_durationSeconds}s";
            if (!string.IsNullOrEmpty(_name)) line += $"  [{_name}]";
            return line;
        }

        public decimal GetCost()
        {
            var perSecond = string.Equals(_resolution, "720p", StringComparison.OrdinalIgnoreCase) ? 0.10m
                          : string.Equals(_resolution, "1080p", StringComparison.OrdinalIgnoreCase) ? 0.20m
                          : 0.05m;
            return perSecond * _durationSeconds;
        }

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokVideoGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;

                // storage_options => xAI keeps a durable, never-expiring copy in
                // the team's Files API store. That's the only server-side
                // history xAI exposes, so it's what makes `--grok-sync`
                // able to re-download every clip later, from any machine.
                var storedFilename = FilenameGenerator.SanitizeFilename(
                    $"{DateTime.UtcNow:yyyyMMddHHmmss}_{GetFilenamePart(null!)}_{FilenameGenerator.TruncatePrompt(prompt, 80)}") + ".mp4";
                var req = new XAIGrokVideoGenerateRequest
                {
                    Prompt = prompt,
                    Model = XAIGrokClient.ModelGrokImagineVideo,
                    Duration = _durationSeconds,
                    AspectRatio = string.IsNullOrWhiteSpace(_aspectRatio) ? null : _aspectRatio,
                    Resolution = string.IsNullOrWhiteSpace(_resolution) ? null : _resolution,
                    StorageOptions = new XAIGrokVideoStorageOptions { Filename = storedFilename },
                };

                Logger.Log($"\t-> Grok Video [{XAIGrokClient.ModelGrokImagineVideo}] AR={_aspectRatio} res={_resolution} dur={_durationSeconds}s: {prompt}");
                var result = await _client.GenerateVideoAsync(req, _pollInterval, _timeout);
                sw.Stop();

                var hasUrl = !string.IsNullOrEmpty(result.Video?.Url);
                var hasStoredFile = !string.IsNullOrEmpty(result.Video?.FileOutput?.FileId);
                if (!result.IsDone || (!hasUrl && !hasStoredFile))
                {
                    _stats.GrokVideoGenerationErrorCount++;
                    var detail = result.Error != null
                        ? $"{result.Error.Code}: {result.Error.Message}"
                        : $"status={result.Status}";
                    Logger.Log($"\t<- Grok Video FAIL {detail}");
                    return Fail($"Grok video did not complete ({detail}).", promptDetails, generator, sw.ElapsedMilliseconds);
                }

                // xAI's direct video URLs are temporary — download immediately.
                // With storage_options set we may instead (or also) get a durable
                // Files API copy; prefer the temp URL, fall back to the file.
                var mp4Bytes = hasUrl
                    ? await _httpClient.GetByteArrayAsync(result.Video!.Url)
                    : await _client.DownloadFileContentAsync(result.Video!.FileOutput!.FileId);
                var mp4Path = SaveVideo(mp4Bytes, prompt);
                DlMirror.Copy(mp4Path, _settings.FlatImageMirrorPath);

                _stats.GrokVideoGenerationSuccessCount++;
                var actualDuration = result.Video.Duration ?? _durationSeconds;
                Logger.Log($"\t<- Grok Video OK in {sw.ElapsedMilliseconds} ms; {mp4Bytes.Length / 1024} KB, {actualDuration}s -> {mp4Path}");

                GrokLedger.Append(_settings, new GrokLedgerEntry
                {
                    Kind = "video",
                    Model = XAIGrokClient.ModelGrokImagineVideo,
                    Prompt = prompt,
                    RequestId = result.RequestId,
                    FileId = result.Video.FileOutput?.FileId,
                    RemoteUrl = result.Video.Url,
                    LocalPath = mp4Path,
                    Bytes = mp4Bytes.Length,
                    Source = "live",
                });

                // PNG stand-in for the contact-sheet pipeline. The mp4 itself
                // is already safe on disk; this card is just how a video shows
                // up alongside the stills.
                var card = RenderVideoCard(prompt, mp4Path, actualDuration, mp4Bytes.Length);

                var processResult = new TaskProcessResult
                {
                    IsSuccess = true,
                    ContentType = "image/png",
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
                processResult.SetImageBytes(0, card);
                return processResult;
            }
            catch (XAIGrokException ex)
            {
                sw.Stop();
                _stats.GrokVideoGenerationErrorCount++;
                Logger.Log($"\t<- Grok Video FAIL http={ex.StatusCode}: {Truncate(ex.ResponseBody, 500)}");
                return Fail($"{ex.StatusCode}: {Truncate(ex.ResponseBody, 300)}", promptDetails, generator, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokVideoGenerationErrorCount++;
                Logger.Log($"\t<- Grok Video EXCEPTION: {ex.Message}");
                return Fail(ex.Message, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
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

        /// Writes the mp4 under {ImageDownloadBaseFolder}\{yyyy-MM-dd-dddd}\Video\
        /// (same day-folder convention as image saves) and returns the path.
        private string SaveVideo(byte[] mp4Bytes, string prompt)
        {
            var dayFolder = Path.Combine(_settings.ImageDownloadBaseFolder, DateTime.Now.ToString("yyyy-MM-dd-dddd"));
            var videoFolder = Path.Combine(dayFolder, "Video");
            Directory.CreateDirectory(videoFolder);

            var stem = FilenameGenerator.SanitizeFilename(
                $"{DateTime.Now:yyyyMMddHHmmss}_{GetFilenamePart(null!)}_{FilenameGenerator.TruncatePrompt(prompt, 90)}");
            var path = Path.Combine(videoFolder, $"{stem}.mp4");
            int count = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(videoFolder, $"{stem}_{count:D4}.mp4");
                count++;
            }
            File.WriteAllBytes(path, mp4Bytes);
            return path;
        }

        /// Renders the PNG "video card" cell used in combined grids: a film-
        /// style header band, the key facts in black, and the saved path so a
        /// viewer of the sheet can find the actual clip.
        private byte[] RenderVideoCard(string prompt, string mp4Path, int durationSeconds, long sizeBytes)
        {
            const int width = 1024;
            const int height = 576; // 16:9 to telegraph "this one is a video"

            using var image = ImageUtils.CreateStandardImage(width, height, UIConstants.White);
            // Title is auto-sized to guarantee a single line inside the gold
            // band; everything below is wrapped, with the band heights sized
            // generously so wrapped lines never spill into the next section.
            var titleFont = ImageUtils.AutoSizeFont("VIDEO — xAI Grok Imagine", width, 34, 16, FontStyle.Bold);
            var bodyFont = FontUtils.CreateFont(22, FontStyle.Regular);
            var pathFont = FontUtils.CreateFont(15, FontStyle.Regular);

            image.Mutate(ctx =>
            {
                ctx.ApplyStandardGraphicsOptions();

                // Gold header band — film-leader feel, and gold is one of the
                // approved semantic colors (see AGENTS.md typography policy).
                ctx.DrawTextWithBackground(new RectangleF(0, 0, width, 70),
                    "VIDEO — xAI Grok Imagine", titleFont, UIConstants.Black, UIConstants.Gold);

                var facts = $"{XAIGrokClient.ModelGrokImagineVideo}   {_aspectRatio}   {_resolution}   {durationSeconds}s   {sizeBytes / 1024} KB";
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

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
