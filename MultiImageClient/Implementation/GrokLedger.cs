#nullable enable
using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;

namespace MultiImageClient
{
    /// One line of the Grok history ledger. Append-only JSONL — the local,
    /// machine-readable record of every Grok generation we know about:
    /// prompts, request ids, remote file ids, and where the bytes live on
    /// disk. The sync workflow (GrokArchive) reads this to know what's
    /// already downloaded and what still needs fetching.
    public class GrokLedgerEntry
    {
        /// "image" | "video" | "file" (file = synced from the Files API
        /// without a locally-known generation context).
        [JsonProperty("kind")]
        public string Kind { get; set; } = "";

        [JsonProperty("timestampUtc")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [JsonProperty("model")]
        public string? Model { get; set; }

        [JsonProperty("prompt")]
        public string? Prompt { get; set; }

        /// xAI deferred request id (videos only).
        [JsonProperty("requestId")]
        public string? RequestId { get; set; }

        /// xAI Files API file id, when the asset has a durable server copy.
        [JsonProperty("fileId")]
        public string? FileId { get; set; }

        /// The (usually temporary) xAI URL the asset was served from.
        [JsonProperty("remoteUrl")]
        public string? RemoteUrl { get; set; }

        [JsonProperty("localPath")]
        public string? LocalPath { get; set; }

        [JsonProperty("bytes")]
        public long Bytes { get; set; }

        /// "live" (recorded at generation time) | "sync" (pulled down by
        /// GrokArchive) | "log-backfill" (reconstructed from old JSON logs).
        [JsonProperty("source")]
        public string Source { get; set; } = "live";
    }

    /// Append-only JSONL ledger at {ImageDownloadBaseFolder}\grok_ledger.jsonl.
    /// Writes are lock-serialized and best-effort: ledger problems must never
    /// break a generation run.
    public static class GrokLedger
    {
        private static readonly object _writeLock = new object();

        public static string GetPath(Settings settings)
            => Path.Combine(settings.ImageDownloadBaseFolder, "grok_ledger.jsonl");

        public static void Append(Settings? settings, GrokLedgerEntry entry)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ImageDownloadBaseFolder))
            {
                return;
            }
            try
            {
                var line = JsonConvert.SerializeObject(entry, Formatting.None);
                lock (_writeLock)
                {
                    Directory.CreateDirectory(settings.ImageDownloadBaseFolder);
                    File.AppendAllText(GetPath(settings), line + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"GrokLedger: failed to append entry: {ex.Message}");
            }
        }

        /// Reads every parseable line; malformed lines are skipped (the
        /// ledger is advisory, not a database).
        public static List<GrokLedgerEntry> ReadAll(Settings settings)
        {
            var result = new List<GrokLedgerEntry>();
            var path = GetPath(settings);
            if (!File.Exists(path))
            {
                return result;
            }
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonConvert.DeserializeObject<GrokLedgerEntry>(line);
                    if (entry != null) result.Add(entry);
                }
                catch
                {
                    // skip malformed line
                }
            }
            return result;
        }
    }
}
