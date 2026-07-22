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
    ///   GET  /api/jobs/{id}/events            SSE stream (replays from the start, so refresh-safe)
    ///   GET  /api/jobs/{id}/images/{gen}/{n}  cached or persisted result bytes
    ///   GET  /api/logs/events                  SSE stream of current-process log lines
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
                    new { key = UiJobRunner.KeyGpt2, label = "gpt-image-2", detail = "OpenAI. /edits when an image is attached, /generations otherwise." },
                    new { key = UiJobRunner.KeyGrokWeb, label = "grok-web pro", detail = "grok.com cookie session (WebSocket). Its consumer transport has no working prompt-aware auto ratio: auto omits the field and currently yields the native 2:3 default, so choose an explicit shape when ratio matters. Edits with auto inherit the source shape. Side-by-side mode requests up to 4 images. Each result can launch a grok-web image-to-video follow-up." },
                    new { key = UiJobRunner.KeyGrokApi, label = "grok-api", detail = "api.x.ai standard tier. Shape, detail (1k/2k), and n honored." },
                    new { key = UiJobRunner.KeyGrokApiPro, label = "grok-api pro", detail = "api.x.ai pro tier. Shape, detail (1k/2k), and n honored." },
                    new { key = UiJobRunner.KeyIdeogram, label = "Ideogram V4", detail = "Ideogram 4.0, 2K-native (detail tier has no effect). Shape maps to a native resolution. n: the v4 endpoint currently ignores num_images (returns 1); a pasted image routes to V3 as a style reference/guide, where shape and n (up to 8) work." },
                    new { key = UiJobRunner.KeyRecraft, label = "Recraft V4.1", detail = "Recraft V4.1. Shape maps to a native aspect ratio (auto lets Recraft pick from the prompt); n up to 6. A pasted image runs image-to-image (output follows the source image)." },
                    new { key = UiJobRunner.KeyBfl, label = "FLUX.2 Pro Preview", detail = "Black Forest Labs FLUX.2 Pro preview. Shape + detail map to an explicit WxH (~1 MP standard, ~4 MP high/max). A pasted image is used as a reference/guide (input_image conditioning). No n support." },
                    new { key = UiJobRunner.KeyGoogle, label = "Nano Banana 2", detail = "Google gemini-3.1-flash-image. Shape maps to a native aspect ratio; detail to 1K/2K/4K. A pasted image is used as a reference/guide. No n support." },
                    new { key = UiJobRunner.KeyGooglePro, label = "Nano Banana Pro", detail = "Google gemini-3-pro-image. Shape maps to a native aspect ratio; detail to 1K/2K/4K. A pasted image is used as a reference/guide. No n support." },
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
                    // Default-on set = the core use case: gpt-image-2 + grok-web pro.
                    defaultOn = g.key is UiJobRunner.KeyGpt2 or UiJobRunner.KeyGrokWeb,
                })
                // Stable sort: available targets keep the intent order above,
                // unavailable ones trail in the same relative order.
                .OrderBy(g => g.available ? 0 : 1);

                // Intent-level geometry: the browser picks a shape + detail
                // tier; the server maps them onto each generator's real knobs
                // (gpt-image-2 WxH, grok AR + 1k/2k). No freetext sizes.
                var shapes = new[]
                {
                    new { key = "auto", label = "auto (grok-web defaults 2:3)" },
                    new { key = "square", label = "square 1:1" },
                    new { key = "landscape", label = "landscape 3:2" },
                    new { key = "portrait", label = "portrait 2:3" },
                    new { key = "wide", label = "wide 16:9" },
                    new { key = "tall", label = "tall 9:16" },
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
                    defaults = new { shape = "auto", detail = "standard", quality = "high", moderation = "low", n = 1 },
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

                // Uploaded image (clipboard paste / drag-drop / file picker) is
                // persisted under the day folder so job inputs are archived
                // alongside outputs, then fed to edit generators by path.
                var inputImagePath = "";
                byte[]? inputImageBytes = null;
                string inputImageContentType = "image/png";
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
                    (inputImagePath, inputImageBytes, inputImageContentType) = await SaveInputImageAsync(file, settings);
                }

                var job = new UiJob { Prompt = prompt, InputImagePath = inputImagePath, GeneratorKeys = genKeys };
                var spec = new UiJobSpec
                {
                    GeneratorKeys = genKeys,
                    Quality = (form["quality"].ToString() ?? "high").Trim().ToLowerInvariant(),
                    Moderation = (form["moderation"].ToString() ?? "low").Trim().ToLowerInvariant(),
                    ImageCount = n,
                    Shape = (form["shape"].ToString() ?? "auto").Trim().ToLowerInvariant(),
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
                var inputImagePath = await SaveInputImageBytesAsync(sourceBytes, sourceContentType, settings);
                var job = new UiJob
                {
                    Prompt = prompt,
                    InputImagePath = inputImagePath,
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

            app.MapGet("/api/jobs/{id}/events", async (string id, HttpContext ctx) =>
            {
                var job = jobs.Get(id);
                if (job == null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";

                var index = 0;
                var ct = ctx.RequestAborted;
                while (!ct.IsCancellationRequested)
                {
                    var (batch, done) = job.ReadFrom(index);
                    foreach (var evt in batch)
                    {
                        await ctx.Response.WriteAsync($"data: {evt}\n\n", ct);
                    }
                    if (batch.Count > 0)
                    {
                        index += batch.Count;
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                    if (done)
                    {
                        // One final read in case events landed between the
                        // snapshot and the done flag (Emit happens before
                        // MarkDone, so this drains everything).
                        var (tail, _) = job.ReadFrom(index);
                        foreach (var evt in tail)
                        {
                            await ctx.Response.WriteAsync($"data: {evt}\n\n", ct);
                        }
                        await ctx.Response.Body.FlushAsync(ct);
                        break;
                    }
                    try { await Task.Delay(250, ct); }
                    catch (OperationCanceledException) { break; }
                }
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

            app.MapGet("/api/logs/events", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers.Connection = "keep-alive";

                var afterText = ctx.Request.Query["after"].ToString();
                if (string.IsNullOrWhiteSpace(afterText))
                {
                    afterText = ctx.Request.Headers["Last-Event-ID"].ToString();
                }
                _ = long.TryParse(afterText, out var afterSequence);

                var ct = ctx.RequestAborted;
                while (!ct.IsCancellationRequested)
                {
                    var batch = Logger.ReadBuffered(afterSequence);
                    foreach (var entry in batch)
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            sequence = entry.Sequence,
                            line = entry.Line,
                        });
                        await ctx.Response.WriteAsync($"id: {entry.Sequence}\ndata: {json}\n\n", ct);
                        afterSequence = entry.Sequence;
                    }
                    if (batch.Count > 0)
                    {
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                    try { await Task.Delay(250, ct); }
                    catch (OperationCanceledException) { break; }
                }
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

        private static async Task<(string Path, byte[] Bytes, string ContentType)> SaveInputImageAsync(IFormFile file, Settings settings)
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

            var path = Path.Combine(folder, $"{DateTime.Now:HHmmss_fff}_input{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Log($"UI input image saved: {path}");
            return (path, bytes, contentType);
        }

        private static async Task<string> SaveInputImageBytesAsync(
            byte[] bytes,
            string contentType,
            Settings settings)
        {
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
            Logger.Log($"UI video source image saved: {path}");
            return path;
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
