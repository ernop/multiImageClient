#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// One web-UI generation job: (optional input image, prompt, generator
    /// set, options). Holds an append-only event log that SSE subscribers
    /// replay from any index (so a page refresh mid-job still sees the full
    /// history), plus an in-memory store of result image bytes so the browser
    /// can display results without touching the saves/ folder layout.
    public class UiJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public required string Prompt { get; init; }
        public string InputImagePath { get; init; } = "";
        public IReadOnlyList<string> GeneratorKeys { get; init; } = Array.Empty<string>();
        public DateTime CreatedAt { get; } = DateTime.Now;

        private readonly object _lock = new();
        private readonly List<string> _events = new();
        private bool _done;

        public bool IsDone
        {
            get { lock (_lock) return _done; }
        }

        private readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> _images = new();

        public bool HasInputImage => !string.IsNullOrEmpty(InputImagePath);

        public void Emit(object evt)
        {
            var json = JsonSerializer.Serialize(evt);
            lock (_lock) _events.Add(json);
        }

        public void MarkDone()
        {
            lock (_lock) _done = true;
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

        public void StoreImage(string genKey, int n, byte[] bytes, string contentType)
            => _images[$"{genKey}/{n}"] = (bytes, contentType);

        public bool TryGetImage(string genKey, int n, out byte[] bytes, out string contentType)
        {
            if (_images.TryGetValue($"{genKey}/{n}", out var v))
            {
                bytes = v.Bytes;
                contentType = v.ContentType;
                return true;
            }
            bytes = Array.Empty<byte>();
            contentType = "";
            return false;
        }
    }

    public class UiJobRegistry
    {
        private readonly ConcurrentDictionary<string, UiJob> _jobs = new();
        private readonly object _orderLock = new();
        private readonly List<UiJob> _ordered = new();

        public void Add(UiJob job)
        {
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
    public class UiJobRunner
    {
        // Generator keys exposed to the browser. Availability is checked at
        // /api/config time and again defensively at build time.
        public const string KeyGpt2 = "gpt2";
        public const string KeyGrokWeb = "grok-web";
        public const string KeyGrokApi = "grok-api";
        public const string KeyGrokApiPro = "grok-api-pro";

        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly RunOptions _options;
        private readonly ImageManager _imageManager;

        // Cap concurrent jobs, not generators-within-a-job: each job already
        // fans out internally, and each generator has its own semaphore.
        private readonly SemaphoreSlim _jobLimit = new(4);

        public UiJobRunner(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            _settings = settings;
            _stats = stats;
            _options = options;
            _imageManager = new ImageManager(settings, stats);
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

        public bool IsAvailable(string key) => key switch
        {
            KeyGpt2 => !string.IsNullOrWhiteSpace(_settings.OpenAIApiKey),
            KeyGrokWeb => ResolveGrokWebCookiePath() != null,
            KeyGrokApi or KeyGrokApiPro => !string.IsNullOrWhiteSpace(_settings.XAIGrokApiKey),
            _ => false,
        };

        public async Task RunJobAsync(UiJob job, UiJobSpec spec)
        {
            await _jobLimit.WaitAsync();
            try
            {
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
                        job.StoreImage("grid", 0, bytes, "image/png");
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
            job.Emit(new { type = "gen-start", gen = key });
            GrokWebClient? grokWebClient = null;
            PromptDetails? copy = null;
            try
            {
                IImageGenerator generator;
                if (key == KeyGrokWeb)
                {
                    var cookiePath = ResolveGrokWebCookiePath()
                        ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                    grokWebClient = GrokWebClient.FromCookieFile(cookiePath);
                    generator = await BuildGrokWebAsync(grokWebClient, job, spec);
                }
                else
                {
                    generator = BuildGenerator(key, spec, job);
                }

                copy = pd.Copy();
                Logger.Log($"[ui #{job.Id}]   -> {generator.GetGeneratorSpecPart()}");
                var result = await generator.ProcessPromptAsync(generator, copy);
                await _imageManager.ProcessAndSaveAsync(result, generator);

                var urls = new List<string>();
                int i = 0;
                foreach (var bytes in result.GetAllImages)
                {
                    job.StoreImage(key, i, bytes, result.ContentType ?? "image/png");
                    urls.Add($"/api/jobs/{job.Id}/images/{key}/{i}");
                    i++;
                }

                var elapsed = result.CreateTotalMs + result.DownloadTotalMs;
                var label = copy.RuntimeMeta.TryGetValue("label", out var l) && !string.IsNullOrEmpty(l)
                    ? l
                    : result.ImageGeneratorDescription;
                job.Emit(new
                {
                    type = "gen-result",
                    gen = key,
                    ok = result.IsSuccess && urls.Count > 0,
                    error = result.IsSuccess ? "" : (result.ErrorMessage ?? "unknown error"),
                    ms = elapsed,
                    images = urls,
                    label,
                });
                Logger.Log($"[ui #{job.Id}]   <- {(result.IsSuccess ? "OK" : $"FAIL ({result.ErrorMessage})")} from {key} in {elapsed} ms");
                return result;
            }
            catch (Exception ex)
            {
                // GrokWebException carries the server's response body — that's
                // where the actual reason lives ("post not found" etc).
                var detail = ex is GrokWebException gwe && !string.IsNullOrEmpty(gwe.ResponseBody)
                    ? $"{ex.Message} {Truncate(gwe.ResponseBody, 300)}"
                    : ex.Message;
                Logger.Log($"[ui #{job.Id}]   <- EXCEPTION from {key}: {detail}");
                job.Emit(new
                {
                    type = "gen-result",
                    gen = key,
                    ok = false,
                    error = detail,
                    ms = 0L,
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
                // grok-web edit has no AR knob; output follows the source image.
                return await GrokWebImagineEditGenerator.CreateAsync(
                    client, job.InputImagePath, maxConcurrency: 1, _stats,
                    enableSideBySide: _options.GrokWebSideBySide);
            }
            var mapped = UiShapeMapping.GrokAspect(spec.Shape);
            var ar = mapped == "" ? _options.GrokWebAspectRatio : mapped;
            return new GrokWebImagineGenerator(
                client, maxConcurrency: 1, _stats,
                pro: _options.GrokWebPro,
                aspectRatio: ar,
                enableSideBySide: _options.GrokWebSideBySide,
                settings: _settings,
                captureSessions: false);
        }

        private IImageGenerator BuildGenerator(string key, UiJobSpec spec, UiJob job)
        {
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
                        aspectRatio: mappedAr == "" ? "1:1" : mappedAr,
                        quality: "high",
                        resolution: UiShapeMapping.GrokResolution(spec.Detail),
                        settings: _settings);
                }

                default:
                    throw new ArgumentException($"unknown generator '{key}'");
            }
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
