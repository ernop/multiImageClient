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

namespace MultiImageClient
{
    /// One web-UI generation job: (optional input image, prompt, generator
    /// set, options). Holds an append-only event log that SSE subscribers
    /// replay from any index (so a page refresh mid-job still sees the full
    /// history), plus a live byte cache and durable references to saved result
    /// files so completed cards survive a server restart.
    public class UiJob
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
        public required string Prompt { get; init; }
        public string InputImagePath { get; init; } = "";
        public IReadOnlyList<string> GeneratorKeys { get; init; } = Array.Empty<string>();
        public DateTime CreatedAt { get; init; } = DateTime.Now;
        public string SourceJobId { get; init; } = "";
        public string SourceGenerator { get; init; } = "";
        public int? SourceIndex { get; init; }

        private readonly object _lock = new();
        private readonly List<string> _events = new();
        private bool _done;
        private UiJobStorage? _storage;

        public bool IsDone
        {
            get { lock (_lock) return _done; }
        }

        private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _images = new();

        public bool HasInputImage => !string.IsNullOrEmpty(InputImagePath);

        public void Emit(object evt)
        {
            var json = JsonSerializer.Serialize(evt);
            lock (_lock)
            {
                _events.Add(json);
                _storage?.AppendEvent(json);
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
                    if (metadata == null
                        || string.IsNullOrWhiteSpace(metadata.Id)
                        || string.IsNullOrWhiteSpace(metadata.Prompt))
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
                        InputImagePath = metadata.InputImagePath,
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

        private sealed class UiPersistedJob
        {
            public string Id { get; set; } = "";
            public string Prompt { get; set; } = "";
            public string InputImagePath { get; set; } = "";
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
        }

        public UiJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

        /// Chronological (oldest first) snapshot, for page-load hydration.
        public List<UiJob> ListChronological()
        {
            lock (_orderLock) return _ordered.ToList();
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
        /// gets an exact WxH, grok gets an aspect ratio + 1k/2k resolution,
        /// edits with shape=auto inherit the source image).
        /// Shapes: auto | square | landscape | portrait | wide | tall.
        public string Shape { get; init; } = "auto";

        /// Output detail tier: standard (~1K) | high (~2K) | max (~4K-ish,
        /// capped by each backend's envelope).
        public string Detail { get; init; } = "standard";
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

        /// gpt-image-2 size string. All values are multiples of 16, within
        /// the [655360, 8294400] pixel envelope, edges < 3840 (2880x2880 is
        /// exactly the max pixel count).
        public static string Gpt2Size(string shape, string detail)
        {
            return (Norm(shape, Shapes), Norm(detail, Details)) switch
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

        /// Aspect-ratio string for the grok generators. Empty string means
        /// "no preference" — callers substitute their own default (or, for
        /// edits, let the source image's AR win).
        public static string GrokAspect(string shape)
        {
            return Norm(shape, Shapes) switch
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
        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3Aspect(string shape)
        {
            return Norm(shape, Shapes) switch
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
        public static (int Width, int Height) BflSize(string shape, string detail)
        {
            var big = Norm(detail, Details) != "standard";
            return Norm(shape, Shapes) switch
            {
                "landscape" => big ? (2304, 1536) : (1248, 832),
                "portrait" => big ? (1536, 2304) : (832, 1248),
                "wide" => big ? (2560, 1440) : (1344, 768),
                "tall" => big ? (1440, 2560) : (768, 1344),
                _ => big ? (1920, 1920) : (1024, 1024),
            };
        }

        /// Gemini imageConfig.aspectRatio; empty (shape=auto) omits the field
        /// so the model decides.
        public static string GoogleAspect(string shape)
        {
            return Norm(shape, Shapes) switch
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
        public const string KeyGpt2 = "gpt2";
        public const string KeyGpt1 = "gpt1";
        public const string KeyGpt1Mini = "gpt1-mini";
        public const string KeyIdeogram = "ideogram";
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
                    // Video app-chat calls share one real browser for the UI
                    // lifetime and serialize inside GrokWebBrowserClient.
                    _grokWebBrowserClient = new GrokWebBrowserClient(
                        GrokWebBrowserClient.BuildOptions(
                            settings,
                            grokWebCookiePath,
                            headedOverride: options.GrokWebHeaded));
                }
                catch (Exception ex)
                {
                    _grokWebBrowserStartupProblem = ex.Message;
                    Logger.Log($"Grok web video unavailable: {ex.Message}");
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
                Logger.Log($"[ui #{job.Id}] START ({spec.GeneratorKeys.Count} gen(s), image={(job.HasInputImage ? job.InputImagePath : "none")}): {job.Prompt}");

                var pd = new PromptDetails();
                pd.ReplacePrompt(job.Prompt, job.Prompt, TransformationType.InitialPrompt);

                var tasks = spec.GeneratorKeys.Select(key => RunOneAsync(job, spec, key, pd)).ToArray();
                var results = await Task.WhenAll(tasks);

                // Build + save the standard combined contact sheet for the
                // archive; never popped open (the browser IS the viewer here).
                try
                {
                    var combined = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                        results, job.Prompt, _settings, openWhenDone: false);
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
            GrokWebClient? grokWebClient = null;
            PromptDetails? copy = null;
            try
            {
                IImageGenerator generator;
                if (key is KeyGrokWeb or KeyGrokWebVideo)
                {
                    var cookiePath = ResolveGrokWebCookiePath()
                        ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                    var appChatBrowser = key == KeyGrokWebVideo
                        ? _grokWebBrowserClient ?? throw new InvalidOperationException(
                            DescribeAvailabilityProblem(KeyGrokWebVideo)
                            ?? "grok-web video browser client is unavailable")
                        : null;
                    grokWebClient = GrokWebClient.FromCookieFile(cookiePath, appChatBrowser);
                    generator = key == KeyGrokWebVideo
                        ? await BuildGrokWebVideoAsync(grokWebClient, job, spec)
                        : await BuildGrokWebAsync(grokWebClient, job, spec);
                }
                else if (key == KeyMetaWeb)
                {
                    if (job.HasInputImage)
                    {
                        throw new NotSupportedException("meta-web image editing is not implemented; remove the input image for text-to-image generation");
                    }
                    generator = new MetaWebImagineGenerator(
                        _metaWebClient ?? throw new InvalidOperationException(
                            DescribeAvailabilityProblem(KeyMetaWeb) ?? "meta-web browser client is unavailable"),
                        maxConcurrency: 1,
                        _stats);
                }
                else
                {
                    generator = BuildGenerator(key, spec, job);
                }

                copy = pd.Copy();
                if (key == KeyGrokWebVideo && !string.IsNullOrWhiteSpace(job.SourceGenerator))
                {
                    copy.RuntimeMeta["sourceJobId"] = job.SourceJobId;
                    copy.RuntimeMeta["sourceGenerator"] = job.SourceGenerator;
                    copy.RuntimeMeta["sourceIndex"] = job.SourceIndex?.ToString() ?? "";
                }
                // Estimated cost for this call (per-image estimate x n, from
                // each generator's GetCost). Estimates, not bills — but good
                // enough for calibrating which providers are worth their price.
                var costEstimate = generator.GetCost();
                Logger.Log($"[ui #{job.Id}]   -> {generator.GetGeneratorSpecPart()} (~${costEstimate:0.###})");
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

                var urls = new List<string>();
                var mediaType = "";
                byte[] firstImageBytes = null;
                if (result.IsSuccess
                    && !string.IsNullOrEmpty(result.GeneratedMediaPath)
                    && File.Exists(result.GeneratedMediaPath))
                {
                    var mediaBytes = await File.ReadAllBytesAsync(result.GeneratedMediaPath);
                    mediaType = string.IsNullOrWhiteSpace(result.GeneratedMediaContentType)
                        ? "application/octet-stream"
                        : result.GeneratedMediaContentType;
                    job.StoreImage(key, 0, mediaBytes, mediaType, result.GeneratedMediaPath);
                    urls.Add($"/api/jobs/{job.Id}/images/{key}/0");
                }
                else
                {
                    int i = 0;
                    foreach (var bytes in result.GetAllImages)
                    {
                        firstImageBytes ??= bytes;
                        job.StoreImage(
                            key,
                            i,
                            bytes,
                            result.ContentType ?? "image/png",
                            result.GetSavedRawImagePath(i));
                        urls.Add($"/api/jobs/{job.Id}/images/{key}/{i}");
                        i++;
                    }
                }

                var elapsed = result.CreateTotalMs + result.DownloadTotalMs;
                if (elapsed <= 0)
                {
                    elapsed = wallClock.ElapsedMilliseconds;
                }
                var label = copy.RuntimeMeta.TryGetValue("label", out var l) && !string.IsNullOrEmpty(l)
                    ? l
                    : result.ImageGeneratorDescription;
                // Append the actual produced pixel size so the card reflects what
                // the provider really returned, not just the requested aspect.
                // grok /images/edits in particular does not reliably honor the
                // requested AR, so "AR 2:3 · 1024x1024" makes the mismatch visible
                // instead of silently mislabeling a square as 2:3.
                var actualSize = firstImageBytes != null ? ReadImageSize(firstImageBytes) : null;
                if (actualSize != null)
                {
                    label = string.IsNullOrEmpty(label) ? actualSize : $"{label} · {actualSize}";
                }
                var ok = result.IsSuccess && urls.Count > 0;
                if (result.IsSuccess && !ok)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = "Generation completed without returning usable image or video media.";
                }
                var errorMessage = ok ? "" : (result.ErrorMessage ?? "unknown error");
                job.Emit(new
                {
                    type = "gen-result",
                    gen = key,
                    ok,
                    error = errorMessage,
                    ms = elapsed,
                    images = urls,
                    mediaType,
                    label,
                    videoMode = key == KeyGrokWebVideo ? spec.VideoMode : null,
                    videoDurationSeconds = key == KeyGrokWebVideo
                        ? spec.VideoDurationSeconds
                        : (int?)null,
                    videoResolution = key == KeyGrokWebVideo ? spec.VideoResolution : null,
                    videoAspectRatio = key == KeyGrokWebVideo ? spec.VideoAspectRatio : null,
                    // Failed calls generally aren't billed, so report 0 for them.
                    cost = ok ? costEstimate : 0m,
                });
                Logger.Log($"[ui #{job.Id}]   <- {(ok ? "OK" : $"FAIL ({errorMessage})")} from {key} in {elapsed} ms");
                return result;
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
                job.Emit(new
                {
                    type = "gen-result",
                    gen = key,
                    ok = false,
                    error = detail,
                    ms = wallClock.ElapsedMilliseconds,
                    images = new List<string>(),
                    label = key,
                });
                return new TaskProcessResult
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    PromptDetails = copy ?? pd,
                    ImageGeneratorDescription = key,
                };
            }
            finally
            {
                grokWebClient?.Dispose();
            }
        }

        // grok-web is async-built because the edit path uploads the source
        // image to grok.com before the generator exists.
        private async Task<IImageGenerator> BuildGrokWebAsync(GrokWebClient client, UiJob job, UiJobSpec spec)
        {
            if (job.HasInputImage)
            {
                // Edit rides the same imagine WebSocket as text-to-image (source
                // image passed as properties.image_uri), sidestepping the
                // anti-bot-blocked /rest/app-chat endpoint. Empty AR = inherit
                // the source image's shape.
                var editAspect = UiShapeMapping.GrokAspect(spec.Shape);
                return await GrokWebImagineEditGenerator.CreateAsync(
                    client, job.InputImagePath, maxConcurrency: 1, _stats,
                    pro: _options.GrokWebPro,
                    aspectRatio: editAspect,
                    enableSideBySide: _options.GrokWebSideBySide,
                    settings: _settings);
            }
            var mapped = UiShapeMapping.GrokAspect(spec.Shape);
            // "auto" tells GrokWebClient to omit aspect_ratio. Unlike the
            // official API, the consumer transport has no working prompt-aware
            // auto mode; its current native default is portrait 2:3.
            var ar = mapped == "" ? "auto" : mapped;
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
                ? GrokWebImagineEditGenerator.DeriveAspectRatio(job.InputImagePath)
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

        private IImageGenerator BuildGenerator(string key, UiJobSpec spec, UiJob job)
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
                    var size = UiShapeMapping.Gpt2Size(spec.Shape, spec.Detail);
                    if (job.HasInputImage)
                    {
                        return new GptImage2EditGenerator(
                            _settings.OpenAIApiKey, maxConcurrency: 2,
                            new[] { job.InputImagePath },
                            size, quality, _stats, "ui",
                            imageCount: spec.ImageCount);
                    }
                    return new GptImage2Generator(
                        _settings.OpenAIApiKey, maxConcurrency: 2,
                        sizePool: new[] { size },
                        moderation: spec.Moderation,
                        qualityPool: new[] { quality },
                        stats: _stats, name: "ui",
                        partialSaveFolder: _settings.ImageDownloadBaseFolder,
                        popUpPartials: false,
                        imageCount: spec.ImageCount,
                        partialImageCallback: (partialIndex, imageIndex, bytes) =>
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
                    if (job.HasInputImage)
                    {
                        throw new NotSupportedException($"{key} image editing is not implemented in the local UI");
                    }
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
                        imageCount: spec.ImageCount);
                }

                case KeyGrokApi:
                case KeyGrokApiPro:
                {
                    RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", key);
                    var pro = key == KeyGrokApiPro;
                    var mappedAr = UiShapeMapping.GrokAspect(spec.Shape);
                    if (job.HasInputImage)
                    {
                        // Empty AR = inherit the source image's aspect ratio.
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
                        imageCount: spec.ImageCount);
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
                    var googleAspect = UiShapeMapping.GoogleAspect(spec.Shape);
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
                    var (bflWidth, bflHeight) = UiShapeMapping.BflSize(spec.Shape, spec.Detail);
                    return new BFLGenerator(
                        ImageGeneratorApiType.BFLFlux2ProPreview, _settings.BFLApiKey,
                        maxConcurrency: 2, "1:1", false, bflWidth, bflHeight, _stats, "bfl ui",
                        inputImagePath: job.HasInputImage ? job.InputImagePath : null);
                }

                case KeyIdeogram:
                {
                    RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", key);
                    var ideogramN = Math.Clamp(spec.ImageCount, 1, 8);
                    if (!job.HasInputImage)
                    {
                        // v4 is 2K-native: shape maps to a documented resolution
                        // (detail has no effect), num_images up to 8.
                        return new IdeogramV4Generator(
                            _settings.IdeogramApiKey, maxConcurrency: 1,
                            UiShapeMapping.IdeogramV4Resolution(spec.Shape),
                            IdeogramRenderingSpeed.DEFAULT,
                            _stats, "ideogram ui",
                            imageCount: ideogramN);
                    }
                    // Reference/guide: route to V3 (V4 is JSON-only, no reference
                    // images) and pass the pasted image as a style reference.
                    return new IdeogramV3Generator(
                        _settings.IdeogramApiKey, maxConcurrency: 1,
                        IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON,
                        UiShapeMapping.IdeogramV3Aspect(spec.Shape), IdeogramRenderingSpeed.QUALITY,
                        "", _stats, "ideogram ui",
                        inputImagePath: job.InputImagePath,
                        imageCount: ideogramN);
                }

                case KeyRecraft:
                {
                    RequireKey(_settings.RecraftApiKey, "RecraftApiKey", key);
                    // Recraft accepts the same "w:h" aspect strings as grok;
                    // "" (shape=auto) omits size so Recraft picks one from the
                    // prompt (or, with an input image, follows the source).
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
