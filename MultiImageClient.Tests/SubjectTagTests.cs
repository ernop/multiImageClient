namespace MultiImageClient;

public sealed class SubjectTagTests
{
    [Fact]
    public void SubjectTagsPersistUpdateDeleteAndTransferAcrossLoginRenames()
    {
        var folder = CreateTempFolder();
        try
        {
            var settings = new Settings
            {
                LogFilePath = Path.Combine(folder, "log.txt"),
                ImageDownloadBaseFolder = folder,
                UiCommunityDbPath = Path.Combine(folder, "ui-community.sqlite3"),
            };
            var store = new UiCommunityStore(settings);

            var sampleMatches = new List<UiSubjectTagMatchedPrompt>
            {
                new()
                {
                    Source = "ui-history",
                    RecordId = "job-1",
                    CreatedAtUtc = "2026-10-10T12:00:00Z",
                    Prompt = "Jing smiling on the sunny wooden porch.",
                },
                new()
                {
                    Source = "generation-archive",
                    RecordId = "attempt-2",
                    CreatedAtUtc = "2026-10-11T15:30:00Z",
                    Prompt = "Jenny and Jing walking through the botanical garden in bright daylight.",
                },
            };

            var saved = store.UpsertSubjectTag(
                identityKey: "login:alice",
                actorDisplay: "alice",
                name: "  Jing  ",
                kind: "character",
                searchQuery: "Jing, cousin Jing",
                model: "claude-opus-5-5",
                description: "an East Asian woman in her late 20s with shoulder-length straight black hair, warm brown eyes, and a navy linen jacket",
                sourceSummary: "Synthesized from 2 family portrait prompts",
                matchedPrompts: sampleMatches,
                rawResponse: "{\"kind\":\"character\"}",
                nowUnixMs: 1_800_000_000_000);

            Assert.Equal("Jing", saved.Name);
            Assert.Equal("JING", saved.NormalizedName);
            Assert.Equal("character", saved.Kind);
            Assert.Equal(2, saved.MatchedPromptsCount);
            Assert.Equal("claude-opus-5-5", saved.Model);

            var updated = store.UpsertSubjectTag(
                identityKey: "login:alice",
                actorDisplay: "alice",
                name: "jing",
                kind: "character",
                searchQuery: "Jing",
                model: "claude-opus-5-5",
                description: "an East Asian woman in her early 30s with shoulder-length black hair, silver-rimmed glasses, and a cream cardigan",
                sourceSummary: "Updated from newer family memory prompts",
                matchedPrompts: sampleMatches,
                rawResponse: "{\"kind\":\"character\"}",
                nowUnixMs: 1_800_000_005_000);

            Assert.Equal(saved.Id, updated.Id);
            Assert.Equal("JING", updated.NormalizedName);
            Assert.Equal(1_800_000_000_000, updated.CreatedAtUnixMs);
            Assert.Equal(1_800_000_005_000, updated.UpdatedAtUnixMs);

            store.UpsertSubjectTag(
                identityKey: "login:alice",
                actorDisplay: "alice",
                name: "Grandma's Courtyard",
                kind: "location",
                searchQuery: "Tainan courtyard",
                model: "claude-sonnet-5-5",
                description: "a sunlit red-brick courtyard house in Tainan with terracotta roof tiles and potted bougainvillea",
                sourceSummary: "Synthesized from travel prompts",
                matchedPrompts: null,
                rawResponse: "",
                nowUnixMs: 1_800_000_006_000);

            var list = store.ListSubjectTags("login:alice");
            Assert.Equal(2, list.Count);

            var byName = store.GetSubjectTag("login:alice", "Grandma's Courtyard");
            Assert.NotNull(byName);
            Assert.Equal("GRANDMA'S COURTYARD", byName!.NormalizedName);

            // TransferLogin preserves subject tags across account renames.
            store.TransferLogin("alice", "alice-renamed");
            Assert.Empty(store.ListSubjectTags("login:alice"));
            var transferred = store.ListSubjectTags("login:alice-renamed");
            Assert.Equal(2, transferred.Count);

            Assert.True(store.DeleteSubjectTag("login:alice-renamed", "Jing"));
            Assert.Single(store.ListSubjectTags("login:alice-renamed"));
            Assert.False(store.DeleteSubjectTag("login:alice-renamed", "Jing"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SubjectTagValidationAndSynthesisReplyParsingFailClosed()
    {
        var folder = CreateTempFolder();
        try
        {
            var settings = new Settings
            {
                LogFilePath = Path.Combine(folder, "log.txt"),
                ImageDownloadBaseFolder = folder,
                UiCommunityDbPath = Path.Combine(folder, "ui-community.sqlite3"),
            };
            var store = new UiCommunityStore(settings);

            Assert.Throws<InvalidDataException>(() =>
                store.UpsertSubjectTag("login:alice", "alice", "", "character", "", "manual", "valid description", "", null, "", 1000));
            Assert.Throws<InvalidDataException>(() =>
                store.UpsertSubjectTag("login:alice", "alice", "Jing", "invalid-kind", "", "manual", "valid description", "", null, "", 1000));
            Assert.Throws<InvalidDataException>(() =>
                store.UpsertSubjectTag("login:alice", "alice", "Jing", "character", "", "manual", "   ", "", null, "", 1000));

            var validJson = """
                {
                  "kind": "character",
                  "description": "Jing (an East Asian woman in her late 20s with shoulder-length black hair and bright daytime lighting)",
                  "summary": "Consolidated from three family vacation prompts."
                }
                """;
            var parsed = PromptSubjectSynthesis.ParseSynthesisReply(validJson, "");
            Assert.Equal("character", parsed.Kind);
            Assert.Contains("shoulder-length black hair", parsed.Description);
            Assert.Equal("Consolidated from three family vacation prompts.", parsed.Summary);

            Assert.Throws<InvalidDataException>(() =>
                PromptSubjectSynthesis.ParseSynthesisReply("not json at all", "character"));
            Assert.Throws<InvalidDataException>(() =>
                PromptSubjectSynthesis.ParseSynthesisReply("""{"kind":"character","description":""}""", "character"));
            Assert.Throws<InvalidDataException>(() =>
                PromptSubjectSynthesis.ParseSynthesisReply("""{"kind":"invalid","description":"some description","sourceSummary":"ok"}""", ""));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SearchAllHistoryCollectsAndDeduplicatesAcrossUiJobsClaudeExchangesAndPromptFiles()
    {
        var folder = CreateTempFolder();
        try
        {
            var communityDb = Path.Combine(folder, "ui-community.sqlite3");
            var promptFile = Path.Combine(folder, "family-memories.txt");
            File.WriteAllLines(
                promptFile,
                new[]
                {
                    "Jenny and Jing standing on the sunny wooden pier in Seattle, Jing wearing a navy linen jacket and silver glasses.",
                    "Unrelated prompt about a mountain bicycle on a gravel trail.",
                });

            var settings = new Settings
            {
                LogFilePath = Path.Combine(folder, "log.txt"),
                ImageDownloadBaseFolder = folder,
                UiCommunityDbPath = communityDb,
                EnableGenerationArchive = false,
                PromptFiles = new List<string> { promptFile },
            };
            var store = new UiCommunityStore(settings);

            var registry = new UiJobRegistry(settings);
            var job = new UiJob
            {
                Id = "jobtest001",
                Prompt = "Jing laughing in Grandma's sunlit kitchen while making dumplings, wearing a cream cardigan, shoulder-length straight black hair.",
                CreatedBy = "alice",
            };
            registry.Add(job);

            var exchange = store.StartClaudePromptExchange(
                identityKey: "login:alice",
                actorDisplay: "alice",
                model: "claude-opus-5-5",
                instruction: "Add more detail about Jing's camera",
                originalPrompt: "Jing holding a vintage Leica camera at the botanical garden",
                systemPrompt: "sys",
                wirePrompt: "wire",
                requestedAtUnixMs: 1_800_000_000_000);
            store.CompleteClaudePromptExchange(
                exchange.Id,
                rawResponse: "Jing (East Asian woman, 30, shoulder-length black hair) holding a silver rangefinder camera in a bright botanical garden.",
                resultPrompt: "Jing (East Asian woman, 30, shoulder-length black hair) holding a silver rangefinder camera in a bright botanical garden.",
                error: "",
                completedAtUnixMs: 1_800_000_001_000);

            var terms = PromptSubjectSynthesis.ExtractSearchTerms("Jing", "cousin Jing");
            Assert.Equal(new[] { "Jing", "cousin Jing" }, terms);

            var matches = PromptSubjectSynthesis.SearchAllHistory(
                settings,
                name: "Jing",
                searchQuery: "cousin Jing",
                jobs: registry,
                community: store,
                visibility: null,
                identityKey: "login:alice",
                limit: 20);

            Assert.Equal(3, matches.Count);
            Assert.Contains(matches, m => m.Source == "ui-history" && m.RecordId == job.Id);
            Assert.Contains(matches, m => m.Source == "prompt-exchange" && m.Prompt.Contains("silver rangefinder camera"));
            Assert.Contains(matches, m => m.Source == "file:family-memories.txt" && m.Prompt.Contains("sunny wooden pier in Seattle"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string CreateTempFolder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "MultiImageClient.SubjectTagTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
