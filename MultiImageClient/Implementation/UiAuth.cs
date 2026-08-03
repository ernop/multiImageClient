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
    ///   { "enabled": true,
    ///     "secret": "long random string",
    ///     "accounts": [ { "username": "alice", "password": "..." } ] }
    ///
    /// Design: login exchanges a correct username/password for a cookie token
    ///   username + "." + Base64Url(HMACSHA256(secret, username + "\n" + password)).
    /// The token is stateless and lives in the browser indefinitely, but every
    /// request re-derives the expected HMAC from the CURRENT file contents, so
    /// the owner invalidates any browser instantly by removing the account,
    /// changing its password, or rotating the secret. The server never writes
    /// the auth file.
    ///
    /// Fail-closed rules: a configured-but-missing/malformed file, a missing
    /// secret, or an empty account list is a hard startup error — never
    /// silently open. Blank UiAuthFilePath means auth is off (local use).
    public sealed class UiAuth
    {
        public const string CookieName = "mic_auth";

        private readonly string _filePath;
        private readonly object _reloadLock = new();
        private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
        private DateTime _lastStatUtc = DateTime.MinValue;
        private AuthFile _current;

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
            if (!parsed.Enabled)
            {
                Logger.Log($"UI auth: {full} has enabled=false; the UI is running OPEN.");
                return null;
            }
            return new UiAuth(full, parsed, File.GetLastWriteTimeUtc(full));
        }

        private static AuthFile ParseAndValidate(string json, string pathForErrors)
        {
            AuthFile? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AuthFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"UI auth file {pathForErrors} is not valid JSON: {ex.Message}");
            }
            if (parsed == null)
            {
                throw new InvalidOperationException($"UI auth file {pathForErrors} parsed to nothing.");
            }
            if (parsed.Enabled)
            {
                if (string.IsNullOrWhiteSpace(parsed.Secret) || parsed.Secret.Trim().Length < 16)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} needs a \"secret\" of at least 16 characters "
                        + "(generate a long random string once and keep it stable; rotating it logs everyone out).");
                }
                if (parsed.Accounts == null || parsed.Accounts.Count == 0)
                {
                    throw new InvalidOperationException($"UI auth file {pathForErrors} has enabled=true but no accounts.");
                }
                foreach (var account in parsed.Accounts)
                {
                    if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrWhiteSpace(account.Password))
                    {
                        throw new InvalidOperationException(
                            $"UI auth file {pathForErrors} has an account with a blank username or password.");
                    }
                }
                var duplicate = parsed.Accounts
                    .GroupBy(a => a.Username.Trim(), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(g => g.Count() > 1);
                if (duplicate != null)
                {
                    throw new InvalidOperationException(
                        $"UI auth file {pathForErrors} lists username '{duplicate.Key}' more than once.");
                }
            }
            return parsed;
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

        /// enabled=false edited in at runtime turns the gate off (owner's
        /// explicit choice); a broken file keeps it on with zero valid tokens.
        public bool IsEnforced => CurrentFile().Enabled;

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
                a => string.Equals(a.Username.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account == null || !FixedTimeEquals(account.Password, password))
            {
                RecordFailure(clientIp);
                error = "Wrong username or password.";
                return false;
            }
            _failures.TryRemove(clientIp, out _);
            cookieValue = $"{account.Username.Trim()}.{ComputeMac(file.Secret, account.Username.Trim(), account.Password)}";
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
            var split = cookieValue.LastIndexOf('.');
            if (split <= 0 || split == cookieValue.Length - 1)
            {
                return false;
            }
            var user = cookieValue[..split];
            var mac = cookieValue[(split + 1)..];
            var file = CurrentFile();
            var account = file.Accounts.FirstOrDefault(
                a => string.Equals(a.Username.Trim(), user, StringComparison.Ordinal));
            if (account == null)
            {
                return false;
            }
            var expected = ComputeMac(file.Secret, account.Username.Trim(), account.Password);
            if (!FixedTimeEquals(expected, mac))
            {
                return false;
            }
            username = account.Username.Trim();
            return true;
        }

        private static string ComputeMac(string secret, string username, string password)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret.Trim()));
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(username + "\n" + password));
            return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            // FixedTimeEquals requires equal lengths; comparing lengths leaks
            // nothing useful here (password lengths are not secret-grade).
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
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
        }
    }
}
