#nullable enable
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace MultiImageClient
{
    public sealed class UiActivityRecord
    {
        public long Id { get; init; }
        public long AtUnixMs { get; init; }
        public string Kind { get; init; } = "";
        public string Audience { get; init; } = "all";
        public string ActorLogin { get; init; } = "";
        public string ActorDisplay { get; init; } = "";
        public string TargetLogin { get; init; } = "";
        public string TargetDisplay { get; init; } = "";
        public string JobId { get; init; } = "";
        public string Generator { get; init; } = "";
        public int ImageIndex { get; init; } = -1;
        public string ResourceKind { get; init; } = "";
    }

    public sealed class UiUserRequestRecord
    {
        public long Sequence { get; init; }
        public string Id { get; init; } = "";
        public long SubmittedAtUnixMs { get; init; }
        public string SubmitterLogin { get; init; } = "";
        public string SubmitterDisplay { get; init; } = "";
        public string Body { get; init; } = "";
    }

    /// SQLite source of truth for low-volume shared-site social activity and
    /// user requests. Reads hydrate only bounded result pages; no activity or
    /// request history is retained in process memory.
    public sealed class UiCommunityStore
    {
        public const int MaxRequestChars = 4000;
        public const int MaxActivityRows = 5000;
        public static readonly TimeSpan ReturnAfter = TimeSpan.FromHours(6);

        private readonly object _sync = new();
        private readonly string _connectionString;

        public UiCommunityStore(Settings settings)
        {
            GenerationArchive.EnsureSqliteAvailable();
            var path = string.IsNullOrWhiteSpace(settings.UiCommunityDbPath)
                ? Path.Combine(settings.ImageDownloadBaseFolder, "ui-community.sqlite3")
                : settings.UiCommunityDbPath;
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                DefaultTimeout = 15,
                Pooling = false,
            }.ToString();

            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    CREATE TABLE IF NOT EXISTS ui_activity (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        at_unix_ms INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        audience TEXT NOT NULL,
                        actor_login TEXT NOT NULL,
                        actor_display TEXT NOT NULL,
                        target_login TEXT NOT NULL,
                        target_display TEXT NOT NULL,
                        job_id TEXT NOT NULL,
                        generator TEXT NOT NULL,
                        image_index INTEGER NOT NULL,
                        resource_kind TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_ui_activity_at
                        ON ui_activity(at_unix_ms);

                    CREATE TABLE IF NOT EXISTS ui_creator_presence (
                        identity_key TEXT PRIMARY KEY,
                        last_generation_unix_ms INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS ui_user_requests (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        id TEXT NOT NULL UNIQUE,
                        submitted_at_unix_ms INTEGER NOT NULL,
                        submitter_login TEXT NOT NULL,
                        submitter_display TEXT NOT NULL,
                        body TEXT NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }
        }

        public bool RecordGenerationStart(
            string actorLogin,
            string actorDisplay,
            string jobId,
            long atUnixMs)
        {
            var identityKey = string.IsNullOrWhiteSpace(actorLogin)
                ? "display:" + actorDisplay.Trim()
                : "login:" + actorLogin.Trim();
            if (identityKey.EndsWith(":", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Generation activity requires an actor identity.");
            }

            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                long? previous = null;
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        "SELECT last_generation_unix_ms FROM ui_creator_presence WHERE identity_key = $identity;";
                    read.Parameters.AddWithValue("$identity", identityKey);
                    var value = read.ExecuteScalar();
                    if (value != null && value != DBNull.Value)
                    {
                        previous = Convert.ToInt64(value);
                    }
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        INSERT INTO ui_creator_presence(identity_key, last_generation_unix_ms)
                        VALUES($identity, $at)
                        ON CONFLICT(identity_key) DO UPDATE
                        SET last_generation_unix_ms = excluded.last_generation_unix_ms;
                        """;
                    update.Parameters.AddWithValue("$identity", identityKey);
                    update.Parameters.AddWithValue("$at", atUnixMs);
                    update.ExecuteNonQuery();
                }

                var thresholdMs = (long)ReturnAfter.TotalMilliseconds;
                var isReturn = previous == null || atUnixMs - previous.Value >= thresholdMs;
                if (isReturn)
                {
                    InsertActivity(
                        connection,
                        transaction,
                        atUnixMs,
                        "creator-return",
                        "all",
                        actorLogin,
                        actorDisplay,
                        "",
                        "",
                        jobId,
                        "",
                        -1,
                        "job");
                    TrimActivity(connection, transaction);
                }
                transaction.Commit();
                return isReturn;
            }
        }

        public void RecordFavorite(
            string actorLogin,
            string actorDisplay,
            string targetLogin,
            string targetDisplay,
            string jobId,
            string resourceKind,
            string generator,
            int imageIndex,
            long atUnixMs)
        {
            if (resourceKind != "image" && resourceKind != "prompt")
            {
                throw new InvalidDataException("Favorite activity kind must be image or prompt.");
            }
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                InsertActivity(
                    connection,
                    transaction,
                    atUnixMs,
                    resourceKind == "image" ? "favorite-image" : "favorite-prompt",
                    "all",
                    actorLogin,
                    actorDisplay,
                    targetLogin,
                    targetDisplay,
                    jobId,
                    generator,
                    imageIndex,
                    resourceKind);
                TrimActivity(connection, transaction);
                transaction.Commit();
            }
        }

        public UiUserRequestRecord SubmitRequest(
            string submitterLogin,
            string submitterDisplay,
            string body,
            long submittedAtUnixMs)
        {
            body = body.Trim();
            if (body.Length == 0 || body.Length > MaxRequestChars)
            {
                throw new InvalidDataException(
                    $"Request text must be between 1 and {MaxRequestChars} characters.");
            }
            var id = Guid.NewGuid().ToString("N");
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                long sequence;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO ui_user_requests(
                            id, submitted_at_unix_ms, submitter_login,
                            submitter_display, body)
                        VALUES($id, $at, $login, $display, $body);
                        SELECT last_insert_rowid();
                        """;
                    command.Parameters.AddWithValue("$id", id);
                    command.Parameters.AddWithValue("$at", submittedAtUnixMs);
                    command.Parameters.AddWithValue("$login", submitterLogin);
                    command.Parameters.AddWithValue("$display", submitterDisplay);
                    command.Parameters.AddWithValue("$body", body);
                    sequence = Convert.ToInt64(command.ExecuteScalar());
                }
                InsertActivity(
                    connection,
                    transaction,
                    submittedAtUnixMs,
                    "request-submitted",
                    "developer",
                    submitterLogin,
                    submitterDisplay,
                    "",
                    "",
                    "",
                    "",
                    -1,
                    "request");
                TrimActivity(connection, transaction);
                transaction.Commit();
                return new UiUserRequestRecord
                {
                    Sequence = sequence,
                    Id = id,
                    SubmittedAtUnixMs = submittedAtUnixMs,
                    SubmitterLogin = submitterLogin,
                    SubmitterDisplay = submitterDisplay,
                    Body = body,
                };
            }
        }

        public (long Cursor, bool Reset, List<UiActivityRecord> Records) ReadActivityAfter(
            long? after,
            int limit = 200)
        {
            limit = Math.Clamp(limit, 1, 500);
            lock (_sync)
            {
                using var connection = OpenConnection();
                var maxId = ReadMaxId(connection, "ui_activity", "id");
                if (after == null)
                {
                    return (maxId, false, new List<UiActivityRecord>());
                }
                if (after.Value < 0 || after.Value > maxId)
                {
                    return (maxId, true, new List<UiActivityRecord>());
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, at_unix_ms, kind, audience, actor_login,
                           actor_display, target_login, target_display, job_id,
                           generator, image_index, resource_kind
                    FROM ui_activity
                    WHERE id > $after
                    ORDER BY id
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$after", after.Value);
                command.Parameters.AddWithValue("$limit", limit);
                var records = new List<UiActivityRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new UiActivityRecord
                    {
                        Id = reader.GetInt64(0),
                        AtUnixMs = reader.GetInt64(1),
                        Kind = reader.GetString(2),
                        Audience = reader.GetString(3),
                        ActorLogin = reader.GetString(4),
                        ActorDisplay = reader.GetString(5),
                        TargetLogin = reader.GetString(6),
                        TargetDisplay = reader.GetString(7),
                        JobId = reader.GetString(8),
                        Generator = reader.GetString(9),
                        ImageIndex = reader.GetInt32(10),
                        ResourceKind = reader.GetString(11),
                    });
                }
                var cursor = records.Count == 0 ? maxId : records[^1].Id;
                return (cursor, false, records);
            }
        }

        public (long Cursor, bool Reset, List<UiUserRequestRecord> Records) ReadRequestsAfter(
            long after,
            int limit = 200)
        {
            limit = Math.Clamp(limit, 1, 500);
            lock (_sync)
            {
                using var connection = OpenConnection();
                var maxId = ReadMaxId(connection, "ui_user_requests", "sequence");
                if (after < 0 || after > maxId)
                {
                    return (maxId, true, new List<UiUserRequestRecord>());
                }
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT sequence, id, submitted_at_unix_ms,
                           submitter_login, submitter_display, body
                    FROM ui_user_requests
                    WHERE sequence > $after
                    ORDER BY sequence
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$after", after);
                command.Parameters.AddWithValue("$limit", limit);
                var records = new List<UiUserRequestRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new UiUserRequestRecord
                    {
                        Sequence = reader.GetInt64(0),
                        Id = reader.GetString(1),
                        SubmittedAtUnixMs = reader.GetInt64(2),
                        SubmitterLogin = reader.GetString(3),
                        SubmitterDisplay = reader.GetString(4),
                        Body = reader.GetString(5),
                    });
                }
                var cursor = records.Count == 0 ? maxId : records[^1].Sequence;
                return (cursor, false, records);
            }
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static long ReadMaxId(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(MAX({column}), 0) FROM {table};";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static void InsertActivity(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long atUnixMs,
            string kind,
            string audience,
            string actorLogin,
            string actorDisplay,
            string targetLogin,
            string targetDisplay,
            string jobId,
            string generator,
            int imageIndex,
            string resourceKind)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ui_activity(
                    at_unix_ms, kind, audience, actor_login, actor_display,
                    target_login, target_display, job_id, generator,
                    image_index, resource_kind)
                VALUES(
                    $at, $kind, $audience, $actorLogin, $actorDisplay,
                    $targetLogin, $targetDisplay, $job, $generator,
                    $imageIndex, $resourceKind);
                """;
            command.Parameters.AddWithValue("$at", atUnixMs);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$audience", audience);
            command.Parameters.AddWithValue("$actorLogin", actorLogin ?? "");
            command.Parameters.AddWithValue("$actorDisplay", actorDisplay ?? "");
            command.Parameters.AddWithValue("$targetLogin", targetLogin ?? "");
            command.Parameters.AddWithValue("$targetDisplay", targetDisplay ?? "");
            command.Parameters.AddWithValue("$job", jobId ?? "");
            command.Parameters.AddWithValue("$generator", generator ?? "");
            command.Parameters.AddWithValue("$imageIndex", imageIndex);
            command.Parameters.AddWithValue("$resourceKind", resourceKind ?? "");
            command.ExecuteNonQuery();
        }

        private static void TrimActivity(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM ui_activity
                WHERE id <= (
                    SELECT id
                    FROM ui_activity
                    ORDER BY id DESC
                    LIMIT 1 OFFSET $keep
                );
                """;
            command.Parameters.AddWithValue("$keep", MaxActivityRows);
            command.ExecuteNonQuery();
        }
    }
}
