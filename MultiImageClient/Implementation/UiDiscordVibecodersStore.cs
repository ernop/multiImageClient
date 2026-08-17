#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient
{
    public sealed class UiDiscordVibecodersSend
    {
        public int Version { get; init; } = 1;
        public required string Kind { get; init; }
        public required string JobId { get; init; }
        public required string Generator { get; init; }
        public int ImageIndex { get; init; }
        public required string SentByLogin { get; init; }
        public long SentAtUnixMs { get; init; }
    }

    /// Persistent one-send-per-result records for #vibecoders. Disk is the
    /// source of truth; the in-process index stores only exact identities.
    public sealed class UiDiscordVibecodersStore
    {
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, UiDiscordVibecodersSend> _records =
            new(StringComparer.Ordinal);
        private long _revision;

        public UiDiscordVibecodersStore(Settings settings)
        {
            _folder = Path.Combine(settings.ImageDownloadBaseFolder, "UiDiscordVibecoders");
            Directory.CreateDirectory(_folder);
            Load();
        }

        public (string Version, List<UiDiscordVibecodersSend> Records) Snapshot()
        {
            lock (_lock)
            {
                return (
                    _instanceId + "-" + _revision.ToString(CultureInfo.InvariantCulture),
                    _records.Values
                        .OrderBy(record => record.SentAtUnixMs)
                        .ToList());
            }
        }

        public bool IsSent(string jobId, string generator, int imageIndex)
        {
            lock (_lock)
            {
                return _records.ContainsKey(RecordKey(jobId, generator, imageIndex));
            }
        }

        public bool TryClaim(UiDiscordVibecodersSend record)
        {
            Validate(record);
            var key = RecordKey(record.JobId, record.Generator, record.ImageIndex);
            var path = RecordPath(key);

            lock (_lock)
            {
                if (_records.ContainsKey(key))
                {
                    return false;
                }
                WriteAtomically(path, record);
                _records.Add(key, record);
                _revision++;
                return true;
            }
        }

        public void ReleaseClaim(string jobId, string generator, int imageIndex, string claimedByLogin)
        {
            var key = RecordKey(jobId, generator, imageIndex);
            var path = RecordPath(key);
            lock (_lock)
            {
                if (!_records.TryGetValue(key, out var existing)
                    || !string.Equals(existing.SentByLogin, claimedByLogin, StringComparison.Ordinal))
                {
                    return;
                }
                _records.Remove(key);
                _revision++;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private void Load()
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
            {
                UiDiscordVibecodersSend record;
                try
                {
                    record = JsonSerializer.Deserialize<UiDiscordVibecodersSend>(
                        File.ReadAllText(path),
                        JsonOptions)
                        ?? throw new InvalidDataException("JSON contained null.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        $"Vibecoders send record is malformed: {path}: {ex.Message}",
                        ex);
                }

                Validate(record);
                var key = RecordKey(record.JobId, record.Generator, record.ImageIndex);
                if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(RecordPath(key)),
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Vibecoders send record file name does not match its exact identity: {path}");
                }
                if (!_records.TryAdd(key, record))
                {
                    throw new InvalidDataException(
                        $"Duplicate vibecoders send identity in {_folder}: {key}");
                }
            }

            if (_records.Count > 0)
            {
                Logger.Log($"UI vibecoders: loaded {_records.Count} sent result(s).");
            }
        }

        private static void Validate(UiDiscordVibecodersSend record)
        {
            if (record.Version != CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported vibecoders send version {record.Version}; expected {CurrentVersion}.");
            }
            if (record.Kind is not ("image" or "video")
                || string.IsNullOrWhiteSpace(record.JobId)
                || string.IsNullOrWhiteSpace(record.Generator)
                || record.ImageIndex < 0
                || string.IsNullOrWhiteSpace(record.SentByLogin)
                || record.SentAtUnixMs <= 0)
            {
                throw new InvalidDataException(
                    "Vibecoders send record is missing required identity or audit data.");
            }
        }

        private string RecordPath(string key)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
                .ToLowerInvariant();
            return Path.Combine(_folder, hash + ".json");
        }

        private static string RecordKey(string jobId, string generator, int imageIndex)
        {
            return string.Join(
                "\n",
                jobId,
                generator,
                imageIndex.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteAtomically(string path, UiDiscordVibecodersSend record)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }
}
