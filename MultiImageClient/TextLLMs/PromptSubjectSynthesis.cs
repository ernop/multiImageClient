#nullable enable
using Microsoft.Data.Sqlite;
using System;
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
        public string Kind { get; init; } = "character";
        public string Description { get; init; } = "";
        public string SourceSummary { get; init; } = "";
        public string Model { get; init; } = "";
        public string RawResponse { get; init; } = "";
        public List<string> SearchTerms { get; init; } = new();
        public List<UiSubjectTagMatchedPrompt> MatchedPrompts { get; init; } = new();
    }

    public static class PromptSubjectSynthesis
    {
        public const string DefaultModel = "claude-opus-5-5";
        public const int MaxMatchedPrompts = 40;
        public const int MaxPromptCorpusChars = 48000;
        public const int MaxSinglePromptChars = 3000;
        public const int MaxNotesChars = 4000;

        public const string SynthesisSystemPrompt =
            """
            You synthesize a reusable visual description of a recurring person, character, location, or object from the user's historical image-generation prompts and personal notes.

            Return ONLY a single valid JSON object with these exact keys (no markdown fences, no commentary outside the JSON):
            {
              "kind": "character" | "location" | "object" | "general",
              "description": "<complete, self-contained visual description ready to be inserted directly into an image prompt>",
              "summary": "<concise 1-2 sentence summary of the historical prompt evidence and traits consolidated>"
            }

            Rules for "description":
            1. Write a concrete, self-contained visual description that an image generator can render directly when the subject's name is expanded in a future prompt.
            2. Begin with the subject's name and core identity (for example, "Jing (an East Asian woman in her early 30s with shoulder-length straight black hair, warm brown eyes, and a calm, observant expression...)").
            3. Consolidate consistent physical traits, apparent age, hair, eyes, facial features, stature, signature clothing or accessories (for characters), or architectural layout, materials, surroundings, geography, and Era/atmosphere (for locations) attested across the historical prompts.
            4. Do not invent unrelated backstory that is absent from the provided prompts and notes. When different prompts show the subject in varied outfits or activities, distinguish enduring traits from scene-specific details.
            5. Follow the Universal Image Prompt Defaults: favor clear, bright, full normal daytime clarity, readable visual details, and natural balanced color unless the subject itself is explicitly defined otherwise. Avoid murky, dim, shadow-choked, or gloomy filler.
            """;

        public static object BuildCatalogPayload(Settings settings)
        {
            var models = ManagerCatalog.All.Select(entry =>
            {
                var problem = ManagerCatalog.DescribeAvailabilityProblem(entry, settings);
                return new
                {
                    id = entry.Model,
                    key = entry.Key,
                    label = entry.Label,
                    provider = entry.Provider,
                    available = problem == null,
                    availabilityProblem = problem,
                };
            }).ToList();

            return new
            {
                defaultModel = DefaultModel,
                maxNameChars = UiCommunityStore.MaxSubjectTagNameChars,
                maxQueryChars = UiCommunityStore.MaxSubjectTagQueryChars,
                maxDescriptionChars = UiCommunityStore.MaxSubjectTagDescriptionChars,
                maxNotesChars = MaxNotesChars,
                validKinds = UiCommunityStore.ValidSubjectKinds,
                models,
            };
        }

        public static ManagerDefinition ResolveModelDefinition(string? requestedModel)
        {
            var token = string.IsNullOrWhiteSpace(requestedModel)
                ? DefaultModel
                : requestedModel.Trim();

            var match = ManagerCatalog.All.FirstOrDefault(m =>
                string.Equals(m.Model, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Key, token, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                throw new InvalidDataException(
                    $"Unsupported synthesis model '{requestedModel}'. Supported models: "
                    + string.Join(", ", ManagerCatalog.All.Select(m => m.Model)));
            }
            return match;
        }

        public static List<string> ExtractSearchTerms(string name, string? searchQuery)
        {
            var (displayName, _) = UiCommunityStore.NormalizeSubjectTagName(name);
            var terms = new List<string> { displayName };

            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                if (searchQuery.Length > UiCommunityStore.MaxSubjectTagQueryChars)
                {
                    throw new InvalidDataException(
                        $"Search query exceeds the {UiCommunityStore.MaxSubjectTagQueryChars}-character limit.");
                }
                foreach (var rawPart in searchQuery.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = Regex.Replace(rawPart.Trim(), @"\s+", " ");
                    if (cleaned.Length >= 2 && !terms.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                    {
                        terms.Add(cleaned);
                    }
                }
                if (terms.Count == 1)
                {
                    foreach (var word in Regex.Split(searchQuery.Trim(), @"\s+"))
                    {
                        var cleaned = word.Trim(',', ';', '.', '"', '\'', '(', ')', '[', ']');
                        if (cleaned.Length >= 2 && !terms.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                        {
                            terms.Add(cleaned);
                        }
                    }
                }
            }

            return terms;
        }

        public static List<UiSubjectTagMatchedPrompt> SearchAllHistory(
            Settings settings,
            string name,
            string? searchQuery,
            UiJobRegistry? jobs = null,
            UiCommunityStore? community = null,
            UiVisibilityStore? visibility = null,
            string? identityKey = null,
            int limit = MaxMatchedPrompts)
        {
            var terms = ExtractSearchTerms(name, searchQuery);
            limit = Math.Clamp(limit, 1, 100);
            var rawCandidates = new List<UiSubjectTagMatchedPrompt>();

            rawCandidates.AddRange(SearchGenerationArchive(settings, terms, visibility, limit * 2));

            if (jobs != null)
            {
                rawCandidates.AddRange(jobs.SearchPrompts(terms, visibility, limit * 2));
            }
            else
            {
                rawCandidates.AddRange(SearchUiHistoryOnDisk(settings, terms, visibility, limit * 2));
            }

            if (community != null)
            {
                rawCandidates.AddRange(community.SearchClaudePromptExchanges(identityKey ?? "", terms, limit));
            }

            rawCandidates.AddRange(SearchPromptFiles(settings, terms, limit));

            var primaryName = terms[0];
            var wholeWordPattern = new Regex(
                $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(primaryName)}(?![\p{{L}}\p{{N}}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var seenPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<(UiSubjectTagMatchedPrompt Item, int Score)>();

            foreach (var candidate in rawCandidates)
            {
                var trimmed = StripAutomaticSuffixes(candidate.Prompt);
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }
                var fingerprint = Regex.Replace(trimmed, @"\s+", " ").Trim();
                if (!seenPrompts.Add(fingerprint))
                {
                    continue;
                }
                var score = 0;
                if (wholeWordPattern.IsMatch(trimmed))
                {
                    score += 100;
                }
                else if (trimmed.IndexOf(primaryName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 50;
                }
                for (var i = 1; i < terms.Count; i++)
                {
                    if (trimmed.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 15;
                    }
                }
                var clipped = trimmed.Length > MaxSinglePromptChars
                    ? trimmed.Substring(0, MaxSinglePromptChars)
                    : trimmed;
                deduped.Add((new UiSubjectTagMatchedPrompt
                {
                    Source = candidate.Source,
                    RecordId = candidate.RecordId,
                    CreatedAtUtc = candidate.CreatedAtUtc,
                    Prompt = clipped,
                }, score));
            }

            return deduped
                .OrderByDescending(entry => entry.Score)
                .ThenByDescending(entry => entry.Item.CreatedAtUtc, StringComparer.Ordinal)
                .Take(limit)
                .Select(entry => entry.Item)
                .ToList();
        }

        public static async Task<PromptSubjectSynthesisResult> SynthesizeSubjectAsync(
            Settings settings,
            string name,
            string? requestedKind,
            string? searchQuery,
            string? requestedModel,
            IReadOnlyList<UiSubjectTagMatchedPrompt> matchedPrompts,
            string? customNotes = null,
            IManagerChatClient? clientOverride = null,
            CancellationToken ct = default)
        {
            var (displayName, _) = UiCommunityStore.NormalizeSubjectTagName(name);
            var normalizedKind = string.IsNullOrWhiteSpace(requestedKind)
                ? ""
                : UiCommunityStore.NormalizeSubjectKind(requestedKind);
            var terms = ExtractSearchTerms(displayName, searchQuery);
            var notes = (customNotes ?? "").Trim();
            if (notes.Length > MaxNotesChars)
            {
                throw new InvalidDataException($"Notes exceed the {MaxNotesChars}-character limit.");
            }
            var prompts = (matchedPrompts ?? Array.Empty<UiSubjectTagMatchedPrompt>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Prompt))
                .Take(MaxMatchedPrompts)
                .ToList();

            if (prompts.Count == 0 && notes.Length == 0)
            {
                throw new InvalidDataException(
                    $"No historical prompts matched '{displayName}'"
                    + (terms.Count > 1 ? $" (or search terms: {string.Join(", ", terms.Skip(1))})" : "")
                    + ". Add search terms or provide descriptive notes before synthesizing.");
            }

            var definition = ResolveModelDefinition(requestedModel);
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

            var userMessage = BuildSynthesisUserMessage(displayName, normalizedKind, terms, prompts, notes);
            var reply = await client.CompleteAsync(
                SynthesisSystemPrompt,
                new[]
                {
                    new ManagerChatMessage
                    {
                        Role = "user",
                        Text = userMessage,
                    },
                },
                ct);

            if (!string.IsNullOrWhiteSpace(reply.ProviderStop))
            {
                throw new InvalidOperationException(
                    $"Subject synthesis stopped early ({reply.ProviderStop}).");
            }

            var parsed = ParseSynthesisReply(reply.Text, normalizedKind);
            return new PromptSubjectSynthesisResult
            {
                Name = displayName,
                Kind = parsed.Kind,
                Description = parsed.Description,
                SourceSummary = parsed.Summary,
                Model = definition.Model,
                RawResponse = reply.RawResponse,
                SearchTerms = terms,
                MatchedPrompts = prompts,
            };
        }

        public static (string Kind, string Description, string Summary) ParseSynthesisReply(
            string? rawText,
            string? fallbackKind = null)
        {
            var stripped = UiGoalLoopProtocol.StripMarkdownFence(rawText ?? "").Trim();
            if (stripped.Length == 0)
            {
                throw new InvalidDataException("Synthesis model returned an empty reply.");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(stripped);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Synthesis model did not return valid JSON: {ex.Message}", ex);
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Synthesis reply must be a JSON object.");
                }

                string? rawDescription = null;
                if (doc.RootElement.TryGetProperty("description", out var descEl)
                    && descEl.ValueKind == JsonValueKind.String)
                {
                    rawDescription = descEl.GetString();
                }
                var description = (rawDescription ?? "").Trim();
                if (description.Length == 0)
                {
                    throw new InvalidDataException("Synthesis reply is missing a non-empty 'description' string.");
                }
                if (description.Length > UiCommunityStore.MaxSubjectTagDescriptionChars)
                {
                    throw new InvalidDataException(
                        $"Synthesized description exceeds {UiCommunityStore.MaxSubjectTagDescriptionChars} characters.");
                }

                string rawKind = "";
                if (doc.RootElement.TryGetProperty("kind", out var kindEl)
                    && kindEl.ValueKind == JsonValueKind.String)
                {
                    rawKind = (kindEl.GetString() ?? "").Trim();
                }
                var kind = !string.IsNullOrWhiteSpace(fallbackKind)
                    ? UiCommunityStore.NormalizeSubjectKind(fallbackKind)
                    : UiCommunityStore.NormalizeSubjectKind(rawKind.Length > 0 ? rawKind : "character");

                string summary = "";
                if (doc.RootElement.TryGetProperty("summary", out var sumEl)
                    && sumEl.ValueKind == JsonValueKind.String)
                {
                    summary = (sumEl.GetString() ?? "").Trim();
                }

                return (kind, description, summary);
            }
        }

        public static string BuildSynthesisUserMessage(
            string name,
            string requestedKind,
            IReadOnlyList<string> searchTerms,
            IReadOnlyList<UiSubjectTagMatchedPrompt> matchedPrompts,
            string notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Subject name to define: {name}");
            if (!string.IsNullOrWhiteSpace(requestedKind))
            {
                sb.AppendLine($"Requested subject kind: {requestedKind}");
            }
            sb.AppendLine($"Database search terms used: {string.Join(", ", searchTerms)}");
            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine();
                sb.AppendLine("Additional user notes / context:");
                sb.AppendLine(notes);
            }
            sb.AppendLine();
            sb.AppendLine($"Historical prompts found ({matchedPrompts.Count}):");

            var budget = MaxPromptCorpusChars;
            for (var i = 0; i < matchedPrompts.Count; i++)
            {
                var item = matchedPrompts[i];
                var block = $"\n--- [{i + 1}] source={item.Source} id={item.RecordId} date={item.CreatedAtUtc} ---\n{item.Prompt}\n";
                if (block.Length > budget && i > 0)
                {
                    break;
                }
                sb.Append(block);
                budget -= block.Length;
                if (budget <= 0)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        private static string StripAutomaticSuffixes(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return "";
            }
            var text = prompt.Trim();
            var sketchMarker = "\n\nComposition sketch legend:";
            var sketchIdx = text.IndexOf(sketchMarker, StringComparison.Ordinal);
            if (sketchIdx > 0)
            {
                text = text.Substring(0, sketchIdx).Trim();
            }
            var layoutMarker = "\n\nLayout template regions:";
            var layoutIdx = text.IndexOf(layoutMarker, StringComparison.Ordinal);
            if (layoutIdx > 0)
            {
                text = text.Substring(0, layoutIdx).Trim();
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
                    : !string.IsNullOrWhiteSpace(settings.ImageDownloadBaseFolder)
                        ? Path.Combine(settings.ImageDownloadBaseFolder, "generation-history.sqlite3")
                        : "";

            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return results;
            }

            try
            {
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

                using (var cmd = connection.CreateCommand())
                {
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
                        var id = reader.GetInt64(0);
                        var externalJobId = reader.GetString(1);
                        if (!string.IsNullOrWhiteSpace(externalJobId)
                            && visibility != null
                            && visibility.IsPromptHidden(externalJobId))
                        {
                            continue;
                        }
                        var startedAt = reader.GetString(2);
                        var prompt = reader.GetString(3);
                        var finalPrompt = reader.GetString(4);
                        var chosen = !string.IsNullOrWhiteSpace(prompt) ? prompt : finalPrompt;
                        if (!string.IsNullOrWhiteSpace(chosen))
                        {
                            results.Add(new UiSubjectTagMatchedPrompt
                            {
                                Source = "generation-archive",
                                RecordId = externalJobId.Length > 0 ? externalJobId : $"attempt-{id}",
                                CreatedAtUtc = startedAt,
                                Prompt = chosen,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Subject search: could not query generation archive {dbPath}: {ex.Message}");
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
            if (string.IsNullOrWhiteSpace(settings.ImageDownloadBaseFolder))
            {
                return results;
            }
            var root = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            if (!Directory.Exists(root))
            {
                return results;
            }
            var entries = UiJobStorage.ScanIndex(root)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();
            foreach (var entry in entries)
            {
                if (results.Count >= limit)
                {
                    break;
                }
                if (visibility != null && visibility.IsPromptHidden(entry.Id))
                {
                    continue;
                }
                var prompt = UiJobStorage.TryReadJobPrompt(root, entry.FolderName);
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    continue;
                }
                if (!terms.Any(t => prompt.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
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
                if (results.Count >= limit)
                {
                    break;
                }
                try
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
                        if (line.Length == 0 || line.StartsWith('#'))
                        {
                            continue;
                        }
                        if (!terms.Any(t => line.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
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
                catch (Exception ex)
                {
                    Logger.Log($"Subject search: could not scan prompt file {rawPath}: {ex.Message}");
                }
            }
            return results;
        }
    }
}
