#nullable enable
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class PromptSubjectSynthesisResult
    {
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Description { get; init; } = "";
        public string SourceSummary { get; init; } = "";
        public string Model { get; init; } = "";
        public string RawResponse { get; init; } = "";
        public List<string> SearchTerms { get; init; } = new();
        public List<UiSubjectTagMatchedPrompt> MatchedPrompts { get; init; } = new();
    }

    // Whose prompts a character search may read. A shared site searches only
    // the signed-in account's own jobs and Claude edits; the generation
    // archive and server prompt files carry no account identity, so only the
    // auth-off local instance (one owner) reads them.
    public sealed class PromptSubjectSearchScope
    {
        public required string CreatorLogin { get; init; }
        public required string ClaudeExchangeIdentityKey { get; init; }
        public required bool IncludeLocalSources { get; init; }

        public static PromptSubjectSearchScope ForAccount(string login) => new()
        {
            CreatorLogin = login,
            ClaudeExchangeIdentityKey = "login:" + login,
            IncludeLocalSources = false,
        };

        public static readonly PromptSubjectSearchScope LocalOwner = new()
        {
            CreatorLogin = "",
            ClaudeExchangeIdentityKey = "",
            IncludeLocalSources = true,
        };
    }

    public static class PromptSubjectSynthesis
    {
        public const string DefaultModelKey = ManagerCatalog.KeyClaudeOpus55;
        public const int MaxMatchedPrompts = 40;
        public const int MaxPromptCorpusChars = 48000;
        public const int MaxSinglePromptChars = 3000;
        public const int MaxNotesChars = 2000;

        private static readonly ConcurrentDictionary<string, byte> RunningSyntheses = new(StringComparer.Ordinal);

        public const string SynthesisSystemPrompt =
            """
            You write a reusable visual description of one recurring character, place, or thing, using the user's own past image-generation prompts and notes as evidence.

            Return ONLY one JSON object with exactly these keys (no markdown fences, no text outside the JSON):
            {
              "kind": "character" | "location" | "object" | "general",
              "description": "<the reusable visual description>",
              "summary": "<1-2 sentences: what evidence you used, and any conflicts between prompts>"
            }

            The application inserts the description directly after the name, in parentheses: "<Name> (<description>)". So the description must:
            1. Be a noun phrase that reads naturally inside those parentheses. Do not start with the name and do not repeat it. Example for a character: "a woman in her early 30s with shoulder-length straight black hair, warm brown eyes, round silver glasses, and a navy linen jacket".
            2. Describe only enduring visual traits an image model can draw: apparent age, build, face, hair, eyes, skin, signature clothing or accessories for characters and animals; layout, architecture, materials, surroundings, and landmarks for places; shape, material, color, size, and markings for things.
            3. Leave out scene-specific actions, poses, camera directions, art styles, and one-off outfits. When prompts disagree, prefer the traits that recur most, and mention the conflict in "summary".
            4. Never invent traits that the prompts and notes do not support.
            5. Stay between 25 and 90 words, in plain concrete language.
            6. Favor clear, bright, readable visual details and natural balanced color. Do not add dim, murky, gloomy, or dark atmosphere unless the evidence defines the subject that way.
            """;

        public static object BuildCatalogPayload(Settings settings)
        {
            return new
            {
                defaultModel = DefaultModelKey,
                maxNameChars = UiCommunityStore.MaxSubjectTagNameChars,
                maxQueryChars = UiCommunityStore.MaxSubjectTagQueryChars,
                maxDescriptionChars = UiCommunityStore.MaxSubjectTagDescriptionChars,
                maxNotesChars = MaxNotesChars,
                models = ManagerCatalog.All.Select(entry => new
                {
                    key = entry.Key,
                    label = entry.Label,
                    availabilityProblem = ManagerCatalog.DescribeAvailabilityProblem(entry, settings),
                }).ToList(),
            };
        }

        public static ManagerDefinition ResolveModelDefinition(string? modelKey)
        {
            var key = (modelKey ?? "").Trim();
            return ManagerCatalog.All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"Unknown writer model '{modelKey}'. Known models: {string.Join(", ", ManagerCatalog.All.Select(m => m.Key))}");
        }

        // One synthesis at a time per account, so extra tabs or repeated
        // clicks cannot multiply provider calls. Dispose the result to release.
        public static IDisposable? TryBeginSynthesis(string identityKey)
        {
            return RunningSyntheses.TryAdd(identityKey, 0) ? new SynthesisLease(identityKey) : null;
        }

        private sealed class SynthesisLease : IDisposable
        {
            private string? _identityKey;
            public SynthesisLease(string identityKey) => _identityKey = identityKey;
            public void Dispose()
            {
                var key = Interlocked.Exchange(ref _identityKey, null);
                if (key != null)
                {
                    RunningSyntheses.TryRemove(key, out _);
                }
            }
        }

        public static List<string> ExtractSearchTerms(string name, string? searchQuery)
        {
            var (displayName, _) = UiCommunityStore.NormalizeSubjectTagName(name);
            var terms = new List<string> { displayName };
            searchQuery ??= "";
            if (searchQuery.Length > UiCommunityStore.MaxSubjectTagQueryChars)
            {
                throw new InvalidDataException(
                    $"Search terms exceed the {UiCommunityStore.MaxSubjectTagQueryChars}-character limit.");
            }
            foreach (var rawPart in searchQuery.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var cleaned = Regex.Replace(rawPart.Trim(), @"\s+", " ");
                if (cleaned.Length >= 2 && !terms.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    terms.Add(cleaned);
                }
            }
            return terms;
        }

        public static List<UiSubjectTagMatchedPrompt> SearchAllHistory(
            Settings settings,
            string name,
            string? searchQuery,
            UiJobRegistry? jobs,
            UiCommunityStore community,
            UiVisibilityStore? visibility,
            PromptSubjectSearchScope scope)
        {
            var terms = ExtractSearchTerms(name, searchQuery);
            var perSourceLimit = MaxMatchedPrompts * 2;
            var rawCandidates = new List<UiSubjectTagMatchedPrompt>();

            if (jobs != null)
            {
                rawCandidates.AddRange(jobs.SearchPrompts(terms, visibility, scope.CreatorLogin, perSourceLimit));
            }
            else
            {
                if (!scope.IncludeLocalSources)
                {
                    throw new InvalidOperationException("A shared-site character search requires the live job registry.");
                }
                rawCandidates.AddRange(SearchUiHistoryOnDisk(settings, terms, visibility, perSourceLimit));
            }
            rawCandidates.AddRange(community.SearchClaudePromptExchanges(scope.ClaudeExchangeIdentityKey, terms, perSourceLimit));
            if (scope.IncludeLocalSources)
            {
                rawCandidates.AddRange(SearchGenerationArchive(settings, terms, visibility, perSourceLimit));
                rawCandidates.AddRange(SearchPromptFiles(settings, terms, perSourceLimit));
            }

            // A name found only inside a longer word ("Jing" in "Beijing")
            // is not evidence about the subject.
            var wholeWordPatterns = terms.Select(term => new Regex(
                $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();

            var seenPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scored = new List<(UiSubjectTagMatchedPrompt Item, int Score)>();
            foreach (var candidate in rawCandidates)
            {
                var trimmed = StripAutomaticSuffixes(candidate.Prompt);
                if (trimmed.Length == 0)
                {
                    continue;
                }
                var fingerprint = Regex.Replace(trimmed, @"\s+", " ");
                if (!seenPrompts.Add(fingerprint))
                {
                    continue;
                }
                var score = wholeWordPatterns[0].IsMatch(trimmed) ? 100 : 0;
                for (var i = 1; i < wholeWordPatterns.Count; i++)
                {
                    if (wholeWordPatterns[i].IsMatch(trimmed))
                    {
                        score += 15;
                    }
                }
                if (score == 0)
                {
                    continue;
                }
                scored.Add((new UiSubjectTagMatchedPrompt
                {
                    Source = candidate.Source,
                    RecordId = candidate.RecordId,
                    CreatedAtUtc = candidate.CreatedAtUtc,
                    Prompt = trimmed.Length > MaxSinglePromptChars ? trimmed.Substring(0, MaxSinglePromptChars) : trimmed,
                }, score));
            }

            return scored
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Item.CreatedAtUtc, StringComparer.Ordinal)
                .Take(MaxMatchedPrompts)
                .Select(entry => entry.Item)
                .ToList();
        }

        public static async Task<PromptSubjectSynthesisResult> SynthesizeSubjectAsync(
            Settings settings,
            string name,
            string kind,
            string? searchQuery,
            string modelKey,
            IReadOnlyList<UiSubjectTagMatchedPrompt> matchedPrompts,
            string? notes,
            IManagerChatClient? clientOverride = null,
            CancellationToken ct = default)
        {
            var (displayName, _) = UiCommunityStore.NormalizeSubjectTagName(name);
            var normalizedKind = UiCommunityStore.NormalizeSubjectKind(kind);
            var terms = ExtractSearchTerms(displayName, searchQuery);
            notes = (notes ?? "").Trim();
            if (notes.Length > MaxNotesChars)
            {
                throw new InvalidDataException($"Notes exceed the {MaxNotesChars}-character limit.");
            }
            if (matchedPrompts.Count == 0 && notes.Length == 0)
            {
                throw new InvalidDataException(
                    $"None of your past prompts mention {displayName}"
                    + (terms.Count > 1 ? $" or {string.Join(", ", terms.Skip(1))}" : "")
                    + ". Add other names it went by, write a few notes, or write the description yourself.");
            }

            var definition = ResolveModelDefinition(modelKey);
            IManagerChatClient client;
            if (clientOverride != null)
            {
                client = clientOverride;
            }
            else
            {
                var problem = ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
                if (problem != null)
                {
                    throw new InvalidOperationException($"{definition.Label} is not available: {problem}");
                }
                client = ManagerCatalog.Build(definition, settings);
            }

            var userMessage = BuildSynthesisUserMessage(displayName, normalizedKind, terms, matchedPrompts, notes);
            var reply = await client.CompleteAsync(
                SynthesisSystemPrompt,
                new[] { new ManagerChatMessage { Role = "user", Text = userMessage } },
                ct);
            if (!string.IsNullOrWhiteSpace(reply.ProviderStop))
            {
                throw new InvalidOperationException($"{definition.Label} stopped early ({reply.ProviderStop}).");
            }

            var parsed = ParseSynthesisReply(reply.Text);
            return new PromptSubjectSynthesisResult
            {
                Name = displayName,
                // The user's chosen kind is the record's kind; the reply's kind
                // is still validated so a malformed reply fails.
                Kind = normalizedKind,
                Description = parsed.Description,
                SourceSummary = parsed.Summary,
                Model = definition.Key,
                RawResponse = reply.RawResponse,
                SearchTerms = terms,
                MatchedPrompts = matchedPrompts.ToList(),
            };
        }

        public static (string Kind, string Description, string Summary) ParseSynthesisReply(string? rawText)
        {
            var stripped = UiGoalLoopProtocol.StripMarkdownFence(rawText ?? "").Trim();
            if (stripped.Length == 0)
            {
                throw new InvalidDataException("The writer model returned an empty reply.");
            }
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(stripped);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The writer model did not return valid JSON: {ex.Message}", ex);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The writer model's reply must be a JSON object.");
                }
                var known = new HashSet<string>(StringComparer.Ordinal) { "kind", "description", "summary" };
                foreach (var property in root.EnumerateObject())
                {
                    if (!known.Contains(property.Name))
                    {
                        throw new InvalidDataException($"The writer model's reply has an unexpected field '{property.Name}'.");
                    }
                }
                var kind = UiCommunityStore.NormalizeSubjectKind(RequireString(root, "kind"));
                var description = RequireString(root, "description");
                if (description.Length > UiCommunityStore.MaxSubjectTagDescriptionChars)
                {
                    throw new InvalidDataException(
                        $"The written description exceeds {UiCommunityStore.MaxSubjectTagDescriptionChars} characters.");
                }
                var summary = RequireString(root, "summary");
                if (summary.Length > UiCommunityStore.MaxSubjectTagSummaryChars)
                {
                    throw new InvalidDataException(
                        $"The written summary exceeds {UiCommunityStore.MaxSubjectTagSummaryChars} characters.");
                }
                return (kind, description, summary);
            }
        }

        private static string RequireString(JsonElement root, string field)
        {
            if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"The writer model's reply is missing the string field '{field}'.");
            }
            var value = (element.GetString() ?? "").Trim();
            if (value.Length == 0)
            {
                throw new InvalidDataException($"The writer model's reply has an empty '{field}'.");
            }
            return value;
        }

        public static string BuildSynthesisUserMessage(
            string name,
            string kind,
            IReadOnlyList<string> searchTerms,
            IReadOnlyList<UiSubjectTagMatchedPrompt> matchedPrompts,
            string notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {name}");
            sb.AppendLine($"Kind: {kind}");
            if (searchTerms.Count > 1)
            {
                sb.AppendLine($"Other names it went by: {string.Join(", ", searchTerms.Skip(1))}");
            }
            if (notes.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("The user's notes (treat as reliable evidence):");
                sb.AppendLine(notes);
            }
            sb.AppendLine();
            sb.AppendLine($"Past prompts that mention it ({matchedPrompts.Count}):");

            var budget = MaxPromptCorpusChars;
            for (var i = 0; i < matchedPrompts.Count; i++)
            {
                var block = $"\n--- [{i + 1}] {matchedPrompts[i].CreatedAtUtc} ---\n{matchedPrompts[i].Prompt}\n";
                if (block.Length > budget && i > 0)
                {
                    break;
                }
                sb.Append(block);
                budget -= block.Length;
            }
            return sb.ToString();
        }

        private static string StripAutomaticSuffixes(string prompt)
        {
            var text = (prompt ?? "").Trim();
            foreach (var marker in new[] { "\n\nComposition sketch legend:", "\n\nLayout template regions:" })
            {
                var index = text.IndexOf(marker, StringComparison.Ordinal);
                if (index > 0)
                {
                    text = text.Substring(0, index).Trim();
                }
            }
            return text;
        }

        private static List<UiSubjectTagMatchedPrompt> SearchGenerationArchive(
            Settings settings,
            IReadOnlyList<string> terms,
            UiVisibilityStore? visibility,
            int limit)
        {
            var results = new List<UiSubjectTagMatchedPrompt>();
            var dbPath = !string.IsNullOrWhiteSpace(GenerationArchive.DatabasePath)
                ? GenerationArchive.DatabasePath
                : !string.IsNullOrWhiteSpace(settings.GenerationArchiveDbPath)
                    ? settings.GenerationArchiveDbPath
                    : Path.Combine(settings.ImageDownloadBaseFolder, "generation-history.sqlite3");
            if (!settings.EnableGenerationArchive || !File.Exists(dbPath))
            {
                return results;
            }

            GenerationArchive.EnsureSqliteAvailable();
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(dbPath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 10,
            }.ToString();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var cmd = connection.CreateCommand();
            var clauses = new List<string>();
            for (var i = 0; i < terms.Count; i++)
            {
                var p = $"$term{i}";
                clauses.Add($"(INSTR(UPPER(COALESCE(prompt, '')), {p}) > 0 OR INSTR(UPPER(COALESCE(final_prompt, '')), {p}) > 0)");
                cmd.Parameters.AddWithValue(p, terms[i].ToUpperInvariant());
            }
            cmd.CommandText =
                $"""
                SELECT id, COALESCE(external_job_id, ''), COALESCE(started_at_utc, ''),
                       COALESCE(prompt, ''), COALESCE(final_prompt, '')
                FROM generation_attempts
                WHERE {string.Join(" OR ", clauses)}
                ORDER BY id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var externalJobId = reader.GetString(1);
                if (externalJobId.Length > 0 && visibility != null && visibility.IsPromptHidden(externalJobId))
                {
                    continue;
                }
                var prompt = reader.GetString(3);
                var chosen = prompt.Length > 0 ? prompt : reader.GetString(4);
                if (chosen.Length > 0)
                {
                    results.Add(new UiSubjectTagMatchedPrompt
                    {
                        Source = "generation-archive",
                        RecordId = externalJobId.Length > 0 ? externalJobId : $"attempt-{reader.GetInt64(0)}",
                        CreatedAtUtc = reader.GetString(2),
                        Prompt = chosen,
                    });
                }
            }
            return results;
        }

        private static List<UiSubjectTagMatchedPrompt> SearchUiHistoryOnDisk(
            Settings settings,
            IReadOnlyList<string> terms,
            UiVisibilityStore? visibility,
            int limit)
        {
            var results = new List<UiSubjectTagMatchedPrompt>();
            var root = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            if (!Directory.Exists(root))
            {
                return results;
            }
            foreach (var entry in UiJobStorage.ScanIndex(root).OrderByDescending(e => e.CreatedAt))
            {
                if (results.Count >= limit)
                {
                    break;
                }
                if (visibility != null && visibility.IsPromptHidden(entry.Id))
                {
                    continue;
                }
                var prompt = UiJobStorage.ReadJobPromptIfPresent(root, entry.FolderName);
                if (string.IsNullOrWhiteSpace(prompt)
                    || !terms.Any(t => prompt.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                results.Add(new UiSubjectTagMatchedPrompt
                {
                    Source = "ui-history",
                    RecordId = entry.Id,
                    CreatedAtUtc = UiJobStorage.EnsureUtc(entry.CreatedAt).ToString("O"),
                    Prompt = prompt,
                });
            }
            return results;
        }

        private static List<UiSubjectTagMatchedPrompt> SearchPromptFiles(
            Settings settings,
            IReadOnlyList<string> terms,
            int limit)
        {
            var results = new List<UiSubjectTagMatchedPrompt>();
            var files = new List<string>();
            if (settings.PromptFiles != null)
            {
                files.AddRange(settings.PromptFiles.Where(f => !string.IsNullOrWhiteSpace(f)));
            }
            if (!string.IsNullOrWhiteSpace(settings.TypedPromptsAppendFile))
            {
                files.Add(settings.TypedPromptsAppendFile);
            }
            foreach (var rawPath in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var expanded = Settings.ExpandPath(rawPath);
                if (!File.Exists(expanded))
                {
                    continue;
                }
                var fileName = Path.GetFileName(expanded);
                var lines = File.ReadAllLines(expanded);
                for (var i = lines.Length - 1; i >= 0 && results.Count < limit; i--)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith('#')
                        || !terms.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    results.Add(new UiSubjectTagMatchedPrompt
                    {
                        Source = $"file:{fileName}",
                        RecordId = $"{fileName}:{i + 1}",
                        CreatedAtUtc = "",
                        Prompt = line,
                    });
                }
            }
            return results;
        }
    }
}
