#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

namespace MultiImageClient
{
    public sealed record UiPromptArchiveFilter(
        string? Query = null,
        string? Source = null,
        int? Year = null,
        string? Kind = null,
        int? MinChars = null,
        int? MaxChars = null);

    public sealed record UiPromptArchiveSourceSummary(
        string Id,
        string Label,
        string Category,
        string OriginHint,
        int UniquePrompts,
        int TotalOccurrences,
        string? EarliestUtc,
        string? LatestUtc);

    public sealed record UiPromptArchiveYearCount(int Year, int Count);

    public sealed record UiPromptArchiveKindCount(string Kind, int Count);

    public sealed record UiPromptArchiveStats(
        bool Exists,
        int TotalPrompts,
        int TotalOccurrences,
        string? EarliestUtc,
        string? LatestUtc,
        string CurrentInstance,
        IReadOnlyList<UiPromptArchiveSourceSummary> Sources,
        IReadOnlyList<UiPromptArchiveYearCount> Years,
        IReadOnlyList<UiPromptArchiveKindCount> Kinds);

    public sealed record UiPromptArchiveItem(
        long Id,
        string PromptText,
        string Kind,
        int CharLen,
        int WordCount,
        string? FirstSeenUtc,
        string? LastSeenUtc,
        int FirstYear,
        int LastYear,
        int OccurrenceCount,
        string PrimarySource,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Models,
        string NegativePrompt,
        Dictionary<string, JsonElement> ExtraMeta,
        string OriginInstance,
        string OriginJobId,
        bool CanOpenJob);

    public sealed record UiPromptArchiveOccurrence(
        long Id,
        string SourceId,
        string SourceKey,
        string? OccurredAtUtc,
        string Model,
        string OriginInstance,
        string OriginJobId,
        bool CanOpenJob,
        Dictionary<string, JsonElement> Details);

    public sealed record UiPromptArchiveItemDetail(
        UiPromptArchiveItem Item,
        IReadOnlyList<UiPromptArchiveOccurrence> Occurrences,
        int TotalOccurrences);

    public sealed record UiPromptArchiveRandomResult(
        bool Exists,
        int MatchingCount,
        IReadOnlyList<UiPromptArchiveItem> Items,
        bool ExhaustedSeen = false);

    public sealed record UiPromptArchiveSearchResult(
        bool Exists,
        int TotalMatching,
        int Page,
        int PageSize,
        int TotalPages,
        IReadOnlyList<UiPromptArchiveItem> Items);

    public sealed record UiPromptArchiveSyncResult(
        int AddedPrompts,
        int AddedOccurrences,
        int TotalPrompts,
        int TotalOccurrences);

    /// Disk-backed SQLite reader/writer for the owner's private lifetime prompt archive.
    /// Keeps zero prompt list in process memory; every query hits SQLite directly with
    /// indexed filters and FTS5 search.
    public sealed class UiPromptArchive
    {
        public const string FileName = "prompt-archive.sqlite3";
        private const string SchemaVersion = "1";
        private const int MaxPageSize = 100;
        private const int MaxRandomCount = 50;
        private const int MaxDetailOccurrences = 200;

        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ImaginePrefixRegex = new(@"^\s*/imagine\s+prompt:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UrlOnlyRegex = new(@"^https?://\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FtsTokenRegex = new(@"""([^""]+)""|([\p{L}\p{N}_-]+)", RegexOptions.Compiled);

        private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
        {
            "image", "video", "goal", "describe", "all",
        };

        private static readonly HashSet<string> AllowedSorts = new(StringComparer.Ordinal)
        {
            "newest", "oldest", "longest", "most_used", "relevance",
        };

        private readonly string _dbPath;
        private readonly string _currentInstance;
        private readonly object _writeGate = new();

        public UiPromptArchive(string dbPath, string currentInstance)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("Prompt archive path is required.", nameof(dbPath));
            _dbPath = dbPath;
            _currentInstance = string.IsNullOrWhiteSpace(currentInstance) ? "local" : currentInstance.Trim();
        }

        public string DbPath => _dbPath;
        public string CurrentInstance => _currentInstance;
        public bool Exists => File.Exists(_dbPath);

        public static string ResolveDbPath(Settings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.UiPromptArchiveDbPath))
                return settings.UiPromptArchiveDbPath.Trim();
            return Path.Combine(settings.ImageDownloadBaseFolder, FileName);
        }

        public static string ResolveInstanceId(Settings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.UiEnvironmentId))
                return "local";
            if (settings.UiEnvironmentController || string.Equals(settings.UiEnvironmentId, "original", StringComparison.Ordinal))
                return "ave";
            if (string.Equals(settings.UiEnvironmentId, "vibecoders-ai-generation", StringComparison.Ordinal))
                return "vibecoders";
            return settings.UiEnvironmentId.Trim();
        }

        public static string CleanPrompt(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            normalized = ImaginePrefixRegex.Replace(normalized, "").Trim();
            return normalized;
        }

        public static bool IsSkippablePrompt(string cleaned)
        {
            if (string.IsNullOrEmpty(cleaned) || cleaned.Length < 2) return true;
            if (UrlOnlyRegex.IsMatch(cleaned)) return true;
            if (string.Equals(cleaned, UiJobRunner.DefaultDescribeInstruction, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string ComputeNormHash(string kind, string cleanedPrompt)
        {
            var norm = WhitespaceRegex.Replace(cleanedPrompt.Trim(), " ").ToLowerInvariant();
            var bytes = Encoding.UTF8.GetBytes($"{kind}\0{norm}");
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        public static string ClassifyMicKind(IEnumerable<string>? models)
        {
            var list = (models ?? Array.Empty<string>())
                .Select(m => (m ?? "").Trim().ToLowerInvariant())
                .Where(m => m.Length > 0)
                .ToList();
            if (list.Count == 0) return "image";
            if (list.All(m => m == "grok-web-video")) return "video";
            if (list.All(m => m.StartsWith("describe-", StringComparison.Ordinal) || m == "layout-map")) return "describe";
            return "image";
        }

        public static string? BuildFtsQuery(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = new List<string>();
            foreach (Match match in FtsTokenRegex.Matches(raw))
            {
                if (match.Groups[1].Success)
                {
                    var phrase = match.Groups[1].Value.Trim();
                    if (phrase.Length > 0)
                    {
                        var escaped = phrase.Replace("\"", "\"\"");
                        parts.Add($"\"{escaped}\"");
                    }
                }
                else if (match.Groups[2].Success)
                {
                    var token = match.Groups[2].Value.Trim();
                    if (token.Length > 0)
                    {
                        var escaped = token.Replace("\"", "\"\"");
                        parts.Add(token.Length >= 2 ? $"\"{escaped}\"*" : $"\"{escaped}\"");
                    }
                }
                if (parts.Count >= 12) break;
            }
            return parts.Count == 0 ? null : string.Join(" AND ", parts);
        }

        public UiPromptArchiveStats GetStats()
        {
            if (!Exists)
            {
                return new UiPromptArchiveStats(
                    Exists: false,
                    TotalPrompts: 0,
                    TotalOccurrences: 0,
                    EarliestUtc: null,
                    LatestUtc: null,
                    CurrentInstance: _currentInstance,
                    Sources: Array.Empty<UiPromptArchiveSourceSummary>(),
                    Years: Array.Empty<UiPromptArchiveYearCount>(),
                    Kinds: Array.Empty<UiPromptArchiveKindCount>());
            }

            using var conn = OpenReadOnly();
            VerifySchemaVersion(conn);

            int totalPrompts;
            int totalOccurrences;
            string? earliestUtc;
            string? latestUtc;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT COUNT(*), COALESCE(SUM(occurrence_count), 0), MIN(first_seen_utc), MAX(last_seen_utc)
                    FROM prompts";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                totalPrompts = reader.GetInt32(0);
                totalOccurrences = reader.GetInt32(1);
                earliestUtc = reader.IsDBNull(2) ? null : reader.GetString(2);
                latestUtc = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            var sources = new List<UiPromptArchiveSourceSummary>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT source_id, label, category, origin_hint, unique_prompts, total_occurrences, earliest_utc, latest_utc
                    FROM sources
                    ORDER BY CASE WHEN earliest_utc IS NULL THEN 0 ELSE 1 END, earliest_utc ASC, source_id ASC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    sources.Add(new UiPromptArchiveSourceSummary(
                        Id: reader.GetString(0),
                        Label: reader.GetString(1),
                        Category: reader.GetString(2),
                        OriginHint: reader.GetString(3),
                        UniquePrompts: reader.GetInt32(4),
                        TotalOccurrences: reader.GetInt32(5),
                        EarliestUtc: reader.IsDBNull(6) ? null : reader.GetString(6),
                        LatestUtc: reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            var years = new List<UiPromptArchiveYearCount>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT first_year, COUNT(*)
                    FROM prompts
                    GROUP BY first_year
                    ORDER BY CASE WHEN first_year = 0 THEN 1 ELSE 0 END, first_year DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    years.Add(new UiPromptArchiveYearCount(reader.GetInt32(0), reader.GetInt32(1)));
                }
            }

            var kinds = new List<UiPromptArchiveKindCount>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT kind, COUNT(*)
                    FROM prompts
                    GROUP BY kind
                    ORDER BY COUNT(*) DESC, kind ASC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    kinds.Add(new UiPromptArchiveKindCount(reader.GetString(0), reader.GetInt32(1)));
                }
            }

            return new UiPromptArchiveStats(
                Exists: true,
                TotalPrompts: totalPrompts,
                TotalOccurrences: totalOccurrences,
                EarliestUtc: earliestUtc,
                LatestUtc: latestUtc,
                CurrentInstance: _currentInstance,
                Sources: sources,
                Years: years,
                Kinds: kinds);
        }

        public UiPromptArchiveRandomResult GetRandom(
            UiPromptArchiveFilter filter,
            int count = 10,
            IReadOnlyCollection<long>? excludeIds = null)
        {
            if (count < 1 || count > MaxRandomCount)
                throw new InvalidDataException($"count must be between 1 and {MaxRandomCount}.");
            ValidateFilter(filter);

            if (!Exists)
                return new UiPromptArchiveRandomResult(false, 0, Array.Empty<UiPromptArchiveItem>());

            using var conn = OpenReadOnly();
            VerifySchemaVersion(conn);

            var matchingIds = new List<long>();
            using (var cmd = conn.CreateCommand())
            {
                var (fromClause, whereClause) = BuildFilterSql(filter, cmd);
                cmd.CommandText = $"SELECT p.id FROM {fromClause}{whereClause}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    matchingIds.Add(reader.GetInt64(0));
                }
            }

            var totalMatching = matchingIds.Count;
            if (totalMatching == 0)
                return new UiPromptArchiveRandomResult(true, 0, Array.Empty<UiPromptArchiveItem>());

            List<long> pool = matchingIds;
            var exhaustedSeen = false;
            if (excludeIds is { Count: > 0 })
            {
                var excluded = excludeIds as HashSet<long> ?? new HashSet<long>(excludeIds);
                var filtered = matchingIds.Where(id => !excluded.Contains(id)).ToList();
                if (filtered.Count > 0)
                    pool = filtered;
                else
                    exhaustedSeen = true;
            }

            var take = Math.Min(count, pool.Count);
            var chosenIds = new List<long>(take);
            for (var i = 0; i < take; i++)
            {
                var j = i + RandomNumberGenerator.GetInt32(pool.Count - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                chosenIds.Add(pool[i]);
            }

            var byId = FetchItemsByIds(conn, chosenIds);
            var ordered = chosenIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            return new UiPromptArchiveRandomResult(true, totalMatching, ordered, exhaustedSeen);
        }

        public UiPromptArchiveSearchResult Search(
            UiPromptArchiveFilter filter,
            string sort = "newest",
            int page = 1,
            int pageSize = 25)
        {
            if (page < 1) throw new InvalidDataException("page must be at least 1.");
            if (pageSize < 1 || pageSize > MaxPageSize)
                throw new InvalidDataException($"pageSize must be between 1 and {MaxPageSize}.");
            var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "newest" : sort.Trim().ToLowerInvariant();
            if (!AllowedSorts.Contains(normalizedSort))
                throw new InvalidDataException($"Unsupported sort '{sort}'.");
            ValidateFilter(filter);

            if (!Exists)
                return new UiPromptArchiveSearchResult(false, 0, page, pageSize, 0, Array.Empty<UiPromptArchiveItem>());

            using var conn = OpenReadOnly();
            VerifySchemaVersion(conn);

            int totalMatching;
            using (var countCmd = conn.CreateCommand())
            {
                var (fromClause, whereClause) = BuildFilterSql(filter, countCmd);
                countCmd.CommandText = $"SELECT COUNT(*) FROM {fromClause}{whereClause}";
                totalMatching = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (totalMatching == 0)
                return new UiPromptArchiveSearchResult(true, 0, page, pageSize, 0, Array.Empty<UiPromptArchiveItem>());

            var totalPages = (int)Math.Ceiling(totalMatching / (double)pageSize);
            var offset = (page - 1) * pageSize;

            var items = new List<UiPromptArchiveItem>();
            using (var cmd = conn.CreateCommand())
            {
                var hasFts = !string.IsNullOrEmpty(BuildFtsQuery(filter.Query));
                var (fromClause, whereClause) = BuildFilterSql(filter, cmd);
                var orderBy = (normalizedSort, hasFts) switch
                {
                    ("relevance", true) => "bm25(prompts_fts) ASC, COALESCE(p.last_seen_utc, '') DESC, p.id DESC",
                    ("oldest", _) => "CASE WHEN p.first_seen_utc IS NULL THEN 1 ELSE 0 END ASC, p.first_seen_utc ASC, p.id ASC",
                    ("longest", _) => "p.char_len DESC, COALESCE(p.last_seen_utc, '') DESC, p.id DESC",
                    ("most_used", _) => "p.occurrence_count DESC, COALESCE(p.last_seen_utc, '') DESC, p.id DESC",
                    _ => "CASE WHEN p.last_seen_utc IS NULL THEN 1 ELSE 0 END ASC, p.last_seen_utc DESC, p.id DESC",
                };
                cmd.CommandText = $@"
                    SELECT {PromptColumns}
                    FROM {fromClause}{whereClause}
                    ORDER BY {orderBy}
                    LIMIT @limit OFFSET @offset";
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadPromptItem(reader));
                }
            }

            return new UiPromptArchiveSearchResult(true, totalMatching, page, pageSize, totalPages, items);
        }

        public UiPromptArchiveItemDetail? GetItem(long id)
        {
            if (id <= 0) throw new InvalidDataException("Prompt id must be positive.");
            if (!Exists) return null;

            using var conn = OpenReadOnly();
            VerifySchemaVersion(conn);

            UiPromptArchiveItem? item = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {PromptColumns} FROM prompts p WHERE p.id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    item = ReadPromptItem(reader);
            }
            if (item == null) return null;

            int totalOcc;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM prompt_occurrences WHERE prompt_id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                totalOcc = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var occurrences = new List<UiPromptArchiveOccurrence>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT id, source_id, source_key, occurred_at_utc, model, origin_instance, origin_job_id, details_json
                    FROM prompt_occurrences
                    WHERE prompt_id = @id
                    ORDER BY CASE WHEN occurred_at_utc IS NULL THEN 1 ELSE 0 END ASC, occurred_at_utc DESC, id DESC
                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@limit", MaxDetailOccurrences);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var occInstance = reader.GetString(5);
                    var occJobId = reader.GetString(6);
                    var canOpen = occJobId.Length > 0 && string.Equals(occInstance, _currentInstance, StringComparison.Ordinal);
                    occurrences.Add(new UiPromptArchiveOccurrence(
                        Id: reader.GetInt64(0),
                        SourceId: reader.GetString(1),
                        SourceKey: reader.GetString(2),
                        OccurredAtUtc: reader.IsDBNull(3) ? null : reader.GetString(3),
                        Model: reader.GetString(4),
                        OriginInstance: occInstance,
                        OriginJobId: occJobId,
                        CanOpenJob: canOpen,
                        Details: ParseJsonDict(reader.GetString(7))));
                }
            }

            return new UiPromptArchiveItemDetail(item, occurrences, totalOcc);
        }

        public bool TryRecordLiveJob(UiJob job, bool allowEmptyCreator)
        {
            if (job == null) return false;
            var creator = (job.CreatorLogin ?? "").Trim();
            var isOwner = string.Equals(creator, UiLoginLinks.OwnerLogin, StringComparison.Ordinal)
                || (allowEmptyCreator && creator.Length == 0);
            if (!isOwner) return false;
            if (!Exists) return false;

            var cleaned = CleanPrompt(job.Prompt);
            if (IsSkippablePrompt(cleaned)) return false;

            var kind = ClassifyMicKind(job.GeneratorKeys);
            var sourceId = InstanceSourceId(_currentInstance);
            var sourceKey = $"{_currentInstance}:ui:{job.Id}";
            var occurredUtc = UiJobStorage.EnsureUtc(job.CreatedAt).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

            try
            {
                lock (_writeGate)
                {
                    using var conn = OpenReadWrite();
                    VerifySchemaVersion(conn);
                    using var tx = conn.BeginTransaction();
                    var res = UpsertOccurrenceLocked(
                        conn, tx,
                        sourceId: sourceId,
                        sourceKey: sourceKey,
                        promptText: cleaned,
                        kind: kind,
                        occurredAtUtc: occurredUtc,
                        models: job.GeneratorKeys ?? Array.Empty<string>(),
                        originInstance: _currentInstance,
                        originJobId: job.Id);
                    if (res.AddedOccurrence)
                    {
                        RefreshSourceStatsLocked(conn, tx, sourceId);
                    }
                    tx.Commit();
                    return res.AddedOccurrence;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"UI prompt archive: could not record live job {job.Id}: {ex.Message}");
                return false;
            }
        }

        public UiPromptArchiveSyncResult SyncFromInstance(string savesDir, bool allowEmptyCreator)
        {
            if (string.IsNullOrWhiteSpace(savesDir))
                throw new InvalidDataException("savesDir is required.");

            var sourceId = InstanceSourceId(_currentInstance);
            var entries = CollectInstanceEntries(savesDir, _currentInstance, allowEmptyCreator);

            lock (_writeGate)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var createdNew = !File.Exists(_dbPath);
                using var conn = OpenReadWrite();
                if (createdNew)
                {
                    CreateSchemaLocked(conn);
                }
                else
                {
                    VerifySchemaVersion(conn);
                }

                var addedPrompts = 0;
                var addedOccurrences = 0;

                using (var tx = conn.BeginTransaction())
                {
                    foreach (var entry in entries)
                    {
                        var res = UpsertOccurrenceLocked(
                            conn, tx,
                            sourceId: sourceId,
                            sourceKey: entry.SourceKey,
                            promptText: entry.PromptText,
                            kind: entry.Kind,
                            occurredAtUtc: entry.OccurredAtUtc,
                            models: entry.Models,
                            originInstance: entry.OriginInstance,
                            originJobId: entry.OriginJobId);
                        if (res.AddedPrompt) addedPrompts++;
                        if (res.AddedOccurrence) addedOccurrences++;
                    }
                    if (addedOccurrences > 0 || createdNew)
                    {
                        RefreshSourceStatsLocked(conn, tx, sourceId);
                    }
                    tx.Commit();
                }

                int totalPrompts;
                int totalOccurrences;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(occurrence_count), 0) FROM prompts";
                    using var r = cmd.ExecuteReader();
                    r.Read();
                    totalPrompts = r.GetInt32(0);
                    totalOccurrences = r.GetInt32(1);
                }

                return new UiPromptArchiveSyncResult(addedPrompts, addedOccurrences, totalPrompts, totalOccurrences);
            }
        }

        private const string PromptColumns = @"
            p.id, p.prompt_text, p.kind, p.char_len, p.word_count,
            p.first_seen_utc, p.last_seen_utc, p.first_year, p.last_year,
            p.occurrence_count, p.primary_source, p.sources_json, p.models_json,
            p.negative_prompt, p.extra_meta_json, p.origin_instance, p.origin_job_id";

        private UiPromptArchiveItem ReadPromptItem(SqliteDataReader r)
        {
            var originInstance = r.GetString(15);
            var originJobId = r.GetString(16);
            var canOpen = originJobId.Length > 0 && string.Equals(originInstance, _currentInstance, StringComparison.Ordinal);
            return new UiPromptArchiveItem(
                Id: r.GetInt64(0),
                PromptText: r.GetString(1),
                Kind: r.GetString(2),
                CharLen: r.GetInt32(3),
                WordCount: r.GetInt32(4),
                FirstSeenUtc: r.IsDBNull(5) ? null : r.GetString(5),
                LastSeenUtc: r.IsDBNull(6) ? null : r.GetString(6),
                FirstYear: r.GetInt32(7),
                LastYear: r.GetInt32(8),
                OccurrenceCount: r.GetInt32(9),
                PrimarySource: r.GetString(10),
                Sources: ParseStringList(r.GetString(11)),
                Models: ParseStringList(r.GetString(12)),
                NegativePrompt: r.GetString(13),
                ExtraMeta: ParseJsonDict(r.GetString(14)),
                OriginInstance: originInstance,
                OriginJobId: originJobId,
                CanOpenJob: canOpen);
        }

        private Dictionary<long, UiPromptArchiveItem> FetchItemsByIds(SqliteConnection conn, IReadOnlyList<long> ids)
        {
            var result = new Dictionary<long, UiPromptArchiveItem>(ids.Count);
            if (ids.Count == 0) return result;
            using var cmd = conn.CreateCommand();
            var placeholders = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                var pName = $"@id{i}";
                placeholders[i] = pName;
                cmd.Parameters.AddWithValue(pName, ids[i]);
            }
            cmd.CommandText = $"SELECT {PromptColumns} FROM prompts p WHERE p.id IN ({string.Join(",", placeholders)})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var item = ReadPromptItem(reader);
                result[item.Id] = item;
            }
            return result;
        }

        private static void ValidateFilter(UiPromptArchiveFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.Kind))
            {
                var k = filter.Kind.Trim().ToLowerInvariant();
                if (!AllowedKinds.Contains(k))
                    throw new InvalidDataException($"Unsupported kind '{filter.Kind}'.");
            }
            if (filter.Year.HasValue && filter.Year.Value != 0 && (filter.Year.Value < 2000 || filter.Year.Value > 2100))
                throw new InvalidDataException($"Invalid year '{filter.Year.Value}'.");
            if (filter.MinChars.HasValue && filter.MinChars.Value < 0)
                throw new InvalidDataException("minChars cannot be negative.");
            if (filter.MaxChars.HasValue && filter.MaxChars.Value < 0)
                throw new InvalidDataException("maxChars cannot be negative.");
            if (filter.MinChars.HasValue && filter.MaxChars.HasValue && filter.MinChars.Value > filter.MaxChars.Value)
                throw new InvalidDataException("minChars cannot exceed maxChars.");
        }

        private static (string FromClause, string WhereClause) BuildFilterSql(UiPromptArchiveFilter filter, SqliteCommand cmd)
        {
            var from = new StringBuilder("prompts p");
            var where = new List<string>();

            var fts = BuildFtsQuery(filter.Query);
            if (!string.IsNullOrEmpty(fts))
            {
                from.Append(" JOIN prompts_fts ON prompts_fts.rowid = p.id");
                where.Add("prompts_fts MATCH @fts");
                cmd.Parameters.AddWithValue("@fts", fts);
            }

            if (!string.IsNullOrWhiteSpace(filter.Source))
            {
                var parts = filter.Source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (parts.Length > 20)
                    throw new InvalidDataException("Too many source filters.");
                if (parts.Length == 1)
                {
                    where.Add("EXISTS (SELECT 1 FROM prompt_sources ps WHERE ps.prompt_id = p.id AND ps.source_id = @source0)");
                    cmd.Parameters.AddWithValue("@source0", parts[0]);
                }
                else if (parts.Length > 1)
                {
                    var names = new string[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        names[i] = "@source" + i;
                        cmd.Parameters.AddWithValue(names[i], parts[i]);
                    }
                    where.Add($"EXISTS (SELECT 1 FROM prompt_sources ps WHERE ps.prompt_id = p.id AND ps.source_id IN ({string.Join(", ", names)}))");
                }
            }

            if (filter.Year.HasValue)
            {
                if (filter.Year.Value == 0)
                {
                    where.Add("p.first_year = 0");
                }
                else
                {
                    where.Add("(p.first_year = @year OR p.last_year = @year)");
                    cmd.Parameters.AddWithValue("@year", filter.Year.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(filter.Kind))
            {
                var k = filter.Kind.Trim().ToLowerInvariant();
                if (k != "all")
                {
                    where.Add("p.kind = @kind");
                    cmd.Parameters.AddWithValue("@kind", k);
                }
            }

            if (filter.MinChars.HasValue && filter.MinChars.Value > 0)
            {
                where.Add("p.char_len >= @minChars");
                cmd.Parameters.AddWithValue("@minChars", filter.MinChars.Value);
            }

            if (filter.MaxChars.HasValue && filter.MaxChars.Value > 0)
            {
                where.Add("p.char_len <= @maxChars");
                cmd.Parameters.AddWithValue("@maxChars", filter.MaxChars.Value);
            }

            var whereSql = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
            return (from.ToString(), whereSql);
        }

        private SqliteConnection OpenReadOnly()
        {
            GenerationArchive.EnsureSqliteAvailable();
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            var conn = new SqliteConnection(csb.ConnectionString);
            try
            {
                conn.Open();
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        private SqliteConnection OpenReadWrite()
        {
            GenerationArchive.EnsureSqliteAvailable();
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            var conn = new SqliteConnection(csb.ConnectionString);
            try
            {
                conn.Open();
                using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = DELETE;";
                pragma.ExecuteNonQuery();
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        private static void VerifySchemaVersion(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            var val = cmd.ExecuteScalar() as string;
            if (!string.Equals(val, SchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Prompt archive schema version mismatch: expected '{SchemaVersion}', found '{val ?? "(missing)"}'.");
            }
        }

        private static void CreateSchemaLocked(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE sources (
                    source_id TEXT PRIMARY KEY,
                    label TEXT NOT NULL,
                    category TEXT NOT NULL,
                    origin_hint TEXT NOT NULL,
                    total_occurrences INTEGER NOT NULL DEFAULT 0,
                    unique_prompts INTEGER NOT NULL DEFAULT 0,
                    earliest_utc TEXT,
                    latest_utc TEXT
                );
                CREATE TABLE prompts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    norm_hash TEXT NOT NULL UNIQUE,
                    prompt_text TEXT NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'image',
                    char_len INTEGER NOT NULL,
                    word_count INTEGER NOT NULL,
                    first_seen_utc TEXT,
                    last_seen_utc TEXT,
                    first_year INTEGER NOT NULL DEFAULT 0,
                    last_year INTEGER NOT NULL DEFAULT 0,
                    occurrence_count INTEGER NOT NULL DEFAULT 1,
                    primary_source TEXT NOT NULL,
                    sources_json TEXT NOT NULL,
                    models_json TEXT NOT NULL,
                    negative_prompt TEXT NOT NULL DEFAULT '',
                    meta_text TEXT NOT NULL DEFAULT '',
                    extra_meta_json TEXT NOT NULL DEFAULT '{}',
                    origin_instance TEXT NOT NULL DEFAULT '',
                    origin_job_id TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX idx_prompts_first_year ON prompts(first_year);
                CREATE INDEX idx_prompts_last_year ON prompts(last_year);
                CREATE INDEX idx_prompts_last_seen ON prompts(last_seen_utc DESC);
                CREATE INDEX idx_prompts_first_seen ON prompts(first_seen_utc ASC);
                CREATE INDEX idx_prompts_char_len ON prompts(char_len);
                CREATE INDEX idx_prompts_occ_count ON prompts(occurrence_count DESC);
                CREATE INDEX idx_prompts_kind ON prompts(kind);
                CREATE INDEX idx_prompts_primary_source ON prompts(primary_source);

                CREATE TABLE prompt_sources (
                    prompt_id INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    PRIMARY KEY (prompt_id, source_id)
                );
                CREATE INDEX idx_prompt_sources_source ON prompt_sources(source_id, prompt_id);

                CREATE TABLE prompt_occurrences (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    prompt_id INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    source_key TEXT NOT NULL,
                    occurred_at_utc TEXT,
                    model TEXT NOT NULL DEFAULT '',
                    origin_instance TEXT NOT NULL DEFAULT '',
                    origin_job_id TEXT NOT NULL DEFAULT '',
                    details_json TEXT NOT NULL DEFAULT '{}',
                    UNIQUE (source_id, source_key)
                );
                CREATE INDEX idx_prompt_occurrences_prompt ON prompt_occurrences(prompt_id, occurred_at_utc DESC);

                CREATE VIRTUAL TABLE prompts_fts USING fts5(
                    prompt_text,
                    models_json,
                    meta_text,
                    content='prompts',
                    content_rowid='id',
                    tokenize='unicode61'
                );
                CREATE TRIGGER prompts_ai AFTER INSERT ON prompts BEGIN
                    INSERT INTO prompts_fts(rowid, prompt_text, models_json, meta_text)
                    VALUES (new.id, new.prompt_text, new.models_json, new.meta_text);
                END;
                CREATE TRIGGER prompts_ad AFTER DELETE ON prompts BEGIN
                    INSERT INTO prompts_fts(prompts_fts, rowid, prompt_text, models_json, meta_text)
                    VALUES ('delete', old.id, old.prompt_text, old.models_json, old.meta_text);
                END;
                CREATE TRIGGER prompts_au AFTER UPDATE OF prompt_text, models_json, meta_text ON prompts BEGIN
                    INSERT INTO prompts_fts(prompts_fts, rowid, prompt_text, models_json, meta_text)
                    VALUES ('delete', old.id, old.prompt_text, old.models_json, old.meta_text);
                    INSERT INTO prompts_fts(rowid, prompt_text, models_json, meta_text)
                    VALUES (new.id, new.prompt_text, new.models_json, new.meta_text);
                END;
                INSERT INTO meta (key, value) VALUES ('schema_version', '1');
                INSERT INTO meta (key, value) VALUES ('importer_version', 'live-sync');
                INSERT INTO meta (key, value) VALUES ('built_at_utc', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));";
            cmd.ExecuteNonQuery();
        }

        private static (bool AddedPrompt, bool AddedOccurrence) UpsertOccurrenceLocked(
            SqliteConnection conn,
            SqliteTransaction tx,
            string sourceId,
            string sourceKey,
            string promptText,
            string kind,
            string? occurredAtUtc,
            IReadOnlyList<string> models,
            string originInstance,
            string originJobId)
        {
            using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM prompt_occurrences WHERE source_id = @sid AND source_key = @skey";
                check.Parameters.AddWithValue("@sid", sourceId);
                check.Parameters.AddWithValue("@skey", sourceKey);
                if (check.ExecuteScalar() != null)
                    return (false, false);
            }

            var normHash = ComputeNormHash(kind, promptText);
            var cleanModels = models.Select(m => (m ?? "").Trim()).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (cleanModels.Count == 0) cleanModels.Add("MultiImageClient");
            var year = ParseYear(occurredAtUtc);

            long promptId = 0;
            bool addedPrompt = false;
            string? existingFirst = null;
            string? existingLast = null;
            int existingCount = 0;
            List<string> existingSources = new();
            List<string> existingModels = new();
            string existingOriginInstance = "";
            string existingOriginJobId = "";

            using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = @"
                    SELECT id, first_seen_utc, last_seen_utc, occurrence_count, sources_json, models_json, origin_instance, origin_job_id
                    FROM prompts WHERE norm_hash = @hash";
                find.Parameters.AddWithValue("@hash", normHash);
                using var r = find.ExecuteReader();
                if (r.Read())
                {
                    promptId = r.GetInt64(0);
                    existingFirst = r.IsDBNull(1) ? null : r.GetString(1);
                    existingLast = r.IsDBNull(2) ? null : r.GetString(2);
                    existingCount = r.GetInt32(3);
                    existingSources = ParseStringList(r.GetString(4));
                    existingModels = ParseStringList(r.GetString(5));
                    existingOriginInstance = r.GetString(6);
                    existingOriginJobId = r.GetString(7);
                }
            }

            if (promptId == 0)
            {
                var wordCount = promptText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO prompts (
                        norm_hash, prompt_text, kind, char_len, word_count,
                        first_seen_utc, last_seen_utc, first_year, last_year,
                        occurrence_count, primary_source, sources_json, models_json,
                        negative_prompt, meta_text, extra_meta_json, origin_instance, origin_job_id
                    ) VALUES (
                        @hash, @text, @kind, @clen, @wcnt,
                        @first, @last, @fy, @ly,
                        1, @psrc, @sjson, @mjson,
                        '', '', '{}', @oinst, @ojob
                    );
                    SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("@hash", normHash);
                ins.Parameters.AddWithValue("@text", promptText);
                ins.Parameters.AddWithValue("@kind", kind);
                ins.Parameters.AddWithValue("@clen", promptText.Length);
                ins.Parameters.AddWithValue("@wcnt", wordCount);
                ins.Parameters.AddWithValue("@first", (object?)occurredAtUtc ?? DBNull.Value);
                ins.Parameters.AddWithValue("@last", (object?)occurredAtUtc ?? DBNull.Value);
                ins.Parameters.AddWithValue("@fy", year);
                ins.Parameters.AddWithValue("@ly", year);
                ins.Parameters.AddWithValue("@psrc", sourceId);
                ins.Parameters.AddWithValue("@sjson", JsonSerializer.Serialize(new[] { sourceId }));
                ins.Parameters.AddWithValue("@mjson", JsonSerializer.Serialize(cleanModels));
                ins.Parameters.AddWithValue("@oinst", originInstance);
                ins.Parameters.AddWithValue("@ojob", originJobId);
                promptId = Convert.ToInt64(ins.ExecuteScalar(), CultureInfo.InvariantCulture);
                addedPrompt = true;
            }
            else
            {
                var newFirst = MinIso(existingFirst, occurredAtUtc);
                var newLast = MaxIso(existingLast, occurredAtUtc);
                if (!existingSources.Contains(sourceId, StringComparer.Ordinal))
                    existingSources.Add(sourceId);
                foreach (var m in cleanModels)
                {
                    if (!existingModels.Contains(m, StringComparer.Ordinal))
                        existingModels.Add(m);
                }
                var newOriginInst = existingOriginInstance;
                var newOriginJob = existingOriginJobId;
                if (originJobId.Length > 0 && (string.IsNullOrEmpty(existingOriginJobId) || string.CompareOrdinal(occurredAtUtc ?? "", existingLast ?? "") >= 0))
                {
                    newOriginInst = originInstance;
                    newOriginJob = originJobId;
                }

                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"
                    UPDATE prompts
                    SET first_seen_utc = @first,
                        last_seen_utc = @last,
                        first_year = @fy,
                        last_year = @ly,
                        occurrence_count = @cnt,
                        sources_json = @sjson,
                        models_json = @mjson,
                        origin_instance = @oinst,
                        origin_job_id = @ojob
                    WHERE id = @id";
                upd.Parameters.AddWithValue("@first", (object?)newFirst ?? DBNull.Value);
                upd.Parameters.AddWithValue("@last", (object?)newLast ?? DBNull.Value);
                upd.Parameters.AddWithValue("@fy", ParseYear(newFirst));
                upd.Parameters.AddWithValue("@ly", ParseYear(newLast));
                upd.Parameters.AddWithValue("@cnt", existingCount + 1);
                upd.Parameters.AddWithValue("@sjson", JsonSerializer.Serialize(existingSources));
                upd.Parameters.AddWithValue("@mjson", JsonSerializer.Serialize(existingModels));
                upd.Parameters.AddWithValue("@oinst", newOriginInst);
                upd.Parameters.AddWithValue("@ojob", newOriginJob);
                upd.Parameters.AddWithValue("@id", promptId);
                upd.ExecuteNonQuery();
            }

            using (var ps = conn.CreateCommand())
            {
                ps.Transaction = tx;
                ps.CommandText = "INSERT OR IGNORE INTO prompt_sources (prompt_id, source_id) VALUES (@pid, @sid)";
                ps.Parameters.AddWithValue("@pid", promptId);
                ps.Parameters.AddWithValue("@sid", sourceId);
                ps.ExecuteNonQuery();
            }

            using (var occ = conn.CreateCommand())
            {
                occ.Transaction = tx;
                occ.CommandText = @"
                    INSERT INTO prompt_occurrences (
                        prompt_id, source_id, source_key, occurred_at_utc, model, origin_instance, origin_job_id, details_json
                    ) VALUES (@pid, @sid, @skey, @ts, @model, @oinst, @ojob, '{}')";
                occ.Parameters.AddWithValue("@pid", promptId);
                occ.Parameters.AddWithValue("@sid", sourceId);
                occ.Parameters.AddWithValue("@skey", sourceKey);
                occ.Parameters.AddWithValue("@ts", (object?)occurredAtUtc ?? DBNull.Value);
                occ.Parameters.AddWithValue("@model", string.Join(", ", cleanModels));
                occ.Parameters.AddWithValue("@oinst", originInstance);
                occ.Parameters.AddWithValue("@ojob", originJobId);
                occ.ExecuteNonQuery();
            }

            return (addedPrompt, true);
        }

        private static void RefreshSourceStatsLocked(SqliteConnection conn, SqliteTransaction tx, string sourceId)
        {
            var (label, category, hint) = sourceId switch
            {
                "mic-ave" => ("MultiImageClient AVE Production (2026)", "multi-image-client", "AVE production owner-only UiHistory and UiGoalLoops"),
                "mic-vibecoders" => ("MultiImageClient Vibecoders Production (2026)", "multi-image-client", "Vibecoders production owner-only UiHistory and UiGoalLoops"),
                "mic-local" => ("MultiImageClient Local (2026)", "multi-image-client", "Local UiHistory and UiGoalLoops"),
                _ => ($"MultiImageClient ({sourceId})", "multi-image-client", $"Instance {sourceId} owner-only UiHistory and UiGoalLoops"),
            };

            int totalOcc;
            string? earliest;
            string? latest;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*), MIN(occurred_at_utc), MAX(occurred_at_utc) FROM prompt_occurrences WHERE source_id = @sid";
                cmd.Parameters.AddWithValue("@sid", sourceId);
                using var r = cmd.ExecuteReader();
                r.Read();
                totalOcc = r.GetInt32(0);
                earliest = r.IsDBNull(1) ? null : r.GetString(1);
                latest = r.IsDBNull(2) ? null : r.GetString(2);
            }

            int uniqueCount;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM prompt_sources WHERE source_id = @sid";
                cmd.Parameters.AddWithValue("@sid", sourceId);
                uniqueCount = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (totalOcc == 0) return;

            using var up = conn.CreateCommand();
            up.Transaction = tx;
            up.CommandText = @"
                INSERT INTO sources (source_id, label, category, origin_hint, total_occurrences, unique_prompts, earliest_utc, latest_utc)
                VALUES (@sid, @label, @cat, @hint, @occ, @uniq, @earliest, @latest)
                ON CONFLICT(source_id) DO UPDATE SET
                    total_occurrences = excluded.total_occurrences,
                    unique_prompts = excluded.unique_prompts,
                    earliest_utc = excluded.earliest_utc,
                    latest_utc = excluded.latest_utc";
            up.Parameters.AddWithValue("@sid", sourceId);
            up.Parameters.AddWithValue("@label", label);
            up.Parameters.AddWithValue("@cat", category);
            up.Parameters.AddWithValue("@hint", hint);
            up.Parameters.AddWithValue("@occ", totalOcc);
            up.Parameters.AddWithValue("@uniq", uniqueCount);
            up.Parameters.AddWithValue("@earliest", (object?)earliest ?? DBNull.Value);
            up.Parameters.AddWithValue("@latest", (object?)latest ?? DBNull.Value);
            up.ExecuteNonQuery();
        }

        private sealed record InstanceSyncEntry(
            string SourceKey,
            string PromptText,
            string Kind,
            string? OccurredAtUtc,
            IReadOnlyList<string> Models,
            string OriginInstance,
            string OriginJobId);

        private static List<InstanceSyncEntry> CollectInstanceEntries(string savesDir, string instanceId, bool allowEmptyCreator)
        {
            var entries = new List<InstanceSyncEntry>();
            var histDir = Path.Combine(savesDir, "UiHistory");
            if (Directory.Exists(histDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(histDir))
                {
                    var jobJson = Path.Combine(dir, "job.json");
                    if (!File.Exists(jobJson)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(jobJson));
                        var root = doc.RootElement;
                        var creator = root.TryGetProperty("CreatorLogin", out var crEl) && crEl.ValueKind == JsonValueKind.String
                            ? (crEl.GetString() ?? "").Trim()
                            : "";
                        var isOwner = string.Equals(creator, UiLoginLinks.OwnerLogin, StringComparison.Ordinal)
                            || (allowEmptyCreator && creator.Length == 0);
                        if (!isOwner) continue;

                        var rawPrompt = root.TryGetProperty("Prompt", out var pEl) && pEl.ValueKind == JsonValueKind.String
                            ? pEl.GetString()
                            : null;
                        var cleaned = CleanPrompt(rawPrompt);
                        if (IsSkippablePrompt(cleaned)) continue;

                        var jid = root.TryGetProperty("Id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString())
                            ? idEl.GetString()!.Trim()
                            : Path.GetFileName(dir);
                        var gens = new List<string>();
                        if (root.TryGetProperty("GeneratorKeys", out var gEl) && gEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in gEl.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                    gens.Add(item.GetString()!.Trim());
                            }
                        }
                        string? occurredUtc = null;
                        if (root.TryGetProperty("CreatedAt", out var dtEl) && dtEl.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(dtEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                        {
                            occurredUtc = dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                        }
                        entries.Add(new InstanceSyncEntry(
                            SourceKey: $"{instanceId}:ui:{jid}",
                            PromptText: cleaned,
                            Kind: ClassifyMicKind(gens),
                            OccurredAtUtc: occurredUtc,
                            Models: gens,
                            OriginInstance: instanceId,
                            OriginJobId: jid));
                    }
                    catch
                    {
                        // Skip unreadable job files during archive sync; job.json diagnostics live in UiJobStorage.
                    }
                }
            }

            var loopDir = Path.Combine(savesDir, "UiGoalLoops");
            if (Directory.Exists(loopDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(loopDir))
                {
                    var loopJson = Path.Combine(dir, "loop.json");
                    if (!File.Exists(loopJson)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(loopJson));
                        var root = doc.RootElement;
                        var creator = GetStringProp(root, "creatorLogin") ?? GetStringProp(root, "CreatorLogin") ?? "";
                        var isOwner = string.Equals(creator.Trim(), UiLoginLinks.OwnerLogin, StringComparison.Ordinal)
                            || (allowEmptyCreator && string.IsNullOrWhiteSpace(creator));
                        if (!isOwner) continue;

                        var goal = CleanPrompt(GetStringProp(root, "goal") ?? GetStringProp(root, "Goal"));
                        if (IsSkippablePrompt(goal)) continue;

                        var lid = (GetStringProp(root, "id") ?? Path.GetFileName(dir)).Trim();
                        var model = GetStringProp(root, "managerLabel") ?? GetStringProp(root, "managerModel") ?? "Goal Loop";
                        string? occurredUtc = null;
                        if (root.TryGetProperty("createdAtUnixMs", out var msEl) && msEl.TryGetInt64(out var ms) && ms > 0)
                        {
                            occurredUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                        }
                        entries.Add(new InstanceSyncEntry(
                            SourceKey: $"{instanceId}:goal:{lid}",
                            PromptText: goal,
                            Kind: "goal",
                            OccurredAtUtc: occurredUtc,
                            Models: new[] { model },
                            OriginInstance: "",
                            OriginJobId: ""));
                    }
                    catch
                    {
                    }
                }
            }

            return entries;
        }

        private static string InstanceSourceId(string instanceId) => instanceId switch
        {
            "ave" => "mic-ave",
            "vibecoders" => "mic-vibecoders",
            "local" => "mic-local",
            _ => "mic-" + instanceId,
        };

        private static string? GetStringProp(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private static int ParseYear(string? isoUtc)
            => isoUtc != null && isoUtc.Length >= 4 && int.TryParse(isoUtc.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 0;

        private static string? MinIso(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return string.CompareOrdinal(a, b) <= 0 ? a : b;
        }

        private static string? MaxIso(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return string.CompareOrdinal(a, b) >= 0 ? a : b;
        }

        private static List<string> ParseStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static Dictionary<string, JsonElement> ParseJsonDict(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, JsonElement>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new Dictionary<string, JsonElement>();
            }
            catch
            {
                return new Dictionary<string, JsonElement>();
            }
        }
    }
}
