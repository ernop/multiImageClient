#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Scans disk records without hydrating the archive or retaining image bytes.
    public static class UiHostedRawCleanup
    {
        private const int MaxInputPaths = 10000;
        private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        public static async Task RunLoopAsync(Settings settings, CancellationToken token)
        {
            if (!settings.EnableB2ImageHosting || settings.B2KeepLocalRawImages) return;
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), token);
                while (!token.IsCancellationRequested)
                {
                    try { await SweepAsync(settings, false, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Log($"hosted raw cleanup: {ex.GetType().Name}; files retained where verification failed.");
                    }
                    await Task.Delay(Interval, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public static async Task<(int Files, long Bytes, int Errors)> SweepAsync(
            Settings settings, bool dryRun, CancellationToken token,
            Func<UiPersistedImageInfo, long, CancellationToken, Task>? verifyHosted = null)
        {
            if (!settings.EnableB2ImageHosting || settings.B2KeepLocalRawImages) return (0, 0, 0);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ImageDownloadBaseFolder));
            var history = Path.Combine(root, "UiHistory");
            if (!Directory.Exists(history)) return (0, 0, 0);
            RequireContainedPath(root, history);
            // Also serializes a manual maintenance command with the server's sweep.
            var lockPath = Path.Combine(root, ".hosted-cleanup.lock");
            if (File.Exists(lockPath)) RequireContainedPath(root, lockPath);
            using var gate = new FileStream(lockPath,
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var inputs = ReadInputPaths(root, history, token);
            var b2 = new B2StorageClient(settings);
            verifyHosted ??= (info, length, ct) => b2.VerifyStoredFileAsync(info, length, ct);
            int files = 0, errors = 0;
            long bytes = 0;
            foreach (var folder in Directory.EnumerateDirectories(history))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var metadataPath = Path.Combine(folder, "job.json");
                    if (!File.Exists(metadataPath)) continue; // Unindexed remnants are not completed jobs.
                    using var metadata = ReadJson(root, metadataPath);
                    if (!IsCompleted(metadata, folder)) continue;
                    var imagesPath = Path.Combine(folder, "images.json");
                    if (!File.Exists(imagesPath)) continue;
                    var images = ReadImages(root, imagesPath);
                    foreach (var info in images)
                    {
                        token.ThrowIfCancellationRequested();
                        if (info.Key.StartsWith("input/", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(info.Path) || !File.Exists(info.Path)
                            || string.IsNullOrWhiteSpace(info.CdnKey)) continue;
                        RequireHostedIdentity(info);
                        var path = Path.GetFullPath(info.Path);
                        RequireContainedPath(root, path);
                        if (inputs.Contains(path)) continue;
                        var original = new FileInfo(path);
                        var length = original.Length;
                        var modified = original.LastWriteTimeUtc;
                        if (length == 0) throw new InvalidDataException("Empty local original.");
                        await using (var stream = File.OpenRead(path))
                        {
                            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                            if (!hash.Equals(info.ContentSha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Local original checksum mismatch.");
                        }
                        // Verify the recorded object version and its complete bytes, including in dry runs.
                        await verifyHosted(info, length, token);
                        using var latest = ReadJson(root, metadataPath);
                        if (!IsCompleted(latest, folder)
                            || !ReadImages(root, imagesPath).Contains(info))
                            throw new InvalidDataException("Job or image identity changed during cleanup.");
                        RequireContainedPath(root, path);
                        original.Refresh();
                        if (!original.Exists) continue;
                        if (original.Length != length || original.LastWriteTimeUtc != modified)
                            throw new InvalidDataException("Local original changed during cleanup.");
                        if (!dryRun) File.Delete(path);
                        files++;
                        bytes += length;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    errors++;
                    Logger.Log($"hosted raw cleanup: job {Path.GetFileName(folder)}: {ex.GetType().Name}; remaining files retained.");
                }
            }
            Logger.Log($"hosted raw cleanup: {(dryRun ? "verified candidates" : "removed")} {files} local file(s), {bytes} bytes; {errors} job error(s).");
            return (files, bytes, errors);
        }

        internal static void RequireHostedIdentity(UiPersistedImageInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.CdnKey) || string.IsNullOrWhiteSpace(info.CdnFileId)
                || info.ContentSha256.Length != 64 || !info.ContentSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Incomplete hosted image identity.");
        }

        private static bool IsCompleted(JsonDocument metadata, string folder)
        {
            if (metadata.RootElement.GetProperty("Id").GetString() != Path.GetFileName(folder))
                throw new InvalidDataException("Job folder identity mismatch.");
            return metadata.RootElement.GetProperty("Done").GetBoolean();
        }

        private static HashSet<string> ReadInputPaths(string root, string history, CancellationToken token)
        {
            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(history))
            {
                token.ThrowIfCancellationRequested();
                void Add(string? path)
                {
                    if (string.IsNullOrWhiteSpace(path)) return;
                    if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Input identity requires an absolute path.");
                    paths.Add(Path.GetFullPath(path));
                    if (paths.Count > MaxInputPaths) throw new InvalidDataException("Input-path safety index exceeds its limit.");
                }
                var metadataPath = Path.Combine(folder, "job.json");
                if (File.Exists(metadataPath))
                {
                    using var metadata = ReadJson(root, metadataPath);
                    _ = IsCompleted(metadata, folder);
                    if (metadata.RootElement.TryGetProperty("InputImagePath", out var single)) Add(single.GetString());
                    if (metadata.RootElement.TryGetProperty("InputImagePaths", out var multiple) && multiple.ValueKind != JsonValueKind.Null)
                        foreach (var path in multiple.EnumerateArray()) Add(path.GetString());
                }
                var imagesPath = Path.Combine(folder, "images.json");
                if (File.Exists(imagesPath))
                    foreach (var image in ReadImages(root, imagesPath))
                        if (image.Key.StartsWith("input/", StringComparison.OrdinalIgnoreCase)) Add(image.Path);
            }
            return paths;
        }

        private static List<UiPersistedImageInfo> ReadImages(string root, string path)
        {
            using var document = ReadJson(root, path);
            var records = new List<UiPersistedImageInfo>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate image identity.");
                string Field(string name) => property.Value.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
                records.Add(new(property.Name, Field("Path"), Field("ContentType"), Field("ContentSha256"), Field("CdnKey"), Field("CdnFileId")));
            }
            return records;
        }

        private static JsonDocument ReadJson(string root, string path)
        {
            RequireContainedPath(root, path);
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Cleanup metadata exceeds its size limit.");
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream);
        }

        internal static void RequireContainedPath(string root, string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Cleanup requires absolute paths.");
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Cleanup path leaves the data root.");
            for (var current = Path.GetFullPath(path); ; current = Path.GetDirectoryName(current)!)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Cleanup refuses symbolic links.");
                if (current == root) break;
            }
        }
    }
}
