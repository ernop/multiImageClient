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
        // Set when this job is one part of a prompt split.
        public UiJobSplit? Split { get; init; }

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

        internal void RewriteCreatorLogin(string from, string to)
        {
            lock (_lock)
            {
                if (string.Equals(CreatorLogin, to, StringComparison.Ordinal))
                    return;
                if (!string.Equals(CreatorLogin, from, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Job {Id} creator login is '{CreatorLogin}', not '{from}'.");
                }
                if (_storage == null)
                {
                    throw new InvalidDataException($"Job {Id} has no durable history storage.");
                }
                CreatorLogin = to;
                try
                {
                    _storage.SaveMetadataStrict(this);
                }
                catch
                {
                    CreatorLogin = from;
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

        public IReadOnlyList<UiPersistedImageInfo> ListImageDeletionArtifacts(
            string generator,
            int imageIndex)
        {
            if (string.IsNullOrWhiteSpace(generator) || imageIndex < 0)
            {
                throw new ArgumentException(
                    "An image deletion requires an exact generator and non-negative index.");
            }
            return ListPersistedImages()
                .Where(info =>
                {
                    var slash = info.Key.LastIndexOf('/');
                    if (slash <= 0
                        || !int.TryParse(info.Key.AsSpan(slash + 1), out var index))
                    {
                        return false;
                    }
                    var artifactGenerator = info.Key.Substring(0, slash);
                    return artifactGenerator == "grid"
                        || (index == imageIndex
                            && string.Equals(
                                PartialSnapshotVisibilityGen(artifactGenerator),
                                generator,
                                StringComparison.Ordinal));
                })
                .ToList();
        }

        public void DeleteLocalArtifacts(IReadOnlyCollection<string> keys)
        {
            if (_storage == null)
            {
                throw new InvalidOperationException(
                    $"UI job {Id} has no durable storage for artifact deletion.");
            }
            _storage.DeleteLocalArtifacts(keys);
            foreach (var key in keys)
            {
                _images.TryRemove(key, out _);
                UiCardPreviewCache.Remove($"{Id}/{key}");
            }
        }

        /// Atomically replaces this job's persisted event log (B2 backfill URL
        /// migration). A one-time pre-rewrite backup is retained on disk.
        public bool TryReplacePersistedEvents(IReadOnlyList<string> lines)
        {
            return _storage?.TryReplaceEvents(lines) == true;
        }

        public bool TryReplacePersistedEventsForDeletion(IReadOnlyList<string> lines)
        {
            return _storage?.TryReplaceEventsForDeletion(lines) == true;
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
                var info = Raster.Identify(mem.Bytes);
                if (Math.Max(info.Width, info.Height) <= CardPreviewMaxEdge)
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

                var (width, height) = CardPreviewSize(info.Width, info.Height);
                using var decoded = Raster.DecodeForBox(mem.Bytes, width, height);
                using var preview = Raster.Resize(decoded, width, height);
                var (previewBytes, previewType) = EncodeCardPreview(preview);
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
                using var image = LoadCardPreview(originalPath, originalContentType);
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

        // Returns the card preview bitmap: long edge at most CardPreviewMaxEdge.
        // Raster originals decode at a reduced size when the codec allows, so
        // a multi-megapixel original never sits in memory at full size.
        private static SKBitmap LoadCardPreview(string path, string contentType)
        {
            if (contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                using var vector = SvgRaster.Rasterize(path);
                var (svgWidth, svgHeight) = CardPreviewSize(vector.Width, vector.Height);
                return Raster.Resize(vector, svgWidth, svgHeight);
            }

            var info = Raster.Identify(path);
            var (width, height) = CardPreviewSize(info.Width, info.Height);
            using var decoded = Raster.DecodeForBox(path, width, height);
            return Raster.Resize(decoded, width, height);
        }

        private static (int Width, int Height) CardPreviewSize(int width, int height)
        {
            var longEdge = Math.Max(width, height);
            if (longEdge <= CardPreviewMaxEdge)
            {
                return (width, height);
            }
            var scale = (double)CardPreviewMaxEdge / longEdge;
            return (
                Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static (byte[] Bytes, string ContentType) EncodeCardPreview(SKBitmap image)
        {
            if (Raster.HasTranslucentPixels(image))
            {
                return (Raster.EncodePng(image), "image/png");
            }
            return (Raster.EncodeJpeg(image, 80), "image/jpeg");
        }

        internal void AttachStorage(UiJobStorage storage) => _storage = storage;

        internal void RestoreDone()
        {
            lock (_lock) _done = true;
        }
    }

    /// Per-job options parsed from the submit form. gpt-image-2 honors all of
    /// them; the grok generators only use what applies to them.
    public class UiJobSpec
    {
        public required List<string> GeneratorKeys { get; init; }
        public string Quality { get; init; } = "high";
        public string Moderation { get; init; } = "low";
        public int ImageCount { get; init; } = 1;
        // Goal-loop samples each require one exact output; other jobs retain the CLI setting.
        public bool? GrokWebSideBySide { get; init; }

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

        /// Condensed prompts the user made for targets over their prompt
        /// limit, verified at intake. A listed target receives its condensed
        /// prompt in place of the job prompt; every other target receives
        /// the job prompt.
        public IReadOnlyDictionary<string, UiCondensedPrompt> CondensedPrompts { get; init; }
            = new Dictionary<string, UiCondensedPrompt>(StringComparer.Ordinal);
        public string VideoMode { get; init; } = "normal";
        public int VideoDurationSeconds { get; init; } = 15;
        public string VideoResolution { get; init; } = "480p";
        public string VideoAspectRatio { get; init; } = "source";
    }

    // ExchangeId names the stored condense exchange that produced Prompt.
    public sealed record UiCondensedPrompt(string ExchangeId, string Model, string Prompt);

    // Budget is the longest prompt that fits every target in Keys after its
    // endpoint extra text.
    public sealed record UiPromptCondensePlan(IReadOnlyList<string> Keys, int Budget, int SourceCharacters);

    public sealed record UiSketchComposerPaletteColor(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("hex")] string Hex);

    /// Exact identity and editable fields for a composition sketch attachment.
    /// The PNG itself remains an ordinary ordered job input; InputIndex proves
    /// which one it is when "set active" restores a completed job.
    /// Version 1: eight meanings aligned to the built-in red/blue/green palette.
    /// Version 2: explicit palette (1–8 named hex colors) plus one meaning per color.
    public sealed record UiSketchComposerState(
        int Version,
        int InputIndex,
        string Aspect,
        IReadOnlyList<string> Meanings,
        IReadOnlyList<UiSketchComposerPaletteColor>? Palette = null);

}
