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
    public sealed class UiHiddenResource
    {
        public int Version { get; init; } = 1;
        public required string Kind { get; init; }
        public required string JobId { get; init; }
        public string Generator { get; init; } = "";
        public int ImageIndex { get; init; } = -1;
        public required string HiddenByLogin { get; init; }
        public long HiddenAtUnixMs { get; init; }
    }

    /// Persistent one-way stream visibility records. Disk is the source of
    /// truth; the bounded in-process index stores only exact resource keys.
    public sealed class UiVisibilityStore
    {
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        private readonly object _lock = new();
        private readonly string _folder;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, UiHiddenResource> _records =
            new(StringComparer.Ordinal);
        private long _revision;

        public UiVisibilityStore(Settings settings)
        {
            _folder = Path.Combine(settings.ImageDownloadBaseFolder, "UiVisibility");
            Directory.CreateDirectory(_folder);
            Load();
        }

        public (string Version, List<UiHiddenResource> Records) Snapshot()
        {
            lock (_lock)
            {
                return (
                    _instanceId + "-" + _revision.ToString(CultureInfo.InvariantCulture),
                    _records.Values
                        .OrderBy(record => record.HiddenAtUnixMs)
                        .ToList());
            }
        }

        public bool IsPromptHidden(string jobId)
        {
            lock (_lock)
            {
                return _records.ContainsKey(PromptKey(jobId));
            }
        }

        public bool IsImageHidden(string jobId, string generator, int imageIndex)
        {
            lock (_lock)
            {
                return _records.ContainsKey(ImageKey(jobId, generator, imageIndex));
            }
        }

        public bool HasHiddenImages(string jobId)
        {
            lock (_lock)
            {
                return _records.Values.Any(record =>
                    string.Equals(record.Kind, "image", StringComparison.Ordinal)
                    && string.Equals(record.JobId, jobId, StringComparison.Ordinal));
            }
        }

        public void Hide(UiHiddenResource record)
        {
            Validate(record);
            var key = RecordKey(record);
            var path = RecordPath(record);

            lock (_lock)
            {
                if (_records.ContainsKey(key))
                {
                    return;
                }
                WriteAtomically(path, record);
                _records.Add(key, record);
                _revision++;
            }
        }

        private void Load()
        {
            foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
            {
                UiHiddenResource record;
                try
                {
                    record = JsonSerializer.Deserialize<UiHiddenResource>(
                        File.ReadAllText(path),
                        JsonOptions)
                        ?? throw new InvalidDataException("JSON contained null.");
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        $"Visibility record is malformed: {path}: {ex.Message}",
                        ex);
                }

                Validate(record);
                if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(RecordPath(record)),
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Visibility record file name does not match its exact identity: {path}");
                }
                if (!_records.TryAdd(RecordKey(record), record))
                {
                    throw new InvalidDataException(
                        $"Duplicate hidden-resource identity in {_folder}: {Describe(record)}");
                }
            }

            if (_records.Count > 0)
            {
                Logger.Log($"UI visibility: loaded {_records.Count} hidden resource(s).");
            }
        }

        private static void Validate(UiHiddenResource record)
        {
            if (record.Version != CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported visibility record version {record.Version}; expected {CurrentVersion}.");
            }
            if (string.IsNullOrWhiteSpace(record.JobId)
                || string.IsNullOrWhiteSpace(record.HiddenByLogin)
                || record.HiddenAtUnixMs <= 0)
            {
                throw new InvalidDataException(
                    "Visibility record is missing required identity or audit data.");
            }
            if (string.Equals(record.Kind, "prompt", StringComparison.Ordinal))
            {
                if (record.Generator.Length != 0 || record.ImageIndex != -1)
                {
                    throw new InvalidDataException(
                        "Hidden prompt record contains image-only identity data.");
                }
                return;
            }
            if (string.Equals(record.Kind, "image", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(record.Generator) || record.ImageIndex < 0)
                {
                    throw new InvalidDataException(
                        "Hidden image record is missing its exact generator/index identity.");
                }
                return;
            }
            throw new InvalidDataException($"Unsupported hidden resource kind '{record.Kind}'.");
        }

        private string RecordPath(UiHiddenResource record)
        {
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(RecordKey(record))))
                .ToLowerInvariant();
            return Path.Combine(_folder, hash + ".json");
        }

        private static string RecordKey(UiHiddenResource record)
        {
            return string.Equals(record.Kind, "prompt", StringComparison.Ordinal)
                ? PromptKey(record.JobId)
                : ImageKey(record.JobId, record.Generator, record.ImageIndex);
        }

        private static string PromptKey(string jobId)
        {
            return $"prompt\n{jobId}";
        }

        private static string ImageKey(string jobId, string generator, int imageIndex)
        {
            return string.Join(
                "\n",
                "image",
                jobId,
                generator,
                imageIndex.ToString(CultureInfo.InvariantCulture));
        }

        private static string Describe(UiHiddenResource record)
        {
            return string.Equals(record.Kind, "prompt", StringComparison.Ordinal)
                ? $"prompt/{record.JobId}"
                : $"image/{record.JobId}/{record.Generator}/{record.ImageIndex}";
        }

        private static void WriteAtomically(string path, UiHiddenResource record)
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
