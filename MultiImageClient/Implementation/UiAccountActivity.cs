#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MultiImageClient
{
    public sealed class UiAccountActivity
    {
        public sealed class Entry
        {
            public DateTimeOffset? FirstLogin { get; set; }
            public DateTimeOffset? LastLogin { get; set; }
            public DateTimeOffset? LastActive { get; set; }
        }
        private readonly string _path;
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries;
        public UiAccountActivity(string root)
        {
            Directory.CreateDirectory(root); _path = Path.Combine(root, "account-activity.json");
            if (File.Exists(_path) && new FileInfo(_path).Length > 1048576) throw new InvalidDataException("Account activity exceeds its limit.");
            _entries = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Invalid account activity.") : new();
            if (_entries.Count > 501) throw new InvalidDataException("Too many activity accounts.");
        }
        public void Record(string login, bool loggedIn = false)
        {
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                if (!_entries.TryGetValue(login, out var entry))
                {
                    if (_entries.Count >= 501) throw new InvalidDataException("Too many activity accounts.");
                    _entries[login] = entry = new();
                }
                if (!loggedIn && entry.LastActive > now.AddMinutes(-1)) return;
                entry.LastActive = now;
                if (loggedIn) { entry.FirstLogin ??= now; entry.LastLogin = now; }
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries)); File.Move(temp, _path, true);
            }
        }
        public Dictionary<string, Entry> Snapshot()
        {
            lock (_sync) return new Dictionary<string, Entry>(_entries);
        }

        public void TransferLogin(string from, string to)
        {
            from = from.Trim();
            to = to.Trim();
            if (from.Length == 0 || to.Length == 0)
                throw new InvalidDataException("A login transfer requires the exact previous login and the exact new login.");
            if (string.Equals(from, to, StringComparison.Ordinal))
                return;
            lock (_sync)
            {
                if (!_entries.TryGetValue(from, out var entry))
                    return;
                if (_entries.ContainsKey(to))
                    throw new InvalidDataException("Account activity already has an entry for the new login.");
                _entries.Remove(from);
                _entries[to] = entry;
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries)); File.Move(temp, _path, true);
            }
        }
    }
}
