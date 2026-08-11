#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    ///   POST /api/generator-preferences        authenticated user's chooser/defaults/presets/endpoint config
    ///   POST /api/prompt/advice                user-directed Claude prompt edit -> {replacement}
    ///   GET  /api/prompt/advice/history        exact, durable Claude exchange history for this user
    ///   GET  /api/archive/days                 archived (pre-today) days with job counts
    ///   GET  /api/archive/days/{day}           one archived day's jobs + full event history
    ///   GET  /api/users                        every creator name with job counts (filter bar)
    ///   GET  /api/favorites                    persistent image + whole-prompt favorites by user
    ///   POST /api/favorites                    idempotently set one user's exact resource favorite
    ///   GET  /api/activity/poll?after=N        bounded shared activity poll
    ///   POST /api/requests                     submit a request to the developer
    ///   GET  /api/requests?after=N             developer-only request inbox
    ///   POST /api/visibility                   creator-only permanent prompt/image stream hiding
    ///   POST /api/auth/login|logout            shared-site access gate (only when UiAuthFilePath is set)
    public class UiWorkflow
    {
        private const string VisibilityOverrideLogin = "ernieMultiZone";

        private static bool SupportsImageAspectOverride(
            UiJobRunner runner,
            string key)
            => runner.IsImageCapableForCurrentSettings(key)
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
        private const int MaxGeneratorExtraTextChars = 16000;
        private const int MaxGeneratorNotesChars = 16000;
        private const int MaxGeneratorConfigurationTotalChars = 128000;
        private const int MaxJobExtraTextTotalChars = 64000;

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
            UiFavoriteStore favorites;
            UiVisibilityStore visibility;
            UiCommunityStore community;
            try
            {
                favorites = new UiFavoriteStore(settings);
                visibility = new UiVisibilityStore(settings);
                community = new UiCommunityStore(settings);
                if (auth != null)
                {
                    community.ReserveLoginNames(auth.ListAccountNames());
                }
                foreach (var attribution in jobs.ListAuthenticatedAttributions())
                {
                    community.ReserveAliases(
                        attribution.CreatorLogin,
                        attribution.Aliases);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"UI aborted: persistent UI state could not be loaded: {ex.Message}");
                return;
            }
            var activeJobs = new ConcurrentDictionary<string, Task>();
            await using var runner = new UiJobRunner(settings, stats, options);

            // User-directed prompt editing through Claude. The exact system
            // prompt, instruction, source prompt, wire prompt, response, and
            // outcome are persisted by UiCommunityStore for audit/history.
            var claudeAdviceProblem = ProviderKeyValidator.DescribeTextKeyProblem(
                nameof(settings.AnthropicApiKey), settings.AnthropicApiKey);
            var claudeService = claudeAdviceProblem == null
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
                var authUser = ctx.Items["micUser"] as string ?? "";
                var profileSnapshot = community.SnapshotProfiles();
                var currentProfile = profileSnapshot.Profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Login, authUser, StringComparison.OrdinalIgnoreCase));
                var generatorPreferences = community.GetGeneratorPreferences(authUser);
                // Display order: gpt-image-2, grok-*, ideogram, recraft, then the
                // rest; unavailable targets (missing keys, gated local models)
                // sink to the end via the stable OrderBy below.
                var generators = new[]
                {
                    new { key = UiJobRunner.KeyGpt2, label = "gpt-image-2", detail = "OpenAI. /edits when an image is attached, /generations otherwise. Accepts up to 4 ordered input images (other selected generators only receive the first). The default output AR matches the primary attached source; explicit AR choices override it." },
                    new { key = UiJobRunner.KeyGrokWeb, label = "grok-web pro", detail = runner.IsImageCapableForCurrentSettings(UiJobRunner.KeyGrokWeb)
                        ? "grok.com cookie session. Text-to-image uses the browser-free imagine WebSocket; attached images use browser-free x-statsig-id-signed imagine-image-edit. Auto edits inherit the source shape. Text-to-image auto requests square 1:1 because the WebSocket has no prompt-aware auto."
                        : "grok.com cookie session using the browser-free imagine WebSocket. Text-to-image only until current x-statsig-id signing material is captured; attached images are not sent. Auto requests square 1:1 because this transport has no prompt-aware auto and Grok's own default is 2:3. Side-by-side mode requests up to 4 images." },
                    new { key = UiJobRunner.KeyGrokWebChat, label = "grok-web chat", detail = "grok.com cookie session, chat-message door. The attached image is sent as a normal chat message; a chat model (grok-3) reads it, expands your instruction into a detailed edit prompt, and edits via imagine-image-edit. Requires an attached image and current x-statsig-id signing material. Slower than direct edit because the chat model reasons first." },
                    new { key = UiJobRunner.KeyGrokApi, label = "grok-api", detail = "api.x.ai standard tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyGrokApiPro, label = "grok-api pro", detail = "api.x.ai pro tier. With an input, the default maps its dimensions to Grok's nearest supported AR; explicit shape, detail (1k/2k), and n are honored." },
                    new { key = UiJobRunner.KeyKrea, label = "Krea 2 Medium", detail = "Krea's own foundation image model, not an aggregated third-party model. Best for expressive illustration and stable general use. An attached image is sent as a 0.6-strength style reference; auto matches its nearest native aspect ratio. The API currently accepts 1K only, so detail has no effect. n runs separate generations." },
                    new { key = UiJobRunner.KeyKreaTurbo, label = "Krea 2 Medium Turbo", detail = "Krea's fastest and least expensive Krea 2 variant. An attached image is sent as a 0.6-strength style reference. The API currently accepts 1K only; n runs separate generations." },
                    new { key = UiJobRunner.KeyKreaLarge, label = "Krea 2 Large", detail = "Krea's highest-fidelity Krea 2 variant, strongest for photorealism, raw texture, grain, and expressive styles. An attached image is sent as a 0.6-strength style reference. The API currently accepts 1K only; n runs separate generations." },
                    new { key = UiJobRunner.KeyIdeogram, label = "Ideogram 4.0", detail = "Ideogram 4.0. Without an attachment it uses Generate; with one it uses Remix and lets Ideogram choose source influence from the instruction. Auto matches the source to the nearest published 2K resolution; explicit shape overrides it. Detail has no effect. n runs separate generations." },
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
                    // Describe targets (image → text). Rendered by the frontend as
                    // their own chooser section; selectable only while an image is
                    // attached (each one describes every attached input). The
                    // composer prompt, when non-blank, is the describe instruction;
                    // blank describe-only jobs get the standard instruction.
                    new { key = UiJobRunner.KeyDescribeIdeogram, label = "Ideogram describe", detail = "Ideogram's /describe endpoint. Fixed built-in instruction — your prompt text is NOT sent to it. Returns Ideogram's own caption(s) for each attached image. $0.01 per image." },
                    new { key = UiJobRunner.KeyDescribeOpenAi, label = "OpenAI describe (gpt-4.1)", detail = "OpenAI gpt-4.1 vision. Your prompt is the instruction when present; otherwise the standard describe instruction is used." },
                    new { key = UiJobRunner.KeyDescribeClaude, label = "Claude describe (Sonnet)", detail = "Anthropic claude-sonnet-4-5 vision. Your prompt is the instruction when present; otherwise the standard describe instruction is used." },
                    new { key = UiJobRunner.KeyDescribeGemini, label = "Gemini describe (2.5 Pro)", detail = "Google gemini-2.5-pro vision. Your prompt is the instruction when present; otherwise the standard describe instruction is used." },
                    new { key = UiJobRunner.KeyDescribeGrok, label = "Grok describe (grok-4.3)", detail = "xAI grok-4.3 vision via api.x.ai. Your prompt is the instruction when present; otherwise the standard describe instruction is used." },
                    // Layout map: analysis target like describe (requires an
                    // attached image, ignores the output-options row) but its
                    // result is an IMAGE — a server-rendered flat-color map of
                    // the sections Gemini identified, with a numbered legend
                    // and one-sentence summary baked into the PNG.
                    new { key = UiJobRunner.KeyLayoutMap, label = "Layout map (Gemini 2.5 Pro)", detail = "Google gemini-2.5-pro names each attached image's main sections and topics with bounding boxes; the server renders them as a simple flat-color map image with a numbered color legend and a one-sentence summary. Your prompt, when present, is passed as context for the section labels." },
                }
                .Select(g => new
                {
                    g.key,
                    g.label,
                    g.detail,
                    available = runner.IsAvailable(g.key),
                    availabilityProblem = runner.DescribeAvailabilityProblem(g.key),
                    // Analysis targets (describe + layout map) consume the
                    // attached image by definition (that's their whole input),
                    // so they count as image-capable for the chip icons and
                    // image-only bulk actions.
                    imageCapable = runner.IsImageCapableForCurrentSettings(g.key) || UiJobRunner.IsAnalysisKey(g.key),
                    imageCapabilityProblem = runner.DescribeImageCapabilityProblem(g.key),
                    imageAspectOverride = SupportsImageAspectOverride(runner, g.key),
                    // "describe"-kind targets analyze the attached image
                    // (describe returns text; layout map returns a rendered
                    // map image). They render as their own chooser section,
                    // selectable only with an image attached; everything else
                    // returns generated media.
                    kind = UiJobRunner.IsAnalysisKey(g.key) ? "describe" : "image",
                    // Analysis targets require an image (their input).
                    // grok-web-chat also requires one: it edits the attached
                    // image through a chat message and has no text-to-image
                    // path in this UI.
                    requiresImage = UiJobRunner.IsAnalysisKey(g.key)
                        || g.key == UiJobRunner.KeyGrokWebChat,
                    // Known hard prompt-length caps, surfaced so the composer can
                    // warn before submit; the server truncates over-limit prompts
                    // at the provider send stage (grok-web: GrokWebClient).
                    maxPromptChars = g.key == UiJobRunner.KeyGrokWeb ? (int?)GrokWebClient.MaxPromptChars : null,
                    defaultExtraText = g.key == UiJobRunner.KeyGpt2
                        ? DefaultGpt2GuidanceText
                        : "",
                    defaultNotes = "",
                    // Default-on set for new windows: gpt-image-2, Recraft V4.1,
                    // grok-web pro, Ideogram 4.0, FLUX.2 Pro Preview, Nano Banana 2.
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
                    claudeAdvice = new
                    {
                        available = claudeAdviceProblem == null,
                        availabilityProblem = claudeAdviceProblem,
                    },
                    generatorPreferences = generatorPreferences == null
                        ? null
                        : new
                        {
                            showImageSection = generatorPreferences.ShowImageSection,
                            showDescribeSection = generatorPreferences.ShowDescribeSection,
                            hiddenGeneratorKeys = generatorPreferences.HiddenGeneratorKeys,
                            defaultSelectedKeys = generatorPreferences.DefaultSelectedKeys,
                            presets = generatorPreferences.Presets.Select(preset => new
                            {
                                preset.Id,
                                preset.Name,
                                generatorKeys = preset.GeneratorKeys,
                            }),
                            endpointConfigurations =
                                generatorPreferences.EndpointConfigurations.Select(configuration => new
                                {
                                    configuration.Key,
                                    configuration.ExtraText,
                                    configuration.Notes,
                                }),
                            updatedAtUnixMs = generatorPreferences.UpdatedAtUnixMs,
                        },
                    generatorEndpointConfiguration = new
                    {
                        maxExtraTextChars = MaxGeneratorExtraTextChars,
                        maxNotesChars = MaxGeneratorNotesChars,
                        maxConfigurationTotalChars = MaxGeneratorConfigurationTotalChars,
                        maxJobExtraTextTotalChars = MaxJobExtraTextTotalChars,
                    },
                    // gpt-image-2 anti-murk guidance: on by default, textbox
                    // prefilled with this text, appended server-side to the
                    // gpt2 target's prompt only.
                    gpt2Guidance = new
                    {
                        defaultEnabled = true,
                        defaultText = DefaultGpt2GuidanceText,
                    },
                    // Standard instruction used when a describe-only job is
                    // submitted with a blank prompt; the composer shows the
                    // same text so the card matches what actually went out.
                    describe = new
                    {
                        defaultInstruction = UiJobRunner.DefaultDescribeInstruction,
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
                        user = authUser,
                        profile = currentProfile == null
                            ? null
                            : new
                            {
                                publicId = currentProfile.PublicId,
                                displayName = currentProfile.DisplayName,
                            },
                    },
                    notifications = new
                    {
                        isDeveloper = IsDeveloperLogin(ctx.Items["micUser"] as string ?? ""),
                        maxRequestChars = UiCommunityStore.MaxRequestChars,
                        returnAfterHours = UiCommunityStore.ReturnAfter.TotalHours,
                    },
                });
            });

            app.MapPost("/api/generator-preferences", async (HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (authUser.Length == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = "Server-persistent generator preferences require an authenticated login; open local mode stores them in this browser.",
                    });
                }
                UiGeneratorPreferencesRequest? submitted;
                try
                {
                    submitted = await request.ReadFromJsonAsync<UiGeneratorPreferencesRequest>();
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = $"generator preferences are not valid JSON: {ex.Message}" });
                }
                if (submitted == null)
                {
                    return Results.BadRequest(new { error = "generator preferences are required" });
                }
                try
                {
                    var normalized = NormalizeGeneratorPreferences(authUser, submitted, runner);
                    community.SaveGeneratorPreferences(normalized);
                    Logger.Log(
                        $"UI generator preferences: '{authUser}' updated chooser visibility, defaults, "
                        + $"{normalized.Presets.Count} preset(s), and "
                        + $"{normalized.EndpointConfigurations.Count} endpoint override(s).");
                    return Results.Json(new
                    {
                        ok = true,
                        updatedAtUnixMs = normalized.UpdatedAtUnixMs,
                    });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/profiles", (long? version, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var snapshot = community.SnapshotProfiles();
                var authUser = ctx.Items["micUser"] as string ?? "";
                var currentPublicId = UiCommunityStore.PublicIdentityId(authUser);
                if (version == snapshot.Version)
                {
                    return Results.Json(new { version = snapshot.Version, unchanged = true });
                }
                return Results.Json(new
                {
                    version = snapshot.Version,
                    unchanged = false,
                    currentPublicId,
                    profiles = snapshot.Profiles.Select(profile => new
                    {
                        publicId = profile.PublicId,
                        displayName = profile.DisplayName,
                    }),
                });
            });

            app.MapPost("/api/profile", async (HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (authUser.Length == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = "Profile-wide attribution requires an authenticated account.",
                    });
                }
                var form = await request.ReadFormAsync();
                if (!TryNormalizeCreatorName(
                    form["displayName"].ToString(),
                    out var displayName,
                    out var displayError))
                {
                    return Results.BadRequest(new { error = displayError });
                }
                try
                {
                    var profile = community.SetProfileName(
                        authUser,
                        displayName,
                        jobs.ListHistoricalAliasesForLogin(authUser),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log(
                        $"UI profile: authenticated account '{authUser}' set its public name to '{displayName}'.");
                    return Results.Json(new
                    {
                        publicId = profile.PublicId,
                        displayName = profile.DisplayName,
                        version = community.SnapshotProfiles().Version,
                    });
                }
                catch (UiProfileNameConflictException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 409);
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI profile: rename failed for '{authUser}': {ex.Message}");
                    return Results.Json(
                        new { error = "The profile name change could not be persisted." },
                        statusCode: 500);
                }
            });

            // Explicit developer-only legacy ownership assignment. It changes
            // exactly one job identified by id and only while CreatorLogin is
            // still blank; CreatedBy remains untouched as the audit record.
            app.MapPost("/api/admin/legacy-attribution", async (HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (!IsDeveloperLogin(authUser))
                {
                    return Results.Json(new { error = "not found" }, statusCode: 404);
                }
                if (auth == null)
                {
                    return Results.BadRequest(new
                    {
                        error = "Legacy attribution requires the authenticated shared site.",
                    });
                }
                var form = await request.ReadFormAsync();
                var jobId = form["jobId"].ToString().Trim();
                var expectedCreatedBy = form["expectedCreatedBy"].ToString();
                var requestedTarget = form["targetLogin"].ToString().Trim();
                var targetLogin = auth.ListAccountNames().FirstOrDefault(account =>
                    string.Equals(account, requestedTarget, StringComparison.OrdinalIgnoreCase));
                if (jobId.Length == 0
                    || expectedCreatedBy.Length == 0
                    || targetLogin == null)
                {
                    return Results.BadRequest(new
                    {
                        error = "jobId, exact expectedCreatedBy, and an existing targetLogin are required.",
                    });
                }

                var job = jobs.Get(jobId);
                if (job == null)
                {
                    return Results.NotFound(new { error = "The exact job does not exist." });
                }
                if (job.CreatorLogin.Length != 0)
                {
                    return Results.Conflict(new
                    {
                        error = "This job already has an authenticated owner.",
                    });
                }
                if (!string.Equals(job.CreatedBy, expectedCreatedBy, StringComparison.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        error = "The job's historical attribution no longer matches expectedCreatedBy.",
                    });
                }

                try
                {
                    community.ReserveAliases(targetLogin, new[] { expectedCreatedBy });
                    var assigned = jobs.AssignLegacyCreatorLogin(
                        jobId,
                        expectedCreatedBy,
                        targetLogin);
                    var profiles = community.SnapshotProfiles();
                    var targetProfile = profiles.Profiles.FirstOrDefault(profile =>
                        string.Equals(
                            profile.Login,
                            targetLogin,
                            StringComparison.OrdinalIgnoreCase));
                    if (targetProfile != null)
                    {
                        community.SetProfileName(
                            targetLogin,
                            targetProfile.DisplayName,
                            jobs.ListHistoricalAliasesForLogin(targetLogin),
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                    Logger.Log(
                        $"UI profile admin: '{authUser}' assigned legacy job {jobId} "
                        + $"('{expectedCreatedBy}') to authenticated account '{targetLogin}'.");
                    var latestProfiles = community.SnapshotProfiles();
                    return Results.Json(new
                    {
                        jobId = assigned.Id,
                        originalUser = assigned.CreatedBy,
                        ownerId = UiCommunityStore.PublicIdentityId(assigned.CreatorLogin),
                        user = latestProfiles.ResolveDisplay(
                            assigned.CreatorLogin,
                            assigned.CreatedBy),
                    });
                }
                catch (UiProfileNameConflictException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 409);
                }
                catch (Exception ex)
                {
                    Logger.Log(
                        $"UI profile admin: legacy assignment failed for {jobId}: {ex.Message}");
                    return Results.Json(
                        new { error = "The exact legacy attribution assignment failed." },
                        statusCode: 500);
                }
            });

            // Live (hydrated) job summaries. Full multi-day history is indexed
            // on disk and served through /api/archive — this endpoint no longer
            // returns every historical job as in-process objects.
            app.MapGet("/api/jobs", (HttpContext ctx) =>
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                var profileSnapshot = community.SnapshotProfiles();
                var list = jobs.ListChronological()
                    .Where(j => !visibility.IsPromptHidden(j.Id))
                    .Select(j => new
                    {
                        id = j.Id,
                        prompt = j.Prompt,
                        user = profileSnapshot.ResolveDisplay(j.CreatorLogin, j.CreatedBy),
                        originalUser = j.CreatedBy,
                        ownerId = UiCommunityStore.PublicIdentityId(j.CreatorLogin),
                        gens = j.GeneratorKeys,
                        hasImage = j.HasInputImage,
                        inputCount = j.InputImageCount,
                        done = j.IsDone,
                        sourceJobId = j.SourceJobId,
                        sourceGenerator = j.SourceGenerator,
                        sourceIndex = j.SourceIndex,
                        canHide = CanManageVisibility(j, authUser),
                        createdAtUnixMs = new DateTimeOffset(UiJobStorage.EnsureUtc(j.CreatedAt)).ToUnixTimeMilliseconds(),
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
                // Blank prompts are rejected below once the generator set is
                // known: a describe-only job may omit the prompt and gets the
                // standard describe instruction instead (declared pre-operation
                // default, recorded as the job's prompt).

                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    request.HttpContext.Items["micUser"] as string ?? "",
                    community,
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

                // Analysis targets = describe + layout map: both require an
                // attached image, and an analysis-only job may omit the
                // prompt. The composer applies the identical substitution so
                // the card shows exactly the recorded prompt (describe treats
                // it as the instruction; layout map as optional label
                // context).
                var analysisKeys = genKeys.Where(UiJobRunner.IsAnalysisKey).ToList();
                if (prompt.Length == 0)
                {
                    if (analysisKeys.Count == genKeys.Count)
                    {
                        prompt = UiJobRunner.DefaultDescribeInstruction;
                    }
                    else
                    {
                        return Results.BadRequest(new { error = "prompt is required" });
                    }
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
                Dictionary<string, string> generatorExtraTexts;
                try
                {
                    generatorExtraTexts = ParseGeneratorExtraTexts(form, genKeys);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
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
                // Analysis targets have no meaning without an image; reject the
                // job outright rather than running them against nothing.
                if (analysisKeys.Count > 0 && uploadFiles.Count == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = $"These targets require an attached image: {string.Join(", ", analysisKeys)}. Attach an image or deselect them.",
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
                            .Where(key => runner.IsImageCapableForCurrentSettings(key)
                                && !SupportsImageAspectOverride(runner, key))
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
                        CreatorLogin = request.HttpContext.Items["micUser"] as string ?? "",
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
                        GeneratorExtraTexts = generatorExtraTexts,
                    };
                    jobs.Add(job);
                    try
                    {
                        community.RecordGenerationStart(
                            job.CreatorLogin,
                            job.CreatedBy,
                            job.Id,
                            new DateTimeOffset(UiJobStorage.EnsureUtc(job.CreatedAt)).ToUnixTimeMilliseconds());
                    }
                    catch (Exception ex)
                    {
                        // Activity is secondary to the accepted generation:
                        // never turn a durable image job into a failed submit
                        // after its exact identity has already been registered.
                        Logger.Log($"UI activity: could not record generation start for {job.Id}: {ex.Message}");
                    }
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
                        generatorExtraTexts = spec.GeneratorExtraTexts,
                        // Keep the legacy gpt2 event fields while pre-feature
                        // browser windows remain possible. New clients use the
                        // keyed map above for every image endpoint.
                        gpt2GuidanceEnabled = spec.GeneratorExtraTexts.ContainsKey(UiJobRunner.KeyGpt2),
                        gpt2GuidanceText = spec.GeneratorExtraTexts.TryGetValue(
                            UiJobRunner.KeyGpt2,
                            out var gpt2ExtraText)
                            ? gpt2ExtraText
                            : "",
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
                    return Results.Json(new
                    {
                        id = job.Id,
                        user = createdBy,
                        originalUser = createdBy,
                        ownerId = UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                    });
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
                    community,
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
                if (visibility.IsPromptHidden(sourceJobId)
                    || visibility.IsImageHidden(sourceJobId, sourceGenerator, sourceIndex))
                {
                    return Results.NotFound(new { error = "The selected source image is hidden." });
                }
                // Local raws of hosted images are evicted in production; the
                // runner refetches the exact recorded B2 object (SHA-verified)
                // when the local copy is gone.
                var source = await runner.TryGetImageBytesIncludingHostedAsync(
                    sourceJob, sourceGenerator, sourceIndex);
                if (source == null)
                {
                    return Results.NotFound(new { error = "The selected source image is no longer available." });
                }
                var (sourceBytes, sourceContentType) = source.Value;
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

                var durationSeconds = 15;
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
                        CreatorLogin = request.HttpContext.Items["micUser"] as string ?? "",
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
                    try
                    {
                        community.RecordGenerationStart(
                            job.CreatorLogin,
                            job.CreatedBy,
                            job.Id,
                            new DateTimeOffset(UiJobStorage.EnsureUtc(job.CreatedAt)).ToUnixTimeMilliseconds());
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"UI activity: could not record video generation start for {job.Id}: {ex.Message}");
                    }
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
                    return Results.Json(new
                    {
                        id = job.Id,
                        user = videoCreatedBy,
                        originalUser = videoCreatedBy,
                        ownerId = UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                    });
                }
                finally
                {
                    admission?.Dispose();
                }
            });

            app.MapPost("/api/prompt/advice", async (HttpRequest request) =>
            {
                if (claudeService == null)
                {
                    return Results.BadRequest(new { error = claudeAdviceProblem });
                }
                var form = await request.ReadFormAsync();
                var prompt = form["prompt"].ToString();
                var instruction = form["instruction"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return Results.BadRequest(new { error = "prompt is empty" });
                }
                if (prompt.Length > 100_000)
                {
                    return Results.BadRequest(new { error = "prompt must be 100,000 characters or fewer" });
                }
                if (instruction.Length == 0 || instruction.Length > 4000)
                {
                    return Results.BadRequest(new { error = "Claude instruction must be between 1 and 4,000 characters" });
                }
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = authUser.Length > 0
                    ? "login:" + authUser
                    : "display:" + actorDisplay.ToUpperInvariant();
                var wirePrompt = ClaudeService.BuildPromptAdviceWirePrompt(instruction, prompt);
                var exchange = community.StartClaudePromptExchange(
                    identityKey,
                    actorDisplay,
                    ClaudeService.PromptAdviceModel,
                    instruction,
                    prompt,
                    ClaudeService.PromptAdviceSystemPrompt,
                    wirePrompt,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                string rawResponse;
                string replacement;
                string failure;
                try
                {
                    var result = await claudeService.GetPromptAdviceAsync(instruction, prompt);
                    rawResponse = result.RawResponse;
                    replacement = result.ResultPrompt;
                    failure = result.Error;
                }
                catch (Exception ex)
                {
                    rawResponse = "";
                    replacement = "";
                    failure = ex.Message;
                }
                community.CompleteClaudePromptExchange(
                    exchange.Id,
                    rawResponse,
                    replacement,
                    failure,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (failure.Length > 0)
                {
                    Logger.Log($"UI Claude advice {exchange.Id} failed for '{actorDisplay}': {failure}");
                    return Results.Json(
                        new { error = failure, exchangeId = exchange.Id },
                        statusCode: 502);
                }
                Logger.Log($"UI Claude advice {exchange.Id} completed for '{actorDisplay}'.");
                return Results.Json(new
                {
                    replacement,
                    exchangeId = exchange.Id,
                });
            });

            app.MapGet("/api/prompt/advice/history", (
                string? user,
                int? limit,
                HttpContext ctx) =>
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    user,
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = authUser.Length > 0
                    ? "login:" + authUser
                    : "display:" + actorDisplay.ToUpperInvariant();
                var records = community.ReadClaudePromptExchanges(identityKey, limit ?? 50);
                return Results.Json(new
                {
                    exchanges = records.Select(record => new
                    {
                        record.Id,
                        record.RequestedAtUnixMs,
                        record.CompletedAtUnixMs,
                        actor = record.ActorDisplay,
                        record.Model,
                        record.Instruction,
                        record.OriginalPrompt,
                        record.SystemPrompt,
                        record.WirePrompt,
                        record.RawResponse,
                        record.ResultPrompt,
                        record.Status,
                        record.Error,
                    }),
                });
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
            app.MapGet("/api/events/poll", (int? cursor, string? visibilityVersion, HttpContext ctx) =>
            {
                var (envelopes, nextCursor) = jobs.ReadEnvelopes(cursor ?? 0);
                var authUser = ctx.Items["micUser"] as string ?? "";
                var visibleEnvelopes = BuildVisibleEnvelopes(
                    envelopes,
                    jobs,
                    visibility,
                    authUser,
                    community.SnapshotProfiles());
                var visibilitySnapshot = visibility.Snapshot();
                var visibilityPayload = string.Equals(
                    visibilityVersion,
                    visibilitySnapshot.Version,
                    StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new
                    {
                        version = visibilitySnapshot.Version,
                        unchanged = true,
                    })
                    : JsonSerializer.Serialize(BuildVisibilityResponse(
                        visibilitySnapshot.Version,
                        visibilitySnapshot.Records));
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(
                    $"{{\"cursor\":{nextCursor},\"visibility\":{visibilityPayload},"
                    + $"\"envelopes\":[{string.Join(",", visibleEnvelopes)}]}}",
                    "application/json");
            });

            app.MapGet("/api/jobs/{id}/images/{gen}/{n:int}", (string id, string gen, int n, HttpContext ctx) =>
            {
                // A miss is transient (job unknown here, or bytes not landed
                // yet); never let a 404 become heuristically cacheable.
                ctx.Response.Headers.CacheControl = "no-store";
                var job = jobs.Get(id);
                if (job == null) return Results.NotFound();
                // In-process progression snapshots ("{gen}~p{k}") show the
                // same content as the result image they preview, so they share
                // its hide identity — hiding gpt2/0 must also hide gpt2~p*/0.
                var visibilityGen = UiJob.PartialSnapshotVisibilityGen(gen);
                if (visibility.IsPromptHidden(id)
                    || visibility.IsImageHidden(id, visibilityGen, n)
                    || (string.Equals(gen, "grid", StringComparison.Ordinal)
                        && visibility.HasHiddenImages(id)))
                {
                    return Results.NotFound();
                }

                // ?thumb=1 asks for the <=640px card preview; without it the
                // exact original bytes are served (viewer, new-tab, video
                // sources, set-active restore all use the plain URL).
                //
                // Cacheability is decided per image, not per job: a durable
                // on-disk path is only ever recorded once, when that gen/index's
                // TERMINAL bytes are saved — the final result, or the kept last
                // preview of a generation that failed after streaming partials
                // (in-flight partials live as path-less RAM bytes). Either way
                // the URL's bytes never change again, so path-backed responses
                // are immutable even while sibling generators of the same job
                // are still running. Without this,
                // reviewing another user's fresh multi-generator job re-fetched
                // every already-final multi-MB original on each viewer open and
                // navigation until the job's last generator landed.
                IResult fileResult;
                bool bytesAreFinal;
                if (ctx.Request.Query.ContainsKey("thumb"))
                {
                    if (job.TryGetCardPreviewPath(gen, n, out var thumbPath, out var thumbType))
                    {
                        // Disk-backed thumb, built from the durable final —
                        // stream, do not buffer into heap.
                        fileResult = Results.File(
                            Path.GetFullPath(thumbPath),
                            thumbType,
                            enableRangeProcessing: true);
                        bytesAreFinal = true;
                    }
                    else if (job.TryGetCardPreviewBytes(gen, n, out var bytes, out var contentType))
                    {
                        // Ephemeral streaming partials only.
                        fileResult = Results.File(bytes, contentType);
                        bytesAreFinal = false;
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
                    bytesAreFinal = true;
                }
                else if (job.TryGetImage(gen, n, out var memBytes, out var memType))
                {
                    // Ephemeral partials (no durable path yet).
                    fileResult = Results.File(memBytes, memType);
                    bytesAreFinal = false;
                }
                else
                {
                    return Results.NotFound();
                }

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
                foreach (var entry in jobs.ListInputLibraryCandidates()
                    .Where(entry => !visibility.IsPromptHidden(entry.Id)))
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
                            // TryPeekJobSummary already normalized this to UTC.
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
                var days = jobs.ListArchivedDays()
                    .Select(d =>
                    {
                        var visibleCount = d.JobIds.Count(id => !visibility.IsPromptHidden(id));
                        return (d.Day, Count: visibleCount);
                    })
                    .Where(d => d.Count > 0)
                    .Select(d => new
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
                var authUser = ctx.Items["micUser"] as string ?? "";
                var profileSnapshot = community.SnapshotProfiles();
                var dayJobs = jobs.ListArchivedDay(parsedDay)
                    .Where(job => !visibility.IsPromptHidden(job.Id))
                    .ToList();
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
                    sb.Append(SerializeJobMetadataForViewer(
                        job,
                        CanManageVisibility(job, authUser),
                        profileSnapshot));
                    sb.Append(",\"events\":[");
                    sb.Append(string.Join(
                        ",",
                        events
                            .Select(eventJson => BuildVisibleEventJson(job.Id, eventJson, visibility))
                            .Where(eventJson => eventJson != null)));
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
                var profileSnapshot = community.SnapshotProfiles();
                var users = jobs.ListUsers(profileSnapshot.ResolveDisplay)
                    .Select(u => new
                    {
                        user = u.User,
                        count = u.JobIds.Count(id => !visibility.IsPromptHidden(id)),
                    })
                    .Where(u => u.count > 0);
                return Results.Json(new { users });
            });

            // One-way stream hiding. Authorization comes only from the
            // authenticated account: the job's creator login, or the fixed
            // ernieMultiZone override. Browser-supplied display names never
            // grant this permission.
            app.MapPost("/api/visibility", async (HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (authUser.Length == 0)
                {
                    return Results.Json(
                        new { error = "Log in to hide a prompt or image." },
                        statusCode: 403);
                }

                var form = await request.ReadFormAsync();
                var kind = form["kind"].ToString().Trim();
                var jobId = form["jobId"].ToString().Trim();
                if (kind != "prompt" && kind != "image")
                {
                    return Results.BadRequest(new { error = "kind must be prompt or image." });
                }
                if (jobId.Length == 0)
                {
                    return Results.BadRequest(new { error = "jobId is required." });
                }

                var job = jobs.Get(jobId);
                if (job == null)
                {
                    return Results.NotFound(new { error = "The selected job is no longer available." });
                }
                if (!CanManageVisibility(job, authUser))
                {
                    return Results.Json(
                        new { error = "Only the authenticated creator can hide this item." },
                        statusCode: 403);
                }

                var generator = "";
                var imageIndex = -1;
                if (kind == "image")
                {
                    if (visibility.IsPromptHidden(jobId))
                    {
                        return Results.NotFound(new { error = "The selected job is hidden." });
                    }
                    generator = form["generator"].ToString().Trim();
                    if (generator.Length == 0
                        || !int.TryParse(form["imageIndex"].ToString(), out imageIndex)
                        || imageIndex < 0)
                    {
                        return Results.BadRequest(
                            new { error = "generator and a non-negative imageIndex are required." });
                    }
                    if (!TryResolveFavoriteImage(
                        job,
                        generator,
                        imageIndex,
                        out _,
                        out var imageError))
                    {
                        return Results.NotFound(new { error = imageError });
                    }
                }

                try
                {
                    visibility.Hide(new UiHiddenResource
                    {
                        Kind = kind,
                        JobId = jobId,
                        Generator = generator,
                        ImageIndex = imageIndex,
                        HiddenByLogin = authUser,
                        HiddenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    Logger.Log(
                        $"UI visibility: {authUser} hid {kind}/{jobId}"
                        + (kind == "image" ? $"/{generator}/{imageIndex}" : "") + ".");
                    var snapshot = visibility.Snapshot();
                    return Results.Json(BuildVisibilityResponse(
                        snapshot.Version,
                        snapshot.Records));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    Logger.Log($"UI visibility: could not persist hide operation: {ex.Message}");
                    return Results.Json(
                        new { error = "The hide operation could not be persisted." },
                        statusCode: 500);
                }
            });

            // Shared persistent image + prompt favorites. POST takes the
            // desired boolean state instead of "toggle", so a retried request
            // is idempotent. New records are accepted only after correlating
            // their exact identity to a persisted job/result. Removing an
            // existing record needs only its stored identity, so a user can
            // still clean it up after old job files are removed.
            app.MapGet("/api/favorites", (string? version, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                try
                {
                    var snapshot = favorites.Snapshot();
                    var visibilitySnapshot = visibility.Snapshot();
                    var profileSnapshot = community.SnapshotProfiles();
                    var combinedVersion = snapshot.Version
                        + "." + visibilitySnapshot.Version
                        + "." + profileSnapshot.Version;
                    if (string.Equals(version, combinedVersion, StringComparison.Ordinal))
                    {
                        return Results.Json(new { version = combinedVersion, unchanged = true });
                    }
                    var visibleRecords = snapshot.Records
                        .Where(record =>
                            !visibility.IsPromptHidden(record.JobId)
                            && (!string.Equals(record.Kind, "image", StringComparison.Ordinal)
                                || !visibility.IsImageHidden(
                                    record.JobId,
                                    record.Generator,
                                    record.ImageIndex)))
                        .ToList();
                    return Results.Json(BuildFavoritesResponse(
                        combinedVersion,
                        visibleRecords,
                        jobs,
                        ctx.Items["micUser"] as string ?? "",
                        profileSnapshot));
                }
                catch (InvalidDataException ex)
                {
                    Logger.Log($"UI favorites: listing failed: {ex.Message}");
                    return Results.Json(
                        new { error = "Favorites contain conflicting stored image identities." },
                        statusCode: 500);
                }
            });

            app.MapPost("/api/favorites", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var favoriteLogin = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    favoriteLogin,
                    community,
                    out var user,
                    out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }

                var jobId = form["jobId"].ToString().Trim();
                var kind = form["kind"].ToString().Trim();
                if (kind.Length == 0)
                {
                    kind = "image";
                }
                if (kind != "image" && kind != "prompt")
                {
                    return Results.BadRequest(new { error = "kind must be image or prompt." });
                }
                if (jobId.Length == 0)
                {
                    return Results.BadRequest(new { error = "jobId is required." });
                }
                if (visibility.IsPromptHidden(jobId))
                {
                    return Results.NotFound(new { error = "The selected job is hidden." });
                }
                if (!bool.TryParse(form["favorite"].ToString(), out var favorite))
                {
                    return Results.BadRequest(new { error = "favorite must be true or false." });
                }

                var generator = "";
                var imageIndex = -1;
                if (kind == "image")
                {
                    generator = form["generator"].ToString().Trim();
                    if (generator.Length == 0)
                    {
                        return Results.BadRequest(new { error = "generator is required for an image favorite." });
                    }
                    if (!int.TryParse(form["imageIndex"].ToString(), out imageIndex)
                        || imageIndex < 0)
                    {
                        return Results.BadRequest(
                            new { error = "imageIndex must be a non-negative integer for an image favorite." });
                    }
                    if (visibility.IsImageHidden(jobId, generator, imageIndex))
                    {
                        return Results.NotFound(new { error = "The selected image is hidden." });
                    }
                }

                try
                {
                    var records = kind == "image"
                        ? favorites.ListImage(jobId, generator, imageIndex)
                        : favorites.ListPrompt(jobId);
                    var existing = records
                        .FirstOrDefault(record =>
                            favoriteLogin.Length > 0
                                ? string.Equals(
                                    record.UserLogin,
                                    favoriteLogin,
                                    StringComparison.OrdinalIgnoreCase)
                                : record.UserLogin.Length == 0
                                    && string.Equals(record.User, user, StringComparison.Ordinal));
                    var addedFavorite = false;
                    UiJob? favoritedJob = null;

                    if (favorite && existing == null)
                    {
                        var job = jobs.Get(jobId);
                        if (job == null)
                        {
                            return Results.NotFound(new { error = "The selected job is no longer available." });
                        }
                        if (kind == "prompt")
                        {
                            existing = new UiFavoriteRecord
                            {
                                Kind = "prompt",
                                User = user,
                                UserLogin = favoriteLogin,
                                JobId = job.Id,
                                Prompt = job.Prompt,
                                CreatedBy = job.CreatedBy,
                                JobCreatedAtUnixMs = new DateTimeOffset(job.CreatedAt).ToUnixTimeMilliseconds(),
                                HasInputImage = job.HasInputImage,
                                FavoritedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        }
                        else
                        {
                            if (!TryResolveFavoriteImage(
                                    job,
                                    generator,
                                    imageIndex,
                                    out var image,
                                    out var imageError))
                            {
                                return Results.NotFound(new { error = imageError });
                            }
                            existing = new UiFavoriteRecord
                            {
                                Kind = "image",
                                User = user,
                                UserLogin = favoriteLogin,
                                JobId = job.Id,
                                Generator = generator,
                                ImageIndex = imageIndex,
                                GeneratorImageCount = image.GeneratorImageCount,
                                Prompt = job.Prompt,
                                CreatedBy = job.CreatedBy,
                                JobCreatedAtUnixMs = new DateTimeOffset(job.CreatedAt).ToUnixTimeMilliseconds(),
                                HasInputImage = job.HasInputImage,
                                ImageUrl = image.ImageUrl,
                                ThumbUrl = image.ThumbUrl,
                                Size = image.Size,
                                FavoritedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        }
                        favoritedJob = job;
                        addedFavorite = true;
                    }

                    if (existing != null)
                    {
                        favorites.Set(existing, favorite);
                        if (addedFavorite && favoritedJob != null)
                        {
                            try
                            {
                                community.RecordFavorite(
                                    request.HttpContext.Items["micUser"] as string ?? "",
                                    user,
                                    favoritedJob.CreatorLogin,
                                    favoritedJob.CreatedBy,
                                    favoritedJob.Id,
                                    kind,
                                    generator,
                                    imageIndex,
                                    existing.FavoritedAtUnixMs);
                            }
                            catch (Exception ex)
                            {
                                // The favorite itself is already durable and
                                // remains the source of truth. A notification
                                // write failure must not make a retry produce
                                // a false favorite failure or duplicate edge.
                                Logger.Log(
                                    $"UI activity: could not record favorite {user}/{kind}/{jobId}: {ex.Message}");
                            }
                        }
                    }

                    records = kind == "image"
                        ? favorites.ListImage(jobId, generator, imageIndex)
                        : favorites.ListPrompt(jobId);
                    var item = records.Count == 0
                        ? null
                        : kind == "image"
                            ? BuildFavoriteImageItem(
                                records,
                                CanManageVisibility(jobs.Get(jobId), request.HttpContext.Items["micUser"] as string ?? ""),
                                jobs.Get(jobId),
                                community.SnapshotProfiles())
                            : BuildFavoritePromptItem(
                                records,
                                CanManageVisibility(jobs.Get(jobId), request.HttpContext.Items["micUser"] as string ?? ""),
                                jobs.Get(jobId),
                                community.SnapshotProfiles());
                    return Results.Json(new { favorite, kind, item });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    Logger.Log(
                        $"UI favorites: could not persist {user}/{kind}/{jobId}/{generator}/{imageIndex}: "
                        + ex.Message);
                    return Results.Json(
                        new { error = "The favorite could not be persisted." },
                        statusCode: 500);
                }
            });

            // Shared social activity uses a bounded SQLite log and short
            // polling. Omitting `after` initializes a browser at the current
            // tail without replaying old alerts; subsequent polls use the
            // returned exact cursor.
            app.MapGet("/api/activity/poll", (long? after, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var result = community.ReadActivityAfter(after);
                var authUser = ctx.Items["micUser"] as string ?? "";
                var isDeveloper = IsDeveloperLogin(authUser);
                var profileSnapshot = community.SnapshotProfiles();
                var entries = result.Records
                    .Where(record =>
                        string.Equals(record.Audience, "all", StringComparison.Ordinal)
                        || (isDeveloper
                            && string.Equals(record.Audience, "developer", StringComparison.Ordinal)))
                    .Where(record =>
                        record.JobId.Length == 0
                        || (!visibility.IsPromptHidden(record.JobId)
                            && (record.ImageIndex < 0
                                || !visibility.IsImageHidden(
                                    record.JobId,
                                    record.Generator,
                                    record.ImageIndex))))
                    .Select(record => new
                    {
                        id = record.Id,
                        at = record.AtUnixMs,
                        kind = record.Kind,
                        actor = profileSnapshot.ResolveDisplay(
                            record.ActorLogin,
                            record.ActorDisplay),
                        target = profileSnapshot.ResolveDisplay(
                            record.TargetLogin,
                            record.TargetDisplay),
                        jobId = record.JobId,
                        generator = record.Generator,
                        imageIndex = record.ImageIndex,
                        resourceKind = record.ResourceKind,
                        isActor = authUser.Length > 0
                            && string.Equals(record.ActorLogin, authUser, StringComparison.Ordinal),
                        isTarget = authUser.Length > 0
                            && string.Equals(record.TargetLogin, authUser, StringComparison.Ordinal),
                    });
                return Results.Json(new
                {
                    cursor = result.Cursor,
                    reset = result.Reset,
                    entries,
                });
            });

            app.MapPost("/api/requests", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    request.HttpContext.Items["micUser"] as string ?? "",
                    community,
                    out var submitter,
                    out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }
                try
                {
                    var stored = community.SubmitRequest(
                        request.HttpContext.Items["micUser"] as string ?? "",
                        submitter,
                        form["body"].ToString(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log($"UI request: stored {stored.Id} from {submitter}.");
                    return Results.Json(new
                    {
                        id = stored.Id,
                        sequence = stored.Sequence,
                        submittedAtUnixMs = stored.SubmittedAtUnixMs,
                    });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI request: persistence failed for {submitter}: {ex.Message}");
                    return Results.Json(
                        new { error = "The request could not be stored." },
                        statusCode: 500);
                }
            });

            app.MapGet("/api/requests", (long? after, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var authUser = ctx.Items["micUser"] as string ?? "";
                if (!IsDeveloperLogin(authUser))
                {
                    return Results.Json(
                        new { error = "Developer access is required." },
                        statusCode: 403);
                }
                var result = community.ReadRequestsAfter(after ?? 0);
                var profiles = community.SnapshotProfiles();
                return Results.Json(new
                {
                    cursor = result.Cursor,
                    reset = result.Reset,
                    requests = result.Records.Select(record => new
                    {
                        sequence = record.Sequence,
                        id = record.Id,
                        submittedAtUnixMs = record.SubmittedAtUnixMs,
                        submitterLogin = record.SubmitterLogin,
                        submitter = profiles.ResolveDisplay(
                            record.SubmitterLogin,
                            record.SubmitterDisplay),
                        body = record.Body,
                    }),
                });
            });

            // Same zero-persistent-connection rule as /api/events/poll: the
            // logs panel used to hold an SSE connection per window, which
            // counted against the same 6-connection browser pool.
            app.MapGet("/api/logs/poll", (long? after, HttpContext ctx) =>
            {
                var buffered = Logger.ReadBuffered(after ?? 0).ToList();
                var next = buffered.Count > 0
                    ? buffered[^1].Sequence
                    : after ?? 0;
                var hiddenPromptIds = visibility.Snapshot().Records
                    .Where(record => string.Equals(record.Kind, "prompt", StringComparison.Ordinal))
                    .Select(record => record.JobId)
                    .ToList();
                var entries = buffered
                    .Where(entry => !hiddenPromptIds.Any(jobId =>
                        entry.Line.Contains($"[ui #{jobId}]", StringComparison.Ordinal)))
                    .Select(entry => new { sequence = entry.Sequence, line = entry.Line });
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { entries, next });
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

        private static bool CanManageVisibility(UiJob? job, string authUser)
        {
            if (job == null || authUser.Length == 0)
            {
                return false;
            }
            if (IsDeveloperLogin(authUser))
            {
                return true;
            }
            if (job.CreatorLogin.Length > 0)
            {
                return string.Equals(
                    authUser,
                    job.CreatorLogin,
                    StringComparison.OrdinalIgnoreCase);
            }
            // Jobs from before authenticated creator identity was persisted
            // cannot safely infer ownership from CreatedBy: that field was
            // browser-supplied attribution and could name another account.
            return false;
        }

        private static bool IsDeveloperLogin(string authUser)
            => authUser.Length > 0
                && string.Equals(
                    authUser,
                    VisibilityOverrideLogin,
                    StringComparison.OrdinalIgnoreCase);

        private static object BuildVisibilityResponse(
            string version,
            List<UiHiddenResource> records)
        {
            var prompts = records
                .Where(record => string.Equals(record.Kind, "prompt", StringComparison.Ordinal))
                .Select(record => record.JobId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(jobId => jobId, StringComparer.Ordinal)
                .ToList();
            var images = records
                .Where(record => string.Equals(record.Kind, "image", StringComparison.Ordinal))
                .OrderBy(record => record.JobId, StringComparer.Ordinal)
                .ThenBy(record => record.Generator, StringComparer.Ordinal)
                .ThenBy(record => record.ImageIndex)
                .Select(record => new
                {
                    jobId = record.JobId,
                    generator = record.Generator,
                    imageIndex = record.ImageIndex,
                })
                .ToList();
            return new { version, prompts, images };
        }

        private static List<string> BuildVisibleEnvelopes(
            List<string> envelopes,
            UiJobRegistry jobs,
            UiVisibilityStore visibility,
            string authUser,
            UiProfileSnapshot profiles)
        {
            var visible = new List<string>(envelopes.Count);
            foreach (var envelopeJson in envelopes)
            {
                var envelope = JsonNode.Parse(envelopeJson)?.AsObject()
                    ?? throw new InvalidDataException("UI envelope parsed to null.");
                var jobId = envelope["jobId"]?.GetValue<string>() ?? "";
                if (jobId.Length == 0 || visibility.IsPromptHidden(jobId))
                {
                    continue;
                }
                var kind = envelope["kind"]?.GetValue<string>() ?? "";
                if (string.Equals(kind, "job-known", StringComparison.Ordinal))
                {
                    var job = jobs.Get(jobId);
                    if (job == null)
                    {
                        continue;
                    }
                    var metadata = envelope["job"]?.AsObject();
                    if (metadata == null)
                    {
                        throw new InvalidDataException(
                            $"UI job-known envelope for {jobId} has no metadata.");
                    }
                    metadata["canHide"] = CanManageVisibility(job, authUser);
                    metadata["originalUser"] = job.CreatedBy;
                    metadata["ownerId"] = UiCommunityStore.PublicIdentityId(job.CreatorLogin);
                    metadata["user"] = profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy);
                    visible.Add(envelope.ToJsonString());
                    continue;
                }
                if (string.Equals(kind, "event", StringComparison.Ordinal))
                {
                    var eventNode = envelope["event"];
                    if (eventNode == null)
                    {
                        throw new InvalidDataException(
                            $"UI event envelope for {jobId} has no event.");
                    }
                    var visibleEvent = BuildVisibleEventJson(
                        jobId,
                        eventNode.ToJsonString(),
                        visibility);
                    if (visibleEvent != null)
                    {
                        visible.Add(
                            $"{{\"jobId\":{JsonSerializer.Serialize(jobId)},"
                            + $"\"kind\":\"event\",\"event\":{visibleEvent}}}");
                    }
                }
            }
            return visible;
        }

        private static string? BuildVisibleEventJson(
            string jobId,
            string eventJson,
            UiVisibilityStore visibility)
        {
            var evt = JsonNode.Parse(eventJson)?.AsObject()
                ?? throw new InvalidDataException(
                    $"UI event for {jobId} parsed to null.");
            var type = evt["type"]?.GetValue<string>() ?? "";
            if (string.Equals(type, "grid", StringComparison.Ordinal)
                && visibility.HasHiddenImages(jobId))
            {
                return null;
            }
            if (string.Equals(type, "gen-partial", StringComparison.Ordinal))
            {
                var generator = evt["gen"]?.GetValue<string>() ?? "";
                var imageIndex = evt["imageIndex"]?.GetValue<int>() ?? -1;
                return visibility.IsImageHidden(jobId, generator, imageIndex)
                    ? null
                    : eventJson;
            }
            if (!string.Equals(type, "gen-result", StringComparison.Ordinal)
                || !visibility.HasHiddenImages(jobId))
            {
                return eventJson;
            }

            var gen = evt["gen"]?.GetValue<string>() ?? "";
            if (evt["images"] is not JsonArray images)
            {
                return eventJson;
            }
            var thumbs = evt["thumbs"] as JsonArray;
            var changed = false;
            for (var index = 0; index < images.Count; index++)
            {
                if (!visibility.IsImageHidden(jobId, gen, index))
                {
                    continue;
                }
                images[index] = null;
                if (thumbs != null && index < thumbs.Count)
                {
                    thumbs[index] = null;
                }
                changed = true;
            }
            return changed ? evt.ToJsonString() : eventJson;
        }

        private static string SerializeJobMetadataForViewer(
            UiJob job,
            bool canHide,
            UiProfileSnapshot profiles)
        {
            var metadata = JsonNode.Parse(UiJobRegistry.SerializeJobMetadata(job))?.AsObject()
                ?? throw new InvalidDataException(
                    $"UI metadata for {job.Id} parsed to null.");
            metadata["canHide"] = canHide;
            metadata["originalUser"] = job.CreatedBy;
            metadata["ownerId"] = UiCommunityStore.PublicIdentityId(job.CreatorLogin);
            metadata["user"] = profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy);
            return metadata.ToJsonString();
        }

        private static Dictionary<string, string> ParseGeneratorExtraTexts(
            IFormCollection form,
            IReadOnlyCollection<string> selectedGeneratorKeys)
        {
            var selected = selectedGeneratorKeys.ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            // Deterministic compatibility for browser windows opened before
            // per-endpoint configuration existed. Their two legacy gpt2 fields
            // keep the old declared default and explicit off-switch semantics.
            if (!form.ContainsKey("generatorExtraTexts"))
            {
                if (!selected.Contains(UiJobRunner.KeyGpt2))
                {
                    return result;
                }
                var enabled = !string.Equals(
                    form["gpt2GuidanceEnabled"].ToString(),
                    "false",
                    StringComparison.OrdinalIgnoreCase);
                if (!enabled)
                {
                    return result;
                }
                var legacyText = form.ContainsKey("gpt2GuidanceText")
                    ? form["gpt2GuidanceText"].ToString().Trim()
                    : DefaultGpt2GuidanceText;
                result[UiJobRunner.KeyGpt2] = string.IsNullOrWhiteSpace(legacyText)
                    ? DefaultGpt2GuidanceText
                    : legacyText;
                return result;
            }

            var raw = form["generatorExtraTexts"].ToString();
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Per-generator extra text is not valid JSON: {ex.Message}",
                    ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        "Per-generator extra text must be a JSON object keyed by generator.");
                }

                var totalChars = 0;
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var key = property.Name;
                    if (!UiJobRunner.IsImageGeneratorKey(key))
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text contains unknown image generator '{key}'.");
                    }
                    if (!selected.Contains(key))
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text contains unselected generator '{key}'.");
                    }
                    if (!seenKeys.Add(key))
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text contains duplicate generator '{key}'.");
                    }
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text for '{key}' must be a string.");
                    }
                    var value = (property.Value.GetString() ?? "").Trim();
                    if (value.Length > MaxGeneratorExtraTextChars)
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text for '{key}' exceeds {MaxGeneratorExtraTextChars:N0} characters.");
                    }
                    totalChars += value.Length;
                    if (totalChars > MaxJobExtraTextTotalChars)
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text exceeds {MaxJobExtraTextTotalChars:N0} characters in total.");
                    }
                    if (value.Length > 0)
                    {
                        result.Add(key, value);
                    }
                }
            }
            return result;
        }

        private static bool TryResolveCreatorName(
            string? submitted,
            string authUser,
            UiCommunityStore community,
            out string name,
            out string error)
        {
            var profiles = community.SnapshotProfiles();
            var profile = profiles.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Login, authUser, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                name = profile.DisplayName;
                error = "";
                return true;
            }

            if (!TryNormalizeCreatorName(submitted, out name, out error))
            {
                if (name.Length == 0 && authUser.Length > 0)
                {
                    return TryNormalizeCreatorName(authUser, out name, out error);
                }
                return false;
            }
            if (authUser.Length > 0 && !community.IsDisplayNameAvailable(authUser, name))
            {
                error = $"The name '{name}' is reserved by another account.";
                return false;
            }
            return true;
        }

        private static bool TryNormalizeCreatorName(
            string? submitted,
            out string name,
            out string error)
        {
            name = System.Text.RegularExpressions.Regex.Replace(
                (submitted ?? "").Trim(),
                @"\s+",
                " ");
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

        private sealed class UiGeneratorPreferencesRequest
        {
            public bool? ShowImageSection { get; init; }
            public bool? ShowDescribeSection { get; init; }
            public List<string> HiddenGeneratorKeys { get; init; } = new();
            public List<string> DefaultSelectedKeys { get; init; } = new();
            public List<UiGeneratorPresetRequest> Presets { get; init; } = new();
            public List<UiGeneratorEndpointConfigurationRequest> EndpointConfigurations { get; init; } = new();
        }

        private sealed class UiGeneratorPresetRequest
        {
            public string Id { get; init; } = "";
            public string Name { get; init; } = "";
            public List<string> GeneratorKeys { get; init; } = new();
        }

        private sealed class UiGeneratorEndpointConfigurationRequest
        {
            public string Key { get; init; } = "";
            public string? ExtraText { get; init; }
            public string? Notes { get; init; }
        }

        private static UiGeneratorPreferencesRecord NormalizeGeneratorPreferences(
            string login,
            UiGeneratorPreferencesRequest submitted,
            UiJobRunner runner)
        {
            if (submitted.ShowImageSection == null || submitted.ShowDescribeSection == null)
            {
                throw new InvalidDataException(
                    "Generator preferences must explicitly state both section visibility choices.");
            }
            if (submitted.HiddenGeneratorKeys.Count > 100
                || submitted.DefaultSelectedKeys.Count > 100)
            {
                throw new InvalidDataException("Generator preference target lists are too large.");
            }
            if (submitted.Presets.Count > 20)
            {
                throw new InvalidDataException("At most 20 personal generator buttons may be saved.");
            }
            if (submitted.EndpointConfigurations.Count > UiJobRunner.ImageGeneratorKeys.Length)
            {
                throw new InvalidDataException("Too many per-endpoint generator configurations were submitted.");
            }

            static List<string> DistinctKeys(
                IEnumerable<string> values,
                UiJobRunner runner,
                string field)
            {
                var keys = values
                    .Select(value => value?.Trim() ?? "")
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                foreach (var key in keys)
                {
                    if (string.Equals(
                        runner.DescribeAvailabilityProblem(key),
                        $"unknown generator '{key}'",
                        StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"{field} contains unknown generator '{key}'.");
                    }
                }
                return keys;
            }

            var hidden = DistinctKeys(
                submitted.HiddenGeneratorKeys,
                runner,
                "hiddenGeneratorKeys");
            var selected = DistinctKeys(
                submitted.DefaultSelectedKeys,
                runner,
                "defaultSelectedKeys");
            var hiddenSet = hidden.ToHashSet(StringComparer.Ordinal);
            var hiddenSelected = selected.FirstOrDefault(hiddenSet.Contains);
            if (hiddenSelected != null)
            {
                throw new InvalidDataException(
                    $"Hidden generator '{hiddenSelected}' cannot also be selected by default.");
            }

            var presets = new List<UiGeneratorPresetRecord>();
            var presetIds = new HashSet<string>(StringComparer.Ordinal);
            var presetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in submitted.Presets)
            {
                var id = preset.Id.Trim();
                var name = preset.Name.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,64}$"))
                {
                    throw new InvalidDataException(
                        "Every personal generator button needs a stable 1-64 character id using letters, digits, _ or -.");
                }
                if (name.Length == 0 || name.Length > 30)
                {
                    throw new InvalidDataException(
                        "Personal generator button names must be between 1 and 30 characters.");
                }
                if (!presetIds.Add(id) || !presetNames.Add(name))
                {
                    throw new InvalidDataException(
                        "Personal generator button ids and names must be unique.");
                }
                var keys = DistinctKeys(
                    preset.GeneratorKeys,
                    runner,
                    $"preset '{name}'");
                var analysisKey = keys.FirstOrDefault(UiJobRunner.IsAnalysisKey);
                if (analysisKey != null)
                {
                    throw new InvalidDataException(
                        $"Personal image-generator button '{name}' cannot include analysis target '{analysisKey}'.");
                }
                var hiddenKey = keys.FirstOrDefault(hiddenSet.Contains);
                if (hiddenKey != null)
                {
                    throw new InvalidDataException(
                        $"Personal generator button '{name}' cannot include hidden target '{hiddenKey}'.");
                }
                presets.Add(new UiGeneratorPresetRecord
                {
                    Id = id,
                    Name = name,
                    GeneratorKeys = keys,
                });
            }

            var endpointConfigurations = new List<UiGeneratorEndpointConfigurationRecord>();
            var configuredKeys = new HashSet<string>(StringComparer.Ordinal);
            var configurationChars = 0;
            foreach (var configuration in submitted.EndpointConfigurations)
            {
                var key = configuration.Key.Trim();
                if (!UiJobRunner.IsImageGeneratorKey(key))
                {
                    throw new InvalidDataException(
                        $"Per-endpoint configuration contains unknown image generator '{key}'.");
                }
                if (!configuredKeys.Add(key))
                {
                    throw new InvalidDataException(
                        $"Per-endpoint configuration contains duplicate generator '{key}'.");
                }
                if (configuration.ExtraText == null && configuration.Notes == null)
                {
                    throw new InvalidDataException(
                        $"Per-endpoint configuration for '{key}' contains no overrides.");
                }
                if (configuration.ExtraText?.Length > MaxGeneratorExtraTextChars)
                {
                    throw new InvalidDataException(
                        $"Extra text for '{key}' exceeds {MaxGeneratorExtraTextChars:N0} characters.");
                }
                if (configuration.Notes?.Length > MaxGeneratorNotesChars)
                {
                    throw new InvalidDataException(
                        $"Private notes for '{key}' exceed {MaxGeneratorNotesChars:N0} characters.");
                }
                configurationChars += configuration.ExtraText?.Length ?? 0;
                configurationChars += configuration.Notes?.Length ?? 0;
                if (configurationChars > MaxGeneratorConfigurationTotalChars)
                {
                    throw new InvalidDataException(
                        $"Per-endpoint configuration exceeds {MaxGeneratorConfigurationTotalChars:N0} characters in total.");
                }
                endpointConfigurations.Add(new UiGeneratorEndpointConfigurationRecord
                {
                    Key = key,
                    ExtraText = configuration.ExtraText,
                    Notes = configuration.Notes,
                });
            }

            return new UiGeneratorPreferencesRecord
            {
                Login = login,
                ShowImageSection = submitted.ShowImageSection.Value,
                ShowDescribeSection = submitted.ShowDescribeSection.Value,
                HiddenGeneratorKeys = hidden,
                DefaultSelectedKeys = selected,
                Presets = presets,
                EndpointConfigurations = endpointConfigurations,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        private sealed class FavoriteImageDescriptor
        {
            public required string ImageUrl { get; init; }
            public required string ThumbUrl { get; init; }
            public string Size { get; init; } = "";
            public int GeneratorImageCount { get; init; }
        }

        private static bool TryResolveFavoriteImage(
            UiJob job,
            string generator,
            int imageIndex,
            out FavoriteImageDescriptor image,
            out string error)
        {
            FavoriteImageDescriptor? found = null;
            var (events, _) = job.ReadFrom(0);
            foreach (var eventJson in events)
            {
                using var document = JsonDocument.Parse(eventJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "gen-result", StringComparison.Ordinal)
                    || !root.TryGetProperty("gen", out var gen)
                    || !string.Equals(gen.GetString(), generator, StringComparison.Ordinal)
                    || !root.TryGetProperty("ok", out var ok)
                    || ok.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if ((root.TryGetProperty("resultKind", out var resultKind)
                        && !string.Equals(resultKind.GetString(), "image", StringComparison.Ordinal))
                    || (root.TryGetProperty("mediaType", out var mediaType)
                        && (mediaType.GetString() ?? "").StartsWith(
                            "video/",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Only successful image results can be favorited.";
                    image = null!;
                    return false;
                }
                if (!root.TryGetProperty("images", out var images)
                    || images.ValueKind != JsonValueKind.Array
                    || imageIndex >= images.GetArrayLength())
                {
                    continue;
                }

                var imageUrl = images[imageIndex].GetString() ?? "";
                if (imageUrl.Length == 0)
                {
                    error = "The selected image result has no recorded URL.";
                    image = null!;
                    return false;
                }

                var thumbUrl = "";
                if (root.TryGetProperty("thumbs", out var thumbs)
                    && thumbs.ValueKind == JsonValueKind.Array
                    && imageIndex < thumbs.GetArrayLength())
                {
                    thumbUrl = thumbs[imageIndex].GetString() ?? "";
                }
                if (thumbUrl.Length == 0 && imageUrl.StartsWith("/", StringComparison.Ordinal))
                {
                    thumbUrl = imageUrl + (imageUrl.Contains('?') ? "&thumb=1" : "?thumb=1");
                }
                if (thumbUrl.Length == 0)
                {
                    error = "The selected hosted image has no exact recorded card thumbnail.";
                    image = null!;
                    return false;
                }

                var candidate = new FavoriteImageDescriptor
                {
                    ImageUrl = imageUrl,
                    ThumbUrl = thumbUrl,
                    Size = root.TryGetProperty("size", out var size) ? size.GetString() ?? "" : "",
                    GeneratorImageCount = images.GetArrayLength(),
                };
                if (found != null)
                {
                    error = "The selected image identity has more than one successful result event.";
                    image = null!;
                    return false;
                }
                found = candidate;
            }

            if (found == null)
            {
                error = "The selected image result is no longer available.";
                image = null!;
                return false;
            }
            image = found;
            error = "";
            return true;
        }

        private static object BuildFavoritesResponse(
            string version,
            List<UiFavoriteRecord> records,
            UiJobRegistry jobs,
            string authUser,
            UiProfileSnapshot profiles)
        {
            var imageItems = records
                .Where(record => record.Kind == "image")
                .GroupBy(
                    record => (record.JobId, record.Generator, record.ImageIndex),
                    record => record)
                .Select(group => BuildFavoriteImageItem(
                    group.ToList(),
                    CanManageVisibility(jobs.Get(group.Key.JobId), authUser),
                    jobs.Get(group.Key.JobId),
                    profiles))
                .ToList();
            var promptItems = records
                .Where(record => record.Kind == "prompt")
                .GroupBy(record => record.JobId, StringComparer.Ordinal)
                .Select(group => BuildFavoritePromptItem(
                    group.ToList(),
                    CanManageVisibility(jobs.Get(group.Key), authUser),
                    jobs.Get(group.Key),
                    profiles))
                .ToList();
            var users = records
                .GroupBy(
                    record => profiles.ResolveDisplay(record.UserLogin, record.User),
                    StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    user = group.Key,
                    count = group.Count(),
                    imageCount = group.Count(record => record.Kind == "image"),
                    promptCount = group.Count(record => record.Kind == "prompt"),
                })
                .ToList();
            return new
            {
                version,
                unchanged = false,
                favorites = imageItems,
                promptFavorites = promptItems,
                users,
            };
        }

        private static object BuildFavoriteImageItem(
            List<UiFavoriteRecord> records,
            bool canHide,
            UiJob? job = null,
            UiProfileSnapshot? profiles = null)
        {
            if (records.Count == 0)
            {
                throw new InvalidDataException("Cannot build an empty favorite item.");
            }
            var first = records[0];
            if (records.Any(record =>
                record.Kind != "image"
                || !string.Equals(record.JobId, first.JobId, StringComparison.Ordinal)
                || !string.Equals(record.Generator, first.Generator, StringComparison.Ordinal)
                || record.ImageIndex != first.ImageIndex
                || record.GeneratorImageCount != first.GeneratorImageCount
                || !string.Equals(record.Prompt, first.Prompt, StringComparison.Ordinal)
                || !string.Equals(record.CreatedBy, first.CreatedBy, StringComparison.Ordinal)
                || record.JobCreatedAtUnixMs != first.JobCreatedAtUnixMs
                || record.HasInputImage != first.HasInputImage
                || !string.Equals(record.ImageUrl, first.ImageUrl, StringComparison.Ordinal)
                || !string.Equals(record.ThumbUrl, first.ThumbUrl, StringComparison.Ordinal)
                || !string.Equals(record.Size, first.Size, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Favorite records disagree for {first.JobId}/{first.Generator}/{first.ImageIndex}.");
            }

            return new
            {
                jobId = first.JobId,
                generator = first.Generator,
                imageIndex = first.ImageIndex,
                generatorImageCount = first.GeneratorImageCount,
                prompt = first.Prompt,
                createdBy = job != null && profiles != null
                    ? profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy)
                    : first.CreatedBy,
                ownerId = job == null
                    ? ""
                    : UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                jobCreatedAtUnixMs = first.JobCreatedAtUnixMs,
                hasInputImage = first.HasInputImage,
                imageUrl = first.ImageUrl,
                thumbUrl = first.ThumbUrl,
                size = first.Size,
                canHide,
                users = records
                    .Select(record => profiles?.ResolveDisplay(
                        record.UserLogin,
                        record.User) ?? record.User)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private static object BuildFavoritePromptItem(
            List<UiFavoriteRecord> records,
            bool canHide,
            UiJob? job = null,
            UiProfileSnapshot? profiles = null)
        {
            if (records.Count == 0)
            {
                throw new InvalidDataException("Cannot build an empty prompt favorite item.");
            }
            var first = records[0];
            if (records.Any(record =>
                record.Kind != "prompt"
                || !string.Equals(record.JobId, first.JobId, StringComparison.Ordinal)
                || !string.Equals(record.Prompt, first.Prompt, StringComparison.Ordinal)
                || !string.Equals(record.CreatedBy, first.CreatedBy, StringComparison.Ordinal)
                || record.JobCreatedAtUnixMs != first.JobCreatedAtUnixMs
                || record.HasInputImage != first.HasInputImage))
            {
                throw new InvalidDataException(
                    $"Prompt favorite records disagree for job {first.JobId}.");
            }

            return new
            {
                jobId = first.JobId,
                prompt = first.Prompt,
                createdBy = job != null && profiles != null
                    ? profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy)
                    : first.CreatedBy,
                ownerId = job == null
                    ? ""
                    : UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                jobCreatedAtUnixMs = first.JobCreatedAtUnixMs,
                hasInputImage = first.HasInputImage,
                canHide,
                users = records
                    .Select(record => profiles?.ResolveDisplay(
                        record.UserLogin,
                        record.User) ?? record.User)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
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
