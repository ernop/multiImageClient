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
        public const string KeyGrokApi = "grok-api";
        public const string KeyGrokApiPro = "grok-api-pro";
        public const string KeyMetaWeb = "meta-web";

        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly RunOptions _options;
        private readonly ImageManager _imageManager;
        private readonly GeneratorGroups _generatorGroups;
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
                if (key == KeyGrokWeb)
                {
                    var cookiePath = ResolveGrokWebCookiePath()
                        ?? throw new InvalidOperationException("grok-web cookie file not found (settings.json GrokWebCookiePath or --grok-web-cookies)");
                    grokWebClient = GrokWebClient.FromCookieFile(cookiePath);
                    generator = await BuildGrokWebAsync(grokWebClient, job, spec);
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
                Logger.Log($"[ui #{job.Id}]   -> {generator.GetGeneratorSpecPart()}");
                var result = await generator.ProcessPromptAsync(generator, copy);
                await _imageManager.ProcessAndSaveAsync(result, generator);

                var urls = new List<string>();
                byte[] firstImageBytes = null;
                int i = 0;
                foreach (var bytes in result.GetAllImages)
                {
                    firstImageBytes ??= bytes;
                    job.StoreImage(key, i, bytes, result.ContentType ?? "image/png");
                    urls.Add($"/api/jobs/{job.Id}/images/{key}/{i}");
                    i++;
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
                            var partialKey = $"{key}-partial";
                            job.StoreImage(partialKey, outputIndex, bytes, "image/png");
                            job.Emit(new
                            {
                                type = "gen-partial",
                                gen = key,
                                partialIndex,
                                imageIndex = outputIndex,
                                url = $"/api/jobs/{job.Id}/images/{partialKey}/{outputIndex}",
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
                        name: $"{key} ui");
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

                case KeyGoogle:
                case KeyGooglePro:
                {
                    if (!job.HasInputImage)
                    {
                        return _generatorGroups.BuildByShortName(key);
                    }
                    // Reference/guide: Gemini is natively multimodal, so the pasted
                    // image rides along as a reference part. Mirror the text preset
                    // (1:1 2K) used by GeneratorGroups.GeminiNanoBanana[Pro].
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", key);
                    var googleApiType = key == KeyGooglePro
                        ? ImageGeneratorApiType.GoogleNanoBananaPro
                        : ImageGeneratorApiType.GoogleNanoBanana;
                    return new GoogleGenerator(
                        googleApiType, _settings.GoogleGeminiApiKey, maxConcurrency: 2,
                        _stats, name: $"{key} ui",
                        aspectRatio: "1:1", imageSize: "2K",
                        inputImagePath: job.InputImagePath);
                }

                case KeyBfl:
                {
                    if (!job.HasInputImage)
                    {
                        return _generatorGroups.BuildByShortName(key);
                    }
                    // Reference/guide: FLUX.2 takes the pasted image as input_image
                    // conditioning. Mirror the pro-preview 1:1 1024 preset.
                    RequireKey(_settings.BFLApiKey, "BFLApiKey", key);
                    return new BFLGenerator(
                        ImageGeneratorApiType.BFLFlux2ProPreview, _settings.BFLApiKey,
                        maxConcurrency: 2, "1:1", false, 1024, 1024, _stats, "bfl ui",
                        inputImagePath: job.InputImagePath);
                }

                case KeyIdeogram:
                case KeyRecraft:
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
