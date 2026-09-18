#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        private static readonly SemaphoreSlim PublicShareSendLimit = new(1, 1);

        private static void ShareHeaders(HttpContext ctx)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' https:; media-src 'self' https:; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'";
        }

        private static void MapPublicShares(WebApplication app, Settings settings, UiAuth? auth,
            UiJobRegistry jobs, UiVisibilityStore visibility, UiJobRunner runner,
            UiDiscordVibecodersStore sent, UiEnvironmentRegistry? environments,
            Func<DiscordVibecodersClient>? clientFactory = null)
        {
            var localTargetPath = Path.Combine(settings.ImageDownloadBaseFolder, "discord-share-target.txt");
            string ShareTarget() => environments?.Get(settings.UiEnvironmentId).DiscordShareTarget
                ?? (File.Exists(localTargetPath) ? File.ReadAllText(localTargetPath).Trim() : "vibecoders");
            if (auth == null && environments == null)
            {
                app.MapGet("/api/discord/target", () => Results.Json(new { target = ShareTarget() }));
                app.MapPost("/api/discord/target", async (HttpContext ctx) =>
                {
                    if (ctx.Request.Headers["X-MIC-Share"] != "1") return Results.StatusCode(403);
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var target = form["target"].ToString();
                    if (target is not ("vibecoders" or "bot-testing")) return Results.BadRequest(new { error = "Unknown target." });
                    await PublicShareSendLimit.WaitAsync(ctx.RequestAborted);
                    try { Directory.CreateDirectory(settings.ImageDownloadBaseFolder); File.WriteAllText(localTargetPath, target); }
                    finally { PublicShareSendLimit.Release(); }
                    return Results.Json(new { target });
                });
            }
            DiscordVibecodersClient Client(string target) => clientFactory?.Invoke() ?? new DiscordVibecodersClient(settings, target: target);
            var store = new UiPublicShareStore(settings.ImageDownloadBaseFolder);
            string Sender(HttpContext ctx) => ctx.Items["micUser"] as string ?? (auth == null ? "local" : "");
            bool Allowed(HttpContext ctx) => Sender(ctx).Length > 0 &&
                (environments?.Get(settings.UiEnvironmentId).AllowVibecoders ?? true);
            bool Visible(UiPublicShareRecord record) => jobs.Get(record.JobId) is { IsDone: true }
                && !visibility.IsPromptHidden(record.JobId) && !visibility.HasHiddenImages(record.JobId);
            UiPublicShareRecord? Public(string token)
            {
                var record = store.Get(token);
                return record is { Published: true } && Visible(record) ? record : null;
            }
            UiPublicShareRecord? Draft(string token, HttpContext ctx)
            {
                var record = store.Get(token);
                return record != null && record.Owner == Sender(ctx) && Allowed(ctx) && Visible(record)
                    && (record.Published || record.ExpiresAt >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? record : null;
            }

            async Task<IResult> Asset(UiPublicShareRecord? record, int index, HttpContext ctx)
            {
                ShareHeaders(ctx);
                if (record == null || index < 0 || index >= record.Snapshot.Assets.Count) return Results.NotFound();
                var asset = record.Snapshot.Assets[index];
                var job = jobs.Get(record.JobId)!;
                if (ctx.Request.Query.ContainsKey("thumb") && asset.Kind != "video")
                {
                    if (job.TryGetCardPreviewPath(asset.Generator, asset.Index, out var thumb, out var thumbType))
                        return Results.File(Path.GetFullPath(thumb), thumbType);
                    var rebuilt = await runner.TryRebuildCardPreviewFromHostedAsync(job, asset.Generator, asset.Index);
                    return rebuilt == null ? Results.NotFound()
                        : Results.File(Path.GetFullPath(rebuilt.Value.Path), rebuilt.Value.ContentType);
                }
                var hosted = runner.HostedOriginalUrl(job, asset.Generator, asset.Index);
                if (hosted != null) return Results.Redirect(hosted);
                if (job.TryGetImagePath(asset.Generator, asset.Index, out var path, out var type) && File.Exists(path))
                    return Results.File(Path.GetFullPath(path), type, enableRangeProcessing: true);
                return Results.NotFound();
            }

            app.MapGet("/public/{token}/", (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                return record == null ? Results.NotFound() : Results.Content(UiPublicShares.Html(record, "asset/", "reuse"), "text/html; charset=utf-8");
            });
            app.MapGet("/public/{token}/asset/{index:int}", (string token, int index, HttpContext ctx) => Asset(Public(token), index, ctx));

            string ComposerUrl(string token)
            {
                // This address is released only after authentication and environment membership checks.
                var prefix = settings.UiPublicBaseUrl.TrimEnd('/');
                if (auth != null && !Uri.TryCreate(prefix, UriKind.Absolute, out _))
                    throw new InvalidOperationException("The authenticated composer address is not configured.");
                return prefix + "/?shared=" + token;
            }
            bool Member(string user) => environments?.CanEnter(settings.UiEnvironmentId, user) ?? true;
            app.MapGet("/public/{token}/reuse", (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                if (record == null) return Results.NotFound();
                if (auth == null || (auth.TryValidateCookie(ctx.Request.Cookies[auth.SessionCookieName], out var user) && Member(user)))
                    return Results.Redirect(ComposerUrl(token));
                return Results.Content(UiPublicShares.Html(record, "asset/", null, login: true), "text/html; charset=utf-8");
            });
            app.MapPost("/public/{token}/reuse", async (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                if (record == null || auth == null) return Results.NotFound();
                if (ctx.Request.Headers.Origin != new Uri(UiPublicShares.BaseUrl(settings)).GetLeftPart(UriPartial.Authority))
                    return Results.StatusCode(403);
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var username = form["username"].ToString().Trim();
                if (!auth.TryLogin(username, form["password"].ToString(), ClientIpForThrottle(ctx), out var cookie, out _)
                    || !Member(username))
                    return Results.Content(UiPublicShares.Html(record, "asset/", null,
                        "Login failed or this account lacks access. Ask Ernie in Discord.", true), "text/html; charset=utf-8", statusCode: 401);
                ctx.Response.Cookies.Append(auth.SessionCookieName, cookie, new CookieOptions
                {
                    HttpOnly = true, Secure = IsEffectivelyHttps(ctx), SameSite = SameSiteMode.Lax,
                    Path = auth.SessionCookiePath, MaxAge = TimeSpan.FromDays(3650),
                });
                return Results.Redirect(ComposerUrl(token));
            });

            app.MapGet("/api/public-shares/{token}/reuse", (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                if (record == null) return Results.NotFound();
                return Results.Json(new { prompt = record.Snapshot.Prompt,
                    inputs = record.Snapshot.Assets.Select((asset, index) => (asset, index)).Where(pair => pair.asset.Kind == "input")
                        .Select(pair => $"api/public-shares/{token}/asset/{pair.index}") });
            });
            app.MapGet("/api/public-shares/{token}/asset/{index:int}", (string token, int index, HttpContext ctx) => Asset(Public(token), index, ctx));
            // Authenticated reuse may read inputs from any published page, not only its publisher.
            app.MapGet("/api/discord/vibecoders/preview/{token}/asset/{index:int}", (string token, int index, HttpContext ctx) =>
                Asset(Public(token) ?? Draft(token, ctx), index, ctx));
            app.MapGet("/api/discord/vibecoders/preview/{token}/", (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Draft(token, ctx);
                return record == null ? Results.NotFound() : Results.Content(UiPublicShares.Html(record, "asset/", null), "text/html; charset=utf-8");
            });

            app.MapPost("/api/discord/vibecoders/prepare", async (HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                if (!Allowed(ctx) || ctx.Request.Headers["X-MIC-Share"] != "1") return Results.StatusCode(403);
                if (!await PublicShareSendLimit.WaitAsync(0)) return Results.Json(new { error = "Another share is being prepared or sent." }, statusCode: 409);
                try
                {
                    var baseUrl = UiPublicShares.BaseUrl(settings);
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var job = jobs.Get(form["jobId"].ToString());
                    var gen = form["generator"].ToString();
                    if (job == null || !int.TryParse(form["imageIndex"], out var index) || index < 0) return Results.NotFound();
                    if (!CanManageVisibility(job, Sender(ctx), auth != null)) return Results.StatusCode(403);
                    if (visibility.IsPromptHidden(job.Id) || visibility.HasHiddenImages(job.Id))
                        throw new InvalidOperationException("A prompt with deleted results cannot be published.");
                    if (sent.IsSent(job.Id, gen, index)) return Results.Json(new { error = "This result was already sent or needs a delivery check." }, statusCode: 409);
                    if (!TryResolveSendMedia(job, gen, index, out var media, out var error)) throw new InvalidOperationException(error);
                    var snapshot = UiPublicShares.Capture(job);
                    if (media.Kind == "image" && !snapshot.Assets.Any(asset => asset.Kind == "grid"))
                        throw new InvalidOperationException("The contact sheet is unavailable. Sharing stopped.");
                    var selected = snapshot.Assets.FindIndex(asset => asset.Generator == gen && asset.Index == index && asset.Kind is "image" or "video");
                    if (selected < 0) throw new InvalidOperationException("The selected output is absent from the public snapshot.");
                    // Validate every original before presenting the scope for consent.
                    foreach (var asset in snapshot.Assets)
                        if (runner.HostedOriginalUrl(job, asset.Generator, asset.Index) == null &&
                            !(job.TryGetImagePath(asset.Generator, asset.Index, out var path, out _) && File.Exists(path)))
                            throw new InvalidOperationException("An original in this prompt is unavailable; sharing stopped.");
                    _ = await runner.TryGetImageBytesIncludingHostedAsync(job, gen, index, DiscordVibecoders.MaxAttachmentBytes, ctx.RequestAborted)
                        ?? throw new InvalidOperationException("The selected original is unavailable or exceeds 10 MiB.");
                    var shareTarget = ShareTarget();
                    var target = await Client(shareTarget).GetDailyDestinationAsync(ctx.RequestAborted);
                    store.PruneExpiredDrafts();
                    var token = UiPublicShareStore.NewToken();
                    var threadDay = DiscordDailyThreads.Day(DateTimeOffset.UtcNow);
                    var record = new UiPublicShareRecord { Token = token, JobId = job.Id, Generator = gen, ImageIndex = index,
                        Owner = Sender(ctx), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(), Snapshot = snapshot,
                        DestinationHash = UiPublicShares.DestinationHash(settings, shareTarget), GuildId = target.GuildId, ChannelId = target.ChannelId,
                        ServerName = target.ServerName, ChannelName = target.ChannelName,
                        ThreadDay = threadDay, ThreadName = DiscordDailyThreads.Name(threadDay),
                        PublicUrl = baseUrl + "/" + token + "/" };
                    store.Save(record);
                    return Results.Json(new { token, jobId = job.Id, generator = gen, imageIndex = index,
                        serverName = record.ServerName, channelName = record.ChannelName, threadName = record.ThreadName,
                        disclosure = UiPublicShares.Disclosure, linkLabel = UiPublicShares.LinkLabel, reuseLabel = UiPublicShares.ReuseLabel,
                        publicUrl = record.PublicUrl, caption = UiPublicShares.Caption(record.PublicUrl),
                        mediaKind = media.Kind, mediaUrl = $"api/discord/vibecoders/preview/{token}/asset/{selected}",
                        previewUrl = $"api/discord/vibecoders/preview/{token}/", inputCount = job.InputImageCount,
                        outputCount = snapshot.Assets.Count(a => a.Kind is "image" or "video"), hasContactSheet = snapshot.Assets.Any(a => a.Kind == "grid") });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { return Results.Json(new { error = ex is InvalidOperationException ? ex.Message : "Could not prepare this share." }, statusCode: 400); }
                finally { PublicShareSendLimit.Release(); }
            });

            app.MapPost("/api/discord/vibecoders", async (HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                if (!Allowed(ctx) || ctx.Request.Headers["X-MIC-Share"] != "1") return Results.StatusCode(403);
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                if (form["confirmed"] != "true") return Results.BadRequest(new { error = "Preview and confirm publication first." });
                if (!await PublicShareSendLimit.WaitAsync(0)) return Results.Json(new { error = "Another Discord send is in progress." }, statusCode: 409);
                UiPublicShareRecord? record = null;
                var claimed = false;
                var published = false;
                try
                {
                    record = Draft(form["token"].ToString(), ctx);
                    if (record == null || record.State != "draft") throw new InvalidOperationException("The preview expired or was already confirmed. Open a new preview.");
                    var job = jobs.Get(record.JobId)!;
                    if (!CanManageVisibility(job, Sender(ctx), auth != null)) return Results.StatusCode(403);
                    var shareTarget = ShareTarget();
                    if (record.DestinationHash != UiPublicShares.DestinationHash(settings, shareTarget)
                        || record.PublicUrl != UiPublicShares.BaseUrl(settings) + "/" + record.Token + "/"
                        || JsonSerializer.Serialize(record.Snapshot) != JsonSerializer.Serialize(UiPublicShares.Capture(job)))
                        throw new InvalidOperationException("The prompt or destination changed. Review a new preview.");
                    var client = Client(shareTarget);
                    var target = await client.GetDailyDestinationAsync(ctx.RequestAborted);
                    if (target.GuildId != record.GuildId || target.ChannelId != record.ChannelId
                        || target.ServerName != record.ServerName || target.ChannelName != record.ChannelName)
                        throw new InvalidOperationException("The Discord destination changed. Review a new preview.");
                    var original = await runner.TryGetImageBytesIncludingHostedAsync(job, record.Generator, record.ImageIndex,
                        DiscordVibecoders.MaxAttachmentBytes, ctx.RequestAborted) ?? throw new InvalidOperationException("The original is unavailable.");
                    if (record.ThreadDay != DiscordDailyThreads.Day(DateTimeOffset.UtcNow)
                        || record.ThreadName != DiscordDailyThreads.Name(record.ThreadDay))
                        throw new InvalidOperationException("The California date changed. Open a new preview for today’s image thread.");
                    var threadId = await client.GetDailyThreadAsync(record.GuildId, record.ChannelId, record.ThreadDay, ctx.RequestAborted);
                    if (record.ThreadDay != DiscordDailyThreads.Day(DateTimeOffset.UtcNow))
                        throw new InvalidOperationException("The California date changed. Open a new preview for today’s image thread.");
                    var claim = new UiDiscordVibecodersSend { Kind = record.Snapshot.Assets.Any(a => a.Generator == record.Generator && a.Kind == "video") ? "video" : "image",
                        JobId = record.JobId, Generator = record.Generator, ImageIndex = record.ImageIndex,
                        SentByLogin = record.Owner, SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), State = "pending" };
                    if (!sent.TryClaim(claim)) return Results.Json(new { error = "This result was already sent or needs a delivery check." }, statusCode: 409);
                    claimed = true;
                    record = record with { State = "pending" };
                    store.Save(record);
                    published = true;
                    using var bytes = new MemoryStream(original.Bytes, writable: false);
                    await client.SendAsync(record.Owner, bytes, original.ContentType,
                        record.Generator + "-" + record.ImageIndex + DiscordVibecoders.FileExtension(original.ContentType),
                        ctx.RequestAborted, record.PublicUrl, threadId, threadId);
                    store.Save(record with { State = "sent" });
                    sent.Complete(record.JobId, record.Generator, record.ImageIndex);
                    return Results.Json(new { state = "sent", publicUrl = record.PublicUrl, version = sent.Snapshot().Version,
                        item = new { jobId = record.JobId, generator = record.Generator, imageIndex = record.ImageIndex } });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (claimed && !published && record != null) sent.ReleaseClaim(record.JobId, record.Generator, record.ImageIndex, record.Owner);
                    return Results.Json(new { state = published ? "pending" : "ready", publicUrl = published ? record?.PublicUrl : null,
                        error = published ? "The page is public. Delivery is unconfirmed; check Discord before sending again."
                            : ex is InvalidOperationException ? ex.Message : "Could not send this share." }, statusCode: 502);
                }
                finally { PublicShareSendLimit.Release(); }
            });
        }
    }
}
