#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed record DiscordVibecodersAttachment(
        Stream Media,
        string ContentType,
        string FileName,
        string? Description = null);

    public sealed record DiscordVibecodersBatchPlan(int StartIndex, int Count, long TotalBytes);

    public static class DiscordVibecoders
    {
        // Discord raised the default per-file limit from 10 MiB to 20 MiB on 2026-09-03,
        // while keeping the 25 MiB total HTTP request body cap and 10 files per message.
        public const long MaxAttachmentBytes = 20L * 1024 * 1024;
        public const int MaxAttachmentsPerMessage = 10;
        // Keep 1 MiB headroom below Discord's 25 MiB HTTP request limit for multipart framing and JSON.
        public const long MaxRequestPayloadBytes = 24L * 1024 * 1024;

        public static IReadOnlyList<DiscordVibecodersBatchPlan> PlanAttachmentBatches(
            IReadOnlyList<long> byteLengths,
            int maxPerMessage = MaxAttachmentsPerMessage,
            long maxBatchBytes = MaxRequestPayloadBytes)
        {
            if (byteLengths == null || byteLengths.Count == 0)
                throw new InvalidOperationException("At least one original attachment is required.");
            if (maxPerMessage < 1 || maxPerMessage > MaxAttachmentsPerMessage)
                throw new ArgumentOutOfRangeException(nameof(maxPerMessage));
            if (maxBatchBytes < 1 || maxBatchBytes > MaxRequestPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBatchBytes));

            var n = byteLengths.Count;
            var prefix = new long[n + 1];
            for (var i = 0; i < n; i++)
            {
                var len = byteLengths[i];
                if (len <= 0 || len > MaxAttachmentBytes || len > maxBatchBytes)
                    throw new InvalidOperationException(
                        $"Page {i + 1} is unavailable or exceeds Discord's {MaxAttachmentBytes / (1024 * 1024)} MiB per-file attachment limit.");
                prefix[i + 1] = prefix[i] + len;
            }

            // Greedy pass gives the exact minimum number of contiguous messages needed.
            var minBatches = 0;
            var pos = 0;
            while (pos < n)
            {
                long sum = 0;
                var taken = 0;
                while (pos < n && taken < maxPerMessage && sum + byteLengths[pos] <= maxBatchBytes)
                {
                    sum += byteLengths[pos];
                    pos++;
                    taken++;
                }
                minBatches++;
            }

            if (minBatches == 1)
                return new[] { new DiscordVibecodersBatchPlan(0, n, prefix[n]) };

            // Partition into exactly minBatches contiguous batches minimizing max batch size
            // (and balancing page counts on ties) so multi-message uploads stay visually even.
            const long Inf = long.MaxValue / 4;
            var dpMaxBytes = new long[minBatches + 1, n + 1];
            var dpMaxCount = new int[minBatches + 1, n + 1];
            var prevSplit = new int[minBatches + 1, n + 1];
            for (var b = 0; b <= minBatches; b++)
            {
                for (var i = 0; i <= n; i++)
                {
                    dpMaxBytes[b, i] = Inf;
                    dpMaxCount[b, i] = int.MaxValue;
                    prevSplit[b, i] = -1;
                }
            }
            dpMaxBytes[0, 0] = 0;
            dpMaxCount[0, 0] = 0;

            for (var b = 1; b <= minBatches; b++)
            {
                for (var end = b; end <= n; end++)
                {
                    for (var start = Math.Max(b - 1, end - maxPerMessage); start < end; start++)
                    {
                        if (dpMaxBytes[b - 1, start] >= Inf) continue;
                        var batchBytes = prefix[end] - prefix[start];
                        if (batchBytes > maxBatchBytes) continue;
                        var candMaxBytes = Math.Max(dpMaxBytes[b - 1, start], batchBytes);
                        var candMaxCount = Math.Max(dpMaxCount[b - 1, start], end - start);
                        if (candMaxCount < dpMaxCount[b, end]
                            || (candMaxCount == dpMaxCount[b, end] && candMaxBytes <= dpMaxBytes[b, end]))
                        {
                            dpMaxCount[b, end] = candMaxCount;
                            dpMaxBytes[b, end] = candMaxBytes;
                            prevSplit[b, end] = start;
                        }
                    }
                }
            }

            var plan = new DiscordVibecodersBatchPlan[minBatches];
            var cursor = n;
            for (var b = minBatches; b >= 1; b--)
            {
                var start = prevSplit[b, cursor];
                if (start < 0)
                    throw new InvalidOperationException("Could not plan Discord attachment batches.");
                plan[b - 1] = new DiscordVibecodersBatchPlan(start, cursor - start, prefix[cursor] - prefix[start]);
                cursor = start;
            }
            return plan;
        }

        public static bool IsConfigured(Settings settings)
        {
            return TryNormalizeWebhookUrl(settings.DiscordVibecodersWebhookUrl, out _);
        }

        public static bool TryNormalizeWebhookUrl(string? raw, out string url)
        {
            url = "";
            if (string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || (!string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase))
                || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Length < "/api/webhooks/x/y".Length)
            {
                return false;
            }
            url = uri.AbsoluteUri;
            return true;
        }

        public static bool TryNormalizePublicBaseUrl(string? raw, out string url)
        {
            url = "";
            if (string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(raw.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || uri.AbsolutePath.Length < 2)
            {
                return false;
            }
            url = uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        public static string FileExtension(string contentType)
        {
            return contentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/svg+xml" => ".svg",
                "video/mp4" => ".mp4",
                "video/webm" => ".webm",
                _ => "",
            };
        }
    }

    public sealed class DiscordVibecodersClient
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        private readonly string _webhookUrl;
        private readonly bool _botTesting;
        private readonly Settings _settings;
        private readonly HttpClient _http;

        public DiscordVibecodersClient(Settings settings, HttpClient? httpClient = null, string target = "vibecoders")
        {
            if (target is not ("vibecoders" or "bot-testing")) throw new InvalidOperationException("Unknown Discord target.");
            _botTesting = target == "bot-testing";
            var url = "";
            if (!_botTesting && !DiscordVibecoders.TryNormalizeWebhookUrl(settings.DiscordVibecodersWebhookUrl, out url))
            {
                throw new InvalidOperationException("Discord vibecoders webhook is not configured.");
            }
            _webhookUrl = url;
            _settings = settings;
            _http = httpClient ?? Http;
        }

        public async Task<(string GuildId, string ChannelId)> GetDestinationAsync(CancellationToken cancellationToken)
        {
            if (_botTesting)
            {
                if (!ulong.TryParse(_settings.DiscordBotTestingGuildId, out var g) || g == 0
                    || !ulong.TryParse(_settings.DiscordBotTestingChannelId, out var c) || c == 0)
                    throw new InvalidOperationException("Configure the Bot testing server and channel before sharing.");
                return (_settings.DiscordBotTestingGuildId, _settings.DiscordBotTestingChannelId);
            }
            if (new Uri(_webhookUrl).Query.Length != 0)
                throw new InvalidOperationException("Public sharing requires a webhook without thread or query overrides.");
            using var request = new HttpRequestMessage(HttpMethod.Get, _webhookUrl);
            request.Headers.UserAgent.ParseAdd("MultiImageClient/1.0");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Could not verify the Discord destination.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var guild = json.RootElement.GetProperty("guild_id").GetString() ?? "";
            var channel = json.RootElement.GetProperty("channel_id").GetString() ?? "";
            if (!ulong.TryParse(guild, out var guildId) || guildId == 0 || !ulong.TryParse(channel, out var channelId) || channelId == 0)
                throw new InvalidOperationException("Discord returned an invalid server or channel identity.");
            return (guild, channel);
        }

        public async Task<(string GuildId, string ChannelId, string ServerName, string ChannelName)> GetDailyDestinationAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_settings.DiscordVibecodersThreadStorePath)
                || !Path.IsPathFullyQualified(_settings.DiscordVibecodersThreadStorePath))
                throw new InvalidOperationException("Configure the shared Discord daily-thread store before sharing.");
            var target = await GetDestinationAsync(ct);
            using var channel = await BotRequestAsync(HttpMethod.Get, "channels/" + target.ChannelId, null, ct);
            using var guild = await BotRequestAsync(HttpMethod.Get, "guilds/" + target.GuildId, null, ct);
            var c = channel.RootElement;
            var g = guild.RootElement;
            if (c.GetProperty("id").GetString() != target.ChannelId || c.GetProperty("guild_id").GetString() != target.GuildId
                || c.GetProperty("type").GetInt32() != 0 || g.GetProperty("id").GetString() != target.GuildId)
                throw new InvalidOperationException("Daily images require the selected Discord text channel.");
            var serverName = g.GetProperty("name").GetString();
            var channelName = c.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(channelName))
                throw new InvalidOperationException("Discord did not return the destination names.");
            return (target.GuildId, target.ChannelId, serverName, channelName);
        }

        public Task<string> GetDailyThreadAsync(string guildId, string channelId, string day, CancellationToken ct) =>
            DiscordDailyThreads.GetOrCreateAsync(_settings.DiscordVibecodersThreadStorePath, guildId, channelId, day,
                async () =>
                {
                    using var created = await BotRequestAsync(HttpMethod.Post, "channels/" + channelId + "/threads",
                        new { name = DiscordDailyThreads.Name(day), type = 11, auto_archive_duration = 1440 }, ct);
                    return ValidateThread(created.RootElement, guildId, channelId, day, null);
                },
                async threadId =>
                {
                    using var thread = await BotRequestAsync(HttpMethod.Get, "channels/" + threadId, null, ct);
                    ValidateThread(thread.RootElement, guildId, channelId, day, threadId);
                });

        private static string ValidateThread(JsonElement thread, string guildId, string channelId, string day, string? expectedId)
        {
            var id = thread.GetProperty("id").GetString() ?? "";
            if (!ulong.TryParse(id, out var parsed) || parsed == 0 || (expectedId != null && id != expectedId)
                || thread.GetProperty("guild_id").GetString() != guildId || thread.GetProperty("parent_id").GetString() != channelId
                || thread.GetProperty("type").GetInt32() != 11 || thread.GetProperty("name").GetString() != DiscordDailyThreads.Name(day)
                || thread.GetProperty("thread_metadata").GetProperty("locked").GetBoolean())
                throw new InvalidOperationException("The daily thread is locked or has a different identity. Sharing stopped.");
            return id;
        }

        private async Task<JsonDocument> BotRequestAsync(HttpMethod method, string route, object? body, CancellationToken ct)
        {
            if (!FableBot.FableBotDiscord.TryNormalizeBotToken(_settings.DiscordVibecodersBotToken, out var token))
                throw new InvalidOperationException("Configure the Vibecoders bot token to create daily image threads.");
            using var request = new HttpRequestMessage(method, "https://discord.com/api/v10/" + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            request.Headers.UserAgent.ParseAdd("MultiImageClient/1.0");
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Discord daily-thread request failed ({(int)response.StatusCode}). Check bot access and permissions.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        public async Task SendAsync(
            string senderName,
            Stream media,
            string contentType,
            string fileName,
            CancellationToken cancellationToken,
            string? publicUrl = null,
            string? expectedChannelId = null,
            string? threadId = null,
            int? publicOutputSlot = null)
        {
            if (media == Stream.Null || !media.CanRead || !media.CanSeek
                || media.Length - media.Position <= 0 || media.Length - media.Position > DiscordVibecoders.MaxAttachmentBytes)
            {
                throw new InvalidOperationException(
                    $"Discord requires an original attachment of at most {DiscordVibecoders.MaxAttachmentBytes / (1024 * 1024)} MiB.");
            }

            var username = string.IsNullOrWhiteSpace(senderName) ? "miic" : senderName.Trim();
            if (username.Length > 80)
            {
                username = username[..80];
            }

            using var form = new MultipartFormDataContent();
            var payload = new
            {
                username = _botTesting ? null : username,
                content = publicUrl == null ? null : UiPublicShares.Caption(publicUrl, publicOutputSlot),
                flags = publicUrl == null ? (int?)null : 4,
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
            form.Add(
                new StringContent(
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        }),
                    Encoding.UTF8),
                "payload_json");

            if (media != Stream.Null)
            {
                var file = new StreamContent(media);
                file.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
                form.Add(file, "files[0]", fileName);
            }

            if (publicUrl != null && (!ulong.TryParse(threadId, out var parsedThread) || parsedThread == 0 || threadId != expectedChannelId))
                throw new InvalidOperationException("Confirmed public shares must target the exact daily thread.");
            if (_botTesting && publicUrl == null) throw new InvalidOperationException("Bot testing requires a confirmed public preview.");
            var destination = _botTesting ? "https://discord.com/api/v10/channels/" + threadId + "/messages"
                : publicUrl == null ? _webhookUrl : _webhookUrl + "?wait=true&thread_id=" + threadId;
            using var upload = new HttpRequestMessage(HttpMethod.Post, destination) { Content = form };
            if (_botTesting)
            {
                if (!FableBot.FableBotDiscord.TryNormalizeBotToken(_settings.DiscordVibecodersBotToken, out var botToken))
                    throw new InvalidOperationException("Configure the Discord bot token before sharing.");
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            }
            using var response = await _http.SendAsync(upload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Discord rejected the upload ({(int)response.StatusCode}).");
            }
            if (publicUrl != null)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (json.RootElement.GetProperty("channel_id").GetString() != expectedChannelId
                    || json.RootElement.GetProperty("content").GetString() != UiPublicShares.Caption(publicUrl, publicOutputSlot)
                    || json.RootElement.GetProperty("attachments").GetArrayLength() != 1)
                    throw new InvalidOperationException("Discord returned a different message identity or content.");
            }
        }

        public async Task SendBatchAsync(
            string senderName,
            IReadOnlyList<DiscordVibecodersAttachment> attachments,
            string content,
            string expectedChannelId,
            string threadId,
            CancellationToken cancellationToken)
        {
            if (attachments == null || attachments.Count == 0 || attachments.Count > DiscordVibecoders.MaxAttachmentsPerMessage)
                throw new InvalidOperationException($"Discord accepts between 1 and {DiscordVibecoders.MaxAttachmentsPerMessage} attachments per message.");
            if (string.IsNullOrWhiteSpace(content) || content.Length > 2000)
                throw new InvalidOperationException("Discord caption text is missing or exceeds 2,000 characters.");
            if (!ulong.TryParse(threadId, out var parsedThread) || parsedThread == 0 || threadId != expectedChannelId)
                throw new InvalidOperationException("Confirmed public shares must target the exact daily thread.");

            long totalBytes = 0;
            for (var i = 0; i < attachments.Count; i++)
            {
                var item = attachments[i];
                if (item.Media == Stream.Null || !item.Media.CanRead || !item.Media.CanSeek)
                    throw new InvalidOperationException("Each Discord attachment must be a readable stream.");
                var len = item.Media.Length - item.Media.Position;
                if (len <= 0 || len > DiscordVibecoders.MaxAttachmentBytes)
                    throw new InvalidOperationException(
                        $"Attachment '{item.FileName}' exceeds Discord's {DiscordVibecoders.MaxAttachmentBytes / (1024 * 1024)} MiB per-file limit.");
                totalBytes += len;
            }
            if (totalBytes > DiscordVibecoders.MaxRequestPayloadBytes)
                throw new InvalidOperationException("Discord attachment batch exceeds the 24 MiB request payload budget.");

            var username = string.IsNullOrWhiteSpace(senderName) ? "miic" : senderName.Trim();
            if (username.Length > 80) username = username[..80];

            var attachmentMeta = attachments.Select((item, idx) => new
            {
                id = idx,
                filename = item.FileName,
                description = string.IsNullOrWhiteSpace(item.Description)
                    ? null
                    : (item.Description.Length > 1024 ? item.Description[..1024] : item.Description),
            }).ToArray();

            using var form = new MultipartFormDataContent();
            var payload = new
            {
                username = _botTesting ? null : username,
                content,
                flags = 4,
                allowed_mentions = new { parse = Array.Empty<string>() },
                attachments = attachmentMeta,
            };
            form.Add(
                new StringContent(
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        }),
                    Encoding.UTF8),
                "payload_json");

            for (var i = 0; i < attachments.Count; i++)
            {
                var item = attachments[i];
                var file = new StreamContent(item.Media);
                file.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType);
                form.Add(file, $"files[{i}]", item.FileName);
            }

            var destination = _botTesting
                ? "https://discord.com/api/v10/channels/" + threadId + "/messages"
                : _webhookUrl + "?wait=true&thread_id=" + threadId;
            using var upload = new HttpRequestMessage(HttpMethod.Post, destination) { Content = form };
            if (_botTesting)
            {
                if (!FableBot.FableBotDiscord.TryNormalizeBotToken(_settings.DiscordVibecodersBotToken, out var botToken))
                    throw new InvalidOperationException("Configure the Discord bot token before sharing.");
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            }
            using var response = await _http.SendAsync(upload, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Discord rejected the upload ({(int)response.StatusCode}).");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.GetProperty("channel_id").GetString() != expectedChannelId
                || json.RootElement.GetProperty("content").GetString() != content
                || json.RootElement.GetProperty("attachments").GetArrayLength() != attachments.Count)
                throw new InvalidOperationException("Discord returned a different message identity, caption, or attachment count.");
            foreach (var returned in json.RootElement.GetProperty("attachments").EnumerateArray())
            {
                if (string.IsNullOrWhiteSpace(returned.GetProperty("id").GetString()))
                    throw new InvalidOperationException("Discord returned an attachment without an identity.");
            }
        }
    }
}
