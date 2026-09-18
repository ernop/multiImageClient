#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class DiscordVibecoders
    {
        public const long MaxAttachmentBytes = 10L * 1024 * 1024;

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
            string? threadId = null)
        {
            if (media == Stream.Null || !media.CanRead || !media.CanSeek
                || media.Length - media.Position <= 0 || media.Length - media.Position > DiscordVibecoders.MaxAttachmentBytes)
            {
                throw new InvalidOperationException("Discord requires an original attachment of at most 10 MiB.");
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
                content = publicUrl == null ? null : UiPublicShares.Caption(publicUrl),
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
                    || json.RootElement.GetProperty("content").GetString() != UiPublicShares.Caption(publicUrl)
                    || json.RootElement.GetProperty("attachments").GetArrayLength() != 1)
                    throw new InvalidOperationException("Discord returned a different message identity or content.");
            }
        }
    }
}
