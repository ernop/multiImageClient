#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    ///   GET  /api/jobs/{id}/events            SSE stream (replays from the start, so refresh-safe)
    ///   GET  /api/jobs/{id}/images/{gen}/{n}  result image bytes from memory
    public class UiWorkflow
    {
        public async Task RunAsync(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            var wwwroot = ResolveWwwRoot();
            if (wwwroot == null)
            {
                Console.Error.WriteLine("UI aborted: could not locate Ui/wwwroot (looked relative to CWD and the exe).");
                return;
            }

            var jobs = new UiJobRegistry();
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
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(wwwroot) });

            app.MapGet("/api/config", () =>
            {
                var generators = new[]
                {
                    new { key = UiJobRunner.KeyGpt2, label = "gpt-image-2", detail = "OpenAI. /edits when an image is attached, /generations otherwise." },
                    new { key = UiJobRunner.KeyGpt1, label = "gpt-image-1", detail = "OpenAI image generation. Text-to-image in this UI." },
                    new { key = UiJobRunner.KeyGpt1Mini, label = "gpt-image-1-mini", detail = "OpenAI lower-cost image generation. Text-to-image in this UI." },
                    new { key = UiJobRunner.KeyIdeogram, label = "Ideogram V4", detail = "Ideogram 4.0 DEFAULT 2048x2048 preset. A pasted image routes to V3 and is used as a style reference/guide." },
                    new { key = UiJobRunner.KeyRecraft, label = "Recraft V4.1", detail = "Recraft V4.1 any-style 1024x1024 preset. A pasted image becomes a custom style (reference/guide)." },
                    new { key = UiJobRunner.KeyBfl, label = "FLUX.2 Pro Preview", detail = "Black Forest Labs current FLUX.2 Pro preview preset. A pasted image is used as a reference/guide (input_image conditioning)." },
                    new { key = UiJobRunner.KeyGoogle, label = "Nano Banana 2", detail = "Google gemini-3.1-flash-image, 2K square preset. A pasted image is used as a reference/guide." },
                    new { key = UiJobRunner.KeyGooglePro, label = "Nano Banana Pro", detail = "Google gemini-3-pro-image, 2K square preset. A pasted image is used as a reference/guide." },
                    new { key = UiJobRunner.KeyGrokWeb, label = "grok-web pro", detail = "grok.com cookie session (WebSocket). NOTE: edit-with-image is currently blocked by grok.com anti-bot rules (403 on /rest/app-chat as of 2026-07-12); text-to-image works." },
                    new { key = UiJobRunner.KeyGrokApi, label = "grok-api", detail = "api.x.ai standard tier." },
                    new { key = UiJobRunner.KeyGrokApiPro, label = "grok-api pro", detail = "api.x.ai pro tier, 2k." },
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
                    // Default-on set = the core use case: gpt-image-2 + grok-web pro.
                    defaultOn = g.key is UiJobRunner.KeyGpt2 or UiJobRunner.KeyGrokWeb,
                });

                // Intent-level geometry: the browser picks a shape + detail
                // tier; the server maps them onto each generator's real knobs
                // (gpt-image-2 WxH, grok AR + 1k/2k). No freetext sizes.
                var shapes = new[]
                {
                    new { key = "auto", label = "auto" },
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
                    var imageCapable = new[]
                    {
                        UiJobRunner.KeyGpt2,
                        UiJobRunner.KeyGrokApi,
                        UiJobRunner.KeyGrokApiPro,
                        UiJobRunner.KeyGoogle,
                        UiJobRunner.KeyGooglePro,
                        UiJobRunner.KeyBfl,
                        UiJobRunner.KeyIdeogram,
                        UiJobRunner.KeyRecraft,
                    };
                    var incompatible = genKeys
                        .Where(key => !imageCapable.Contains(key, StringComparer.OrdinalIgnoreCase))
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
                if (inputImageBytes != null)
                {
                    // Keep the input in the job's image store so reloaded pages
                    // can show the thumbnail without touching the saves/ layout.
                    job.StoreImage("input", 0, inputImageBytes, inputImageContentType);
                }
                jobs.Add(job);
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

            app.MapGet("/api/jobs/{id}/images/{gen}/{n:int}", (string id, string gen, int n) =>
            {
                var job = jobs.Get(id);
                if (job == null) return Results.NotFound();
                if (!job.TryGetImage(gen, n, out var bytes, out var contentType)) return Results.NotFound();
                return Results.File(bytes, contentType);
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
