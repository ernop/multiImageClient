#nullable enable
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class GenerationArchiveContext
    {
        public string Source { get; init; } = "";
        public string ExternalJobId { get; init; } = "";
        public string GeneratorKey { get; init; } = "";
    }

    /// Durable, best-effort SQLite audit archive. It records one attempt per
    /// generator invocation and one child row per provider call and saved
    /// asset. JSON payloads are also flattened into queryable JSON-path rows.
    /// Image/video bytes and credentials are deliberately never stored.
    public static class GenerationArchive
    {
        private static readonly object Sync = new();
        private static string _connectionString = "";
        private static string _databasePath = "";
        private static bool _enabled;
        private static bool _sqliteProviderInitialized;

        public static string DatabasePath => _databasePath;

        public static void Initialize(Settings settings)
        {
            if (!settings.EnableGenerationArchive)
            {
                _enabled = false;
                return;
            }

            var path = string.IsNullOrWhiteSpace(settings.GenerationArchiveDbPath)
                ? Path.Combine(settings.ImageDownloadBaseFolder, "generation-history.sqlite3")
                : settings.GenerationArchiveDbPath;
            path = Path.GetFullPath(path);

            lock (Sync)
            {
                if (!_sqliteProviderInitialized)
                {
                    var native = LoadSystemSqlite();
                    SQLitePCL.SQLite3Provider_dynamic_cdecl.Setup("sqlite3", native);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
                    _sqliteProviderInitialized = true;
                }
                if (_enabled && string.Equals(path, _databasePath, StringComparison.Ordinal))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                _databasePath = path;
                _connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                    ForeignKeys = true,
                    DefaultTimeout = 15,
                }.ToString();

                using var connection = OpenConnection();
                CreateSchema(connection);
                GenerationTrace.ProviderCallSink = SaveProviderCall;
                _enabled = true;
                Logger.Log($"Generation archive: {path}");
            }
        }

        public static async Task<TaskProcessResult> ExecuteAndSaveAsync(
            IImageGenerator generator,
            PromptDetails promptDetails,
            ImageManager imageManager,
            GenerationArchiveContext? context = null)
        {
            return await ExecuteCoreAsync(
                generator,
                promptDetails,
                async () =>
                {
                    var result = await generator.ProcessPromptAsync(generator, promptDetails);
                    await imageManager.ProcessAndSaveAsync(result, generator);
                    return result;
                },
                context);
        }

        public static async Task<TaskProcessResult> ExecuteAsync(
            IImageGenerator generator,
            PromptDetails promptDetails,
            GenerationArchiveContext? context = null)
        {
            return await ExecuteCoreAsync(
                generator,
                promptDetails,
                () => generator.ProcessPromptAsync(generator, promptDetails),
                context);
        }

        public static void RecordSyntheticResult(
            IImageGenerator generator,
            TaskProcessResult result,
            GenerationArchiveContext? context = null)
        {
            if (!_enabled)
            {
                return;
            }
            var attemptId = Guid.NewGuid().ToString("N");
            result.GenerationAttemptId = attemptId;
            var startedAtUtc = DateTime.UtcNow;
            TryArchive(() =>
            {
                InsertAttempt(attemptId, generator, result.PromptDetails, context, startedAtUtc);
                CompleteAttempt(attemptId, result, null, startedAtUtc);
            });
        }

        public static void MarkExternalJobInterrupted(string externalJobId)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(externalJobId))
            {
                return;
            }
            TryArchive(() =>
            {
                lock (Sync)
                {
                    using var connection = OpenConnection();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        UPDATE generation_attempts
                        SET finished_at_utc = $finished,
                            status = 'interrupted',
                            success = 0,
                            error_message = 'The UI process ended before this generation attempt completed.',
                            exception_type = 'ProcessInterrupted'
                        WHERE external_job_id = $job
                          AND status = 'running';
                        """;
                    command.Parameters.AddWithValue("$finished", ToDbTime(DateTime.UtcNow));
                    command.Parameters.AddWithValue("$job", externalJobId);
                    var updated = command.ExecuteNonQuery();
                    if (updated > 0)
                    {
                        Logger.Log(
                            $"Generation archive: marked {updated} interrupted attempt(s) for UI job {externalJobId}.");
                    }
                }
            });
        }

        private static async Task<TaskProcessResult> ExecuteCoreAsync(
            IImageGenerator generator,
            PromptDetails promptDetails,
            Func<Task<TaskProcessResult>> invoke,
            GenerationArchiveContext? context)
        {
            if (!_enabled)
            {
                return await invoke();
            }

            var attemptId = Guid.NewGuid().ToString("N");
            var startedAtUtc = DateTime.UtcNow;
            TryArchive(() => InsertAttempt(attemptId, generator, promptDetails, context, startedAtUtc));
            using var traceScope = GenerationTrace.BeginAttempt(attemptId);
            try
            {
                var result = await invoke();
                result.GenerationAttemptId = attemptId;
                TryArchive(() => CompleteAttempt(attemptId, result, null, startedAtUtc));
                return result;
            }
            catch (Exception ex)
            {
                TryArchive(() => CompleteAttempt(attemptId, null, ex, startedAtUtc));
                throw;
            }
        }

        private static void InsertAttempt(
            string attemptId,
            IImageGenerator generator,
            PromptDetails promptDetails,
            GenerationArchiveContext? context,
            DateTime startedAtUtc)
        {
            var generatorJson = SnapshotGenerator(generator);
            var promptJson = GenerationTrace.NormalizePayload(promptDetails);

            lock (Sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO generation_attempts (
                            id, external_job_id, source, generator_key,
                            generator_api_type, generator_description, cost_estimate,
                            started_at_utc, status, prompt, prompt_details_json,
                            generator_parameters_json)
                        VALUES (
                            $id, $job, $source, $key, $api, $description, $cost,
                            $started, 'running', $prompt, $promptJson, $generatorJson);
                        """;
                    command.Parameters.AddWithValue("$id", attemptId);
                    command.Parameters.AddWithValue("$job", context?.ExternalJobId ?? "");
                    command.Parameters.AddWithValue("$source", context?.Source ?? "");
                    command.Parameters.AddWithValue("$key", context?.GeneratorKey ?? "");
                    command.Parameters.AddWithValue("$api", generator.ApiType.ToString());
                    command.Parameters.AddWithValue("$description", Safe(() => generator.GetGeneratorSpecPart()));
                    command.Parameters.AddWithValue("$cost", generator.GetCost());
                    command.Parameters.AddWithValue("$started", ToDbTime(startedAtUtc));
                    command.Parameters.AddWithValue("$prompt", promptDetails.Prompt ?? "");
                    command.Parameters.AddWithValue("$promptJson", promptJson);
                    command.Parameters.AddWithValue("$generatorJson", generatorJson);
                    command.ExecuteNonQuery();
                }

                for (var i = 0; i < promptDetails.TransformationSteps.Count; i++)
                {
                    var step = promptDetails.TransformationSteps[i];
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO prompt_steps (
                            attempt_id, sequence, prompt, explanation,
                            transformation_type, metadata_json)
                        VALUES ($attempt, $sequence, $prompt, $explanation, $type, $metadata);
                        """;
                    command.Parameters.AddWithValue("$attempt", attemptId);
                    command.Parameters.AddWithValue("$sequence", i);
                    command.Parameters.AddWithValue("$prompt", step.Prompt ?? "");
                    command.Parameters.AddWithValue("$explanation", step.Explanation ?? "");
                    command.Parameters.AddWithValue("$type", step.TransformationType.ToString());
                    command.Parameters.AddWithValue(
                        "$metadata",
                        GenerationTrace.NormalizePayload(step.PromptReplacementMetadata));
                    command.ExecuteNonQuery();
                }

                InsertStructuredFields(connection, transaction, "attempt", attemptId, "prompt", promptJson);
                InsertStructuredFields(connection, transaction, "attempt", attemptId, "generator", generatorJson);
                InsertInputAssets(connection, transaction, attemptId, generatorJson);
                transaction.Commit();
            }
        }

        private static void CompleteAttempt(
            string attemptId,
            TaskProcessResult? result,
            Exception? exception,
            DateTime startedAtUtc)
        {
            var finishedAtUtc = DateTime.UtcNow;
            var resultJson = BuildResultJson(result, exception);
            lock (Sync)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        UPDATE generation_attempts
                        SET finished_at_utc = $finished,
                            status = $status,
                            success = $success,
                            error_message = $error,
                            exception_type = $exceptionType,
                            image_error_type = $imageError,
                            text_error_type = $textError,
                            create_ms = $createMs,
                            download_ms = $downloadMs,
                            wall_clock_ms = $wallMs,
                            final_prompt = $finalPrompt,
                            runtime_meta_json = $runtimeMeta,
                            result_json = $resultJson
                        WHERE id = $id;
                        """;
                    command.Parameters.AddWithValue("$finished", ToDbTime(finishedAtUtc));
                    command.Parameters.AddWithValue("$status", exception != null ? "exception" : result?.IsSuccess == true ? "succeeded" : "failed");
                    command.Parameters.AddWithValue("$success", result?.IsSuccess == true ? 1 : 0);
                    command.Parameters.AddWithValue("$error", GenerationTrace.NormalizePayload(exception?.Message ?? result?.ErrorMessage ?? "").Trim('"'));
                    command.Parameters.AddWithValue("$exceptionType", exception?.GetType().FullName ?? "");
                    command.Parameters.AddWithValue("$imageError", result?.GenericImageErrorType.ToString() ?? "");
                    command.Parameters.AddWithValue("$textError", result?.GenericTextErrorType.ToString() ?? "");
                    command.Parameters.AddWithValue("$createMs", result?.CreateTotalMs ?? 0L);
                    command.Parameters.AddWithValue("$downloadMs", result?.DownloadTotalMs ?? 0L);
                    command.Parameters.AddWithValue("$wallMs", (long)(finishedAtUtc - startedAtUtc).TotalMilliseconds);
                    command.Parameters.AddWithValue("$finalPrompt", result?.PromptDetails?.Prompt ?? "");
                    command.Parameters.AddWithValue(
                        "$runtimeMeta",
                        GenerationTrace.NormalizePayload(result?.PromptDetails?.RuntimeMeta));
                    command.Parameters.AddWithValue("$resultJson", resultJson);
                    command.Parameters.AddWithValue("$id", attemptId);
                    command.ExecuteNonQuery();
                }

                InsertStructuredFields(connection, transaction, "attempt", attemptId, "result", resultJson);
                if (result != null)
                {
                    InsertResultAssets(connection, transaction, attemptId, result);
                }
                transaction.Commit();
            }
        }

        private static void SaveProviderCall(ProviderCallTrace trace)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(trace.AttemptId))
            {
                return;
            }
            TryArchive(() =>
            {
                lock (Sync)
                {
                    using var connection = OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    int sequence;
                    using (var sequenceCommand = connection.CreateCommand())
                    {
                        sequenceCommand.Transaction = transaction;
                        sequenceCommand.CommandText =
                            "SELECT COALESCE(MAX(sequence), -1) + 1 FROM provider_calls WHERE attempt_id = $attempt;";
                        sequenceCommand.Parameters.AddWithValue("$attempt", trace.AttemptId);
                        sequence = Convert.ToInt32(sequenceCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            """
                            INSERT INTO provider_calls (
                                id, attempt_id, sequence, provider, transport, method,
                                endpoint, started_at_utc, finished_at_utc, duration_ms,
                                status_code, success, request_json, response_json,
                                metadata_json, error_type, error_message)
                            VALUES (
                                $id, $attempt, $sequence, $provider, $transport, $method,
                                $endpoint, $started, $finished, $duration,
                                $status, $success, $request, $response,
                                $metadata, $errorType, $errorMessage);
                            """;
                        command.Parameters.AddWithValue("$id", trace.Id);
                        command.Parameters.AddWithValue("$attempt", trace.AttemptId);
                        command.Parameters.AddWithValue("$sequence", sequence);
                        command.Parameters.AddWithValue("$provider", trace.Provider);
                        command.Parameters.AddWithValue("$transport", trace.Transport);
                        command.Parameters.AddWithValue("$method", trace.Method);
                        command.Parameters.AddWithValue("$endpoint", trace.Endpoint);
                        command.Parameters.AddWithValue("$started", ToDbTime(trace.StartedAtUtc));
                        command.Parameters.AddWithValue("$finished", ToDbTime(trace.FinishedAtUtc));
                        command.Parameters.AddWithValue("$duration", (long)(trace.FinishedAtUtc - trace.StartedAtUtc).TotalMilliseconds);
                        command.Parameters.AddWithValue("$status", (object?)trace.StatusCode ?? DBNull.Value);
                        command.Parameters.AddWithValue("$success", trace.Success ? 1 : 0);
                        command.Parameters.AddWithValue("$request", trace.RequestJson);
                        command.Parameters.AddWithValue("$response", trace.ResponseJson);
                        command.Parameters.AddWithValue("$metadata", trace.MetadataJson);
                        command.Parameters.AddWithValue("$errorType", trace.ErrorType);
                        command.Parameters.AddWithValue("$errorMessage", trace.ErrorMessage);
                        command.ExecuteNonQuery();
                    }

                    InsertStructuredFields(connection, transaction, "provider_call", trace.Id, "request", trace.RequestJson);
                    InsertStructuredFields(connection, transaction, "provider_call", trace.Id, "response", trace.ResponseJson);
                    InsertStructuredFields(connection, transaction, "provider_call", trace.Id, "metadata", trace.MetadataJson);
                    transaction.Commit();
                }
            });
        }

        private static void InsertResultAssets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string attemptId,
            TaskProcessResult result)
        {
            foreach (var imageEntry in result.GetSavedImagePaths())
            {
                foreach (var pathEntry in imageEntry.Value)
                {
                    InsertAsset(
                        connection,
                        transaction,
                        attemptId,
                        pathEntry.Key == SaveType.Raw ? "output" : "derived",
                        imageEntry.Key,
                        pathEntry.Key.ToString(),
                        pathEntry.Value,
                        pathEntry.Key == SaveType.Raw ? result.ContentType : "image/png",
                        "");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.GeneratedMediaPath))
            {
                InsertAsset(
                    connection,
                    transaction,
                    attemptId,
                    "generated-media",
                    0,
                    "GeneratedMedia",
                    result.GeneratedMediaPath,
                    result.GeneratedMediaContentType,
                    "");
            }
        }

        private static void InsertInputAssets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string attemptId,
            string generatorJson)
        {
            try
            {
                var token = JToken.Parse(generatorJson);
                IEnumerable<JToken> tokens = token is JContainer container
                    ? container.DescendantsAndSelf()
                    : new[] { token };
                foreach (var property in tokens.OfType<JProperty>())
                {
                    if (!property.Name.Contains("input", StringComparison.OrdinalIgnoreCase)
                        || property.Value.Type != JTokenType.String)
                    {
                        continue;
                    }
                    var path = property.Value.Value<string>() ?? "";
                    if (File.Exists(path))
                    {
                        InsertAsset(connection, transaction, attemptId, "input", 0, property.Name, path, "", "");
                    }
                }
            }
            catch
            {
                // Generator snapshot still exists even if input path discovery fails.
            }
        }

        private static void InsertAsset(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string attemptId,
            string kind,
            int index,
            string variant,
            string path,
            string? contentType,
            string remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            var fullPath = Path.GetFullPath(path);
            var exists = File.Exists(fullPath);
            long byteLength = exists ? new FileInfo(fullPath).Length : 0;
            var sha256 = exists ? HashFile(fullPath) : "";
            var (width, height) = exists ? ReadImageDimensions(fullPath) : (0, 0);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO assets (
                    attempt_id, kind, image_index, variant, local_path,
                    content_type, byte_length, sha256, width, height, remote_url)
                VALUES (
                    $attempt, $kind, $index, $variant, $path,
                    $contentType, $bytes, $sha256, $width, $height, $remoteUrl);
                """;
            command.Parameters.AddWithValue("$attempt", attemptId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$index", index);
            command.Parameters.AddWithValue("$variant", variant);
            command.Parameters.AddWithValue("$path", fullPath);
            command.Parameters.AddWithValue("$contentType", contentType ?? "");
            command.Parameters.AddWithValue("$bytes", byteLength);
            command.Parameters.AddWithValue("$sha256", sha256);
            command.Parameters.AddWithValue("$width", width);
            command.Parameters.AddWithValue("$height", height);
            command.Parameters.AddWithValue("$remoteUrl", remoteUrl);
            command.ExecuteNonQuery();
        }

        private static void InsertStructuredFields(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string ownerType,
            string ownerId,
            string scope,
            string json)
        {
            JToken token;
            try
            {
                token = JToken.Parse(json);
            }
            catch
            {
                token = JValue.CreateString(json);
            }

            foreach (var field in Flatten(token, "$"))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO structured_fields (
                        owner_type, owner_id, scope, json_path, value_type,
                        text_value, number_value, bool_value, is_redacted)
                    VALUES (
                        $ownerType, $ownerId, $scope, $path, $type,
                        $text, $number, $bool, $redacted);
                    """;
                command.Parameters.AddWithValue("$ownerType", ownerType);
                command.Parameters.AddWithValue("$ownerId", ownerId);
                command.Parameters.AddWithValue("$scope", scope);
                command.Parameters.AddWithValue("$path", field.Path);
                command.Parameters.AddWithValue("$type", field.Type);
                command.Parameters.AddWithValue("$text", (object?)field.Text ?? DBNull.Value);
                command.Parameters.AddWithValue("$number", (object?)field.Number ?? DBNull.Value);
                command.Parameters.AddWithValue("$bool", (object?)field.Bool ?? DBNull.Value);
                command.Parameters.AddWithValue("$redacted", field.Redacted ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        private static IEnumerable<FlatField> Flatten(JToken token, string path)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    foreach (var field in Flatten(property.Value, $"{path}.{property.Name}"))
                    {
                        yield return field;
                    }
                }
                yield break;
            }
            if (token is JArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var field in Flatten(array[i], $"{path}[{i}]"))
                    {
                        yield return field;
                    }
                }
                yield break;
            }

            var type = token.Type.ToString().ToLowerInvariant();
            string? text = null;
            double? number = null;
            int? boolean = null;
            if (token.Type == JTokenType.Boolean)
            {
                boolean = token.Value<bool>() ? 1 : 0;
            }
            else if (token.Type is JTokenType.Integer or JTokenType.Float)
            {
                number = token.Value<double>();
            }
            else if (token.Type is not (JTokenType.Null or JTokenType.Undefined))
            {
                text = token.ToString(Formatting.None);
                if (token.Type == JTokenType.String)
                {
                    text = token.Value<string>();
                }
            }

            yield return new FlatField(
                path,
                type,
                text,
                number,
                boolean,
                string.Equals(text, "[REDACTED]", StringComparison.Ordinal));
        }

        private static string SnapshotGenerator(IImageGenerator generator)
        {
            var values = new Dictionary<string, object?>
            {
                ["runtimeType"] = generator.GetType().FullName,
                ["apiType"] = generator.ApiType.ToString(),
                ["spec"] = Safe(() => generator.GetGeneratorSpecPart()),
                ["rightParts"] = SafeObject(() => generator.GetRightParts()),
                ["costEstimate"] = generator.GetCost(),
                ["configuredFields"] = SnapshotObject(generator, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)),
            };
            return GenerationTrace.NormalizePayload(values);
        }

        private static object? SnapshotObject(object? value, int depth, HashSet<object> seen)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (value is string or char or bool
                || type.IsPrimitive || type.IsEnum
                || value is decimal or DateTime or DateTimeOffset or Guid)
            {
                return value;
            }
            if (value is byte[] bytes)
            {
                return new
                {
                    redacted = "binary",
                    byteLength = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                };
            }
            if (depth >= 4)
            {
                return new { runtimeType = type.FullName };
            }
            if (!type.IsValueType && !seen.Add(value))
            {
                return new { runtimeType = type.FullName, circularReference = true };
            }
            if (value is IEnumerable enumerable)
            {
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    if (items.Count >= 100)
                    {
                        items.Add(new { truncated = true });
                        break;
                    }
                    items.Add(SnapshotObject(item, depth + 1, seen));
                }
                return items;
            }
            if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                return new { runtimeType = type.FullName, value = value.ToString() };
            }

            var fields = new Dictionary<string, object?>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || fields.ContainsKey(field.Name))
                    {
                        continue;
                    }
                    try
                    {
                        fields[field.Name] = SnapshotObject(field.GetValue(value), depth + 1, seen);
                    }
                    catch
                    {
                        fields[field.Name] = new { unreadable = true };
                    }
                }
            }
            return fields;
        }

        private static string BuildResultJson(TaskProcessResult? result, Exception? exception)
        {
            if (result == null)
            {
                return GenerationTrace.NormalizePayload(new
                {
                    success = false,
                    exceptionType = exception?.GetType().FullName,
                    error = exception?.Message,
                });
            }
            return GenerationTrace.NormalizePayload(new
            {
                result.IsSuccess,
                result.GenericImageErrorType,
                result.GenericTextErrorType,
                result.ErrorMessage,
                remoteUrl = result.Url,
                result.ContentType,
                result.ImageGenerator,
                result.ImageGeneratorDescription,
                result.TextGenerator,
                result.CreateTotalMs,
                result.DownloadTotalMs,
                result.GeneratedMediaPath,
                result.GeneratedMediaContentType,
                base64ImageCount = result.Base64ImageDatas?.Count() ?? 0,
                savedImagePaths = result.GetSavedImagePaths(),
                promptDetails = result.PromptDetails,
            });
        }

        private static SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
            command.ExecuteNonQuery();
            return connection;
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS generation_attempts (
                    id TEXT PRIMARY KEY,
                    external_job_id TEXT NOT NULL DEFAULT '',
                    source TEXT NOT NULL DEFAULT '',
                    generator_key TEXT NOT NULL DEFAULT '',
                    generator_api_type TEXT NOT NULL,
                    generator_description TEXT NOT NULL,
                    cost_estimate NUMERIC NOT NULL DEFAULT 0,
                    started_at_utc TEXT NOT NULL,
                    finished_at_utc TEXT,
                    status TEXT NOT NULL,
                    success INTEGER,
                    prompt TEXT NOT NULL,
                    final_prompt TEXT NOT NULL DEFAULT '',
                    prompt_details_json TEXT NOT NULL,
                    generator_parameters_json TEXT NOT NULL,
                    runtime_meta_json TEXT NOT NULL DEFAULT 'null',
                    result_json TEXT NOT NULL DEFAULT 'null',
                    error_message TEXT NOT NULL DEFAULT '',
                    exception_type TEXT NOT NULL DEFAULT '',
                    image_error_type TEXT NOT NULL DEFAULT '',
                    text_error_type TEXT NOT NULL DEFAULT '',
                    create_ms INTEGER NOT NULL DEFAULT 0,
                    download_ms INTEGER NOT NULL DEFAULT 0,
                    wall_clock_ms INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS prompt_steps (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    attempt_id TEXT NOT NULL REFERENCES generation_attempts(id) ON DELETE CASCADE,
                    sequence INTEGER NOT NULL,
                    prompt TEXT NOT NULL,
                    explanation TEXT NOT NULL,
                    transformation_type TEXT NOT NULL,
                    metadata_json TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS provider_calls (
                    id TEXT PRIMARY KEY,
                    attempt_id TEXT NOT NULL REFERENCES generation_attempts(id) ON DELETE CASCADE,
                    sequence INTEGER NOT NULL,
                    provider TEXT NOT NULL,
                    transport TEXT NOT NULL,
                    method TEXT NOT NULL,
                    endpoint TEXT NOT NULL,
                    started_at_utc TEXT NOT NULL,
                    finished_at_utc TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    status_code INTEGER,
                    success INTEGER NOT NULL,
                    request_json TEXT NOT NULL,
                    response_json TEXT NOT NULL,
                    metadata_json TEXT NOT NULL,
                    error_type TEXT NOT NULL,
                    error_message TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS assets (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    attempt_id TEXT NOT NULL REFERENCES generation_attempts(id) ON DELETE CASCADE,
                    kind TEXT NOT NULL,
                    image_index INTEGER NOT NULL,
                    variant TEXT NOT NULL,
                    local_path TEXT NOT NULL,
                    content_type TEXT NOT NULL,
                    byte_length INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    width INTEGER NOT NULL DEFAULT 0,
                    height INTEGER NOT NULL DEFAULT 0,
                    remote_url TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS structured_fields (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_type TEXT NOT NULL,
                    owner_id TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    json_path TEXT NOT NULL,
                    value_type TEXT NOT NULL,
                    text_value TEXT,
                    number_value REAL,
                    bool_value INTEGER,
                    is_redacted INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_attempts_started
                    ON generation_attempts(started_at_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_attempts_external_job
                    ON generation_attempts(external_job_id);
                CREATE INDEX IF NOT EXISTS ix_attempts_generator
                    ON generation_attempts(generator_api_type, success);
                CREATE INDEX IF NOT EXISTS ix_prompt_steps_attempt
                    ON prompt_steps(attempt_id, sequence);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_provider_calls_attempt_sequence
                    ON provider_calls(attempt_id, sequence);
                CREATE INDEX IF NOT EXISTS ix_assets_attempt
                    ON assets(attempt_id, image_index);
                CREATE INDEX IF NOT EXISTS ix_assets_path
                    ON assets(local_path);
                CREATE INDEX IF NOT EXISTS ix_fields_owner
                    ON structured_fields(owner_type, owner_id, scope);
                CREATE INDEX IF NOT EXISTS ix_fields_path
                    ON structured_fields(scope, json_path);

                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "assets", "width", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "assets", "height", "INTEGER NOT NULL DEFAULT 0");
        }

        private static string HashFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch
            {
                return "";
            }
        }

        private static (int Width, int Height) ReadImageDimensions(string path)
        {
            try
            {
                var info = Image.Identify(path);
                return info == null ? (0, 0) : (info.Width, info.Height);
            }
            catch
            {
                return (0, 0);
            }
        }

        private static void EnsureColumn(
            SqliteConnection connection,
            string table,
            string column,
            string declaration)
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            reader.Close();
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
            alter.ExecuteNonQuery();
        }

        private static NativeLibraryAdapter LoadSystemSqlite()
        {
            string[] candidates;
            if (OperatingSystem.IsWindows())
            {
                candidates = new[] { "winsqlite3.dll", "sqlite3.dll" };
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates = new[] { "libsqlite3.dylib", "/usr/lib/libsqlite3.dylib" };
            }
            else
            {
                candidates = new[] { "libsqlite3.so.0", "libsqlite3.so" };
            }

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                try
                {
                    return new NativeLibraryAdapter(candidate);
                }
                catch (Exception ex)
                {
                    failures.Add($"{candidate}: {ex.Message}");
                }
            }
            throw new DllNotFoundException(
                "Could not load the operating system SQLite library. "
                + string.Join("; ", failures));
        }

        private static string ToDbTime(DateTime value)
            => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static string Safe(Func<string> getter)
        {
            try { return getter() ?? ""; }
            catch (Exception ex) { return $"[unavailable: {ex.Message}]"; }
        }

        private static object? SafeObject(Func<object?> getter)
        {
            try { return getter(); }
            catch (Exception ex) { return new { unavailable = ex.Message }; }
        }

        private static void TryArchive(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Log($"Generation archive write failed: {ex.Message}");
            }
        }

        private sealed record FlatField(
            string Path,
            string Type,
            string? Text,
            double? Number,
            int? Bool,
            bool Redacted);

        private sealed class NativeLibraryAdapter : SQLitePCL.IGetFunctionPointer
        {
            private readonly IntPtr _library;

            public NativeLibraryAdapter(string name)
            {
                _library = NativeLibrary.Load(name);
            }

            public IntPtr GetFunctionPointer(string name)
                => NativeLibrary.TryGetExport(_library, name, out var address)
                    ? address
                    : IntPtr.Zero;
        }
    }
}
