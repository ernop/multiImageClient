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

namespace MultiImageClient
{
    // One process owns this bounded, atomic file. Credentials never appear in list responses.
    public sealed class UiLoginLinks
    {
        public const string OwnerLogin = "ernieMultiZone";
        private const int MaxAccounts = 500;
        private readonly string _path;
        private readonly object _sync = new();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };

        public sealed class Account
        {
            public required string Id { get; init; }
            public required string Login { get; init; }
            public required string DisplayName { get; init; }
            public required string TokenHash { get; set; }
            public bool Revoked { get; set; }
            public string? PasswordHash { get; init; }
        }

        public sealed class Document
        {
            public required int Version { get; init; }
            public required List<Account> Accounts { get; init; }
            public List<string>? DefaultGenerators { get; set; }
        }

        public UiLoginLinks(string path)
        {
            _path = Path.GetFullPath(path);
            Read(); // Missing or malformed configured state is a startup error.
        }

        public IReadOnlyList<Account> List() { lock (_sync) return Read().Accounts; }
        public IReadOnlyList<string>? Defaults() { lock (_sync) return Read().DefaultGenerators; }

        public static string NormalizeName(string name)
        {
            name = Regex.Replace(name.Trim(), @"\s+", " ");
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9 ._-]{1,32}\z"))
                throw new InvalidDataException("Use 1–32 letters, numbers, spaces, dots, underscores, or hyphens for the name.");
            return name;
        }

        public (Account Account, string Token) Create(string displayName, Action<Account> prepareProfile, string? passwordHash = null)
        {
            displayName = NormalizeName(displayName);
            lock (_sync)
            {
                var doc = Read();
                if (doc.Accounts.Count >= MaxAccounts)
                    throw new InvalidDataException("This environment has reached its 500-account limit.");
                if (doc.Accounts.Any(a => a.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("That name already has an account in this environment.");
                var id = Guid.NewGuid().ToString("N");
                var secret = NewSecret();
                var account = new Account { Id = id, Login = "member-" + id, DisplayName = displayName, TokenHash = Hash(secret), PasswordHash = passwordHash };
                // A profile failure must never issue a usable token. A later file failure leaves only a reserved name.
                prepareProfile(account);
                doc.Accounts.Add(account);
                Write(doc);
                return (account, id + "." + secret);
            }
        }

        public string Replace(string id)
        {
            lock (_sync)
            {
                var doc = Read();
                var account = RequireAccount(doc, id);
                var secret = NewSecret();
                account.TokenHash = Hash(secret);
                account.Revoked = false;
                Write(doc);
                return id + "." + secret;
            }
        }

        public void Revoke(string id)
        {
            lock (_sync)
            {
                var doc = Read();
                RequireAccount(doc, id).Revoked = true;
                Write(doc);
            }
        }

        public void SetDefaults(List<string> keys)
        {
            if (keys.Count > 64 || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                throw new InvalidDataException("Provider defaults must contain distinct provider keys.");
            lock (_sync) { var doc = Read(); doc.DefaultGenerators = keys; Write(doc); }
        }

        public bool TryPassword(string login, string password, string signingSecret, out string cookie)
        {
            cookie = "";
            lock (_sync)
            {
                var account = Read().Accounts.SingleOrDefault(a => a.Login == login && !a.Revoked);
                if (account?.PasswordHash == null || !UiAuth.VerifyPassword(password, account.PasswordHash)) return false;
                cookie = "link:" + account.Id + "." + Mac(signingSecret, account); return true;
            }
        }

        public bool TryExchange(string token, string signingSecret, out string cookie, out Account? account)
        {
            cookie = "";
            account = null;
            if (token.Length != 76 || token[32] != '.') return false;
            var id = token[..32];
            var secret = token[33..];
            if (!Regex.IsMatch(secret, @"\A[A-Za-z0-9_-]{43}\z")) return false;
            lock (_sync)
            {
                account = Read().Accounts.SingleOrDefault(a => a.Id == id && !a.Revoked);
                if (account == null || !Equal(account.TokenHash, Hash(secret))) { account = null; return false; }
                cookie = "link:" + id + "." + Mac(signingSecret, account);
                return true;
            }
        }

        public bool TryValidateCookie(string cookie, string signingSecret, out string login)
        {
            login = "";
            if (cookie.Length != 81 || !cookie.StartsWith("link:", StringComparison.Ordinal) || cookie[37] != '.') return false;
            var id = cookie.Substring(5, 32);
            lock (_sync)
            {
                var account = Read().Accounts.SingleOrDefault(a => a.Id == id && !a.Revoked);
                if (account == null || !Equal(Mac(signingSecret, account), cookie[38..])) return false;
                login = account.Login;
                return true;
            }
        }

        private Document Read()
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length > 1_048_576)
                throw new InvalidDataException("The login-link file is missing or exceeds 1 MiB.");
            Document doc;
            try { doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidDataException("The login-link file is empty."); }
            catch (JsonException) { throw new InvalidDataException("The login-link file is malformed."); }
            if (doc.Version != 1 || doc.Accounts == null || doc.Accounts.Count > MaxAccounts)
                throw new InvalidDataException("The login-link file has an unsupported version or account count.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in doc.Accounts)
            {
                if (a == null || a.Id == null || !Regex.IsMatch(a.Id, @"\A[a-f0-9]{32}\z")
                    || !ids.Add(a.Id) || a.Login == null || !logins.Add(a.Login)
                    || (a.Login != "member-" + a.Id && a.Login != OwnerLogin)
                    || a.DisplayName == null || NormalizeName(a.DisplayName) != a.DisplayName
                    || a.TokenHash == null || !Regex.IsMatch(a.TokenHash, @"\A[a-f0-9]{64}\z"))
                    throw new InvalidDataException("The login-link file contains an invalid or duplicate account.");
            }
            if (doc.DefaultGenerators != null && (doc.DefaultGenerators.Count > 64
                || doc.DefaultGenerators.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 80)
                || doc.DefaultGenerators.Distinct(StringComparer.Ordinal).Count() != doc.DefaultGenerators.Count))
                throw new InvalidDataException("The login-link file contains invalid provider defaults.");
            return doc;
        }

        private void Write(Document doc)
        {
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                    JsonSerializer.Serialize(stream, doc, JsonOptions);
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static Account RequireAccount(Document doc, string id) => doc.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new InvalidDataException("The account does not exist in this environment.");
        private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));
        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        private static string Mac(string secret, Account account) => Base64Url(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("login-link\n" + account.Id + "\n" + account.Login + "\n" + account.TokenHash)));
        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
