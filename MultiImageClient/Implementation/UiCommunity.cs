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
                    """;
                command.ExecuteNonQuery();
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
                           updated_at_unix_ms
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
                        updated_at_unix_ms)
                    VALUES(
                        $login, $showImage, $showDescribe, $hidden, $selected,
                        $presets, $endpointConfigurations, $updated)
                    ON CONFLICT(login) DO UPDATE SET
                        show_image_section = excluded.show_image_section,
                        show_describe_section = excluded.show_describe_section,
                        hidden_generator_keys_json = excluded.hidden_generator_keys_json,
                        default_selected_keys_json = excluded.default_selected_keys_json,
                        presets_json = excluded.presets_json,
                        endpoint_configurations_json = excluded.endpoint_configurations_json,
                        updated_at_unix_ms = excluded.updated_at_unix_ms;
                    """;
                command.Parameters.AddWithValue("$login", login);
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
            int limit = 50)
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
                    ORDER BY requested_at_unix_ms DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$identity", identityKey);
                command.Parameters.AddWithValue("$limit", limit);
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

        private static List<string> DeserializeList(string json)
            => JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

        public void ReserveLoginNames(IEnumerable<string> loginNames)
            => ReserveAliases(loginNames.Select(login => (Owner: login, Alias: login)));

        public void ReserveAliases(string ownerLogin, IEnumerable<string> aliases)
            => ReserveAliases(aliases.Select(alias => (Owner: ownerLogin, Alias: alias)));

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
