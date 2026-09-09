#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FableBot
{
    /// Pure validation and message-shape rules for FableBot's Discord
    /// posting, kept static and network-free so tests cover them directly.
    public static class FableBotDiscord
    {
        // Discord hard limit for message content.
        public const int MaxMessageChars = 2000;

        // Discord hard limit for attachments on one message.
        public const int MaxAttachmentsPerMessage = 10;

        // Upload cap for servers without a boost tier.
        public const long MaxAttachmentBytes = 10L * 1024 * 1024;

        public static bool TryNormalizeChannelId(string? raw, out ulong channelId)
        {
            channelId = 0;
            var s = raw?.Trim() ?? "";
            // Snowflakes are 17-20 digits today; allow small margins.
            if (s.Length < 15 || s.Length > 21)
            {
                return false;
            }
            foreach (var c in s)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out channelId)
                && channelId != 0;
        }

        public static bool TryNormalizeBotToken(string? raw, out string token)
        {
            token = "";
            var s = raw?.Trim() ?? "";
            if (s.Length < 50)
            {
                return false;
            }
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    return false;
                }
            }
            token = s;
            return true;
        }

        /// Throws with the exact rule violated; returns silently when the
        /// message is postable. Content and attachments are both optional,
        /// but at least one must be present.
        public static void ValidateOutgoingMessage(
            string content,
            IReadOnlyList<FableBotAttachment> attachments)
        {
            if (content.Length == 0 && attachments.Count == 0)
            {
                throw new InvalidOperationException(
                    "A Discord message needs text content, at least one file, or both.");
            }
            if (content.Length > MaxMessageChars)
            {
                throw new InvalidOperationException(
                    $"Message content is {content.Length} characters; Discord's limit is {MaxMessageChars}. Shorten the message.");
            }
            if (attachments.Count > MaxAttachmentsPerMessage)
            {
                throw new InvalidOperationException(
                    $"{attachments.Count} files were given; Discord allows at most {MaxAttachmentsPerMessage} per message.");
            }
            foreach (var attachment in attachments)
            {
                if (attachment.Bytes.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment '{attachment.FileName}' is empty.");
                }
                if (attachment.Bytes.Length > MaxAttachmentBytes)
                {
                    throw new InvalidOperationException(
                        $"Attachment '{attachment.FileName}' is {attachment.Bytes.Length} bytes; the upload cap is {MaxAttachmentBytes} bytes (10 MiB).");
                }
            }
        }

        /// Magic-byte sniffing for the media types FableBot posts. Unknown
        /// bytes are sent as application/octet-stream, which Discord accepts
        /// for any attachment; the true bytes travel verbatim either way.
        public static string DetectContentType(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }
            if (bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }
            if (bytes.Length >= 6
                && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "image/gif";
            }
            if (bytes.Length >= 12
                && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                return "video/mp4";
            }
            return "application/octet-stream";
        }
    }

    public sealed record FableBotAttachment(string FileName, byte[] Bytes);

    public sealed record FableBotIdentity(string Id, string Username);

    public sealed record FableBotChannelInfo(string Id, int Type, string Name, string? GuildId);

    public sealed record FableBotPostedMessage(string MessageId, string ChannelId, string? GuildId)
    {
        // guild_id is absent from REST message responses; the caller supplies
        // it from the channel lookup so a jump link can be printed.
        public string JumpUrl =>
            $"https://discord.com/channels/{GuildId ?? "@me"}/{ChannelId}/{MessageId}";
    }

    /// Minimal Discord bot REST client (API v10). Post-only in v1: identity
    /// check, channel lookup, and message create. No gateway connection.
    public sealed class FableBotDiscordClient
    {
        private const string ApiBase = "https://discord.com/api/v10";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        private readonly string _token;
        private readonly HttpClient _http;

        public FableBotDiscordClient(string botToken, HttpClient? httpClient = null)
        {
            if (!FableBotDiscord.TryNormalizeBotToken(botToken, out var token))
            {
                throw new InvalidOperationException(
                    "FableBotDiscordBotToken is not a usable bot token (blank, too short, or contains whitespace).");
            }
            _token = token;
            _http = httpClient ?? Http;
        }

        public async Task<FableBotIdentity> GetBotIdentityAsync(CancellationToken cancellationToken)
        {
            using var document = await GetJsonAsync("/users/@me", cancellationToken);
            var root = document.RootElement;
            var id = RequiredString(root, "id", "GET /users/@me");
            var username = RequiredString(root, "username", "GET /users/@me");
            return new FableBotIdentity(id, username);
        }

        public async Task<FableBotChannelInfo> GetChannelAsync(
            ulong channelId, CancellationToken cancellationToken)
        {
            using var document = await GetJsonAsync($"/channels/{channelId}", cancellationToken);
            var root = document.RootElement;
            var id = RequiredString(root, "id", $"GET /channels/{channelId}");
            if (id != channelId.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Discord returned a different channel identity.");
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException(
                    $"Discord's channel response for {channelId} is missing the numeric 'type' field.");
            }
            var name = root.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? ""
                : "";
            string? guildId = root.TryGetProperty("guild_id", out var guildElement)
                && guildElement.ValueKind == JsonValueKind.String
                ? guildElement.GetString()
                : null;
            return new FableBotChannelInfo(id, typeElement.GetInt32(), name, guildId);
        }

        public async Task<FableBotPostedMessage> PostMessageAsync(
            ulong channelId,
            string content,
            IReadOnlyList<FableBotAttachment> attachments,
            string? guildIdForLink,
            CancellationToken cancellationToken)
        {
            FableBotDiscord.ValidateOutgoingMessage(content, attachments);

            using var form = new MultipartFormDataContent();
            var payload = new
            {
                content = content.Length == 0 ? null : content,
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
            for (int i = 0; i < attachments.Count; i++)
            {
                var file = new ByteArrayContent(attachments[i].Bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(
                    FableBotDiscord.DetectContentType(attachments[i].Bytes));
                form.Add(file, $"files[{i}]", attachments[i].FileName);
            }

            using var request = BuildRequest(HttpMethod.Post, $"/channels/{channelId}/messages");
            request.Content = form;
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Discord rejected the message ({(int)response.StatusCode}): {Truncate(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var messageId = RequiredString(
                document.RootElement, "id", $"POST /channels/{channelId}/messages");
            var returnedChannelId = RequiredString(
                document.RootElement, "channel_id", $"POST /channels/{channelId}/messages");
            if (returnedChannelId != channelId.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Discord returned a different message channel identity.");
            if (!document.RootElement.TryGetProperty("attachments", out var returnedAttachments)
                || returnedAttachments.ValueKind != JsonValueKind.Array
                || returnedAttachments.GetArrayLength() != attachments.Count)
                throw new InvalidOperationException("Discord did not confirm every attachment.");
            for (var i = 0; i < attachments.Count; i++)
            {
                var received = returnedAttachments[i];
                if (!received.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length)
                    || length != attachments[i].Bytes.LongLength)
                    throw new InvalidOperationException("Discord returned an unexpected attachment size.");
            }
            return new FableBotPostedMessage(messageId, returnedChannelId, guildIdForLink);
        }

        private async Task<JsonDocument> GetJsonAsync(
            string path, CancellationToken cancellationToken)
        {
            using var request = BuildRequest(HttpMethod.Get, path);
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Discord rejected {path} ({(int)response.StatusCode}): {Truncate(body)}");
            }
            return JsonDocument.Parse(body);
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bot " + _token);
            // Discord requires bots to send a DiscordBot user agent.
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "DiscordBot (https://github.com/ernop/multiImageClient, 1.0)");
            return request;
        }

        private static string RequiredString(JsonElement root, string property, string operation)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(property, out var element)
                || element.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(element.GetString()))
            {
                throw new InvalidOperationException(
                    $"Discord's response to {operation} is missing the '{property}' field; refusing to continue with a partial response.");
            }
            return element.GetString()!;
        }

        private static string Truncate(string body)
        {
            var trimmed = body.Trim();
            return trimmed.Length <= 600 ? trimmed : trimmed[..600] + "…";
        }
    }
}
