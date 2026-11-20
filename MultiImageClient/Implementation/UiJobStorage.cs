#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using IdeogramAPIClient;
using RecraftAPIClient;

using SkiaSharp;

namespace MultiImageClient
{
    /// Process-wide LRU for card thumbnails, capped by total cached bytes.
    internal static class UiCardPreviewCache
    {
        private const long MaxTotalBytes = 48L * 1024 * 1024; // 48 MiB
        private static readonly object Gate = new();
        private static readonly LinkedList<string> Order = new();
        private static readonly Dictionary<string, (LinkedListNode<string> Node, byte[] Bytes, string ContentType)> Map = new();
        private static long _totalBytes;

        public static bool TryGet(string key, out byte[] bytes, out string contentType)
        {
            lock (Gate)
            {
                if (!Map.TryGetValue(key, out var entry))
                {
                    bytes = Array.Empty<byte>();
                    contentType = "";
                    return false;
                }
                Order.Remove(entry.Node);
                Order.AddFirst(entry.Node);
                bytes = entry.Bytes;
                contentType = entry.ContentType;
                return true;
            }
        }

        public static void Set(string key, byte[] bytes, string contentType)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxTotalBytes)
            {
                return;
            }
            lock (Gate)
            {
                if (Map.TryGetValue(key, out var existing))
                {
                    Order.Remove(existing.Node);
                    _totalBytes -= existing.Bytes.Length;
                    Map.Remove(key);
                }
                while (_totalBytes + bytes.Length > MaxTotalBytes && Order.Last != null)
                {
                    var evictKey = Order.Last.Value;
                    Order.RemoveLast();
                    if (Map.Remove(evictKey, out var evicted))
                    {
                        _totalBytes -= evicted.Bytes.Length;
                    }
                }
                var node = Order.AddFirst(key);
                Map[key] = (node, bytes, contentType);
                _totalBytes += bytes.Length;
            }
        }

        public static void Remove(string key)
        {
            lock (Gate)
            {
                if (!Map.Remove(key, out var entry))
                {
                    return;
                }
                Order.Remove(entry.Node);
                _totalBytes -= entry.Bytes.Length;
            }
        }

        public static (int Entries, long Bytes) Snapshot()
        {
            lock (Gate) return (Map.Count, _totalBytes);
        }
    }

    /// Read-only snapshot of one images.json record, exposed for maintenance
    /// tooling (B2 backfill). Empty CdnKey = not hosted.
    public sealed record UiPersistedImageInfo(
        string Key,
        string Path,
        string ContentType,
        string ContentSha256,
        string CdnKey,
        string CdnFileId);

    internal sealed class UiJobStorage
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private readonly object _writeLock = new();
        private readonly string _root;
        private readonly string _folder;
        private readonly string _folderName;
        private readonly string _metadataPath;
        private readonly string _eventsPath;
        private readonly string _imagesPath;
        private readonly string _thumbsFolder;
        private Dictionary<string, UiPersistedImage> _images = new();

        public UiJobStorage(string root, string jobId)
        {
            _root = root;
            _folderName = jobId;
            _folder = Path.Combine(root, jobId);
            _metadataPath = Path.Combine(_folder, "job.json");
            _eventsPath = Path.Combine(_folder, "events.jsonl");
            _imagesPath = Path.Combine(_folder, "images.json");
            _thumbsFolder = Path.Combine(_folder, "thumbs");
        }

        public void Initialize(UiJob job)
        {
            Directory.CreateDirectory(_folder);
            job.AttachStorage(this);
            SaveMetadata(job);
        }

        public void SaveMetadata(UiJob job)
        {
            try
            {
                SaveMetadataStrict(job);
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not save job {job.Id}: {ex.Message}");
            }
        }

        internal void SaveMetadataStrict(UiJob job)
        {
            var metadata = new UiPersistedJob
            {
                Id = job.Id,
                Prompt = job.Prompt,
                CreatedBy = job.CreatedBy,
                CreatorLogin = job.CreatorLogin,
                InputImagePath = job.InputImagePath,
                InputImagePaths = job.InputImagePaths.ToList(),
                InputImageWidth = job.InputImageWidth,
                InputImageHeight = job.InputImageHeight,
                GeneratorKeys = job.GeneratorKeys.ToList(),
                CreatedAt = job.CreatedAt,
                SourceJobId = job.SourceJobId,
                SourceGenerator = job.SourceGenerator,
                SourceIndex = job.SourceIndex,
                Split = job.Split == null ? null : UiPersistedJobSplit.From(job.Split),
                Done = job.IsDone,
            };
            WriteJsonAtomically(_metadataPath, metadata);
            UiHistoryIndexCache.Upsert(_root, IndexEntryFromMetadata(_folderName, metadata));
        }

        private static UiHistoryIndexEntry IndexEntryFromMetadata(string folderName, UiPersistedJob metadata)
        {
            var inputPaths = ResolvePersistedInputPaths(metadata);
            return new UiHistoryIndexEntry
            {
                Id = metadata.Id,
                FolderName = folderName,
                CreatedAt = EnsureUtc(metadata.CreatedAt),
                CreatedBy = metadata.CreatedBy ?? "",
                CreatorLogin = metadata.CreatorLogin ?? "",
                SourceJobId = metadata.SourceJobId ?? "",
                SplitGroupId = metadata.Split?.GroupId ?? "",
                InputImageCount = inputPaths.Count,
                Done = metadata.Done,
            };
        }

        public static void RewritePersistedCreatorLogin(string root, string folderName, string from, string to)
        {
            var path = Path.Combine(root, folderName, "job.json");
            if (!File.Exists(path))
                throw new InvalidDataException($"Job history file is missing for {folderName}.");
            var metadata = JsonSerializer.Deserialize<UiPersistedJob>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Job history file is empty for {folderName}.");
            if (string.Equals(metadata.CreatorLogin, to, StringComparison.Ordinal))
                return;
            if (!string.Equals(metadata.CreatorLogin, from, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Job {metadata.Id} creator login is '{metadata.CreatorLogin}', not '{from}'.");
            }
            metadata.CreatorLogin = to;
            var storage = new UiJobStorage(root, folderName);
            storage.WriteJsonAtomically(path, metadata);
            UiHistoryIndexCache.Upsert(root, IndexEntryFromMetadata(folderName, metadata));
        }

        public void AppendEvent(string json)
        {
            try
            {
                lock (_writeLock)
                {
                    Directory.CreateDirectory(_folder);
                    File.AppendAllText(_eventsPath, json + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not append event: {ex.Message}");
            }
        }

        public List<string> ReadAllEvents()
        {
            var list = new List<string>();
            lock (_writeLock)
            {
                if (!File.Exists(_eventsPath))
                {
                    return list;
                }
                foreach (var line in File.ReadLines(_eventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var _ = JsonDocument.Parse(line);
                        list.Add(line);
                    }
                    catch (JsonException)
                    {
                        Logger.Log($"UI history: skipped malformed event in {_eventsPath}.");
                    }
                }
            }
            return list;
        }

        public List<UiPersistedImageInfo> ListImages()
        {
            lock (_writeLock)
            {
                return _images
                    .Select(kv => new UiPersistedImageInfo(
                        kv.Key,
                        kv.Value.Path ?? "",
                        kv.Value.ContentType ?? "",
                        kv.Value.ContentSha256 ?? "",
                        kv.Value.CdnKey ?? "",
                        kv.Value.CdnFileId ?? ""))
                    .ToList();
            }
        }

        /// Permanently removes local raw files and card previews for exact
        /// persisted keys. images.json keeps the original paths, hashes, and
        /// B2 identities as deletion audit data and for idempotent retries.
        public void DeleteLocalArtifacts(IReadOnlyCollection<string> keys)
        {
            if (keys == null || keys.Count == 0)
            {
                return;
            }
            lock (_writeLock)
            {
                var uniqueKeys = keys.ToHashSet(StringComparer.Ordinal);
                var unknown = uniqueKeys.Where(key => !_images.ContainsKey(key)).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidDataException(
                        $"Deletion referenced unknown persisted image key(s): {string.Join(", ", unknown)}");
                }

                var paths = uniqueKeys
                    .Select(key => _images[key].Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                foreach (var key in uniqueKeys)
                {
                    paths.Add(Path.Combine(
                        _thumbsFolder,
                        ThumbFileName(key, "image/jpeg")));
                    paths.Add(Path.Combine(
                        _thumbsFolder,
                        ThumbFileName(key, "image/png")));
                }
                foreach (var path in paths.Distinct(StringComparer.Ordinal))
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }

        /// Atomically replaces events.jsonl after validating every line parses
        /// as JSON (fail closed — a malformed rewrite must never replace a
        /// working event log). The original file is copied once to
        /// events.jsonl.pre-b2 before the first rewrite and kept.
        public bool TryReplaceEvents(IReadOnlyList<string> lines)
            => TryReplaceEvents(lines, keepPreB2Backup: true);

        /// Deletion rewrites must not preserve old capability URLs in the
        /// migration backup. images.json retains exact B2 identity separately
        /// for idempotent purge retries.
        public bool TryReplaceEventsForDeletion(IReadOnlyList<string> lines)
            => TryReplaceEvents(lines, keepPreB2Backup: false);

        private bool TryReplaceEvents(
            IReadOnlyList<string> lines,
            bool keepPreB2Backup)
        {
            lock (_writeLock)
            {
                try
                {
                    foreach (var line in lines)
                    {
                        using var _ = JsonDocument.Parse(line);
                    }
                    var backup = _eventsPath + ".pre-b2";
                    if (keepPreB2Backup
                        && File.Exists(_eventsPath)
                        && !File.Exists(backup))
                    {
                        File.Copy(_eventsPath, backup);
                    }
                    var temp = _eventsPath + ".tmp";
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, _eventsPath, true);
                    if (!keepPreB2Backup && File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI history: could not replace events in {_folder}: {ex.Message}");
                    return false;
                }
            }
        }

        /// Returns true only when the path exists and was recorded in images.json.
        /// Callers must not drop in-memory bytes unless this returns true.
        public bool SaveImageReference(string key, string path, string contentType)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    return false;
                }
                var sha = ComputeFileSha256Hex(fullPath);
                lock (_writeLock)
                {
                    _images[key] = new UiPersistedImage
                    {
                        Path = fullPath,
                        ContentType = contentType,
                        ContentSha256 = sha,
                    };
                    WriteJsonAtomically(_imagesPath, _images);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not save image reference: {ex.Message}");
                return false;
            }
        }

        /// Writes streamed gpt-image-2 preview bytes (the kept last partial of
        /// a failed generation under its stable result key, or a per-partial
        /// progression snapshot under a "{gen}~p{k}" key) to a durable file
        /// under the job folder and registers it in images.json under the
        /// given key. Returns the written path, or null when the write or
        /// registration failed — the caller must then keep the bytes in RAM.
        public string? PersistEphemeralImage(string key, byte[] bytes, string contentType)
        {
            try
            {
                var partialsFolder = Path.Combine(_folder, "partials");
                Directory.CreateDirectory(partialsFolder);
                var ext = contentType switch
                {
                    "image/jpeg" => "jpg",
                    "image/webp" => "webp",
                    _ => "png",
                };
                var path = Path.Combine(partialsFolder, key.Replace('/', '-') + "." + ext);
                File.WriteAllBytes(path, bytes);
                return SaveImageReference(key, path, contentType) ? path : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not persist ephemeral image {key}: {ex.Message}");
                return null;
            }
        }

        /// Records a verified B2 upload against an already-registered image.
        /// Returns false when the image key was never registered — callers
        /// treat that as a failure (an upload we cannot correlate is useless).
        public bool SaveCdnReference(string key, string cdnKey, string cdnFileId)
        {
            lock (_writeLock)
            {
                if (!_images.TryGetValue(key, out var image))
                {
                    return false;
                }
                image.CdnKey = cdnKey;
                image.CdnFileId = cdnFileId;
                WriteJsonAtomically(_imagesPath, _images);
                return true;
            }
        }

        /// Result/grid images whose verified B2 upload was recorded and whose
        /// local raw still exists on disk. Input images are never eviction
        /// candidates: they stay local by design (v1 scope decision).
        public List<(string Key, string Path)> GetEvictableHostedImages()
        {
            lock (_writeLock)
            {
                return _images
                    .Where(kv => !kv.Key.StartsWith("input/", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value.CdnKey)
                        && !string.IsNullOrWhiteSpace(kv.Value.Path)
                        && File.Exists(kv.Value.Path))
                    .Select(kv => (kv.Key, kv.Value.Path))
                    .ToList();
            }
        }

        /// Deletes the local raw file for an image with a verified B2 upload
        /// (production storage mode). The images.json record — path, hash,
        /// CdnKey — is kept for provenance; local full-res serving of this key
        /// 404s from now on, which is correct: its published URL is the B2 one.
        public bool EvictLocalFile(string key)
        {
            lock (_writeLock)
            {
                if (!_images.TryGetValue(key, out var image)
                    || string.IsNullOrWhiteSpace(image.CdnKey)
                    || string.IsNullOrWhiteSpace(image.Path))
                {
                    return false;
                }
                try
                {
                    File.Delete(image.Path);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI history: could not evict {image.Path}: {ex.Message}");
                    return false;
                }
            }
        }

        public bool TryGetContentSha256(string key, out string sha256)
        {
            UiPersistedImage? image;
            lock (_writeLock)
            {
                _images.TryGetValue(key, out image);
            }
            if (image == null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
            {
                sha256 = "";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(image.ContentSha256))
            {
                sha256 = image.ContentSha256;
                return true;
            }
            try
            {
                sha256 = ComputeFileSha256Hex(image.Path);
                lock (_writeLock)
                {
                    if (_images.TryGetValue(key, out var current)
                        && string.Equals(current.Path, image.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        current.ContentSha256 = sha256;
                        WriteJsonAtomically(_imagesPath, _images);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not hash {image.Path}: {ex.Message}");
                sha256 = "";
                return false;
            }
        }

        private static string ThumbFileName(string key, string contentType)
        {
            var safe = key.Replace('/', '_').Replace('\\', '_');
            var ext = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            return safe + ext;
        }

        public bool TryGetCardPreviewPath(string key, out string path, out string contentType)
        {
            // Prefer recorded extension; accept either jpg or png if present.
            var jpg = Path.Combine(_thumbsFolder, ThumbFileName(key, "image/jpeg"));
            var png = Path.Combine(_thumbsFolder, ThumbFileName(key, "image/png"));
            if (File.Exists(jpg))
            {
                path = jpg;
                contentType = "image/jpeg";
                return true;
            }
            if (File.Exists(png))
            {
                path = png;
                contentType = "image/png";
                return true;
            }
            path = "";
            contentType = "";
            return false;
        }

        public bool TrySaveCardPreview(string key, byte[] bytes, string contentType, out string path)
        {
            path = "";
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }
            try
            {
                Directory.CreateDirectory(_thumbsFolder);
                var dest = Path.Combine(_thumbsFolder, ThumbFileName(key, contentType));
                var temp = dest + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, dest, true);
                path = dest;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not save card thumb for {key}: {ex.Message}");
                path = "";
                return false;
            }
        }

        private static string ComputeFileSha256Hex(string path)
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        public bool TryReadImage(string key, out byte[] bytes, out string contentType)
        {
            UiPersistedImage? image;
            lock (_writeLock)
            {
                _images.TryGetValue(key, out image);
            }
            if (image == null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
            {
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }
            try
            {
                bytes = File.ReadAllBytes(image.Path);
                contentType = image.ContentType;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not read {image.Path}: {ex.Message}");
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }
        }

        public bool TryGetImagePath(string key, out string path, out string contentType)
        {
            UiPersistedImage? image;
            lock (_writeLock)
            {
                _images.TryGetValue(key, out image);
            }
            if (image == null || string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
            {
                path = "";
                contentType = "";
                return false;
            }
            path = image.Path;
            contentType = image.ContentType;
            return true;
        }

        /// Normalizes a persisted CreatedAt to a UTC instant. Every historical
        /// writer serialized DateTime.Now, which System.Text.Json emits WITH
        /// its offset, so round-tripped values arrive as Kind=Local carrying
        /// the correct instant; current writers store DateTime.UtcNow (Kind=
        /// Utc, "Z" suffix). Kind=Unspecified can only come from an offset-less
        /// hand-edited file; the sole writer of such values was the server's
        /// local clock, so that is the one documented interpretation applied.
        internal static DateTime EnsureUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
            };
        }

        public static List<UiHistoryIndexEntry> ScanIndex(string root)
            => ScanIndex(root, out _, out _);

        public static List<UiHistoryIndexEntry> ScanIndex(string root, out int cacheHits, out int diskReads)
        {
            return UiHistoryIndexCache.Scan(
                root,
                ReadIndexEntryFromDisk,
                DateTime.Now.Date,
                out cacheHits,
                out diskReads);
        }

        internal static UiHistoryIndexEntry? ReadIndexEntryFromDisk(string folder)
        {
            var metadataPath = Path.Combine(folder, "job.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }
            try
            {
                var metadata = JsonSerializer.Deserialize<UiPersistedJob>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (metadata == null
                    || string.IsNullOrWhiteSpace(metadata.Id)
                    || metadata.Prompt == null)
                {
                    return null;
                }
                return IndexEntryFromMetadata(Path.GetFileName(folder), metadata);
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: skipped index entry {folder}: {ex.Message}");
                return null;
            }
        }

        // Null means the job folder is gone (deleted since indexing). A job.json
        // that exists but cannot be parsed is a hard error.
        public static string? ReadJobPromptIfPresent(string root, string folderName)
        {
            var metadataPath = Path.Combine(root, folderName, "job.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }
            var metadata = JsonSerializer.Deserialize<UiPersistedJob>(File.ReadAllText(metadataPath), JsonOptions)
                ?? throw new InvalidDataException($"UI history job file {metadataPath} is empty.");
            return metadata.Prompt;
        }

        public static UiJob? TryLoad(string root, string folderOrJobId)
        {
            if (string.IsNullOrWhiteSpace(folderOrJobId) || !Directory.Exists(root))
            {
                return null;
            }

            var folder = Path.Combine(root, folderOrJobId);
            var metadataPath = Path.Combine(folder, "job.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            try
            {
                var metadata = JsonSerializer.Deserialize<UiPersistedJob>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (metadata == null
                    || string.IsNullOrWhiteSpace(metadata.Id)
                    || metadata.Prompt == null)
                {
                    return null;
                }

                var storage = new UiJobStorage(root, Path.GetFileName(folder));
                if (File.Exists(storage._imagesPath))
                {
                    storage._images = JsonSerializer.Deserialize<Dictionary<string, UiPersistedImage>>(
                        File.ReadAllText(storage._imagesPath), JsonOptions)
                        ?? new Dictionary<string, UiPersistedImage>();
                }

                var job = new UiJob
                {
                    Id = metadata.Id,
                    Prompt = metadata.Prompt,
                    CreatedBy = metadata.CreatedBy ?? "",
                    CreatorLogin = metadata.CreatorLogin ?? "",
                    InputImagePaths = ResolvePersistedInputPaths(metadata),
                    InputImageWidth = metadata.InputImageWidth,
                    InputImageHeight = metadata.InputImageHeight,
                    GeneratorKeys = metadata.GeneratorKeys,
                    CreatedAt = EnsureUtc(metadata.CreatedAt),
                    SourceJobId = metadata.SourceJobId,
                    SourceGenerator = metadata.SourceGenerator,
                    SourceIndex = metadata.SourceIndex,
                    Split = metadata.Split?.ToJobSplit(),
                };
                job.AttachStorage(storage);

                if (metadata.Done)
                {
                    job.RestoreDone();
                }
                else
                {
                    // A process restart cannot resume a provider request. Recover
                    // any images already persisted for generators that never got
                    // a gen-result (N>1 loops only emit at the end, so a kill
                    // mid-loop otherwise left completed files invisible), then
                    // close the replay stream.
                    EmitInterruptedRecoveryResults(job, storage);
                    job.Emit(new { type = "job-done", interrupted = true });
                    job.MarkDone();
                }
                return job;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not load {folder}: {ex.Message}");
                return null;
            }
        }

        /// For generators that started but never emitted gen-result before a
        /// process kill: publish whatever durable images already sit in
        /// images.json so completed work is not invisible. Local app URLs are
        /// intentional — the image route serves path-backed or hosted bytes.
        private static void EmitInterruptedRecoveryResults(UiJob job, UiJobStorage storage)
        {
            var prior = storage.ReadAllEvents();
            var started = new HashSet<string>(StringComparer.Ordinal);
            var completed = new HashSet<string>(StringComparer.Ordinal);
            int? requestedN = null;
            foreach (var line in prior)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        continue;
                    }
                    var type = typeEl.GetString();
                    if (type == "accepted"
                        && root.TryGetProperty("n", out var nEl)
                        && nEl.TryGetInt32(out var n)
                        && n > 0)
                    {
                        requestedN = n;
                    }
                    else if (type == "gen-start"
                        && root.TryGetProperty("gen", out var startGen)
                        && startGen.GetString() is { Length: > 0 } sg)
                    {
                        started.Add(sg);
                    }
                    else if (type == "gen-result"
                        && root.TryGetProperty("gen", out var doneGen)
                        && doneGen.GetString() is { Length: > 0 } dg)
                    {
                        completed.Add(dg);
                    }
                }
                catch (JsonException)
                {
                    // Fail closed on this line only; other events still apply.
                }
            }

            var images = storage.ListImages();
            foreach (var gen in started)
            {
                if (completed.Contains(gen))
                {
                    continue;
                }

                var urls = new List<string>();
                var thumbs = new List<string>();
                var mediaType = "image/png";
                string? size = null;
                for (var i = 0; ; i++)
                {
                    var key = $"{gen}/{i}";
                    var info = images.FirstOrDefault(x =>
                        string.Equals(x.Key, key, StringComparison.Ordinal));
                    if (info == null)
                    {
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(info.Path)
                        && string.IsNullOrWhiteSpace(info.CdnKey))
                    {
                        break;
                    }
                    urls.Add($"/api/jobs/{job.Id}/images/{gen}/{i}");
                    thumbs.Add($"/api/jobs/{job.Id}/images/{gen}/{i}?thumb=1");
                    if (!string.IsNullOrWhiteSpace(info.ContentType))
                    {
                        mediaType = info.ContentType;
                    }
                    if (size == null
                        && !string.IsNullOrWhiteSpace(info.Path)
                        && File.Exists(info.Path))
                    {
                        try
                        {
                            var identified = Raster.Identify(info.Path);
                            if (identified.Width > 0 && identified.Height > 0)
                            {
                                size = $"{identified.Width}x{identified.Height}";
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log(
                                $"[ui #{job.Id}] could not read recovered {key} dimensions: {ex.Message}");
                        }
                    }
                }

                var want = requestedN ?? Math.Max(urls.Count, 1);
                var ok = urls.Count > 0;
                var labelExtra = want > 1 && urls.Count > 0
                    ? $"{urls.Count}/{want} imgs"
                    : null;
                var error = ok
                    ? (urls.Count < want
                        ? $"UI process restarted before this generator finished all {want} requested images; recovered {urls.Count} completed image(s)."
                        : "")
                    : "UI process restarted before this generator returned any image.";
                job.Emit(new
                {
                    type = "gen-result",
                    gen,
                    ok,
                    error,
                    errorHint = (string?)null,
                    errorHintUrl = (string?)null,
                    ms = 0,
                    images = urls,
                    partialImages = (List<string?>?)null,
                    partialThumbs = (List<string?>?)null,
                    progressImages = (List<object>?)null,
                    thumbs,
                    mediaType,
                    label = GeneratorPresentation.UiContactSheetLabel(gen, labelExtra),
                    size,
                    videoMode = (string?)null,
                    videoDurationSeconds = (int?)null,
                    videoResolution = (string?)null,
                    videoAspectRatio = (string?)null,
                    cost = 0m,
                    interrupted = true,
                });
                Logger.Log(
                    $"[ui #{job.Id}] recovered interrupted {gen}: {urls.Count}/{want} image(s) from disk");
            }
        }

        /// Read job.json prompt/dims without hydrating a UiJob (input library).
        public static bool TryPeekJobSummary(
            string root,
            string folderName,
            out string prompt,
            out int width,
            out int height,
            out DateTime createdAt)
        {
            prompt = "";
            width = 0;
            height = 0;
            createdAt = default;
            var metadataPath = Path.Combine(root, folderName, "job.json");
            if (!File.Exists(metadataPath))
            {
                return false;
            }
            try
            {
                var metadata = JsonSerializer.Deserialize<UiPersistedJob>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (metadata == null || metadata.Prompt == null)
                {
                    return false;
                }
                prompt = metadata.Prompt;
                width = metadata.InputImageWidth;
                height = metadata.InputImageHeight;
                createdAt = EnsureUtc(metadata.CreatedAt);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not peek {metadataPath}: {ex.Message}");
                return false;
            }
        }

        /// Resolve content SHA for an images.json key without hydrating a UiJob.
        /// Computes and persists the hash when missing (streamed; no full buffer retained).
        public static bool TryPeekImageSha256(string root, string folderName, string key, out string sha256)
        {
            sha256 = "";
            var imagesPath = Path.Combine(root, folderName, "images.json");
            if (!File.Exists(imagesPath))
            {
                return false;
            }
            try
            {
                var images = JsonSerializer.Deserialize<Dictionary<string, UiPersistedImage>>(
                    File.ReadAllText(imagesPath), JsonOptions)
                    ?? new Dictionary<string, UiPersistedImage>();
                if (!images.TryGetValue(key, out var image)
                    || string.IsNullOrWhiteSpace(image.Path)
                    || !File.Exists(image.Path))
                {
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(image.ContentSha256))
                {
                    sha256 = image.ContentSha256;
                    return true;
                }
                sha256 = ComputeFileSha256Hex(image.Path);
                image.ContentSha256 = sha256;
                var tempPath = imagesPath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(images, JsonOptions));
                File.Move(tempPath, imagesPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not peek hash for {folderName}/{key}: {ex.Message}");
                sha256 = "";
                return false;
            }
        }

        private void WriteJsonAtomically<T>(string path, T value)
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(_folder);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(tempPath, path, true);
            }
        }

        // Older job.json records only had InputImagePath. Prefer the list when
        // present; otherwise promote the single path. Never invent paths.
        private static IReadOnlyList<string> ResolvePersistedInputPaths(UiPersistedJob metadata)
        {
            var paths = (metadata.InputImagePaths ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (paths.Count == 0 && !string.IsNullOrWhiteSpace(metadata.InputImagePath))
            {
                paths.Add(metadata.InputImagePath);
            }
            return paths;
        }

        private sealed class UiPersistedJob
        {
            public string Id { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string CreatedBy { get; set; } = "";
            public string CreatorLogin { get; set; } = "";
            public string InputImagePath { get; set; } = "";
            public List<string> InputImagePaths { get; set; } = new();
            public int InputImageWidth { get; set; }
            public int InputImageHeight { get; set; }
            public List<string> GeneratorKeys { get; set; } = new();
            public DateTime CreatedAt { get; set; }
            public string SourceJobId { get; set; } = "";
            public string SourceGenerator { get; set; } = "";
            public int? SourceIndex { get; set; }
            public UiPersistedJobSplit? Split { get; set; }
            public bool Done { get; set; }
        }

        private sealed class UiPersistedJobSplit
        {
            public string GroupId { get; set; } = "";
            public string ExchangeId { get; set; } = "";
            public int Index { get; set; }
            public int Count { get; set; }
            public string Title { get; set; } = "";
            public string Model { get; set; } = "";
            public string Reason { get; set; } = "";

            public static UiPersistedJobSplit From(UiJobSplit split) => new()
            {
                GroupId = split.GroupId,
                ExchangeId = split.ExchangeId,
                Index = split.Index,
                Count = split.Count,
                Title = split.Title,
                Model = split.Model,
                Reason = split.Reason,
            };

            public UiJobSplit ToJobSplit()
                => PromptSplit.ValidateJobSplit(new UiJobSplit(GroupId, ExchangeId, Index, Count, Title, Model, Reason));
        }

        private sealed class UiPersistedImage
        {
            public string Path { get; set; } = "";
            public string ContentType { get; set; } = "application/octet-stream";
            public string ContentSha256 { get; set; } = "";

            /// Backblaze B2 object key (ui/{jobId}/{gen}/{n}-{random}.{ext})
            /// recorded only after a checksum-verified upload. Empty = not
            /// hosted. The random segment is the public access capability.
            public string CdnKey { get; set; } = "";

            /// B2 fileId of the uploaded object, kept for future
            /// deletion/purge bookkeeping.
            public string CdnFileId { get; set; } = "";
        }
    }

    /// Lightweight on-disk history row — enough for day counts, usernames, and
    /// the input-library filter without hydrating full UiJob graphs.
    public sealed class UiHistoryIndexEntry
    {
        public required string Id { get; init; }
        public required string FolderName { get; init; }
        public DateTime CreatedAt { get; init; }
        public string CreatedBy { get; init; } = "";
        public string CreatorLogin { get; init; } = "";
        public string SourceJobId { get; init; } = "";
        public string SplitGroupId { get; init; } = "";
        public int InputImageCount { get; init; }
        public bool Done { get; init; }
        public bool HasInputImage => InputImageCount > 0;
    }

    public class UiJobRegistry
    {
        // Soft cap on non-live (archive) jobs kept hydrated in RAM. Expanding
        // many archive days still works; cold ones are dropped and reloaded
        // from disk on the next Get / day expand.
        private const int MaxHydratedArchiveJobs = 250;

        private readonly ConcurrentDictionary<string, UiJob> _jobs = new();
        private readonly object _orderLock = new();
        private readonly List<UiJob> _ordered = new();
        private readonly string _historyRoot;
        private readonly object _indexLock = new();
        private readonly List<UiHistoryIndexEntry> _index = new();
        private readonly ConcurrentDictionary<string, long> _archiveAccessTicks = new();

        // Global append-only envelope log for the polling transport: each
        // entry is a pre-serialized {"jobId","kind":"job-known"|"event",...}
        // JSON object. A job-known announcement (card metadata) always
        // precedes that job's events. Clients poll with an integer cursor
        // (index into this list), so they hold ZERO persistent connections —
        // long-lived SSE streams ate the browser's ~6-connection HTTP/1.1
        // pool once a few windows were open, starving all image loads.
        private readonly object _envelopeLock = new();
        private readonly List<string> _envelopes = new();
        // Soft cap: when exceeded, drop the oldest half. Clients whose cursor
        // falls before the trimmed range resync from 0 (idempotent replay).
        private const int MaxLiveEnvelopes = 8000;

        // Jobs whose card + events ride the live envelope feed. Jobs restored
        // from earlier days stay out of the feed (a multi-day history would
        // otherwise replay through every page load) and are served through
        // the /api/archive endpoints instead.
        private readonly HashSet<string> _liveFeedJobIds = new();

        public int LiveJobCount
        {
            get { lock (_envelopeLock) return _liveFeedJobIds.Count; }
        }

        public int HydratedJobCount => _jobs.Count;

        public int IndexedJobCount
        {
            get { lock (_indexLock) return _index.Count; }
        }

        public object AccountGenerationSummary()
        {
            lock (_indexLock) return _index.Where(j => j.CreatorLogin.Length > 0).GroupBy(j => j.CreatorLogin)
                .Select(g => new { login = g.Key, jobsSubmitted = g.Count(), lastGeneration = g.Max(j => j.CreatedAt) }).ToList();
        }

        public int EnvelopeCount
        {
            get { lock (_envelopeLock) return _envelopes.Count; }
        }

        public UiJobRegistry(Settings settings)
        {
            _historyRoot = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            // CreatedAt instants are UTC; the live-feed/"today" and archive-day
            // buckets deliberately follow the SERVER's calendar day, matching
            // the saves/<day> folder convention. Only the buckets are server-
            // local — displayed times are formatted in each viewer's browser.
            var today = DateTime.Now.Date;
            var archivedCount = 0;
            var closedInterrupted = 0;

            var scannedEntries = UiJobStorage.ScanIndex(_historyRoot, out var cacheHits, out var diskReads);
            foreach (var entry in scannedEntries.OrderBy(e => e.CreatedAt))
            {
                lock (_indexLock) _index.Add(entry);

                if (entry.CreatedAt.ToLocalTime().Date == today)
                {
                    var job = UiJobStorage.TryLoad(_historyRoot, entry.FolderName);
                    if (job == null)
                    {
                        continue;
                    }
                    if (job.IsDone)
                    {
                        GenerationArchive.MarkExternalJobInterrupted(job.Id);
                    }
                    RegisterHydrated(job, live: true);
                }
                else
                {
                    archivedCount++;
                    if (!entry.Done)
                    {
                        // Close abandoned in-flight jobs from prior days without
                        // keeping their full graphs resident.
                        var job = UiJobStorage.TryLoad(_historyRoot, entry.FolderName);
                        if (job != null)
                        {
                            GenerationArchive.MarkExternalJobInterrupted(job.Id);
                            closedInterrupted++;
                            UpdateIndexDone(job.Id, done: true);
                        }
                    }
                }
            }

            if (IndexedJobCount > 0)
            {
                Logger.Log(
                    $"UI history: indexed {IndexedJobCount} job(s) ({cacheHits} cached, {diskReads} disk); hydrated {HydratedJobCount} for today "
                    + $"({archivedCount} archived on disk"
                    + (closedInterrupted > 0 ? $", closed {closedInterrupted} interrupted" : "")
                    + ").");
            }
        }

        public void Add(UiJob job)
        {
            var storage = new UiJobStorage(_historyRoot, job.Id);
            storage.Initialize(job);
            lock (_indexLock)
            {
                _index.Add(new UiHistoryIndexEntry
                {
                    Id = job.Id,
                    FolderName = job.Id,
                    CreatedAt = job.CreatedAt,
                    CreatedBy = job.CreatedBy ?? "",
                    CreatorLogin = job.CreatorLogin ?? "",
                    SourceJobId = job.SourceJobId ?? "",
                    SplitGroupId = job.Split?.GroupId ?? "",
                    InputImageCount = job.InputImageCount,
                    Done = job.IsDone,
                });
            }
            // RegisterHydrated(live) wires the envelope callback + job-known.
            RegisterHydrated(job, live: true);
        }

        private UiJob RegisterHydrated(UiJob job, bool live)
        {
            if (!_jobs.TryAdd(job.Id, job))
            {
                return _jobs.TryGetValue(job.Id, out var existing) ? existing : job;
            }
            lock (_orderLock) _ordered.Add(job);
            if (live)
            {
                lock (_envelopeLock) _liveFeedJobIds.Add(job.Id);
                AppendJobKnown(job);
                job.AttachEmitCallback(AppendEventEnvelope);
            }
            else
            {
                _archiveAccessTicks[job.Id] = Environment.TickCount64;
                EvictColdArchiveJobsIfNeeded();
            }
            return job;
        }

        private void UpdateIndexDone(string jobId, bool done)
        {
            lock (_indexLock)
            {
                for (var i = 0; i < _index.Count; i++)
                {
                    if (!string.Equals(_index[i].Id, jobId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var e = _index[i];
                    _index[i] = new UiHistoryIndexEntry
                    {
                        Id = e.Id,
                        FolderName = e.FolderName,
                        CreatedAt = e.CreatedAt,
                        CreatedBy = e.CreatedBy,
                        CreatorLogin = e.CreatorLogin,
                        SourceJobId = e.SourceJobId,
                        SplitGroupId = e.SplitGroupId,
                        InputImageCount = e.InputImageCount,
                        Done = done,
                    };
                    return;
                }
            }
        }

        private void EvictColdArchiveJobsIfNeeded()
        {
            var archiveIds = _jobs.Keys.Where(id => !IsInLiveFeed(id)).ToList();
            if (archiveIds.Count <= MaxHydratedArchiveJobs)
            {
                return;
            }

            var victimCount = archiveIds.Count - MaxHydratedArchiveJobs;
            var victims = archiveIds
                .OrderBy(id => _archiveAccessTicks.TryGetValue(id, out var t) ? t : 0L)
                .Take(victimCount)
                .ToList();
            foreach (var id in victims)
            {
                if (_jobs.TryRemove(id, out _))
                {
                    lock (_orderLock) _ordered.RemoveAll(j => j.Id == id);
                    _archiveAccessTicks.TryRemove(id, out _);
                }
            }
            if (victims.Count > 0)
            {
                Logger.Log($"UI history: evicted {victims.Count} cold archive job(s) from RAM (cap {MaxHydratedArchiveJobs}).");
            }
        }

        public UiJob? Get(string id)
        {
            if (_jobs.TryGetValue(id, out var j))
            {
                if (!IsInLiveFeed(id))
                {
                    _archiveAccessTicks[id] = Environment.TickCount64;
                }
                return j;
            }

            UiHistoryIndexEntry? entry;
            lock (_indexLock)
            {
                entry = _index.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            }
            if (entry == null)
            {
                // Fail closed on unknown id — do not guess a "nearest" job.
                // Still try the folder name == id convention for brand-new
                // jobs that raced ahead of the index (should not happen).
                var job = UiJobStorage.TryLoad(_historyRoot, id);
                if (job == null)
                {
                    return null;
                }
                return RegisterHydrated(job, live: false);
            }

            var loaded = UiJobStorage.TryLoad(_historyRoot, entry.FolderName);
            if (loaded == null)
            {
                return null;
            }
            return RegisterHydrated(loaded, live: false);
        }

        /// Chronological (oldest first) snapshot of *hydrated* jobs only.
        /// Full history lives in the on-disk index + /api/archive endpoints.
        public List<UiJob> ListChronological()
        {
            lock (_orderLock) return _ordered.ToList();
        }

        // creatorLogin non-empty restricts the search to that account's own
        // jobs; it is checked on the index entry before any file is read.
        public List<UiSubjectTagMatchedPrompt> SearchPrompts(
            IReadOnlyList<string> terms,
            UiVisibilityStore? visibility,
            string creatorLogin,
            int limit)
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock)
            {
                snapshot = _index.OrderByDescending(e => e.CreatedAt).ToList();
            }

            var matches = new List<UiSubjectTagMatchedPrompt>();
            foreach (var entry in snapshot)
            {
                if (matches.Count >= limit)
                {
                    break;
                }
                if (creatorLogin.Length > 0 && !string.Equals(entry.CreatorLogin, creatorLogin, StringComparison.Ordinal))
                {
                    continue;
                }
                if (visibility != null && visibility.IsPromptHidden(entry.Id))
                {
                    continue;
                }
                var prompt = _jobs.TryGetValue(entry.Id, out var resident)
                    ? resident.Prompt
                    : UiJobStorage.ReadJobPromptIfPresent(_historyRoot, entry.FolderName);
                if (string.IsNullOrWhiteSpace(prompt)
                    || !terms.Any(t => prompt.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                matches.Add(new UiSubjectTagMatchedPrompt
                {
                    Source = "ui-history",
                    RecordId = entry.Id,
                    CreatedAtUtc = UiJobStorage.EnsureUtc(entry.CreatedAt).ToString("O"),
                    Prompt = prompt,
                });
            }
            return matches;
        }

        /// Card metadata for the frontend, shared by the live job-known
        /// envelope and the archive payloads so both render identically.
        public static string SerializeJobMetadata(UiJob job)
        {
            return JsonSerializer.Serialize(new
            {
                id = job.Id,
                prompt = job.Prompt,
                user = job.CreatedBy,
                gens = job.GeneratorKeys,
                hasImage = job.HasInputImage,
                inputCount = job.InputImageCount,
                split = PromptSplit.ToMetadata(job.Split),
                // No preformatted time string: the browser formats the unix-ms
                // instant in the viewer's own timezone.
                createdAtUnixMs = new DateTimeOffset(UiJobStorage.EnsureUtc(job.CreatedAt)).ToUnixTimeMilliseconds(),
            });
        }

        private void AppendJobKnown(UiJob job)
        {
            var metadata = SerializeJobMetadata(job);
            lock (_envelopeLock)
            {
                _envelopes.Add($"{{\"jobId\":\"{job.Id}\",\"kind\":\"job-known\",\"job\":{metadata}}}");
                TrimEnvelopesLocked();
            }
        }

        private void AppendEventEnvelope(UiJob job, string eventJson)
        {
            lock (_envelopeLock)
            {
                _envelopes.Add($"{{\"jobId\":\"{job.Id}\",\"kind\":\"event\",\"event\":{eventJson}}}");
                TrimEnvelopesLocked();
            }
        }

        private void TrimEnvelopesLocked()
        {
            if (_envelopes.Count <= MaxLiveEnvelopes)
            {
                return;
            }
            var remove = _envelopes.Count / 2;
            _envelopes.RemoveRange(0, remove);
            Logger.Log($"UI envelope log trimmed by {remove}; {_envelopes.Count} remain (clients behind the cut resync).");
        }

        /// Envelopes from `fromCursor` onward plus the next cursor. A cursor
        /// outside the log's range (older server run, restart) deliberately
        /// resyncs from 0 — the full-history replay is the same idempotent
        /// semantic a fresh page load uses.
        public (List<string> Envelopes, int NextCursor) ReadEnvelopes(int fromCursor)
        {
            lock (_envelopeLock)
            {
                var start = fromCursor >= 0 && fromCursor <= _envelopes.Count ? fromCursor : 0;
                var batch = _envelopes.GetRange(start, _envelopes.Count - start);
                return (batch, _envelopes.Count);
            }
        }

        private bool IsInLiveFeed(string jobId)
        {
            lock (_envelopeLock) return _liveFeedJobIds.Contains(jobId);
        }

        public bool TryGetJobLocation(string id, out string day, out bool live)
        {
            day = "";
            live = false;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }
            if (IsInLiveFeed(id))
            {
                var job = Get(id);
                if (job == null)
                {
                    return false;
                }
                day = UiJobStorage.EnsureUtc(job.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd");
                live = true;
                return true;
            }

            UiHistoryIndexEntry? entry;
            lock (_indexLock)
            {
                entry = _index.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            }
            if (entry == null)
            {
                return false;
            }
            day = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
            return true;
        }

        /// Archived days (jobs not in the live feed), newest day first.
        public List<(string Day, List<string> JobIds)> ListArchivedDays()
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock) snapshot = _index.ToList();
            return snapshot
                .Where(e => !IsInLiveFeed(e.Id))
                // Server-local calendar day, same bucketing as the registry's
                // "today" hydration and the saves/<day> folders.
                .GroupBy(e => e.CreatedAt.ToLocalTime().Date)
                .OrderByDescending(g => g.Key)
                .Select(g => (
                    g.Key.ToString("yyyy-MM-dd"),
                    g.Select(entry => entry.Id).ToList()))
                .ToList();
        }

        /// One archived day's jobs, chronological. Day format yyyy-MM-dd.
        /// Hydrates from disk on demand; fail-closed if a listed folder is gone.
        public List<UiJob> ListArchivedDay(DateTime day)
        {
            List<UiHistoryIndexEntry> dayEntries;
            lock (_indexLock)
            {
                dayEntries = _index
                    .Where(e => !IsInLiveFeed(e.Id) && e.CreatedAt.ToLocalTime().Date == day.Date)
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
            }

            var result = new List<UiJob>(dayEntries.Count);
            foreach (var entry in dayEntries)
            {
                var job = Get(entry.Id);
                if (job == null)
                {
                    Logger.Log($"UI archive: job {entry.Id} listed for {day:yyyy-MM-dd} is missing on disk; omitted.");
                    continue;
                }
                result.Add(job);
            }
            return result;
        }

        /// Every effective creator username across all history (live + archive)
        /// with job counts, most prolific first. Authenticated jobs can resolve
        /// through their account's current profile name without rewriting the
        /// original CreatedBy value stored in job.json.
        public List<(string User, List<string> JobIds)> ListUsers(
            Func<string, string, string> displayResolver)
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock) snapshot = _index.ToList();
            return snapshot
                .GroupBy(e => displayResolver(e.CreatorLogin, e.CreatedBy ?? ""))
                .OrderByDescending(g => g.Count())
                .Select(g => (g.Key, g.Select(entry => entry.Id).ToList()))
                .ToList();
        }

        public List<string> ListHistoricalAliasesForLogin(string creatorLogin)
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock) snapshot = _index.ToList();
            return snapshot
                .Where(entry => string.Equals(
                    entry.CreatorLogin,
                    creatorLogin,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.CreatedBy)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<(string CreatorLogin, List<string> Aliases)> ListAuthenticatedAttributions()
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock) snapshot = _index.ToList();
            return snapshot
                .Where(entry => !string.IsNullOrWhiteSpace(entry.CreatorLogin))
                .GroupBy(entry => entry.CreatorLogin, StringComparer.OrdinalIgnoreCase)
                .Select(group => (
                    group.Key,
                    group.Select(entry => entry.CreatedBy)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()))
                .ToList();
        }

        public List<UiJob> ListBySplitGroup(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return new List<UiJob>();
            }
            List<UiHistoryIndexEntry> entries;
            lock (_indexLock)
            {
                entries = _index
                    .Where(e => string.Equals(e.SplitGroupId, groupId, StringComparison.Ordinal))
                    .OrderBy(e => e.CreatedAt)
                    .ToList();
            }
            var parts = new List<UiJob>(entries.Count);
            foreach (var entry in entries)
            {
                var job = Get(entry.Id);
                if (job?.Split != null
                    && string.Equals(job.Split.GroupId, groupId, StringComparison.Ordinal))
                {
                    parts.Add(job);
                }
            }
            return parts
                .OrderBy(j => j.Split!.Index)
                .ThenBy(j => j.CreatedAt)
                .ThenBy(j => j.Id, StringComparer.Ordinal)
                .ToList();
        }

        public UiJob AssignLegacyCreatorLogin(
            string jobId,
            string expectedCreatedBy,
            string creatorLogin)
        {
            var job = Get(jobId)
                ?? throw new InvalidOperationException($"Job {jobId} does not exist.");
            job.AssignLegacyCreatorLogin(expectedCreatedBy, creatorLogin);
            lock (_indexLock)
            {
                var index = _index.FindIndex(entry =>
                    string.Equals(entry.Id, jobId, StringComparison.Ordinal));
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Job {jobId} is hydrated but missing from the history index.");
                }
                var entry = _index[index];
                _index[index] = new UiHistoryIndexEntry
                {
                    Id = entry.Id,
                    FolderName = entry.FolderName,
                    CreatedAt = entry.CreatedAt,
                    CreatedBy = entry.CreatedBy,
                    CreatorLogin = creatorLogin,
                    SourceJobId = entry.SourceJobId,
                    SplitGroupId = entry.SplitGroupId,
                    InputImageCount = entry.InputImageCount,
                    Done = entry.Done,
                };
            }
            return job;
        }

        /// Input-library candidates from the disk index (user uploads only).
        /// Newest first. Listing peeks hashes from disk — does not hydrate
        /// full UiJob graphs. Image URLs hydrate on demand via Get.
        public List<UiHistoryIndexEntry> ListInputLibraryCandidates()
        {
            List<UiHistoryIndexEntry> snapshot;
            lock (_indexLock) snapshot = _index.ToList();
            return snapshot
                .Where(e => e.HasInputImage && string.IsNullOrEmpty(e.SourceJobId))
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
        }

        public int RewriteCreatorLogin(string from, string to)
        {
            from = from.Trim();
            to = to.Trim();
            if (from.Length == 0 || to.Length == 0)
                throw new InvalidDataException("A login transfer requires the exact previous login and the exact new login.");
            if (string.Equals(from, to, StringComparison.Ordinal))
                return 0;
            List<UiHistoryIndexEntry> matches;
            lock (_indexLock)
            {
                matches = _index.Where(entry => string.Equals(entry.CreatorLogin, from, StringComparison.Ordinal)).ToList();
            }
            foreach (var entry in matches)
            {
                if (_jobs.TryGetValue(entry.Id, out var job))
                    job.RewriteCreatorLogin(from, to);
                else
                    UiJobStorage.RewritePersistedCreatorLogin(_historyRoot, entry.FolderName, from, to);
                lock (_indexLock)
                {
                    var index = _index.FindIndex(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal));
                    if (index < 0)
                        throw new InvalidDataException($"Job {entry.Id} left the history index during login transfer.");
                    var current = _index[index];
                    if (!string.Equals(current.CreatorLogin, from, StringComparison.Ordinal)
                        && !string.Equals(current.CreatorLogin, to, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Job {entry.Id} creator login is '{current.CreatorLogin}', not '{from}'.");
                    }
                    _index[index] = new UiHistoryIndexEntry
                    {
                        Id = current.Id,
                        FolderName = current.FolderName,
                        CreatedAt = current.CreatedAt,
                        CreatedBy = current.CreatedBy,
                        CreatorLogin = to,
                        SourceJobId = current.SourceJobId,
                        SplitGroupId = current.SplitGroupId,
                        InputImageCount = current.InputImageCount,
                        Done = current.Done,
                    };
                }
            }
            return matches.Count;
        }

        public string HistoryRoot => _historyRoot;
    }
}
