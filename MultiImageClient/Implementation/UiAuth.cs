#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient
{
    /// Optional username/password gate for shared --ui deployments.
    ///
    /// Configured by Settings.UiAuthFilePath pointing at a JSON file:
    ///   { "version": 2,
    ///     "enabled": true,
    ///     "secret": "long random string",
    ///     "accounts": [ { "username": "alice",
    ///                     "passwordHash": "pbkdf2-sha256$600000$salt$hash" } ] }
    ///
    /// Design: login verifies the submitted password against the stored
    /// PBKDF2 hash, then issues a cookie token
    ///   username + "." + Base64Url(HMACSHA256(secret, username + "\n" + passwordHash)).
    /// The token is stateless and lives in the browser indefinitely, but every
    /// request re-derives the expected HMAC from the CURRENT file contents, so
    /// the owner invalidates any browser instantly by removing the account,
    /// replacing its hash, or rotating the secret. The server never writes
    /// the auth file and never stores a plaintext password.
    ///
    /// Fail-closed rules: a configured-but-missing/malformed file, a version-1
    /// plaintext file, enabled=false, a missing secret, or an empty account
    /// list is a hard startup error — never silently open. Blank
    /// UiAuthFilePath means auth is off (local use).
    public sealed class UiAuth
    {
        public const string CookieName = "mic_auth";
        public const int AuthFileVersion = 2;
        public const int MinSecretChars = 32;
        public const int Pbkdf2Iterations = 600_000;
        public const int Pbkdf2SaltBytes = 16;
        public const int Pbkdf2HashBytes = 32;
        public const string PasswordHashPrefix = "pbkdf2-sha256";

        private static readonly string DummyPasswordHash = BuildDummyPasswordHash();

        private readonly string _filePath;
        private readonly object _reloadLock = new();
        private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
        private DateTime _lastStatUtc = DateTime.MinValue;
        private AuthFile _current;
        public UiLoginLinks? LoginLinks { get; private set; }
        public string SessionCookieName { get; private set; } = CookieName;
        public string SessionCookiePath { get; private set; } = "/";

        // Per-IP failed-login throttle: after MaxFailures failures inside the
        // window, further attempts from that IP get rejected until it expires.
        private const int MaxFailuresPerWindow = 10;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStartUtc)> _failures = new();

        private UiAuth(string filePath, AuthFile initial, DateTime writeTimeUtc)
        {
            _filePath = filePath;
            _current = initial;
            _loadedWriteTimeUtc = writeTimeUtc;
        }

        /// Null when UiAuthFilePath is blank (auth disabled, local mode).
        /// Throws on any configuration problem — a shared deployment must
        /// never start half-protected.
        public static UiAuth? CreateFromSettings(Settings settings)
        {
            var path = settings.UiAuthFilePath?.Trim() ?? "";
            if (path.Length == 0)
            {
                return null;
            }
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new InvalidOperationException(
                    $"UiAuthFilePath is set but the file does not exist: {full}");
            }
            var parsed = ParseAndValidate(File.ReadAllText(full), full);
            var auth = new UiAuth(full, parsed, File.GetLastWriteTimeUtc(full));
            if (settings.UiEnvironmentId.Length > 0 && settings.UiEnvironmentRegistryPath.Length == 0)
            {
                auth.SessionCookieName = CookieName + "_" + settings.UiEnvironmentId;
                auth.SessionCookiePath = new Uri(settings.UiPublicBaseUrl).AbsolutePath.TrimEnd('/') + "/";
            }
            if (!string.IsNullOrWhiteSpace(settings.UiLoginLinksFilePath))
                auth.LoginLinks = new UiLoginLinks(settings.UiLoginLinksFilePath);
            return auth;
        }

        private static AuthFile ParseAndValidate(string json, string pathForErrors)
        {
            byte[] utf8;
            try
            {
                utf8 = Encoding.UTF8.GetBytes(json);
                RejectDuplicatePropertyNames(utf8);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"UI auth file {pathForErrors} is not valid JSON: {ex.Message}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(utf8);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"UI auth file {pathForErrors} is not valid JSON: {ex.Message}");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} root must be an object.");
                }

                var root = document.RootElement;
                RequireExactObjectKeys(root, pathForErrors, "root", "version", "enabled", "secret", "accounts");

                if (!TryGetInt32(root, "version", out var version) || version != AuthFileVersion)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} must be version {AuthFileVersion} with PBKDF2 password hashes; "
                        + "plaintext version-1 files are rejected. Run deploy/migrate-ui-auth-v1-to-v2.py.");
                }

                if (root.GetProperty("enabled").ValueKind != JsonValueKind.True)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} must have enabled=true. "
                        + "Blank UiAuthFilePath is the only way to run the UI open; enabled=false is rejected.");
                }

                var secret = RequireNonEmptyString(root, "secret", pathForErrors);
                if (secret.Length < MinSecretChars)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} needs a \"secret\" of at least {MinSecretChars} characters "
                        + "(generate a long random string once and keep it stable; rotating it logs everyone out).");
                }

                var accountsElement = root.GetProperty("accounts");
                if (accountsElement.ValueKind != JsonValueKind.Array || accountsElement.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} has enabled=true but no accounts.");
                }

                var accounts = new List<AuthAccount>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var accountElement in accountsElement.EnumerateArray())
                {
                    if (accountElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"UI auth file {pathForErrors} account {index} must be an object.");
                    }
                    RequireExactObjectKeys(
                        accountElement,
                        pathForErrors,
                        $"account {index}",
                        "username",
                        "passwordHash");
                    var username = RequireNonEmptyString(accountElement, "username", pathForErrors);
                    if (username.Length > 128 || username.Any(ch => char.IsControl(ch)))
                    {
                        throw new InvalidOperationException(
                            $"UI auth file {pathForErrors} account {index} has an invalid username.");
                    }
                    if (!seen.Add(username))
                    {
                        throw new InvalidOperationException(
                            $"UI auth file {pathForErrors} lists username '{username}' more than once.");
                    }
                    var passwordHash = RequireNonEmptyString(accountElement, "passwordHash", pathForErrors);
                    if (!TryParsePasswordHash(passwordHash, out var salt, out var digest, out var hashError))
                    {
                        throw new InvalidOperationException(
                            $"UI auth file {pathForErrors} account '{username}' passwordHash is invalid: {hashError}");
                    }
                    accounts.Add(new AuthAccount
                    {
                        Username = username,
                        PasswordHash = passwordHash,
                        Salt = salt,
                        Digest = digest,
                    });
                    index++;
                }

                return new AuthFile
                {
                    Enabled = true,
                    Secret = secret,
                    Accounts = accounts,
                };
            }
        }

        // Re-read the auth file when its timestamp changes so account edits
        // (revocation!) apply without a restart. Stat at most once per second.
        // A file that becomes broken AFTER startup fails closed: the stale
        // in-memory copy is discarded and every request is rejected until the
        // file parses again.
        private AuthFile CurrentFile()
        {
            lock (_reloadLock)
            {
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - _lastStatUtc < TimeSpan.FromSeconds(1))
                {
                    return _current;
                }
                _lastStatUtc = nowUtc;
                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(_filePath);
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI auth: cannot stat {_filePath} ({ex.Message}); failing closed.");
                    _current = AuthFile.Locked;
                    return _current;
                }
                if (writeTime == _loadedWriteTimeUtc)
                {
                    return _current;
                }
                try
                {
                    var reloaded = ParseAndValidate(File.ReadAllText(_filePath), _filePath);
                    _current = reloaded;
                    _loadedWriteTimeUtc = writeTime;
                    Logger.Log($"UI auth: reloaded {_filePath} ({reloaded.Accounts.Count} account(s), enabled={reloaded.Enabled}).");
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI auth: {_filePath} changed but is now invalid ({ex.Message}); failing closed until it parses.");
                    _current = AuthFile.Locked;
                    _loadedWriteTimeUtc = writeTime;
                }
                return _current;
            }
        }

        /// A constructed UiAuth means the gate is on. Broken or rejected files
        /// stay enforced with zero valid tokens. Blank UiAuthFilePath is the
        /// only open mode.
        public bool IsEnforced => true;

        public IReadOnlyList<string> ListAccountNames()
        {
            return CurrentFile().Accounts
                .Select(account => account.Username)
                .ToList();
        }

        public bool TryLogin(string username, string password, string clientIp, out string cookieValue, out string error)
        {
            cookieValue = "";
            if (IsThrottled(clientIp))
            {
                error = "Too many failed attempts; wait a few minutes.";
                return false;
            }
            var file = CurrentFile();
            var account = file.Accounts.FirstOrDefault(
                a => string.Equals(a.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
            var passwordMatches = account != null
                ? VerifyPassword(password, account.Salt, account.Digest)
                : VerifyPassword(password, DummyPasswordHash);
            if (account == null && LoginLinks != null && LoginLinks.TryPassword(username, password, file.Secret, out cookieValue))
            { _failures.TryRemove(clientIp, out _); error = ""; return true; }
            if (account == null || !passwordMatches)
            {
                RecordFailure(clientIp);
                error = "Wrong username or password.";
                return false;
            }
            _failures.TryRemove(clientIp, out _);
            cookieValue = $"{account.Username}.{ComputeMac(file.Secret, account.Username, account.PasswordHash)}";
            error = "";
            return true;
        }

        /// Validates a cookie against the CURRENT file. Returns the username
        /// when valid.
        public bool TryValidateCookie(string? cookieValue, out string username)
        {
            username = "";
            if (string.IsNullOrEmpty(cookieValue))
            {
                return false;
            }
            if (cookieValue.StartsWith("link:", StringComparison.Ordinal))
            {
                var current = CurrentFile();
                if (current.Accounts.Count == 0 || LoginLinks == null) return false;
                if (!LoginLinks.TryValidateCookie(cookieValue, current.Secret, out username)) return false;
                if (username == UiLoginLinks.OwnerLogin && !current.Accounts.Any(a => a.Username == UiLoginLinks.OwnerLogin))
                { username = ""; return false; }
                return true;
            }
            var split = cookieValue.LastIndexOf('.');
            if (split <= 0 || split == cookieValue.Length - 1)
            {
                return false;
            }
            var user = cookieValue[..split];
            var mac = cookieValue[(split + 1)..];
            var file = CurrentFile();
            var account = file.Accounts.FirstOrDefault(
                a => string.Equals(a.Username, user, StringComparison.Ordinal));
            if (account == null)
            {
                return false;
            }
            var expected = ComputeMac(file.Secret, account.Username, account.PasswordHash);
            if (!FixedTimeEquals(expected, mac))
            {
                return false;
            }
            username = account.Username;
            return true;
        }

        public bool TryLoginLink(string token, string clientIp, out string cookie, out UiLoginLinks.Account? account, out string error)
        {
            cookie = "";
            account = null;
            error = "This login link is invalid or revoked.";
            if (IsThrottled(clientIp)) { error = "Too many failed attempts; wait a few minutes."; return false; }
            var file = CurrentFile();
            if (file.Accounts.Count == 0 || LoginLinks == null
                || !LoginLinks.TryExchange(token, file.Secret, out cookie, out account)
                || (account!.Login == UiLoginLinks.OwnerLogin && !file.Accounts.Any(a => a.Username == UiLoginLinks.OwnerLogin)))
            {
                cookie = ""; account = null;
                RecordFailure(clientIp);
                return false;
            }
            _failures.TryRemove(clientIp, out _);
            error = "";
            return true;
        }

        private static string ComputeMac(string secret, string username, string passwordHash)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(username + "\n" + passwordHash));
            return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static string NewPasswordHash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(Pbkdf2SaltBytes);
            var digest = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, Pbkdf2HashBytes);
            return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
        }

        internal static bool VerifyPassword(string password, string passwordHash)
        {
            if (!TryParsePasswordHash(passwordHash, out var salt, out var digest, out _))
            {
                return false;
            }
            return VerifyPassword(password, salt, digest);
        }

        private static bool VerifyPassword(string password, byte[] salt, byte[] digest)
        {
            var candidate = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                Pbkdf2HashBytes);
            return CryptographicOperations.FixedTimeEquals(candidate, digest);
        }

        private static bool TryParsePasswordHash(
            string passwordHash,
            out byte[] salt,
            out byte[] digest,
            out string error)
        {
            salt = Array.Empty<byte>();
            digest = Array.Empty<byte>();
            error = "";
            var parts = passwordHash.Split('$');
            if (parts.Length != 4
                || parts[0] != PasswordHashPrefix
                || parts[1] != Pbkdf2Iterations.ToString()
                || parts[2].Length == 0
                || parts[3].Length == 0)
            {
                error = $"must be {PasswordHashPrefix}${Pbkdf2Iterations}$<salt>$<hash> with exact iteration count.";
                return false;
            }
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                digest = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                error = "salt and hash must be Base64.";
                return false;
            }
            if (salt.Length != Pbkdf2SaltBytes || digest.Length != Pbkdf2HashBytes)
            {
                error = $"salt must be {Pbkdf2SaltBytes} bytes and hash {Pbkdf2HashBytes} bytes.";
                return false;
            }
            return true;
        }

        private static void RejectDuplicatePropertyNames(byte[] utf8)
        {
            var reader = new Utf8JsonReader(utf8);
            var stack = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        stack.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString() ?? "";
                        if (!stack.Peek().Add(name))
                        {
                            throw new JsonException($"duplicate field '{name}'");
                        }
                        break;
                }
            }
        }

        private static void RequireExactObjectKeys(
            JsonElement element,
            string pathForErrors,
            string context,
            params string[] expected)
        {
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                actual.Add(property.Name);
            }
            if (!actual.SetEquals(expected))
            {
                throw new InvalidOperationException(
                    $"UI auth file {pathForErrors} {context} fields must be exactly [{string.Join(", ", expected)}]; "
                    + $"found [{string.Join(", ", actual.OrderBy(name => name, StringComparer.Ordinal))}].");
            }
        }

        private static string RequireNonEmptyString(JsonElement parent, string name, string pathForErrors)
        {
            var element = parent.GetProperty(name);
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"UI auth file {pathForErrors} \"{name}\" must be a string.");
            }
            var value = element.GetString()?.Trim() ?? "";
            if (value.Length == 0)
            {
                throw new InvalidOperationException(
                    $"UI auth file {pathForErrors} \"{name}\" is blank.");
            }
            return value;
        }

        private static bool TryGetInt32(JsonElement parent, string name, out int value)
        {
            value = 0;
            var element = parent.GetProperty(name);
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
            {
                return false;
            }
            return true;
        }

        private static string BuildDummyPasswordHash()
        {
            var salt = new byte[Pbkdf2SaltBytes];
            var digest = new byte[Pbkdf2HashBytes];
            return $"{PasswordHashPrefix}${Pbkdf2Iterations}$"
                + Convert.ToBase64String(salt) + "$"
                + Convert.ToBase64String(digest);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            // FixedTimeEquals requires equal lengths; comparing lengths leaks
            // nothing useful here (MAC encodings are not secret-grade).
            return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
        }

        private bool IsThrottled(string clientIp)
        {
            if (!_failures.TryGetValue(clientIp, out var entry))
            {
                return false;
            }
            if (DateTime.UtcNow - entry.WindowStartUtc > FailureWindow)
            {
                _failures.TryRemove(clientIp, out _);
                return false;
            }
            return entry.Count >= MaxFailuresPerWindow;
        }

        private void RecordFailure(string clientIp)
        {
            _failures.AddOrUpdate(
                clientIp,
                _ => (1, DateTime.UtcNow),
                (_, entry) => DateTime.UtcNow - entry.WindowStartUtc > FailureWindow
                    ? (1, DateTime.UtcNow)
                    : (entry.Count + 1, entry.WindowStartUtc));
        }

        private sealed class AuthFile
        {
            public bool Enabled { get; set; }
            public string Secret { get; set; } = "";
            public List<AuthAccount> Accounts { get; set; } = new();

            /// Fail-closed placeholder used when the on-disk file is broken:
            /// enforced, but no account can ever match.
            public static AuthFile Locked { get; } = new()
            {
                Enabled = true,
                Secret = "broken-auth-file-no-tokens-can-match",
                Accounts = new List<AuthAccount>(),
            };
        }

        private sealed class AuthAccount
        {
            public string Username { get; init; } = "";
            public string PasswordHash { get; init; } = "";
            public byte[] Salt { get; init; } = Array.Empty<byte>();
            public byte[] Digest { get; init; } = Array.Empty<byte>();
        }
    }
}
