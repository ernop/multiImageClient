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
using System.Threading;
using System.Threading.Tasks;

using IdeogramAPIClient;
using ImageMagick;
using RecraftAPIClient;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MultiImageClient
{
    /// One web-UI generation job: (optional input image(s), prompt, generator
    /// set, options). Holds an append-only event log that SSE subscribers
    /// replay from any index (so a page refresh mid-job still sees the full
    /// history), plus durable path references for saved results. Full image
    /// bytes are not retained in RAM once on disk; only streaming partials
    /// briefly live in memory until a durable path replaces them. Completed
    /// cards survive a server restart via images.json paths + disk files.
    ///
    /// Multiple input images (up to UiJobRunner.MaxInputImages) are ordered;
    /// index 0 is the primary used for AR matching, compare-with-input, and
    /// the job-card thumb. gpt-image-2 /edits receives the full list; every
    /// other generator receives only the primary.
    public class UiJob
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
        public required string Prompt { get; init; }
        /// Display username the job was created under (shared-site
        /// attribution; deliberately not an access-control concept). Empty on
        /// jobs persisted before usernames existed.
        public string CreatedBy { get; init; } = "";
        /// Authenticated account that created the job. Unlike CreatedBy, this
        /// is an access-control identity and is never supplied by the browser.
        /// Empty on jobs created without the access gate or before this field
        /// existed.
        public string CreatorLogin { get; set; } = "";
        public IReadOnlyList<string> InputImagePaths { get; init; } = Array.Empty<string>();
        public int InputImageWidth { get; init; }
        public int InputImageHeight { get; init; }
        public IReadOnlyList<string> GeneratorKeys { get; init; } = Array.Empty<string>();
        /// Always a UTC instant (Kind=Utc). Persisted with its offset in
        /// job.json; loaders normalize through UiJobStorage.EnsureUtc. Clients
        /// receive it only as unix milliseconds and format it in the viewer's
        /// own timezone.
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public string SourceJobId { get; init; } = "";
        public string SourceGenerator { get; init; } = "";
        public int? SourceIndex { get; init; }

        private readonly object _lock = new();
        // Events live on disk (events.jsonl). An in-RAM copy is kept only while
        // the job is still running so AttachEmitCallback can replay without a
        // disk round-trip; MarkDone clears it. Archive views reload from disk.
        private readonly List<string> _events = new();
        private bool _done;
        private UiJobStorage? _storage;

        internal void AssignLegacyCreatorLogin(string expectedCreatedBy, string creatorLogin)
        {
            lock (_lock)
            {
                if (CreatorLogin.Length != 0)
                {
                    throw new InvalidOperationException(
                        $"Job {Id} already has an authenticated creator.");
                }
                if (!string.Equals(CreatedBy, expectedCreatedBy, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Job {Id} no longer has the expected historical attribution.");
                }
                if (_storage == null)
                {
                    throw new InvalidOperationException(
                        $"Job {Id} has no durable history storage.");
                }
                CreatorLogin = creatorLogin;
                try
                {
                    _storage.SaveMetadata(this);
                }
                catch
                {
                    CreatorLogin = "";
                    throw;
                }
            }
        }
        private Action<UiJob, string>? _emitCallback;

        public bool IsDone
        {
            get { lock (_lock) return _done; }
        }

        private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _images = new();

        public string InputImagePath => InputImagePaths.Count > 0 ? InputImagePaths[0] : "";
        public int InputImageCount => InputImagePaths.Count;
        public bool HasInputImage => InputImagePaths.Count > 0;

        public void Emit(object evt)
        {
            var json = JsonSerializer.Serialize(evt);
            lock (_lock)
            {
                if (!_done)
                {
                    _events.Add(json);
                }
                _storage?.AppendEvent(json);
                _emitCallback?.Invoke(this, json);
            }
        }

        /// Drains every already-recorded event through the callback, then wires
        /// it up for all future Emits — atomically under the event lock, so the
        /// global registry log sees this job's events exactly once and in order.
        internal void AttachEmitCallback(Action<UiJob, string> callback)
        {
            lock (_lock)
            {
                IEnumerable<string> prior = _events;
                if (_events.Count == 0 && _storage != null)
                {
                    prior = _storage.ReadAllEvents();
                }
                foreach (var json in prior)
                {
                    callback(this, json);
                }
                _emitCallback = callback;
            }
        }

        public void MarkDone()
        {
            lock (_lock)
            {
                _done = true;
                // Persisted to events.jsonl already; free the running-job copy.
                _events.Clear();
                _events.TrimExcess();
            }
            // Successful finals and archived inputs are path-backed before a
            // job completes. Any bytes left here are obsolete stream partials
            // or failed-output remnants and must not live with hydrated history.
            _images.Clear();
            // Snapshot URLs were baked into the persisted gen-result events;
            // the arrival-order index has no post-completion consumer.
            _partialSnapshots.Clear();
            _storage?.SaveMetadata(this);
        }

        /// Snapshot events from `fromIndex` onward plus the done flag.
        /// Finished jobs read from disk so archive payloads do not require a
        /// process-lifetime in-RAM event list.
        public (List<string> Events, bool Done) ReadFrom(int fromIndex)
        {
            lock (_lock)
            {
                List<string> all;
                if (_events.Count > 0)
                {
                    all = _events;
                }
                else if (_storage != null)
                {
                    all = _storage.ReadAllEvents();
                }
                else
                {
                    all = new List<string>();
                }

                var batch = fromIndex < all.Count
                    ? all.GetRange(fromIndex, all.Count - fromIndex)
                    : new List<string>();
                return (batch, _done);
            }
        }

        /// Registers image bytes for serving. When <paramref name="durablePath"/>
        /// points at a file on disk, that path is the source of truth and full
        /// bytes are NOT retained in RAM (the shared host OOM'd keeping every
        /// result for the process lifetime). Ephemeral bytes — gpt-image-2
        /// streaming partials with no path yet — still live in <see cref="_images"/>
        /// until a later call replaces them with a durable path.
        public void StoreImage(
            string genKey,
            int n,
            byte[] bytes,
            string contentType,
            string durablePath = "")
        {
            var key = $"{genKey}/{n}";
            if (!string.IsNullOrWhiteSpace(durablePath)
                && _storage != null
                && _storage.SaveImageReference(key, durablePath, contentType))
            {
                // Drop any prior partial / in-memory copy; serve via disk.
                _images.TryRemove(key, out _);
                return;
            }

            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"UI job {Id}: StoreImage({key}) has no durable path and no bytes — nothing to serve.");
            }
            _images[key] = (bytes, contentType);
        }

        /// Path-only register: never loads the file into the job's RAM cache.
        public void StoreImagePath(string genKey, int n, string durablePath, string contentType)
        {
            StoreImage(genKey, n, Array.Empty<byte>(), contentType, durablePath);
        }

        /// User-required product behavior (2026-08-10): when a streaming
        /// generation fails after preview partials were shown (typically
        /// gpt-image-2's final image getting moderated away), the last
        /// streamed partial is kept visible instead of vanishing. This
        /// persists the in-memory partial bytes for {genKey}/{n} to the job
        /// folder and re-registers the key as path-backed, so the same stable
        /// image URL the live preview used keeps serving that last known
        /// state across restarts. Returns false when no ephemeral partial
        /// exists for the key or persistence failed — the caller must not
        /// claim a kept preview it cannot durably serve.
        public bool TryPersistLastPartialImage(string genKey, int n)
        {
            var key = $"{genKey}/{n}";
            if (_storage == null
                || !_images.TryGetValue(key, out var v)
                || v.Bytes == null
                || v.Bytes.Length == 0)
            {
                return false;
            }
            if (_storage.PersistEphemeralImage(key, v.Bytes, v.ContentType) == null)
            {
                return false;
            }
            _images.TryRemove(key, out _);
            return true;
        }

        // User-required product behavior (2026-08-10): the in-process streamed
        // previews are wanted on SUCCESSFUL generations too, not only when the
        // final fails. Each arriving partial is therefore persisted durably
        // right away under its own snapshot key ("{genKey}~p{partialIndex}"),
        // separate from the stable result key the live preview and final
        // share, so the whole progression survives the final overwriting the
        // stable key. Snapshot keys never appear in gen-result `images`, are
        // never B2-uploaded or evicted, and share the hide identity of the
        // result image they preview (see PartialSnapshotVisibilityGen).
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(int PartialIndex, int ImageIndex)>> _partialSnapshots = new();

        public static string PartialSnapshotGenKey(string genKey, int partialIndex)
            => $"{genKey}~p{partialIndex}";

        /// Maps a snapshot gen key ("gpt2~p1") back to the result gen key it
        /// previews ("gpt2") for visibility checks; non-snapshot keys map to
        /// themselves.
        public static string PartialSnapshotVisibilityGen(string gen)
        {
            var i = gen.LastIndexOf("~p", StringComparison.Ordinal);
            return i > 0 && int.TryParse(gen.AsSpan(i + 2), out var idx) && idx >= 0
                ? gen.Substring(0, i)
                : gen;
        }

        /// Durably persists one in-process streamed preview the moment it
        /// arrives (bytes go straight to disk — never retained in this job's
        /// RAM beyond the stable-key live preview that already exists).
        /// Returns false when persistence failed; the snapshot is then simply
        /// absent from the progression, never substituted.
        public bool TryPersistPartialSnapshot(string genKey, int partialIndex, int imageIndex, byte[] bytes, string contentType)
        {
            if (_storage == null || bytes == null || bytes.Length == 0)
            {
                return false;
            }
            var key = $"{PartialSnapshotGenKey(genKey, partialIndex)}/{imageIndex}";
            if (_storage.PersistEphemeralImage(key, bytes, contentType) == null)
            {
                return false;
            }
            _partialSnapshots.GetOrAdd(genKey, _ => new ConcurrentQueue<(int, int)>())
                .Enqueue((partialIndex, imageIndex));
            return true;
        }

        /// Persisted in-process previews for one generator, in arrival order.
        /// Only meaningful while the job is running (gen-result snapshots the
        /// URLs into the persisted event; replay reads those, not this).
        public List<(int PartialIndex, int ImageIndex)> GetPartialSnapshots(string genKey)
        {
            return _partialSnapshots.TryGetValue(genKey, out var q)
                ? q.ToList()
                : new List<(int, int)>();
        }

        /// Records a checksum-verified B2 upload for a durably saved image.
        public bool StoreCdnReference(string genKey, int n, string cdnKey, string cdnFileId)
        {
            return _storage?.SaveCdnReference($"{genKey}/{n}", cdnKey, cdnFileId) == true;
        }

        /// Production storage mode (B2KeepLocalRawImages=false): delete local
        /// raw result/grid files whose B2 uploads were verified. The durable
        /// card thumb is forced into existence first — thumbs are normally
        /// built lazily from the original, which is about to disappear.
        /// Returns the number of files actually deleted.
        public int EvictHostedLocalRaws()
        {
            if (_storage == null)
            {
                return 0;
            }
            var evicted = 0;
            foreach (var (key, _) in _storage.GetEvictableHostedImages())
            {
                var slash = key.LastIndexOf('/');
                if (slash <= 0 || !int.TryParse(key.Substring(slash + 1), out var n))
                {
                    Logger.Log($"UI job {Id}: eviction skipped unparseable image key '{key}'.");
                    continue;
                }
                var genKey = key.Substring(0, slash);
                TryGetCardPreviewPath(genKey, n, out _, out _);
                if (_storage.EvictLocalFile(key))
                {
                    evicted++;
                }
            }
            return evicted;
        }

        public bool TryGetContentSha256(string genKey, int n, out string sha256)
        {
            if (_storage?.TryGetContentSha256($"{genKey}/{n}", out sha256) == true)
            {
                return true;
            }
            sha256 = "";
            return false;
        }

        /// Snapshot of this job's persisted image records (images.json) for
        /// maintenance tooling — the B2 backfill enumerates candidates here.
        public IReadOnlyList<UiPersistedImageInfo> ListPersistedImages()
        {
            return _storage?.ListImages()
                ?? (IReadOnlyList<UiPersistedImageInfo>)Array.Empty<UiPersistedImageInfo>();
        }

        /// Atomically replaces this job's persisted event log (B2 backfill URL
        /// migration). A one-time pre-rewrite backup is retained on disk.
        public bool TryReplacePersistedEvents(IReadOnlyList<string> lines)
        {
            return _storage?.TryReplaceEvents(lines) == true;
        }

        public bool TryGetImage(string genKey, int n, out byte[] bytes, out string contentType)
        {
            var key = $"{genKey}/{n}";
            if (_images.TryGetValue(key, out var v))
            {
                bytes = v.Bytes;
                contentType = v.ContentType;
                return true;
            }
            if (_storage?.TryReadImage(key, out bytes, out contentType) == true)
            {
                return true;
            }
            bytes = Array.Empty<byte>();
            contentType = "";
            return false;
        }

        /// Resolves a durable on-disk path for streaming without loading bytes
        /// into the job's RAM cache. Returns false for ephemeral partials that
        /// only exist in <see cref="_images"/>.
        public bool TryGetImagePath(string genKey, int n, out string path, out string contentType)
        {
            if (_storage != null && _storage.TryGetImagePath($"{genKey}/{n}", out path, out contentType))
            {
                return true;
            }
            path = "";
            contentType = "";
            return false;
        }

        // Card previews exist because a page hydrating 200+ jobs otherwise pulls
        // every full-resolution original (2-8 MB each, gigabytes total) through
        // the browser's ~6-socket HTTP/1.1 pool. Cards get a <=640px preview;
        // the viewer keeps the exact original. Durable thumbs live on disk under
        // UiHistory/{jobId}/thumbs/; a process-wide 48 MiB LRU is only a hot cache.
        private const int CardPreviewMaxEdge = 640;
        private const int MaxCacheablePreviewBytes = 512 * 1024;

        /// Prefer a durable on-disk card thumb (built once, streamed forever).
        /// Returns false for ephemeral in-memory partials — use
        /// <see cref="TryGetCardPreviewBytes"/> for those.
        public bool TryGetCardPreviewPath(string genKey, int n, out string path, out string contentType)
        {
            var key = $"{genKey}/{n}";
            var cacheKey = $"{Id}/{key}";

            if (_storage != null
                && _storage.TryGetCardPreviewPath(key, out path, out contentType))
            {
                return true;
            }

            if (_storage == null
                || !TryGetImagePath(genKey, n, out var originalPath, out var originalType)
                || !originalType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                path = "";
                contentType = "";
                return false;
            }

            if (!TryBuildAndPersistCardPreview(
                key,
                originalPath,
                originalType,
                out path,
                out contentType,
                out var previewBytes))
            {
                path = "";
                contentType = "";
                return false;
            }

            if (previewBytes.Length <= MaxCacheablePreviewBytes)
            {
                UiCardPreviewCache.Set(cacheKey, previewBytes, contentType);
            }
            return true;
        }

        /// Builds and persists the disk thumb from an explicit source file —
        /// used by hosted-thumb regeneration, where the verified B2 download
        /// sits in a temp file because the local original was evicted.
        public bool TryBuildCardPreviewFromSource(
            string genKey, int n, string sourcePath, string sourceContentType,
            out string path, out string contentType)
        {
            if (!TryBuildAndPersistCardPreview(
                $"{genKey}/{n}", sourcePath, sourceContentType,
                out path, out contentType, out _))
            {
                path = "";
                contentType = "";
                return false;
            }
            return true;
        }

        /// In-memory / ephemeral preview path (streaming partials with no durable
        /// original yet). Never enters multi-MB originals into the process LRU.
        public bool TryGetCardPreviewBytes(string genKey, int n, out byte[] bytes, out string contentType)
        {
            var cacheKey = $"{Id}/{genKey}/{n}";
            if (UiCardPreviewCache.TryGet(cacheKey, out bytes, out contentType))
            {
                return true;
            }

            // Durable originals should use TryGetCardPreviewPath (disk thumb).
            if (TryGetImagePath(genKey, n, out _, out _))
            {
                if (TryGetCardPreviewPath(genKey, n, out var thumbPath, out contentType))
                {
                    try
                    {
                        bytes = File.ReadAllBytes(thumbPath);
                        if (bytes.Length <= MaxCacheablePreviewBytes)
                        {
                            UiCardPreviewCache.Set(cacheKey, bytes, contentType);
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UI job {Id}: could not read disk thumb {thumbPath}: {ex.Message}");
                    }
                }
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }

            if (!_images.TryGetValue($"{genKey}/{n}", out var mem)
                || mem.Bytes.Length == 0
                || !mem.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }

            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(mem.Bytes);
                if (Math.Max(image.Width, image.Height) <= CardPreviewMaxEdge)
                {
                    // Ephemeral and already small — serve as-is, cache only if compact.
                    bytes = mem.Bytes;
                    contentType = mem.ContentType;
                    if (bytes.Length <= MaxCacheablePreviewBytes)
                    {
                        UiCardPreviewCache.Set(cacheKey, bytes, contentType);
                    }
                    return true;
                }

                DownscaleInPlace(image);
                var (previewBytes, previewType) = EncodeCardPreview(image);
                if (previewBytes.Length <= MaxCacheablePreviewBytes)
                {
                    UiCardPreviewCache.Set(cacheKey, previewBytes, previewType);
                }
                bytes = previewBytes;
                contentType = previewType;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI job {Id}: could not build ephemeral card preview for {genKey}/{n} ({ex.Message}).");
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }
        }

        private bool TryBuildAndPersistCardPreview(
            string imageKey,
            string originalPath,
            string originalContentType,
            out string thumbPath,
            out string contentType,
            out byte[] previewBytes)
        {
            thumbPath = "";
            contentType = "";
            previewBytes = Array.Empty<byte>();
            if (_storage == null)
            {
                return false;
            }

            try
            {
                using var image = LoadCardPreviewSource(originalPath, originalContentType);
                if (Math.Max(image.Width, image.Height) > CardPreviewMaxEdge)
                {
                    DownscaleInPlace(image);
                }

                var (encoded, type) = EncodeCardPreview(image);
                if (!_storage.TrySaveCardPreview(imageKey, encoded, type, out thumbPath))
                {
                    return false;
                }
                contentType = type;
                previewBytes = encoded;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"UI job {Id}: could not build disk card preview for {imageKey} ({ex.Message}).");
                return false;
            }
        }

        private static Image<Rgba32> LoadCardPreviewSource(string path, string contentType)
        {
            if (contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                using var vector = new MagickImage(path);
                return SixLabors.ImageSharp.Image.Load<Rgba32>(
                    vector.ToByteArray(MagickFormat.Png));
            }

            using var stream = File.OpenRead(path);
            return SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
        }

        private void DownscaleInPlace(Image<Rgba32> image)
        {
            var scale = (double)CardPreviewMaxEdge / Math.Max(image.Width, image.Height);
            image.Mutate(x => x.Resize(
                Math.Max(1, (int)Math.Round(image.Width * scale)),
                Math.Max(1, (int)Math.Round(image.Height * scale))));
        }

        private static (byte[] Bytes, string ContentType) EncodeCardPreview(Image<Rgba32> image)
        {
            bool hasAlpha = false;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height && !hasAlpha; y++)
                {
                    foreach (ref var p in accessor.GetRowSpan(y))
                    {
                        if (p.A < 255) { hasAlpha = true; break; }
                    }
                }
            });

            using var ms = new MemoryStream();
            if (hasAlpha)
            {
                image.SaveAsPng(ms);
                return (ms.ToArray(), "image/png");
            }
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
            return (ms.ToArray(), "image/jpeg");
        }

        internal void AttachStorage(UiJobStorage storage) => _storage = storage;

        internal void RestoreDone()
        {
            lock (_lock) _done = true;
        }
    }

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
        private readonly string _folder;
        private readonly string _metadataPath;
        private readonly string _eventsPath;
        private readonly string _imagesPath;
        private readonly string _thumbsFolder;
        private Dictionary<string, UiPersistedImage> _images = new();

        public UiJobStorage(string root, string jobId)
        {
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
                    Done = job.IsDone,
                };
                WriteJsonAtomically(_metadataPath, metadata);
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not save job {job.Id}: {ex.Message}");
            }
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

        /// Atomically replaces events.jsonl after validating every line parses
        /// as JSON (fail closed — a malformed rewrite must never replace a
        /// working event log). The original file is copied once to
        /// events.jsonl.pre-b2 before the first rewrite and kept.
        public bool TryReplaceEvents(IReadOnlyList<string> lines)
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
                    if (File.Exists(_eventsPath) && !File.Exists(backup))
                    {
                        File.Copy(_eventsPath, backup);
                    }
                    var temp = _eventsPath + ".tmp";
                    File.WriteAllLines(temp, lines);
                    File.Move(temp, _eventsPath, true);
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
        {
            var entries = new List<UiHistoryIndexEntry>();
            if (!Directory.Exists(root))
            {
                return entries;
            }

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var metadataPath = Path.Combine(folder, "job.json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }
                try
                {
                    var metadata = JsonSerializer.Deserialize<UiPersistedJob>(
                        File.ReadAllText(metadataPath), JsonOptions);
                    if (metadata == null
                        || string.IsNullOrWhiteSpace(metadata.Id)
                        || metadata.Prompt == null)
                    {
                        continue;
                    }

                    var inputPaths = ResolvePersistedInputPaths(metadata);
                    entries.Add(new UiHistoryIndexEntry
                    {
                        Id = metadata.Id,
                        FolderName = Path.GetFileName(folder),
                        CreatedAt = EnsureUtc(metadata.CreatedAt),
                        CreatedBy = metadata.CreatedBy ?? "",
                        CreatorLogin = metadata.CreatorLogin ?? "",
                        SourceJobId = metadata.SourceJobId ?? "",
                        InputImageCount = inputPaths.Count,
                        Done = metadata.Done,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI history: skipped index entry {folder}: {ex.Message}");
                }
            }
            return entries;
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
                };
                job.AttachStorage(storage);

                if (metadata.Done)
                {
                    job.RestoreDone();
                }
                else
                {
                    // A process restart cannot resume a provider request. Keep
                    // completed cells, close the replay stream, and let the UI
                    // mark any unfinished cells as having no result.
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
            public bool Done { get; set; }
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

            foreach (var entry in UiJobStorage.ScanIndex(_historyRoot).OrderBy(e => e.CreatedAt))
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
                    $"UI history: indexed {IndexedJobCount} job(s); hydrated {HydratedJobCount} for today "
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

        public string HistoryRoot => _historyRoot;
    }

    /// Per-job options parsed from the submit form. gpt-image-2 honors all of
    /// them; the grok generators only use what applies to them.
    public class UiJobSpec
    {
        public required List<string> GeneratorKeys { get; init; }
        public string Quality { get; init; } = "high";
        public string Moderation { get; init; } = "low";
        public int ImageCount { get; init; } = 1;

        /// Intent-level output geometry, mapped per generator (gpt-image-2
        /// gets an exact WxH, grok gets an aspect ratio + 1k/2k resolution).
        /// With an input image, shape=auto means match the source aspect ratio
        /// using the closest representation each provider accepts.
        /// Shapes: auto | square | landscape | portrait | wide | tall.
        public string Shape { get; init; } = "auto";

        /// Output detail tier: standard (~1K) | high (~2K) | max (~4K-ish,
        /// capped by each backend's envelope).
        public string Detail { get; init; } = "standard";

        /// Exact per-generator suffixes chosen before the job starts. Each
        /// non-empty value is appended only to that generator's private copy
        /// of the prompt and recorded as a ManualSuffixation transformation.
        public IReadOnlyDictionary<string, string> GeneratorExtraTexts { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
        public string VideoMode { get; init; } = "normal";
        public int VideoDurationSeconds { get; init; } = 15;
        public string VideoResolution { get; init; } = "480p";
        public string VideoAspectRatio { get; init; } = "source";
    }

    /// Maps the intent-level (shape, detail) pair onto each backend's actual
    /// knobs. Kept as a standalone static so future generators (and tests)
    /// can reuse the same mapping.
    public static class UiShapeMapping
    {
        public static readonly string[] Shapes = { "auto", "square", "landscape", "portrait", "wide", "tall" };
        public static readonly string[] Details = { "standard", "high", "max" };

        private static readonly (string Name, int Width, int Height)[] GrokAspects =
        {
            ("1:1", 1, 1),
            ("2:3", 2, 3),
            ("3:2", 3, 2),
            ("3:4", 3, 4),
            ("4:3", 4, 3),
            ("9:16", 9, 16),
            ("16:9", 16, 9),
        };

        private static readonly (string Name, int Width, int Height)[] GoogleAspects =
        {
            ("1:1", 1, 1),
            ("2:3", 2, 3),
            ("3:2", 3, 2),
            ("3:4", 3, 4),
            ("4:3", 4, 3),
            ("4:5", 4, 5),
            ("5:4", 5, 4),
            ("9:16", 9, 16),
            ("16:9", 16, 9),
            ("21:9", 21, 9),
        };

        private static readonly (string Name, int Width, int Height)[] KreaAspects =
        {
            ("1:1", 1, 1),
            ("4:3", 4, 3),
            ("3:2", 3, 2),
            ("16:9", 16, 9),
            ("2.35:1", 235, 100),
            ("4:5", 4, 5),
            ("2:3", 2, 3),
            ("9:16", 9, 16),
        };

        private static readonly (IdeogramAPIClient.IdeogramAspectRatio Aspect, int Width, int Height)[] IdeogramV3Aspects =
        {
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_1, 1, 1),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_2_3, 2, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_2, 3, 2),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_4, 3, 4),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_4_3, 4, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_9_16, 9, 16),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_9, 16, 9),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_10_16, 10, 16),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_10, 16, 10),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_3, 1, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_1, 3, 1),
        };

        private static readonly (string Name, int Width, int Height)[] IdeogramV4Resolutions =
        {
            ("2048x2048", 2048, 2048),
            ("1440x2880", 1440, 2880), ("2880x1440", 2880, 1440),
            ("1664x2496", 1664, 2496), ("2496x1664", 2496, 1664),
            ("1792x2240", 1792, 2240), ("2240x1792", 2240, 1792),
            ("1440x2560", 1440, 2560), ("2560x1440", 2560, 1440),
            ("1600x2560", 1600, 2560), ("2560x1600", 2560, 1600),
            ("1728x2304", 1728, 2304), ("2304x1728", 2304, 1728),
            ("1296x3168", 1296, 3168), ("3168x1296", 3168, 1296),
            ("1152x2944", 1152, 2944), ("2944x1152", 2944, 1152),
            ("1248x3328", 1248, 3328), ("3328x1248", 3328, 1248),
            ("1280x3072", 1280, 3072), ("3072x1280", 3072, 1280),
            ("1024x3072", 1024, 3072), ("3072x1024", 3072, 1024),
        };

        public static bool IsKnownShape(string shape)
        {
            var value = (shape ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Shapes, value) >= 0;
        }

        public static string GrokAspectForInput(int width, int height)
            => NearestAspect(width, height, GrokAspects);

        public static string GoogleAspectForInput(int width, int height)
            => NearestAspect(width, height, GoogleAspects);

        public static string KreaAspectForInput(int width, int height)
            => NearestAspect(width, height, KreaAspects);

        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3AspectForInput(int width, int height)
        {
            ValidateInputDimensions(width, height);
            var sourceRatio = (double)width / height;
            return IdeogramV3Aspects
                .OrderBy(candidate => RatioDistance(sourceRatio, (double)candidate.Width / candidate.Height))
                .First()
                .Aspect;
        }

        public static string IdeogramV4ResolutionForInput(int width, int height)
            => NearestAspect(width, height, IdeogramV4Resolutions);

        /// gpt-image-2 size string. All values are multiples of 16, within
        /// the [655360, 8294400] pixel envelope, edges < 3840 (2880x2880 is
        /// exactly the max pixel count).
        public static string Gpt2Size(
            string shape,
            string detail,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            var normalizedDetail = Norm(detail, Details);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                var targetPixels = normalizedDetail switch
                {
                    "high" => 4194304,
                    "max" => GptImage2Generator.SizeMaxPixels,
                    _ => 1048576,
                };
                var (width, height) = SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels,
                    multiple: GptImage2Generator.SizeEdgeMultiple,
                    maxPixels: GptImage2Generator.SizeMaxPixels,
                    maxEdgeExclusive: GptImage2Generator.SizeMaxEdge,
                    maxLongShortRatio: GptImage2Generator.SizeMaxAspectRatio);
                var requested = $"{width}x{height}";
                if (!GptImage2Generator.TryNormalizeSize(requested, out var normalized, out _, out var error)
                    || normalized != requested)
                {
                    throw new InvalidOperationException(
                        $"Could not map input aspect ratio to a valid gpt-image-2 size: {error ?? $"{requested} normalized to {normalized}"}");
                }
                return requested;
            }

            return (normalizedShape, normalizedDetail) switch
            {
                ("auto", _) => "auto",
                ("square", "standard") => "1024x1024",
                ("square", "high") => "2048x2048",
                ("square", "max") => "2880x2880",
                ("landscape", "standard") => "1536x1024",
                ("landscape", "high") => "2304x1536",
                ("landscape", "max") => "3216x2144",
                ("portrait", "standard") => "1024x1536",
                ("portrait", "high") => "1536x2304",
                ("portrait", "max") => "2144x3216",
                ("wide", "standard") => "1536x864",
                ("wide", "high") => "2560x1440",
                ("wide", "max") => "3824x2144",
                ("tall", "standard") => "864x1536",
                ("tall", "high") => "1440x2560",
                ("tall", "max") => "2144x3824",
                _ => "auto",
            };
        }

        /// gpt-image-1 / mini support the three canonical 1024-edge sizes.
        public static string Gpt1Size(string shape)
        {
            return Norm(shape, Shapes) switch
            {
                "landscape" or "wide" => "1536x1024",
                "portrait" or "tall" => "1024x1536",
                _ => "1024x1024",
            };
        }

        /// Aspect-ratio string for the grok generators. With no input image,
        /// empty means no preference. With input dimensions, auto maps to the
        /// closest ratio accepted by Grok.
        public static string GrokAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return GrokAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "1:1",
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "",
            };
        }

        /// Krea 2 requires an explicit aspect ratio. Text-to-image auto uses
        /// square; image jobs map auto to the nearest native Krea ratio.
        public static string KreaAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return KreaAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "1:1",
            };
        }

        /// grok-api resolution tier. Standard = 1k; both higher tiers = 2k
        /// (2k is grok-api's ceiling).
        public static string GrokResolution(string detail)
            => Norm(detail, Details) == "standard" ? "1k" : "2k";

        /// Ideogram v4 resolution string. Detail has no effect. Text-to-image
        /// auto omits the field; Remix auto chooses the closest published 2K
        /// resolution to the source image.
        public static string IdeogramV4Resolution(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return IdeogramV4ResolutionForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "2048x2048",
                "landscape" => "2496x1664",
                "portrait" => "1664x2496",
                "wide" => "2560x1440",
                "tall" => "1440x2560",
                _ => "",
            };
        }

        /// Ideogram v3 aspect enum (image-reference jobs route to v3).
        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3Aspect(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return IdeogramV3AspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "landscape" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_2,
                "portrait" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_2_3,
                "wide" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_9,
                "tall" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_9_16,
                _ => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_1,
            };
        }

        /// BFL FLUX.2 width/height. Multiples of 32, total pixels kept under
        /// FLUX.2's ~4 MP output ceiling, so "max" == "high".
        public static (int Width, int Height) BflSize(
            string shape,
            string detail,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var big = Norm(detail, Details) != "standard";
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels: big ? 4000000 : 1048576,
                    multiple: 32,
                    maxPixels: 4000000,
                    maxEdgeExclusive: 2592,
                    maxLongShortRatio: 3.0);
            }
            return normalizedShape switch
            {
                "landscape" => big ? (2304, 1536) : (1248, 832),
                "portrait" => big ? (1536, 2304) : (832, 1248),
                "wide" => big ? (2560, 1440) : (1344, 768),
                "tall" => big ? (1440, 2560) : (768, 1344),
                _ => big ? (1920, 1920) : (1024, 1024),
            };
        }

        /// FLUX.1 [pro]/[dev]/1.1 endpoints cap each edge at 1440 and require
        /// multiples of 32. Their API has no separate detail tier.
        public static (int Width, int Height) BflLegacySize(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels: 1048576,
                    multiple: 32,
                    maxPixels: 1440 * 1440,
                    maxEdgeExclusive: 1441,
                    maxLongShortRatio: 1440.0 / 256.0);
            }
            return normalizedShape switch
            {
                "landscape" => (1248, 832),
                "portrait" => (832, 1248),
                "wide" => (1344, 768),
                "tall" => (768, 1344),
                _ => (1024, 1024),
            };
        }

        /// Gemini imageConfig.aspectRatio. With no input image, auto omits the
        /// field so the model decides. With input dimensions, auto maps to the
        /// closest ratio in Gemini's supported set.
        public static string GoogleAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return GoogleAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "1:1",
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "",
            };
        }

        /// Gemini imageConfig.imageSize ("K" must be uppercase). 1K and 2K
        /// cost the same tokens; 4K is pricier.
        public static string GoogleImageSize(string detail)
        {
            return Norm(detail, Details) switch
            {
                "high" => "2K",
                "max" => "4K",
                _ => "1K",
            };
        }

        private static string NearestAspect(
            int width,
            int height,
            IReadOnlyList<(string Name, int Width, int Height)> candidates)
        {
            ValidateInputDimensions(width, height);
            var sourceRatio = (double)width / height;
            return candidates
                .OrderBy(candidate => RatioDistance(sourceRatio, (double)candidate.Width / candidate.Height))
                .First()
                .Name;
        }

        private static double RatioDistance(double first, double second)
            => Math.Abs(Math.Log(first / second));

        private static void ValidateInputDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException(
                    $"Input image dimensions must be positive to match its aspect ratio; received {width}x{height}.");
            }
        }

        private static (int Width, int Height) SizeMatchingInput(
            int inputWidth,
            int inputHeight,
            int targetPixels,
            int multiple,
            int maxPixels,
            int maxEdgeExclusive,
            double maxLongShortRatio)
        {
            ValidateInputDimensions(inputWidth, inputHeight);
            if (targetPixels <= 0 || multiple <= 0 || maxPixels <= 0 || maxEdgeExclusive <= multiple)
            {
                throw new ArgumentOutOfRangeException(nameof(targetPixels), "Output size constraints must be positive.");
            }

            var ratio = (double)inputWidth / inputHeight;
            ratio = Math.Clamp(ratio, 1.0 / maxLongShortRatio, maxLongShortRatio);
            var rawHeight = Math.Sqrt((double)targetPixels / ratio);
            var rawWidth = rawHeight * ratio;
            var scale = Math.Min(
                1.0,
                Math.Min(
                    (double)(maxEdgeExclusive - multiple) / rawWidth,
                    (double)(maxEdgeExclusive - multiple) / rawHeight));
            scale = Math.Min(scale, Math.Sqrt((double)maxPixels / (rawWidth * rawHeight)));
            rawWidth *= scale;
            rawHeight *= scale;

            var width = Math.Max(multiple, RoundToMultiple(rawWidth, multiple));
            var height = Math.Max(multiple, RoundToMultiple(rawHeight, multiple));
            while (width >= maxEdgeExclusive
                || height >= maxEdgeExclusive
                || (long)width * height > maxPixels)
            {
                var shrink = Math.Min(
                    (double)(maxEdgeExclusive - multiple) / width,
                    Math.Min(
                        (double)(maxEdgeExclusive - multiple) / height,
                        Math.Sqrt((double)maxPixels / ((long)width * height))));
                if (shrink >= 1.0)
                {
                    shrink = 0.99;
                }
                width = Math.Max(multiple, FloorToMultiple(width * shrink, multiple));
                height = Math.Max(multiple, FloorToMultiple(height * shrink, multiple));
            }
            return (width, height);
        }

        private static int RoundToMultiple(double value, int multiple)
            => (int)Math.Round(value / multiple, MidpointRounding.AwayFromZero) * multiple;

        private static int FloorToMultiple(double value, int multiple)
            => (int)Math.Floor(value / multiple) * multiple;

        private static string Norm(string value, string[] known)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(known, v) >= 0 ? v : known[0];
        }
    }

    /// Executes UI jobs: builds the per-job generator instances (edit variant
    /// when the job has an input image, plain text-to-image otherwise), fans
    /// out in parallel, saves through the standard ImageManager pipeline, and
    /// emits progress events onto the job. Modeled on ReplWorkflow.ProcessOneAsync.
    public class UiJobRunner : IAsyncDisposable
    {
        // Generator keys exposed to the browser. Availability is checked at
        // /api/config time and again defensively at build time.
        // Composer + /api/jobs accept at most this many ordered input images.
        // gpt-image-2 /edits allows up to 16; four is enough for the A+B(+…)
        // gesture without turning the paste zone into a contact sheet.
        public const int MaxInputImages = 4;

        public const string KeyGpt2 = "gpt2";
        public const string KeyGpt1 = "gpt1";
        public const string KeyGpt1Mini = "gpt1-mini";
        public const string KeyIdeogram = "ideogram";
        public const string KeyIdeogramV3 = "ideogram-v3";
        public const string KeyIdeogramV2 = "ideogram-v2";
        public const string KeyRecraft = "recraft";
        public const string KeyRecraftV41Utility = "recraft-v41-utility";
        public const string KeyRecraftV41Pro = "recraft-v41-pro";
        public const string KeyRecraftV41Vector = "recraft-v41-vector";
        public const string KeyRecraftV3 = "recraft-v3";
        public const string KeyRecraftV4 = "recraft-v4";
        public const string KeyRecraftV4Pro = "recraft-v4-pro";
        // Historical "bfl" key remains FLUX.2 Pro Preview so persisted jobs and
        // browser selections keep working.
        public const string KeyBfl = "bfl";
        public const string KeyBflFlux2Pro = "bfl-flux2-pro";
        public const string KeyBflFlux2Max = "bfl-flux2-max";
        public const string KeyBflFlux2Flex = "bfl-flux2-flex";
        public const string KeyBflFlux2Klein4b = "bfl-flux2-klein-4b";
        public const string KeyBflFlux2Klein9bPreview = "bfl-flux2-klein-9b-preview";
        public const string KeyBflFlux2Klein9b = "bfl-flux2-klein-9b";
        public const string KeyBflKontextPro = "bfl-kontext-pro";
        public const string KeyBflKontextMax = "bfl-kontext-max";
        public const string KeyBflFlux11Ultra = "bfl-flux11-ultra";
        public const string KeyBflFlux11 = "bfl-flux11";
        public const string KeyBflFluxPro = "bfl-flux-pro";
        public const string KeyBflFluxDev = "bfl-flux-dev";
        public const string KeyKrea = "krea";
        public const string KeyKreaTurbo = "krea-turbo";
        public const string KeyKreaLarge = "krea-large";
        public const string KeyGoogle = "google";
        public const string KeyGooglePro = "googlepro";
        public const string KeyLocalKlein = "local-klein";
        public const string KeyLocalZImage = "local-zimage";
        public const string KeyGrokWeb = "grok-web";
        public const string KeyGrokWebChat = "grok-web-chat";
        public const string KeyGrokWebVideo = "grok-web-video";
        public const string KeyGrokApi = "grok-api";
        public const string KeyGrokApiPro = "grok-api-pro";
        public const string KeyMetaWeb = "meta-web";

        // Describe targets (image → text). Every one REQUIRES an input image;
        // the server rejects describe jobs without one before they start.
        // The composer prompt (when non-blank) is the describe instruction for
        // the instruction-capable models; Ideogram /describe takes no
        // instruction and always runs its fixed built-in describe.
        // The local InternVL/Qwen describers were removed from the UI catalog
        // 2026-08-05: neither local server exists on the dev box or production,
        // so the targets only produced instant failures. The CLI describer
        // classes remain for the batch workflows.
        public const string KeyDescribeIdeogram = "describe-ideogram";
        public const string KeyDescribeOpenAi = "describe-openai";
        public const string KeyDescribeClaude = "describe-claude";
        public const string KeyDescribeGemini = "describe-gemini";
        public const string KeyDescribeGrok = "describe-grok";

        // Layout map (image → labeled section-map IMAGE). Lives in the
        // describe chooser section (requires an attached image, output
        // options don't apply) but returns a normal image gen-result: Gemini
        // names the main sections/topics with bounding boxes under a strict
        // JSON contract, and the server renders them as a flat-color map
        // with a numbered legend baked into the PNG.
        public const string KeyLayoutMap = "layout-map";

        public static readonly string[] ImageGeneratorKeys =
        {
            KeyGpt2,
            KeyGpt1,
            KeyGpt1Mini,
            KeyIdeogram,
            KeyIdeogramV3,
            KeyIdeogramV2,
            KeyRecraft,
            KeyRecraftV41Utility,
            KeyRecraftV41Pro,
            KeyRecraftV41Vector,
            KeyRecraftV3,
            KeyRecraftV4,
            KeyRecraftV4Pro,
            KeyBfl,
            KeyBflFlux2Pro,
            KeyBflFlux2Max,
            KeyBflFlux2Flex,
            KeyBflFlux2Klein4b,
            KeyBflFlux2Klein9bPreview,
            KeyBflFlux2Klein9b,
            KeyBflKontextPro,
            KeyBflKontextMax,
            KeyBflFlux11Ultra,
            KeyBflFlux11,
            KeyBflFluxPro,
            KeyBflFluxDev,
            KeyKrea,
            KeyKreaTurbo,
            KeyKreaLarge,
            KeyGoogle,
            KeyGooglePro,
            KeyLocalKlein,
            KeyLocalZImage,
            KeyGrokWeb,
            KeyGrokWebChat,
            KeyGrokApi,
            KeyGrokApiPro,
            KeyMetaWeb,
        };

        public static readonly string[] DescribeKeys =
        {
            KeyDescribeIdeogram,
            KeyDescribeOpenAi,
            KeyDescribeClaude,
            KeyDescribeGemini,
            KeyDescribeGrok,
        };

        public static bool IsDescribeKey(string key)
            => DescribeKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        public static bool IsLayoutMapKey(string key)
            => string.Equals(key, KeyLayoutMap, StringComparison.OrdinalIgnoreCase);

        // Analysis targets (describe + layout map) share the describe
        // section's rules: they require an attached image, ignore the output
        // options row, may run with a blank composer prompt, and contribute
        // no cell to the image contact sheet.
        public static bool IsAnalysisKey(string key)
            => IsDescribeKey(key) || IsLayoutMapKey(key);

        public static bool IsImageGeneratorKey(string key)
            => ImageGeneratorKeys.Contains(key, StringComparer.Ordinal);

        // Declared pre-operation default: used as the wire instruction when a
        // describe-only job is submitted with a blank prompt (the server
        // substitutes it BEFORE the job is accepted, and it becomes the job's
        // recorded prompt — never a post-failure fallback).
        public const string DefaultDescribeInstruction =
            "Describe this image in detail. Cover the main subjects, setting, composition, "
            + "style, colors, lighting, and any visible text.";

        // Appended to every instruction-capable describe call (all targets
        // except Ideogram's fixed-instruction /describe) to form the exact wire
        // prompt. Two goals, both user-specified product requirements
        // (2026-08-05): (a) separate the actual description from chatty
        // meta-text — models kept closing with "Would you like me to..."
        // offers, so the reply is forced into a JSON object whose
        // "description" field is ONLY the description and whose "comments"
        // field absorbs everything else; (b) descriptions must be complete and
        // matter-of-fact about every person's visible details (apparent age,
        // sex, appearance) — this project depends on plain, even-handed
        // descriptions of everyone to support its fairness goals.
        public const string DescribeJsonReplyContract =
            "Reply with ONLY a JSON object containing exactly two string fields, and no text outside it:\n"
            + "  \"description\" — the description itself: clear, complete, plain prose describing the "
            + "image as well as possible. No meta-commentary, no offers of further help, no questions, "
            + "no markdown, no mention of these instructions.\n"
            + "  \"comments\" — everything else you would otherwise have said (caveats, uncertainty, "
            + "meta remarks, follow-up offers). Use an empty string if there is nothing.\n"
            + "In the description, please be clear and matter-of-fact about every visible detail, "
            + "including each person's apparent age, sex, and physical appearance. Complete, "
            + "even-handed descriptions of every person are important to this application's goal of "
            + "fair, equal treatment of all groups; do not omit or soften them. "
            + "For everyone in the image who resembles a real-world person, you may mention the "
            + "resemblance — and indeed must.";

        // Describe wire parameters, matched to tools/describe-eval defaults
        // (1200 max tokens) and the Workflow Lab's deterministic temperature.
        private const int DescribeMaxTokens = 1200;
        private const float DescribeTemperature = 0.2f;

        // The only tolerated cosmetic deviation across the structured-reply
        // parsers: a markdown code fence wrapped around the JSON object,
        // stripped deterministically.
        private static string StripMarkdownFence(string raw)
        {
            var s = raw.Trim();
            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = s.IndexOf('\n');
                var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (firstNewline >= 0 && lastFence > firstNewline)
                {
                    s = s.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
                }
            }
            return s;
        }

        // Strict parse of the {description, comments} reply contract. A reply
        // that is not the required JSON object, or whose description is blank,
        // is a hard failure (fail closed — never present chatty free text as
        // if it were the structured description). The only tolerated cosmetic
        // deviation is a markdown code fence around the object, stripped
        // deterministically; a missing "comments" field means no comments.
        private static (string Description, string Comments) ParseDescribeJsonReply(string raw)
        {
            var s = StripMarkdownFence(raw);
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the reply's JSON root is not an object");
                }
                if (!doc.RootElement.TryGetProperty("description", out var description)
                    || description.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    throw new JsonException("the reply lacks a non-empty string \"description\" field");
                }
                var comments = doc.RootElement.TryGetProperty("comments", out var c)
                        && c.ValueKind == JsonValueKind.String
                    ? c.GetString()!.Trim()
                    : "";
                return (description.GetString()!.Trim(), comments);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"describe reply did not follow the required JSON contract ({ex.Message}); reply starts: {Truncate(raw, 300)}");
            }
        }

        // Layout-map wire contract: the whole feature depends on this reply
        // being machine-readable, so it is forced JSON (Gemini additionally
        // gets responseMimeType application/json) and parsed fail-closed.
        // Coordinates use Gemini's native detection convention: integers
        // 0-1000 relative to the image, [ymin, xmin, ymax, xmax].
        public const string LayoutMapJsonContract =
            "Divide this image into its main visual sections and topics. Reply with ONLY a JSON "
            + "object, no text outside it:\n"
            + "{\"summary\": \"<one concise sentence stating the image's overall subject>\", "
            + "\"regions\": [{\"label\": \"<2-4 word name of this section or topic>\", "
            + "\"box\": [ymin, xmin, ymax, xmax]}]}\n"
            + "Rules: box coordinates are integers from 0 to 1000 relative to the image "
            + "(y increases downward, x increases rightward), with ymin < ymax and xmin < xmax. "
            + "Return 1 to 8 regions (usually 3 to 6), ordered from the most background/largest "
            + "to the most foreground/smallest. Together the regions should cover every major "
            + "area of the image. Labels must be concrete and specific to THIS image.";

        private const int LayoutMapMaxRegions = 8;

        // Strict parse of the layout-map reply. Non-object JSON, a blank
        // summary, zero or more than 8 regions, a blank label, or a malformed
        // box (wrong arity, out of 0-1000 range, or inverted min/max) is a
        // hard failure for the whole target. Numeric box values are accepted
        // as any JSON number and rounded to integers — a cosmetic tolerance
        // like the markdown fence, never a correlation guess. Public because
        // it is a pure contract function exercised directly by tests.
        public static (string Summary, List<UiLayoutMapRegion> Regions) ParseLayoutMapJsonReply(string raw)
        {
            var s = StripMarkdownFence(raw);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("the reply's JSON root is not an object");
                }
                if (!root.TryGetProperty("summary", out var summaryEl)
                    || summaryEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(summaryEl.GetString()))
                {
                    throw new JsonException("the reply lacks a non-empty string \"summary\" field");
                }
                if (!root.TryGetProperty("regions", out var regionsEl)
                    || regionsEl.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("the reply lacks a \"regions\" array");
                }
                var count = regionsEl.GetArrayLength();
                if (count < 1 || count > LayoutMapMaxRegions)
                {
                    throw new JsonException(
                        $"the reply has {count} regions; the contract requires 1 to {LayoutMapMaxRegions}");
                }

                var regions = new List<UiLayoutMapRegion>(count);
                var index = 0;
                foreach (var regionEl in regionsEl.EnumerateArray())
                {
                    index++;
                    if (regionEl.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException($"region {index} is not an object");
                    }
                    if (!regionEl.TryGetProperty("label", out var labelEl)
                        || labelEl.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(labelEl.GetString()))
                    {
                        throw new JsonException($"region {index} lacks a non-empty string \"label\"");
                    }
                    var label = labelEl.GetString()!.Trim();
                    if (label.Length > 200)
                    {
                        throw new JsonException(
                            $"region {index}'s label is {label.Length} characters; the contract asks for a short name");
                    }
                    if (!regionEl.TryGetProperty("box", out var boxEl)
                        || boxEl.ValueKind != JsonValueKind.Array
                        || boxEl.GetArrayLength() != 4)
                    {
                        throw new JsonException($"region {index} lacks a 4-element \"box\" array");
                    }
                    var box = new int[4];
                    var boxIndex = 0;
                    foreach (var coordEl in boxEl.EnumerateArray())
                    {
                        if (coordEl.ValueKind != JsonValueKind.Number)
                        {
                            throw new JsonException($"region {index}'s box contains a non-numeric coordinate");
                        }
                        var value = (int)Math.Round(coordEl.GetDouble(), MidpointRounding.AwayFromZero);
                        if (value < 0 || value > 1000)
                        {
                            throw new JsonException(
                                $"region {index}'s box coordinate {value} is outside 0-1000");
                        }
                        box[boxIndex++] = value;
                    }
                    if (box[0] >= box[2] || box[1] >= box[3])
                    {
                        throw new JsonException(
                            $"region {index}'s box [{string.Join(", ", box)}] does not satisfy ymin < ymax and xmin < xmax");
                    }
                    regions.Add(new UiLayoutMapRegion(label, box[0], box[1], box[2], box[3]));
                }
                return (summaryEl.GetString()!.Trim(), regions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"layout-map reply did not follow the required JSON contract ({ex.Message}); reply starts: {Truncate(raw, 300)}");
            }
        }

        // Single source of truth for which UI targets accept an input image.
        // Exposed to the frontend via /api/config (per-generator imageCapable
        // flag) and consulted in BuildGenerator, so the two can't drift.
        // Everything NOT in this set still runs when a job carries an input image — it
        // just receives the prompt text only. That "sans image" behavior is a
        // user-specified product requirement (2026-07-28): keep every target
        // usable on image jobs, and let the UI badge the ones that won't see
        // the attachment.
        public static readonly string[] ImageCapableKeys =
        {
            KeyGpt2,
            KeyGrokApi,
            KeyGrokApiPro,
            KeyGoogle,
            KeyGooglePro,
            KeyBfl,
            KeyBflFlux2Pro,
            KeyBflFlux2Max,
            KeyBflFlux2Flex,
            KeyBflFlux2Klein4b,
            KeyBflFlux2Klein9bPreview,
            KeyBflFlux2Klein9b,
            KeyBflKontextPro,
            KeyBflKontextMax,
            KeyBflFlux11Ultra,
            KeyBflFlux11,
            KeyBflFluxDev,
            KeyKrea,
            KeyKreaTurbo,
            KeyKreaLarge,
            KeyIdeogram,
            KeyIdeogramV3,
            KeyRecraft,
            KeyRecraftV41Utility,
            KeyRecraftV41Pro,
            KeyRecraftV41Vector,
            KeyRecraftV3,
            KeyRecraftV4,
            KeyRecraftV4Pro,
        };

        public static bool IsImageCapable(string key)
            => ImageCapableKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        public bool IsImageCapableForCurrentSettings(string key)
            => IsImageCapable(key)
                || ((key.Equals(KeyGrokWeb, StringComparison.OrdinalIgnoreCase)
                        || key.Equals(KeyGrokWebChat, StringComparison.OrdinalIgnoreCase))
                    && _grokWebStatsigSigner != null);

        public string? DescribeImageCapabilityProblem(string key)
            => key.Equals(KeyGrokWeb, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(KeyGrokWebChat, StringComparison.OrdinalIgnoreCase)
                ? _grokWebStatsigProblem
                : null;

        public static bool IsRecraftKey(string key)
            => key is KeyRecraft
                or KeyRecraftV41Utility
                or KeyRecraftV41Pro
                or KeyRecraftV41Vector
                or KeyRecraftV3
                or KeyRecraftV4
                or KeyRecraftV4Pro;

        // States, per selected generator, exactly what function the job's
        // input image served. Rendered under the INPUT cell of the combined
        // contact sheet. Must stay truthful to BuildGenerator's routing: edit
        // generators consume it as the edit source, Recraft as the
        // image-to-image source, the reference-style targets as a
        // style/reference guide, and text-only targets never receive it
        // (user-specified attachment policy, 2026-07-28).
        private string DescribeInputImageFunction(string key) => key switch
        {
            KeyGpt2 or KeyGrokApi or KeyGrokApiPro => "edit source",
            KeyGrokWeb when _grokWebStatsigSigner != null => "edit source",
            KeyGrokWebChat => "edit source (chat door)",
            KeyRecraft or KeyRecraftV41Utility or KeyRecraftV41Pro
                or KeyRecraftV41Vector or KeyRecraftV3 or KeyRecraftV4
                or KeyRecraftV4Pro => "image-to-image source",
            KeyBfl or KeyBflFlux2Pro
                or KeyBflFlux2Max or KeyBflFlux2Flex or KeyBflFlux2Klein4b
                or KeyBflFlux2Klein9bPreview or KeyBflFlux2Klein9b
                or KeyBflKontextPro or KeyBflKontextMax => "edit/reference source",
            KeyBflFlux11Ultra or KeyBflFlux11 or KeyBflFluxDev => "image remix/reference source",
            KeyIdeogram => "image remix source",
            KeyGoogle or KeyGooglePro or KeyIdeogramV3
                or KeyKrea or KeyKreaTurbo or KeyKreaLarge => "style/reference image",
            KeyGrokWebVideo => "video source",
            _ when IsDescribeKey(key) => "describe source (image \u2192 text)",
            _ when IsLayoutMapKey(key) => "layout-map source (image \u2192 labeled section map)",
            _ => "NOT sent (text-only target, prompt only)",
        };

        public string BuildInputImageRoleText(IReadOnlyList<string> generatorKeys)
        {
            var perGenerator = generatorKeys
                .Select(k => $"{k}: {DescribeInputImageFunction(k)}");
            return $"Input image attached to this job (not a generated result). Function per generator - {string.Join("; ", perGenerator)}";
        }

        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly RunOptions _options;
        private readonly ImageManager _imageManager;
        private readonly GeneratorGroups _generatorGroups;
        private readonly GrokWebStatsigSigner? _grokWebStatsigSigner;
        private readonly string? _grokWebStatsigProblem;
        private const string MetaWebDisabledProblem =
            "Chromium-backed meta-web is disabled in the UI";
        private readonly object _comfyProbeLock = new();
        private DateTime _comfyProbeExpiresAt;
        private string? _cachedComfyProbeProblem;

        // Endpoint work is scheduled independently by provider/account. The
        // finalization gate protects ImageSharp contact-sheet memory without
        // blocking unrelated remote targets, while admission bounds retained
        // job tasks and queued descriptors on a resident shared host.
        private readonly SemaphoreSlim _finalizationLimit;
        private readonly UiTargetScheduler _targetScheduler;
        private readonly int _maxPendingJobs;
        private int _pendingJobs;

        public bool IsGrokBrowserWarm => false;
        public bool IsMetaBrowserWarm => false;
        public bool GrokBrowserConfigured => false;
        public bool MetaBrowserConfigured => false;

        // Non-null exactly when EnableB2ImageHosting: finished result/grid
        // files upload to the public B2 bucket and gen-result/grid events
        // carry B2 capability URLs. See docs/b2-image-hosting-plan.md.
        private readonly B2StorageClient? _b2;

        public UiJobRunner(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            _settings = settings;
            _stats = stats;
            _options = options;
            GrokWebStatsigSigner.TryCreateFromSettings(
                settings,
                out _grokWebStatsigSigner,
                out _grokWebStatsigProblem);
            _b2 = settings.EnableB2ImageHosting ? new B2StorageClient(settings) : null;
            _finalizationLimit = new SemaphoreSlim(ValidateUiConcurrency(
                nameof(settings.UiMaxConcurrentJobs), settings.UiMaxConcurrentJobs));
            _targetScheduler = new UiTargetScheduler(
                settings.UiMaxConcurrentGenerators,
                settings.UiTargetConcurrency);
            _maxPendingJobs = ValidatePendingJobLimit(settings.UiMaxPendingJobs);
            _imageManager = new ImageManager(settings, stats);
            _generatorGroups = new GeneratorGroups(settings, concurrency: 1, stats);
        }

        private static int ValidateUiConcurrency(string settingName, int value)
        {
            if (value < 1 || value > 32)
            {
                throw new InvalidOperationException(
                    $"{settingName} must be between 1 and 32; got {value}.");
            }
            return value;
        }

        private static int ValidatePendingJobLimit(int value)
        {
            if (value < 1 || value > 10000)
            {
                throw new InvalidOperationException(
                    $"{nameof(Settings.UiMaxPendingJobs)} must be between 1 and 10000; got {value}.");
            }
            return value;
        }

        public bool TryAcquireJobAdmission(out IDisposable? admission)
        {
            while (true)
            {
                var current = Volatile.Read(ref _pendingJobs);
                if (current >= _maxPendingJobs)
                {
                    admission = null;
                    return false;
                }
                if (Interlocked.CompareExchange(ref _pendingJobs, current + 1, current) == current)
                {
                    admission = new JobAdmission(this);
                    return true;
                }
            }
        }

        internal UiTargetSchedulerSnapshot SchedulerSnapshot() => _targetScheduler.Snapshot();

        public int PendingJobCount => Volatile.Read(ref _pendingJobs);
        public int MaxPendingJobs => _maxPendingJobs;

        private sealed class JobAdmission : IDisposable
        {
            private UiJobRunner? _owner;

            public JobAdmission(UiJobRunner owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    Interlocked.Decrement(ref owner._pendingJobs);
                }
            }
        }

        public string? ResolveGrokWebCookiePath()
        {
            var cookiePath = !string.IsNullOrWhiteSpace(_options.GrokWebCookies)
                ? _options.GrokWebCookies
                : _settings.GrokWebCookiePath;
            if (string.IsNullOrWhiteSpace(cookiePath)) return null;
            var expanded = Settings.ExpandPath(cookiePath);
            return File.Exists(expanded) ? expanded : null;
        }

        private static RecraftModel RecraftModelForKey(string key) => key switch
        {
            KeyRecraft => RecraftModel.recraftv4_1,
            KeyRecraftV41Utility => RecraftModel.recraftv4_1_utility,
            KeyRecraftV41Pro => RecraftModel.recraftv4_1_pro,
            KeyRecraftV41Vector => RecraftModel.recraftv4_1_vector,
            KeyRecraftV3 => RecraftModel.recraftv3,
            KeyRecraftV4 => RecraftModel.recraftv4,
            KeyRecraftV4Pro => RecraftModel.recraftv4_pro,
            _ => throw new ArgumentException($"Unknown Recraft generator key '{key}'.", nameof(key)),
        };

        private static ImageGeneratorApiType RecraftApiTypeForKey(string key) => key switch
        {
            KeyRecraft => ImageGeneratorApiType.RecraftV41,
            KeyRecraftV41Utility => ImageGeneratorApiType.RecraftV41Utility,
            KeyRecraftV41Pro => ImageGeneratorApiType.RecraftV41Pro,
            KeyRecraftV41Vector => ImageGeneratorApiType.RecraftV41Vector,
            KeyRecraftV3 => ImageGeneratorApiType.Recraft,
            KeyRecraftV4 => ImageGeneratorApiType.RecraftV4,
            KeyRecraftV4Pro => ImageGeneratorApiType.RecraftV4Pro,
            _ => throw new ArgumentException($"Unknown Recraft generator key '{key}'.", nameof(key)),
        };

        public string? DescribeAvailabilityProblem(string key) => key switch
        {
            KeyGpt2 or KeyGpt1 or KeyGpt1Mini
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GptImage2, _settings),
            KeyIdeogram
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV4, _settings),
            KeyIdeogramV3
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV3, _settings),
            KeyIdeogramV2
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.Ideogram, _settings),
            KeyRecraft or KeyRecraftV41Utility or KeyRecraftV41Pro
                or KeyRecraftV41Vector or KeyRecraftV3 or KeyRecraftV4
                or KeyRecraftV4Pro
                => ProviderKeyValidator.DescribeKeyProblem(RecraftApiTypeForKey(key), _settings),
            KeyBflFluxPro
                => "disabled after the hosted compatibility endpoint returned HTTP 403",
            KeyBflFlux2Klein4b or KeyBflFlux2Klein9bPreview or KeyBflFlux2Klein9b
                => "disabled by operator; these hosted Klein targets are not currently usable",
            KeyBfl or KeyBflFlux2Pro or KeyBflFlux2Max or KeyBflFlux2Flex
                or KeyBflKontextPro or KeyBflKontextMax or KeyBflFlux11Ultra
                or KeyBflFlux11 or KeyBflFluxDev
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.BFLFlux2ProPreview, _settings),
            KeyKrea or KeyKreaTurbo or KeyKreaLarge
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.Krea2Medium, _settings),
            KeyGoogle or KeyGooglePro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GoogleNanoBananaPro, _settings),
            KeyLocalKlein
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalFlux2Klein),
            KeyLocalZImage
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalZImage),
            // Text-to-image needs only cookies. Signed app-chat features
            // validate their deployment material separately.
            KeyGrokWeb => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : null,
            KeyGrokWebVideo => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : _grokWebStatsigProblem,
            // Chat-door image path: always signed app-chat, so it needs both
            // the cookie file and current statsig signing material.
            KeyGrokWebChat => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : _grokWebStatsigProblem,
            KeyGrokApi or KeyGrokApiPro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GrokImagine, _settings),
            KeyMetaWeb => MetaWebDisabledProblem,
            // Describe targets: gated on the same provider keys as their image
            // siblings; the local vision models share the "not installed"
            // EnableLocalGenerators gate (they are separate local servers, not
            // ComfyUI, so no ComfyUI probe applies).
            KeyDescribeIdeogram
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.IdeogramApiKey), _settings.IdeogramApiKey),
            KeyDescribeOpenAi
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.OpenAIApiKey), _settings.OpenAIApiKey),
            KeyDescribeClaude
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.AnthropicApiKey), _settings.AnthropicApiKey),
            KeyDescribeGemini
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.GoogleGeminiApiKey), _settings.GoogleGeminiApiKey),
            KeyDescribeGrok
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.XAIGrokApiKey), _settings.XAIGrokApiKey),
            // Layout map rides the same Gemini vision transport as
            // describe-gemini, so it shares that key gate exactly.
            KeyLayoutMap
                => ProviderKeyValidator.DescribeTextKeyProblem(nameof(Settings.GoogleGeminiApiKey), _settings.GoogleGeminiApiKey),
            _ => $"unknown generator '{key}'",
        };

        public bool IsAvailable(string key) => DescribeAvailabilityProblem(key) == null;

        private string? DescribeComfyAvailability(ImageGeneratorApiType apiType)
        {
            var settingsProblem = ProviderKeyValidator.DescribeKeyProblem(apiType, _settings);
            if (settingsProblem != null)
            {
                return settingsProblem;
            }

            lock (_comfyProbeLock)
            {
                if (DateTime.UtcNow < _comfyProbeExpiresAt)
                {
                    return _cachedComfyProbeProblem;
                }

                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var url = $"{_settings.ComfyUIBaseUrl.TrimEnd('/')}/system_stats";
                    using var response = client.GetAsync(url).GetAwaiter().GetResult();
                    _cachedComfyProbeProblem = response.IsSuccessStatusCode
                        ? null
                        : $"ComfyUI at {_settings.ComfyUIBaseUrl} returned HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
                    _cachedComfyProbeProblem =
                        $"ComfyUI is not reachable at {_settings.ComfyUIBaseUrl}: {ex.GetBaseException().Message}";
                }

                _comfyProbeExpiresAt = DateTime.UtcNow.AddSeconds(5);
                return _cachedComfyProbeProblem;
            }
        }

        public async Task RunJobAsync(UiJob job, UiJobSpec spec)
        {
            job.Emit(new
            {
                type = "job-queued",
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            var finalizationAcquired = false;
            try
            {
                job.Emit(new
                {
                    type = "job-start",
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                var imageLabel = job.InputImageCount switch
                {
                    0 => "none",
                    1 => job.InputImagePath,
                    _ => $"{job.InputImageCount} images (primary {Path.GetFileName(job.InputImagePath)})",
                };
                Logger.Log($"[ui #{job.Id}] START ({spec.GeneratorKeys.Count} gen(s), image={imageLabel}): {job.Prompt}");
                if (job.InputImageCount > 1)
                {
                    var others = spec.GeneratorKeys
                        .Where(k => !string.Equals(k, KeyGpt2, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (others.Count > 0)
                    {
                        Logger.Log(
                            $"[ui #{job.Id}] {job.InputImageCount} input images attached; "
                            + $"gpt-image-2 received all {job.InputImageCount}; "
                            + $"other generators ({string.Join(", ", others)}) received only the first.");
                    }
                    else
                    {
                        Logger.Log(
                            $"[ui #{job.Id}] {job.InputImageCount} input images attached; "
                            + $"gpt-image-2 received all {job.InputImageCount}.");
                    }
                }

                var pd = new PromptDetails();
                pd.ReplacePrompt(job.Prompt, job.Prompt, TransformationType.InitialPrompt);

                var tasks = spec.GeneratorKeys.Select(key =>
                {
                    var lane = UiTargetScheduler.ResolveLane(key, job.HasInputImage);
                    job.Emit(new
                    {
                        type = "gen-queued",
                        gen = key,
                        target = lane,
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    return _targetScheduler.ScheduleAsync(
                        lane,
                        job.CreatedBy,
                        () => RunOneAsync(job, spec, key, pd));
                }).ToArray();
                var results = await Task.WhenAll(tasks);

                // Build + save the standard combined contact sheet for the
                // archive; never popped open (the browser IS the viewer here).
                // Jobs with an input image get it rendered as the sheet's
                // first cell, with an explicit statement of what each
                // selected generator did (or didn't do) with it.
                await _finalizationLimit.WaitAsync();
                finalizationAcquired = true;
                try
                {
                    // Describe results are text-only, layout maps are derived
                    // analysis artifacts, and videos are already directly
                    // playable/downloadable; none contributes a cell to an
                    // image contact sheet. A job containing only those result
                    // kinds builds no sheet at all.
                    var sheetResults = results
                        .Where(r =>
                            !IsAnalysisKey(r.ImageGeneratorDescription)
                            && !string.Equals(
                                r.ImageGeneratorDescription,
                                KeyGrokWebVideo,
                                StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (sheetResults.Length > 0)
                    {
                        var sheetKeys = spec.GeneratorKeys
                            .Where(k =>
                                !IsAnalysisKey(k)
                                && !string.Equals(
                                    k,
                                    KeyGrokWebVideo,
                                    StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var combined = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                            sheetResults, job.Prompt, _settings, openWhenDone: false,
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                            inputImageRole: job.HasInputImage ? BuildInputImageRoleText(sheetKeys) : null);
                        if (!string.IsNullOrEmpty(combined) && File.Exists(combined))
                        {
                            job.StoreImagePath("grid", 0, combined, "image/png");
                            // Retry x3 inside; a final upload failure throws to
                            // the catch below, so the grid link is absent rather
                            // than silently local (fail closed, no substitutes).
                            var gridUrl = _b2 != null
                                ? await UploadResultToB2Async(job, "grid", 0, combined, "image/png")
                                : $"/api/jobs/{job.Id}/images/grid/0";
                            job.Emit(new { type = "grid", url = gridUrl, path = combined });
                        }
                        Logger.Log($"[ui #{job.Id}] grid saved: {combined}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ui #{job.Id}] grid build failed: {ex.Message}");
                }

                // Production storage mode: local raws whose uploads were
                // checksum-verified are deleted once nothing here needs them
                // (contact sheet composed, durable thumbs forced during
                // eviction). B2 is the raw-byte source of truth on such
                // installs; failures delete nothing.
                if (_b2 != null && !_settings.B2KeepLocalRawImages)
                {
                    try
                    {
                        var evicted = job.EvictHostedLocalRaws();
                        if (evicted > 0)
                        {
                            Logger.Log($"[ui #{job.Id}] evicted {evicted} local raw file(s) after verified B2 upload (B2KeepLocalRawImages=false)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] local raw eviction failed: {ex.Message}");
                    }
                }

                foreach (var result in results)
                {
                    result.ReleaseImageData();
                }
                Array.Clear(results);
                Array.Clear(tasks);
            }
            finally
            {
                job.Emit(new { type = "job-done" });
                job.MarkDone();
                if (finalizationAcquired)
                {
                    // Completed UI jobs no longer own any ImageSharp canvases.
                    // Keep cleanup under the same gate as contact-sheet rendering
                    // so concurrent jobs cannot multiply peak native allocations.
                    try
                    {
                        Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Aggressive,
                            blocking: true,
                            compacting: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] post-job image-memory cleanup failed: {ex.Message}");
                    }
                    finally
                    {
                        _finalizationLimit.Release();
                    }
                }
                Logger.Log($"[ui #{job.Id}] DONE");
            }
        }

        /// Uploads one durably saved result file to B2 with the decided retry
        /// policy (3 attempts, short backoff) and persists the object key +
        /// fileId. Returns the public capability URL. Throws after the final
        /// attempt — the caller records the image as FAILED; a local URL is
        /// never substituted (owner decision 2026-08-05: silent local serving
        /// would mask upload failures and refill the disk-constrained host).
        private async Task<string> UploadResultToB2Async(UiJob job, string genKey, int index, string localPath, string contentType)
        {
            var objectKey = B2StorageClient.BuildObjectKey(
                job.Id, genKey, index, ExtensionForHostedFile(contentType, localPath));
            const int attempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // The local save's descriptive filename (sanitized prompt +
                    // generator + index) becomes the browser's save-as name via
                    // b2-content-disposition; without it a right-click save
                    // would get the opaque capability key.
                    var fileId = await _b2!.UploadFileAsync(
                        localPath, objectKey, contentType, Path.GetFileName(localPath), CancellationToken.None);
                    if (!job.StoreCdnReference(genKey, index, objectKey, fileId))
                    {
                        throw new InvalidOperationException(
                            $"uploaded {objectKey} but could not persist its CDN reference for {genKey}/{index}");
                    }
                    return _b2.DownloadUrlFor(objectKey);
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    Logger.Log($"[ui #{job.Id}]   B2 upload attempt {attempt}/{attempts} for {genKey}/{index} failed: {ex.Message}; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        /// Resolves result-image bytes for internal reuse (currently the
        /// video-source upload). Local disk first; when the local raw was
        /// evicted after its verified B2 upload, the exact recorded object is
        /// fetched back and its SHA-256 must match the recorded content hash —
        /// exact identity, never a guess. Returns null when the image is
        /// neither locally readable nor verifiably hosted.
        public async Task<(byte[] Bytes, string ContentType)?> TryGetImageBytesIncludingHostedAsync(
            UiJob job, string gen, int index)
        {
            if (job.TryGetImage(gen, index, out var bytes, out var contentType))
            {
                return (bytes, contentType);
            }
            if (_b2 == null)
            {
                return null;
            }
            var key = $"{gen}/{index}";
            var info = job.ListPersistedImages()
                .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (info == null
                || string.IsNullOrWhiteSpace(info.CdnKey)
                || string.IsNullOrWhiteSpace(info.ContentSha256))
            {
                return null;
            }
            var downloaded = await _b2.DownloadBytesAsync(info.CdnKey, CancellationToken.None);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloaded));
            if (!string.Equals(sha, info.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Hosted copy of {job.Id}/{key} does not match its recorded SHA-256 — refusing to reuse unverifiable bytes.");
            }
            return (downloaded, info.ContentType);
        }

        // Hosted-thumb regeneration: expired disk thumbs are rebuilt on demand
        // from the exact recorded B2 object when the local original was
        // evicted. Single-flight per image (Lazy so a GetOrAdd race never
        // starts a second download) and a small global cap so opening an old
        // archive day cannot stampede B2 with dozens of full-original fetches.
        private static readonly SemaphoreSlim ThumbRegenLimit = new(3);
        private readonly ConcurrentDictionary<string, Lazy<Task<(string Path, string ContentType)?>>> _thumbRegens = new();

        public async Task<(string Path, string ContentType)?> TryRebuildCardPreviewFromHostedAsync(
            UiJob job, string gen, int n)
        {
            if (_b2 == null)
            {
                return null;
            }
            var flightKey = $"{job.Id}/{gen}/{n}";
            var lazy = _thumbRegens.GetOrAdd(
                flightKey,
                _ => new Lazy<Task<(string, string)?>>(() => RebuildCardPreviewFromHostedAsync(job, gen, n)));
            try
            {
                return await lazy.Value;
            }
            finally
            {
                _thumbRegens.TryRemove(flightKey, out _);
            }
        }

        private async Task<(string Path, string ContentType)?> RebuildCardPreviewFromHostedAsync(
            UiJob job, string gen, int n)
        {
            var key = $"{gen}/{n}";
            var info = job.ListPersistedImages()
                .FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (info == null
                || string.IsNullOrWhiteSpace(info.CdnKey)
                || string.IsNullOrWhiteSpace(info.ContentSha256)
                || !info.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            await ThumbRegenLimit.WaitAsync();
            try
            {
                // A concurrent request for a sibling image of the same job may
                // have rebuilt this thumb while we queued.
                if (job.TryGetCardPreviewPath(gen, n, out var existingPath, out var existingType))
                {
                    return (existingPath, existingType);
                }
                var temp = Path.Combine(Path.GetTempPath(), $"mic-thumb-regen-{Guid.NewGuid():N}");
                try
                {
                    await _b2.DownloadToFileAsync(info.CdnKey, temp, CancellationToken.None);
                    string sha;
                    using (var stream = File.OpenRead(temp))
                    {
                        sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    }
                    if (!string.Equals(sha, info.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"UI job {job.Id}: hosted copy of {key} does not match its recorded SHA-256 — refusing to rebuild its thumb.");
                        return null;
                    }
                    if (!job.TryBuildCardPreviewFromSource(gen, n, temp, info.ContentType, out var path, out var type))
                    {
                        return null;
                    }
                    return (path, type);
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* best-effort temp cleanup */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI job {job.Id}: hosted thumb rebuild for {key} failed: {ex.Message}");
                return null;
            }
            finally
            {
                ThumbRegenLimit.Release();
            }
        }

        internal static string ExtensionForHostedFile(string contentType, string localPath)
        {
            switch (contentType?.ToLowerInvariant())
            {
                case "image/png": return "png";
                case "image/jpeg": return "jpg";
                case "image/webp": return "webp";
                case "image/gif": return "gif";
                case "video/mp4": return "mp4";
            }
            var fromPath = Path.GetExtension(localPath)?.TrimStart('.');
            if (string.IsNullOrWhiteSpace(fromPath))
            {
                throw new InvalidOperationException(
                    $"Cannot determine a hosted-file extension for content type '{contentType}' and path '{localPath}'.");
            }
            return fromPath.ToLowerInvariant();
        }

        private async Task<TaskProcessResult> RunOneAsync(UiJob job, UiJobSpec spec, string key, PromptDetails pd)
        {
            if (IsDescribeKey(key))
            {
                return await RunDescribeOneAsync(job, key, pd);
            }
            if (IsLayoutMapKey(key))
            {
                return await RunLayoutMapOneAsync(job, key, pd);
            }

            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            // An explicit N>1 is a deliberate "give me N of these" — so we run N
            // independent single-image generations and return every one. They run
            // sequentially (the save-path filename scheme collides for the same
            // generator+prompt within a second) and give up the mid-stream partial
            // preview when N>1 (OpenAI forbids streaming with n>1, and parallel
            // partials would fight over one cell). All N are saved and shown.
            // Video is inherently single-output, so grok-web-video ignores N.
            var maxImages = key switch
            {
                KeyGrokWebVideo => 1,
                KeyRecraft or KeyRecraftV41Utility or KeyRecraftV41Pro
                    or KeyRecraftV41Vector or KeyRecraftV3 or KeyRecraftV4
                    or KeyRecraftV4Pro => 6,
                KeyIdeogram or KeyIdeogramV3 => 8,
                _ => 10,
            };
            var want = Math.Clamp(spec.ImageCount, 1, maxImages);
            var enablePartials = want == 1;

            var urls = new List<string>();
            // Local card-thumb URLs, index-aligned with `urls`. Only emitted
            // when B2 hosting is on: appending ?thumb=1 to a B2 URL would be
            // ignored and pull full-resolution originals into every card —
            // the exact regression the card-image rule exists to prevent.
            var thumbs = new List<string>();
            var merged = new TaskProcessResult
            {
                PromptDetails = pd,
                ImageGeneratorDescription = key,
                ContentType = "image/png",
            };
            string firstImagePath = null;
            byte[] firstImageBytes = null;
            var mediaType = "";
            decimal totalCost = 0m;
            long createMs = 0, downloadMs = 0;
            string label = null;
            string firstError = null;

            for (var attempt = 0; attempt < want; attempt++)
            {
                GrokWebClient? grokWebClient = null;
                PromptDetails? copy = null;
                TaskProcessResult? attemptResult = null;
                try
                {
                    IImageGenerator generator;
                    if (key is KeyGrokWeb or KeyGrokWebVideo or KeyGrokWebChat)
                    {
                        var cookiePath = ResolveGrokWebCookiePath()
                            ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                        grokWebClient = GrokWebClient.FromCookieFile(
                            cookiePath,
                            statsigSigner: _grokWebStatsigSigner);
                        generator = key switch
                        {
                            KeyGrokWebVideo => await BuildGrokWebVideoAsync(grokWebClient, job, spec),
                            KeyGrokWebChat => await BuildGrokWebChatEditAsync(grokWebClient, spec, job),
                            _ => job.HasInputImage && _grokWebStatsigSigner != null
                                ? await BuildGrokWebEditAsync(grokWebClient, spec, job)
                                : BuildGrokWeb(grokWebClient, spec),
                        };
                    }
                    else if (key == KeyMetaWeb)
                    {
                        throw new InvalidOperationException(MetaWebDisabledProblem);
                    }
                    else
                    {
                        generator = BuildGenerator(key, spec, job, enablePartials);
                    }

                    copy = pd.Copy();
                    // Each generator gets an independent prompt copy. Append only
                    // that endpoint's configured text and record the exact wire
                    // prompt in the normal transformation/archive pipeline.
                    if (spec.GeneratorExtraTexts.TryGetValue(key, out var extraText)
                        && !string.IsNullOrWhiteSpace(extraText))
                    {
                        var suffixed = $"{copy.Prompt}\n\n{extraText.Trim()}";
                        copy.ReplacePrompt(suffixed, suffixed, TransformationType.ManualSuffixation);
                    }
                    else if (key == KeyGpt2)
                    {
                        // Loud on purpose: gpt-image-2 without the default
                        // anti-murk suffix reliably comes back darker, and this once went
                        // unnoticed for two days (2026-07-31 → 08-02).
                        Logger.Log($"[ui #{job.Id}]   gpt-image-2 extra text is blank for this call — anti-murk guidance was not sent");
                    }
                    if (key == KeyGrokWebVideo && !string.IsNullOrWhiteSpace(job.SourceGenerator))
                    {
                        copy.RuntimeMeta["sourceJobId"] = job.SourceJobId;
                        copy.RuntimeMeta["sourceGenerator"] = job.SourceGenerator;
                        copy.RuntimeMeta["sourceIndex"] = job.SourceIndex?.ToString() ?? "";
                    }
                    // Per single-image estimate from each generator's GetCost.
                    // Estimates, not bills — but good enough for calibrating
                    // which providers are worth their price.
                    var costEstimate = generator.GetCost();
                    var attemptTag = want > 1 ? $"  [{attempt + 1}/{want}]" : "";
                    Logger.Log($"[ui #{job.Id}]   -> {generator.GetGeneratorSpecPart()} (~${costEstimate:0.###}){attemptTag}");
                    attemptResult = await GenerationArchive.ExecuteAndSaveAsync(
                        generator,
                        copy,
                        _imageManager,
                        new GenerationArchiveContext
                        {
                            Source = "ui",
                            ExternalJobId = job.Id,
                            GeneratorKey = key,
                        },
                        // The web archive already has raw files, structured
                        // metadata/events, and one labeled combined sheet.
                        // Five additional full-resolution annotation variants
                        // per image multiply decode/render RAM, CPU, and disk
                        // without adding information to the web product.
                        saveAnnotatedVariants: false);
                    var result = attemptResult;

                    if (result.IsSuccess
                        && !string.IsNullOrEmpty(result.GeneratedMediaPath)
                        && File.Exists(result.GeneratedMediaPath))
                    {
                        mediaType = string.IsNullOrWhiteSpace(result.GeneratedMediaContentType)
                            ? "application/octet-stream"
                            : result.GeneratedMediaContentType;
                        var idx = urls.Count;
                        job.StoreImagePath(key, idx, result.GeneratedMediaPath, mediaType);
                        if (_b2 != null && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        {
                            // Owner decision 2026-08-11 (v2 scope-lift): videos
                            // upload like raster results — same retry x3 then
                            // visible hard-fail, never a local-URL substitute.
                            var hostedUrl = await UploadResultToB2Async(
                                job, key, idx, result.GeneratedMediaPath, mediaType);
                            urls.Add(hostedUrl);
                        }
                        else
                        {
                            // SVG and other non-video media stay local (v1 scope).
                            urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                        }
                        thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{idx}?thumb=1");
                        merged.GeneratedMediaPath = result.GeneratedMediaPath;
                        merged.GeneratedMediaContentType = result.GeneratedMediaContentType;
                    }
                    else if (result.IsSuccess)
                    {
                        int i = 0;
                        foreach (var bytes in result.GetAllImages)
                        {
                            var idx = urls.Count;
                            var rawPath = result.GetSavedRawImagePath(i);
                            // Prefer path-only when ImageManager already wrote the file;
                            // otherwise keep bytes in RAM until something persists them.
                            if (!string.IsNullOrWhiteSpace(rawPath) && File.Exists(rawPath))
                            {
                                job.StoreImagePath(key, idx, rawPath, result.ContentType ?? "image/png");
                                firstImagePath ??= rawPath;
                                merged.SetSavedRawImagePath(idx, rawPath);
                                if (_b2 != null)
                                {
                                    // Retry x3 inside; throws on final failure,
                                    // failing this image visibly. Never a local
                                    // URL substitute.
                                    var hostedUrl = await UploadResultToB2Async(
                                        job, key, idx, rawPath, result.ContentType ?? "image/png");
                                    urls.Add(hostedUrl);
                                }
                                else
                                {
                                    urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                                }
                            }
                            else if (_b2 != null)
                            {
                                // Hosting uploads stream from the durable saved
                                // file; an image that never reached disk cannot
                                // be hosted and must not fall back to local
                                // serving (fail closed).
                                throw new InvalidOperationException(
                                    $"{key} returned image {i} without a durable saved file; B2 hosting requires the saved raw file.");
                            }
                            else
                            {
                                job.StoreImage(
                                    key,
                                    idx,
                                    bytes,
                                    result.ContentType ?? "image/png",
                                    rawPath);
                                firstImageBytes ??= bytes;
                                merged.SetImageBytes(idx, bytes);
                                urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                            }
                            thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{idx}?thumb=1");
                            i++;
                        }
                    }

                    if (result.IsSuccess)
                    {
                        totalCost += costEstimate;
                        createMs += result.CreateTotalMs;
                        downloadMs += result.DownloadTotalMs;
                        if (label == null)
                        {
                            label = copy.RuntimeMeta.TryGetValue("label", out var l) && !string.IsNullOrEmpty(l)
                                ? l
                                : result.ImageGeneratorDescription;
                        }
                    }
                    else if (firstError == null)
                    {
                        firstError = result.ErrorMessage ?? "unknown error";
                    }
                }
                catch (Exception ex)
                {
                    // GrokWebException carries the server's response body — that's
                    // where the actual reason lives ("post not found" etc).
                    var responseBody = ex switch
                    {
                        GrokWebException gwe => gwe.ResponseBody,
                        MetaWebException mwe => mwe.ResponseBody,
                        _ => "",
                    };
                    var detail = !string.IsNullOrEmpty(responseBody)
                        ? $"{ex.Message} {Truncate(responseBody, 300)}"
                        : ex.Message;
                    Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {detail}");
                    firstError ??= detail;
                }
                finally
                {
                    attemptResult?.ReleaseImageData();
                    grokWebClient?.Dispose();
                }
            }

            var elapsed = createMs + downloadMs;
            if (elapsed <= 0)
            {
                elapsed = wallClock.ElapsedMilliseconds;
            }
            // The actual produced pixel size travels as its own field so the
            // card reflects what the provider really returned, not just the
            // requested aspect. grok /images/edits in particular does not
            // reliably honor the requested AR, so showing "1024x1024" next to
            // a 2:3 request makes the mismatch visible. The frontend keeps
            // the catalog display name as the cell title and demotes this
            // provider spec label to a tooltip.
            var actualSize = firstImagePath != null
                ? ReadImageSize(firstImagePath)
                : firstImageBytes != null
                    ? ReadImageSize(firstImageBytes)
                    : null;
            // When N>1, show how many of the requested images actually came back.
            if (want > 1 && urls.Count > 0)
            {
                label = string.IsNullOrEmpty(label) ? $"{urls.Count}/{want} imgs" : $"{label} · {urls.Count}/{want} imgs";
            }

            var ok = urls.Count > 0;
            merged.IsSuccess = ok;
            merged.CreateTotalMs = createMs;
            merged.DownloadTotalMs = downloadMs;
            // User-required product behavior (2026-08-10): a generation that
            // failed AFTER streaming preview partials (typically gpt-image-2's
            // final getting moderated) keeps its last streamed partial visible
            // instead of vanishing what the user watched arrive. The partial is
            // persisted into the job folder and served under the same stable
            // image URL the live preview used (so replayed gen-partial events
            // stop 404ing after a restart too). The gen-result stays a failure:
            // ok=false, zero cost, error text intact — the kept preview rides a
            // separate index-aligned partialImages field and is explicitly
            // labeled by the frontend, never presented as a result.
            // Owner decision 2026-08-11: kept previews are B2-hosted too. A
            // failed upload here keeps the LOCAL url (documented exception to
            // the results policy): the artifact is the same bytes either way,
            // it stays local-served forever (no CdnKey means it is never
            // evicted), and failing an already-failed generation over its
            // preview would discard the only thing the user has left of it.
            // partialThumbs carries index-aligned local ?thumb=1 URLs so cards
            // never pull full hosted bytes.
            List<string?>? partialUrls = null;
            List<string?>? partialThumbs = null;
            if (!ok)
            {
                merged.ErrorMessage = firstError ?? "Generation completed without returning usable image or video media.";
                for (var partialIdx = 0; partialIdx < want; partialIdx++)
                {
                    if (job.TryPersistLastPartialImage(key, partialIdx))
                    {
                        var localUrl = $"/api/jobs/{job.Id}/images/{key}/{partialIdx}";
                        var keptUrl = localUrl;
                        if (_b2 != null
                            && job.TryGetImagePath(key, partialIdx, out var keptPath, out var keptType))
                        {
                            try
                            {
                                keptUrl = await UploadResultToB2Async(job, key, partialIdx, keptPath, keptType);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ui #{job.Id}] kept-preview upload failed for {key}/{partialIdx}: {ex.Message}; keeping local serving");
                            }
                        }
                        partialUrls ??= Enumerable.Repeat<string?>(null, want).ToList();
                        partialThumbs ??= Enumerable.Repeat<string?>(null, want).ToList();
                        partialUrls[partialIdx] = keptUrl;
                        partialThumbs[partialIdx] = localUrl + "?thumb=1";
                    }
                }
            }

            // In-process progression snapshots (persisted per-partial as the
            // stream ran) ride the gen-result on success AND failure so the
            // user can revisit how the image came together. Owner decision
            // 2026-08-11: snapshots are B2-hosted like results; a failed
            // snapshot upload keeps the local url (same documented exception
            // as kept previews — the artifact stays local-served and is never
            // evicted without a CdnKey, and a successful generation must not
            // fail over a preview nicety). `thumb` is the local ?thumb=1 card
            // preview, valid after eviction because eviction force-builds
            // disk thumbs first.
            var snapshots = job.GetPartialSnapshots(key);
            List<object>? progressImages = null;
            if (snapshots.Count > 0)
            {
                progressImages = new List<object>();
                foreach (var s in snapshots)
                {
                    var snapGen = UiJob.PartialSnapshotGenKey(key, s.PartialIndex);
                    var localUrl = $"/api/jobs/{job.Id}/images/{snapGen}/{s.ImageIndex}";
                    var url = localUrl;
                    if (_b2 != null
                        && job.TryGetImagePath(snapGen, s.ImageIndex, out var snapPath, out var snapType))
                    {
                        try
                        {
                            url = await UploadResultToB2Async(job, snapGen, s.ImageIndex, snapPath, snapType);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ui #{job.Id}] snapshot upload failed for {snapGen}/{s.ImageIndex}: {ex.Message}; keeping local serving");
                        }
                    }
                    progressImages.Add(new
                    {
                        partialIndex = s.PartialIndex,
                        imageIndex = s.ImageIndex,
                        url,
                        thumb = localUrl + "?thumb=1",
                    });
                }
            }

            // Known payment/auth failures carry a "next step" hint + the URL
            // where it gets fixed; the card renders it under the error text.
            var actionHint = ok ? null : ProviderActionHints.For(key, merged.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : merged.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                images = urls,
                // Failure only: last streamed preview partial(s) kept visible,
                // index-aligned with the requested output indexes; hosted URLs
                // when B2 hosting is on, plus index-aligned local ?thumb=1
                // card previews.
                partialImages = partialUrls,
                partialThumbs,
                // Success or failure: the durably persisted in-process
                // streamed previews, in arrival order.
                progressImages,
                // Present only when B2 hosting is on: local ?thumb=1 card
                // previews, index-aligned with `images` (whose entries are
                // then absolute B2 URLs that have no thumb variant).
                thumbs = _b2 != null ? thumbs : null,
                mediaType,
                label = label ?? key,
                size = actualSize,
                videoMode = key == KeyGrokWebVideo ? spec.VideoMode : null,
                videoDurationSeconds = key == KeyGrokWebVideo
                    ? spec.VideoDurationSeconds
                    : (int?)null,
                videoResolution = key == KeyGrokWebVideo ? spec.VideoResolution : null,
                videoAspectRatio = key == KeyGrokWebVideo ? spec.VideoAspectRatio : null,
                // Failed calls generally aren't billed, so report 0 for them.
                cost = ok ? totalCost : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({urls.Count}/{want})" : $"FAIL ({merged.ErrorMessage})")} from {key} in {elapsed} ms"
                + (actionHint != null ? $"\n[ui #{job.Id}]      next step: {actionHint.Text} -> {actionHint.Url}" : ""));
            return merged;
        }

        // Rough per-call cost ceilings for the describe targets, mirroring the
        // "estimate, not bill" convention of the image generators' GetCost().
        // Ideogram is a published flat price; the LLM vision calls are
        // token-billed and estimated at ~1K image-input tokens + the 1200-token
        // output cap at each provider's current list price.
        private static decimal DescribeCostEstimate(string key) => key switch
        {
            KeyDescribeIdeogram => IdeogramModelPricing.IdeogramDescribe,
            KeyDescribeOpenAi => 0.015m,
            KeyDescribeClaude => 0.025m,
            // Gemini runs with a mandatory thinking budget (2.5 Pro cannot
            // disable thinking), so its estimate includes those output-rate
            // thinking tokens.
            KeyDescribeGemini => 0.025m,
            KeyDescribeGrok => 0.025m,
            // Layout map is one Gemini vision call per input image (same
            // transport and token budget class as describe-gemini); the
            // server-side map rendering itself costs nothing.
            KeyLayoutMap => 0.025m,
            _ => throw new ArgumentException($"Unknown describe key '{key}'.", nameof(key)),
        };

        private ILocalVisionModel BuildDescriber(string key)
        {
            var availabilityProblem = DescribeAvailabilityProblem(key);
            if (availabilityProblem != null)
            {
                throw new InvalidOperationException(availabilityProblem);
            }

            switch (key)
            {
                case KeyDescribeIdeogram:
                    RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                    return new IdeogramDescriber(_settings.IdeogramApiKey);
                // OpenAI and Gemini get their provider-native JSON output modes
                // on top of the prompt contract; Claude and Grok have no such
                // knob on these transports and rely on the prompt alone.
                case KeyDescribeOpenAi:
                    RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", key);
                    return new OpenAIVisionDescriber(_settings.OpenAIApiKey) { RequestJsonOutput = true };
                case KeyDescribeClaude:
                    RequireKey(_settings.AnthropicApiKey, "AnthropicApiKey", key);
                    return new ClaudeVisionDescriber(_settings.AnthropicApiKey);
                case KeyDescribeGemini:
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    return new GeminiVisionDescriber(_settings.GoogleGeminiApiKey) { RequestJsonOutput = true };
                case KeyDescribeGrok:
                    RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", key);
                    return new GrokVisionDescriber(_settings.XAIGrokApiKey);
                default:
                    throw new ArgumentException($"unknown describe target '{key}'");
            }
        }

        /// Describe targets return TEXT, not media: one provider call per
        /// attached input image, all-or-nothing (fail closed — a blank or
        /// missing description for ANY input fails the whole target rather
        /// than presenting partial output as success). The descriptions ride
        /// the persisted gen-result event (resultKind "text" + texts[]), which
        /// is how they survive restarts and reach archive views; they are
        /// deliberately absent from the image contact sheet.
        private async Task<TaskProcessResult> RunDescribeOneAsync(UiJob job, string key, PromptDetails pd)
        {
            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = new TaskProcessResult
            {
                PromptDetails = pd,
                ImageGeneratorDescription = key,
                ContentType = "text/plain",
            };

            var texts = new List<object>();
            string? error = null;
            string label = key;
            var perCallCost = 0m;
            // The exact full prompt sent over the wire (user instruction + the
            // JSON reply contract), carried on the event so the card's
            // sent/returned exchange viewer shows precisely what went out.
            // Ideogram sends nothing (fixed built-in instruction), flagged as "".
            var isIdeogram = key == KeyDescribeIdeogram;
            var sentPrompt = "";
            try
            {
                if (!job.HasInputImage)
                {
                    throw new InvalidOperationException($"'{key}' requires an input image; this job has none.");
                }
                var describer = BuildDescriber(key);
                perCallCost = DescribeCostEstimate(key);
                label = isIdeogram
                    ? $"{describer.GetModelName()} (fixed instruction; the prompt is not sent)"
                    : describer.GetModelName();
                var instruction = pd.Prompt;
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    // POST /api/jobs substitutes the default instruction for
                    // blank describe-only prompts, so this only guards direct
                    // programmatic misuse.
                    throw new InvalidOperationException("describe instruction is empty");
                }
                sentPrompt = isIdeogram ? "" : instruction + "\n\n" + DescribeJsonReplyContract;
                Logger.Log($"[ui #{job.Id}]   -> {key} ({describer.GetModelName()}, ~${perCallCost:0.###} x {job.InputImageCount} input(s))");

                for (var i = 0; i < job.InputImageCount; i++)
                {
                    var imageBytes = await File.ReadAllBytesAsync(job.InputImagePaths[i]);
                    var raw = await describer.DescribeImageAsync(
                        imageBytes,
                        isIdeogram ? instruction : sentPrompt,
                        maxTokens: DescribeMaxTokens,
                        temperature: DescribeTemperature);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        // Blank text from any endpoint is a hard failure here,
                        // never an empty "success".
                        throw new InvalidDataException(
                            $"{key} returned no description for input image {i + 1} of {job.InputImageCount}.");
                    }
                    raw = raw.Trim();
                    // Ideogram's captions are the description by definition; the
                    // instruction-capable models answer under the JSON contract
                    // and a non-conforming reply fails this target.
                    var (description, comments) = isIdeogram ? (raw, "") : ParseDescribeJsonReply(raw);
                    texts.Add(new { inputIndex = i, text = description, comments, raw });
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {ex.Message}");
                error = ex.Message;
                texts.Clear();
            }

            var ok = error == null && texts.Count == job.InputImageCount && texts.Count > 0;
            result.IsSuccess = ok;
            if (!ok)
            {
                result.ErrorMessage = error ?? "Describe completed without returning a description.";
            }

            var elapsed = wallClock.ElapsedMilliseconds;
            var actionHint = ok ? null : ProviderActionHints.For(key, result.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : result.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                // No media: the frontend renders resultKind "text" cells from
                // texts[] and the viewer pairs each text with its archived
                // input image (/api/jobs/{id}/images/input/{inputIndex}).
                images = Array.Empty<string>(),
                mediaType = "",
                label,
                size = (string?)null,
                resultKind = "text",
                texts,
                // The exact wire prompt ("" = Ideogram's fixed instruction);
                // each texts[] entry carries the raw pre-parse reply, so the
                // card's exchange toggle can show precisely what was sent to
                // and returned from each endpoint.
                sentPrompt,
                cost = ok ? perCallCost * texts.Count : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({texts.Count} description(s))" : $"FAIL ({result.ErrorMessage})")} from {key} in {elapsed} ms");
            return result;
        }

        /// Layout map (image → labeled section-map IMAGE): one Gemini vision
        /// call per attached input under the strict LayoutMapJsonContract,
        /// then a server-rendered flat-color map with a numbered legend and
        /// the model's one-sentence summary baked into the PNG. All-or-nothing
        /// like describe (any input failing fails the whole target), but the
        /// result is a normal image gen-result so cards, the viewer, video
        /// follow-ups, and favorites treat it like any generated image. The
        /// composer prompt is optional CONTEXT for the section labels, never
        /// the contract itself. Deliberately absent from the contact sheet.
        private async Task<TaskProcessResult> RunLayoutMapOneAsync(UiJob job, string key, PromptDetails pd)
        {
            var wallClock = Stopwatch.StartNew();
            job.Emit(new
            {
                type = "gen-start",
                gen = key,
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            var result = new TaskProcessResult
            {
                PromptDetails = pd,
                ImageGeneratorDescription = key,
                ContentType = "image/png",
            };

            // Rendered map files, collected first and only published to the
            // job store + event after EVERY input succeeded (all-or-nothing;
            // the durable files themselves stay on disk either way).
            var rendered = new List<(string Path, string Size)>();
            string? error = null;
            string label = key;
            var perCallCost = 0m;
            var sentPrompt = "";
            try
            {
                if (!job.HasInputImage)
                {
                    throw new InvalidOperationException($"'{key}' requires an input image; this job has none.");
                }
                var availabilityProblem = DescribeAvailabilityProblem(key);
                if (availabilityProblem != null)
                {
                    throw new InvalidOperationException(availabilityProblem);
                }
                RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                var describer = new GeminiVisionDescriber(_settings.GoogleGeminiApiKey)
                {
                    RequestJsonOutput = true,
                };
                perCallCost = DescribeCostEstimate(key);
                label = $"{describer.GetModelName()} layout map";
                var instruction = pd.Prompt;
                sentPrompt = string.IsNullOrWhiteSpace(instruction)
                    ? LayoutMapJsonContract
                    : LayoutMapJsonContract
                        + "\n\nUser-provided context about the image (may inform your section labels): "
                        + instruction.Trim();
                Logger.Log($"[ui #{job.Id}]   -> {key} ({describer.GetModelName()}, ~${perCallCost:0.###} x {job.InputImageCount} input(s))");

                var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
                var folder = Path.Combine(_settings.ImageDownloadBaseFolder, today, "LayoutMaps");
                Directory.CreateDirectory(folder);

                for (var i = 0; i < job.InputImageCount; i++)
                {
                    var imageBytes = await File.ReadAllBytesAsync(job.InputImagePaths[i]);
                    ImageInfo info;
                    using (var infoStream = new MemoryStream(imageBytes, writable: false))
                    {
                        info = Image.Identify(infoStream);
                    }
                    if (info == null || info.Width <= 0 || info.Height <= 0)
                    {
                        throw new InvalidDataException(
                            $"input image {i + 1} of {job.InputImageCount} could not be decoded for its dimensions.");
                    }
                    var raw = await describer.DescribeImageAsync(
                        imageBytes,
                        sentPrompt,
                        maxTokens: DescribeMaxTokens,
                        temperature: DescribeTemperature);
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        throw new InvalidDataException(
                            $"{key} returned no reply for input image {i + 1} of {job.InputImageCount}.");
                    }
                    var (summary, regions) = ParseLayoutMapJsonReply(raw.Trim());
                    Logger.Log($"[ui #{job.Id}]   {key} input {i + 1}: {regions.Count} region(s) — "
                        + string.Join("; ", regions.Select(r => r.Label)));

                    var mapPath = Path.Combine(
                        folder,
                        $"{DateTime.Now:HHmmss_fff}_{job.Id}_map{i}.png");
                    using (var map = UiLayoutMapRenderer.Render(info.Width, info.Height, regions, summary))
                    {
                        await map.SaveAsPngAsync(mapPath);
                        rendered.Add((mapPath, $"{map.Width}x{map.Height}"));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {ex.Message}");
                error = ex.Message;
                rendered.Clear();
            }

            var ok = error == null && rendered.Count == job.InputImageCount && rendered.Count > 0;
            var urls = new List<string>();
            var thumbs = new List<string>();
            if (ok)
            {
                try
                {
                    for (var i = 0; i < rendered.Count; i++)
                    {
                        job.StoreImagePath(key, i, rendered[i].Path, "image/png");
                        if (_b2 != null)
                        {
                            // Retry x3 inside; throws on final failure, failing
                            // this target visibly. Never a local URL substitute.
                            urls.Add(await UploadResultToB2Async(job, key, i, rendered[i].Path, "image/png"));
                        }
                        else
                        {
                            urls.Add($"/api/jobs/{job.Id}/images/{key}/{i}");
                        }
                        thumbs.Add($"/api/jobs/{job.Id}/images/{key}/{i}?thumb=1");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ui #{job.Id}]   <- EXCEPTION publishing {key} maps: {ex.Message}");
                    error = ex.Message;
                    ok = false;
                    urls.Clear();
                    thumbs.Clear();
                }
            }

            result.IsSuccess = ok;
            if (!ok)
            {
                result.ErrorMessage = error ?? "Layout map completed without producing a map image.";
            }

            var elapsed = wallClock.ElapsedMilliseconds;
            var actionHint = ok ? null : ProviderActionHints.For(key, result.ErrorMessage);
            job.Emit(new
            {
                type = "gen-result",
                gen = key,
                ok,
                error = ok ? "" : result.ErrorMessage,
                errorHint = actionHint?.Text,
                errorHintUrl = actionHint?.Url,
                ms = elapsed,
                images = urls,
                thumbs = _b2 != null && ok ? thumbs : null,
                mediaType = ok ? "image/png" : "",
                label,
                size = ok ? rendered[0].Size : null,
                // The exact wire prompt (contract + optional user context),
                // recorded on the persisted event like describe's.
                sentPrompt,
                cost = ok ? perCallCost * urls.Count : 0m,
            });
            Logger.Log($"[ui #{job.Id}]   <- {(ok ? $"OK ({urls.Count} map(s))" : $"FAIL ({result.ErrorMessage})")} from {key} in {elapsed} ms");
            return result;
        }

        private IImageGenerator BuildGrokWeb(GrokWebClient client, UiJobSpec spec)
        {
            var mapped = UiShapeMapping.GrokAspect(spec.Shape);
            // Unlike the official API, the consumer transport has no working
            // prompt-aware auto mode: live tests (2026-07-20) showed both
            // literal "auto" and an omitted aspect_ratio always return Grok's
            // native 2:3 default regardless of the prompt. 2:3 is a poor
            // universal shape, so an unspecified shape requests square 1:1
            // instead (a declared input default chosen before the call, not a
            // fallback). Explicit shapes map as usual.
            var ar = mapped == "" ? "1:1" : mapped;
            return new GrokWebImagineGenerator(
                client, maxConcurrency: 1, _stats,
                pro: _options.GrokWebPro,
                aspectRatio: ar,
                enableSideBySide: _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private async Task<IImageGenerator> BuildGrokWebEditAsync(
            GrokWebClient client,
            UiJobSpec spec,
            UiJob job)
        {
            if (_grokWebStatsigSigner == null)
            {
                throw new InvalidOperationException(
                    _grokWebStatsigProblem
                    ?? "Grok web browser-free image editing is not configured.");
            }

            // Empty means inherit/derive from the submitted source image.
            var aspectRatio = UiShapeMapping.GrokAspect(spec.Shape);
            return await GrokWebImagineEditGenerator.CreateAsync(
                client,
                job.InputImagePath,
                maxConcurrency: 1,
                _stats,
                pro: _options.GrokWebPro,
                aspectRatio: aspectRatio,
                enableSideBySide: _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private async Task<IImageGenerator> BuildGrokWebChatEditAsync(
            GrokWebClient client,
            UiJobSpec spec,
            UiJob job)
        {
            if (_grokWebStatsigSigner == null)
            {
                throw new InvalidOperationException(
                    _grokWebStatsigProblem
                    ?? "Grok web chat-door image path is not configured.");
            }
            if (!job.HasInputImage || string.IsNullOrWhiteSpace(job.InputImagePath))
            {
                throw new InvalidOperationException(
                    "grok-web-chat requires an attached image; it edits the image through a chat message.");
            }

            // Empty means inherit/derive from the submitted source image.
            var aspectRatio = UiShapeMapping.GrokAspect(spec.Shape);
            return await GrokWebImagineChatEditGenerator.CreateAsync(
                client,
                job.InputImagePath,
                maxConcurrency: 1,
                _stats,
                chatModel: GrokWebClient.DefaultChatModel,
                aspectRatio: aspectRatio);
        }

        private async Task<IImageGenerator> BuildGrokWebVideoAsync(
            GrokWebClient client,
            UiJob job,
            UiJobSpec spec)
        {
            if (_grokWebStatsigSigner == null)
            {
                throw new InvalidOperationException(
                    _grokWebStatsigProblem
                    ?? "Grok web browser-free video generation is not configured.");
            }
            if (!job.HasInputImage)
            {
                throw new InvalidOperationException(
                    "grok-web image-to-video requires a source image");
            }

            var aspectRatio = spec.VideoAspectRatio == "source"
                ? UiShapeMapping.GrokAspectForInput(
                    job.InputImageWidth,
                    job.InputImageHeight)
                : spec.VideoAspectRatio;
            return await GrokWebImagineVideoGenerator.CreateFromImageAsync(
                client,
                _settings,
                _stats,
                job.InputImagePath,
                maxConcurrency: 1,
                aspectRatio: aspectRatio,
                resolution: spec.VideoResolution,
                durationSeconds: spec.VideoDurationSeconds,
                enableSideBySide: false,
                videoMode: spec.VideoMode);
        }

        private static ImageGeneratorApiType BflApiTypeForKey(string key) => key switch
        {
            KeyBfl => ImageGeneratorApiType.BFLFlux2ProPreview,
            KeyBflFlux2Pro => ImageGeneratorApiType.BFLFlux2Pro,
            KeyBflFlux2Max => ImageGeneratorApiType.BFLFlux2Max,
            KeyBflFlux2Flex => ImageGeneratorApiType.BFLFlux2Flex,
            KeyBflFlux2Klein4b => ImageGeneratorApiType.BFLFlux2Klein4b,
            KeyBflFlux2Klein9bPreview => ImageGeneratorApiType.BFLFlux2Klein9bPreview,
            KeyBflFlux2Klein9b => ImageGeneratorApiType.BFLFlux2Klein9b,
            KeyBflKontextPro => ImageGeneratorApiType.BFLFluxKontextPro,
            KeyBflKontextMax => ImageGeneratorApiType.BFLFluxKontextMax,
            KeyBflFlux11Ultra => ImageGeneratorApiType.BFLv11Ultra,
            KeyBflFlux11 => ImageGeneratorApiType.BFLv11,
            KeyBflFluxPro => ImageGeneratorApiType.BFLFluxPro,
            KeyBflFluxDev => ImageGeneratorApiType.BFLFluxDev,
            _ => throw new ArgumentException($"Unknown BFL generator key '{key}'.", nameof(key)),
        };

        // enablePartials: emit mid-stream preview images for gpt-image-2. Only
        // valid for a single-image request; RunOneAsync turns it off for N>1
        // (each generator is always built single-image and invoked N times).
        private IImageGenerator BuildGenerator(string key, UiJobSpec spec, UiJob job, bool enablePartials = true)
        {
            var availabilityProblem = DescribeAvailabilityProblem(key);
            if (availabilityProblem != null)
            {
                throw new InvalidOperationException(availabilityProblem);
            }

            switch (key)
            {
                case KeyGpt2:
                    {
                        RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", key);
                        if (!Enum.TryParse<OpenAIGPTImageOneQuality>(spec.Quality, true, out var quality))
                        {
                            quality = OpenAIGPTImageOneQuality.high;
                        }
                        var size = UiShapeMapping.Gpt2Size(
                            spec.Shape,
                            spec.Detail,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (job.HasInputImage)
                        {
                            return new GptImage2EditGenerator(
                                _settings.OpenAIApiKey, maxConcurrency: 2,
                                job.InputImagePaths,
                                size, quality, _stats, "ui",
                                imageCount: 1);
                        }
                        return new GptImage2Generator(
                            _settings.OpenAIApiKey, maxConcurrency: 2,
                            sizePool: new[] { size },
                            moderation: spec.Moderation,
                            qualityPool: new[] { quality },
                            stats: _stats, name: "ui",
                            // No day-folder PartialsLive copies in UI mode
                            // (owner decision 2026-08-11): the UI persists its
                            // own durable progression snapshots per partial;
                            // the CLI-era live-viewing copies were unreferenced
                            // disk growth on the shared host.
                            partialSaveFolder: null,
                            popUpPartials: false,
                            imageCount: 1,
                            partialImageCallback: !enablePartials ? null : (partialIndex, imageIndex, bytes) =>
                            {
                                var outputIndex = Math.Max(0, imageIndex);
                                // Preview and final bytes deliberately share one stable
                                // URL. Each partial replaces the previous bytes, then
                                // RunOneAsync replaces them with the completed image.
                                job.StoreImage(key, outputIndex, bytes, "image/png");
                                // Independently persist this exact partial as a durable
                                // progression snapshot so the in-process previews stay
                                // viewable after the final lands (user-required,
                                // 2026-08-10). Additive: a failed write only means this
                                // step is absent from the progression strip.
                                if (!job.TryPersistPartialSnapshot(key, partialIndex, outputIndex, bytes, "image/png"))
                                {
                                    Logger.Log($"[ui #{job.Id}] could not persist progression snapshot {key}~p{partialIndex}/{outputIndex}");
                                }
                                job.Emit(new
                                {
                                    type = "gen-partial",
                                    gen = key,
                                    partialIndex,
                                    imageIndex = outputIndex,
                                    url = $"/api/jobs/{job.Id}/images/{key}/{outputIndex}",
                                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                });
                            });
                    }

                case KeyGpt1:
                case KeyGpt1Mini:
                    {
                        // Text-only target: on image jobs it runs from the prompt
                        // alone (see ImageCapableKeys).
                        if (!Enum.TryParse<OpenAIGPTImageOneQuality>(spec.Quality, true, out var quality))
                        {
                            quality = OpenAIGPTImageOneQuality.high;
                        }
                        return new GptImageOneGenerator(
                            _settings.OpenAIApiKey,
                            maxConcurrency: 2,
                            size: UiShapeMapping.Gpt1Size(spec.Shape),
                            moderation: spec.Moderation,
                            quality,
                            apiType: key == KeyGpt1 ? ImageGeneratorApiType.GptImage1 : ImageGeneratorApiType.GptImage1Mini,
                            _stats,
                            name: $"{key} ui",
                            imageCount: 1);
                    }

                case KeyGrokApi:
                case KeyGrokApiPro:
                    {
                        RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", key);
                        var pro = key == KeyGrokApiPro;
                        var mappedAr = UiShapeMapping.GrokAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (job.HasInputImage)
                        {
                            return new GrokImagineEditGenerator(
                                _settings.XAIGrokApiKey, maxConcurrency: 1, _stats, _settings,
                                inputImage: job.InputImagePath, pro: pro, aspectRatio: mappedAr);
                        }
                        return new GrokImagineGenerator(
                            _settings.XAIGrokApiKey, 1,
                            pro ? ImageGeneratorApiType.GrokImaginePro : ImageGeneratorApiType.GrokImagine,
                            _stats, "ui",
                            aspectRatio: mappedAr == "" ? "auto" : mappedAr,
                            quality: "high",
                            resolution: UiShapeMapping.GrokResolution(spec.Detail),
                            settings: _settings,
                            imageCount: 1);
                    }

                case KeyGoogle:
                case KeyGooglePro:
                    {
                        // Gemini honors aspectRatio + imageSize; a pasted image rides
                        // along as a reference part (Gemini is natively multimodal).
                        // No n equivalent — Gemini returns what it returns.
                        RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                        var googleApiType = key == KeyGooglePro
                            ? ImageGeneratorApiType.GoogleNanoBananaPro
                            : ImageGeneratorApiType.GoogleNanoBanana;
                        var googleAspect = UiShapeMapping.GoogleAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        return new GoogleGenerator(
                            googleApiType, _settings.GoogleGeminiApiKey, maxConcurrency: 2,
                            _stats, name: $"{key} ui",
                            aspectRatio: googleAspect == "" ? null : googleAspect,
                            imageSize: UiShapeMapping.GoogleImageSize(spec.Detail),
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyBfl:
                case KeyBflFlux2Pro:
                case KeyBflFlux2Max:
                case KeyBflFlux2Flex:
                case KeyBflFlux2Klein4b:
                case KeyBflFlux2Klein9bPreview:
                case KeyBflFlux2Klein9b:
                case KeyBflKontextPro:
                case KeyBflKontextMax:
                case KeyBflFlux11Ultra:
                case KeyBflFlux11:
                case KeyBflFluxPro:
                case KeyBflFluxDev:
                    {
                        RequireKey(_settings.BFLApiKey, "BFLApiKey", key);
                        var apiType = BflApiTypeForKey(key);
                        var legacyDimensions = apiType is ImageGeneratorApiType.BFLv11
                            or ImageGeneratorApiType.BFLFluxPro
                            or ImageGeneratorApiType.BFLFluxDev;
                        var (bflWidth, bflHeight) = legacyDimensions
                            ? UiShapeMapping.BflLegacySize(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight)
                            : UiShapeMapping.BflSize(
                                spec.Shape,
                                spec.Detail,
                                job.InputImageWidth,
                                job.InputImageHeight);
                        var bflAspect = UiShapeMapping.GrokAspect(
                            spec.Shape,
                            job.InputImageWidth,
                            job.InputImageHeight);
                        if (string.IsNullOrEmpty(bflAspect))
                        {
                            bflAspect = "1:1";
                        }
                        return new BFLGenerator(
                            apiType, _settings.BFLApiKey,
                            maxConcurrency: 2, bflAspect, false, bflWidth, bflHeight, _stats, $"{key} ui",
                            inputImagePath: job.HasInputImage && IsImageCapable(key) ? job.InputImagePath : null);
                    }

                case KeyKrea:
                case KeyKreaTurbo:
                case KeyKreaLarge:
                    {
                        RequireKey(_settings.KreaApiKey, "KreaApiKey", key);
                        var variant = key switch
                        {
                            KeyKreaTurbo => Krea2Variant.MediumTurbo,
                            KeyKreaLarge => Krea2Variant.Large,
                            _ => Krea2Variant.Medium,
                        };
                        return new Krea2Generator(
                            _settings.KreaApiKey,
                            maxConcurrency: 2,
                            variant,
                            UiShapeMapping.KreaAspect(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            _stats,
                            name: $"{key} ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyIdeogram:
                    {
                        // Ideogram 4.0 uses /generate without an input and the
                        // dedicated /remix endpoint when one is attached. Detail
                        // has no effect; auto Remix maps to the nearest published
                        // 2K resolution so the source is not cropped to square.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramV4Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            UiShapeMapping.IdeogramV4Resolution(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            IdeogramRenderingSpeed.DEFAULT,
                            _stats, "ideogram ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                    }

                case KeyIdeogramV3:
                    {
                        // V3 is the image-capable Ideogram target: a pasted image
                        // rides along as a style reference; without one it's plain
                        // text-to-image on the same endpoint.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramV3Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON,
                            UiShapeMapping.IdeogramV3Aspect(
                                spec.Shape,
                                job.InputImageWidth,
                                job.InputImageHeight),
                            IdeogramRenderingSpeed.QUALITY,
                            "", _stats, "ideogram-v3 ui",
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                            imageCount: 1);
                    }

                case KeyIdeogramV2:
                    {
                        // V2 remains available through Ideogram's legacy JSON
                        // /generate endpoint. It is text-only in this UI and returns
                        // one image; detail and n therefore have no effect.
                        RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                        return new IdeogramGenerator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            IdeogramMagicPromptOption.ON,
                            UiShapeMapping.IdeogramV3Aspect(spec.Shape),
                            styleType: null,
                            negativePrompt: "",
                            model: IdeogramModel.V_2,
                            stats: _stats,
                            name: $"{key} ui");
                    }

                case KeyRecraft:
                case KeyRecraftV41Utility:
                case KeyRecraftV41Pro:
                case KeyRecraftV41Vector:
                case KeyRecraftV3:
                case KeyRecraftV4:
                case KeyRecraftV4Pro:
                    {
                        RequireKey(_settings.RecraftApiKey, "RecraftApiKey", key);
                        if (job.HasInputImage && !spec.Shape.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException(
                                "Recraft image-to-image always follows the source dimensions and cannot override output aspect ratio.");
                        }
                        // Recraft text-to-image accepts the same "w:h" aspect strings
                        // as Grok. Its image-to-image endpoint takes no size field and
                        // inherently follows the source dimensions.
                        var recraftAspect = UiShapeMapping.GrokAspect(spec.Shape);
                        // With an input image, every exposed Recraft V3/V4 model
                        // runs through image-to-image; output size follows the source.
                        return new RecraftGenerator(
                            _settings.RecraftApiKey, maxConcurrency: 1,
                            RecraftImageSize._1024x1024, RecraftStyle.any,
                            null, null, null, _stats, $"{key} ui",
                            model: RecraftModelForKey(key),
                            inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                            sizeOverride: recraftAspect,
                            imageCount: 1);
                    }

                case KeyLocalKlein:
                case KeyLocalZImage:
                    {
                        if (job.HasInputImage)
                        {
                            throw new NotSupportedException($"{key} is text-to-image only in the local UI; remove the input image");
                        }
                        return _generatorGroups.BuildByShortName(key);
                    }

                default:
                    throw new ArgumentException($"unknown generator '{key}'");
            }
        }

        // Minimal PNG/JPEG dimension reader for the display label. Returns "WxH",
        // or null for anything it doesn't recognize — no decode, no exceptions used
        // for control flow. PNG and JPEG cover every provider the UI shows.
        private static string ReadImageSize(string path)
        {
            try
            {
                if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    using var vector = new MagickImage(path);
                    return vector.Width > 0 && vector.Height > 0
                        ? $"{vector.Width}x{vector.Height}"
                        : null;
                }
                var info = Image.Identify(path);
                return info.Width > 0 && info.Height > 0
                    ? $"{info.Width}x{info.Height}"
                    : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not identify saved result dimensions for {path}: {ex.Message}");
                return null;
            }
        }

        private static string ReadImageSize(byte[] b)
        {
            if (b == null) return null;

            // PNG: 8-byte signature, then IHDR with width @16, height @20 (BE).
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return (w > 0 && h > 0) ? $"{w}x{h}" : null;
            }

            // JPEG: FF D8, then walk segments to the first SOF (C0..CF except
            // C4/C8/CC); its payload is precision(1), height(2), width(2).
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int p = 2;
                while (p + 9 < b.Length)
                {
                    if (b[p] != 0xFF) { p++; continue; }
                    int marker = b[p + 1];
                    // Standalone markers (padding, RSTn, SOI/EOI) carry no length.
                    if (marker == 0xFF || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                    {
                        p += 2;
                        continue;
                    }
                    int segLen = (b[p + 2] << 8) | b[p + 3];
                    if (segLen < 2) return null;
                    bool isSof = marker >= 0xC0 && marker <= 0xCF
                        && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (isSof)
                    {
                        int h = (b[p + 5] << 8) | b[p + 6];
                        int w = (b[p + 7] << 8) | b[p + 8];
                        return (w > 0 && h > 0) ? $"{w}x{h}" : null;
                    }
                    p += 2 + segLen;
                }
                return null;
            }

            return null;
        }

        public ValueTask DisposeAsync()
        {
            _finalizationLimit.Dispose();
            return ValueTask.CompletedTask;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        private static void RequireKey(string value, string settingName, string genName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Settings.{settingName} is not set; cannot run '{genName}'.");
            }
        }
    }
}
