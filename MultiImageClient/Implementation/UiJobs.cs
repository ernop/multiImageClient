#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using IdeogramAPIClient;
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
    /// history), plus a live byte cache and durable references to saved result
    /// files so completed cards survive a server restart.
    ///
    /// Multiple input images (up to UiJobRunner.MaxInputImages) are ordered;
    /// index 0 is the primary used for AR matching, compare-with-input, and
    /// the job-card thumb. gpt-image-2 /edits receives the full list; every
    /// other generator receives only the primary.
    public class UiJob
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
        public required string Prompt { get; init; }
        public IReadOnlyList<string> InputImagePaths { get; init; } = Array.Empty<string>();
        public int InputImageWidth { get; init; }
        public int InputImageHeight { get; init; }
        public IReadOnlyList<string> GeneratorKeys { get; init; } = Array.Empty<string>();
        public DateTime CreatedAt { get; init; } = DateTime.Now;
        public string SourceJobId { get; init; } = "";
        public string SourceGenerator { get; init; } = "";
        public int? SourceIndex { get; init; }

        private readonly object _lock = new();
        private readonly List<string> _events = new();
        private bool _done;
        private UiJobStorage? _storage;
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
                _events.Add(json);
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
                foreach (var json in _events)
                {
                    callback(this, json);
                }
                _emitCallback = callback;
            }
        }

        public void MarkDone()
        {
            lock (_lock) _done = true;
            _storage?.SaveMetadata(this);
        }

        /// Snapshot events from `fromIndex` onward plus the done flag, for the
        /// SSE poll loop.
        public (List<string> Events, bool Done) ReadFrom(int fromIndex)
        {
            lock (_lock)
            {
                var batch = fromIndex < _events.Count
                    ? _events.GetRange(fromIndex, _events.Count - fromIndex)
                    : new List<string>();
                return (batch, _done);
            }
        }

        public void StoreImage(
            string genKey,
            int n,
            byte[] bytes,
            string contentType,
            string durablePath = "")
        {
            var key = $"{genKey}/{n}";
            _images[key] = (bytes, contentType);
            if (!string.IsNullOrWhiteSpace(durablePath))
            {
                _storage?.SaveImageReference(key, durablePath, contentType);
            }
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

        // Card previews exist because a page hydrating 200+ jobs otherwise pulls
        // every full-resolution original (2-8 MB each, gigabytes total) through
        // the browser's ~6-socket HTTP/1.1 pool, which starves image loads AND
        // every other same-origin tab's API calls. Cards get a <=640px preview;
        // the viewer and open-in-new-tab keep the exact original bytes.
        private const int CardPreviewMaxEdge = 640;
        private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _cardPreviews = new();

        /// Downscaled preview of a stored image for job-card display. Returns
        /// the ORIGINAL bytes verbatim when the image is already small enough or
        /// cannot be decoded for resizing — that is not a data fallback: it is
        /// the exact same resource, merely un-shrunk, and can never select
        /// unrelated data. Returns false only when the image itself is missing
        /// or is not an image (videos have no card preview).
        public bool TryGetCardPreview(string genKey, int n, out byte[] bytes, out string contentType)
        {
            var key = $"{genKey}/{n}";
            if (_cardPreviews.TryGetValue(key, out var cached))
            {
                bytes = cached.Bytes;
                contentType = cached.ContentType;
                return true;
            }
            if (!TryGetImage(genKey, n, out var original, out var originalType))
            {
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }
            if (!originalType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Array.Empty<byte>();
                contentType = "";
                return false;
            }

            byte[] previewBytes;
            string previewType;
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(original);
                if (Math.Max(image.Width, image.Height) <= CardPreviewMaxEdge)
                {
                    previewBytes = original;
                    previewType = originalType;
                }
                else
                {
                    var scale = (double)CardPreviewMaxEdge / Math.Max(image.Width, image.Height);
                    image.Mutate(x => x.Resize(
                        Math.Max(1, (int)Math.Round(image.Width * scale)),
                        Math.Max(1, (int)Math.Round(image.Height * scale))));

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
                        previewType = "image/png";
                    }
                    else
                    {
                        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
                        previewType = "image/jpeg";
                    }
                    previewBytes = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI job {Id}: could not build card preview for {key} ({ex.Message}); serving the original bytes.");
                previewBytes = original;
                previewType = originalType;
            }

            _cardPreviews[key] = (previewBytes, previewType);
            bytes = previewBytes;
            contentType = previewType;
            return true;
        }

        internal void AttachStorage(UiJobStorage storage) => _storage = storage;

        internal void RestoreEvent(string json)
        {
            lock (_lock) _events.Add(json);
        }

        internal void RestoreDone()
        {
            lock (_lock) _done = true;
        }
    }

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
        private Dictionary<string, UiPersistedImage> _images = new();

        public UiJobStorage(string root, string jobId)
        {
            _folder = Path.Combine(root, jobId);
            _metadataPath = Path.Combine(_folder, "job.json");
            _eventsPath = Path.Combine(_folder, "events.jsonl");
            _imagesPath = Path.Combine(_folder, "images.json");
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

        public void SaveImageReference(string key, string path, string contentType)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    return;
                }
                lock (_writeLock)
                {
                    _images[key] = new UiPersistedImage
                    {
                        Path = fullPath,
                        ContentType = contentType,
                    };
                    WriteJsonAtomically(_imagesPath, _images);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI history: could not save image reference: {ex.Message}");
            }
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

        public static List<UiJob> LoadAll(string root)
        {
            var jobs = new List<UiJob>();
            if (!Directory.Exists(root))
            {
                return jobs;
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
                    // Empty prompt is legitimate (video jobs have an optional
                    // motion prompt); only a missing id makes a record unusable.
                    if (metadata == null
                        || string.IsNullOrWhiteSpace(metadata.Id)
                        || metadata.Prompt == null)
                    {
                        continue;
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
                        InputImagePaths = ResolvePersistedInputPaths(metadata),
                        InputImageWidth = metadata.InputImageWidth,
                        InputImageHeight = metadata.InputImageHeight,
                        GeneratorKeys = metadata.GeneratorKeys,
                        CreatedAt = metadata.CreatedAt,
                        SourceJobId = metadata.SourceJobId,
                        SourceGenerator = metadata.SourceGenerator,
                        SourceIndex = metadata.SourceIndex,
                    };
                    job.AttachStorage(storage);

                    if (File.Exists(storage._eventsPath))
                    {
                        foreach (var line in File.ReadLines(storage._eventsPath))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                using var _ = JsonDocument.Parse(line);
                                job.RestoreEvent(line);
                            }
                            catch (JsonException)
                            {
                                Logger.Log($"UI history: skipped malformed event for job {job.Id}.");
                            }
                        }
                    }

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
                    jobs.Add(job);
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI history: skipped {folder}: {ex.Message}");
                }
            }
            return jobs;
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
        }
    }

    public class UiJobRegistry
    {
        private readonly ConcurrentDictionary<string, UiJob> _jobs = new();
        private readonly object _orderLock = new();
        private readonly List<UiJob> _ordered = new();
        private readonly string _historyRoot;

        // Global append-only envelope log for the polling transport: each
        // entry is a pre-serialized {"jobId","kind":"job-known"|"event",...}
        // JSON object. A job-known announcement (card metadata) always
        // precedes that job's events. Clients poll with an integer cursor
        // (index into this list), so they hold ZERO persistent connections —
        // long-lived SSE streams ate the browser's ~6-connection HTTP/1.1
        // pool once a few windows were open, starving all image loads.
        private readonly object _envelopeLock = new();
        private readonly List<string> _envelopes = new();

        public UiJobRegistry(Settings settings)
        {
            _historyRoot = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            foreach (var job in UiJobStorage.LoadAll(_historyRoot).OrderBy(j => j.CreatedAt))
            {
                if (job.IsDone)
                {
                    GenerationArchive.MarkExternalJobInterrupted(job.Id);
                }
                _jobs[job.Id] = job;
                _ordered.Add(job);
                AppendJobKnown(job);
                job.AttachEmitCallback(AppendEventEnvelope);
            }
            if (_ordered.Count > 0)
            {
                Logger.Log($"UI history: restored {_ordered.Count} job(s) from disk.");
            }
        }

        public void Add(UiJob job)
        {
            var storage = new UiJobStorage(_historyRoot, job.Id);
            storage.Initialize(job);
            _jobs[job.Id] = job;
            lock (_orderLock) _ordered.Add(job);
            AppendJobKnown(job);
            job.AttachEmitCallback(AppendEventEnvelope);
        }

        public UiJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

        /// Chronological (oldest first) snapshot, for page-load hydration.
        public List<UiJob> ListChronological()
        {
            lock (_orderLock) return _ordered.ToList();
        }

        private void AppendJobKnown(UiJob job)
        {
            var metadata = JsonSerializer.Serialize(new
            {
                id = job.Id,
                prompt = job.Prompt,
                gens = job.GeneratorKeys,
                hasImage = job.HasInputImage,
                inputCount = job.InputImageCount,
                createdAt = job.CreatedAt.ToString("HH:mm:ss"),
                createdAtUnixMs = new DateTimeOffset(job.CreatedAt).ToUnixTimeMilliseconds(),
            });
            lock (_envelopeLock)
            {
                _envelopes.Add($"{{\"jobId\":\"{job.Id}\",\"kind\":\"job-known\",\"job\":{metadata}}}");
            }
        }

        private void AppendEventEnvelope(UiJob job, string eventJson)
        {
            lock (_envelopeLock)
            {
                _envelopes.Add($"{{\"jobId\":\"{job.Id}\",\"kind\":\"event\",\"event\":{eventJson}}}");
            }
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

        /// gpt-image-2 anti-murk guidance (on by default): when enabled and
        /// non-empty, this text is appended to the prompt sent to the gpt2
        /// target only — both /generations and /edits, since both ride the
        /// same UI key. Every other target receives the untouched prompt.
        /// Exists because gpt-image-2 habitually drifts into dark, murky,
        /// underexposed output unless told not to on EVERY call (see the
        /// Universal Image Prompt Defaults policy).
        public bool Gpt2GuidanceEnabled { get; init; } = true;
        public string Gpt2GuidanceText { get; init; } = "";
        public string VideoMode { get; init; } = "normal";
        public int VideoDurationSeconds { get; init; } = 10;
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

        public static bool IsKnownShape(string shape)
        {
            var value = (shape ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Shapes, value) >= 0;
        }

        public static string GrokAspectForInput(int width, int height)
            => NearestAspect(width, height, GrokAspects);

        public static string GoogleAspectForInput(int width, int height)
            => NearestAspect(width, height, GoogleAspects);

        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3AspectForInput(int width, int height)
        {
            ValidateInputDimensions(width, height);
            var sourceRatio = (double)width / height;
            return IdeogramV3Aspects
                .OrderBy(candidate => RatioDistance(sourceRatio, (double)candidate.Width / candidate.Height))
                .First()
                .Aspect;
        }

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

        /// grok-api resolution tier. Standard = 1k; both higher tiers = 2k
        /// (2k is grok-api's ceiling).
        public static string GrokResolution(string detail)
            => Norm(detail, Details) == "standard" ? "1k" : "2k";

        /// Ideogram v4 resolution string. v4 is 2K-native — detail has no
        /// effect, only shape. Empty = API default (2048x2048).
        public static string IdeogramV4Resolution(string shape)
        {
            return Norm(shape, Shapes) switch
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
        public const string KeyRecraft = "recraft";
        public const string KeyBfl = "bfl";
        public const string KeyGoogle = "google";
        public const string KeyGooglePro = "googlepro";
        public const string KeyLocalKlein = "local-klein";
        public const string KeyLocalZImage = "local-zimage";
        public const string KeyGrokWeb = "grok-web";
        public const string KeyGrokWebVideo = "grok-web-video";
        public const string KeyGrokApi = "grok-api";
        public const string KeyGrokApiPro = "grok-api-pro";
        public const string KeyMetaWeb = "meta-web";

        // Single source of truth for which UI targets accept an input image.
        // Exposed to the frontend via /api/config (per-generator imageCapable
        // flag) and consulted in BuildGenerator, so the two can't drift.
        // grok-web edits use browser app-chat (imagine-image-edit). Everything
        // NOT in this set still runs when a job carries an input image — it
        // just receives the prompt text only. That "sans image" behavior is a
        // user-specified product requirement (2026-07-28): keep every target
        // usable on image jobs, and let the UI badge the ones that won't see
        // the attachment.
        public static readonly string[] ImageCapableKeys =
        {
            KeyGpt2,
            KeyGrokWeb,
            KeyGrokApi,
            KeyGrokApiPro,
            KeyGoogle,
            KeyGooglePro,
            KeyBfl,
            KeyIdeogramV3,
            KeyRecraft,
        };

        public static bool IsImageCapable(string key)
            => ImageCapableKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        // States, per selected generator, exactly what function the job's
        // input image served. Rendered under the INPUT cell of the combined
        // contact sheet. Must stay truthful to BuildGenerator's routing: edit
        // generators consume it as the edit source, Recraft as the
        // image-to-image source, the reference-style targets as a
        // style/reference guide, and text-only targets never receive it
        // (user-specified attachment policy, 2026-07-28).
        private static string DescribeInputImageFunction(string key) => key switch
        {
            KeyGpt2 or KeyGrokWeb or KeyGrokApi or KeyGrokApiPro => "edit source",
            KeyRecraft => "image-to-image source",
            KeyGoogle or KeyGooglePro or KeyBfl or KeyIdeogramV3 => "style/reference image",
            KeyGrokWebVideo => "video source",
            _ => "NOT sent (text-only target, prompt only)",
        };

        public static string BuildInputImageRoleText(IReadOnlyList<string> generatorKeys)
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
        private readonly GrokWebBrowserClient? _grokWebBrowserClient;
        private readonly string? _grokWebBrowserStartupProblem;
        private readonly MetaWebClientOptions _metaWebOptions;
        private readonly MetaWebClient? _metaWebClient;
        private readonly string? _metaWebStartupProblem;
        private readonly object _comfyProbeLock = new();
        private DateTime _comfyProbeExpiresAt;
        private string? _cachedComfyProbeProblem;

        // Cap concurrent jobs, not generators-within-a-job: each job already
        // fans out internally, and each generator has its own semaphore.
        private readonly SemaphoreSlim _jobLimit = new(4);

        public UiJobRunner(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            _settings = settings;
            _stats = stats;
            _options = options;
            _imageManager = new ImageManager(settings, stats);
            _generatorGroups = new GeneratorGroups(settings, concurrency: 1, stats);
            var grokWebCookiePath = ResolveGrokWebCookiePath();
            if (grokWebCookiePath != null)
            {
                try
                {
                    // Video + image-edit app-chat calls share one real browser
                    // for the UI lifetime and serialize inside GrokWebBrowserClient.
                    _grokWebBrowserClient = new GrokWebBrowserClient(
                        GrokWebBrowserClient.BuildOptions(
                            settings,
                            grokWebCookiePath,
                            headedOverride: options.GrokWebHeaded));
                }
                catch (Exception ex)
                {
                    _grokWebBrowserStartupProblem = ex.Message;
                    Logger.Log($"Grok web browser (video/edit) unavailable: {ex.Message}");
                }
            }
            _metaWebOptions = MetaWebClient.BuildOptions(
                settings,
                cookieOverride: options.MetaWebCookies,
                headedOverride: options.MetaWebHeaded);
            if (MetaWebClient.DescribeAvailabilityProblem(_metaWebOptions) == null)
            {
                try
                {
                    // One browser context for the whole UI lifetime. MetaWebClient
                    // serializes page use internally, so concurrent UI jobs cannot
                    // type into the same Meta composer at once.
                    _metaWebClient = new MetaWebClient(_metaWebOptions);
                }
                catch (Exception ex)
                {
                    _metaWebStartupProblem = ex.Message;
                    Logger.Log($"Meta web unavailable: {ex.Message}");
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

        public string? DescribeAvailabilityProblem(string key) => key switch
        {
            KeyGpt2 or KeyGpt1 or KeyGpt1Mini
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GptImage2, _settings),
            KeyIdeogram
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV4, _settings),
            KeyIdeogramV3
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.IdeogramV3, _settings),
            KeyRecraft
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.RecraftV41, _settings),
            KeyBfl
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.BFLFlux2ProPreview, _settings),
            KeyGoogle or KeyGooglePro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GoogleNanoBananaPro, _settings),
            KeyLocalKlein
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalFlux2Klein),
            KeyLocalZImage
                => DescribeComfyAvailability(ImageGeneratorApiType.LocalZImage),
            // Text-to-image only needs cookies (imagine WS). Edit-with-image
            // additionally needs the Playwright browser; that is enforced at
            // job build time when an input image is attached.
            KeyGrokWeb => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : null,
            KeyGrokWebVideo => ResolveGrokWebCookiePath() == null
                ? "grok-web cookie file not found (Settings.GrokWebCookiePath or --grok-web-cookies)"
                : _grokWebBrowserStartupProblem,
            KeyGrokApi or KeyGrokApiPro
                => ProviderKeyValidator.DescribeKeyProblem(ImageGeneratorApiType.GrokImagine, _settings),
            KeyMetaWeb => _metaWebStartupProblem ?? MetaWebClient.DescribeAvailabilityProblem(_metaWebOptions),
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
            await _jobLimit.WaitAsync();
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

                var tasks = spec.GeneratorKeys.Select(key => RunOneAsync(job, spec, key, pd)).ToArray();
                var results = await Task.WhenAll(tasks);

                // Build + save the standard combined contact sheet for the
                // archive; never popped open (the browser IS the viewer here).
                // Jobs with an input image get it rendered as the sheet's
                // first cell, with an explicit statement of what each
                // selected generator did (or didn't do) with it.
                try
                {
                    var combined = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                        results, job.Prompt, _settings, openWhenDone: false,
                        inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                        inputImageRole: job.HasInputImage ? BuildInputImageRoleText(spec.GeneratorKeys) : null);
                    if (!string.IsNullOrEmpty(combined) && File.Exists(combined))
                    {
                        var bytes = await File.ReadAllBytesAsync(combined);
                        job.StoreImage("grid", 0, bytes, "image/png", combined);
                        job.Emit(new { type = "grid", url = $"/api/jobs/{job.Id}/images/grid/0", path = combined });
                    }
                    Logger.Log($"[ui #{job.Id}] grid saved: {combined}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ui #{job.Id}] grid build failed: {ex.Message}");
                }
            }
            finally
            {
                _jobLimit.Release();
                job.Emit(new { type = "job-done" });
                job.MarkDone();
                Logger.Log($"[ui #{job.Id}] DONE");
            }
        }

        private async Task<TaskProcessResult> RunOneAsync(UiJob job, UiJobSpec spec, string key, PromptDetails pd)
        {
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
            var want = key == KeyGrokWebVideo ? 1 : Math.Clamp(spec.ImageCount, 1, 10);
            var enablePartials = want == 1;

            var urls = new List<string>();
            var merged = new TaskProcessResult
            {
                PromptDetails = pd,
                ImageGeneratorDescription = key,
                ContentType = "image/png",
            };
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
                try
                {
                    IImageGenerator generator;
                    if (key is KeyGrokWeb or KeyGrokWebVideo)
                    {
                        var cookiePath = ResolveGrokWebCookiePath()
                            ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                        // Image edit and video both need the integrity-signed
                        // browser app-chat path. Text-to-image stays on the WS.
                        var needsAppChatBrowser = key == KeyGrokWebVideo
                            || (key == KeyGrokWeb && job.HasInputImage);
                        var appChatBrowser = needsAppChatBrowser
                            ? _grokWebBrowserClient ?? throw new InvalidOperationException(
                                _grokWebBrowserStartupProblem
                                ?? "grok-web browser client is unavailable (required for edit-with-image and video; run --playwright-install once)")
                            : null;
                        grokWebClient = GrokWebClient.FromCookieFile(cookiePath, appChatBrowser);
                        generator = key == KeyGrokWebVideo
                            ? await BuildGrokWebVideoAsync(grokWebClient, job, spec)
                            : await BuildGrokWebAsync(grokWebClient, job, spec);
                    }
                    else if (key == KeyMetaWeb)
                    {
                        // Text-only target: on image jobs it runs from the prompt
                        // alone (see ImageCapableKeys).
                        generator = new MetaWebImagineGenerator(
                            _metaWebClient ?? throw new InvalidOperationException(
                                DescribeAvailabilityProblem(KeyMetaWeb) ?? "meta-web browser client is unavailable"),
                            maxConcurrency: 1,
                            _stats);
                    }
                    else
                    {
                        generator = BuildGenerator(key, spec, job, enablePartials);
                    }

                    copy = pd.Copy();
                    // gpt-image-2 only: append the anti-murk guidance as a recorded
                    // prompt-transformation step, so the archive, annotations, and
                    // sidecar logs all show the exact text that went to OpenAI.
                    // Other generators keep the untouched prompt (their copies are
                    // made from the shared pd independently).
                    if (key == KeyGpt2
                        && spec.Gpt2GuidanceEnabled
                        && !string.IsNullOrWhiteSpace(spec.Gpt2GuidanceText))
                    {
                        var guided = $"{copy.Prompt}\n\n{spec.Gpt2GuidanceText.Trim()}";
                        copy.ReplacePrompt(guided, guided, TransformationType.ManualSuffixation);
                    }
                    else if (key == KeyGpt2)
                    {
                        // Loud on purpose: a guidance-free gpt2 call reliably
                        // comes back dark and murky, and this once went
                        // unnoticed for two days (2026-07-31 → 08-02).
                        Logger.Log($"[ui #{job.Id}]   gpt2 anti-murk guidance is OFF for this call — expect darker output");
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
                    var result = await GenerationArchive.ExecuteAndSaveAsync(
                        generator,
                        copy,
                        _imageManager,
                        new GenerationArchiveContext
                        {
                            Source = "ui",
                            ExternalJobId = job.Id,
                            GeneratorKey = key,
                        });

                    if (result.IsSuccess
                        && !string.IsNullOrEmpty(result.GeneratedMediaPath)
                        && File.Exists(result.GeneratedMediaPath))
                    {
                        var mediaBytes = await File.ReadAllBytesAsync(result.GeneratedMediaPath);
                        mediaType = string.IsNullOrWhiteSpace(result.GeneratedMediaContentType)
                            ? "application/octet-stream"
                            : result.GeneratedMediaContentType;
                        var idx = urls.Count;
                        job.StoreImage(key, idx, mediaBytes, mediaType, result.GeneratedMediaPath);
                        urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
                        merged.GeneratedMediaPath = result.GeneratedMediaPath;
                        merged.GeneratedMediaContentType = result.GeneratedMediaContentType;
                    }
                    else if (result.IsSuccess)
                    {
                        int i = 0;
                        foreach (var bytes in result.GetAllImages)
                        {
                            firstImageBytes ??= bytes;
                            var idx = urls.Count;
                            job.StoreImage(
                                key,
                                idx,
                                bytes,
                                result.ContentType ?? "image/png",
                                result.GetSavedRawImagePath(i));
                            merged.SetImageBytes(idx, bytes);
                            merged.SetSavedRawImagePath(idx, result.GetSavedRawImagePath(i));
                            urls.Add($"/api/jobs/{job.Id}/images/{key}/{idx}");
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
            var actualSize = firstImageBytes != null ? ReadImageSize(firstImageBytes) : null;
            // When N>1, show how many of the requested images actually came back.
            if (want > 1 && urls.Count > 0)
            {
                label = string.IsNullOrEmpty(label) ? $"{urls.Count}/{want} imgs" : $"{label} · {urls.Count}/{want} imgs";
            }

            var ok = urls.Count > 0;
            merged.IsSuccess = ok;
            merged.CreateTotalMs = createMs;
            merged.DownloadTotalMs = downloadMs;
            if (!ok)
            {
                merged.ErrorMessage = firstError ?? "Generation completed without returning usable image or video media.";
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

        // grok-web is async-built because the edit path uploads the source
        // image to grok.com before the generator exists.
        private async Task<IImageGenerator> BuildGrokWebAsync(GrokWebClient client, UiJob job, UiJobSpec spec)
        {
            if (job.HasInputImage)
            {
                // Edit uses browser-backed app-chat (imagine-image-edit with
                // mediaGenInput.imageToImage.inputAssets). The WS image_uri
                // path silently ignores the source. Auto AR resolves from the
                // validated source dimensions before the provider call.
                var editAspect = UiShapeMapping.GrokAspect(
                    spec.Shape,
                    job.InputImageWidth,
                    job.InputImageHeight);
                return await GrokWebImagineEditGenerator.CreateAsync(
                    client, job.InputImagePath, maxConcurrency: 1, _stats,
                    pro: _options.GrokWebPro,
                    aspectRatio: editAspect,
                    enableSideBySide: _options.GrokWebSideBySide,
                    settings: _settings);
            }
            var mapped = UiShapeMapping.GrokAspect(spec.Shape);
            // Unlike the official API, the consumer transport has no working
            // prompt-aware auto mode: live tests (2026-07-20) showed both
            // literal "auto" and an omitted aspect_ratio always return Grok's
            // native 2:3 default regardless of the prompt. 2:3 is a poor
            // universal shape, so an unspecified shape requests square 1:1
            // instead (a declared input default chosen before the call, not a
            // fallback). Explicit shapes map as usual; edit-with-image auto
            // still derives the source's ratio above.
            var ar = mapped == "" ? "1:1" : mapped;
            return new GrokWebImagineGenerator(
                client, maxConcurrency: 1, _stats,
                pro: _options.GrokWebPro,
                aspectRatio: ar,
                enableSideBySide: _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private async Task<IImageGenerator> BuildGrokWebVideoAsync(
            GrokWebClient client,
            UiJob job,
            UiJobSpec spec)
        {
            if (!job.HasInputImage)
            {
                throw new InvalidOperationException("grok-web image-to-video requires a source image");
            }
            var aspectRatio = spec.VideoAspectRatio == "source"
                ? UiShapeMapping.GrokAspectForInput(job.InputImageWidth, job.InputImageHeight)
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
                        partialSaveFolder: _settings.ImageDownloadBaseFolder,
                        popUpPartials: false,
                        imageCount: 1,
                        partialImageCallback: !enablePartials ? null : (partialIndex, imageIndex, bytes) =>
                        {
                            var outputIndex = Math.Max(0, imageIndex);
                            // Preview and final bytes deliberately share one stable
                            // URL. Each partial replaces the previous bytes, then
                            // RunOneAsync replaces them with the completed image.
                            job.StoreImage(key, outputIndex, bytes, "image/png");
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
                {
                    // FLUX.2 takes explicit width/height (shape+detail mapped, ~4 MP
                    // ceiling) and optional input_image conditioning for a pasted
                    // image. No n parameter exists on the BFL API — n is ignored.
                    RequireKey(_settings.BFLApiKey, "BFLApiKey", key);
                    var (bflWidth, bflHeight) = UiShapeMapping.BflSize(
                        spec.Shape,
                        spec.Detail,
                        job.InputImageWidth,
                        job.InputImageHeight);
                    return new BFLGenerator(
                        ImageGeneratorApiType.BFLFlux2ProPreview, _settings.BFLApiKey,
                        maxConcurrency: 2, "1:1", false, bflWidth, bflHeight, _stats, "bfl ui",
                        inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                }

                case KeyIdeogram:
                {
                    // V4 is a dedicated text-only target now (split from V3 on
                    // 2026-07-28 at the user's request): the v4 endpoint is
                    // JSON-only with no reference-image support, so on image
                    // jobs it runs from the prompt alone (see ImageCapableKeys)
                    // — Ideogram's text-to-image is good enough to want even
                    // when an image is attached. v4 is 2K-native: shape maps to
                    // a documented resolution (detail has no effect).
                    RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                    return new IdeogramV4Generator(
                        _settings.IdeogramApiKey, maxConcurrency: 1,
                        UiShapeMapping.IdeogramV4Resolution(spec.Shape),
                        IdeogramRenderingSpeed.DEFAULT,
                        _stats, "ideogram ui",
                        imageCount: Math.Clamp(spec.ImageCount, 1, 8));
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
                        imageCount: Math.Clamp(spec.ImageCount, 1, 8));
                }

                case KeyRecraft:
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
                    var recraftN = Math.Clamp(spec.ImageCount, 1, 6);
                    // With an input image, RecraftGenerator runs image-to-image
                    // (V4.1-native reference path; output size follows the source).
                    return new RecraftGenerator(
                        _settings.RecraftApiKey, maxConcurrency: 1,
                        RecraftImageSize._1024x1024, RecraftStyle.any,
                        null, null, null, _stats, "recraft ui",
                        model: RecraftModel.recraftv4_1,
                        inputImagePath: job.HasInputImage ? job.InputImagePath : null,
                        sizeOverride: recraftAspect,
                        imageCount: recraftN);
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

        public async ValueTask DisposeAsync()
        {
            if (_grokWebBrowserClient != null)
            {
                await _grokWebBrowserClient.DisposeAsync();
            }
            if (_metaWebClient != null)
            {
                await _metaWebClient.DisposeAsync();
            }
            _jobLimit.Dispose();
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
