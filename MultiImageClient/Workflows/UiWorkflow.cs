#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    ///   GET  /api/events/poll?cursor=N&clientInstance=...  cursor-based poll over every job's envelope log
    ///                                          (cursor=0 replays the full history, so refresh-safe;
    ///                                          stale/missing process identity returns 401: the page replays
    ///                                          from 0 for the same committed build, else reloads;
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
    ///   POST /api/visibility                   creator-only prompt/image hiding plus artifact deletion
    ///   GET  /api/jobs/{id}/location           live vs archive-day home for a share link
    ///   GET  /api/discord/vibecoders           identities already sent to #vibecoders
    ///   POST /api/discord/vibecoders           send one original attachment without private site links
    ///   POST /api/auth/login|logout            shared-site access gate (only when UiAuthFilePath is set)
    public partial class UiWorkflow
    {
        private const string VisibilityOverrideLogin = "ernieMultiZone";
        private const string LocalVisibilityActor = "local-instance-owner";

        private static bool SupportsImageAspectOverride(string key)
            => UiTargetCatalog.Find(key)?.ImageAspectOverride == true;

        private const string DefaultGpt2GuidanceText = UiTargetCatalog.DefaultGptAntiMurkGuidanceText;
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
            UiDiscordVibecodersStore vibecoders;
            try
            {
                favorites = new UiFavoriteStore(settings);
                visibility = new UiVisibilityStore(settings);
                community = new UiCommunityStore(settings);
                var accountLists = AddIdeogram45ToAccountDefaultsOnce(community);
                if (accountLists > 0)
                {
                    Logger.Log($"UI defaults: added Ideogram 4.5 to {accountLists} saved account default lists.");
                }
                vibecoders = new UiDiscordVibecodersStore(settings);
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
            if (auth?.LoginLinks?.Defaults()?.Any(key => !UiJobRunner.IsConfigurableEndpointKey(key)) == true)
                throw new InvalidDataException("The environment has an unknown default provider key.");
            await PurgePendingHiddenArtifactsAsync(
                jobs,
                favorites,
                visibility,
                runner);

            // User-directed prompt editing through Claude. The exact system
            // prompt, instruction, source prompt, wire prompt, response, and
            // outcome are persisted by UiCommunityStore for audit/history.
            var claudeAdviceProblem = ProviderKeyValidator.DescribeTextKeyProblem(
                nameof(settings.AnthropicApiKey), settings.AnthropicApiKey);
            var claudeAdviceLabel = ManagerCatalog.All.Single(m => m.Model == DirectedPromptRewrite.AdviceModel).Label;

            // Goal loops: manager-model-driven iterative design → render →
            // review conversations. Each render is an ordinary UiJob (cards,
            // thumbs, B2, deletion all apply); the loop itself persists under
            // UiGoalLoops/. See docs/goal-loop-prd.md.
            UiGoalLoopRegistry goalLoops;
            try
            {
                goalLoops = new UiGoalLoopRegistry(settings);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"UI aborted: goal loop history could not be loaded: {ex.Message}");
                return;
            }
            var goalLoopRunner = new UiGoalLoopRunner(
                settings,
                jobs,
                runner,
                goalLoops,
                onRenderJobCreated: job =>
                {
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
                        Logger.Log($"UI activity: could not record generation start for goal-loop render {job.Id}: {ex.Message}");
                    }
                });

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var url = $"http://127.0.0.1:{options.UiPort}";
            if (settings.UiIdleTimeoutSeconds != 0 && (settings.UiIdleTimeoutSeconds is < 60 or > 86400))
                throw new InvalidOperationException("UiIdleTimeoutSeconds must be zero or between 60 and 86400.");
            if (UiIdleLifetime.UseSystemdSocket(options.UiPort, settings.UiIdleTimeoutSeconds > 0))
                builder.WebHost.ConfigureKestrel(server => server.ListenHandle(3));
            else
                builder.WebHost.UseUrls(url);

            var app = builder.Build();
            if (auth == null)
                app.Use(UiLocalRequestGuard.Middleware);
            var idleLifetime = settings.UiIdleTimeoutSeconds == 0 ? null
                : new UiIdleLifetime(TimeSpan.FromSeconds(settings.UiIdleTimeoutSeconds));
            if (idleLifetime != null)
                app.Use(async (ctx, next) =>
                {
                    if (!idleLifetime.TryEnter())
                    {
                        ctx.Response.StatusCode = 503;
                        ctx.Response.Headers.RetryAfter = "1";
                        return;
                    }
                    try { await next(ctx); }
                    finally { idleLifetime.Exit(ctx.Request.Path != "/healthz"); }
                });
            var environments = string.IsNullOrWhiteSpace(settings.UiEnvironmentRegistryPath) ? null : new UiEnvironmentRegistry(settings.UiEnvironmentRegistryPath);
            if (environments != null) environments.Get(settings.UiEnvironmentId);
            try
            {
                var environmentLists = AddIdeogram45ToEnvironmentDefaultsOnce(settings, community, environments, auth?.LoginLinks);
                if (environmentLists > 0)
                {
                    Logger.Log($"UI defaults: added Ideogram 4.5 to {environmentLists} environment default lists.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"UI aborted: environment default lists could not be migrated: {ex.Message}");
                return;
            }
            // Every process instance gets a new token; stale and pre-token
            // clients receive 401 from the always-running job poll. The page
            // then reads /api/config: for the same committed build it adopts
            // the new token and replays from cursor 0, and otherwise it
            // reloads. Durable jobs, prompts, media, and history stay on disk
            // and hydrate again either way.
            var clientInstanceId = Guid.NewGuid().ToString("N");
            var accountActivity = auth == null ? null : new UiAccountActivity(settings.ImageDownloadBaseFolder);
            if (auth?.LoginLinks != null)
            {
                foreach (var transfer in auth.LoginLinks.PasswordAccountTransfers())
                {
                    jobs.RewriteCreatorLogin(transfer.From, transfer.To);
                    goalLoops.RewriteCreatorLogin(transfer.From, transfer.To);
                    community.TransferLogin(transfer.From, transfer.To);
                    favorites.TransferLogin(transfer.From, transfer.To);
                    accountActivity?.TransferLogin(transfer.From, transfer.To);
                }
            }

            // ---- access gate (shared deployments only) ----
            // Runs before static files and every endpoint. Unauthenticated
            // API calls get 401 JSON; unauthenticated page loads get the
            // inline login page. Login and the content-free loopback liveness
            // probe and scoped public shares are the only anonymous routes. When auth is off (blank
            // UiAuthFilePath) this middleware is not registered.
            if (auth != null)
            {
                app.Use(async (ctx, next) =>
                {
                    var path = ctx.Request.Path.Value ?? "/";
                    if (path.StartsWith("/public/", StringComparison.Ordinal)
                        && !UiPublicShares.IsPublicRequest(path, ctx.Request.Method))
                    {
                        ctx.Response.StatusCode = 404;
                        return;
                    }
                    if (((path == "/api/auth/login" || path == "/api/auth/logout") && HttpMethods.IsPost(ctx.Request.Method))
                        || (auth.LoginLinks != null && path == "/api/auth/link" && HttpMethods.IsPost(ctx.Request.Method))
                        || (auth.LoginLinks != null && path == "/enter.html" && HttpMethods.IsGet(ctx.Request.Method))
                        || (path == "/healthz" && HttpMethods.IsGet(ctx.Request.Method))
                        || UiPublicShares.IsPublicRequest(path, ctx.Request.Method))
                    {
                        await next();
                        return;
                    }
                    if (!auth.IsEnforced)
                    {
                        await next();
                        return;
                    }
                    if (auth.TryValidateCookie(ctx.Request.Cookies[auth.SessionCookieName], out var user))
                    {
                        ctx.Items["micUser"] = user;
                        var admin = user == UiLoginLinks.OwnerLogin;
                        if (environments != null && !environments.CanEnter(settings.UiEnvironmentId, user))
                        {
                            ctx.Response.StatusCode = 403;
                            ctx.Response.Headers.CacheControl = "no-store";
                            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                                await ctx.Response.WriteAsJsonAsync(new { error = "Your account does not have access to this environment." });
                            else
                            {
                                ctx.Response.ContentType = "text/html; charset=utf-8";
                                await ctx.Response.WriteAsync("<h1>Environment access</h1><p>Your account does not have access to this environment.</p><button onclick=\"fetch('api/auth/logout',{method:'POST'}).then(()=>location.reload())\">Switch account</button>");
                            }
                            return;
                        }
                        var policy = environments?.Get(settings.UiEnvironmentId);
                        if ((!admin && (path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/api/control/", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/api/people", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/api/environment/", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith("/api/logs/", StringComparison.OrdinalIgnoreCase)
                                || path is "/people.html" or "/admin.html"))
                            || (policy != null && ((!policy.GoalLoops && (path.StartsWith("/api/goal-loops", StringComparison.OrdinalIgnoreCase) || path is "/goal.html" or "/recap.html"))
                                || (!policy.Video && path.StartsWith("/api/video-jobs", StringComparison.OrdinalIgnoreCase))
                                || (!policy.PromptRewrite && path.StartsWith("/api/prompt/advice", StringComparison.OrdinalIgnoreCase))
                                || (!policy.PromptRewrite && path.StartsWith("/api/prompt/condense", StringComparison.OrdinalIgnoreCase))
                                || (!policy.PromptRewrite && path.StartsWith("/api/prompt/split", StringComparison.OrdinalIgnoreCase))
                                || (!policy.PromptRewrite && path.StartsWith("/api/prompt/subjects/synthesize", StringComparison.OrdinalIgnoreCase))
                                || (!policy.AllowVibecoders && path.StartsWith("/api/discord/vibecoders", StringComparison.OrdinalIgnoreCase)))))
                        {
                            ctx.Response.StatusCode = 403;
                            await ctx.Response.WriteAsJsonAsync(new { error = "This feature is not available for this account or environment." });
                            return;
                        }
                        if (settings.UiEnvironmentId.Length > 0 && ctx.Request.Headers.TryGetValue("X-Mic-Identity", out var identity)
                            && identity != UiEnvironmentEndpoints.Identity(ctx, auth))
                        {
                            ctx.Response.StatusCode = 401;
                            await ctx.Response.WriteAsJsonAsync(new { error = "Account changed; reload this page." });
                            return;
                        }
                        if (path == "/" || path == "/index.html") accountActivity?.Record(user);
                        await next();
                        if (ctx.Response.StatusCode < 300 && HttpMethods.IsPost(ctx.Request.Method)) accountActivity?.Record(user);
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
                    ctx.Response.Cookies.Append(auth.SessionCookieName, cookieValue, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Secure = IsEffectivelyHttps(ctx),
                        MaxAge = TimeSpan.FromDays(3650),
                        Path = auth.SessionCookiePath,
                    });
                    accountActivity?.Record(username, true);
                    Logger.Log($"UI auth: '{username}' logged in from {ClientIpForThrottle(ctx)}.");
                    return Results.Json(new { ok = true, username });
                });

                app.MapPost("/api/auth/logout", (HttpContext ctx) =>
                {
                    ctx.Response.Cookies.Delete(auth.SessionCookieName, new CookieOptions { Path = auth.SessionCookiePath });
                    return Results.Json(new { ok = true });
                });
            }

            // Intentionally content-free and authentication-independent. The
            // in-process guard calls this through a raw loopback socket to prove
            // that Kestrel, routing, and middleware can still complete work.
            UiEnvironmentEndpoints.Map(app, settings, auth, community, runner, wwwroot, environments, accountActivity, jobs, goalLoops, favorites);

            app.MapGet("/api/admin/summary", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if ((ctx.Items["micUser"] as string) != UiLoginLinks.OwnerLogin) return Results.StatusCode(403);
                return Results.Json(new { accounts = accountActivity?.Snapshot(), generations = jobs.AccountGenerationSummary() });
            });
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
                var environmentDefaults = environments?.Get(settings.UiEnvironmentId).DefaultGenerators ?? auth?.LoginLinks?.Defaults();
                var managerPricesAt = DateTimeOffset.UtcNow;
                var standardGeneratorGroups = UiTargetCatalog.StandardGroups
                    .Select(group => new
                    {
                        id = group.Id,
                        name = group.Name,
                        generatorKeys = group.GeneratorKeys,
                    })
                    .ToArray();

                var generators = UiTargetCatalog.UiConfigTargets
                    .Select(g => new
                    {
                        key = g.Key,
                        label = g.DisplayName,
                        detail = g.Detail,
                        standardGroupIds = UiTargetCatalog.StandardGroupIdsFor(g.Key),
                        available = runner.IsAvailable(g.Key),
                        availabilityProblem = runner.DescribeAvailabilityProblem(g.Key),
                        imageCapable = g.ImageCapable || g.IsAnalysis,
                        imageAspectOverride = g.ImageAspectOverride,
                        sketchCapable = g.SketchCapable,
                        kind = g.IsAnalysis ? "describe" : "image",
                        requiresImage = g.IsAnalysis,
                        goalLoopProblem = UiGoalLoopRunner.GoalLoopExclusion(g.Key),
                        maxPromptChars = g.MaxPromptChars(imageSent: false),
                        maxImagePromptChars = g.MaxPromptChars(imageSent: true),
                        maxPromptUtf8Bytes = g.MaxPromptUtf8Bytes,
                        maxInputImages = g.MaxInputImages,
                        defaultExtraText = g.UsesGptAntiMurkDefault ? DefaultGpt2GuidanceText : "",
                        defaultNotes = "",
                        defaultOn = environmentDefaults != null
                            ? environmentDefaults.Contains(g.Key)
                            : g.DefaultSelected,
                    })
                    // Stable sort: available targets keep catalog order,
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
                    socketActivationSupported = true,
                    generators,
                    standardGeneratorGroups = standardGeneratorGroups.Select(group => new
                    {
                        group.id,
                        group.name,
                    }),
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
                        key = DirectedPromptRewrite.DefaultAdviceKey,
                        model = DirectedPromptRewrite.AdviceModel,
                        label = claudeAdviceLabel,
                    },
                    promptRewrites = DirectedPromptRewrite.Models.Select(model =>
                    {
                        var def = ManagerCatalog.All.Single(m => m.Model == model);
                        return new
                        {
                            key = def.Key,
                            model,
                            label = def.Label,
                            available = DirectedPromptRewrite.AvailabilityProblem(model, settings) == null,
                            availabilityProblem = DirectedPromptRewrite.AvailabilityProblem(model, settings),
                        };
                    }),
                    promptCondense = new
                    {
                        key = DirectedPromptRewrite.DefaultCondenseKey,
                        model = DirectedPromptRewrite.CondenseModel,
                        label = ManagerCatalog.All.Single(m => m.Model == DirectedPromptRewrite.CondenseModel).Label,
                        available = DirectedPromptRewrite.AvailabilityProblem(DirectedPromptRewrite.CondenseModel, settings) == null,
                        availabilityProblem = DirectedPromptRewrite.AvailabilityProblem(DirectedPromptRewrite.CondenseModel, settings),
                        minCondensedPromptChars = UiJobRunner.MinCondensedPromptChars,
                    },
                    promptSplit = PromptSplit.Config(settings),
                    promptAssistantModels = ManagerCatalog.All.Select(m => new
                    {
                        key = m.Key,
                        model = m.Model,
                        label = m.Label,
                        provider = m.Provider,
                        detail = m.Detail,
                        available = ManagerCatalog.DescribeAvailabilityProblem(m, settings) == null,
                        availabilityProblem = ManagerCatalog.DescribeAvailabilityProblem(m, settings),
                        inputUsdPerMTok = m.PricesAt(managerPricesAt).InputUsdPerMTok,
                        outputUsdPerMTok = m.PricesAt(managerPricesAt).OutputUsdPerMTok,
                    }),
                    promptAssistantDefaults = new
                    {
                        advice = DirectedPromptRewrite.DefaultAdviceKey,
                        fleshOutPrimary = DirectedPromptRewrite.DefaultFleshOutPrimaryKey,
                        fleshOutSecondary = DirectedPromptRewrite.DefaultFleshOutSecondaryKey,
                        condense = DirectedPromptRewrite.DefaultCondenseKey,
                        split = PromptSplit.ModelKey,
                        characterWriter = PromptSubjectSynthesis.DefaultModelKey,
                    },
                    // Goal loop manager catalog + limits, consumed by goal.html.
                    // Generators come from the `generators` list above (image
                    // kind, available, no goalLoopProblem).
                    goalLoop = new
                    {
                        protocolVersion = UiGoalLoopProtocol.Version,
                        defaultMaxCandidates = UiGoalLoopFanout.DefaultMaxCandidates,
                        maxCandidatesCap = UiGoalLoopFanout.MaxCandidatesCap,
                        defaultSamplePolicy = UiGoalLoopSampling.DefaultPolicy,
                        maxImagesPerRound = UiGoalLoopSampling.MaxImagesPerRound,
                        feedbackEnabled = true,
                        managers = ManagerCatalog.All.Select(m => new
                        {
                            m.Key,
                            m.Label,
                            m.Provider,
                            m.Model,
                            m.Detail,
                            available = ManagerCatalog.DescribeAvailabilityProblem(m, settings) == null,
                            availabilityProblem = ManagerCatalog.DescribeAvailabilityProblem(m, settings),
                            inputUsdPerMTok = m.PricesAt(managerPricesAt).InputUsdPerMTok,
                            outputUsdPerMTok = m.PricesAt(managerPricesAt).OutputUsdPerMTok,
                        }),
                        defaultManagerKey = ManagerCatalog.DefaultKey,
                        defaultMaxTurns = UiGoalLoopRunner.DefaultMaxTurns,
                        maxTurnsCap = UiGoalLoopRunner.MaxTurnsCap,
                        maxGoalChars = UiGoalLoopRunner.MaxGoalChars,
                        maxEditedTextChars = UiGoalLoopRunner.MaxEditedTextChars,
                        maxGenerators = UiGoalLoopSources.MaxGenerators,
                        maxCritics = UiGoalLoopCritics.MaxCritics,
                        runningCount = goalLoopRunner.RunningCount,
                    },
                    generatorPreferences = generatorPreferences == null
                        ? null
                        : new
                        {
                            showImageSection = generatorPreferences.ShowImageSection,
                            showDescribeSection = generatorPreferences.ShowDescribeSection,
                            defaultView = generatorPreferences.DefaultView,
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
                    clientInstanceId,
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
                        localVisibilityManagement = auth == null,
                        user = authUser,
                        canManagePeople = auth?.LoginLinks != null && authUser == UiLoginLinks.OwnerLogin,
                        role = authUser == UiLoginLinks.OwnerLogin ? "admin" : "normal",
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
                    fableBot = new
                    {
                        available = FableBot.FableBotDiscord.TryNormalizeBotToken(settings.FableBotDiscordBotToken, out _)
                            && FableBot.FableBotDiscord.TryNormalizeChannelId(settings.FableBotDiscordChannelId, out _),
                    },
                    vibecoders = new
                    {
                        available = DiscordVibecoders.IsConfigured(settings) && (environments?.Get(settings.UiEnvironmentId).AllowVibecoders ?? true),
                    },
                    shareLinks = new { available = ShareLinksProblem(settings, auth) == null },
                    promptSubjects = PromptSubjectSynthesis.BuildCatalogPayload(settings),
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
                        split = PromptSplit.ToMetadata(j.Split),
                        canHide = CanManageVisibility(j, authUser, auth != null),
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

                var prompt = UiJobRunner.PromptFromFormField(form["prompt"].ToString());
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
                UiSketchComposerState? sketchComposer;
                try
                {
                    sketchComposer = UiJobRunner.ParseSketchComposerState(
                        form["sketchComposer"].ToString(),
                        uploadFiles.Count);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
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
                Dictionary<string, UiCondensedPrompt> condensedPrompts;
                try
                {
                    var condensedPromptsJson = form.ContainsKey("condensedPrompts")
                        ? form["condensedPrompts"].ToString()
                        : null;
                    if (condensedPromptsJson != null
                        && environments?.Get(settings.UiEnvironmentId) is { PromptRewrite: false })
                    {
                        throw new InvalidDataException("Prompt condensing is not available in this environment.");
                    }
                    var exchangeIdentity = PromptExchangeIdentityKey(
                        request.HttpContext.Items["micUser"] as string ?? "",
                        createdBy);
                    condensedPrompts = UiJobRunner.ResolveCondensedPrompts(
                        condensedPromptsJson,
                        genKeys,
                        prompt,
                        generatorExtraTexts,
                        imageAttached: uploadFiles.Count > 0,
                        exchangeId => community
                            .ReadClaudePromptExchanges(exchangeIdentity, 1, exchangeId: exchangeId)
                            .SingleOrDefault());
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                // A split submission names the user's stored split. Each part
                // becomes its own job, in part order, with one image per
                // generator. See docs/prompt-split-prd.md.
                var splitExchangeId = form["splitExchangeId"].ToString().Trim();
                PromptSplitPlan? splitPlan = null;
                var splitModel = "";
                if (splitExchangeId.Length > 0)
                {
                    try
                    {
                        if (environments?.Get(settings.UiEnvironmentId) is { PromptRewrite: false })
                        {
                            throw new InvalidDataException("Prompt splitting is not available in this environment.");
                        }
                        if (form["n"].ToString() != "1")
                        {
                            throw new InvalidDataException("A split makes one image per part on each generator. Send n=1.");
                        }
                        if (form.ContainsKey("condensedPrompts"))
                        {
                            throw new InvalidDataException("A split part cannot use a condensed prompt.");
                        }
                        if (sketchComposer != null)
                        {
                            throw new InvalidDataException("A composition sketch lays out one image. Remove the sketch to split the prompt.");
                        }
                        var exchangeIdentity = PromptExchangeIdentityKey(
                            request.HttpContext.Items["micUser"] as string ?? "",
                            createdBy);
                        var resolved = PromptSplit.ResolveSubmission(
                            splitExchangeId,
                            prompt,
                            genKeys,
                            generatorExtraTexts,
                            imageAttached: uploadFiles.Count > 0,
                            exchangeId => community
                                .ReadClaudePromptExchanges(exchangeIdentity, 1, exchangeId: exchangeId)
                                .SingleOrDefault());
                        splitPlan = resolved.Plan;
                        splitModel = resolved.Exchange.Model;
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
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
                            .Where(key => UiJobRunner.IsImageCapable(key)
                                && !SupportsImageAspectOverride(key))
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

                // Every job of a split is admitted before any of them starts,
                // so a full queue starts none.
                var jobCount = splitPlan?.Parts.Count ?? 1;
                var admissions = new List<IDisposable?>(jobCount);
                for (var a = 0; a < jobCount; a++)
                {
                    if (!runner.TryAcquireJobAdmission(out var admission))
                    {
                        foreach (var held in admissions) held?.Dispose();
                        DeleteRejectedJobInputs(savedInputs.Select(saved => saved.Path));
                        return Results.Json(
                            new
                            {
                                error = jobCount == 1
                                    ? $"The UI queue is full ({runner.MaxPendingJobs} pending jobs). Try again after a job finishes."
                                    : $"The UI queue has no room for {jobCount} more jobs ({runner.MaxPendingJobs} pending jobs at most). Nothing was started. Try again after jobs finish.",
                            },
                            statusCode: 503);
                    }
                    admissions.Add(admission);
                }
                try
                {
                    UiJob StartJob(string jobPrompt, UiJobSplit? split, int admissionIndex)
                    {
                        var job = new UiJob
                        {
                            Prompt = jobPrompt,
                            CreatedBy = createdBy,
                            CreatorLogin = request.HttpContext.Items["micUser"] as string ?? "",
                            InputImagePaths = inputPaths,
                            InputImageWidth = inputImageWidth,
                            InputImageHeight = inputImageHeight,
                            GeneratorKeys = genKeys,
                            Split = split,
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
                            CondensedPrompts = condensedPrompts,
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
                            // One entry per condensed prompt, naming exactly the
                            // targets that received it instead of the job prompt.
                            condensedPrompts = genKeys
                                .Where(spec.CondensedPrompts.ContainsKey)
                                .GroupBy(key => spec.CondensedPrompts[key].ExchangeId)
                                .Select(group => new
                                {
                                    exchangeId = group.Key,
                                    model = spec.CondensedPrompts[group.First()].Model,
                                    prompt = spec.CondensedPrompts[group.First()].Prompt,
                                    generators = group.ToArray(),
                                })
                                .ToArray(),
                            // Exact persisted identity for the sketch editor. Null
                            // explicitly means this new job had no linked sketch;
                            // old accepted events lack the property entirely and
                            // therefore cannot safely identify a legacy attachment.
                            sketchComposer = sketchComposer == null
                                ? null
                                : sketchComposer.Version == 1
                                ? (object)new
                                {
                                    version = sketchComposer.Version,
                                    inputIndex = sketchComposer.InputIndex,
                                    aspect = sketchComposer.Aspect,
                                    meanings = sketchComposer.Meanings,
                                }
                                : (object)new
                                {
                                    version = sketchComposer.Version,
                                    inputIndex = sketchComposer.InputIndex,
                                    aspect = sketchComposer.Aspect,
                                    palette = sketchComposer.Palette!
                                        .Select(color => new { name = color.Name, hex = color.Hex })
                                        .ToArray(),
                                    meanings = sketchComposer.Meanings,
                                },
                            // Keep the legacy gpt2 event fields while pre-feature
                            // browser windows remain possible. New clients use the
                            // keyed map above for every image and describe endpoint.
                            gpt2GuidanceEnabled = spec.GeneratorExtraTexts.ContainsKey(UiJobRunner.KeyGpt2),
                            gpt2GuidanceText = spec.GeneratorExtraTexts.TryGetValue(
                                UiJobRunner.KeyGpt2,
                                out var gpt2ExtraText)
                                ? gpt2ExtraText
                                : "",
                            prompt = jobPrompt,
                            at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        });
                        var ownedAdmission = admissions[admissionIndex]!;
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
                        admissions[admissionIndex] = null;
                        return job;
                    }

                    if (splitPlan == null)
                    {
                        var job = StartJob(prompt, null, 0);
                        return Results.Json(new
                        {
                            id = job.Id,
                            user = createdBy,
                            originalUser = createdBy,
                            ownerId = UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                        });
                    }
                    var groupId = Guid.NewGuid().ToString("N")[..12];
                    var parts = new List<UiJob>(splitPlan.Parts.Count);
                    for (var i = 0; i < splitPlan.Parts.Count; i++)
                    {
                        var part = splitPlan.Parts[i];
                        parts.Add(StartJob(
                            part.Prompt,
                            new UiJobSplit(groupId, splitExchangeId, i + 1, splitPlan.Parts.Count, part.Title, splitModel, splitPlan.Reason),
                            i));
                    }
                    Logger.Log($"UI split {splitExchangeId} started {parts.Count} jobs for '{createdBy}': "
                        + string.Join(", ", parts.Select(job => job.Id)));
                    return Results.Json(new
                    {
                        jobs = parts.Select(job => new { id = job.Id, split = PromptSplit.ToMetadata(job.Split) }).ToArray(),
                        user = createdBy,
                        originalUser = createdBy,
                        ownerId = UiCommunityStore.PublicIdentityId(parts[0].CreatorLogin),
                    });
                }
                finally
                {
                    foreach (var held in admissions) held?.Dispose();
                }
            });

            MapGoalLoopRoutes(app, settings, auth, runner, goalLoops, goalLoopRunner, community);

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

                var prompt = UiJobRunner.PromptFromFormField(form["prompt"].ToString());
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

            MapPromptToolRoutes(app, settings, auth, jobs, visibility, community);

            // Cursor-based poll for ALL job events. Deliberately NOT a
            // persistent stream: browsers cap plain-HTTP/1.1 at ~6 connections
            // per origin ACROSS ALL TABS (no HTTP/2 without TLS on localhost),
            // so any long-lived SSE/WebSocket per window re-starved every
            // <img> load once a few windows were open (observed 2026-07-27,
            // twice). Each poll answers immediately and releases its socket.
            // Envelopes are {jobId, kind:"job-known"|"event", job?|event?};
            // a job-known announcement precedes each job's events, so cursor=0
            // replays the full history and hydrates a fresh window. Out-of-range
            // cursors within one process resync from 0; a process change fails
            // the instance check below, and the page replays or reloads.
            app.MapGet("/api/events/poll", (
                int? cursor,
                string? visibilityVersion,
                string? clientInstance,
                HttpContext ctx) =>
            {
                if (!string.Equals(clientInstance, clientInstanceId, StringComparison.Ordinal))
                {
                    ctx.Response.Headers.CacheControl = "no-store";
                    return Results.Json(
                        new { error = "client reload required" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }
                var (envelopes, nextCursor) = jobs.ReadEnvelopes(cursor ?? 0);
                var authUser = ctx.Items["micUser"] as string ?? "";
                var visibleEnvelopes = BuildVisibleEnvelopes(
                    envelopes,
                    jobs,
                    visibility,
                    authUser,
                    community.SnapshotProfiles(),
                    auth != null);
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

            app.MapGet("/api/jobs/{id}/describe-sheet", async (string id, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var job = jobs.Get(id);
                if (job == null || visibility.IsPromptHidden(id) || visibility.HasHiddenImages(id))
                    return Results.NotFound();
                if (!job.IsDone)
                    return Results.Json(new { error = "Wait for this job to finish before making its describe sheet." }, statusCode: 409);
                try
                {
                    var stream = await runner.CreateDescribeSheetAsync(job, ctx.RequestAborted);
                    if (stream == null)
                        return Results.Json(new { error = "Image finalization is busy. Try the describe sheet again shortly." }, statusCode: 503);
                    // Recheck after rendering and hosted downloads; deletion must also hide this derived image.
                    if (visibility.IsPromptHidden(id) || visibility.HasHiddenImages(id))
                    {
                        await stream.DisposeAsync();
                        return Results.NotFound();
                    }
                    ctx.Response.Headers.ContentDisposition = "inline; filename=describe-sheet.png";
                    return Results.File(stream, "image/png");
                }
                catch (InvalidDataException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 422);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Log($"[ui #{job.Id}] describe sheet failed: {ex.Message}");
                    return Results.Json(new { error = "Could not build the complete describe sheet from its recorded images and results." }, statusCode: 500);
                }
            });

            app.MapGet("/api/jobs/{id}/images/{gen}/{n:int}", async (string id, string gen, int n, HttpContext ctx) =>
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
                        // Expired thumb whose local original was evicted after
                        // its verified B2 upload: rebuild from the exact
                        // recorded hosted object (SHA-verified, single-flight,
                        // capped concurrency).
                        if (visibility.IsPromptHidden(id)
                            || visibility.IsImageHidden(id, visibilityGen, n))
                        {
                            return Results.NotFound();
                        }
                        var rebuilt = await runner.TryRebuildCardPreviewFromHostedAsync(job, gen, n);
                        if (rebuilt == null)
                        {
                            return Results.NotFound();
                        }
                        fileResult = Results.File(
                            Path.GetFullPath(rebuilt.Value.Path),
                            rebuilt.Value.ContentType,
                            enableRangeProcessing: true);
                        bytesAreFinal = true;
                    }
                }
                else if (runner.HostedOriginalUrl(job, gen, n) is { } hostedUrl)
                {
                    // Hosted identity is authoritative, including local URLs recorded after an interrupted job.
                    ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                    return Results.Redirect(hostedUrl);
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
                        CanManageVisibility(job, authUser, auth != null),
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

            // One-way destructive hiding. Ungated local instances permit their
            // owner to manage all jobs. Shared sites require the authenticated
            // creator or fixed ernieMultiZone override. Display names never
            // grant this permission. Persist the visibility tombstone before
            // deleting exact local/B2 artifacts. Interrupted purges remain
            // hidden and retry during the next server startup.
            app.MapPost("/api/visibility", async (HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (auth != null && authUser.Length == 0)
                {
                    return Results.Json(
                        new { error = "Log in to hide and delete a prompt or image." },
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
                if (!CanManageVisibility(job, authUser, auth != null))
                {
                    return Results.Json(
                        new { error = "Only the authenticated creator can hide and delete this item." },
                        statusCode: 403);
                }
                if (!job.IsDone)
                {
                    return Results.Json(
                        new { error = "Wait for this job to finish before deleting its images." },
                        statusCode: 409);
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

                var visibilityActor = auth == null ? LocalVisibilityActor : authUser;
                var hiddenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var pendingRecord = new UiHiddenResource
                {
                    Kind = kind,
                    JobId = jobId,
                    Generator = generator,
                    ImageIndex = imageIndex,
                    HiddenByLogin = visibilityActor,
                    HiddenAtUnixMs = hiddenAt,
                };
                var tombstonePersisted = false;
                try
                {
                    visibility.Hide(pendingRecord);
                    tombstonePersisted = true;
                    var deletedArtifacts = await runner.DeleteHiddenArtifactsAsync(
                        job,
                        kind,
                        generator,
                        imageIndex,
                        request.HttpContext.RequestAborted);
                    var removedFavorites = favorites.RemoveHiddenResource(
                        kind,
                        jobId,
                        generator,
                        imageIndex);
                    visibility.MarkPurged(
                        pendingRecord,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log(
                        $"UI visibility: {visibilityActor} hid and deleted {kind}/{jobId}"
                        + (kind == "image" ? $"/{generator}/{imageIndex}" : "")
                        + $" ({deletedArtifacts} artifact(s), {removedFavorites} favorite(s)).");
                    var snapshot = visibility.Snapshot();
                    return Results.Json(BuildVisibilityResponse(
                        snapshot.Version,
                        snapshot.Records));
                }
                catch (Exception ex) when (
                    ex is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or HttpRequestException
                    or TaskCanceledException)
                {
                    Logger.Log(
                        $"UI visibility: hide/delete failed for {kind}/{jobId}"
                        + (kind == "image" ? $"/{generator}/{imageIndex}" : "")
                        + $": {ex.Message}");
                    var snapshot = visibility.Snapshot();
                    return Results.Json(
                        new
                        {
                            error = tombstonePersisted
                                ? "The item is hidden, but permanent file deletion is incomplete. "
                                    + "The server will retry it during startup."
                                : "The hide and delete operation could not be persisted.",
                            visibility = tombstonePersisted
                                ? BuildVisibilityResponse(snapshot.Version, snapshot.Records)
                                : null,
                        },
                        statusCode: 500);
                }
            });

            app.MapGet("/api/jobs/{id}/location", (string id, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (visibility.IsPromptHidden(id) || !jobs.TryGetJobLocation(id, out var day, out var live))
                {
                    return Results.NotFound(new { error = "The selected job is no longer available." });
                }
                return Results.Json(new { day, live });
            });

            MapFableBot(app, settings, auth, jobs, visibility, runner);

            MapCommunityRoutes(app, settings, auth, jobs, runner, visibility, favorites, community, vibecoders, environments, accountActivity);

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
            // Expire old thumbnails, including those whose originals are gone.
            using var thumbExpiryCts = new CancellationTokenSource();
            var thumbExpiryLoop = Task.Run(
                () => UiThumbExpiry.RunLoopAsync(settings, thumbExpiryCts.Token));
            var hostedCleanupLoop = Task.Run(
                () => UiHostedRawCleanup.RunLoopAsync(settings, thumbExpiryCts.Token));
            using var idleMonitorCts = new CancellationTokenSource();
            var idleMonitor = idleLifetime?.MonitorAsync(
                () => activeJobs.Values.Any(job => !job.IsCompleted) || runner.PendingJobCount > 0 || goalLoopRunner.RunningCount > 0,
                app.Lifetime.StopApplication, idleMonitorCts.Token);
            await app.RunAsync();
            idleMonitorCts.Cancel();
            if (idleMonitor != null) await idleMonitor;
            thumbExpiryCts.Cancel();
            await Task.WhenAll(thumbExpiryLoop, hostedCleanupLoop);

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

        private static async Task PurgePendingHiddenArtifactsAsync(
            UiJobRegistry jobs,
            UiFavoriteStore favorites,
            UiVisibilityStore visibility,
            UiJobRunner runner)
        {
            foreach (var record in visibility.ListPendingPurges())
            {
                var job = jobs.Get(record.JobId);
                if (job == null)
                {
                    Logger.Log(
                        $"UI visibility: pending purge has no exact job: "
                        + $"{record.Kind}/{record.JobId}/{record.Generator}/{record.ImageIndex}.");
                    continue;
                }
                try
                {
                    var deletedArtifacts = await runner.DeleteHiddenArtifactsAsync(
                        job,
                        record.Kind,
                        record.Generator,
                        record.ImageIndex,
                        CancellationToken.None);
                    var removedFavorites = favorites.RemoveHiddenResource(
                        record.Kind,
                        record.JobId,
                        record.Generator,
                        record.ImageIndex);
                    visibility.MarkPurged(
                        record,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log(
                        $"UI visibility: completed pending purge for "
                        + $"{record.Kind}/{record.JobId}"
                        + (record.Kind == "image"
                            ? $"/{record.Generator}/{record.ImageIndex}"
                            : "")
                        + $" ({deletedArtifacts} artifact(s), {removedFavorites} favorite(s)).");
                }
                catch (Exception ex) when (
                    ex is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or HttpRequestException
                    or TaskCanceledException)
                {
                    Logger.Log(
                        $"UI visibility: pending purge remains incomplete for "
                        + $"{record.Kind}/{record.JobId}"
                        + (record.Kind == "image"
                            ? $"/{record.Generator}/{record.ImageIndex}"
                            : "")
                        + $": {ex.Message}");
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

        private static bool CanManageVisibility(UiJob? job, string authUser, bool authenticationEnabled)
        {
            if (job == null) return false;
            // An explicitly ungated local instance has one owner. Never infer
            // this mode from a missing login on an authenticated deployment.
            if (!authenticationEnabled) return true;
            if (authUser.Length == 0)
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
            UiProfileSnapshot profiles,
            bool authenticationEnabled)
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
                    metadata["canHide"] = CanManageVisibility(job, authUser, authenticationEnabled);
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
            var partialImages = evt["partialImages"] as JsonArray;
            var partialThumbs = evt["partialThumbs"] as JsonArray;
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
                if (partialImages != null && index < partialImages.Count)
                {
                    partialImages[index] = null;
                }
                if (partialThumbs != null && index < partialThumbs.Count)
                {
                    partialThumbs[index] = null;
                }
                changed = true;
            }
            if (evt["progressImages"] is JsonArray progressImages)
            {
                for (var index = progressImages.Count - 1; index >= 0; index--)
                {
                    var imageIndex = progressImages[index]?["imageIndex"]?.GetValue<int>() ?? -1;
                    if (visibility.IsImageHidden(jobId, gen, imageIndex))
                    {
                        progressImages.RemoveAt(index);
                        changed = true;
                    }
                }
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
                    if (!UiJobRunner.IsConfigurableEndpointKey(key))
                    {
                        throw new InvalidDataException(
                            $"Per-generator extra text contains unknown generator '{key}'.");
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

        private static string NormalizeOption(string? value, string fallbackBeforeStart)
            => string.IsNullOrWhiteSpace(value) ? fallbackBeforeStart : value.Trim().ToLowerInvariant();

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
            public string? DefaultView { get; init; }
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
            if (submitted.DefaultView != null && submitted.DefaultView != "all" && submitted.DefaultView != "only-sota")
                throw new InvalidDataException("Generator default view must be all or only-sota.");
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
            if (submitted.EndpointConfigurations.Count > UiJobRunner.ConfigurableEndpointCount)
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
                if (!UiJobRunner.IsConfigurableEndpointKey(key))
                {
                    throw new InvalidDataException(
                        $"Per-endpoint configuration contains unknown generator '{key}'.");
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
                DefaultView = submitted.DefaultView ?? "all",
                HiddenGeneratorKeys = hidden,
                DefaultSelectedKeys = selected,
                Presets = presets,
                EndpointConfigurations = endpointConfigurations,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
        // saved verbatim under their true type; GIF and BMP (first frame) are
        // deterministically re-encoded to PNG before any job starts (logged
        // pre-operation input conformance, same policy as the Recraft input
        // conformer). Every other format, and any undecodable upload, is a
        // hard 400 before the job exists.
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
                var info = Raster.Identify(bytes);
                mime = info.MimeType;
                width = info.Width;
                height = info.Height;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Uploaded image could not be read: {ex.Message}", ex);
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
                byte[] png;
                using (var image = Raster.Decode(bytes))
                {
                    png = Raster.EncodePng(image);
                }
                Logger.Log(
                    $"UI input image arrived as {mime} ({width}x{height}); re-encoded to PNG "
                    + $"({bytes.LongLength:N0} -> {png.LongLength:N0} bytes) so every provider "
                    + "receives a correctly-labeled supported format.");
                bytes = png;
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
