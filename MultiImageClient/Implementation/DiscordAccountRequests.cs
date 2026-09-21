#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed record DiscordAccountMember(string UserId, string Username, string GuildId, string ChannelId);

    public sealed class DiscordDmRejectedException : InvalidOperationException
    {
        public DiscordDmRejectedException(string message) : base(message) { }
    }

    public sealed class DiscordAccountRequestsClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly Settings _settings;
        private readonly HttpClient _http;
        public DiscordAccountRequestsClient(Settings settings, HttpClient? http = null)
        {
            _settings = settings;
            _http = http ?? Http;
        }

        public static string Username(string value)
        {
            var name = value.Trim().ToLowerInvariant();
            if (name.StartsWith('@')) name = name[1..];
            if (!Regex.IsMatch(name, @"\A[a-z0-9_.]{2,32}\z") || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Enter your exact Discord username, not your display name or server nickname.");
            return name;
        }

        public async Task<DiscordAccountMember> FindMemberAsync(string username, CancellationToken ct)
        {
            username = Username(username);
            var destination = await new DiscordVibecodersClient(_settings, _http).GetDestinationAsync(ct);
            using var members = await RequestAsync(HttpMethod.Get,
                $"guilds/{destination.GuildId}/members/search?query={Uri.EscapeDataString(username)}&limit=1000", null, ct);
            if (members.RootElement.ValueKind != JsonValueKind.Array || members.RootElement.GetArrayLength() >= 1000)
                throw new InvalidOperationException("Discord did not return a complete member search. Check your exact username.");
            var matches = members.RootElement.EnumerateArray().Where(member =>
                member.GetProperty("user").GetProperty("username").GetString() == username).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("No exact Discord username matched a member of the Vibecoders server.");
            var userId = Id(matches[0].GetProperty("user").GetProperty("id"));
            var verified = await CheckMemberAsync(userId, destination.GuildId, destination.ChannelId, ct);
            if (verified.Username != username) throw new InvalidOperationException("That Discord username changed. Enter the current username.");
            return verified;
        }

        public async Task<DiscordAccountMember> CheckMemberAsync(string userId, string guildId, string channelId, CancellationToken ct)
        {
            var destination = await new DiscordVibecodersClient(_settings, _http).GetDestinationAsync(ct);
            if (destination.GuildId != guildId || destination.ChannelId != channelId)
                throw new InvalidOperationException("The Discord destination changed. Request a new account link.");
            using var member = await RequestAsync(HttpMethod.Get, $"guilds/{guildId}/members/{userId}", null, ct);
            using var guild = await RequestAsync(HttpMethod.Get, "guilds/" + guildId, null, ct);
            using var channel = await RequestAsync(HttpMethod.Get, "channels/" + channelId, null, ct);
            var user = member.RootElement.GetProperty("user");
            if (Id(user.GetProperty("id")) != userId || Id(guild.RootElement.GetProperty("id")) != guildId
                || Id(channel.RootElement.GetProperty("id")) != channelId
                || Id(channel.RootElement.GetProperty("guild_id")) != guildId || channel.RootElement.GetProperty("type").GetInt32() != 0)
                throw new InvalidOperationException("Discord returned a different member or channel identity.");
            if ((user.TryGetProperty("bot", out var bot) && bot.GetBoolean())
                || (member.RootElement.TryGetProperty("pending", out var pending) && pending.GetBoolean())
                || !CanViewChannel(member.RootElement, guild.RootElement, channel.RootElement))
                throw new InvalidOperationException("This Discord account cannot access the Vibecoders channel.");
            return new(userId, Username(user.GetProperty("username").GetString() ?? ""), guildId, channelId);
        }

        public static bool CanViewChannel(JsonElement member, JsonElement guild, JsonElement channel)
        {
            const ulong administrator = 1UL << 3;
            const ulong viewChannel = 1UL << 10;
            var userId = Id(member.GetProperty("user").GetProperty("id"));
            var guildId = Id(guild.GetProperty("id"));
            if (Id(channel.GetProperty("guild_id")) != guildId) throw new InvalidOperationException("Discord channel identity mismatch.");
            if (Id(guild.GetProperty("owner_id")) == userId) return true;
            var roles = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var role in guild.GetProperty("roles").EnumerateArray())
                if (!roles.TryAdd(Id(role.GetProperty("id")), Permissions(role.GetProperty("permissions"))))
                    throw new InvalidOperationException("Discord returned duplicate roles.");
            if (!roles.TryGetValue(guildId, out var permissions)) throw new InvalidOperationException("Discord omitted the server role.");
            var memberRoles = member.GetProperty("roles").EnumerateArray().Select(Id).ToHashSet(StringComparer.Ordinal);
            foreach (var roleId in memberRoles)
            {
                if (!roles.TryGetValue(roleId, out var role)) throw new InvalidOperationException("Discord omitted a member role.");
                permissions |= role;
            }
            if ((permissions & administrator) != 0) return true;
            ulong everyoneAllow = 0, everyoneDeny = 0, roleAllow = 0, roleDeny = 0, userAllow = 0, userDeny = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var overwrite in channel.GetProperty("permission_overwrites").EnumerateArray())
            {
                var id = Id(overwrite.GetProperty("id"));
                var type = overwrite.GetProperty("type").GetInt32();
                var allow = Permissions(overwrite.GetProperty("allow"));
                var deny = Permissions(overwrite.GetProperty("deny"));
                if (type is not (0 or 1) || !seen.Add(type + "/" + id))
                    throw new InvalidOperationException("Discord returned ambiguous channel permissions.");
                if (type == 0 && id == guildId) { everyoneAllow = allow; everyoneDeny = deny; }
                else if (type == 0 && memberRoles.Contains(id)) { roleAllow |= allow; roleDeny |= deny; }
                else if (type == 1 && id == userId) { userAllow = allow; userDeny = deny; }
            }
            permissions = (permissions & ~everyoneDeny) | everyoneAllow;
            permissions = (permissions & ~roleDeny) | roleAllow;
            permissions = (permissions & ~userDeny) | userAllow;
            return (permissions & viewChannel) != 0;
        }

        public async Task SendLinkAsync(string userId, string link, CancellationToken ct)
        {
            using var dm = await RequestAsync(HttpMethod.Post, "users/@me/channels", new { recipient_id = userId }, ct, delivery: true);
            var channel = dm.RootElement;
            var recipients = channel.GetProperty("recipients");
            if (channel.GetProperty("type").GetInt32() != 1 || recipients.GetArrayLength() != 1
                || Id(recipients[0].GetProperty("id")) != userId)
                throw new InvalidOperationException("Discord returned a different DM recipient. No link was sent.");
            var channelId = Id(channel.GetProperty("id"));
            var content = "Your MultiImageClient account request\n[Continue to Vibecoders](<" + link
                + ">)\nThis link expires in 30 minutes and works once.\nIf you did not request it, ignore this message.";
            using var message = await RequestAsync(HttpMethod.Post, "channels/" + channelId + "/messages",
                new { content, flags = 4, allowed_mentions = new { parse = Array.Empty<string>() } }, ct, delivery: true);
            if (Id(message.RootElement.GetProperty("channel_id")) != channelId
                || message.RootElement.GetProperty("content").GetString() != content)
                throw new InvalidOperationException("Discord returned an unconfirmed DM delivery.");
            _ = Id(message.RootElement.GetProperty("id"));
        }

        private async Task<JsonDocument> RequestAsync(HttpMethod method, string route, object? body, CancellationToken ct, bool delivery = false)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            ct = deadline.Token; // Include the bounded response body, not only response headers.
            if (!FableBot.FableBotDiscord.TryNormalizeBotToken(_settings.DiscordVibecodersBotToken, out var token))
                throw new InvalidOperationException("Discord account requests are not configured.");
            using var request = new HttpRequestMessage(method, "https://discord.com/api/v10/" + route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            request.Headers.UserAgent.ParseAdd("MultiImageClient/1.0");
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (delivery && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                    throw new DiscordDmRejectedException("Discord could not deliver the DM. Check your message privacy settings before requesting another link.");
                throw new InvalidOperationException($"Discord could not verify this request (HTTP {(int)response.StatusCode}). Try again later.");
            }
            await response.Content.LoadIntoBufferAsync(1_048_576, ct);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        private static string Id(JsonElement value)
        {
            var id = value.GetString() ?? "";
            return ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? id : throw new InvalidOperationException("Discord returned an invalid identity.");
        }
        private static ulong Permissions(JsonElement value) =>
            ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
                ? bits : throw new InvalidOperationException("Discord returned invalid permissions.");
    }
}
