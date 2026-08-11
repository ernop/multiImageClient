#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// Expires old disk card thumbs (UiHistory/{id}/thumbs/). Thumbs are pure
    /// derived data ONLY when their source is still obtainable — the local
    /// original file, or (with B2 hosting) the exact recorded hosted object,
    /// from which the serving route rebuilds a missing thumb on demand.
    /// Thumbs whose source is gone (pre-B2 images whose raws were deleted in
    /// old disk-pressure cleanups) are the last remaining visual for their
    /// cards and are never deleted. Regeneration refreshes the file's write
    /// time, so recently viewed archive days stay warm.
    /// </summary>
    public static class UiThumbExpiry
    {
        private static readonly TimeSpan MaxThumbAge = TimeSpan.FromDays(2);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

        public static async Task RunLoopAsync(Settings settings, CancellationToken token)
        {
            try
            {
                // Let startup hydration and the first page loads win the disk.
                await Task.Delay(StartupDelay, token);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        SweepOnce(settings);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"thumb expiry: sweep failed: {ex.Message}");
                    }
                    await Task.Delay(SweepInterval, token);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        public static void SweepOnce(Settings settings)
        {
            var root = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            if (!Directory.Exists(root))
            {
                return;
            }
            var cutoff = DateTime.UtcNow - MaxThumbAge;
            int deleted = 0, keptIrreplaceable = 0;
            long freedBytes = 0;

            foreach (var jobFolder in Directory.EnumerateDirectories(root))
            {
                var thumbsFolder = Path.Combine(jobFolder, "thumbs");
                if (!Directory.Exists(thumbsFolder))
                {
                    continue;
                }
                JsonDocument? images = null;
                try
                {
                    foreach (var thumbPath in Directory.EnumerateFiles(thumbsFolder))
                    {
                        var fileInfo = new FileInfo(thumbPath);
                        if (fileInfo.LastWriteTimeUtc >= cutoff)
                        {
                            continue;
                        }
                        if (!TryThumbFileToImageKey(fileInfo.Name, out var imageKey))
                        {
                            continue; // unknown naming — never delete what we can't map
                        }
                        images ??= LoadImagesJson(jobFolder);
                        if (images == null
                            || !IsRegenerable(images, imageKey, settings.EnableB2ImageHosting))
                        {
                            keptIrreplaceable++;
                            continue;
                        }
                        try
                        {
                            var size = fileInfo.Length;
                            File.Delete(thumbPath);
                            deleted++;
                            freedBytes += size;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"thumb expiry: could not delete {thumbPath}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    images?.Dispose();
                }
            }

            if (deleted > 0 || keptIrreplaceable > 0)
            {
                Logger.Log(
                    $"thumb expiry: deleted {deleted} thumb(s) ({freedBytes / (1024.0 * 1024):F1} MiB) older than {MaxThumbAge.TotalDays:F0} days; "
                    + $"kept {keptIrreplaceable} whose source is no longer obtainable.");
            }
        }

        /// "gpt2~p0_0.jpg" -> "gpt2~p0/0". The last underscore separates the
        /// generator key from the image index (generator keys never contain
        /// underscores; ThumbFileName maps '/' to '_').
        public static bool TryThumbFileToImageKey(string fileName, out string imageKey)
        {
            imageKey = "";
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var split = stem.LastIndexOf('_');
            if (split <= 0 || split == stem.Length - 1
                || !int.TryParse(stem.Substring(split + 1), out _))
            {
                return false;
            }
            imageKey = stem.Substring(0, split) + "/" + stem.Substring(split + 1);
            return true;
        }

        private static JsonDocument? LoadImagesJson(string jobFolder)
        {
            var path = Path.Combine(jobFolder, "images.json");
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Logger.Log($"thumb expiry: could not parse {path}: {ex.Message}");
                return null;
            }
        }

        private static bool IsRegenerable(JsonDocument images, string imageKey, bool b2HostingEnabled)
        {
            if (!images.RootElement.TryGetProperty(imageKey, out var record))
            {
                return false;
            }
            var localPath = record.TryGetProperty("Path", out var pathProp)
                ? pathProp.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                return true;
            }
            if (!b2HostingEnabled)
            {
                return false;
            }
            var cdnKey = record.TryGetProperty("CdnKey", out var cdnProp) ? cdnProp.GetString() : null;
            var sha = record.TryGetProperty("ContentSha256", out var shaProp) ? shaProp.GetString() : null;
            return !string.IsNullOrWhiteSpace(cdnKey) && !string.IsNullOrWhiteSpace(sha);
        }
    }
}
