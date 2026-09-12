#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// Expires disk card thumbs after two days, including orphaned previews.
    /// The owner approved removing previews without originals on 2026-09-12.
    /// Unknown image identities remain untouched. Regeneration refreshes age.
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

        public static (int Files, long Bytes) SweepOnce(Settings settings, bool dryRun = false)
        {
            var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ImageDownloadBaseFolder));
            var root = Path.Combine(dataRoot, "UiHistory");
            if (!Directory.Exists(root))
            {
                return (0, 0);
            }
            UiHostedRawCleanup.RequireContainedPath(dataRoot, root);
            var cutoff = DateTime.UtcNow - MaxThumbAge;
            int deleted = 0, keptUnknown = 0;
            long freedBytes = 0;

            foreach (var jobFolder in Directory.EnumerateDirectories(root))
            {
                if ((File.GetAttributes(jobFolder) & FileAttributes.ReparsePoint) != 0) continue;
                var thumbsFolder = Path.Combine(jobFolder, "thumbs");
                if (!Directory.Exists(thumbsFolder))
                {
                    continue;
                }
                if ((File.GetAttributes(thumbsFolder) & FileAttributes.ReparsePoint) != 0) continue;
                JsonDocument? images = null;
                try
                {
                    foreach (var thumbPath in Directory.EnumerateFiles(thumbsFolder))
                    {
                        var fileInfo = new FileInfo(thumbPath);
                        if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (fileInfo.LastWriteTimeUtc >= cutoff)
                        {
                            continue;
                        }
                        if (!TryThumbFileToImageKey(fileInfo.Name, out var imageKey))
                        {
                            continue; // unknown naming — never delete what we can't map
                        }
                        images ??= LoadImagesJson(jobFolder);
                        if (images == null || images.RootElement.ValueKind != JsonValueKind.Object
                            || !images.RootElement.TryGetProperty(imageKey, out var record)
                            || record.ValueKind != JsonValueKind.Object)
                        {
                            keptUnknown++;
                            continue;
                        }
                        try
                        {
                            var size = fileInfo.Length;
                            if (!dryRun) File.Delete(thumbPath);
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

            if (deleted > 0 || keptUnknown > 0)
            {
                Logger.Log(
                    $"thumb expiry: {(dryRun ? "candidates" : "deleted")} {deleted} thumb(s) ({freedBytes / (1024.0 * 1024):F1} MiB) older than {MaxThumbAge.TotalDays:F0} days; "
                    + $"kept {keptUnknown} with unknown identities.");
            }
            return (deleted, freedBytes);
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
                || !int.TryParse(stem.Substring(split + 1), out var index) || index < 0)
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
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
                using var stream = File.OpenRead(path);
                return JsonDocument.Parse(stream);
            }
            catch (Exception ex)
            {
                Logger.Log($"thumb expiry: could not parse {path}: {ex.Message}");
                return null;
            }
        }

    }
}
