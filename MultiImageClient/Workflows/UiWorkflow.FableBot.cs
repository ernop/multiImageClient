#nullable enable
using System;
using System.IO;
using System.Threading;
using FableBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        private static readonly SemaphoreSlim FableBotSendLimit = new(1, 1);

        private static void MapFableBot(WebApplication app, Settings settings, UiAuth? auth,
            UiJobRegistry jobs, UiVisibilityStore visibility, UiJobRunner runner)
        {
            var available = FableBotDiscord.TryNormalizeBotToken(settings.FableBotDiscordBotToken, out var token)
                && FableBotDiscord.TryNormalizeChannelId(settings.FableBotDiscordChannelId, out _);
            var store = available ? new UiFableBotStore(settings.ImageDownloadBaseFolder) : null;
            var channel = available ? ulong.Parse(settings.FableBotDiscordChannelId.Trim()) : 0;
            var channelKey = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);

            app.MapGet("/api/discord/fablebot", (HttpContext ctx, string? jobId, string? generator, int? imageIndex) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!available) return Results.Json(new { available = false });
                if (jobId == null && generator == null && imageIndex == null)
                    return Results.Json(new { available = true });
                if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(generator) || imageIndex is null or < 0)
                    return Results.BadRequest(new { error = "An exact image identity is required." });
                if (jobs.Get(jobId) == null || visibility.IsPromptHidden(jobId)
                    || visibility.IsImageHidden(jobId, generator, imageIndex.Value)) return Results.NotFound();
                var record = store!.Get(channelKey, jobId, generator, imageIndex.Value);
                return Results.Json(new { available = true, state = record?.State ?? "ready", jumpUrl = record?.JumpUrl });
            });

            app.MapPost("/api/discord/fablebot", async (HttpRequest request) =>
            {
                if (!available) return Results.NotFound(new { error = "FableBot is not configured on this instance." });
                var sender = request.HttpContext.Items["micUser"] as string ?? "";
                if (auth != null && sender.Length == 0) return Results.StatusCode(403);
                // Custom-header requests require same-origin access; ordinary cross-site forms cannot post.
                if (request.Headers["X-MIC-FableBot"] != "1") return Results.StatusCode(403);
                var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
                var jobId = form["jobId"].ToString();
                var generator = form["generator"].ToString();
                if (jobId.Length == 0 || generator.Length == 0
                    || !int.TryParse(form["imageIndex"], out var index) || index < 0)
                    return Results.BadRequest(new { error = "An exact image identity is required." });
                var job = jobs.Get(jobId);
                if (job == null || visibility.IsPromptHidden(jobId) || visibility.IsImageHidden(jobId, generator, index))
                    return Results.NotFound(new { error = "The selected result is unavailable." });
                if (!TryResolveSendMedia(job, generator, index, out _, out var error))
                    return Results.BadRequest(new { error });
                if (!await FableBotSendLimit.WaitAsync(0))
                    return Results.Json(new { error = "Another Discord send is in progress." }, statusCode: 409);
                var claimed = false;
                var failure = "The original is unavailable, empty, or exceeds the 10 MiB attachment limit.";
                try
                {
                    if (store!.Get(channelKey, jobId, generator, index) != null)
                        return Results.Json(new { error = "This result was sent or its delivery needs checking in Discord." }, statusCode: 409);
                    var ct = request.HttpContext.RequestAborted;
                    var original = await runner.TryGetImageBytesIncludingHostedAsync(
                        job, generator, index, FableBotDiscord.MaxAttachmentBytes, ct)
                        ?? throw new InvalidOperationException("The selected original is unavailable.");
                    var type = FableBotDiscord.DetectContentType(original.Bytes);
                    if (!type.StartsWith("image/", StringComparison.Ordinal) && type != "video/mp4")
                        return Results.BadRequest(new { state = "ready", error = "Send a PNG, JPEG, WEBP, GIF, or MP4 original." });
                    var fileName = generator + "-" + index + DiscordVibecoders.FileExtension(type);
                    var attachments = new[] { new FableBotAttachment(fileName, original.Bytes) };
                    var content = $"{generator} — shared by {(sender.Length == 0 ? "local" : sender)}";
                    FableBotDiscord.ValidateOutgoingMessage(content, attachments);
                    failure = "FableBot could not verify its token or configured channel. Run FableBot --check.";
                    var client = new FableBotDiscordClient(token);
                    await client.GetBotIdentityAsync(ct);
                    var target = await client.GetChannelAsync(channel, ct);
                    failure = "The Discord send record could not be saved.";
                    var record = new UiFableBotSend(channelKey, jobId, generator, index,
                        sender.Length == 0 ? "local" : sender, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (!store.TryClaim(record)) return Results.StatusCode(409);
                    claimed = true;
                    var posted = await client.PostMessageAsync(channel, content, attachments, target.GuildId, ct);
                    store.Complete(record, posted.JumpUrl);
                    return Results.Json(new { state = "sent", jumpUrl = posted.JumpUrl });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // After dispatch, a lost response cannot prove that Discord did not create the message.
                    // Keep the durable pending record; never offer an automatic duplicate send.
                    return Results.Json(new { state = claimed ? "pending" : "ready", error = claimed
                        ? "Delivery is unconfirmed. Check Discord before any further send."
                        : failure }, statusCode: 502);
                }
                finally { FableBotSendLimit.Release(); }
            });
        }
    }
}
