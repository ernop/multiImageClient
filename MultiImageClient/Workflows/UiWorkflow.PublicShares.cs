#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        private static readonly SemaphoreSlim PublicShareSendLimit = new(1, 1);

        private static void ShareHeaders(HttpContext ctx, bool loginScript = false, bool seriesScript = false)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var csp = "default-src 'none'; img-src 'self' https:; media-src 'self' https:; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'self'; base-uri 'none'";
            if (loginScript || seriesScript)
            {
                var hashes = new System.Collections.Generic.List<string>();
                if (loginScript)
                {
                    csp += "; connect-src 'self'";
                    hashes.Add("'" + ShareLinkLoginScriptHash + "'");
                }
                if (seriesScript)
                {
                    hashes.Add("'" + UiPublicShares.SeriesReaderScriptHash + "'");
                }
                csp += "; script-src " + string.Join(" ", hashes);
            }
            ctx.Response.Headers.ContentSecurityPolicy = csp;
        }

        private static void MapPublicShares(WebApplication app, Settings settings, UiAuth? auth,
            UiJobRegistry jobs, UiVisibilityStore visibility, UiJobRunner runner,
            UiDiscordVibecodersStore sent, UiEnvironmentRegistry? environments,
            Func<DiscordVibecodersClient>? clientFactory = null,
            UiCommunityStore? community = null)
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
            bool Visible(UiPublicShareRecord record)
            {
                if (record.Series != null)
                {
                    if (record.Series.Pages.Count < 2) return false;
                    foreach (var page in record.Series.Pages)
                    {
                        if (jobs.Get(page.JobId) is not { IsDone: true }
                            || visibility.IsPromptHidden(page.JobId)
                            || visibility.HasHiddenImages(page.JobId))
                            return false;
                    }
                    return true;
                }
                return jobs.Get(record.JobId) is { IsDone: true }
                    && !visibility.IsPromptHidden(record.JobId) && !visibility.HasHiddenImages(record.JobId);
            }
            (string SourcePrompt, string StyleNote) ReadSeriesExchange(IReadOnlyList<UiJob> parts)
            {
                var exchangeId = parts[0].Split?.ExchangeId ?? "";
                if (community == null || string.IsNullOrWhiteSpace(exchangeId)) return ("", "");
                var exchange = community.GetClaudePromptExchangeById(exchangeId)
                    ?? throw new InvalidOperationException("The recorded split prompt is unavailable; sharing stopped.");
                if (exchange.Purpose != PromptSplit.Purpose)
                    throw new InvalidOperationException("The recorded split exchange does not match this series.");
                var (_, styleNote) = PromptSplit.ParseRecordedInstruction(exchange.Instruction);
                return (exchange.OriginalPrompt, styleNote);
            }
            UiPublicShareRecord? Public(string token)
            {
                var record = store.Get(token);
                return record is { Published: true, PageToken: "" } && Visible(record) ? record : null;
            }
            string? AccountRequestUrl(UiPublicShareRecord record)
            {
                var url = UiDiscordAccountRequests.PublicUrl(settings, auth, environments);
                return url == null ? null : url + (settings.UiEnvironmentId == UiDiscordAccountRequests.EnvironmentId ? "?share=" + record.Token : "");
            }
            UiPublicShareRecord? Draft(string token, HttpContext ctx)
            {
                var record = store.Get(token);
                return record is { IsPage: false } && record.Owner == Sender(ctx) && Allowed(ctx) && Visible(record)
                    && (record.Published || record.ExpiresAt >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? record : null;
            }

            async Task<IResult> Asset(UiPublicShareRecord? record, int index, HttpContext ctx)
            {
                ShareHeaders(ctx);
                if (record == null || index < 0) return Results.NotFound();
                if (record.Series != null)
                {
                    var slot = UiPublicShares.SeriesAssetSlot(record.Series, index);
                    if (slot == null) return Results.NotFound();
                    if (slot.Value.Kind == "series-grid")
                    {
                        var parts = record.Series.Pages.Select(p => jobs.Get(p.JobId)).Where(j => j != null).Cast<UiJob>().ToList();
                        if (parts.Count != record.Series.Pages.Count) return Results.NotFound();
                        var stream = await runner.CreateSplitContactSheetAsync(
                            parts,
                            record.Series.SourcePrompt,
                            record.Series.StyleNote,
                            includeText: true,
                            record.Series.Generator,
                            ctx.RequestAborted);
                        if (stream == null)
                            return Results.Json(new { error = "Image finalization is busy. Try again shortly." }, statusCode: 503);
                        if (!Visible(record))
                        {
                            await stream.DisposeAsync();
                            return Results.NotFound();
                        }
                        return Results.File(stream, "image/png");
                    }
                    var seriesJob = jobs.Get(slot.Value.JobId);
                    if (seriesJob == null) return Results.NotFound();
                    if (ctx.Request.Query.ContainsKey("thumb"))
                    {
                        if (seriesJob.TryGetCardPreviewPath(slot.Value.Generator, slot.Value.Index, out var sThumb, out var sThumbType))
                            return Results.File(Path.GetFullPath(sThumb), sThumbType);
                        var sRebuilt = await runner.TryRebuildCardPreviewFromHostedAsync(seriesJob, slot.Value.Generator, slot.Value.Index);
                        return sRebuilt == null ? Results.NotFound()
                            : Results.File(Path.GetFullPath(sRebuilt.Value.Path), sRebuilt.Value.ContentType);
                    }
                    var sHosted = runner.HostedOriginalUrl(seriesJob, slot.Value.Generator, slot.Value.Index);
                    if (sHosted != null) return Results.Redirect(sHosted);
                    if (seriesJob.TryGetImagePath(slot.Value.Generator, slot.Value.Index, out var sPath, out var sType) && File.Exists(sPath))
                        return Results.File(Path.GetFullPath(sPath), sType, enableRangeProcessing: true);
                    return Results.NotFound();
                }
                if (index >= record.Snapshot.Assets.Count) return Results.NotFound();
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
                var record = Public(token);
                ShareHeaders(ctx, seriesScript: record?.IsSeries == true);
                return record == null ? Results.NotFound() : Results.Content(UiPublicShares.Html(record, "asset/", "reuse",
                    requestAccountUrl: AccountRequestUrl(record)), "text/html; charset=utf-8");
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
                ShareHeaders(ctx, loginScript: true, seriesScript: record.IsSeries);
                return Results.Content(UiPublicShares.Html(record, "asset/", null, login: true,
                    requestAccountUrl: AccountRequestUrl(record)), "text/html; charset=utf-8");
            });
            // Browsers send "Origin: null" for native form posts from no-referrer pages, so the page logs in through fetch.
            app.MapPost("/public/{token}/reuse", async (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                if (record == null || auth == null) return Results.NotFound();
                if (ctx.Request.Headers.Origin.ToString() != new Uri(UiPublicShares.BaseUrl(settings)).GetLeftPart(UriPartial.Authority)
                    || ctx.Request.Headers["X-Mic-Login"] != "1")
                    return Results.Json(new { error = "Reload this page and try again." }, statusCode: 403);
                if (ctx.Request.ContentLength is null or > 4096 || !ctx.Request.HasFormContentType)
                    return Results.Json(new { error = "The login request is invalid." }, statusCode: 400);
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var username = form["username"].ToString().Trim();
                if (!auth.TryLogin(username, form["password"].ToString(), ClientIpForThrottle(ctx), out var cookie, out _)
                    || !auth.TryValidateCookie(cookie, out var login) || !Member(login))
                    return Results.Json(new { error = "Login failed or this account lacks access. Ask Ernie in Discord." }, statusCode: 401);
                ctx.Response.Cookies.Append(auth.SessionCookieName, cookie, new CookieOptions
                {
                    HttpOnly = true, Secure = IsEffectivelyHttps(ctx), SameSite = SameSiteMode.Lax,
                    Path = auth.SessionCookiePath, MaxAge = TimeSpan.FromDays(3650),
                });
                return Results.Json(new { destination = ComposerUrl(token) });
            });

            app.MapGet("/api/public-shares/{token}/reuse", (string token, HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                var record = Public(token);
                if (record == null) return Results.NotFound();
                if (record.Series != null)
                {
                    return Results.Json(new
                    {
                        prompt = record.Series.SourcePrompt,
                        inputs = record.Series.Inputs.Select((_, index) => $"api/public-shares/{token}/asset/{index}"),
                    });
                }
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
                var record = Draft(token, ctx);
                ShareHeaders(ctx, seriesScript: record?.IsSeries == true);
                return record == null ? Results.NotFound() : Results.Content(UiPublicShares.Html(record, "asset/", null), "text/html; charset=utf-8");
            });

            app.MapPost("/api/discord/vibecoders/prepare-series", async (HttpContext ctx) =>
            {
                ShareHeaders(ctx);
                if (!Allowed(ctx) || ctx.Request.Headers["X-MIC-Share"] != "1") return Results.StatusCode(403);
                if (!await PublicShareSendLimit.WaitAsync(0)) return Results.Json(new { error = "Another share is being prepared or sent." }, statusCode: 409);
                try
                {
                    var baseUrl = UiPublicShares.BaseUrl(settings);
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var groupId = form["groupId"].ToString().Trim();
                    var gen = form["generator"].ToString().Trim();
                    var rawIndices = form["imageIndices"].ToString().Trim();
                    var parts = jobs.ListBySplitGroup(groupId);
                    if (parts.Count == 0) return Results.NotFound();
                    foreach (var part in parts)
                    {
                        if (!CanManageVisibility(part, Sender(ctx), auth != null)) return Results.StatusCode(403);
                        if (visibility.IsPromptHidden(part.Id) || visibility.HasHiddenImages(part.Id))
                            throw new InvalidOperationException("A series with deleted parts or results cannot be published.");
                    }
                    var claimJobId = UiPublicShares.SeriesClaimJobId(groupId);
                    if (sent.IsSent(claimJobId, gen, 0))
                        return Results.Json(new { error = "This series was already sent or needs a delivery check." }, statusCode: 409);

                    var parsedIndices = new System.Collections.Generic.List<int>();
                    if (string.IsNullOrEmpty(rawIndices))
                    {
                        for (var i = 0; i < parts.Count; i++) parsedIndices.Add(0);
                    }
                    else
                    {
                        foreach (var piece in rawIndices.Split(','))
                        {
                            if (!int.TryParse(piece.Trim(), out var idx) || idx < 0)
                                throw new InvalidOperationException("Invalid page image index selection.");
                            parsedIndices.Add(idx);
                        }
                    }

                    var (sourcePrompt, styleNote) = ReadSeriesExchange(parts);
                    var series = UiPublicShares.CaptureSeries(parts, gen, parsedIndices, sourcePrompt, styleNote);

                    // Verify inputs exist locally or hosted.
                    var firstJob = jobs.Get(series.Pages[0].JobId)!;
                    for (var i = 0; i < series.Inputs.Count; i++)
                    {
                        if (runner.HostedOriginalUrl(firstJob, "input", i) == null
                            && !(firstJob.TryGetImagePath("input", i, out var inPath, out _) && File.Exists(inPath)))
                            throw new InvalidOperationException("An input image in this series is unavailable; sharing stopped.");
                    }

                    // Measure every selected page's original bytes one at a time (peak RAM = 1 image).
                    var pageSizes = new long[series.Pages.Count];
                    for (var i = 0; i < series.Pages.Count; i++)
                    {
                        var page = series.Pages[i];
                        var pageJob = jobs.Get(page.JobId)!;
                        var fetched = await runner.TryGetImageBytesIncludingHostedAsync(
                            pageJob, series.Generator, page.ImageIndex, DiscordVibecoders.MaxAttachmentBytes, ctx.RequestAborted)
                            ?? throw new InvalidOperationException(
                                $"Page {page.Index} original is unavailable or exceeds Discord's {DiscordVibecoders.MaxAttachmentBytes / (1024 * 1024)} MiB per-file limit.");
                        pageSizes[i] = fetched.Bytes.LongLength;
                    }

                    var batches = DiscordVibecoders.PlanAttachmentBatches(pageSizes);
                    var shareTarget = ShareTarget();
                    var target = await Client(shareTarget).GetDailyDestinationAsync(ctx.RequestAborted);
                    store.PruneExpiredDrafts();
                    var pageRecord = store.GetOrCreateSeriesPage(series);
                    var token = UiPublicShareStore.NewToken();
                    var threadDay = DiscordDailyThreads.Day(DateTimeOffset.UtcNow);
                    var publicUrl = baseUrl + "/" + pageRecord.Token + "/";
                    var draftRecord = new UiPublicShareRecord
                    {
                        Token = token,
                        PageToken = pageRecord.Token,
                        JobId = claimJobId,
                        Generator = series.Generator,
                        ImageIndex = 0,
                        Owner = Sender(ctx),
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                        Series = series,
                        DestinationHash = UiPublicShares.DestinationHash(settings, shareTarget),
                        GuildId = target.GuildId,
                        ChannelId = target.ChannelId,
                        ServerName = target.ServerName,
                        ChannelName = target.ChannelName,
                        ThreadDay = threadDay,
                        ThreadName = DiscordDailyThreads.Name(threadDay),
                        PublicUrl = publicUrl,
                    };
                    store.Save(draftRecord);
                    return Results.Json(new
                    {
                        kind = "series",
                        token,
                        groupId = series.GroupId,
                        generator = series.Generator,
                        generatorLabel = series.GeneratorLabel,
                        imageIndices = series.Pages.Select(p => p.ImageIndex).ToArray(),
                        serverName = draftRecord.ServerName,
                        channelName = draftRecord.ChannelName,
                        threadName = draftRecord.ThreadName,
                        disclosure = UiPublicShares.SeriesDisclosure(series),
                        linkLabel = $"Read 1–{series.Pages.Count}",
                        reuseLabel = UiPublicShares.ReuseLabel,
                        publicUrl,
                        viewUrl = publicUrl,
                        pagePublished = pageRecord.Published,
                        previewUrl = $"api/discord/vibecoders/preview/{token}/",
                        inputCount = series.Inputs.Count,
                        pageCount = series.Pages.Count,
                        totalBytes = pageSizes.Sum(),
                        batches = batches.Select((b, bi) => new
                        {
                            startPage = b.StartIndex + 1,
                            endPage = b.StartIndex + b.Count,
                            count = b.Count,
                            totalBytes = b.TotalBytes,
                            caption = UiPublicShares.SeriesCaption(publicUrl, series.Pages.Count, b.StartIndex, b.Count, includeReuse: bi == 0),
                        }),
                        pages = series.Pages.Select((p, i) => new
                        {
                            index = p.Index,
                            title = p.Title,
                            imageIndex = p.ImageIndex,
                            bytes = pageSizes[i],
                            previewUrl = $"api/discord/vibecoders/preview/{token}/asset/{series.Inputs.Count + i}?thumb=1",
                        }),
                    });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Results.Json(new { error = ex is InvalidOperationException ? ex.Message : "Could not prepare this series share." }, statusCode: 400);
                }
                finally { PublicShareSendLimit.Release(); }
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
                        ?? throw new InvalidOperationException(
                            $"The selected original is unavailable or exceeds {DiscordVibecoders.MaxAttachmentBytes / (1024 * 1024)} MiB.");
                    var shareTarget = ShareTarget();
                    var target = await Client(shareTarget).GetDailyDestinationAsync(ctx.RequestAborted);
                    store.PruneExpiredDrafts();
                    var page = store.GetOrCreatePage(job.Id, snapshot);
                    var token = UiPublicShareStore.NewToken();
                    var threadDay = DiscordDailyThreads.Day(DateTimeOffset.UtcNow);
                    var record = new UiPublicShareRecord { Token = token, PageToken = page.Token, JobId = job.Id, Generator = gen, ImageIndex = index,
                        Owner = Sender(ctx), CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(), Snapshot = snapshot,
                        DestinationHash = UiPublicShares.DestinationHash(settings, shareTarget), GuildId = target.GuildId, ChannelId = target.ChannelId,
                        ServerName = target.ServerName, ChannelName = target.ChannelName,
                        ThreadDay = threadDay, ThreadName = DiscordDailyThreads.Name(threadDay),
                        PublicUrl = baseUrl + "/" + page.Token + "/" };
                    store.Save(record);
                    return Results.Json(new { token, jobId = job.Id, generator = gen, imageIndex = index,
                        serverName = record.ServerName, channelName = record.ChannelName, threadName = record.ThreadName,
                        disclosure = UiPublicShares.Disclosure, linkLabel = UiPublicShares.LinkLabel, reuseLabel = UiPublicShares.ReuseLabel,
                        publicUrl = record.PublicUrl, viewUrl = UiPublicShares.ViewUrl(record.PublicUrl, selected),
                        pagePublished = page.Published,
                        caption = UiPublicShares.Caption(record.PublicUrl, selected),
                        mediaKind = media.Kind, mediaUrl = $"api/discord/vibecoders/preview/{token}/asset/{selected}",
                        previewUrl = $"api/discord/vibecoders/preview/{token}/#output-{selected}", inputCount = job.InputImageCount,
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
                var batchesSent = 0;
                var batchesPlanned = 0;
                try
                {
                    record = Draft(form["token"].ToString(), ctx);
                    if (record == null || record.State != "draft") throw new InvalidOperationException("The preview expired or was already confirmed. Open a new preview.");
                    if (record.Series != null)
                    {
                        var series = record.Series;
                        var parts = jobs.ListBySplitGroup(series.GroupId);
                        if (parts.Count != series.Pages.Count)
                            throw new InvalidOperationException("This series changed. Open a new preview.");
                        foreach (var part in parts)
                            if (!CanManageVisibility(part, Sender(ctx), auth != null)) return Results.StatusCode(403);
                        var page = store.PageForShare(record);
                        var shareTarget = ShareTarget();
                        var (sourcePrompt, styleNote) = ReadSeriesExchange(parts);
                        var freshSeries = UiPublicShares.CaptureSeries(
                            parts,
                            series.Generator,
                            series.Pages.Select(p => p.ImageIndex).ToList(),
                            sourcePrompt,
                            styleNote);
                        if (record.DestinationHash != UiPublicShares.DestinationHash(settings, shareTarget)
                            || record.PublicUrl != UiPublicShares.BaseUrl(settings) + "/" + page.Token + "/"
                            || !UiPublicShares.SameSeriesSnapshot(series, freshSeries))
                            throw new InvalidOperationException("The series or destination changed. Review a new preview.");
                        var client = Client(shareTarget);
                        var target = await client.GetDailyDestinationAsync(ctx.RequestAborted);
                        if (target.GuildId != record.GuildId || target.ChannelId != record.ChannelId
                            || target.ServerName != record.ServerName || target.ChannelName != record.ChannelName)
                            throw new InvalidOperationException("The Discord destination changed. Review a new preview.");

                        // Measure exact byte sizes one image at a time before claiming.
                        var pageSizes = new long[series.Pages.Count];
                        for (var i = 0; i < series.Pages.Count; i++)
                        {
                            var p = series.Pages[i];
                            var pJob = jobs.Get(p.JobId)!;
                            var fetched = await runner.TryGetImageBytesIncludingHostedAsync(
                                pJob, series.Generator, p.ImageIndex, DiscordVibecoders.MaxAttachmentBytes, ctx.RequestAborted)
                                ?? throw new InvalidOperationException($"Page {p.Index} original is unavailable.");
                            pageSizes[i] = fetched.Bytes.LongLength;
                        }
                        var batches = DiscordVibecoders.PlanAttachmentBatches(pageSizes);
                        batchesPlanned = batches.Count;

                        if (record.ThreadDay != DiscordDailyThreads.Day(DateTimeOffset.UtcNow)
                            || record.ThreadName != DiscordDailyThreads.Name(record.ThreadDay))
                            throw new InvalidOperationException("The California date changed. Open a new preview for today’s image thread.");
                        var threadId = await client.GetDailyThreadAsync(record.GuildId, record.ChannelId, record.ThreadDay, ctx.RequestAborted);
                        if (record.ThreadDay != DiscordDailyThreads.Day(DateTimeOffset.UtcNow))
                            throw new InvalidOperationException("The California date changed. Open a new preview for today’s image thread.");

                        var claimJobId = UiPublicShares.SeriesClaimJobId(series.GroupId);
                        var claim = new UiDiscordVibecodersSend
                        {
                            Kind = "series",
                            JobId = claimJobId,
                            Generator = series.Generator,
                            ImageIndex = 0,
                            SentByLogin = record.Owner,
                            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            State = "pending",
                        };
                        if (!sent.TryClaim(claim))
                            return Results.Json(new { error = "This series was already sent or needs a delivery check." }, statusCode: 409);
                        claimed = true;
                        store.PublishPage(record);
                        published = true;
                        record = record with { State = "pending" };
                        store.Save(record);

                        for (var b = 0; b < batches.Count; b++)
                        {
                            var batch = batches[b];
                            var streams = new System.Collections.Generic.List<MemoryStream>(batch.Count);
                            var attachments = new System.Collections.Generic.List<DiscordVibecodersAttachment>(batch.Count);
                            try
                            {
                                for (var offset = 0; offset < batch.Count; offset++)
                                {
                                    var p = series.Pages[batch.StartIndex + offset];
                                    var pJob = jobs.Get(p.JobId)!;
                                    var original = await runner.TryGetImageBytesIncludingHostedAsync(
                                        pJob, series.Generator, p.ImageIndex, DiscordVibecoders.MaxAttachmentBytes, ctx.RequestAborted)
                                        ?? throw new InvalidOperationException($"Page {p.Index} original is unavailable.");
                                    var ms = new MemoryStream(original.Bytes, writable: false);
                                    streams.Add(ms);
                                    var fileName = $"page-{p.Index:D2}-{series.Generator}{DiscordVibecoders.FileExtension(original.ContentType)}";
                                    var desc = string.IsNullOrWhiteSpace(p.Title)
                                        ? $"Page {p.Index} of {series.Pages.Count}"
                                        : $"Page {p.Index} of {series.Pages.Count} · {p.Title}";
                                    attachments.Add(new DiscordVibecodersAttachment(ms, original.ContentType, fileName, desc));
                                }
                                var caption = UiPublicShares.SeriesCaption(
                                    record.PublicUrl,
                                    series.Pages.Count,
                                    batch.StartIndex,
                                    batch.Count,
                                    includeReuse: b == 0);
                                await client.SendBatchAsync(record.Owner, attachments, caption, threadId, threadId, ctx.RequestAborted);
                                batchesSent++;
                            }
                            finally
                            {
                                foreach (var ms in streams) ms.Dispose();
                            }
                        }

                        store.Save(record with { State = "sent" });
                        sent.Complete(claimJobId, series.Generator, 0);
                        return Results.Json(new
                        {
                            state = "sent",
                            publicUrl = record.PublicUrl,
                            viewUrl = record.PublicUrl,
                            batchesSent,
                            version = sent.Snapshot().Version,
                            item = new
                            {
                                kind = "series",
                                groupId = series.GroupId,
                                jobId = claimJobId,
                                generator = series.Generator,
                                imageIndex = 0,
                            },
                        });
                    }
                    else
                    {
                        var job = jobs.Get(record.JobId)!;
                        if (!CanManageVisibility(job, Sender(ctx), auth != null)) return Results.StatusCode(403);
                        var page = store.PageForShare(record);
                        var shareTarget = ShareTarget();
                        if (record.DestinationHash != UiPublicShares.DestinationHash(settings, shareTarget)
                            || record.PublicUrl != UiPublicShares.BaseUrl(settings) + "/" + page.Token + "/"
                            || !UiPublicShares.SameSnapshot(record.Snapshot, UiPublicShares.Capture(job)))
                            throw new InvalidOperationException("The prompt or destination changed. Review a new preview.");
                        var selected = UiPublicShares.SelectedSlot(record);
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
                        store.PublishPage(record);
                        published = true;
                        record = record with { State = "pending" };
                        store.Save(record);
                        using var bytes = new MemoryStream(original.Bytes, writable: false);
                        await client.SendAsync(record.Owner, bytes, original.ContentType,
                            record.Generator + "-" + record.ImageIndex + DiscordVibecoders.FileExtension(original.ContentType),
                            ctx.RequestAborted, record.PublicUrl, threadId, threadId, selected);
                        store.Save(record with { State = "sent" });
                        sent.Complete(record.JobId, record.Generator, record.ImageIndex);
                        return Results.Json(new { state = "sent", publicUrl = record.PublicUrl,
                            viewUrl = UiPublicShares.ViewUrl(record.PublicUrl, selected), version = sent.Snapshot().Version,
                            item = new { jobId = record.JobId, generator = record.Generator, imageIndex = record.ImageIndex } });
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (claimed && !published && record != null) sent.ReleaseClaim(record.JobId, record.Generator, record.ImageIndex, record.Owner);
                    var uncertainMessage = batchesPlanned > 1 && batchesSent > 0
                        ? $"The page is public. {batchesSent} of {batchesPlanned} Discord messages succeeded, and the next failed or was unconfirmed; check Discord before sending again."
                        : "The page is public. Delivery is unconfirmed; check Discord before sending again.";
                    return Results.Json(new { state = published ? "pending" : "ready", publicUrl = published ? record?.PublicUrl : null,
                        error = published ? uncertainMessage
                            : ex is InvalidOperationException ? ex.Message : "Could not send this share." }, statusCode: 502);
                }
                finally { PublicShareSendLimit.Release(); }
            });
        }
    }
}
