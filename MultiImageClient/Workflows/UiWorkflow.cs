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
using Microsoft.AspNetCore.StaticFiles;
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
    ///   POST /api/jobs                        multipart: prompt, generators, options, images? (up to 4) -> {id}
    ///   POST /api/video-jobs                  grok-web image result -> video job
    ///   GET  /api/events/poll?cursor=N        cursor-based poll over every job's envelope log
    ///                                          (cursor=0 replays the full history, so refresh-safe;
    ///                                          polling instead of SSE because the browser's
    ///                                          ~6-connection HTTP/1.1 pool is shared across ALL
    ///                                          tabs and must stay free for image loads)
    ///   GET  /api/jobs/{id}/images/{gen}/{n}  cached or persisted result bytes; ?thumb=1 serves a
    ///                                          <=640px card preview; finished jobs get immutable
    ///                                          cache headers so refreshes don't re-download history
    ///   GET  /api/input-images                 distinct user-uploaded input images, newest first
    ///                                          (SHA-256 deduped), for the composer's load picker
    ///   GET  /api/logs/poll?after=N            current-process log lines after sequence N
    ///   POST /api/prompt/spellfix              Claude spelling-only correction -> {corrected}
    ///   GET  /api/archive/days                 archived (pre-today) days with job counts
    ///   GET  /api/archive/days/{day}           one archived day's jobs + full event history
    ///   GET  /api/users                        every creator name with job counts (filter bar)
    ///   POST /api/auth/login|logout            shared-site access gate (only when UiAuthFilePath is set)
    public class UiWorkflow
    {
        private static bool SupportsImageAspectOverride(string key)
            => UiJobRunner.IsImageCapable(key)
                && !UiJobRunner.IsRecraftKey(key);

        // Default anti-murk guidance appended to every gpt-image-2 prompt while
        // the composer's toggle is on (which it is by default). gpt-image-2
        // reliably drifts into dark cinematic murk — low luminosity, dusky
        // haze, over-fine smudged texture — unless told not to on EVERY call.
        // The composer textbox is prefilled with this text and fully editable;
        // whatever text the user submits is what gets appended.
        private const string DefaultGpt2GuidanceText =
            "Render in normal daytime lighting. Absolutely do not make the image dim, murky, grimy, "
            + "muddy, gloomy, shadow-choked, underexposed, hazy, dusk-like, night-like, or dark. "
            + "No smudged, muddy, overly fine micro-texture.";

        public async Task RunAsync(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            var wwwroot = ResolveWwwRoot();
            if (wwwroot == null)
            {
                Console.Error.WriteLine("UI aborted: could not locate Ui/wwwroot (looked relative to CWD and the exe).");
                return;
            }

            // Optional shared-site gate. A configured-but-broken auth file is
            // a hard startup error: a shared deployment must never come up
            // open because of a typo in its access-control file.
            UiAuth? auth;
            try
            {
                auth = UiAuth.CreateFromSettings(settings);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"UI aborted: {ex.Message}");
                return;
            }

            var jobs = new UiJobRegistry(settings);
            var activeJobs = new ConcurrentDictionary<string, Task>();
            await using var runner = new UiJobRunner(settings, stats, options);

            // Claude-backed spelling-only prompt correction ("fix spelling" in
            // the composer). Gated on AnthropicApiKey like every other lazy key.
            var spellfixProblem = ProviderKeyValidator.DescribeTextKeyProblem(
                nameof(settings.AnthropicApiKey), settings.AnthropicApiKey);
            var claudeService = spellfixProblem == null
                ? new ClaudeService(settings.AnthropicApiKey, maxConcurrency: 2, stats)
                : null;

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

            // ---- access gate (shared deployments only) ----
            // Runs before static files and every endpoint. Unauthenticated
            // API calls get 401 JSON; unauthenticated page loads get the
            // inline login page. Login and the content-free loopback liveness
            // probe are the only anonymous routes. When auth is off (blank
            // UiAuthFilePath) this middleware is not registered.
            if (auth != null)
            {
                app.Use(async (ctx, next) =>
                {
                    var path = ctx.Request.Path.Value ?? "/";
                    if ((path == "/api/auth/login" && HttpMethods.IsPost(ctx.Request.Method))
                        || (path == "/healthz" && HttpMethods.IsGet(ctx.Request.Method)))
                    {
                        await next();
                        return;
                    }
                    if (!auth.IsEnforced)
                    {
                        await next();
                        return;
                    }
                    if (auth.TryValidateCookie(ctx.Request.Cookies[UiAuth.CookieName], out var user))
                    {
                        ctx.Items["micUser"] = user;
                        await next();
                        return;
                    }
                    if (path.StartsWith("/api/", StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.Headers.CacheControl = "no-store";
                        await ctx.Response.WriteAsJsonAsync(new { error = "not logged in" });
                        return;
                    }
                    ctx.Response.StatusCode = 401;
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.Headers.CacheControl = "no-store";
                    await ctx.Response.WriteAsync(LoginPageHtml);
                });

                app.MapPost("/api/auth/login", async (HttpRequest request, HttpContext ctx) =>
                {
                    var form = await request.ReadFormAsync();
                    var username = form["username"].ToString().Trim();
                    var password = form["password"].ToString();
                    if (username.Length == 0 || password.Length == 0)
                    {
                        return Results.BadRequest(new { error = "username and password are required" });
                    }
                    if (!auth.TryLogin(username, password, ClientIpForThrottle(ctx), out var cookieValue, out var error))
                    {
                        Logger.Log($"UI auth: failed login for '{username}' from {ClientIpForThrottle(ctx)}.");
                        return Results.Json(new { error }, statusCode: 401);
                    }
                    ctx.Response.Cookies.Append(UiAuth.CookieName, cookieValue, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = IsEffectivelyHttps(ctx),
                        MaxAge = TimeSpan.FromDays(3650),
                        Path = "/",
                    });
                    Logger.Log($"UI auth: '{username}' logged in from {ClientIpForThrottle(ctx)}.");
                    return Results.Json(new { ok = true, username });
                });

                app.MapPost("/api/auth/logout", (HttpContext ctx) =>
                {
                    ctx.Response.Cookies.Delete(UiAuth.CookieName, new CookieOptions { Path = "/" });
                    return Results.Json(new { ok = true });
                });
            }

            // Intentionally content-free and authentication-independent. The
            // in-process guard calls this through a raw loopback socket to prove
            // that Kestrel, routing, and middleware can still complete work.
            app.MapGet("/healthz", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text("ok", "text/plain");
            });

            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(wwwroot) });
            // McPhee's Hunspell dictionary files use extensions the default
            // content-type map doesn't know; without these entries the static
            // middleware would 404 them.
            var contentTypes = new FileExtensionContentTypeProvider();
            contentTypes.Mappings[".aff"] = "text/plain";
            contentTypes.Mappings[".dic"] = "text/plain";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = context =>
                {
                    // The UI deliberately serves source-tree assets for live
                    // editing. Never let index.html and app.js get out of sync.
                    context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    context.Context.Response.Headers.Pragma = "no-cache";
                    context.Context.Response.Headers.Expires = "0";
                },
            });

            app.MapGet("/api/config", (HttpContext ctx) =>
            {
                // Display order: gpt-image-2, grok-*, ideogram, recraft, then the
                // rest; unavailable targets (missing keys, gated local models)
                // sink to the end via the stable OrderBy below.
                var generators = new[]
                {
                    new { key = UiJobRunner.KeyGpt2, label = "gpt-image-2", detail = "OpenAI. /edits when an image is attached, /generations otherwise. Accepts up to 4 ordered input images (other selected generators only receive the first). The default output AR matches the primary attached source; explicit AR choices override it." },
                    new { key = UiJobRunner.KeyGrokWeb, label = "grok-web pro", detail = "grok.com cookie session using the browser-free imagine WebSocket. Text-to-image only in this UI; attached images are not sent. Auto requests square 1:1 because this transport has no prompt-aware auto and Grok's own default is 2:3. Side-by-side mode requests up to 4 images." },
                    new { key = UiJobRunner.KeyGrokApi, label = "grok-api", detail = "api.x.ai standard tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyGrokApiPro, label = "grok-api pro", detail = "api.x.ai pro tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyKrea, label = "Krea 2 Medium", detail = "Krea's own foundation image model, not an aggregated third-party model. Best for expressive illustration and stable general use. An attached image is sent as a 0.6-strength style reference; auto matches its nearest native aspect ratio. The API currently accepts 1K only, so detail has no effect. n runs separate generations." },
                    new { key = UiJobRunner.KeyKreaTurbo, label = "Krea 2 Medium Turbo", detail = "Krea's fastest and least expensive Krea 2 variant. An attached image is sent as a 0.6-strength style reference. The API currently accepts 1K only; n runs separate generations." },
                    new { key = UiJobRunner.KeyKreaLarge, label = "Krea 2 Large", detail = "Krea's highest-fidelity Krea 2 variant, strongest for photorealism, raw texture, grain, and expressive styles. An attached image is sent as a 0.6-strength style reference. The API currently accepts 1K only; n runs separate generations." },
                    new { key = UiJobRunner.KeyIdeogram, label = "Ideogram V4", detail = "Ideogram 4.0 text-to-image, 2K-native (detail tier has no effect). The v4 endpoint takes no input image, so on image jobs it runs from the prompt alone. It also currently ignores num_images and returns 1." },
                    new { key = UiJobRunner.KeyIdeogramV3, label = "Ideogram V3", detail = "Ideogram 3.0. A pasted image is used as a style reference and the default AR matches the source; explicit AR choices and n up to 8 are honored. Without an image it runs text-to-image (auto = square)." },
                    new { key = UiJobRunner.KeyIdeogramV2, label = "Ideogram V2", detail = "Ideogram 2.0 through the legacy text-to-image endpoint. Shape and Magic Prompt are honored; detail and n have no effect. An attached image is not sent." },
                    new { key = UiJobRunner.KeyRecraft, label = "Recraft V4.1", detail = "Recraft V4.1. A pasted image runs image-to-image and inherently keeps the source dimensions. That endpoint exposes no size override, so Recraft is unavailable for image jobs with an explicit output AR. n up to 6." },
                    new { key = UiJobRunner.KeyRecraftV41Utility, label = "Recraft V4.1 Utility", detail = "Simpler, more predictable V4.1 raster model with flatter lighting and front-facing compositions. Standard 1MP class; n up to 6. Image-to-image follows source dimensions." },
                    new { key = UiJobRunner.KeyRecraftV41Pro, label = "Recraft V4.1 Pro", detail = "Higher-resolution 4MP-class V4.1 raster model for print-ready output. n up to 6. Image-to-image follows source dimensions." },
                    new { key = UiJobRunner.KeyRecraftV41Vector, label = "Recraft V4.1 Vector", detail = "Native vector generation for logos, typography, icons, and illustration. Raw results are preserved as SVG; cards and contact sheets use raster previews. n up to 6." },
                    new { key = UiJobRunner.KeyRecraftV3, label = "Recraft V3", detail = "Previous-generation raster model (Red Panda), retained for comparison and V3-era behavior. n up to 6. Image-to-image follows source dimensions." },
                    new { key = UiJobRunner.KeyRecraftV4, label = "Recraft V4", detail = "Previous-generation standard raster model, 1MP class. n up to 6. Image-to-image follows source dimensions." },
                    new { key = UiJobRunner.KeyRecraftV4Pro, label = "Recraft V4 Pro", detail = "Previous-generation Pro raster model, 4MP class. n up to 6. Image-to-image follows source dimensions." },
                    new { key = UiJobRunner.KeyBfl, label = "FLUX.2 Pro Preview", detail = "BFL's latest Pro improvements. Generation and up to 8-reference editing; this UI sends its primary input. Shape + detail map to ~1 MP or ~4 MP. No n support." },
                    new { key = UiJobRunner.KeyBflFlux2Pro, label = "FLUX.2 Pro (pinned)", detail = "Fixed FLUX.2 Pro snapshot for reproducible workflows. Same request contract as Pro Preview; generation and image editing." },
                    new { key = UiJobRunner.KeyBflFlux2Max, label = "FLUX.2 Max", detail = "BFL's highest-quality FLUX.2 model with strongest prompt following, editing consistency, and grounding search." },
                    new { key = UiJobRunner.KeyBflFlux2Flex, label = "FLUX.2 Flex", detail = "Typography and small-detail specialist with adjustable inference steps and guidance. This integration uses 40 steps and guidance 4.5." },
                    new { key = UiJobRunner.KeyBflFlux2Klein4b, label = "FLUX.2 Klein 4B", detail = "Fastest and least expensive hosted FLUX.2 model. Supports generation and up to 4 references; this UI sends its primary input." },
                    new { key = UiJobRunner.KeyBflFlux2Klein9bPreview, label = "FLUX.2 Klein 9B Preview", detail = "Latest Klein 9B improvements with KV-cached inference. Supports generation and image editing." },
                    new { key = UiJobRunner.KeyBflFlux2Klein9b, label = "FLUX.2 Klein 9B (pinned)", detail = "Fixed Klein 9B snapshot for reproducible fast generation and image editing." },
                    new { key = UiJobRunner.KeyBflKontextPro, label = "FLUX.1 Kontext Pro", detail = "Previous-generation generation/editing model. BFL recommends FLUX.2 Pro for new integrations." },
                    new { key = UiJobRunner.KeyBflKontextMax, label = "FLUX.1 Kontext Max", detail = "Previous-generation maximum-quality Kontext generation/editing model. BFL recommends FLUX.2 for new integrations." },
                    new { key = UiJobRunner.KeyBflFlux11Ultra, label = "FLUX1.1 Pro Ultra", detail = "Previous-generation up-to-4MP endpoint with optional image remix. Shape is honored; detail is model-controlled." },
                    new { key = UiJobRunner.KeyBflFlux11, label = "FLUX1.1 Pro", detail = "Previous-generation text-to-image endpoint with optional image remix. Output edges are capped at 1440." },
                    new { key = UiJobRunner.KeyBflFluxPro, label = "FLUX.1 Pro (compatibility)", detail = "BFL still lists /flux-pro in its available-endpoints guide, but it is absent from the current OpenAPI. Kept as an explicitly unverified compatibility target; text-to-image only in this UI." },
                    new { key = UiJobRunner.KeyBflFluxDev, label = "FLUX.1 Dev", detail = "Older hosted FLUX.1 Dev endpoint with optional image prompt and explicit dimensions up to 1440." },
                    new { key = UiJobRunner.KeyGoogle, label = "Nano Banana 2", detail = "Google gemini-3.1-flash-image. With an input, the default uses the nearest Gemini-supported AR; explicit shape overrides it and detail maps to 1K/2K/4K. No n support." },
                    new { key = UiJobRunner.KeyGooglePro, label = "Nano Banana Pro", detail = "Google gemini-3-pro-image. With an input, the default uses the nearest Gemini-supported AR; explicit shape overrides it and detail maps to 1K/2K/4K. No n support." },
                    new { key = UiJobRunner.KeyGpt1, label = "gpt-image-1", detail = "OpenAI image generation. Shape, quality, moderation, and n honored. Text-to-image in this UI." },
                    new { key = UiJobRunner.KeyGpt1Mini, label = "gpt-image-1-mini", detail = "OpenAI lower-cost image generation. Shape, quality, moderation, and n honored. Text-to-image in this UI." },
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
                    imageCapable = UiJobRunner.IsImageCapable(g.key),
                    imageAspectOverride = SupportsImageAspectOverride(g.key),
                    // Known hard prompt-length caps, surfaced so the composer can
                    // warn before submit; the server truncates over-limit prompts
                    // at the provider send stage (grok-web: GrokWebClient).
                    maxPromptChars = g.key == UiJobRunner.KeyGrokWeb ? (int?)GrokWebClient.MaxPromptChars : null,
                    // Default-on set for new windows: gpt-image-2, Recraft V4.1,
                    // grok-web pro, Ideogram V4, FLUX.2 Pro Preview, Nano Banana 2.
                    defaultOn = g.key is UiJobRunner.KeyGpt2
                        or UiJobRunner.KeyRecraft
                        or UiJobRunner.KeyGrokWeb
                        or UiJobRunner.KeyIdeogram
                        or UiJobRunner.KeyBfl
                        or UiJobRunner.KeyGoogle,
                })
                // Stable sort: available targets keep the intent order above,
                // unavailable ones trail in the same relative order.
                .OrderBy(g => g.available ? 0 : 1);

                // Intent-level geometry: auto lets text-to-image models decide,
                // but means match input whenever an image is attached. Explicit
                // choices always map onto each generator's real knobs. text and
                // ratio are separate so the frontend can right-align the numeric
                // ratio column in the picker; label stays the plain fallback.
                var shapes = new[]
                {
                    new { key = "auto", label = "auto (no input)", inputLabel = "match input image", text = "", ratio = "" },
                    new { key = "square", label = "square 1:1", inputLabel = "square 1:1", text = "square", ratio = "1:1" },
                    new { key = "landscape", label = "landscape 3:2", inputLabel = "landscape 3:2", text = "landscape", ratio = "3:2" },
                    new { key = "portrait", label = "portrait 2:3", inputLabel = "portrait 2:3", text = "portrait", ratio = "2:3" },
                    new { key = "wide", label = "wide 16:9", inputLabel = "wide 16:9", text = "wide", ratio = "16:9" },
                    new { key = "tall", label = "tall 9:16", inputLabel = "tall 9:16", text = "tall", ratio = "9:16" },
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
                    spellfix = new
                    {
                        available = spellfixProblem == null,
                        availabilityProblem = spellfixProblem,
                    },
                    // gpt-image-2 anti-murk guidance: on by default, textbox
                    // prefilled with this text, appended server-side to the
                    // gpt2 target's prompt only.
                    gpt2Guidance = new
                    {
                        defaultEnabled = true,
                        defaultText = DefaultGpt2GuidanceText,
                    },
                    defaults = new { shape = "auto", detail = "high", quality = "high", moderation = "low", n = 1 },
                    maxInputImages = UiJobRunner.MaxInputImages,
                    // Exact code identity of the running server (embedded at
                    // build time), so any window can trace what this instance
                    // does or does not contain yet.
                    build = new
                    {
                        commit = UiBuildInfo.Commit,
                        commitDate = UiBuildInfo.CommitDate,
                        commitUrl = UiBuildInfo.CommitUrl,
                    },
                    // Shared-site identity: when the access gate is on, the
                    // authenticated login name seeds the creator-name control.
                    auth = new
                    {
                        enabled = auth != null,
                        user = ctx.Items["micUser"] as string ?? "",
                    },
                });
            });

            // Live (hydrated) job summaries. Full multi-day history is indexed
            // on disk and served through /api/archive — this endpoint no longer
            // returns every historical job as in-process objects.
            app.MapGet("/api/jobs", () =>
            {
                var list = jobs.ListChronological().Select(j => new
                {
                    id = j.Id,
                    prompt = j.Prompt,
                    user = j.CreatedBy,
                    gens = j.GeneratorKeys,
                    hasImage = j.HasInputImage,
                    inputCount = j.InputImageCount,
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
                var diskProblem = DescribeDiskCapacityProblem(settings);
                if (diskProblem != null)
                {
                    return Results.Json(new { error = diskProblem }, statusCode: 503);
                }
                var form = await request.ReadFormAsync();

                var prompt = (form["prompt"].ToString() ?? "").Trim();
                if (prompt.Length == 0)
                {
                    return Results.BadRequest(new { error = "prompt is required" });
                }

                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    request.HttpContext.Items["micUser"] as string ?? "",
                    out var createdBy,
                    out var userError))
                {
                    return Results.BadRequest(new { error = userError });
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

                // Uploaded images (clipboard paste / drag-drop / file picker)
                // are persisted under the day folder so job inputs are archived
                // alongside outputs, then fed to edit generators by path.
                // Accept repeated "images" parts and the legacy single "image".
                var uploadFiles = form.Files.GetFiles("images")
                    .Concat(form.Files.GetFiles("image"))
                    .Where(f => f != null && f.Length > 0)
                    .Take(UiJobRunner.MaxInputImages + 1)
                    .ToList();
                if (uploadFiles.Count > UiJobRunner.MaxInputImages)
                {
                    return Results.BadRequest(new
                    {
                        error = $"At most {UiJobRunner.MaxInputImages} input images are accepted.",
                    });
                }

                var inputPaths = new List<string>();
                var inputImageWidth = 0;
                var inputImageHeight = 0;
                var savedInputs = new List<(byte[] Bytes, string ContentType, string Path)>();
                if (uploadFiles.Count > 0)
                {
                    // Text-to-image-only targets are deliberately allowed on
                    // image jobs: they run from the prompt alone (user-specified
                    // product behavior, 2026-07-28 — see UiJobRunner.ImageCapableKeys).
                    // The AR-override rule only applies to targets that will
                    // actually consume the image. Aspect matching uses the
                    // primary (first) attached image.
                    if (shape != "auto")
                    {
                        var aspectIncompatible = genKeys
                            .Where(key => UiJobRunner.IsImageCapable(key) && !SupportsImageAspectOverride(key))
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
                        for (var i = 0; i < uploadFiles.Count; i++)
                        {
                            var (path, bytes, contentType, width, height)
                                = await SaveInputImageAsync(uploadFiles[i], settings, $"input{i}");
                            inputPaths.Add(path);
                            savedInputs.Add((bytes, contentType, path));
                            if (i == 0)
                            {
                                inputImageWidth = width;
                                inputImageHeight = height;
                            }
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }

                // gpt-image-2 anti-murk guidance. Missing fields (an older
                // window still open from before this control existed) get the
                // declared defaults — enabled with the standard text — chosen
                // before the job starts, exactly like the other option defaults.
                var gpt2GuidanceEnabled = !string.Equals(
                    form["gpt2GuidanceEnabled"].ToString(),
                    "false",
                    StringComparison.OrdinalIgnoreCase);
                var gpt2GuidanceText = form.ContainsKey("gpt2GuidanceText")
                    ? form["gpt2GuidanceText"].ToString().Trim()
                    : DefaultGpt2GuidanceText;
                // Enabled-but-blank falls back to the default text. A browser
                // that persisted an emptied textbox stripped the guidance from
                // every gpt2 call for two days (2026-07-31 → 08-02; ultra-dark
                // output) while the toggle still said on. Turning guidance off
                // is the toggle's job; blank text never silently disables it.
                if (gpt2GuidanceEnabled && string.IsNullOrWhiteSpace(gpt2GuidanceText))
                {
                    gpt2GuidanceText = DefaultGpt2GuidanceText;
                }

                if (!runner.TryAcquireJobAdmission(out var admission))
                {
                    DeleteRejectedJobInputs(savedInputs.Select(saved => saved.Path));
                    return Results.Json(
                        new { error = $"The UI queue is full ({runner.MaxPendingJobs} pending jobs). Try again after a job finishes." },
                        statusCode: 503);
                }
                try
                {
                    var job = new UiJob
                    {
                        Prompt = prompt,
                        CreatedBy = createdBy,
                        InputImagePaths = inputPaths,
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
                        Gpt2GuidanceEnabled = gpt2GuidanceEnabled,
                        Gpt2GuidanceText = gpt2GuidanceText,
                    };
                    jobs.Add(job);
                    for (var i = 0; i < savedInputs.Count; i++)
                    {
                        // Keep each input in the job's image store so reloaded pages
                        // can show thumbnails without touching the saves/ layout.
                        var saved = savedInputs[i];
                        job.StoreImage("input", i, saved.Bytes, saved.ContentType, saved.Path);
                    }
                    // The full option set rides the persisted accepted event so a
                    // job card can restore this exact setup into the composer
                    // ("set active"), including after a server restart.
                    job.Emit(new
                    {
                        type = "accepted",
                        gens = genKeys,
                        hasImage = job.HasInputImage,
                        inputCount = job.InputImageCount,
                        inputWidth = job.HasInputImage ? job.InputImageWidth : (int?)null,
                        inputHeight = job.HasInputImage ? job.InputImageHeight : (int?)null,
                        shape,
                        detail = spec.Detail,
                        quality = spec.Quality,
                        moderation = spec.Moderation,
                        n = spec.ImageCount,
                        gpt2GuidanceEnabled = spec.Gpt2GuidanceEnabled,
                        gpt2GuidanceText = spec.Gpt2GuidanceText,
                        prompt,
                        at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    var ownedAdmission = admission!;
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
                            ownedAdmission.Dispose();
                            activeJobs.TryRemove(job.Id, out _);
                        }
                    });
                    admission = null;
                    return Results.Json(new { id = job.Id });
                }
                finally
                {
                    admission?.Dispose();
                }
            });

            app.MapPost("/api/video-jobs", async (HttpRequest request) =>
            {
                var diskProblem = DescribeDiskCapacityProblem(settings);
                if (diskProblem != null)
                {
                    return Results.Json(new { error = diskProblem }, statusCode: 503);
                }
                var availabilityProblem = runner.DescribeAvailabilityProblem(UiJobRunner.KeyGrokWebVideo);
                if (availabilityProblem != null)
                {
                    return Results.BadRequest(new { error = availabilityProblem });
                }

                var form = await request.ReadFormAsync();
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    request.HttpContext.Items["micUser"] as string ?? "",
                    out var videoCreatedBy,
                    out var videoUserError))
                {
                    return Results.BadRequest(new { error = videoUserError });
                }
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
                if (!runner.TryAcquireJobAdmission(out var admission))
                {
                    DeleteRejectedJobInputs(new[] { inputImagePath });
                    return Results.Json(
                        new { error = $"The UI queue is full ({runner.MaxPendingJobs} pending jobs). Try again after a job finishes." },
                        statusCode: 503);
                }
                try
                {
                    var job = new UiJob
                    {
                        Prompt = prompt,
                        CreatedBy = videoCreatedBy,
                        InputImagePaths = new[] { inputImagePath },
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
                    var ownedAdmission = admission!;
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
                            ownedAdmission.Dispose();
                            activeJobs.TryRemove(job.Id, out _);
                        }
                    });
                    admission = null;
                    return Results.Json(new { id = job.Id });
                }
                finally
                {
                    admission?.Dispose();
                }
            });

            // Spelling-only prompt correction via Claude (temperature 0, no
            // rephrasing). The frontend keeps the pre-fix text for its undo
            // button; the server is stateless here.
            app.MapPost("/api/prompt/spellfix", async (HttpRequest request) =>
            {
                if (claudeService == null)
                {
                    return Results.BadRequest(new { error = spellfixProblem });
                }
                var form = await request.ReadFormAsync();
                var prompt = form["prompt"].ToString();
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return Results.BadRequest(new { error = "prompt is empty" });
                }
                try
                {
                    var corrected = await claudeService.FixSpellingAsync(prompt);
                    return Results.Json(new { corrected });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI spellfix failed: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 502);
                }
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
                // A miss is transient (job unknown here, or bytes not landed
                // yet); never let a 404 become heuristically cacheable.
                ctx.Response.Headers.CacheControl = "no-store";
                var job = jobs.Get(id);
                if (job == null) return Results.NotFound();

                // ?thumb=1 asks for the <=640px card preview; without it the
                // exact original bytes are served (viewer, new-tab, video
                // sources, set-active restore all use the plain URL).
                IResult fileResult;
                if (ctx.Request.Query.ContainsKey("thumb"))
                {
                    if (job.TryGetCardPreviewPath(gen, n, out var thumbPath, out var thumbType))
                    {
                        // Disk-backed thumb — stream, do not buffer into heap.
                        fileResult = Results.File(
                            Path.GetFullPath(thumbPath),
                            thumbType,
                            enableRangeProcessing: true);
                    }
                    else if (job.TryGetCardPreviewBytes(gen, n, out var bytes, out var contentType))
                    {
                        // Ephemeral streaming partials only.
                        fileResult = Results.File(bytes, contentType);
                    }
                    else
                    {
                        return Results.NotFound();
                    }
                }
                else if (job.TryGetImagePath(gen, n, out var path, out var pathType))
                {
                    // Stream from disk — do not buffer the whole file into the
                    // process heap for every concurrent viewer.
                    fileResult = Results.File(
                        Path.GetFullPath(path),
                        pathType,
                        enableRangeProcessing: true);
                }
                else if (job.TryGetImage(gen, n, out var memBytes, out var memType))
                {
                    // Ephemeral partials (no durable path yet).
                    fileResult = Results.File(memBytes, memType);
                }
                else
                {
                    return Results.NotFound();
                }

                // Input images are archived to disk before the job is announced
                // to any client and are never rewritten, so they are stable from
                // the first moment their URL can exist — cacheable mid-run.
                var bytesAreFinal = job.IsDone
                    || string.Equals(gen, "input", StringComparison.Ordinal);
                if (bytesAreFinal)
                {
                    // Finished bytes never change, so let the browser cache
                    // them: without this every page refresh re-downloads the
                    // entire job history through the ~6-socket pool.
                    ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                }
                else
                {
                    // While the job runs, a stable URL may advance from blurry
                    // GPT-Image-2 partials to the final result. Force reloads
                    // to ask for the current bytes.
                    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Response.Headers.Pragma = "no-cache";
                    ctx.Response.Headers.Expires = "0";
                }
                return fileResult;
            });

            app.MapGet("/api/status", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(UiProcessMemory.Snapshot(jobs, runner));
            });

            // Every distinct user-uploaded input image, newest first, for the
            // composer's "load a previous image" picker. Video jobs are
            // excluded: their stored input is a copied result image, not an
            // upload. Multi-input jobs contribute every index. Re-pastes of
            // the same image across jobs are deduped by SHA-256 of the
            // archived bytes (hashes live in images.json; listing peeks disk
            // without hydrating full UiJob graphs). Entries whose archived
            // bytes are no longer readable are omitted and logged, never guessed.
            app.MapGet("/api/input-images", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var images = new List<object>();
                foreach (var entry in jobs.ListInputLibraryCandidates())
                {
                    if (!UiJobStorage.TryPeekJobSummary(
                        jobs.HistoryRoot,
                        entry.FolderName,
                        out var prompt,
                        out var width,
                        out var height,
                        out var createdAt))
                    {
                        Logger.Log(
                            $"UI input library: job {entry.Id} metadata is unreadable; omitted.");
                        continue;
                    }
                    for (var index = 0; index < entry.InputImageCount; index++)
                    {
                        if (!UiJobStorage.TryPeekImageSha256(
                            jobs.HistoryRoot,
                            entry.FolderName,
                            $"input/{index}",
                            out var hash))
                        {
                            Logger.Log(
                                $"UI input library: job {entry.Id} input image {index} bytes are unavailable; omitted from the listing.");
                            continue;
                        }
                        if (!seenHashes.Add(hash))
                        {
                            continue;
                        }
                        images.Add(new
                        {
                            jobId = entry.Id,
                            index,
                            url = $"/api/jobs/{entry.Id}/images/input/{index}",
                            width = index == 0 ? width : 0,
                            height = index == 0 ? height : 0,
                            prompt,
                            createdAtUnixMs = new DateTimeOffset(createdAt).ToUnixTimeMilliseconds(),
                        });
                    }
                }
                return Results.Json(new { images });
            });

            // ---- day archive ----
            // The live envelope feed only carries today's jobs (multi-day
            // history would otherwise replay through every page load). Older
            // jobs are grouped by day and fetched lazily: the day list first,
            // then one day's complete jobs+events on expand. The payload uses
            // the same job metadata + event JSON as the live feed so the
            // frontend renders archived cards through the identical path.
            app.MapGet("/api/archive/days", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var days = jobs.ListArchivedDays().Select(d => new
                {
                    day = d.Day,
                    label = DateTime.ParseExact(d.Day, "yyyy-MM-dd", null).ToString("dddd, MMM d, yyyy"),
                    count = d.Count,
                });
                return Results.Json(new { days });
            });

            app.MapGet("/api/archive/days/{day}", (string day, HttpContext ctx) =>
            {
                if (!DateTime.TryParseExact(day, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var parsedDay))
                {
                    return Results.BadRequest(new { error = "day must be yyyy-MM-dd" });
                }
                var dayJobs = jobs.ListArchivedDay(parsedDay);
                // Hand-assembled like /api/events/poll: job metadata and each
                // event are already-serialized JSON strings.
                var sb = new StringBuilder();
                sb.Append("{\"jobs\":[");
                for (var i = 0; i < dayJobs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var job = dayJobs[i];
                    var (events, _) = job.ReadFrom(0);
                    sb.Append("{\"job\":");
                    sb.Append(UiJobRegistry.SerializeJobMetadata(job));
                    sb.Append(",\"events\":[");
                    sb.Append(string.Join(",", events));
                    sb.Append("]}");
                }
                sb.Append("]}");
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(sb.ToString(), "application/json");
            });

            // Every creator name seen across live + archived history, for the
            // person filter bar. "" groups jobs from before attribution.
            app.MapGet("/api/users", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var users = jobs.ListUsers().Select(u => new { user = u.User, count = u.Count });
                return Results.Json(new { users });
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

            Logger.Log($"UI server starting on {url}  (build {UiBuildInfo.Describe}, wwwroot: {wwwroot})");
            Console.WriteLine();
            Console.WriteLine($"  MultiImageClient UI:  {url}  (build {UiBuildInfo.Commit})");
            Console.WriteLine("  Ctrl-C to stop.");
            Console.WriteLine();
            // Interactive `dotnet run -- --ui` still opens a tab. Under systemd
            // (always-on unit / dashboard restart) every start would otherwise
            // pile up new browser tabs — skip unless --ui-open forced it.
            if (ShouldAutoOpenBrowser(options))
            {
                TryOpenBrowser(url);
            }
            else
            {
                Logger.Log($"UI: not auto-opening browser (systemd or --ui-no-open); open {url} yourself.");
            }

            using var livenessGuard = new UiLivenessGuard(options.UiPort);
            livenessGuard.Start();
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

        private static void DeleteRejectedJobInputs(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Could not remove rejected UI job input '{path}': {ex.Message}");
                }
            }
        }

        /// Creator-name policy: every job is created under a display username
        /// (shared site, no privacy — attribution only). The submitted name
        /// wins; with the access gate on and no name submitted, the login
        /// name is the declared pre-operation default. No gate + no name is
        /// a 400. Names are trimmed, inner whitespace collapsed, 1-32 chars,
        /// letters/digits/space/._- only.
        private static bool TryResolveCreatorName(string? submitted, string authUser, out string name, out string error)
        {
            name = System.Text.RegularExpressions.Regex.Replace((submitted ?? "").Trim(), @"\s+", " ");
            if (name.Length == 0)
            {
                name = authUser;
            }
            if (name.Length == 0)
            {
                error = "choose a username first (top of the page) — every job is created under a name";
                return false;
            }
            if (name.Length > 32)
            {
                error = "username must be 32 characters or fewer";
                return false;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9 ._-]+$"))
            {
                error = "username may only contain letters, digits, spaces, and . _ -";
                return false;
            }
            error = "";
            return true;
        }

        private static string? DescribeDiskCapacityProblem(Settings settings)
        {
            if (settings.UiMinimumFreeDiskBytes <= 0)
            {
                return null;
            }
            try
            {
                var fullPath = Path.GetFullPath(settings.ImageDownloadBaseFolder);
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return $"Cannot determine the filesystem for ImageDownloadBaseFolder '{fullPath}'.";
                }
                var available = new DriveInfo(root).AvailableFreeSpace;
                if (available < settings.UiMinimumFreeDiskBytes)
                {
                    return "New jobs are paused to protect this shared server: "
                        + $"the output filesystem has {available / (1024 * 1024):N0} MiB free, "
                        + $"below the configured reserve of "
                        + $"{settings.UiMinimumFreeDiskBytes / (1024 * 1024):N0} MiB.";
                }
                return null;
            }
            catch (Exception ex)
            {
                // A configured guard must fail closed if capacity cannot be
                // measured; otherwise a mount/permission failure could disable
                // the exact protection the owner requested.
                return $"Cannot verify free output space; new jobs are paused: {ex.Message}";
            }
        }

        // The failed-login throttle keys on the caller's address. Behind the
        // nginx deployment every connection arrives from loopback, so trust
        // X-Forwarded-For (nginx always sets it) only for loopback peers.
        private static string ClientIpForThrottle(HttpContext ctx)
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote != null && System.Net.IPAddress.IsLoopback(remote))
            {
                var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
                if (!string.IsNullOrWhiteSpace(forwarded))
                {
                    return forwarded.Split(',')[0].Trim();
                }
            }
            return remote?.ToString() ?? "unknown";
        }

        private static bool IsEffectivelyHttps(HttpContext ctx)
        {
            if (ctx.Request.IsHttps)
            {
                return true;
            }
            return string.Equals(
                ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
                "https",
                StringComparison.OrdinalIgnoreCase);
        }

        // Served inline (no static file) so the gate has zero anonymous
        // surface beyond this page and the login POST. Posts to the RELATIVE
        // api path so it works behind any reverse-proxy prefix.
        private const string LoginPageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>MultiImageClient — log in</title>
<style>
  body { font-family: system-ui, sans-serif; background: #f5f2ea; color: #1a1a1a;
         display: flex; min-height: 100vh; align-items: center; justify-content: center; margin: 0; }
  form { background: #fff; border: 1px solid #d8d2c4; border-radius: 8px; padding: 28px 32px;
         display: flex; flex-direction: column; gap: 12px; min-width: 280px; }
  h1 { font-size: 18px; margin: 0 0 4px; }
  label { display: flex; flex-direction: column; gap: 4px; font-size: 13px; }
  input { font-size: 15px; padding: 7px 9px; border: 1px solid #c9c2b2; border-radius: 5px; }
  button { font-size: 15px; padding: 8px; border: none; border-radius: 5px;
           background: #2456b8; color: #fff; cursor: pointer; }
  #err { color: #b00020; font-size: 13px; min-height: 1.2em; margin: 0; }
</style>
</head>
<body>
<form id="f">
  <h1>MultiImageClient</h1>
  <label>username <input id="u" autocomplete="username" autofocus></label>
  <label>password <input id="p" type="password" autocomplete="current-password"></label>
  <button type="submit">log in</button>
  <p id="err"></p>
</form>
<script>
document.getElementById("f").addEventListener("submit", async (e) => {
  e.preventDefault();
  const err = document.getElementById("err");
  err.textContent = "";
  const form = new FormData();
  form.append("username", document.getElementById("u").value);
  form.append("password", document.getElementById("p").value);
  try {
    const resp = await fetch("api/auth/login", { method: "POST", body: form });
    const body = await resp.json();
    if (!resp.ok) { err.textContent = body.error || ("HTTP " + resp.status); return; }
    location.reload();
  } catch (ex) {
    err.textContent = String(ex);
  }
});
</script>
</body>
</html>
""";

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
            Settings settings,
            string namePart = "input")
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            return await SaveConformedInputImageAsync(ms.ToArray(), namePart, settings);
        }

        private static async Task<(string Path, int Width, int Height)> SaveInputImageBytesAsync(
            byte[] bytes,
            string contentType,
            Settings settings)
        {
            var (path, _, _, width, height)
                = await SaveConformedInputImageAsync(bytes, "video_source", settings);
            return (path, width, height);
        }

        // Every provider consumes the pasted image from this saved file, so the
        // saved extension + content type MUST match the actual bytes. The browser
        // hands over whatever it had (drag-dropping a web image can yield GIF,
        // BMP, AVIF, ...), and providers sniff the bytes: Ideogram, for one,
        // rejects anything that isn't PNG/JPEG/WEBP. PNG/JPEG/WEBP inputs are
        // saved verbatim under their true type; other decodable formats are
        // deterministically re-encoded to PNG before any job starts (logged
        // pre-operation input conformance, same policy as the Recraft input
        // conformer). Undecodable uploads are a hard 400 before the job exists.
        private static async Task<(string Path, byte[] Bytes, string ContentType, int Width, int Height)> SaveConformedInputImageAsync(
            byte[] bytes,
            string namePart,
            Settings settings)
        {
            string mime;
            int width;
            int height;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var info = Image.Identify(stream);
                if (info == null || info.Width <= 0 || info.Height <= 0)
                {
                    throw new InvalidDataException("Uploaded image has no readable dimensions.");
                }
                mime = info.Metadata.DecodedImageFormat?.DefaultMimeType
                    ?? throw new InvalidDataException("Uploaded image format could not be identified.");
                width = info.Width;
                height = info.Height;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Uploaded image could not be decoded: {ex.Message}", ex);
            }

            var ext = mime.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => "",
            };
            var contentType = mime.ToLowerInvariant();
            if (ext == "")
            {
                using var image = Image.Load(bytes);
                using var pngStream = new MemoryStream();
                await image.SaveAsPngAsync(pngStream);
                Logger.Log(
                    $"UI input image arrived as {mime} ({width}x{height}); re-encoded to PNG "
                    + $"({bytes.LongLength:N0} -> {pngStream.Length:N0} bytes) so every provider "
                    + "receives a correctly-labeled supported format.");
                bytes = pngStream.ToArray();
                ext = ".png";
                contentType = "image/png";
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            var folder = Path.Combine(settings.ImageDownloadBaseFolder, today, "UiInputs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{DateTime.Now:HHmmss_fff}_{namePart}{ext}");
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Log($"UI {namePart} image saved: {path} ({contentType}, {width}x{height})");
            return (path, bytes, contentType, width, height);
        }

        // Interactive --ui still opens a tab by default. That is not gated by
        // --open-images (which governs finished-image viewer pops). Always-on
        // systemd starts must not open a tab on every restart.
        private static bool ShouldAutoOpenBrowser(RunOptions options)
        {
            if (options.UiOpenBrowser == false)
            {
                return false;
            }
            if (options.UiOpenBrowser == true)
            {
                return true;
            }
            // systemd sets INVOCATION_ID for every started unit.
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));
        }

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
