#nullable enable
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    public sealed class UiProfileRecord
    {
        public string Login { get; init; } = "";
        public string PublicId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public long UpdatedAtUnixMs { get; init; }
    }

    public sealed class UiProfileSnapshot
    {
        public long Version { get; init; }
        public List<UiProfileRecord> Profiles { get; init; } = new();

        public string ResolveDisplay(string login, string originalDisplay)
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                return originalDisplay;
            }
            var profile = Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Login, login, StringComparison.OrdinalIgnoreCase));
            return profile?.DisplayName ?? originalDisplay;
        }
    }

    public sealed class UiProfileNameConflictException : InvalidOperationException
    {
        public UiProfileNameConflictException(string message)
            : base(message)
        {
        }
    }

    public sealed class UiGeneratorPresetRecord
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public List<string> GeneratorKeys { get; init; } = new();
    }

    public sealed class UiGeneratorEndpointConfigurationRecord
    {
        public string Key { get; init; } = "";
        public string? ExtraText { get; init; }
        public string? Notes { get; init; }
    }

    public sealed class UiGeneratorPreferencesRecord
    {
        public string DefaultView { get; init; } = "all";
        public string Login { get; init; } = "";
        public bool ShowImageSection { get; init; } = true;
        public bool ShowDescribeSection { get; init; } = true;
        public List<string> HiddenGeneratorKeys { get; init; } = new();
        public List<string> DefaultSelectedKeys { get; init; } = new();
        public List<UiGeneratorPresetRecord> Presets { get; init; } = new();
        public List<UiGeneratorEndpointConfigurationRecord> EndpointConfigurations { get; init; } = new();
        public long UpdatedAtUnixMs { get; init; }
    }

    public sealed class UiClaudePromptExchangeRecord
    {
        public string Id { get; init; } = "";
        public long RequestedAtUnixMs { get; init; }
        public long? CompletedAtUnixMs { get; init; }
        public string IdentityKey { get; init; } = "";
        public string ActorDisplay { get; init; } = "";
        public string Model { get; init; } = "";
        public string Instruction { get; init; } = "";
        public string OriginalPrompt { get; init; } = "";
        public string SystemPrompt { get; init; } = "";
        public string WirePrompt { get; init; } = "";
        public string RawResponse { get; init; } = "";
        public string ResultPrompt { get; init; } = "";
        public string Status { get; init; } = "";
        public string Error { get; init; } = "";
    }

    public sealed class UiSubjectTagMatchedPrompt
    {
        public string Source { get; init; } = "";
        public string RecordId { get; init; } = "";
        public string CreatedAtUtc { get; init; } = "";
        public string Prompt { get; init; } = "";
    }

    public sealed class UiSubjectTagRecord
    {
        public string Id { get; init; } = "";
        public string IdentityKey { get; init; } = "";
        public string ActorDisplay { get; init; } = "";
        public string Name { get; init; } = "";
        public string NormalizedName { get; init; } = "";
        public string Kind { get; init; } = "character";
        public string SearchQuery { get; init; } = "";
        public string Model { get; init; } = "";
        public string Description { get; init; } = "";
        public string SourceSummary { get; init; } = "";
        public int MatchedPromptsCount { get; init; }
        public List<UiSubjectTagMatchedPrompt> MatchedPrompts { get; init; } = new();
        public string RawResponse { get; init; } = "";
        public long CreatedAtUnixMs { get; init; }
        public long UpdatedAtUnixMs { get; init; }
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

                    CREATE TABLE IF NOT EXISTS ui_profiles (
                        login TEXT PRIMARY KEY COLLATE NOCASE,
                        public_id TEXT NOT NULL UNIQUE,
                        display_name TEXT NOT NULL,
                        updated_at_unix_ms INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS ui_profile_aliases (
                        normalized_name TEXT PRIMARY KEY,
                        display_name TEXT NOT NULL,
                        owner_login TEXT NOT NULL COLLATE NOCASE
                    );

                    CREATE TABLE IF NOT EXISTS ui_profile_meta (
                        singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                        revision INTEGER NOT NULL
                    );
                    INSERT OR IGNORE INTO ui_profile_meta(singleton, revision)
                    VALUES(1, 0);

                    CREATE TABLE IF NOT EXISTS ui_generator_preferences (
                        login TEXT PRIMARY KEY COLLATE NOCASE,
                        show_image_section INTEGER NOT NULL,
                        show_describe_section INTEGER NOT NULL,
                        hidden_generator_keys_json TEXT NOT NULL,
                        default_selected_keys_json TEXT NOT NULL,
                        presets_json TEXT NOT NULL,
                        endpoint_configurations_json TEXT NOT NULL DEFAULT '[]',
                        updated_at_unix_ms INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS ui_claude_prompt_exchanges (
                        id TEXT PRIMARY KEY,
                        requested_at_unix_ms INTEGER NOT NULL,
                        completed_at_unix_ms INTEGER,
                        identity_key TEXT NOT NULL,
                        actor_display TEXT NOT NULL,
                        model TEXT NOT NULL,
                        instruction TEXT NOT NULL,
                        original_prompt TEXT NOT NULL,
                        system_prompt TEXT NOT NULL,
                        wire_prompt TEXT NOT NULL,
                        raw_response TEXT NOT NULL,
                        result_prompt TEXT NOT NULL,
                        status TEXT NOT NULL,
                        error TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_ui_claude_prompt_exchanges_identity_time
                        ON ui_claude_prompt_exchanges(identity_key, requested_at_unix_ms DESC);

                    CREATE TABLE IF NOT EXISTS ui_migrations (
                        id TEXT PRIMARY KEY,
                        applied_at_unix_ms INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS ui_subject_tags (
                        id TEXT PRIMARY KEY,
                        identity_key TEXT NOT NULL,
                        actor_display TEXT NOT NULL,
                        name TEXT NOT NULL,
                        normalized_name TEXT NOT NULL,
                        subject_kind TEXT NOT NULL,
                        search_query TEXT NOT NULL,
                        model TEXT NOT NULL,
                        description TEXT NOT NULL,
                        source_summary TEXT NOT NULL,
                        matched_prompts_count INTEGER NOT NULL,
                        matched_prompts_json TEXT NOT NULL,
                        raw_response TEXT NOT NULL,
                        created_at_unix_ms INTEGER NOT NULL,
                        updated_at_unix_ms INTEGER NOT NULL,
                        UNIQUE(identity_key, normalized_name)
                    );
                    CREATE INDEX IF NOT EXISTS ix_ui_subject_tags_identity_updated
                        ON ui_subject_tags(identity_key, updated_at_unix_ms DESC);
                    """;
                command.ExecuteNonQuery();
                EnsureColumn(connection, "ui_generator_preferences", "default_view", "TEXT NOT NULL DEFAULT 'all'");
                EnsureColumn(
                    connection,
                    "ui_generator_preferences",
                    "endpoint_configurations_json",
                    "TEXT NOT NULL DEFAULT '[]'");
            }
        }

        public UiGeneratorPreferencesRecord? GetGeneratorPreferences(string login)
        {
            login = login.Trim();
            if (login.Length == 0)
            {
                return null;
            }
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT show_image_section, show_describe_section,
                           hidden_generator_keys_json, default_selected_keys_json,
                           presets_json, endpoint_configurations_json,
                           updated_at_unix_ms, default_view
                    FROM ui_generator_preferences
                    WHERE login = $login;
                    """;
                command.Parameters.AddWithValue("$login", login);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }
                try
                {
                    return new UiGeneratorPreferencesRecord
                    {
                        Login = login,
                        ShowImageSection = reader.GetInt64(0) != 0,
                        ShowDescribeSection = reader.GetInt64(1) != 0,
                        HiddenGeneratorKeys = DeserializeList(reader.GetString(2)),
                        DefaultSelectedKeys = DeserializeList(reader.GetString(3)),
                        Presets = JsonSerializer.Deserialize<List<UiGeneratorPresetRecord>>(
                            reader.GetString(4)) ?? new List<UiGeneratorPresetRecord>(),
                        EndpointConfigurations =
                            JsonSerializer.Deserialize<List<UiGeneratorEndpointConfigurationRecord>>(
                                reader.GetString(5))
                            ?? new List<UiGeneratorEndpointConfigurationRecord>(),
                        UpdatedAtUnixMs = reader.GetInt64(6),
                        DefaultView = reader.GetString(7),
                    };
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Stored generator preferences for '{login}' are malformed.", ex);
                }
            }
        }

        public void SaveGeneratorPreferences(UiGeneratorPreferencesRecord preferences)
        {
            var login = preferences.Login.Trim();
            if (login.Length == 0)
            {
                throw new InvalidDataException("Generator preferences require an authenticated login.");
            }
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO ui_generator_preferences(
                        login, show_image_section, show_describe_section,
                        hidden_generator_keys_json, default_selected_keys_json,
                        presets_json, endpoint_configurations_json,
                        updated_at_unix_ms, default_view)
                    VALUES(
                        $login, $showImage, $showDescribe, $hidden, $selected,
                        $presets, $endpointConfigurations, $updated, $defaultView)
                    ON CONFLICT(login) DO UPDATE SET
                        default_view = excluded.default_view,
                        show_image_section = excluded.show_image_section,
                        show_describe_section = excluded.show_describe_section,
                        hidden_generator_keys_json = excluded.hidden_generator_keys_json,
                        default_selected_keys_json = excluded.default_selected_keys_json,
                        presets_json = excluded.presets_json,
                        endpoint_configurations_json = excluded.endpoint_configurations_json,
                        updated_at_unix_ms = excluded.updated_at_unix_ms;
                    """;
                command.Parameters.AddWithValue("$login", login);
                command.Parameters.AddWithValue("$defaultView", preferences.DefaultView);
                command.Parameters.AddWithValue("$showImage", preferences.ShowImageSection ? 1 : 0);
                command.Parameters.AddWithValue("$showDescribe", preferences.ShowDescribeSection ? 1 : 0);
                command.Parameters.AddWithValue("$hidden", JsonSerializer.Serialize(preferences.HiddenGeneratorKeys));
                command.Parameters.AddWithValue("$selected", JsonSerializer.Serialize(preferences.DefaultSelectedKeys));
                command.Parameters.AddWithValue("$presets", JsonSerializer.Serialize(preferences.Presets));
                command.Parameters.AddWithValue(
                    "$endpointConfigurations",
                    JsonSerializer.Serialize(preferences.EndpointConfigurations));
                command.Parameters.AddWithValue("$updated", preferences.UpdatedAtUnixMs);
                command.ExecuteNonQuery();
            }
        }

        // Adds key to every saved account default list once per database. The
        // recorded migration keeps a later removal by the user in place. Lists
        // that hide the key or already select it stay unchanged.
        public int AddDefaultSelectedKeyOnce(string migrationId, string key, long appliedAtUnixMs)
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                if (MigrationApplied(connection, transaction, migrationId))
                {
                    return 0;
                }
                var updates = new List<(string Login, List<string> Selected)>();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        "SELECT login, hidden_generator_keys_json, default_selected_keys_json FROM ui_generator_preferences;";
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        var login = reader.GetString(0);
                        List<string> hidden, selected;
                        try
                        {
                            hidden = DeserializeList(reader.GetString(1));
                            selected = DeserializeList(reader.GetString(2));
                        }
                        catch (JsonException ex)
                        {
                            throw new InvalidDataException(
                                $"Stored generator preferences for '{login}' are malformed.", ex);
                        }
                        if (hidden.Contains(key, StringComparer.Ordinal) || selected.Contains(key, StringComparer.Ordinal))
                        {
                            continue;
                        }
                        selected.Add(key);
                        updates.Add((login, selected));
                    }
                }
                foreach (var (login, selected) in updates)
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        "UPDATE ui_generator_preferences SET default_selected_keys_json = $selected WHERE login = $login;";
                    update.Parameters.AddWithValue("$selected", JsonSerializer.Serialize(selected));
                    update.Parameters.AddWithValue("$login", login);
                    update.ExecuteNonQuery();
                }
                RecordMigration(connection, transaction, migrationId, appliedAtUnixMs);
                transaction.Commit();
                return updates.Count;
            }
        }

        public bool MigrationApplied(string migrationId)
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                return MigrationApplied(connection, null, migrationId);
            }
        }

        public void RecordMigration(string migrationId, long appliedAtUnixMs)
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                RecordMigration(connection, null, migrationId, appliedAtUnixMs);
            }
        }

        private static bool MigrationApplied(SqliteConnection connection, SqliteTransaction? transaction, string migrationId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM ui_migrations WHERE id = $id;";
            command.Parameters.AddWithValue("$id", migrationId);
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }

        private static void RecordMigration(
            SqliteConnection connection, SqliteTransaction? transaction, string migrationId, long appliedAtUnixMs)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO ui_migrations(id, applied_at_unix_ms) VALUES($id, $at);";
            command.Parameters.AddWithValue("$id", migrationId);
            command.Parameters.AddWithValue("$at", appliedAtUnixMs);
            command.ExecuteNonQuery();
        }

        public UiClaudePromptExchangeRecord StartClaudePromptExchange(
            string identityKey,
            string actorDisplay,
            string model,
            string instruction,
            string originalPrompt,
            string systemPrompt,
            string wirePrompt,
            long requestedAtUnixMs)
        {
            identityKey = identityKey.Trim();
            actorDisplay = actorDisplay.Trim();
            if (identityKey.Length == 0 || actorDisplay.Length == 0)
            {
                throw new InvalidDataException("A Claude prompt exchange requires an exact user identity.");
            }
            var record = new UiClaudePromptExchangeRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                RequestedAtUnixMs = requestedAtUnixMs,
                IdentityKey = identityKey,
                ActorDisplay = actorDisplay,
                Model = model,
                Instruction = instruction,
                OriginalPrompt = originalPrompt,
                SystemPrompt = systemPrompt,
                WirePrompt = wirePrompt,
                Status = "pending",
            };
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO ui_claude_prompt_exchanges(
                        id, requested_at_unix_ms, completed_at_unix_ms,
                        identity_key, actor_display, model, instruction,
                        original_prompt, system_prompt, wire_prompt,
                        raw_response, result_prompt, status, error)
                    VALUES(
                        $id, $requested, NULL, $identity, $display, $model,
                        $instruction, $original, $system, $wire, '', '',
                        'pending', '');
                    """;
                command.Parameters.AddWithValue("$id", record.Id);
                command.Parameters.AddWithValue("$requested", record.RequestedAtUnixMs);
                command.Parameters.AddWithValue("$identity", record.IdentityKey);
                command.Parameters.AddWithValue("$display", record.ActorDisplay);
                command.Parameters.AddWithValue("$model", record.Model);
                command.Parameters.AddWithValue("$instruction", record.Instruction);
                command.Parameters.AddWithValue("$original", record.OriginalPrompt);
                command.Parameters.AddWithValue("$system", record.SystemPrompt);
                command.Parameters.AddWithValue("$wire", record.WirePrompt);
                command.ExecuteNonQuery();
            }
            return record;
        }

        public void CompleteClaudePromptExchange(
            string id,
            string rawResponse,
            string resultPrompt,
            string error,
            long completedAtUnixMs)
        {
            var status = error.Length == 0 ? "succeeded" : "failed";
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE ui_claude_prompt_exchanges
                    SET completed_at_unix_ms = $completed,
                        raw_response = $raw,
                        result_prompt = $result,
                        status = $status,
                        error = $error
                    WHERE id = $id AND status = 'pending';
                    """;
                command.Parameters.AddWithValue("$completed", completedAtUnixMs);
                command.Parameters.AddWithValue("$raw", rawResponse);
                command.Parameters.AddWithValue("$result", resultPrompt);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$error", error);
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException(
                        $"Claude prompt exchange '{id}' is missing or already completed.");
                }
            }
        }

        public List<UiClaudePromptExchangeRecord> ReadClaudePromptExchanges(
            string identityKey,
            int limit = 50,
            long? beforeTime = null,
            string? beforeId = null,
            string? exchangeId = null)
        {
            identityKey = identityKey.Trim();
            if (identityKey.Length == 0)
            {
                throw new InvalidDataException("Claude prompt history requires an exact user identity.");
            }
            limit = Math.Clamp(limit, 1, 200);
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, requested_at_unix_ms, completed_at_unix_ms,
                           identity_key, actor_display, model, instruction,
                           original_prompt, system_prompt, wire_prompt,
                           raw_response, result_prompt, status, error
                    FROM ui_claude_prompt_exchanges
                    WHERE identity_key = $identity
                      AND ($id IS NULL OR id = $id)
                      AND ($beforeTime IS NULL OR requested_at_unix_ms < $beforeTime
                           OR (requested_at_unix_ms = $beforeTime AND id < $beforeId))
                    ORDER BY requested_at_unix_ms DESC, id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$identity", identityKey);
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$id", (object?)exchangeId ?? DBNull.Value);
                command.Parameters.AddWithValue("$beforeTime", (object?)beforeTime ?? DBNull.Value);
                command.Parameters.AddWithValue("$beforeId", (object?)beforeId ?? DBNull.Value);
                var records = new List<UiClaudePromptExchangeRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new UiClaudePromptExchangeRecord
                    {
                        Id = reader.GetString(0),
                        RequestedAtUnixMs = reader.GetInt64(1),
                        CompletedAtUnixMs = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        IdentityKey = reader.GetString(3),
                        ActorDisplay = reader.GetString(4),
                        Model = reader.GetString(5),
                        Instruction = reader.GetString(6),
                        OriginalPrompt = reader.GetString(7),
                        SystemPrompt = reader.GetString(8),
                        WirePrompt = reader.GetString(9),
                        RawResponse = reader.GetString(10),
                        ResultPrompt = reader.GetString(11),
                        Status = reader.GetString(12),
                        Error = reader.GetString(13),
                    });
                }
                return records;
            }
        }

        public const int MaxSubjectTagNameChars = 80;
        public const int MaxSubjectTagDescriptionChars = 8000;
        public const int MaxSubjectTagQueryChars = 400;
        public const int MaxSubjectTagSummaryChars = 4000;

        public static readonly string[] ValidSubjectKinds =
        {
            "character",
            "location",
            "object",
            "general",
        };

        public static (string Display, string Normalized) NormalizeSubjectTagName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException("Subject tag name is required.");
            }
            foreach (var ch in name)
            {
                if (ch == '\r' || ch == '\n' || (char.IsControl(ch) && ch != ' ' && ch != '\t'))
                {
                    throw new InvalidDataException("Subject tag name cannot contain control characters or line breaks.");
                }
            }
            var collapsed = System.Text.RegularExpressions.Regex.Replace(
                name.Trim(),
                @"\s+",
                " ");
            if (collapsed.Length == 0 || collapsed.Length > MaxSubjectTagNameChars)
            {
                throw new InvalidDataException(
                    $"Subject tag name must be 1 to {MaxSubjectTagNameChars} characters.");
            }
            return (collapsed, collapsed.ToUpperInvariant());
        }

        public static string NormalizeSubjectKind(string? kind)
        {
            var normalized = (kind ?? "character").Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return "character";
            }
            if (!ValidSubjectKinds.Contains(normalized, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Subject kind '{kind}' is invalid. Valid kinds: {string.Join(", ", ValidSubjectKinds)}.");
            }
            return normalized;
        }

        public UiSubjectTagRecord UpsertSubjectTag(
            string identityKey,
            string actorDisplay,
            string name,
            string kind,
            string searchQuery,
            string model,
            string description,
            string sourceSummary,
            IReadOnlyList<UiSubjectTagMatchedPrompt>? matchedPrompts,
            string rawResponse,
            long nowUnixMs)
        {
            identityKey = (identityKey ?? "").Trim();
            if (identityKey.Length == 0)
            {
                throw new InvalidDataException("Subject tags require an exact user identity.");
            }
            actorDisplay = (actorDisplay ?? "").Trim();
            var (displayName, normalizedName) = NormalizeSubjectTagName(name);
            var normalizedKind = NormalizeSubjectKind(kind);
            searchQuery = (searchQuery ?? "").Trim();
            if (searchQuery.Length > MaxSubjectTagQueryChars)
            {
                throw new InvalidDataException(
                    $"Subject search query exceeds the {MaxSubjectTagQueryChars}-character limit.");
            }
            model = (model ?? "").Trim();
            if (model.Length == 0)
            {
                model = "manual";
            }
            description = (description ?? "").Trim();
            if (description.Length == 0)
            {
                throw new InvalidDataException("Subject description is required.");
            }
            if (description.Length > MaxSubjectTagDescriptionChars)
            {
                throw new InvalidDataException(
                    $"Subject description exceeds the {MaxSubjectTagDescriptionChars}-character limit.");
            }
            sourceSummary = (sourceSummary ?? "").Trim();
            if (sourceSummary.Length > MaxSubjectTagSummaryChars)
            {
                sourceSummary = sourceSummary.Substring(0, MaxSubjectTagSummaryChars);
            }
            var promptList = (matchedPrompts ?? Array.Empty<UiSubjectTagMatchedPrompt>())
                .Take(50)
                .ToList();
            var matchedPromptsJson = JsonSerializer.Serialize(promptList);
            rawResponse ??= "";

            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                string existingId = "";
                long createdAtUnixMs = nowUnixMs;
                using (var find = connection.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText =
                        """
                        SELECT id, created_at_unix_ms
                        FROM ui_subject_tags
                        WHERE identity_key = $identity AND normalized_name = $normalized
                        LIMIT 1;
                        """;
                    find.Parameters.AddWithValue("$identity", identityKey);
                    find.Parameters.AddWithValue("$normalized", normalizedName);
                    using var reader = find.ExecuteReader();
                    if (reader.Read())
                    {
                        existingId = reader.GetString(0);
                        createdAtUnixMs = reader.GetInt64(1);
                    }
                }

                var id = existingId.Length > 0 ? existingId : Guid.NewGuid().ToString("N");
                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText =
                        """
                        INSERT INTO ui_subject_tags(
                            id, identity_key, actor_display, name, normalized_name,
                            subject_kind, search_query, model, description,
                            source_summary, matched_prompts_count, matched_prompts_json,
                            raw_response, created_at_unix_ms, updated_at_unix_ms)
                        VALUES(
                            $id, $identity, $display, $name, $normalized,
                            $kind, $query, $model, $description,
                            $summary, $matchedCount, $matchedJson,
                            $raw, $created, $updated)
                        ON CONFLICT(identity_key, normalized_name) DO UPDATE SET
                            actor_display = excluded.actor_display,
                            name = excluded.name,
                            subject_kind = excluded.subject_kind,
                            search_query = excluded.search_query,
                            model = excluded.model,
                            description = excluded.description,
                            source_summary = excluded.source_summary,
                            matched_prompts_count = excluded.matched_prompts_count,
                            matched_prompts_json = excluded.matched_prompts_json,
                            raw_response = excluded.raw_response,
                            updated_at_unix_ms = excluded.updated_at_unix_ms;
                        """;
                    upsert.Parameters.AddWithValue("$id", id);
                    upsert.Parameters.AddWithValue("$identity", identityKey);
                    upsert.Parameters.AddWithValue("$display", actorDisplay);
                    upsert.Parameters.AddWithValue("$name", displayName);
                    upsert.Parameters.AddWithValue("$normalized", normalizedName);
                    upsert.Parameters.AddWithValue("$kind", normalizedKind);
                    upsert.Parameters.AddWithValue("$query", searchQuery);
                    upsert.Parameters.AddWithValue("$model", model);
                    upsert.Parameters.AddWithValue("$description", description);
                    upsert.Parameters.AddWithValue("$summary", sourceSummary);
                    upsert.Parameters.AddWithValue("$matchedCount", promptList.Count);
                    upsert.Parameters.AddWithValue("$matchedJson", matchedPromptsJson);
                    upsert.Parameters.AddWithValue("$raw", rawResponse);
                    upsert.Parameters.AddWithValue("$created", createdAtUnixMs);
                    upsert.Parameters.AddWithValue("$updated", nowUnixMs);
                    upsert.ExecuteNonQuery();
                }

                transaction.Commit();

                return new UiSubjectTagRecord
                {
                    Id = id,
                    IdentityKey = identityKey,
                    ActorDisplay = actorDisplay,
                    Name = displayName,
                    NormalizedName = normalizedName,
                    Kind = normalizedKind,
                    SearchQuery = searchQuery,
                    Model = model,
                    Description = description,
                    SourceSummary = sourceSummary,
                    MatchedPromptsCount = promptList.Count,
                    MatchedPrompts = promptList,
                    RawResponse = rawResponse,
                    CreatedAtUnixMs = createdAtUnixMs,
                    UpdatedAtUnixMs = nowUnixMs,
                };
            }
        }

        public List<UiSubjectTagRecord> ListSubjectTags(string identityKey)
        {
            identityKey = (identityKey ?? "").Trim();
            if (identityKey.Length == 0)
            {
                throw new InvalidDataException("Subject tags require an exact user identity.");
            }
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, identity_key, actor_display, name, normalized_name,
                           subject_kind, search_query, model, description,
                           source_summary, matched_prompts_count, matched_prompts_json,
                           raw_response, created_at_unix_ms, updated_at_unix_ms
                    FROM ui_subject_tags
                    WHERE identity_key = $identity
                    ORDER BY updated_at_unix_ms DESC, name COLLATE NOCASE ASC;
                    """;
                command.Parameters.AddWithValue("$identity", identityKey);
                var results = new List<UiSubjectTagRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(ReadSubjectTagRow(reader));
                }
                return results;
            }
        }

        public UiSubjectTagRecord? GetSubjectTag(string identityKey, string idOrName)
        {
            identityKey = (identityKey ?? "").Trim();
            idOrName = (idOrName ?? "").Trim();
            if (identityKey.Length == 0 || idOrName.Length == 0)
            {
                return null;
            }
            var normalized = System.Text.RegularExpressions.Regex.Replace(idOrName, @"\s+", " ").ToUpperInvariant();
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, identity_key, actor_display, name, normalized_name,
                           subject_kind, search_query, model, description,
                           source_summary, matched_prompts_count, matched_prompts_json,
                           raw_response, created_at_unix_ms, updated_at_unix_ms
                    FROM ui_subject_tags
                    WHERE identity_key = $identity
                      AND (id = $id OR normalized_name = $normalized)
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$identity", identityKey);
                command.Parameters.AddWithValue("$id", idOrName);
                command.Parameters.AddWithValue("$normalized", normalized);
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadSubjectTagRow(reader) : null;
            }
        }

        public bool DeleteSubjectTag(string identityKey, string idOrName)
        {
            identityKey = (identityKey ?? "").Trim();
            idOrName = (idOrName ?? "").Trim();
            if (identityKey.Length == 0 || idOrName.Length == 0)
            {
                throw new InvalidDataException("Deleting a subject tag requires both user identity and tag id or name.");
            }
            var normalized = System.Text.RegularExpressions.Regex.Replace(idOrName, @"\s+", " ").ToUpperInvariant();
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM ui_subject_tags
                    WHERE identity_key = $identity
                      AND (id = $id OR normalized_name = $normalized);
                    """;
                command.Parameters.AddWithValue("$identity", identityKey);
                command.Parameters.AddWithValue("$id", idOrName);
                command.Parameters.AddWithValue("$normalized", normalized);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public List<UiSubjectTagMatchedPrompt> SearchClaudePromptExchanges(
            string identityKey,
            IReadOnlyList<string> terms,
            int limit = 25)
        {
            if (terms == null || terms.Count == 0)
            {
                return new List<UiSubjectTagMatchedPrompt>();
            }
            var normalizedTerms = terms
                .Select(t => (t ?? "").Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedTerms.Count == 0)
            {
                return new List<UiSubjectTagMatchedPrompt>();
            }
            limit = Math.Clamp(limit, 1, 100);
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                var whereClauses = new List<string>();
                for (var i = 0; i < normalizedTerms.Count; i++)
                {
                    var p = $"$term{i}";
                    whereClauses.Add($"(INSTR(UPPER(original_prompt), {p}) > 0 OR INSTR(UPPER(result_prompt), {p}) > 0)");
                    command.Parameters.AddWithValue(p, normalizedTerms[i].ToUpperInvariant());
                }
                var identityFilter = string.IsNullOrWhiteSpace(identityKey)
                    ? ""
                    : "AND identity_key = $identity";
                command.CommandText =
                    $"""
                    SELECT id, requested_at_unix_ms, original_prompt, result_prompt
                    FROM ui_claude_prompt_exchanges
                    WHERE status = 'succeeded'
                      {identityFilter}
                      AND ({string.Join(" OR ", whereClauses)})
                    ORDER BY requested_at_unix_ms DESC
                    LIMIT $limit;
                    """;
                if (!string.IsNullOrWhiteSpace(identityKey))
                {
                    command.Parameters.AddWithValue("$identity", identityKey.Trim());
                }
                command.Parameters.AddWithValue("$limit", limit);

                var matches = new List<UiSubjectTagMatchedPrompt>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var atMs = reader.GetInt64(1);
                    var original = reader.GetString(2);
                    var result = reader.GetString(3);
                    var createdIso = DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime.ToString("O");
                    var text = result.Length > 0 && ContainsAnyTerm(result, normalizedTerms)
                        ? result
                        : original;
                    if (text.Length > 0)
                    {
                        matches.Add(new UiSubjectTagMatchedPrompt
                        {
                            Source = "prompt-exchange",
                            RecordId = id,
                            CreatedAtUtc = createdIso,
                            Prompt = text,
                        });
                    }
                }
                return matches;
            }
        }

        private static bool ContainsAnyTerm(string text, IReadOnlyList<string> terms)
        {
            foreach (var term in terms)
            {
                if (text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static UiSubjectTagRecord ReadSubjectTagRow(SqliteDataReader reader)
        {
            List<UiSubjectTagMatchedPrompt> matchedPrompts;
            try
            {
                matchedPrompts = JsonSerializer.Deserialize<List<UiSubjectTagMatchedPrompt>>(
                    reader.GetString(11)) ?? new List<UiSubjectTagMatchedPrompt>();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Stored subject tag '{reader.GetString(3)}' has malformed matched_prompts_json.", ex);
            }
            return new UiSubjectTagRecord
            {
                Id = reader.GetString(0),
                IdentityKey = reader.GetString(1),
                ActorDisplay = reader.GetString(2),
                Name = reader.GetString(3),
                NormalizedName = reader.GetString(4),
                Kind = reader.GetString(5),
                SearchQuery = reader.GetString(6),
                Model = reader.GetString(7),
                Description = reader.GetString(8),
                SourceSummary = reader.GetString(9),
                MatchedPromptsCount = reader.GetInt32(10),
                MatchedPrompts = matchedPrompts,
                RawResponse = reader.GetString(12),
                CreatedAtUnixMs = reader.GetInt64(13),
                UpdatedAtUnixMs = reader.GetInt64(14),
            };
        }

        private static List<string> DeserializeList(string json)
            => JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

        public void ReserveLoginNames(IEnumerable<string> loginNames)
            => ReserveAliases(loginNames.Select(login => (Owner: login, Alias: login)));

        public void ReserveAliases(string ownerLogin, IEnumerable<string> aliases)
            => ReserveAliases(aliases.Select(alias => (Owner: ownerLogin, Alias: alias)));

        public void TransferLogin(string from, string to)
        {
            from = from.Trim();
            to = to.Trim();
            if (from.Length == 0 || to.Length == 0)
                throw new InvalidDataException("A login transfer requires the exact previous login and the exact new login.");
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return;
            var fromKey = "login:" + from;
            var toKey = "login:" + to;
            var publicId = PublicIdentityId(to);
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                FailIfLoginExists(connection, transaction, "ui_profiles", "login", to);
                FailIfLoginExists(connection, transaction, "ui_generator_preferences", "login", to);
                FailIfValueExists(connection, transaction, "ui_creator_presence", "identity_key", toKey);
                FailIfValueExists(connection, transaction, "ui_subject_tags", "identity_key", toKey);
                Execute(connection, transaction,
                    "UPDATE ui_profiles SET login = $to, public_id = $publicId WHERE login = $from;",
                    ("$to", to), ("$publicId", publicId), ("$from", from));
                Execute(connection, transaction,
                    "UPDATE ui_profile_aliases SET owner_login = $to WHERE owner_login = $from;",
                    ("$to", to), ("$from", from));
                Execute(connection, transaction,
                    "UPDATE ui_generator_preferences SET login = $to WHERE login = $from;",
                    ("$to", to), ("$from", from));
                Execute(connection, transaction,
                    "UPDATE ui_claude_prompt_exchanges SET identity_key = $toKey WHERE identity_key = $fromKey;",
                    ("$toKey", toKey), ("$fromKey", fromKey));
                Execute(connection, transaction,
                    "UPDATE ui_subject_tags SET identity_key = $toKey WHERE identity_key = $fromKey;",
                    ("$toKey", toKey), ("$fromKey", fromKey));
                Execute(connection, transaction,
                    "UPDATE ui_creator_presence SET identity_key = $toKey WHERE identity_key = $fromKey;",
                    ("$toKey", toKey), ("$fromKey", fromKey));
                Execute(connection, transaction,
                    "UPDATE ui_activity SET actor_login = $to WHERE actor_login = $from;",
                    ("$to", to), ("$from", from));
                Execute(connection, transaction,
                    "UPDATE ui_activity SET target_login = $to WHERE target_login = $from;",
                    ("$to", to), ("$from", from));
                Execute(connection, transaction,
                    "UPDATE ui_user_requests SET submitter_login = $to WHERE submitter_login = $from;",
                    ("$to", to), ("$from", from));
                ReserveAlias(connection, transaction, from, to);
                ReserveAlias(connection, transaction, to, to);
                using (var revision = connection.CreateCommand())
                {
                    revision.Transaction = transaction;
                    revision.CommandText = "UPDATE ui_profile_meta SET revision = revision + 1 WHERE singleton = 1;";
                    revision.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        private void ReserveAliases(IEnumerable<(string Owner, string Alias)> reservations)
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                foreach (var reservation in reservations)
                {
                    var owner = reservation.Owner.Trim();
                    var alias = reservation.Alias.Trim();
                    if (owner.Length == 0 || alias.Length == 0)
                    {
                        throw new InvalidDataException("A reserved alias and its owner cannot be blank.");
                    }
                    ReserveAlias(connection, transaction, alias, owner);
                }
                transaction.Commit();
            }
        }

        public UiProfileRecord SetProfileName(
            string login,
            string displayName,
            IEnumerable<string> historicalAliases,
            long updatedAtUnixMs)
        {
            login = login.Trim();
            displayName = displayName.Trim();
            if (login.Length == 0 || displayName.Length == 0)
            {
                throw new InvalidDataException("A profile login and display name are required.");
            }

            lock (_sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                var aliases = historicalAliases
                    .Append(login)
                    .Append(displayName)
                    .Select(alias => alias.Trim())
                    .Where(alias => alias.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var alias in aliases)
                {
                    ReserveAlias(connection, transaction, alias, login);
                }

                var publicId = PublicIdentityId(login);
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO ui_profiles(login, public_id, display_name, updated_at_unix_ms)
                        VALUES($login, $publicId, $display, $updated)
                        ON CONFLICT(login) DO UPDATE SET
                            display_name = excluded.display_name,
                            updated_at_unix_ms = excluded.updated_at_unix_ms;
                        """;
                    command.Parameters.AddWithValue("$login", login);
                    command.Parameters.AddWithValue("$publicId", publicId);
                    command.Parameters.AddWithValue("$display", displayName);
                    command.Parameters.AddWithValue("$updated", updatedAtUnixMs);
                    command.ExecuteNonQuery();
                }
                using (var revision = connection.CreateCommand())
                {
                    revision.Transaction = transaction;
                    revision.CommandText =
                        "UPDATE ui_profile_meta SET revision = revision + 1 WHERE singleton = 1;";
                    revision.ExecuteNonQuery();
                }
                transaction.Commit();
                return new UiProfileRecord
                {
                    Login = login,
                    PublicId = publicId,
                    DisplayName = displayName,
                    UpdatedAtUnixMs = updatedAtUnixMs,
                };
            }
        }

        public UiProfileSnapshot SnapshotProfiles()
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                long revision;
                using (var version = connection.CreateCommand())
                {
                    version.CommandText =
                        "SELECT revision FROM ui_profile_meta WHERE singleton = 1;";
                    revision = Convert.ToInt64(version.ExecuteScalar());
                }
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT login, public_id, display_name, updated_at_unix_ms
                    FROM ui_profiles
                    ORDER BY display_name COLLATE NOCASE, login COLLATE NOCASE;
                    """;
                var profiles = new List<UiProfileRecord>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    profiles.Add(new UiProfileRecord
                    {
                        Login = reader.GetString(0),
                        PublicId = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        UpdatedAtUnixMs = reader.GetInt64(3),
                    });
                }
                return new UiProfileSnapshot
                {
                    Version = revision,
                    Profiles = profiles,
                };
            }
        }

        public bool IsDisplayNameAvailable(string login, string displayName)
        {
            login = login.Trim();
            displayName = displayName.Trim();
            if (displayName.Length == 0)
            {
                return false;
            }
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT owner_login
                    FROM ui_profile_aliases
                    WHERE normalized_name = $normalized;
                    """;
                command.Parameters.AddWithValue("$normalized", NormalizeAlias(displayName));
                var owner = command.ExecuteScalar() as string;
                return owner == null
                    || string.Equals(owner, login, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string PublicIdentityId(string login)
        {
            if (string.IsNullOrWhiteSpace(login))
            {
                return "";
            }
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(login.Trim().ToUpperInvariant()));
            return Convert.ToHexString(hash).ToLowerInvariant()[..20];
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

        private static void EnsureColumn(
            SqliteConnection connection,
            string table,
            string column,
            string declaration)
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = $"PRAGMA table_info({table});";
                using var reader = check.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
            alter.ExecuteNonQuery();
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

        private static void FailIfLoginExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            string column,
            string value)
        {
            FailIfValueExists(connection, transaction, table, column, value);
        }

        private static void FailIfValueExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            string column,
            string value)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT 1 FROM {table} WHERE {column} = $value LIMIT 1;";
            command.Parameters.AddWithValue("$value", value);
            if (command.ExecuteScalar() != null)
                throw new InvalidDataException($"Stored data already uses the new login in {table}.");
        }

        private static void Execute(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            command.ExecuteNonQuery();
        }

        private static void ReserveAlias(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string displayName,
            string ownerLogin)
        {
            var normalized = NormalizeAlias(displayName);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT owner_login
                    FROM ui_profile_aliases
                    WHERE normalized_name = $normalized;
                    """;
                read.Parameters.AddWithValue("$normalized", normalized);
                var existingOwner = read.ExecuteScalar() as string;
                if (existingOwner != null
                    && !string.Equals(existingOwner, ownerLogin, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UiProfileNameConflictException(
                        $"The name '{displayName}' is reserved by another account.");
                }
                if (existingOwner != null)
                {
                    return;
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO ui_profile_aliases(normalized_name, display_name, owner_login)
                VALUES($normalized, $display, $owner);
                """;
            insert.Parameters.AddWithValue("$normalized", normalized);
            insert.Parameters.AddWithValue("$display", displayName);
            insert.Parameters.AddWithValue("$owner", ownerLogin);
            insert.ExecuteNonQuery();
        }

        private static string NormalizeAlias(string displayName)
        {
            var collapsed = System.Text.RegularExpressions.Regex.Replace(
                displayName.Trim(),
                @"\s+",
                " ");
            if (collapsed.Length == 0)
            {
                throw new InvalidDataException("A reserved profile name cannot be blank.");
            }
            return collapsed.ToUpperInvariant();
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
