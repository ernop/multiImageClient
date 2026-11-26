#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiImageClient
{
    public sealed record UiTelemetryEventInput(string EventName, int? Sequence, Dictionary<string, object?>? Data);

    public sealed record UiTelemetryBatchInput(string TabId, string Page, IReadOnlyList<UiTelemetryEventInput> Events);

    public sealed record UiTelemetryRecord(
        long AtUnixMs,
        string Day,
        string Actor,
        string Role,
        string TabId,
        string Page,
        string EventName,
        int? Sequence,
        Dictionary<string, object?>? Data);

    public sealed class UiTelemetryRateLimitException : Exception
    {
        public UiTelemetryRateLimitException(string message) : base(message) { }
    }

    // Respectful, bounded local usage telemetry for the web UI.
    // Appends validated structured action events to daily UTC JSONL files under
    // saves/UiTelemetry/ with zero in-memory event cache and strict rejection of
    // free-text, prompt, draft, credential, or network-identity fields.
    public sealed class UiTelemetryStore
    {
        public const int MaxEventsPerBatch = 64;
        public const int MaxRequestBytes = 32 * 1024;
        public const long MaxDailyFileBytes = 20L * 1024 * 1024;
        public const int RetentionDays = 90;
        public const int MaxEventsPerMinutePerActor = 240;
        public const int MaxDataEntries = 16;
        public const int MaxStringValueChars = 80;
        public const int MaxEventNameChars = 48;
        public const int MaxSummaryDays = 90;

        private const string FilePrefix = "telemetry-";
        private const string FileSuffix = ".jsonl";

        private static readonly HashSet<string> AllowedPages = new(StringComparer.Ordinal)
        {
            "composer", "goal", "archive", "admin", "people", "recap",
        };

        private static readonly Regex TabIdPattern = new(@"^[a-zA-Z0-9_-]{4,32}$", RegexOptions.Compiled);
        private static readonly Regex EventNamePattern = new(@"^[a-z][a-z0-9_.-]{0,47}$", RegexOptions.Compiled);
        private static readonly Regex DataKeyPattern = new(@"^[a-zA-Z0-9_]{1,32}$", RegexOptions.Compiled);

        // Guardrails so callers cannot accidentally store prompt text, drafts,
        // secrets, or network identifiers in telemetry metadata.
        private static readonly string[] ForbiddenKeyFragments =
        {
            "prompt", "text", "instruction", "description", "notes", "draft",
            "cookie", "password", "token", "secret", "email", "address",
            "url", "query", "content", "body", "header", "useragent", "ua",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        private readonly string _directory;
        private readonly long _maxDailyFileBytes;
        private readonly int _retentionDays;
        private readonly int _maxEventsPerMinutePerActor;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _sync = new();
        private readonly Dictionary<string, Queue<long>> _actorWindows = new(StringComparer.Ordinal);
        private string _lastPrunedDay = "";

        public string DirectoryPath => _directory;

        public UiTelemetryStore(
            string directory,
            long maxDailyFileBytes = MaxDailyFileBytes,
            int retentionDays = RetentionDays,
            int maxEventsPerMinutePerActor = MaxEventsPerMinutePerActor,
            Func<DateTimeOffset>? clock = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Telemetry directory must not be empty.");
            if (maxDailyFileBytes <= 0)
                throw new InvalidDataException("Telemetry daily file limit must be positive.");
            if (retentionDays <= 0)
                throw new InvalidDataException("Telemetry retention days must be positive.");
            if (maxEventsPerMinutePerActor <= 0)
                throw new InvalidDataException("Telemetry per-minute limit must be positive.");

            _directory = directory;
            _maxDailyFileBytes = maxDailyFileBytes;
            _retentionDays = retentionDays;
            _maxEventsPerMinutePerActor = maxEventsPerMinutePerActor;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            Directory.CreateDirectory(_directory);
            SetOwnerOnlyDirectoryMode(_directory);
            PruneOldFilesLocked(_clock());
        }

        public static string ResolveDirectory(Settings settings)
            => Path.Combine(settings.ImageDownloadBaseFolder, "UiTelemetry");

        public static UiTelemetryBatchInput ParseBatchJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("Telemetry payload is empty.");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Telemetry payload must be a JSON object.");

            var tab = ReadRequiredString(root, "tab");
            if (!TabIdPattern.IsMatch(tab))
                throw new InvalidDataException("Telemetry tab identifier is invalid.");

            var page = ReadRequiredString(root, "page");
            if (!AllowedPages.Contains(page))
                throw new InvalidDataException($"Unknown telemetry page '{page}'.");

            if (!root.TryGetProperty("events", out var eventsEl) || eventsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Telemetry payload must include an events array.");

            var count = eventsEl.GetArrayLength();
            if (count == 0)
                throw new InvalidDataException("Telemetry batch must contain at least one event.");
            if (count > MaxEventsPerBatch)
                throw new InvalidDataException($"Telemetry batch exceeds maximum of {MaxEventsPerBatch} events.");

            var events = new List<UiTelemetryEventInput>(count);
            foreach (var item in eventsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Each telemetry event must be a JSON object.");
                var ev = ReadRequiredString(item, "ev");
                if (ev.Length > MaxEventNameChars || !EventNamePattern.IsMatch(ev))
                    throw new InvalidDataException($"Invalid telemetry event name '{ev}'.");

                int? seq = null;
                if (item.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind != JsonValueKind.Null)
                {
                    if (seqEl.ValueKind != JsonValueKind.Number || !seqEl.TryGetInt32(out var parsedSeq) || parsedSeq < 0)
                        throw new InvalidDataException("Telemetry seq must be a non-negative integer.");
                    seq = parsedSeq;
                }

                Dictionary<string, object?>? data = null;
                if (item.TryGetProperty("d", out var dataEl) && dataEl.ValueKind != JsonValueKind.Null)
                {
                    data = ParseDataObject(dataEl);
                }

                events.Add(new UiTelemetryEventInput(ev, seq, data));
            }

            return new UiTelemetryBatchInput(tab, page, events);
        }

        public int RecordBatch(string actor, string role, UiTelemetryBatchInput batch)
        {
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidDataException("Telemetry actor is required.");
            if (role is not ("admin" or "normal" or "local"))
                throw new InvalidDataException($"Invalid telemetry role '{role}'.");
            if (batch.Events.Count == 0)
                throw new InvalidDataException("Telemetry batch must contain at least one event.");
            if (batch.Events.Count > MaxEventsPerBatch)
                throw new InvalidDataException($"Telemetry batch exceeds maximum of {MaxEventsPerBatch} events.");

            var now = _clock();
            var nowMs = now.ToUnixTimeMilliseconds();
            var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(batch.Events.Count * 160);
            foreach (var item in batch.Events)
            {
                var lineObj = new Dictionary<string, object?>
                {
                    ["at"] = nowMs,
                    ["day"] = day,
                    ["actor"] = actor,
                    ["role"] = role,
                    ["tab"] = batch.TabId,
                    ["page"] = batch.Page,
                    ["ev"] = item.EventName,
                };
                if (item.Sequence.HasValue) lineObj["seq"] = item.Sequence.Value;
                if (item.Data is { Count: > 0 }) lineObj["d"] = item.Data;
                sb.Append(JsonSerializer.Serialize(lineObj, JsonOptions));
                sb.Append('\n');
            }
            var payloadBytes = Encoding.UTF8.GetBytes(sb.ToString());

            lock (_sync)
            {
                EnforceRateLimitLocked(actor, nowMs, batch.Events.Count);
                if (!string.Equals(_lastPrunedDay, day, StringComparison.Ordinal))
                {
                    PruneOldFilesLocked(now);
                    _lastPrunedDay = day;
                }

                var path = DailyFilePath(day);
                var existingBytes = File.Exists(path) ? new FileInfo(path).Length : 0L;
                if (existingBytes + payloadBytes.Length > _maxDailyFileBytes)
                {
                    throw new InvalidDataException("Daily telemetry file limit reached.");
                }

                var isNew = !File.Exists(path);
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(payloadBytes, 0, payloadBytes.Length);
                }
                if (isNew) SetOwnerOnlyFileMode(path);
            }

            return batch.Events.Count;
        }

        public object BuildSummary(int days = 14)
        {
            if (days < 1 || days > MaxSummaryDays)
                throw new InvalidDataException($"days must be between 1 and {MaxSummaryDays}.");

            var now = _clock().UtcDateTime.Date;
            var fromDate = now.AddDays(-(days - 1));
            var fromDay = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toDay = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            long totalEvents = 0;
            long totalBytes = 0;
            int filesRead = 0;
            var uniqueTabs = new HashSet<string>(StringComparer.Ordinal);
            var byEvent = new Dictionary<string, long>(StringComparer.Ordinal);
            var byPage = new Dictionary<string, long>(StringComparer.Ordinal);
            var byDay = new Dictionary<string, long>(StringComparer.Ordinal);
            var byActor = new Dictionary<string, (string Role, long Count, HashSet<string> Tabs)>(StringComparer.Ordinal);
            var actionBreakdown = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            var viewerDwells = new List<long>();
            long viewerItemsTotal = 0;

            for (var d = fromDate; d <= now; d = d.AddDays(1))
            {
                var dayKey = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var path = DailyFilePath(dayKey);
                if (!File.Exists(path)) continue;
                filesRead++;
                totalBytes += new FileInfo(path).Length;

                using var reader = new StreamReader(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                    Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var ev = root.TryGetProperty("ev", out var evEl) ? evEl.GetString() ?? "" : "";
                    var page = root.TryGetProperty("page", out var pageEl) ? pageEl.GetString() ?? "" : "";
                    var actor = root.TryGetProperty("actor", out var actorEl) ? actorEl.GetString() ?? "" : "";
                    var role = root.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? "" : "";
                    var tab = root.TryGetProperty("tab", out var tabEl) ? tabEl.GetString() ?? "" : "";

                    totalEvents++;
                    if (tab.Length > 0) uniqueTabs.Add(tab);
                    Increment(byEvent, ev);
                    Increment(byPage, page);
                    Increment(byDay, dayKey);
                    if (!byActor.TryGetValue(actor, out var actorStats))
                    {
                        actorStats = (role, 0, new HashSet<string>(StringComparer.Ordinal));
                    }
                    actorStats.Count++;
                    if (tab.Length > 0) actorStats.Tabs.Add(tab);
                    byActor[actor] = actorStats;

                    if (root.TryGetProperty("d", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                    {
                        if (ev is "composer.tool" or "composer.prompt_action" or "character.action"
                            or "feed.action" or "viewer.action" or "stage1.card_action"
                            or "goal.action" or "archive.action" or "settings.change")
                        {
                            var label = ReadOptionalString(dataEl, "action")
                                ?? ReadOptionalString(dataEl, "tool")
                                ?? ReadOptionalString(dataEl, "field");
                            if (!string.IsNullOrEmpty(label))
                            {
                                if (!actionBreakdown.TryGetValue(ev, out var bucket))
                                {
                                    bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                                    actionBreakdown[ev] = bucket;
                                }
                                Increment(bucket, label);
                            }
                        }
                        else if (ev == "stage1.mode")
                        {
                            var mode = ReadOptionalString(dataEl, "mode");
                            if (!string.IsNullOrEmpty(mode))
                            {
                                if (!actionBreakdown.TryGetValue(ev, out var bucket))
                                {
                                    bucket = new Dictionary<string, long>(StringComparer.Ordinal);
                                    actionBreakdown[ev] = bucket;
                                }
                                Increment(bucket, mode);
                            }
                        }
                        else if (ev == "viewer.close")
                        {
                            if (dataEl.TryGetProperty("dwellMs", out var dwellEl) && dwellEl.TryGetInt64(out var dwellMs) && dwellMs >= 0)
                            {
                                viewerDwells.Add(dwellMs);
                            }
                            if (dataEl.TryGetProperty("itemsViewed", out var itemsEl) && itemsEl.TryGetInt64(out var items) && items >= 0)
                            {
                                viewerItemsTotal += items;
                            }
                        }
                    }
                }
            }

            viewerDwells.Sort();
            long? medianViewerDwellMs = viewerDwells.Count > 0 ? viewerDwells[viewerDwells.Count / 2] : null;

            return new
            {
                days,
                fromDay,
                toDay,
                totalEvents,
                uniqueTabs = uniqueTabs.Count,
                filesCount = filesRead,
                totalBytes,
                byEvent = byEvent.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { ev = kv.Key, count = kv.Value }).ToList(),
                byPage = byPage.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { page = kv.Key, count = kv.Value }).ToList(),
                byActor = byActor.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { actor = kv.Key, role = kv.Value.Role, count = kv.Value.Count, tabs = kv.Value.Tabs.Count }).ToList(),
                byDay = byDay.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { day = kv.Key, count = kv.Value }).ToList(),
                actionBreakdown = actionBreakdown.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => new { name = x.Key, count = x.Value }).ToList(),
                    StringComparer.Ordinal),
                viewer = new
                {
                    sessions = viewerDwells.Count,
                    medianDwellMs = medianViewerDwellMs,
                    totalItemsViewed = viewerItemsTotal,
                },
            };
        }

        private string DailyFilePath(string day) => Path.Combine(_directory, $"{FilePrefix}{day}{FileSuffix}");

        private void EnforceRateLimitLocked(string actor, long nowMs, int incomingCount)
        {
            var cutoff = nowMs - 60_000L;
            if (!_actorWindows.TryGetValue(actor, out var window))
            {
                window = new Queue<long>();
                _actorWindows[actor] = window;
            }
            while (window.Count > 0 && window.Peek() < cutoff)
            {
                window.Dequeue();
            }
            if (window.Count + incomingCount > _maxEventsPerMinutePerActor)
            {
                throw new UiTelemetryRateLimitException("Telemetry rate limit exceeded for this account.");
            }
            for (var i = 0; i < incomingCount; i++)
            {
                window.Enqueue(nowMs);
            }
            if (_actorWindows.Count > 256)
            {
                foreach (var stale in _actorWindows.Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < cutoff).Select(kv => kv.Key).ToList())
                {
                    _actorWindows.Remove(stale);
                }
            }
        }

        private void PruneOldFilesLocked(DateTimeOffset now)
        {
            if (!Directory.Exists(_directory)) return;
            var cutoffDate = now.UtcDateTime.Date.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(_directory, $"{FilePrefix}*{FileSuffix}"))
            {
                var name = Path.GetFileName(path);
                if (name.Length != FilePrefix.Length + 10 + FileSuffix.Length) continue;
                var datePart = name.Substring(FilePrefix.Length, 10);
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate)
                    && fileDate < cutoffDate)
                {
                    File.Delete(path);
                }
            }
        }

        private static Dictionary<string, object?> ParseDataObject(JsonElement dataEl)
        {
            if (dataEl.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Telemetry event data 'd' must be a JSON object.");

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in dataEl.EnumerateObject())
            {
                if (result.Count >= MaxDataEntries)
                    throw new InvalidDataException($"Telemetry event data exceeds {MaxDataEntries} fields.");
                if (!DataKeyPattern.IsMatch(prop.Name))
                    throw new InvalidDataException($"Invalid telemetry data key '{prop.Name}'.");
                if (IsForbiddenKey(prop.Name))
                    throw new InvalidDataException($"Telemetry data key '{prop.Name}' is not permitted.");

                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => ParseFiniteNumber(prop.Name, prop.Value),
                    JsonValueKind.String => ParseBoundedString(prop.Name, prop.Value.GetString() ?? ""),
                    _ => throw new InvalidDataException($"Telemetry field '{prop.Name}' must be a scalar value."),
                };
            }
            return result;
        }

        private static bool IsForbiddenKey(string key)
        {
            var lower = key.ToLowerInvariant();
            if (lower == "ip") return true;
            foreach (var fragment in ForbiddenKeyFragments)
            {
                if (lower.Contains(fragment, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static object ParseFiniteNumber(string key, JsonElement el)
        {
            if (el.TryGetInt64(out var i64)) return i64;
            var d = el.GetDouble();
            if (!double.IsFinite(d))
                throw new InvalidDataException($"Telemetry field '{key}' must be a finite number.");
            return d;
        }

        private static string ParseBoundedString(string key, string value)
        {
            if (value.Length > MaxStringValueChars)
                throw new InvalidDataException($"Telemetry field '{key}' exceeds {MaxStringValueChars} characters.");
            foreach (var ch in value)
            {
                if (ch < ' ' && ch != '\t')
                    throw new InvalidDataException($"Telemetry field '{key}' contains control characters.");
            }
            return value;
        }

        private static string ReadRequiredString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Telemetry field '{name}' is required.");
            var val = el.GetString() ?? "";
            if (val.Length == 0)
                throw new InvalidDataException($"Telemetry field '{name}' must not be empty.");
            return val;
        }

        private static string? ReadOptionalString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private static void Increment(Dictionary<string, long> map, string key)
            => map[key] = map.TryGetValue(key, out var existing) ? existing + 1 : 1;

        private static void SetOwnerOnlyDirectoryMode(string path)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static void SetOwnerOnlyFileMode(string path)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
