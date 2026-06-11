#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using XAIGrokAPIClient;

namespace MultiImageClient
{
    /// One-shot exerciser for the three Grok video request modes
    /// (--grok-video-test):
    ///
    ///   1. text-to-video   — prompt alone.
    ///   2. image-to-video  — generate a still with grok-imagine-image first,
    ///                        then animate it (the image becomes frame one).
    ///   3. extend-video    — POST /v1/videos/extensions on the clip from
    ///                        step 2; xAI returns original + extension as one
    ///                        combined mp4.
    ///
    /// Every clip is requested with storage_options (durable Files-API copy,
    /// so --grok-sync can always recover it), saved under the day folder's
    /// Video\ subfolder, mirrored, and recorded in grok_ledger.jsonl.
    /// Videos are deliberately short (3s) and 480p to keep the test cheap
    /// (~$0.15 per clip + $0.02 for the still).
    public class GrokVideoModesWorkflow
    {
        private const int DurationSeconds = 3;
        private const string Resolution = "480p";
        private const string AspectRatio = "16:9";

        public async Task RunAsync(AbstractPromptSource promptSource, Settings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.XAIGrokApiKey))
            {
                Logger.Log("Grok video test: settings.json has no XAIGrokApiKey; aborting.");
                return;
            }

            var prompt = promptSource.Prompts
                .Select(p => p.Prompt)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                ?? "A red kite surfing wind gusts above a green coastal cliff, bright daylight";

            var client = new XAIGrokClient(settings.XAIGrokApiKey);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            Logger.Log($"Grok video test: 3 modes x {DurationSeconds}s {Resolution} {AspectRatio}");
            Logger.Log($"  prompt: {prompt}");

            // ---- Mode 1: text -> video --------------------------------
            var textVideo = await RunOneAsync(settings, client, http, "text2video", new XAIGrokVideoGenerateRequest
            {
                Prompt = prompt,
                Model = XAIGrokClient.ModelGrokImagineVideo,
                Duration = DurationSeconds,
                AspectRatio = AspectRatio,
                Resolution = Resolution,
                StorageOptions = MakeStorage("text2video", prompt),
            }, prompt);

            // ---- Mode 2: grok image -> video --------------------------
            string? imageUrl = null;
            try
            {
                Logger.Log($"Grok video test: generating source still via {XAIGrokClient.ModelGrokImagine}...");
                var imgResponse = await client.GenerateAsync(new XAIGrokGenerateRequest
                {
                    Prompt = prompt,
                    Model = XAIGrokClient.ModelGrokImagine,
                    AspectRatio = AspectRatio,
                    Quality = "high",
                    Resolution = "1k",
                    N = 1,
                });
                imageUrl = imgResponse.Data?.FirstOrDefault()?.Url;
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    var imgBytes = await http.GetByteArrayAsync(imageUrl);
                    var imgPath = BuildOutputPath(settings, "img2video_source", prompt, ".png");
                    await File.WriteAllBytesAsync(imgPath, imgBytes);
                    DlMirror.Copy(imgPath, settings.FlatImageMirrorPath);
                    Logger.Log($"Grok video test: source still saved -> {imgPath}");
                    GrokLedger.Append(settings, new GrokLedgerEntry
                    {
                        Kind = "image",
                        Model = XAIGrokClient.ModelGrokImagine,
                        Prompt = prompt,
                        RemoteUrl = imageUrl,
                        LocalPath = imgPath,
                        Bytes = imgBytes.Length,
                        Source = "live",
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Grok video test: source image generation failed: {ex.Message}");
            }

            XAIGrokVideoResult? imageVideo = null;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                imageVideo = await RunOneAsync(settings, client, http, "img2video", new XAIGrokVideoGenerateRequest
                {
                    Prompt = $"Animate this scene naturally: {prompt}",
                    Model = XAIGrokClient.ModelGrokImagineVideo,
                    Duration = DurationSeconds,
                    Resolution = Resolution,
                    // No AspectRatio: image-to-video inherits the still's AR;
                    // overriding it would stretch the source.
                    Image = new XAIGrokVideoImageInput { Url = imageUrl },
                    StorageOptions = MakeStorage("img2video", prompt),
                }, prompt);
            }
            else
            {
                Logger.Log("Grok video test: SKIPPING image-to-video (no source image).");
            }

            // ---- Mode 3: extend the image-video (fall back to the text one) ----
            var baseClip = imageVideo ?? textVideo;
            if (baseClip?.Video != null)
            {
                var extendPrompt = "Continue the scene: the camera slowly pulls back to reveal the wider surroundings";
                var videoInput = !string.IsNullOrEmpty(baseClip.Video.FileOutput?.FileId)
                    ? new XAIGrokVideoVideoInput { FileId = baseClip.Video.FileOutput.FileId }
                    : new XAIGrokVideoVideoInput { Url = baseClip.Video.Url };
                try
                {
                    Logger.Log($"Grok video test: extend-video (+{DurationSeconds}s) using "
                        + (videoInput.FileId != null ? $"file_id {videoInput.FileId}" : "temp url"));
                    var extended = await client.ExtendVideoAsync(new XAIGrokVideoExtendRequest
                    {
                        Prompt = extendPrompt,
                        Model = XAIGrokClient.ModelGrokImagineVideo,
                        Video = videoInput,
                        Duration = DurationSeconds,
                        Resolution = Resolution,
                        StorageOptions = MakeStorage("extended", prompt),
                    });
                    await SaveResultAsync(settings, client, http, "extended", extended, extendPrompt);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Grok video test: extend-video FAILED: {ex.Message}");
                }
            }
            else
            {
                Logger.Log("Grok video test: SKIPPING extend-video (no base clip succeeded).");
            }

            Logger.Log("Grok video test: done. All clips are in the day folder's Video\\ subfolder, "
                + "stored durably at xAI (Files API), and recorded in grok_ledger.jsonl.");
        }

        private async Task<XAIGrokVideoResult?> RunOneAsync(
            Settings settings,
            XAIGrokClient client,
            HttpClient http,
            string modeTag,
            XAIGrokVideoGenerateRequest request,
            string promptForLedger)
        {
            try
            {
                Logger.Log($"Grok video test: {modeTag} starting...");
                var result = await client.GenerateVideoAsync(request, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
                return await SaveResultAsync(settings, client, http, modeTag, result, promptForLedger);
            }
            catch (Exception ex)
            {
                Logger.Log($"Grok video test: {modeTag} FAILED: {ex.Message}");
                return null;
            }
        }

        /// Downloads the finished clip (temp URL preferred, Files-API copy as
        /// fallback), saves + mirrors + ledgers it. Returns the result when
        /// the clip was retrievable, else null.
        private async Task<XAIGrokVideoResult?> SaveResultAsync(
            Settings settings,
            XAIGrokClient client,
            HttpClient http,
            string modeTag,
            XAIGrokVideoResult result,
            string promptForLedger)
        {
            var hasUrl = !string.IsNullOrEmpty(result.Video?.Url);
            var hasFile = !string.IsNullOrEmpty(result.Video?.FileOutput?.FileId);
            if (!result.IsDone || (!hasUrl && !hasFile))
            {
                var detail = result.Error != null ? $"{result.Error.Code}: {result.Error.Message}" : $"status={result.Status}";
                Logger.Log($"Grok video test: {modeTag} did not complete ({detail}).");
                return null;
            }

            var bytes = hasUrl
                ? await http.GetByteArrayAsync(result.Video!.Url)
                : await client.DownloadFileContentAsync(result.Video!.FileOutput!.FileId);

            var path = BuildOutputPath(settings, modeTag, promptForLedger, ".mp4");
            await File.WriteAllBytesAsync(path, bytes);
            DlMirror.Copy(path, settings.FlatImageMirrorPath);
            Logger.Log($"Grok video test: {modeTag} OK — {result.Video.Duration}s, {bytes.Length / 1024} KB -> {path}");

            GrokLedger.Append(settings, new GrokLedgerEntry
            {
                Kind = "video",
                Model = XAIGrokClient.ModelGrokImagineVideo,
                Prompt = promptForLedger,
                RequestId = result.RequestId,
                FileId = result.Video.FileOutput?.FileId,
                RemoteUrl = result.Video.Url,
                LocalPath = path,
                Bytes = bytes.Length,
                Source = "live",
            });
            return result;
        }

        private static XAIGrokVideoStorageOptions MakeStorage(string modeTag, string prompt)
            => new XAIGrokVideoStorageOptions
            {
                Filename = FilenameGenerator.SanitizeFilename(
                    $"{DateTime.UtcNow:yyyyMMddHHmmss}_grokvidtest_{modeTag}_{FilenameGenerator.TruncatePrompt(prompt, 60)}") + ".mp4",
            };

        private static string BuildOutputPath(Settings settings, string modeTag, string prompt, string extension)
        {
            var folder = Path.Combine(settings.ImageDownloadBaseFolder, DateTime.Now.ToString("yyyy-MM-dd-dddd"), "Video");
            Directory.CreateDirectory(folder);
            var stem = FilenameGenerator.SanitizeFilename(
                $"{DateTime.Now:yyyyMMddHHmmss}_grokvidtest_{modeTag}_{FilenameGenerator.TruncatePrompt(prompt, 70)}");
            var path = Path.Combine(folder, stem + extension);
            int count = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, $"{stem}_{count:D4}{extension}");
                count++;
            }
            return path;
        }
    }
}
