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
    public sealed class UiFavoriteRecord
    {
        public int Version { get; init; } = 1;
        public string Kind { get; init; } = "image";
        public required string User { get; init; }
        public required string JobId { get; init; }
        public string Generator { get; init; } = "";
        public int ImageIndex { get; init; } = -1;
        public int GeneratorImageCount { get; init; }
        public required string Prompt { get; init; }
        public required string CreatedBy { get; init; }
        public long JobCreatedAtUnixMs { get; init; }
        public bool HasInputImage { get; init; }
        public string ImageUrl { get; init; } = "";
        public string ThumbUrl { get; init; } = "";
        public string Size { get; init; } = "";
        public long FavoritedAtUnixMs { get; init; }
    }

    /// Persistent shared-site favorites. Disk is the source of truth: each
    /// user/resource edge is one atomically replaced JSON file, while the
    /// in-process dictionary is only the lightweight index used for reads.
    public sealed class UiFavoriteStore
    {
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private long _revision;
        private readonly Dictionary<string, UiFavoriteRecord> _records =
            new(StringComparer.Ordinal);

        public UiFavoriteStore(Settings settings)
        {
            _folder = Path.Combine(settings.ImageDownloadBaseFolder, "UiFavorites");
            Directory.CreateDirectory(_folder);
            Load();
        }

        public (string Version, List<UiFavoriteRecord> Records) Snapshot()
        {
            lock (_lock)
            {
                return (
                    VersionLocked(),
                    _records.Values
                        .OrderByDescending(record => record.FavoritedAtUnixMs)
                        .ToList());
            }
        }

        public List<UiFavoriteRecord> ListImage(string jobId, string generator, int imageIndex)
        {
            return ListResource("image", jobId, generator, imageIndex);
        }

        public List<UiFavoriteRecord> ListPrompt(string jobId)
        {
            return ListResource("prompt", jobId, "", -1);
        }

        private List<UiFavoriteRecord> ListResource(
            string kind,
            string jobId,
            string generator,
            int imageIndex)
        {
            lock (_lock)
            {
                return _records.Values
                    .Where(record =>
                        string.Equals(record.Kind, kind, StringComparison.Ordinal)
                        && string.Equals(record.JobId, jobId, StringComparison.Ordinal)
                        && string.Equals(record.Generator, generator, StringComparison.Ordinal)
                        && record.ImageIndex == imageIndex)
                    .OrderBy(record => record.User, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public void Set(UiFavoriteRecord record, bool favorite)
        {
            Validate(record);
            var key = RecordKey(record);
            var path = RecordPath(record);

            lock (_lock)
            {
                if (favorite)
                {
                    if (_records.ContainsKey(key))
                    {
                        return;
                    }
                    WriteAtomically(path, record);
                    _records.Add(key, record);
                    _revision++;
                    return;
                }

                if (!_records.ContainsKey(key))
                {
                    return;
                }
                if (!File.Exists(path))
                {
                    throw new IOException(
                        $"Favorite index contains {DescribeIdentity(record)}, "
                        + $"but its source file is missing: {path}");
                }
                File.Delete(path);
                _records.Remove(key);
                _revision++;
            }
        }

        private void Load()
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
            {
                UiFavoriteRecord record;
                try
                {
                    record = JsonSerializer.Deserialize<UiFavoriteRecord>(
                        File.ReadAllText(path),
                        JsonOptions)
                        ?? throw new InvalidDataException("JSON contained null.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    throw new InvalidDataException($"Favorite file is malformed: {path}: {ex.Message}", ex);
                }

                Validate(record);
                var expectedPath = RecordPath(record);
                if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(expectedPath),
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Favorite file name does not match its exact identity: {path}");
                }

                var key = RecordKey(record);
                if (!_records.TryAdd(key, record))
                {
                    throw new InvalidDataException(
                        $"Duplicate favorite identity in {_folder}: "
                        + DescribeIdentity(record));
                }
            }

            if (_records.Count > 0)
            {
                Logger.Log($"UI favorites: loaded {_records.Count} persistent favorite(s).");
            }
        }

        private static void Validate(UiFavoriteRecord record)
        {
            if (record.Version != CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported favorite record version {record.Version}; expected {CurrentVersion}.");
            }
            if (string.IsNullOrWhiteSpace(record.User)
                || string.IsNullOrWhiteSpace(record.JobId)
                || string.IsNullOrWhiteSpace(record.Prompt)
                || record.JobCreatedAtUnixMs <= 0
                || record.FavoritedAtUnixMs <= 0)
            {
                throw new InvalidDataException("Favorite record is missing required exact-identity or display data.");
            }
            if (string.Equals(record.Kind, "image", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(record.Generator)
                    || record.ImageIndex < 0
                    || record.GeneratorImageCount <= record.ImageIndex
                    || string.IsNullOrWhiteSpace(record.ImageUrl)
                    || string.IsNullOrWhiteSpace(record.ThumbUrl))
                {
                    throw new InvalidDataException("Image favorite is missing required exact-identity or display data.");
                }
                return;
            }
            if (string.Equals(record.Kind, "prompt", StringComparison.Ordinal))
            {
                if (record.Generator.Length != 0
                    || record.ImageIndex != -1
                    || record.GeneratorImageCount != 0
                    || record.ImageUrl.Length != 0
                    || record.ThumbUrl.Length != 0
                    || record.Size.Length != 0)
                {
                    throw new InvalidDataException("Prompt favorite contains image-only identity or display data.");
                }
                return;
            }
            throw new InvalidDataException($"Unsupported favorite kind '{record.Kind}'.");
        }

        private string RecordPath(UiFavoriteRecord record)
        {
            var identity = RecordKey(record);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
            return Path.Combine(_folder, hash + ".json");
        }

        private static string RecordKey(UiFavoriteRecord record)
        {
            // Keep the original image key shape so already-persisted image
            // favorites retain the same file names after prompt support.
            if (string.Equals(record.Kind, "image", StringComparison.Ordinal))
            {
                return string.Join(
                    "\n",
                    record.User,
                    record.JobId,
                    record.Generator,
                    record.ImageIndex.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(
                "\n",
                record.User,
                "prompt",
                record.JobId);
        }

        private static string DescribeIdentity(UiFavoriteRecord record)
        {
            return string.Equals(record.Kind, "image", StringComparison.Ordinal)
                ? $"{record.User}/image/{record.JobId}/{record.Generator}/{record.ImageIndex}"
                : $"{record.User}/prompt/{record.JobId}";
        }

        private string VersionLocked()
        {
            return _instanceId + "-" + _revision.ToString(CultureInfo.InvariantCulture);
        }

        private static void WriteAtomically(string path, UiFavoriteRecord record)
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
