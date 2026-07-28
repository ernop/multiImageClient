#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace MultiImageClient
{
    /// The local web UI (--ui): Kestrel bound to 127.0.0.1 serving a no-build
    /// static frontend (Ui/wwwroot) plus a small job API. The browser is a
    /// control panel + image viewer over the exact same generator +
    /// ImageManager pipeline the console workflows use.
    ///
    /// API surface:
    ///   GET  /api/config                      generator availability + defaults
    ///   POST /api/jobs                        multipart: prompt, generators, options, image? -> {id}
    ///   POST /api/video-jobs                  grok-web image result -> video job
    ///   GET  /api/events/poll?cursor=N        cursor-based poll over every job's envelope log
    ///                                          (cursor=0 replays the full history, so refresh-safe;
    ///                                          polling instead of SSE because the browser's
    ///                                          ~6-connection HTTP/1.1 pool is shared across ALL
    ///                                          tabs and must stay free for image loads)
    ///   GET  /api/jobs/{id}/images/{gen}/{n}  cached or persisted result bytes
    ///   GET  /api/logs/poll?after=N            current-process log lines after sequence N
    public class UiWorkflow
    {
        // Single source of truth for which UI targets accept an input image.
        // Exposed to the frontend via /api/config (per-generator imageCapable
        // flag) and enforced server-side in POST /api/jobs, so the two can't
        // drift. grok-web edits ride the imagine WebSocket (image_uri).
        private static readonly string[] ImageCapableKeys =
        {
            UiJobRunner.KeyGpt2,
            UiJobRunner.KeyGrokWeb,
            UiJobRunner.KeyGrokApi,
            UiJobRunner.KeyGrokApiPro,
            UiJobRunner.KeyGoogle,
            UiJobRunner.KeyGooglePro,
            UiJobRunner.KeyBfl,
            UiJobRunner.KeyIdeogram,
            UiJobRunner.KeyRecraft,
        };

        private static bool SupportsImageAspectOverride(string key)
            => ImageCapableKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                && !key.Equals(UiJobRunner.KeyRecraft, StringComparison.OrdinalIgnoreCase);

        public async Task RunAsync(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            var wwwroot = ResolveWwwRoot();
            if (wwwroot == null)
            {
                Console.Error.WriteLine("UI aborted: could not locate Ui/wwwroot (looked relative to CWD and the exe).");
                return;
            }

            var jobs = new UiJobRegistry(settings);
            var activeJobs = new ConcurrentDictionary<string, Task>();
            await using var runner = new UiJobRunner(settings, stats, options);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var url = $"http://127.0.0.1:{options.UiPort}";
            builder.WebHost.UseUrls(url);

            var app = builder.Build();

            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(wwwroot) });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                OnPrepareResponse = context =>
                {
                    // The UI deliberately serves source-tree assets for live
                    // editing. Never let index.html and app.js get out of sync.
                    context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    context.Context.Response.Headers.Pragma = "no-cache";
                    context.Context.Response.Headers.Expires = "0";
                },
            });

            app.MapGet("/api/config", () =>
            {
                // Display order: gpt-image-2, grok-*, ideogram, recraft, then the
                // rest; unavailable targets (missing keys, gated local models)
                // sink to the end via the stable OrderBy below.
                var generators = new[]
                {
                    new { key = UiJobRunner.KeyGpt2, label = "gpt-image-2", detail = "OpenAI. /edits when an image is attached, /generations otherwise. The default output AR matches an attached source; explicit AR choices override it." },
                    new { key = UiJobRunner.KeyGrokWeb, label = "grok-web pro", detail = "grok.com cookie session (WebSocket). Without an input, auto omits the ratio and currently yields Grok's native 2:3 default. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit AR choices override it. Side-by-side mode requests up to 4 images. Each result can launch a grok-web image-to-video follow-up." },
                    new { key = UiJobRunner.KeyGrokApi, label = "grok-api", detail = "api.x.ai standard tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyGrokApiPro, label = "grok-api pro", detail = "api.x.ai pro tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyIdeogram, label = "Ideogram V4", detail = "Ideogram 4.0, 2K-native (detail tier has no effect). A pasted image routes to V3 as a style reference and defaults to the nearest supported AR; explicit AR choices and n up to 8 work. The v4 text endpoint currently ignores num_images and returns 1." },
                    new { key = UiJobRunner.KeyRecraft, label = "Recraft V4.1", detail = "Recraft V4.1. A pasted image runs image-to-image and inherently keeps the source dimensions. That endpoint exposes no size override, so Recraft is unavailable for image jobs with an explicit output AR. n up to 6." },
                    new { key = UiJobRunner.KeyBfl, label = "FLUX.2 Pro Preview", detail = "Black Forest Labs FLUX.2 Pro preview. With an input, the default derives an explicit source-matching WxH; explicit shape + detail map to ~1 MP standard or ~4 MP high/max. No n support." },
                    new { key = UiJobRunner.KeyGoogle, label = "Nano Banana 2", detail = "Google gemini-3.1-flash-image. With an input, the default uses the nearest Gemini-supported AR; explicit shape overrides it and detail maps to 1K/2K/4K. No n support." },
                    new { key = UiJobRunner.KeyGooglePro, label = "Nano Banana Pro", detail = "Google gemini-3-pro-image. With an input, the default uses the nearest Gemini-supported AR; explicit shape overrides it and detail maps to 1K/2K/4K. No n support." },
                    new { key = UiJobRunner.KeyGpt1, label = "gpt-image-1", detail = "OpenAI image generation. Shape, quality, moderation, and n honored. Text-to-image in this UI." },
                    new { key = UiJobRunner.KeyGpt1Mini, label = "gpt-image-1-mini", detail = "OpenAI lower-cost image generation. Shape, quality, moderation, and n honored. Text-to-image in this UI." },
                    new { key = UiJobRunner.KeyMetaWeb, label = "meta-web Muse Image", detail = "Meta AI browser session through Playwright. Text-to-image only; experimental and best-effort." },
                    new { key = UiJobRunner.KeyLocalKlein, label = "local FLUX.2 Klein", detail = "Configured local ComfyUI FLUX.2 Klein workflow." },
                    new { key = UiJobRunner.KeyLocalZImage, label = "local Z-Image Turbo", detail = "Configured local ComfyUI Z-Image workflow." },
                }
                .Select(g => new
                {
                    g.key,
                    g.label,
                    g.detail,
                    available = runner.IsAvailable(g.key),
                    availabilityProblem = runner.DescribeAvailabilityProblem(g.key),
                    imageCapable = ImageCapableKeys.Contains(g.key, StringComparer.OrdinalIgnoreCase),
                    imageAspectOverride = SupportsImageAspectOverride(g.key),
                    // Default-on set for new windows: gpt-image-2, Recraft V4.1,
                    // grok-web pro, Ideogram V4, Nano Banana 2.
                    defaultOn = g.key is UiJobRunner.KeyGpt2
                        or UiJobRunner.KeyRecraft
                        or UiJobRunner.KeyGrokWeb
                        or UiJobRunner.KeyIdeogram
                        or UiJobRunner.KeyGoogle,
                })
                // Stable sort: available targets keep the intent order above,
                // unavailable ones trail in the same relative order.
                .OrderBy(g => g.available ? 0 : 1);

                // Intent-level geometry: auto lets text-to-image models decide,
                // but means match input whenever an image is attached. Explicit
                // choices always map onto each generator's real knobs.
                var shapes = new[]
                {
                    new { key = "auto", label = "auto (no input)", inputLabel = "match input image" },
                    new { key = "square", label = "square 1:1", inputLabel = "square 1:1" },
                    new { key = "landscape", label = "landscape 3:2", inputLabel = "landscape 3:2" },
                    new { key = "portrait", label = "portrait 2:3", inputLabel = "portrait 2:3" },
                    new { key = "wide", label = "wide 16:9", inputLabel = "wide 16:9" },
                    new { key = "tall", label = "tall 9:16", inputLabel = "tall 9:16" },
                };
                var details = new[]
                {
                    new { key = "standard", label = "standard \u2248 1K" },
                    new { key = "high", label = "high \u2248 2K" },
                    new { key = "max", label = "max \u2248 4K" },
                };

                return Results.Json(new
                {
                    generators,
                    shapes,
                    details,
                    videoGeneration = new
                    {
                        available = runner.IsAvailable(UiJobRunner.KeyGrokWebVideo),
                        availabilityProblem = runner.DescribeAvailabilityProblem(UiJobRunner.KeyGrokWebVideo),
                    },
                    defaults = new { shape = "auto", detail = "high", quality = "high", moderation = "low", n = 1 },
                });
            });

            // Chronological job summaries so a (re)loaded page can hydrate
            // itself: render every job's card, then let the replayable SSE
            // stream fill in the results.
            app.MapGet("/api/jobs", () =>
            {
                var list = jobs.ListChronological().Select(j => new
                {
                    id = j.Id,
                    prompt = j.Prompt,
                    gens = j.GeneratorKeys,
                    hasImage = j.HasInputImage,
                    done = j.IsDone,
                    sourceJobId = j.SourceJobId,
                    sourceGenerator = j.SourceGenerator,
                    sourceIndex = j.SourceIndex,
                    createdAt = j.CreatedAt.ToString("HH:mm:ss"),
                    createdAtUnixMs = new DateTimeOffset(j.CreatedAt).ToUnixTimeMilliseconds(),
                });
                return Results.Json(new { jobs = list });
            });

            app.MapPost("/api/jobs", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();

                var prompt = (form["prompt"].ToString() ?? "").Trim();
                if (prompt.Length == 0)
                {
                    return Results.BadRequest(new { error = "prompt is required" });
                }

                var genKeys = (form["generators"].ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (genKeys.Count == 0)
                {
                    return Results.BadRequest(new { error = "pick at least one generator" });
                }
                var unavailable = genKeys.Where(k => !runner.IsAvailable(k)).ToList();
                if (unavailable.Count > 0)
                {
                    var reasons = unavailable.Select(k => $"{k}: {runner.DescribeAvailabilityProblem(k)}");
                    return Results.BadRequest(new { error = $"not available: {string.Join("; ", reasons)}" });
                }

                var n = 1;
                if (int.TryParse(form["n"].ToString(), out var parsedN) && parsedN >= 1 && parsedN <= 10)
                {
                    n = parsedN;
                }
                var shapeValue = form["shape"].ToString();
                var shape = string.IsNullOrWhiteSpace(shapeValue)
                    ? "auto"
                    : shapeValue.Trim().ToLowerInvariant();
                if (!UiShapeMapping.IsKnownShape(shape))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown output shape '{shape}'. Expected one of: {string.Join(", ", UiShapeMapping.Shapes)}.",
                    });
                }

                // Uploaded image (clipboard paste / drag-drop / file picker) is
                // persisted under the day folder so job inputs are archived
                // alongside outputs, then fed to edit generators by path.
                var inputImagePath = "";
                byte[]? inputImageBytes = null;
                string inputImageContentType = "image/png";
                var inputImageWidth = 0;
                var inputImageHeight = 0;
                var file = form.Files.GetFile("image");
                if (file != null && file.Length > 0)
                {
                    var incompatible = genKeys
                        .Where(key => !ImageCapableKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (incompatible.Count > 0)
                    {
                        return Results.BadRequest(new
                        {
                            error = $"These generators are text-to-image only in the local UI: {string.Join(", ", incompatible)}. Remove the input image or deselect them.",
                        });
                    }
                    if (shape != "auto")
                    {
                        var aspectIncompatible = genKeys
                            .Where(key => !SupportsImageAspectOverride(key))
                            .ToList();
                        if (aspectIncompatible.Count > 0)
                        {
                            return Results.BadRequest(new
                            {
                                error = $"These generators cannot override aspect ratio when an input image is attached: {string.Join(", ", aspectIncompatible)}. Choose match input image or deselect them.",
                            });
                        }
                    }
                    try
                    {
                        (inputImagePath, inputImageBytes, inputImageContentType, inputImageWidth, inputImageHeight)
                            = await SaveInputImageAsync(file, settings);
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }

                var job = new UiJob
                {
                    Prompt = prompt,
                    InputImagePath = inputImagePath,
                    InputImageWidth = inputImageWidth,
                    InputImageHeight = inputImageHeight,
                    GeneratorKeys = genKeys,
                };
                var spec = new UiJobSpec
                {
                    GeneratorKeys = genKeys,
                    Quality = (form["quality"].ToString() ?? "high").Trim().ToLowerInvariant(),
                    Moderation = (form["moderation"].ToString() ?? "low").Trim().ToLowerInvariant(),
                    ImageCount = n,
                    Shape = shape,
                    Detail = (form["detail"].ToString() ?? "standard").Trim().ToLowerInvariant(),
                };
                jobs.Add(job);
                if (inputImageBytes != null)
                {
                    // Keep the input in the job's image store so reloaded pages
                    // can show the thumbnail without touching the saves/ layout.
                    job.StoreImage("input", 0, inputImageBytes, inputImageContentType, inputImagePath);
                }
                job.Emit(new
                {
                    type = "accepted",
                    gens = genKeys,
                    hasImage = job.HasInputImage,
                    inputWidth = job.HasInputImage ? job.InputImageWidth : (int?)null,
                    inputHeight = job.HasInputImage ? job.InputImageHeight : (int?)null,
                    shape,
                    prompt,
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                activeJobs[job.Id] = Task.Run(async () =>
                {
                    try
                    {
                        await runner.RunJobAsync(job, spec);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] job runner failed: {ex}");
                    }
                    finally
                    {
                        activeJobs.TryRemove(job.Id, out _);
                    }
                });

                return Results.Json(new { id = job.Id });
            });

            app.MapPost("/api/video-jobs", async (HttpRequest request) =>
            {
                var availabilityProblem = runner.DescribeAvailabilityProblem(UiJobRunner.KeyGrokWebVideo);
                if (availabilityProblem != null)
                {
                    return Results.BadRequest(new { error = availabilityProblem });
                }

                var form = await request.ReadFormAsync();
                var sourceJobId = (form["sourceJobId"].ToString() ?? "").Trim();
                var sourceGenerator = (form["sourceGenerator"].ToString() ?? "").Trim();
                if (!int.TryParse(form["sourceIndex"].ToString(), out var sourceIndex) || sourceIndex < 0)
                {
                    return Results.BadRequest(new { error = "sourceIndex must be a non-negative integer." });
                }

                var sourceJob = jobs.Get(sourceJobId);
                if (sourceJob == null)
                {
                    return Results.NotFound(new { error = "The source job is no longer available." });
                }
                if (!sourceJob.TryGetImage(sourceGenerator, sourceIndex, out var sourceBytes, out var sourceContentType))
                {
                    return Results.NotFound(new { error = "The selected source image is no longer available." });
                }
                if (!sourceContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "The selected source result is not an image." });
                }

                string videoMode;
                try
                {
                    videoMode = GrokWebClient.NormalizeVideoMode(form["mode"].ToString());
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                var durationSeconds = 10;
                if (int.TryParse(form["duration"].ToString(), out var parsedDuration))
                {
                    durationSeconds = parsedDuration;
                }
                if (durationSeconds < 1 || durationSeconds > 15)
                {
                    return Results.BadRequest(new { error = "Video duration must be between 1 and 15 seconds." });
                }

                var resolution = (form["resolution"].ToString() ?? "480p").Trim().ToLowerInvariant();
                if (resolution is not ("480p" or "720p"))
                {
                    return Results.BadRequest(new { error = "Video resolution must be 480p or 720p." });
                }

                var aspectRatio = (form["aspectRatio"].ToString() ?? "source").Trim().ToLowerInvariant();
                if (aspectRatio is not ("source" or "1:1" or "3:2" or "2:3" or "16:9" or "9:16"))
                {
                    return Results.BadRequest(new
                    {
                        error = "Video aspect ratio must be source, 1:1, 3:2, 2:3, 16:9, or 9:16.",
                    });
                }

                var prompt = (form["prompt"].ToString() ?? "").Trim();
                string inputImagePath;
                int inputImageWidth;
                int inputImageHeight;
                try
                {
                    (inputImagePath, inputImageWidth, inputImageHeight)
                        = await SaveInputImageBytesAsync(sourceBytes, sourceContentType, settings);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                var job = new UiJob
                {
                    Prompt = prompt,
                    InputImagePath = inputImagePath,
                    InputImageWidth = inputImageWidth,
                    InputImageHeight = inputImageHeight,
                    GeneratorKeys = new[] { UiJobRunner.KeyGrokWebVideo },
                    SourceJobId = sourceJobId,
                    SourceGenerator = sourceGenerator,
                    SourceIndex = sourceIndex,
                };
                var spec = new UiJobSpec
                {
                    GeneratorKeys = new List<string> { UiJobRunner.KeyGrokWebVideo },
                    VideoMode = videoMode,
                    VideoDurationSeconds = durationSeconds,
                    VideoResolution = resolution,
                    VideoAspectRatio = aspectRatio,
                };

                jobs.Add(job);
                job.StoreImage("input", 0, sourceBytes, sourceContentType, inputImagePath);
                job.Emit(new
                {
                    type = "accepted",
                    gens = job.GeneratorKeys,
                    hasImage = true,
                    prompt,
                    sourceJobId,
                    sourceGenerator,
                    sourceIndex,
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                Logger.Log(
                    $"[ui #{job.Id}] video source={sourceJobId}/{sourceGenerator}/{sourceIndex} "
                    + $"content-type={sourceContentType} bytes={sourceBytes.Length}");
                activeJobs[job.Id] = Task.Run(async () =>
                {
                    try
                    {
                        await runner.RunJobAsync(job, spec);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ui #{job.Id}] video job runner failed: {ex}");
                    }
                    finally
                    {
                        activeJobs.TryRemove(job.Id, out _);
                    }
                });

                return Results.Json(new { id = job.Id });
            });

            // Cursor-based poll for ALL job events. Deliberately NOT a
            // persistent stream: browsers cap plain-HTTP/1.1 at ~6 connections
            // per origin ACROSS ALL TABS (no HTTP/2 without TLS on localhost),
            // so any long-lived SSE/WebSocket per window re-starved every
            // <img> load once a few windows were open (observed 2026-07-27,
            // twice). Each poll answers immediately and releases its socket.
            // Envelopes are {jobId, kind:"job-known"|"event", job?|event?};
            // a job-known announcement precedes each job's events, so cursor=0
            // replays the full history and hydrates a fresh window. An
            // out-of-range cursor (server restart) resyncs from 0, which is
            // idempotent client-side.
            app.MapGet("/api/events/poll", (int? cursor, HttpContext ctx) =>
            {
                var (envelopes, nextCursor) = jobs.ReadEnvelopes(cursor ?? 0);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(
                    $"{{\"cursor\":{nextCursor},\"envelopes\":[{string.Join(",", envelopes)}]}}",
                    "application/json");
            });

            app.MapGet("/api/jobs/{id}/images/{gen}/{n:int}", (string id, string gen, int n, HttpContext ctx) =>
            {
                var job = jobs.Get(id);
                if (job == null) return Results.NotFound();
                if (!job.TryGetImage(gen, n, out var bytes, out var contentType)) return Results.NotFound();
                // A stable URL may advance from blurry GPT-Image-2 partials to
                // the final result. Force reloads to ask for the current bytes.
                ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                ctx.Response.Headers.Expires = "0";
                return Results.File(bytes, contentType);
            });

            // Same zero-persistent-connection rule as /api/events/poll: the
            // logs panel used to hold an SSE connection per window, which
            // counted against the same 6-connection browser pool.
            app.MapGet("/api/logs/poll", (long? after, HttpContext ctx) =>
            {
                var entries = Logger.ReadBuffered(after ?? 0)
                    .Select(entry => new { sequence = entry.Sequence, line = entry.Line });
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { entries });
            });

            Logger.Log($"UI server starting on {url}  (wwwroot: {wwwroot})");
            Console.WriteLine();
            Console.WriteLine($"  MultiImageClient UI:  {url}");
            Console.WriteLine("  Ctrl-C to stop.");
            Console.WriteLine();
            TryOpenBrowser(url);

            await app.RunAsync();

            var remaining = activeJobs.Values.ToArray();
            if (remaining.Length > 0)
            {
                Logger.Log($"UI shutdown: waiting up to 30 seconds for {remaining.Length} active job(s).");
                var allJobs = Task.WhenAll(remaining);
                if (await Task.WhenAny(allJobs, Task.Delay(TimeSpan.FromSeconds(30))) != allJobs)
                {
                    Logger.Log("UI shutdown: active jobs did not finish within the 30-second grace period.");
                }
            }
        }

        // Prefer the source tree copies (live-editable during dev: tweak
        // app.js, refresh the browser) over the build-output copy.
        private static string? ResolveWwwRoot()
        {
            var candidates = new[]
            {
                Path.Combine("MultiImageClient", "Ui", "wwwroot"),
                Path.Combine("Ui", "wwwroot"),
                Path.Combine(AppContext.BaseDirectory, "Ui", "wwwroot"),
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(Path.Combine(full, "index.html"))) return full;
            }
            return null;
        }

        private static async Task<(string Path, byte[] Bytes, string ContentType, int Width, int Height)> SaveInputImageAsync(
            IFormFile file,
            Settings settings)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            var folder = Path.Combine(settings.ImageDownloadBaseFolder, today, "UiInputs");
            Directory.CreateDirectory(folder);

            var contentType = (file.ContentType ?? "").ToLowerInvariant();
            var ext = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png",
            };
            if (ext == ".png") contentType = "image/png";

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var (width, height) = IdentifyImageDimensions(bytes);

            var path = Path.Combine(folder, $"{DateTime.Now:HHmmss_fff}_input{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Log($"UI input image saved: {path} ({width}x{height})");
            return (path, bytes, contentType, width, height);
        }

        private static async Task<(string Path, int Width, int Height)> SaveInputImageBytesAsync(
            byte[] bytes,
            string contentType,
            Settings settings)
        {
            var (width, height) = IdentifyImageDimensions(bytes);
            var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            var folder = Path.Combine(settings.ImageDownloadBaseFolder, today, "UiInputs");
            Directory.CreateDirectory(folder);
            var ext = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => ".png",
            };
            var path = Path.Combine(folder, $"{DateTime.Now:HHmmss_fff}_video_source{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Log($"UI video source image saved: {path} ({width}x{height})");
            return (path, width, height);
        }

        private static (int Width, int Height) IdentifyImageDimensions(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var info = Image.Identify(stream);
                if (info == null || info.Width <= 0 || info.Height <= 0)
                {
                    throw new InvalidDataException("Uploaded image has no readable dimensions.");
                }
                return (info.Width, info.Height);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Uploaded image could not be decoded: {ex.Message}", ex);
            }
        }

        // Launching the browser is the whole point of --ui, so this is not
        // gated by --open-images (which governs finished-image viewer pops).
        private static void TryOpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Log($"(couldn't auto-open browser: {ex.Message} — open {url} manually)");
            }
        }
    }
}
