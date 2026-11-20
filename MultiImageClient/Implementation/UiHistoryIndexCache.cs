#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Data.Sqlite;

namespace MultiImageClient
{
    /// Derived SQLite index of UiHistory/*/job.json headers so startup avoids
    /// opening hundreds of archived job.json files on slow disks or during
    /// idle-sleep wakeups. Disk job.json files remain the sole source of truth:
    /// missing, unfinished, current-day, or corrupt entries always re-read disk.
    public static class UiHistoryIndexCache
    {
        public const string FileName = "index-cache.sqlite3";
        private const string SchemaVersion = "2";
        private static readonly object WriteGate = new();

        public static string CachePath(string historyRoot) => Path.Combine(historyRoot, FileName);

        public static List<UiHistoryIndexEntry> Scan(
            string historyRoot,
            Func<string, UiHistoryIndexEntry?> readFolderFromDisk,
            DateTime localToday,
            out int cacheHits,
            out int diskReads)
        {
            cacheHits = 0;
            diskReads = 0;
            if (!Directory.Exists(historyRoot))
            {
                return new List<UiHistoryIndexEntry>();
            }

            var folderPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(historyRoot))
            {
                var name = Path.GetFileName(folder);
                if (!string.IsNullOrEmpty(name))
                {
                    folderPaths[name] = folder;
                }
            }

            if (folderPaths.Count == 0)
            {
                TryClearRemovedEntries(historyRoot, folderPaths.Keys);
                return new List<UiHistoryIndexEntry>();
            }

            lock (WriteGate)
            {
                try
                {
                    return ScanCore(historyRoot, folderPaths, readFolderFromDisk, localToday, out cacheHits, out diskReads);
                }
                catch (SqliteException ex)
                {
                    Logger.Log($"UI history index cache unreadable ({ex.Message}); rebuilding from job.json files.");
                    DeleteCacheFiles(historyRoot);
                    try
                    {
                        return ScanCore(historyRoot, folderPaths, readFolderFromDisk, localToday, out cacheHits, out diskReads);
                    }
                    catch (Exception rebuildEx)
                    {
                        Logger.Log($"UI history index cache rebuild failed ({rebuildEx.Message}); scanning disk directly.");
                        return ScanDiskOnly(folderPaths.Values, readFolderFromDisk, out diskReads);
                    }
                }
            }
        }

        public static void Upsert(string historyRoot, UiHistoryIndexEntry entry)
        {
            if (string.IsNullOrWhiteSpace(historyRoot) || !Directory.Exists(historyRoot))
            {
                return;
            }
            lock (WriteGate)
            {
                try
                {
                    using var connection = OpenConnection(historyRoot);
                    if (!EnsureSchema(connection))
                    {
                        ResetSchema(connection);
                    }
                    UpsertRow(connection, transaction: null, entry);
                }
                catch (SqliteException ex)
                {
                    Logger.Log($"UI history index cache update failed ({ex.Message}); dropping cache so next startup rebuilds.");
                    DeleteCacheFiles(historyRoot);
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI history index cache update skipped: {ex.Message}");
                }
            }
        }

        private static List<UiHistoryIndexEntry> ScanCore(
            string historyRoot,
            Dictionary<string, string> folderPaths,
            Func<string, UiHistoryIndexEntry?> readFolderFromDisk,
            DateTime localToday,
            out int cacheHits,
            out int diskReads)
        {
            cacheHits = 0;
            diskReads = 0;

            using var connection = OpenConnection(historyRoot);
            if (!EnsureSchema(connection))
            {
                // Schema mismatch: reset table cleanly and rebuild from job.json.
                ResetSchema(connection);
            }

            var cached = LoadAllRows(connection);
            var results = new List<UiHistoryIndexEntry>(folderPaths.Count);
            var toUpsert = new List<UiHistoryIndexEntry>();
            var toDelete = new List<string>();

            foreach (var cachedFolder in cached.Keys)
            {
                if (!folderPaths.ContainsKey(cachedFolder))
                {
                    toDelete.Add(cachedFolder);
                }
            }

            foreach (var (folderName, folderPath) in folderPaths)
            {
                if (cached.TryGetValue(folderName, out var row)
                    && row.Done
                    && row.CreatedAt.ToLocalTime().Date != localToday)
                {
                    results.Add(row);
                    cacheHits++;
                    continue;
                }

                diskReads++;
                var fresh = readFolderFromDisk(folderPath);
                if (fresh != null)
                {
                    results.Add(fresh);
                    toUpsert.Add(fresh);
                }
                else if (cached.ContainsKey(folderName))
                {
                    toDelete.Add(folderName);
                }
            }

            if (toUpsert.Count > 0 || toDelete.Count > 0)
            {
                using var tx = connection.BeginTransaction();
                foreach (var folderName in toDelete)
                {
                    DeleteRow(connection, tx, folderName);
                }
                foreach (var entry in toUpsert)
                {
                    UpsertRow(connection, tx, entry);
                }
                tx.Commit();
            }

            return results;
        }

        private static List<UiHistoryIndexEntry> ScanDiskOnly(
            IEnumerable<string> folderPaths,
            Func<string, UiHistoryIndexEntry?> readFolderFromDisk,
            out int diskReads)
        {
            diskReads = 0;
            var results = new List<UiHistoryIndexEntry>();
            foreach (var folder in folderPaths)
            {
                diskReads++;
                var entry = readFolderFromDisk(folder);
                if (entry != null)
                {
                    results.Add(entry);
                }
            }
            return results;
        }

        private static void TryClearRemovedEntries(string historyRoot, IEnumerable<string> activeFolders)
        {
            if (!File.Exists(CachePath(historyRoot)))
            {
                return;
            }
            lock (WriteGate)
            {
                try
                {
                    using var connection = OpenConnection(historyRoot);
                    if (!EnsureSchema(connection))
                    {
                        ResetSchema(connection);
                        return;
                    }
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DELETE FROM history_index;";
                    cmd.ExecuteNonQuery();
                }
                catch
                {
                    DeleteCacheFiles(historyRoot);
                }
            }
        }

        private static SqliteConnection OpenConnection(string historyRoot)
        {
            GenerationArchive.EnsureSqliteAvailable();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = CachePath(historyRoot),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5,
            };
            var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }

        private const string CreateHistoryIndexTableSql = @"
CREATE TABLE IF NOT EXISTS history_index (
    folder_name TEXT PRIMARY KEY,
    id TEXT NOT NULL,
    created_at_utc_ticks INTEGER NOT NULL,
    created_by TEXT NOT NULL,
    creator_login TEXT NOT NULL,
    source_job_id TEXT NOT NULL,
    split_group_id TEXT NOT NULL DEFAULT '',
    input_image_count INTEGER NOT NULL,
    done INTEGER NOT NULL
);";

        private static bool EnsureSchema(SqliteConnection connection)
        {
            using var create = connection.CreateCommand();
            create.CommandText = @"
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);" + CreateHistoryIndexTableSql;
            create.ExecuteNonQuery();

            using var check = connection.CreateCommand();
            check.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            var existing = check.ExecuteScalar() as string;
            if (existing == null)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('schema_version', $v);";
                insert.Parameters.AddWithValue("$v", SchemaVersion);
                insert.ExecuteNonQuery();
                return true;
            }
            return string.Equals(existing, SchemaVersion, StringComparison.Ordinal);
        }

        private static void ResetSchema(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
DROP TABLE IF EXISTS history_index;"
                + CreateHistoryIndexTableSql + @"
INSERT OR REPLACE INTO meta(key, value) VALUES('schema_version', $v);";
            cmd.Parameters.AddWithValue("$v", SchemaVersion);
            cmd.ExecuteNonQuery();
        }

        private static Dictionary<string, UiHistoryIndexEntry> LoadAllRows(SqliteConnection connection)
        {
            var map = new Dictionary<string, UiHistoryIndexEntry>(StringComparer.Ordinal);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT folder_name, id, created_at_utc_ticks, created_by, creator_login, source_job_id, split_group_id, input_image_count, done
FROM history_index;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var folderName = reader.GetString(0);
                var id = reader.GetString(1);
                var ticks = reader.GetInt64(2);
                if (string.IsNullOrWhiteSpace(folderName)
                    || string.IsNullOrWhiteSpace(id)
                    || ticks < DateTime.MinValue.Ticks
                    || ticks > DateTime.MaxValue.Ticks)
                {
                    continue;
                }
                map[folderName] = new UiHistoryIndexEntry
                {
                    FolderName = folderName,
                    Id = id,
                    CreatedAt = new DateTime(ticks, DateTimeKind.Utc),
                    CreatedBy = reader.GetString(3),
                    CreatorLogin = reader.GetString(4),
                    SourceJobId = reader.GetString(5),
                    SplitGroupId = reader.GetString(6),
                    InputImageCount = reader.GetInt32(7),
                    Done = reader.GetInt32(8) != 0,
                };
            }
            return map;
        }

        private static void UpsertRow(SqliteConnection connection, SqliteTransaction? transaction, UiHistoryIndexEntry entry)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT INTO history_index (
    folder_name, id, created_at_utc_ticks, created_by, creator_login, source_job_id, split_group_id, input_image_count, done
) VALUES (
    $folder, $id, $ticks, $createdBy, $creatorLogin, $sourceJobId, $splitGroupId, $inputCount, $done
)
ON CONFLICT(folder_name) DO UPDATE SET
    id = excluded.id,
    created_at_utc_ticks = excluded.created_at_utc_ticks,
    created_by = excluded.created_by,
    creator_login = excluded.creator_login,
    source_job_id = excluded.source_job_id,
    split_group_id = excluded.split_group_id,
    input_image_count = excluded.input_image_count,
    done = excluded.done;";
            cmd.Parameters.AddWithValue("$folder", entry.FolderName);
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$ticks", UiJobStorage.EnsureUtc(entry.CreatedAt).Ticks);
            cmd.Parameters.AddWithValue("$createdBy", entry.CreatedBy ?? "");
            cmd.Parameters.AddWithValue("$creatorLogin", entry.CreatorLogin ?? "");
            cmd.Parameters.AddWithValue("$sourceJobId", entry.SourceJobId ?? "");
            cmd.Parameters.AddWithValue("$splitGroupId", entry.SplitGroupId ?? "");
            cmd.Parameters.AddWithValue("$inputCount", entry.InputImageCount);
            cmd.Parameters.AddWithValue("$done", entry.Done ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteRow(SqliteConnection connection, SqliteTransaction transaction, string folderName)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM history_index WHERE folder_name = $folder;";
            cmd.Parameters.AddWithValue("$folder", folderName);
            cmd.ExecuteNonQuery();
        }

        private static void DeleteCacheFiles(string historyRoot)
        {
            var path = CachePath(historyRoot);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var candidate = path + suffix;
                try
                {
                    if (File.Exists(candidate))
                    {
                        File.Delete(candidate);
                    }
                }
                catch
                {
                    // Best-effort cleanup of corrupt cache file; direct scan follows.
                }
            }
        }
    }
}
