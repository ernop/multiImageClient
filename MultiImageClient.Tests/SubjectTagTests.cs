namespace MultiImageClient;

public sealed class SubjectTagTests
{
    private const string ValidReply = """
        {
          "kind": "character",
          "description": "a woman in her early 30s with shoulder-length straight black hair and round silver glasses",
          "summary": "Consolidated from three family prompts."
        }
        """;

    [Fact]
    public void CharactersCreateEditRenameRefuseDuplicatesDeleteByIdAndTransferLogins()
    {
        using var temp = new TempFolder();
        var store = new UiCommunityStore(temp.Settings());

        var jing = store.SaveSubjectTag("login:alice", "alice", null, "  Jing  ", "character", "cousin Jing",
            ManagerCatalog.KeyClaudeOpus55, "a woman in her late 20s", "from 2 prompts",
            new List<UiSubjectTagMatchedPrompt> { new() { Source = "ui-history", RecordId = "job-1", Prompt = "Jing on a porch" } },
            "{}", 1_000);
        Assert.Equal("Jing", jing.Name);
        Assert.Equal(1, jing.MatchedPromptsCount);

        var duplicate = Assert.Throws<InvalidDataException>(() => store.SaveSubjectTag("login:alice", "alice", null, "jing",
            "character", "", "manual", "someone else", "", null, "", 2_000));
        Assert.Contains("already have a character named Jing.", duplicate.Message);

        var renamed = store.SaveSubjectTag("login:alice", "alice", jing.Id, "Cousin Jing", "character", "",
            "manual", "a woman in her early 30s", "", null, "", 3_000);
        Assert.Equal(jing.Id, renamed.Id);
        Assert.Equal(1_000, renamed.CreatedAtUnixMs);
        Assert.Equal(3_000, renamed.UpdatedAtUnixMs);
        Assert.Null(store.GetSubjectTagByName("login:alice", "Jing"));
        Assert.Equal(jing.Id, store.GetSubjectTagByName("login:alice", "cousin  jing")!.Id);

        var courtyard = store.SaveSubjectTag("login:alice", "alice", null, "Grandma's Courtyard", "location", "",
            "manual", "a sunlit red-brick courtyard", "", null, "", 4_000);
        Assert.Throws<InvalidDataException>(() => store.SaveSubjectTag("login:alice", "alice", courtyard.Id, "Cousin Jing",
            "location", "", "manual", "renamed onto another", "", null, "", 5_000));
        Assert.Throws<InvalidDataException>(() => store.SaveSubjectTag("login:alice", "alice", "missing-id", "Ghost",
            "character", "", "manual", "nobody", "", null, "", 5_000));

        // Another account never sees or edits Alice's characters.
        Assert.Empty(store.ListSubjectTags("login:bob"));
        Assert.Throws<InvalidDataException>(() => store.SaveSubjectTag("login:bob", "bob", jing.Id, "Jing",
            "character", "", "manual", "hijack", "", null, "", 6_000));
        Assert.False(store.DeleteSubjectTag("login:bob", jing.Id));

        store.TransferLogin("alice", "alice-renamed");
        Assert.Empty(store.ListSubjectTags("login:alice"));
        Assert.Equal(new[] { "Cousin Jing", "Grandma's Courtyard" },
            store.ListSubjectTags("login:alice-renamed").Select(t => t.Name));

        // Delete matches the exact id, never a name.
        Assert.False(store.DeleteSubjectTag("login:alice-renamed", "Cousin Jing"));
        Assert.True(store.DeleteSubjectTag("login:alice-renamed", jing.Id));
        Assert.Single(store.ListSubjectTags("login:alice-renamed"));
    }

    [Fact]
    public void CharacterValidationFailsClosed()
    {
        using var temp = new TempFolder();
        var store = new UiCommunityStore(temp.Settings());
        UiSubjectTagRecord Save(string name = "Jing", string kind = "character", string model = "manual",
            string description = "a woman", string summary = "") =>
            store.SaveSubjectTag("local", "local", null, name, kind, "", model, description, summary, null, "", 1);

        Assert.Throws<InvalidDataException>(() => Save(name: " "));
        Assert.Throws<InvalidDataException>(() => Save(name: "two\nlines"));
        Assert.Throws<InvalidDataException>(() => Save(name: new string('x', UiCommunityStore.MaxSubjectTagNameChars + 1)));
        Assert.Throws<InvalidDataException>(() => Save(kind: ""));
        Assert.Throws<InvalidDataException>(() => Save(kind: "villain"));
        Assert.Throws<InvalidDataException>(() => Save(model: ""));
        Assert.Throws<InvalidDataException>(() => Save(description: "  "));
        Assert.Throws<InvalidDataException>(() => Save(summary: new string('s', UiCommunityStore.MaxSubjectTagSummaryChars + 1)));
        Assert.Empty(store.ListSubjectTags("local"));
    }

    [Fact]
    public void SynthesisReplyContractIsStrict()
    {
        var parsed = PromptSubjectSynthesis.ParseSynthesisReply("```json\n" + ValidReply + "\n```");
        Assert.Equal("character", parsed.Kind);
        Assert.StartsWith("a woman in her early 30s", parsed.Description);
        Assert.Equal("Consolidated from three family prompts.", parsed.Summary);

        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply("not json"));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply("[]"));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply(
            """{"kind":"character","description":"","summary":"s"}"""));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply(
            """{"description":"d","summary":"s"}"""));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply(
            """{"kind":"villain","description":"d","summary":"s"}"""));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply(
            """{"kind":"character","description":"d"}"""));
        Assert.Throws<InvalidDataException>(() => PromptSubjectSynthesis.ParseSynthesisReply(
            """{"kind":"character","description":"d","summary":"s","name":"Jing"}"""));
    }

    [Fact]
    public void SharedSiteSearchReadsOnlyTheAccountsOwnPromptsAndWholeWords()
    {
        using var temp = new TempFolder();
        var promptFile = Path.Combine(temp.Path, "owner-prompts.txt");
        File.WriteAllLines(promptFile, new[] { "Jing on the sunny pier in Seattle." });
        var settings = temp.Settings(promptFile);
        var store = new UiCommunityStore(settings);
        var registry = new UiJobRegistry(settings);
        registry.Add(new UiJob { Id = "alice-1", Prompt = "Jing laughing in a sunlit kitchen.", CreatedBy = "alice", CreatorLogin = "alice" });
        registry.Add(new UiJob { Id = "alice-2", Prompt = "A night market in Beijing.", CreatedBy = "alice", CreatorLogin = "alice" });
        registry.Add(new UiJob { Id = "alice-3", Prompt = "Cousin J at the lake.", CreatedBy = "alice", CreatorLogin = "alice" });
        registry.Add(new UiJob { Id = "alice-4", Prompt = "Cousin Jones on a bright pier.", CreatedBy = "alice", CreatorLogin = "alice" });
        registry.Add(new UiJob { Id = "bob-1", Prompt = "Jing as a pirate captain.", CreatedBy = "bob", CreatorLogin = "bob" });
        var exchange = store.StartClaudePromptExchange("login:alice", "alice", "claude-opus-5-5", "detail",
            "Jing with a camera", "sys", "wire", 1);
        store.CompleteClaudePromptExchange(exchange.Id, "raw", "Jing holding a silver rangefinder camera.", "", 2);
        var bobExchange = store.StartClaudePromptExchange("login:bob", "bob", "claude-opus-5-5", "detail",
            "Jing in armor", "sys", "wire", 3);
        store.CompleteClaudePromptExchange(bobExchange.Id, "raw", "Jing in shining armor.", "", 4);

        var alice = PromptSubjectSynthesis.SearchAllHistory(settings, "Jing", "Cousin J", registry, store, null,
            PromptSubjectSearchScope.ForAccount("alice"));
        Assert.Equal(
            new[] { "Cousin J at the lake.", "Jing holding a silver rangefinder camera.", "Jing laughing in a sunlit kitchen." },
            alice.Select(m => m.Prompt).OrderBy(p => p, StringComparer.Ordinal));

        var local = PromptSubjectSynthesis.SearchAllHistory(settings, "Jing", "", registry, store, null,
            PromptSubjectSearchScope.LocalOwner);
        Assert.Contains(local, m => m.Source == "file:owner-prompts.txt");
        Assert.Contains(local, m => m.RecordId == "bob-1");
        Assert.Contains(local, m => m.Prompt == "Jing in shining armor.");
        Assert.DoesNotContain(local, m => m.RecordId == "alice-2");
    }

    [Fact]
    public async Task SynthesisKeepsTheChosenKindRecordsTheCatalogKeyAndRequiresEvidence()
    {
        var client = new FakeWriter(ValidReply.Replace("\"character\"", "\"object\""));
        var matches = new List<UiSubjectTagMatchedPrompt> { new() { Source = "ui-history", RecordId = "j1", Prompt = "Jing at the beach" } };

        var result = await PromptSubjectSynthesis.SynthesizeSubjectAsync(new Settings(), "Jing", "character", "",
            ManagerCatalog.KeyClaudeOpus55, matches, "", client);
        Assert.Equal("character", result.Kind);
        Assert.Equal(ManagerCatalog.KeyClaudeOpus55, result.Model);
        Assert.Contains("Jing at the beach", client.LastUserMessage);

        await Assert.ThrowsAsync<InvalidDataException>(() => PromptSubjectSynthesis.SynthesizeSubjectAsync(
            new Settings(), "Jing", "character", "", ManagerCatalog.KeyClaudeOpus55,
            new List<UiSubjectTagMatchedPrompt>(), "", client));
        await Assert.ThrowsAsync<InvalidDataException>(() => PromptSubjectSynthesis.SynthesizeSubjectAsync(
            new Settings(), "Jing", "character", "", "claude-opus-5-5", matches, "", client));

        var fromNotes = await PromptSubjectSynthesis.SynthesizeSubjectAsync(new Settings(), "Jing", "character", "",
            ManagerCatalog.KeyClaudeOpus55, new List<UiSubjectTagMatchedPrompt>(), "She always wears a red scarf.", client);
        Assert.Contains("red scarf", client.LastUserMessage);
        Assert.Empty(fromNotes.MatchedPrompts);
    }

    [Fact]
    public void OneSynthesisRunsAtATimePerAccount()
    {
        using (var first = PromptSubjectSynthesis.TryBeginSynthesis("login:test-lease"))
        {
            Assert.NotNull(first);
            Assert.Null(PromptSubjectSynthesis.TryBeginSynthesis("login:test-lease"));
            using var other = PromptSubjectSynthesis.TryBeginSynthesis("login:test-lease-other");
            Assert.NotNull(other);
        }
        using var again = PromptSubjectSynthesis.TryBeginSynthesis("login:test-lease");
        Assert.NotNull(again);
    }

    private sealed class FakeWriter : IManagerChatClient
    {
        private readonly string _reply;
        public FakeWriter(string reply) => _reply = reply;
        public string Model => "fake";
        public string LastUserMessage { get; private set; } = "";

        public Task<ManagerChatReply> CompleteAsync(string systemPrompt, IReadOnlyList<ManagerChatMessage> history, CancellationToken ct)
        {
            LastUserMessage = history.Single().Text;
            return Task.FromResult(new ManagerChatReply
            {
                Text = _reply,
                ProviderReasoning = "",
                RawResponse = _reply,
                RedactedRequest = "",
                RequestBytes = 0,
                Ms = 1,
                Model = "fake",
            });
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MultiImageClient.SubjectTagTests." + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public Settings Settings(string? promptFile = null) => new()
        {
            LogFilePath = System.IO.Path.Combine(Path, "log.txt"),
            ImageDownloadBaseFolder = Path,
            UiCommunityDbPath = System.IO.Path.Combine(Path, "ui-community.sqlite3"),
            EnableGenerationArchive = false,
            PromptFiles = promptFile == null ? new List<string>() : new List<string> { promptFile },
        };

        public void Dispose() => Directory.Delete(Path, true);
    }
}
