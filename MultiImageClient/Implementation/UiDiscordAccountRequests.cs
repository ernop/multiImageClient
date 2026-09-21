#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // The original environment controller is the sole writer, as with administration.
    public sealed partial class UiDiscordAccountRequests
    {
        public const string EnvironmentId = "vibecoders-ai-generation";
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ReviewLifetime = TimeSpan.FromHours(24);
        private readonly Settings _settings;
        private readonly UiAuth _auth;
        private readonly UiEnvironmentRegistry _environments;
        private readonly Func<DiscordAccountRequestsClient> _client;
        private readonly Func<DateTimeOffset> _now;
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false };

        public sealed class Ticket
        {
            public required string Id { get; init; }
            public required string TokenHash { get; set; }
            public required string UserId { get; init; }
            public required string Username { get; init; }
            public required string GuildId { get; init; }
            public required string ChannelId { get; init; }
            public required long CreatedAt { get; init; }
            public required long ExpiresAt { get; set; }
            public bool ReviewRequired { get; init; }
            public string ReviewerId { get; set; } = "";
            public long TestedAt { get; set; }
            public long ApprovedAt { get; set; }
            public string ShareToken { get; init; } = "";
            public string State { get; set; } = "pending";
            public string AccountId { get; set; } = "";
            public string Login { get; set; } = "";
            public bool CreatesAccount { get; set; }
        }
        public sealed record Attempt(string IpHash, long At);
        public sealed class Document
        {
            public int Version { get; init; } = 1;
            public List<Ticket> Tickets { get; init; } = new();
            public List<Attempt> Attempts { get; init; } = new();
        }
        public sealed record Delivery(string State, string Message);
        public sealed record SignIn(string Cookie, string Login, string Destination);

        public UiDiscordAccountRequests(Settings settings, UiAuth auth, UiEnvironmentRegistry environments,
            Func<DiscordAccountRequestsClient>? client = null, Func<DateTimeOffset>? now = null)
        {
            _settings = settings;
            _auth = auth;
            _environments = environments;
            _client = client ?? (() => new DiscordAccountRequestsClient(settings));
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _path = Path.Combine(settings.ImageDownloadBaseFolder, "UiDiscordAccountRequests", "requests.json");
            if (settings.UiEnvironmentController)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (!File.Exists(_path)) Write(new());
                _ = Read();
            }
        }

        public static string? PublicUrl(Settings settings, UiAuth? auth, UiEnvironmentRegistry? environments)
        {
            if (auth?.LoginLinks == null || environments == null) return null;
            var all = environments.Read().Environments;
            if (all.SingleOrDefault(environment => environment.Id == EnvironmentId) is not { Original: false, DiscordAccountRequests: true })
                return null;
            var original = all.Single(environment => environment.Original);
            var origin = new Uri(UiPublicShares.BaseUrl(settings)).GetLeftPart(UriPartial.Authority);
            return origin + "/shared/" + original.Id + "/signup";
        }

        public bool Enabled => _settings.UiEnvironmentController && _environments.Get(_settings.UiEnvironmentId).Original
            && PublicUrl(_settings, _auth, _environments) != null;

        public async Task<Delivery> RequestAsync(string username, string ip, string shareToken, CancellationToken ct)
        {
            if (!Enabled) throw new InvalidOperationException("Discord account requests are disabled.");
            if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another account request is in progress. Try again shortly.");
            try
            {
                var now = _now().ToUnixTimeMilliseconds();
                var doc = Read();
                Prune(doc, now);
                var ipHash = Hash(ip);
                if (doc.Attempts.Count >= 60 || doc.Attempts.Count(a => a.IpHash == ipHash) >= 12)
                    throw new InvalidOperationException("Too many account requests. Try again in an hour.");
                doc.Attempts.Add(new(ipHash, now));
                Write(doc);
                username = DiscordAccountRequestsClient.Username(username);
                if (shareToken.Length > 0 && !UiPublicShareStore.ValidToken(shareToken))
                    throw new InvalidOperationException("Invalid shared prompt address.");
                var client = _client();
                var member = await client.FindMemberAsync(username, ct);
                var previous = doc.Tickets.Where(ticket => ticket.UserId == member.UserId).ToArray();
                if (previous.Any(ticket => ticket.ExpiresAt > now && ticket.State is not ("used" or "failed" or "rejected")))
                    return new("pending", previous.Any(ticket => ticket.ExpiresAt > now && IsReview(ticket))
                        ? "An account request is already pending. Ernie must review it before the bot sends your link."
                        : "An account link is already pending. Check your Discord DMs before requesting another link.");
                if (previous.Any(ticket => ticket.CreatedAt > now - (long)TimeSpan.FromMinutes(5).TotalMilliseconds)
                    || previous.Count(ticket => ticket.CreatedAt > now - (long)TimeSpan.FromHours(1).TotalMilliseconds) >= 3)
                    throw new InvalidOperationException("A link was requested recently for this Discord account. Try again later.");
                var existing = _auth.LoginLinks!.List().SingleOrDefault(account => account.DiscordUserId == member.UserId);
                if (existing != null && (existing.Revoked || !_environments.CanEnter(EnvironmentId, existing.Login)))
                    throw new InvalidOperationException("This account's access is disabled. Ask Ernie in Discord.");
                if (doc.Tickets.Count >= 2048) throw new InvalidOperationException("Account requests are full. Try again later.");
                doc.Tickets.Add(new Ticket { Id = Guid.NewGuid().ToString("N"), TokenHash = Hash(NewSecret()),
                    UserId = member.UserId, Username = member.Username, GuildId = member.GuildId, ChannelId = member.ChannelId,
                    CreatedAt = now, ExpiresAt = now + (long)ReviewLifetime.TotalMilliseconds, ShareToken = shareToken,
                    ReviewRequired = true, State = "review" });
                Write(doc);
                return new("review", "Your request is waiting for Ernie's review. After approval, the bot will DM your account.");
            }
            finally { _gate.Release(); }
        }

        public async Task<SignIn> RedeemAsync(string token, CancellationToken ct)
        {
            if (!Enabled) throw new InvalidOperationException("Discord account requests are disabled.");
            if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Another account request is in progress. Try again shortly.");
            try
            {
                var doc = Read();
                var ticket = RequireTicket(doc, token);
                await _client().CheckMemberAsync(ticket.UserId, ticket.GuildId, ticket.ChannelId, ct);
                if (!Enabled) throw new InvalidOperationException("Discord account requests are disabled.");
                if (ticket.ExpiresAt <= _now().ToUnixTimeMilliseconds()) throw new InvalidOperationException("This account link expired. Request a new link.");
                var links = _auth.LoginLinks!;
                if (ticket.State is "pending" or "sent")
                {
                    var existing = links.List().SingleOrDefault(account => account.DiscordUserId == ticket.UserId);
                    if (existing == null)
                    {
                        if (_auth.ListAccountNames().Contains(ticket.Username, StringComparer.OrdinalIgnoreCase)
                            || links.List().Any(account => account.Login.Equals(ticket.Username, StringComparison.OrdinalIgnoreCase)
                                || account.DisplayName.Equals(ticket.Username, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("That site username already belongs to an account. Ask Ernie in Discord.");
                        ticket.AccountId = Guid.NewGuid().ToString("N");
                        ticket.Login = ticket.Username;
                        ticket.CreatesAccount = true;
                    }
                    else
                    {
                        if (existing.Revoked || !_environments.CanEnter(EnvironmentId, existing.Login))
                            throw new InvalidOperationException("This account's access is disabled. Ask Ernie in Discord.");
                        ticket.AccountId = existing.Id;
                        ticket.Login = existing.Login;
                    }
                    ticket.State = "activating";
                    Write(doc); // Resume only this account after an interrupted activation.
                }
                if (ticket.CreatesAccount)
                {
                    _ = links.CreateDiscordAccount(ticket.AccountId, ticket.UserId, ticket.Login);
                    if (ticket.State == "activating")
                    {
                        // A retry must never restore membership an administrator has since removed.
                        ticket.State = "granting";
                        Write(doc);
                        _environments.AddDiscordMember(ticket.Login);
                    }
                }
                var account = links.List().SingleOrDefault(account => account.Id == ticket.AccountId);
                if (account == null || account.DiscordUserId != ticket.UserId || account.Revoked
                    || account.Login != ticket.Login || !_environments.CanEnter(EnvironmentId, account.Login))
                    throw new InvalidOperationException("This account's access changed. Ask Ernie in Discord.");
                var cookie = _auth.DiscordSession(account.Id, ticket.UserId);
                ticket.State = "used";
                Write(doc); // No response receives a session until the one-use token is consumed durably.
                var environment = _environments.Get(EnvironmentId);
                var origin = new Uri(UiPublicShares.BaseUrl(_settings)).GetLeftPart(UriPartial.Authority);
                var destination = origin + "/" + environment.Slug + "/";
                if (ticket.ShareToken.Length > 0) destination += "?shared=" + ticket.ShareToken;
                return new(cookie, account.Login, destination);
            }
            finally { _gate.Release(); }
        }

        private Ticket RequireTicket(Document doc, string token)
        {
            if (!Regex.IsMatch(token, @"\A[a-f0-9]{32}\.[A-Za-z0-9_-]{43}\z"))
                throw new InvalidOperationException("This account link is invalid, expired, or already used.");
            var ticket = doc.Tickets.SingleOrDefault(ticket => ticket.Id == token[..32]);
            if (ticket == null || ticket.ExpiresAt <= _now().ToUnixTimeMilliseconds()
                || ticket.State is not ("pending" or "sent" or "activating" or "granting")
                || ticket.ReviewRequired && (ticket.ApprovedAt == 0 || ticket.TestedAt == 0)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ticket.TokenHash), Encoding.UTF8.GetBytes(Hash(token[33..]))))
                throw new InvalidOperationException("This account link is invalid, expired, or already used.");
            return ticket;
        }

        private Document Read()
        {
            if (new FileInfo(_path).Length > 2_097_152) throw new InvalidDataException("The account-request store is too large.");
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Json)
                ?? throw new InvalidDataException("The account-request store is invalid.");
            if (doc.Version != 1 || doc.Tickets.Count > 2048 || doc.Attempts.Count > 60
                || doc.Tickets.Select(ticket => ticket.Id).Distinct(StringComparer.Ordinal).Count() != doc.Tickets.Count
                || doc.Tickets.Any(ticket => !Regex.IsMatch(ticket.Id, @"\A[a-f0-9]{32}\z")
                    || !Regex.IsMatch(ticket.TokenHash, @"\A[a-f0-9]{64}\z")
                    || !ulong.TryParse(ticket.UserId, out var userId) || userId == 0
                    || !ulong.TryParse(ticket.GuildId, out var guildId) || guildId == 0
                    || !ulong.TryParse(ticket.ChannelId, out var channelId) || channelId == 0
                    || !ValidReview(ticket)
                    || ticket.ExpiresAt != (ticket.ReviewRequired
                        ? ticket.ApprovedAt > 0 ? ticket.ApprovedAt + (long)Lifetime.TotalMilliseconds : ticket.CreatedAt + (long)ReviewLifetime.TotalMilliseconds
                        : ticket.CreatedAt + (long)Lifetime.TotalMilliseconds)
                    || ticket.State is not ("review" or "test-pending" or "tested" or "test-failed" or "rejected" or "pending" or "sent" or "failed" or "activating" or "granting" or "used")
                    || (ticket.ShareToken.Length > 0 && !UiPublicShareStore.ValidToken(ticket.ShareToken))
                    || (ticket.State is "activating" or "granting" or "used" && (!Regex.IsMatch(ticket.AccountId, @"\A[a-f0-9]{32}\z") || ticket.Login.Length == 0))))
                throw new InvalidDataException("The account-request store contains invalid identities or states.");
            return doc;
        }
        private static bool ValidReview(Ticket ticket)
        {
            var reviewState = ticket.State is "review" or "test-pending" or "tested" or "test-failed" or "rejected";
            if (!ticket.ReviewRequired) return !reviewState && ticket.ReviewerId.Length == 0 && ticket.TestedAt == 0 && ticket.ApprovedAt == 0;
            if (ticket.CreatedAt <= 0 || ticket.TestedAt < 0 || ticket.ApprovedAt < 0) return false;
            if (ticket.State == "review") return ticket.ReviewerId.Length == 0 && ticket.TestedAt == 0 && ticket.ApprovedAt == 0;
            if (ticket.State == "rejected" && ticket.ReviewerId.Length == 0) return ticket.TestedAt == 0 && ticket.ApprovedAt == 0;
            if (!Regex.IsMatch(ticket.ReviewerId, @"\A[1-9][0-9]{0,19}\z") || !ulong.TryParse(ticket.ReviewerId, out _)) return false;
            if (ticket.State is "test-pending" or "test-failed") return ticket.TestedAt == 0 && ticket.ApprovedAt == 0;
            if (reviewState) return ticket.ApprovedAt == 0 && (ticket.State == "rejected" && ticket.TestedAt == 0 || ticket.TestedAt >= ticket.CreatedAt);
            return ticket.TestedAt >= ticket.CreatedAt && ticket.ApprovedAt >= ticket.TestedAt
                && ticket.ApprovedAt < ticket.TestedAt + (long)Lifetime.TotalMilliseconds
                && ticket.ApprovedAt < ticket.CreatedAt + (long)ReviewLifetime.TotalMilliseconds;
        }
        private static void Prune(Document doc, long now)
        {
            doc.Attempts.RemoveAll(attempt => attempt.At <= now - (long)TimeSpan.FromHours(1).TotalMilliseconds);
            doc.Tickets.RemoveAll(ticket => ticket.ExpiresAt <= now - (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }
        private void Write(Document doc)
        {
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, doc, Json);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
        }
        private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
