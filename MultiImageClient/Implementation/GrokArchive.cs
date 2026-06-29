#nullable enable
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using XAIGrokAPIClient;

namespace MultiImageClient
{
    /// Reusable Grok history archiver/sync. Call SyncAsync(settings) any time
    /// (or via the --grok-sync CLI flag) to bring the local archive up to
    /// date. It is idempotent and incremental — already-downloaded assets are
    /// skipped — so it doubles as the "keep local copies synced" mechanism.
    ///
    /// What xAI actually exposes, and therefore what each phase does:
    ///
    ///   Phase 1 — Files API sweep. GET /v1/files is the ONLY enumerable
    ///       server-side history xAI offers. Videos we generate with
    ///       storage_options (which GrokImagineVideoGenerator now always
    ///       sets) land there permanently; this phase pages through the
    ///       whole inventory and downloads anything we don't have locally
    ///       into {ImageDownloadBaseFolder}\GrokArchive\.
    ///
    ///   Phase 2 — request_id re-poll. Image generations are synchronous
    ///       and their URLs are temporary, so there is no server history for
    ///       them; but video request_ids recorded in the ledger can be
    ///       re-polled via GET /v1/videos/{id}. Any clip whose local file
    ///       went missing gets re-downloaded while xAI still has it.
    ///
    ///   Phase 3 — JSON-log backfill. Reconstructs ledger entries (prompt,
    ///       url, local path) for every PRE-ledger Grok image/video by
    ///       scanning the per-image JSON logs under the saves tree. This is
    ///       the "back-read all prompts" part: after one sync, the entire
    ///       known Grok history lives in grok_ledger.jsonl.
    public static class GrokArchive
    {
        private static readonly HashSet<ImageGeneratorApiType> GrokGeneratorTypes = new()
        {
            ImageGeneratorApiType.GrokImagine,
            ImageGeneratorApiType.GrokImaginePro,
            ImageGeneratorApiType.GrokImagineVideo,
        };

        public static string GetArchiveFolder(Settings settings)
            => Path.Combine(settings.ImageDownloadBaseFolder, "GrokArchive");

        public static async Task SyncAsync(Settings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.XAIGrokApiKey))
            {
                Logger.Log("Grok sync: settings.json has no XAIGrokApiKey; nothing to do.");
                return;
            }

            var client = new XAIGrokClient(settings.XAIGrokApiKey, baseUrl: settings.XAIBaseUrl);
            var ledger = GrokLedger.ReadAll(settings);
            Logger.Log($"Grok sync: ledger has {ledger.Count} entries ({GrokLedger.GetPath(settings)})");

            var backfilled = BackfillLedgerFromJsonLogs(settings, ledger);
            var downloadedFiles = await SyncFilesApiAsync(settings, client, ledger);
            var repolled = await RepollMissingVideosAsync(settings, client, ledger);

            Logger.Log($"Grok sync complete: +{backfilled} ledger entries backfilled from logs, " +
                       $"{downloadedFiles} files downloaded from xAI storage, {repolled} videos re-fetched by request_id.");
            Logger.Log($"Grok sync: archive folder {GetArchiveFolder(settings)}; ledger {GrokLedger.GetPath(settings)}");
        }

        /// Full export of the entire known Grok history to a folder OUTSIDE
        /// the repo/saves tree (default C:\GrokArchive). Runs a sync first so
        /// every remotely-recoverable asset is local, then copies every
        /// ledger-known image and video into {dest}\Images / {dest}\Videos,
        /// plus a copy of the ledger and a human-readable prompts.txt with
        /// every prompt ever recorded. Incremental and rerunnable: files
        /// already present with the same size are skipped, so this is also
        /// the "keep the external copy synced" mechanism.
        public static async Task ExportAsync(Settings settings, string destRoot)
        {
            await SyncAsync(settings);

            var ledger = GrokLedger.ReadAll(settings);
            if (ledger.Count == 0)
            {
                Logger.Log("Grok export: ledger is empty; nothing to export.");
                return;
            }

            var imagesDir = Path.Combine(destRoot, "Images");
            var videosDir = Path.Combine(destRoot, "Videos");
            var otherDir = Path.Combine(destRoot, "Other");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(videosDir);
            Directory.CreateDirectory(otherDir);

            int copied = 0, skipped = 0, missing = 0;
            // The same local file can appear in multiple ledger entries
            // (live + sync); export each distinct path once.
            var distinctPaths = ledger
                .Where(e => !string.IsNullOrEmpty(e.LocalPath))
                .GroupBy(e => Path.GetFullPath(e.LocalPath!), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Path: g.Key, Entry: g.First()));
            foreach (var (sourcePath, entry) in distinctPaths)
            {
                if (!File.Exists(sourcePath))
                {
                    missing++;
                    continue;
                }
                var destDir = entry.Kind switch
                {
                    "video" => videosDir,
                    "image" => imagesDir,
                    _ => GuessKind(sourcePath) switch { "video" => videosDir, "image" => imagesDir, _ => otherDir },
                };
                var destPath = Path.Combine(destDir, Path.GetFileName(sourcePath));
                var sourceInfo = new FileInfo(sourcePath);
                if (File.Exists(destPath) && new FileInfo(destPath).Length == sourceInfo.Length)
                {
                    skipped++;
                    continue;
                }
                // Same name, different content: keep both.
                int n = 1;
                while (File.Exists(destPath) && new FileInfo(destPath).Length != sourceInfo.Length)
                {
                    destPath = Path.Combine(destDir,
                        $"{Path.GetFileNameWithoutExtension(sourcePath)}_{n:D4}{Path.GetExtension(sourcePath)}");
                    n++;
                }
                if (File.Exists(destPath) && new FileInfo(destPath).Length == sourceInfo.Length)
                {
                    skipped++;
                    continue;
                }
                File.Copy(sourcePath, destPath);
                copied++;
            }

            WritePromptsFile(ledger, Path.Combine(destRoot, "prompts.txt"));
            File.Copy(GrokLedger.GetPath(settings), Path.Combine(destRoot, "grok_ledger.jsonl"), overwrite: true);

            Logger.Log($"Grok export: {copied} files copied, {skipped} already present, {missing} ledger paths missing on disk.");
            Logger.Log($"Grok export: complete archive at {destRoot} (Images\\, Videos\\, prompts.txt, grok_ledger.jsonl).");
        }

        /// One block per generation, newest last: timestamp, kind/model, and
        /// the full prompt. The greppable "all prompts ever" companion to the
        /// machine-readable ledger.
        private static void WritePromptsFile(List<GrokLedgerEntry> ledger, string path)
        {
            var lines = new List<string>
            {
                $"# Every recorded Grok prompt - exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"# {ledger.Count} ledger entries total",
                "",
            };
            foreach (var e in ledger.Where(e => !string.IsNullOrWhiteSpace(e.Prompt)).OrderBy(e => e.TimestampUtc))
            {
                lines.Add($"[{e.TimestampUtc:yyyy-MM-dd HH:mm:ss}Z] {e.Kind} ({e.Model ?? "?"})");
                lines.Add(e.Prompt!);
                lines.Add("");
            }
            File.WriteAllLines(path, lines);
        }

        // ----- Phase 1: Files API sweep ---------------------------------

        private static async Task<int> SyncFilesApiAsync(Settings settings, XAIGrokClient client, List<GrokLedgerEntry> ledger)
        {
            List<XAIGrokFileObject> remoteFiles;
            try
            {
                remoteFiles = await client.ListAllFilesAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"Grok sync: Files API list failed: {ex.Message}");
                return 0;
            }
            Logger.Log($"Grok sync: xAI Files API reports {remoteFiles.Count} stored files.");

            // A file counts as "have" when some ledger entry maps its file_id
            // to a path that still exists on disk.
            var haveFileIds = ledger
                .Where(e => !string.IsNullOrEmpty(e.FileId) && !string.IsNullOrEmpty(e.LocalPath) && File.Exists(e.LocalPath))
                .Select(e => e.FileId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var archiveFolder = GetArchiveFolder(settings);
            int downloaded = 0;
            foreach (var file in remoteFiles)
            {
                if (haveFileIds.Contains(file.Id))
                {
                    continue;
                }
                try
                {
                    var bytes = await client.DownloadFileContentAsync(file.Id);
                    var localPath = BuildArchivePath(archiveFolder, file);
                    Directory.CreateDirectory(archiveFolder);
                    await File.WriteAllBytesAsync(localPath, bytes);
                    DlMirror.Copy(localPath, settings.FlatImageMirrorPath);
                    downloaded++;
                    Logger.Log($"Grok sync: downloaded {file.Id} ({bytes.Length / 1024} KB) -> {localPath}");

                    var entry = new GrokLedgerEntry
                    {
                        Kind = GuessKind(file.Filename),
                        FileId = file.Id,
                        LocalPath = localPath,
                        Bytes = bytes.Length,
                        TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(file.CreatedAt).UtcDateTime,
                        Source = "sync",
                    };
                    GrokLedger.Append(settings, entry);
                    ledger.Add(entry);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Grok sync: failed to download file {file.Id} ('{file.Filename}'): {ex.Message}");
                }
            }
            return downloaded;
        }

        private static string BuildArchivePath(string archiveFolder, XAIGrokFileObject file)
        {
            var created = DateTimeOffset.FromUnixTimeSeconds(file.CreatedAt).UtcDateTime;
            // file ids look like "file_a128090d-..."; the first id chunk is
            // plenty to guarantee uniqueness alongside the timestamp.
            var idPart = file.Id.Replace("file_", "").Split('-')[0];
            var name = string.IsNullOrWhiteSpace(file.Filename) ? "unnamed.bin" : file.Filename;
            var stem = FilenameGenerator.SanitizeFilename($"{created:yyyyMMddHHmmss}_{idPart}_{Path.GetFileNameWithoutExtension(name)}");
            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            return Path.Combine(archiveFolder, stem + ext);
        }

        private static string GuessKind(string? filename)
        {
            var ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            if (ext == ".mp4" || ext == ".mov" || ext == ".webm") return "video";
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".gif") return "image";
            return "file";
        }

        // ----- Phase 2: re-poll known video request ids ------------------

        private static async Task<int> RepollMissingVideosAsync(Settings settings, XAIGrokClient client, List<GrokLedgerEntry> ledger)
        {
            // request_ids where NO ledger entry currently has a live local copy.
            var byRequestId = ledger
                .Where(e => !string.IsNullOrEmpty(e.RequestId))
                .GroupBy(e => e.RequestId!, StringComparer.OrdinalIgnoreCase)
                .Where(g => !g.Any(e => !string.IsNullOrEmpty(e.LocalPath) && File.Exists(e.LocalPath)))
                .ToList();
            if (byRequestId.Count == 0)
            {
                return 0;
            }
            Logger.Log($"Grok sync: {byRequestId.Count} video request_ids have no surviving local file; re-polling xAI.");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var archiveFolder = GetArchiveFolder(settings);
            int recovered = 0;
            foreach (var group in byRequestId)
            {
                var requestId = group.Key;
                var original = group.OrderByDescending(e => e.TimestampUtc).First();
                try
                {
                    var result = await client.GetVideoAsync(requestId);
                    byte[]? bytes = null;
                    if (result.IsDone && !string.IsNullOrEmpty(result.Video?.Url))
                    {
                        bytes = await http.GetByteArrayAsync(result.Video.Url);
                    }
                    else if (result.IsDone && !string.IsNullOrEmpty(result.Video?.FileOutput?.FileId))
                    {
                        bytes = await client.DownloadFileContentAsync(result.Video.FileOutput.FileId);
                    }
                    if (bytes == null)
                    {
                        Logger.Log($"Grok sync: request {requestId} status={result.Status}; not recoverable.");
                        continue;
                    }

                    Directory.CreateDirectory(archiveFolder);
                    var stem = FilenameGenerator.SanitizeFilename(
                        $"{DateTime.UtcNow:yyyyMMddHHmmss}_repoll_{requestId.Substring(0, Math.Min(12, requestId.Length))}");
                    var localPath = Path.Combine(archiveFolder, stem + ".mp4");
                    await File.WriteAllBytesAsync(localPath, bytes);
                    DlMirror.Copy(localPath, settings.FlatImageMirrorPath);
                    recovered++;
                    Logger.Log($"Grok sync: recovered video {requestId} ({bytes.Length / 1024} KB) -> {localPath}");

                    var entry = new GrokLedgerEntry
                    {
                        Kind = "video",
                        Model = result.Model ?? original.Model,
                        Prompt = original.Prompt,
                        RequestId = requestId,
                        FileId = result.Video?.FileOutput?.FileId,
                        RemoteUrl = result.Video?.Url,
                        LocalPath = localPath,
                        Bytes = bytes.Length,
                        Source = "sync",
                    };
                    GrokLedger.Append(settings, entry);
                    ledger.Add(entry);
                }
                catch (Exception ex)
                {
                    // Expected for old requests — xAI expires deferred results.
                    Logger.Log($"Grok sync: request {requestId} re-poll failed (likely expired): {ex.Message}");
                }
            }
            return recovered;
        }

        // ----- Phase 3: back-read prompts from existing JSON logs ---------

        /// Every image save (with SaveJsonLog=true) wrote a per-image JSON
        /// log containing the full PromptDetails, the remote URL, the local
        /// save paths, and which generator made it. That tree IS our
        /// pre-ledger Grok history; fold the Grok entries into the ledger so
        /// "all prompts" are queryable in one place.
        private static int BackfillLedgerFromJsonLogs(Settings settings, List<GrokLedgerEntry> ledger)
        {
            var root = settings.ImageDownloadBaseFolder;
            if (!Directory.Exists(root))
            {
                return 0;
            }

            var knownLocalPaths = ledger
                .Where(e => !string.IsNullOrEmpty(e.LocalPath))
                .Select(e => Path.GetFullPath(e.LocalPath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var logFile in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                // only the per-image logs live in folders literally named "logs"
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(logFile)), "logs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(logFile));
                    var generatorToken = obj["GeneratorUsed"];
                    if (generatorToken == null) continue;

                    ImageGeneratorApiType generator;
                    if (generatorToken.Type == JTokenType.Integer)
                    {
                        generator = (ImageGeneratorApiType)(int)generatorToken;
                    }
                    else if (!Enum.TryParse((string?)generatorToken, out generator))
                    {
                        continue;
                    }
                    if (!GrokGeneratorTypes.Contains(generator)) continue;

                    var rawPath = (string?)obj["SavedImagePaths"]?["Raw"];
                    if (string.IsNullOrEmpty(rawPath)) continue;
                    var fullRawPath = Path.GetFullPath(rawPath);
                    if (knownLocalPaths.Contains(fullRawPath)) continue;

                    var entry = new GrokLedgerEntry
                    {
                        Kind = generator == ImageGeneratorApiType.GrokImagineVideo ? "video" : "image",
                        TimestampUtc = (DateTime?)obj["Timestamp"] ?? DateTime.UtcNow,
                        Model = generator.ToString(),
                        Prompt = (string?)obj["PromptDetails"]?["Prompt"],
                        RemoteUrl = (string?)obj["GeneratedImageUrl"],
                        LocalPath = fullRawPath,
                        Bytes = File.Exists(fullRawPath) ? new FileInfo(fullRawPath).Length : 0,
                        Source = "log-backfill",
                    };
                    GrokLedger.Append(settings, entry);
                    ledger.Add(entry);
                    knownLocalPaths.Add(fullRawPath);
                    added++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Grok sync: skipping unparseable log {logFile}: {ex.Message}");
                }
            }
            if (added > 0)
            {
                Logger.Log($"Grok sync: backfilled {added} pre-ledger Grok generations from JSON logs into the ledger.");
            }
            return added;
        }
    }
}
