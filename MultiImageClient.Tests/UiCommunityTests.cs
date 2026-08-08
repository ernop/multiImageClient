namespace MultiImageClient;

public sealed class UiCommunityTests
{
    [Fact]
    public void GenerationReturnActivityUsesPersistedSixHourBoundary()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            const long start = 1_800_000_000_000;

            Assert.True(store.RecordGenerationStart("alice", "Alice", "job1", start));
            Assert.False(store.RecordGenerationStart(
                "alice",
                "Alice",
                "job2",
                start + (long)TimeSpan.FromHours(1).TotalMilliseconds));
            Assert.True(store.RecordGenerationStart(
                "alice",
                "Alice",
                "job3",
                start
                    + (long)TimeSpan.FromHours(1).TotalMilliseconds
                    + (long)UiCommunityStore.ReturnAfter.TotalMilliseconds));

            var initial = store.ReadActivityAfter(null);
            Assert.Empty(initial.Records);
            Assert.Equal(2, initial.Cursor);

            var replay = store.ReadActivityAfter(0);
            Assert.Equal(2, replay.Records.Count);
            Assert.All(replay.Records, record => Assert.Equal("creator-return", record.Kind));
            Assert.Equal(new[] { "job1", "job3" }, replay.Records.Select(record => record.JobId));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FavoriteAndDeveloperRequestKeepExactActorsAndResources()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            store.RecordFavorite(
                "bob-login",
                "Bob",
                "alice-login",
                "Alice",
                "job-a",
                "image",
                "gpt2",
                3,
                1_800_000_000_000);
            var request = store.SubmitRequest(
                "bob-login",
                "Bob",
                "Please add a comparison control.",
                1_800_000_000_100);

            var activity = store.ReadActivityAfter(0);
            Assert.Collection(
                activity.Records,
                favorite =>
                {
                    Assert.Equal("favorite-image", favorite.Kind);
                    Assert.Equal("bob-login", favorite.ActorLogin);
                    Assert.Equal("alice-login", favorite.TargetLogin);
                    Assert.Equal("job-a", favorite.JobId);
                    Assert.Equal("gpt2", favorite.Generator);
                    Assert.Equal(3, favorite.ImageIndex);
                },
                submitted =>
                {
                    Assert.Equal("request-submitted", submitted.Kind);
                    Assert.Equal("developer", submitted.Audience);
                });

            var inbox = store.ReadRequestsAfter(0);
            var stored = Assert.Single(inbox.Records);
            Assert.Equal(request.Id, stored.Id);
            Assert.Equal("bob-login", stored.SubmitterLogin);
            Assert.Equal("Bob", stored.SubmitterDisplay);
            Assert.Equal("Please add a comparison control.", stored.Body);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RequestValidationAndInvalidCursorsFailClosed()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            Assert.Throws<InvalidDataException>(() =>
                store.SubmitRequest("alice", "Alice", "", 1));
            Assert.Throws<InvalidDataException>(() =>
                store.SubmitRequest(
                    "alice",
                    "Alice",
                    new string('x', UiCommunityStore.MaxRequestChars + 1),
                    1));

            var activity = store.ReadActivityAfter(99);
            Assert.True(activity.Reset);
            Assert.Equal(0, activity.Cursor);
            Assert.Empty(activity.Records);

            var requests = store.ReadRequestsAfter(99);
            Assert.True(requests.Reset);
            Assert.Equal(0, requests.Cursor);
            Assert.Empty(requests.Records);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ProfileRenameChangesLinkedPresentationWithoutChangingOriginalAttribution()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            store.ReserveLoginNames(new[] { "alice-login", "bob-login" });
            var first = store.SetProfileName(
                "alice-login",
                "Alice",
                new[] { "Alice Old" },
                1_800_000_000_000);
            var second = store.SetProfileName(
                "alice-login",
                "Alice New",
                new[] { "Alice Old" },
                1_800_000_000_100);

            Assert.Equal(first.PublicId, second.PublicId);
            var snapshot = store.SnapshotProfiles();
            Assert.Equal(2, snapshot.Version);
            Assert.Equal(
                "Alice New",
                snapshot.ResolveDisplay("alice-login", "Alice Old"));
            Assert.Equal(
                "Legacy Name",
                snapshot.ResolveDisplay("", "Legacy Name"));

            var reopened = CreateStore(folder).SnapshotProfiles();
            Assert.Equal(
                "Alice New",
                reopened.ResolveDisplay("alice-login", "Alice Old"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ProfileAliasesAndAccountLoginsCannotBeClaimedByAnotherAccount()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            store.ReserveLoginNames(new[] { "alice-login", "bob-login" });
            store.SetProfileName(
                "alice-login",
                "Alice Current",
                new[] { "Alice Old" },
                1_800_000_000_000);

            Assert.False(store.IsDisplayNameAvailable("bob-login", "alice old"));
            Assert.Throws<UiProfileNameConflictException>(() =>
                store.SetProfileName(
                    "bob-login",
                    "Alice Old",
                    Array.Empty<string>(),
                    1_800_000_000_100));
            Assert.Throws<UiProfileNameConflictException>(() =>
                store.SetProfileName(
                    "alice-login",
                    "bob-login",
                    Array.Empty<string>(),
                    1_800_000_000_200));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void LegacyAssignmentPersistsExactOwnerWithoutRewritingHistoricalName()
    {
        var folder = CreateTempFolder();
        try
        {
            var settings = new Settings
            {
                ImageDownloadBaseFolder = folder,
            };
            var registry = new UiJobRegistry(settings);
            var job = new UiJob
            {
                Id = "legacy-job",
                Prompt = "test",
                CreatedBy = "Alice Old",
                CreatorLogin = "",
                GeneratorKeys = new[] { "gpt2" },
            };
            registry.Add(job);

            var assigned = registry.AssignLegacyCreatorLogin(
                job.Id,
                "Alice Old",
                "alice-login");
            Assert.Equal("Alice Old", assigned.CreatedBy);
            Assert.Equal("alice-login", assigned.CreatorLogin);
            Assert.Throws<InvalidOperationException>(() =>
                registry.AssignLegacyCreatorLogin(
                    job.Id,
                    "Alice Old",
                    "bob-login"));

            var reloaded = new UiJobRegistry(settings).Get(job.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("Alice Old", reloaded.CreatedBy);
            Assert.Equal("alice-login", reloaded.CreatorLogin);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void GeneratorPreferencesPersistExactVisibilityDefaultsAndPresets()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            store.SaveGeneratorPreferences(new UiGeneratorPreferencesRecord
            {
                Login = "alice-login",
                ShowImageSection = true,
                ShowDescribeSection = false,
                HiddenGeneratorKeys = new List<string> { "gpt1", "describe-grok" },
                DefaultSelectedKeys = new List<string> { "gpt2", "recraft" },
                Presets = new List<UiGeneratorPresetRecord>
                {
                    new()
                    {
                        Id = "favorite_pair",
                        Name = "favorite pair",
                        GeneratorKeys = new List<string> { "gpt2", "recraft" },
                    },
                },
                UpdatedAtUnixMs = 1_800_000_000_000,
            });

            var reopened = CreateStore(folder).GetGeneratorPreferences("ALICE-LOGIN");
            Assert.NotNull(reopened);
            Assert.True(reopened.ShowImageSection);
            Assert.False(reopened.ShowDescribeSection);
            Assert.Equal(new[] { "gpt1", "describe-grok" }, reopened.HiddenGeneratorKeys);
            Assert.Equal(new[] { "gpt2", "recraft" }, reopened.DefaultSelectedKeys);
            var preset = Assert.Single(reopened.Presets);
            Assert.Equal("favorite_pair", preset.Id);
            Assert.Equal("favorite pair", preset.Name);
            Assert.Equal(new[] { "gpt2", "recraft" }, preset.GeneratorKeys);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ClaudePromptExchangePersistsExactWireResponseAndFailureHistory()
    {
        var folder = CreateTempFolder();
        try
        {
            var store = CreateStore(folder);
            var succeeded = store.StartClaudePromptExchange(
                "login:alice",
                "Alice",
                "claude-test",
                "expand it",
                "a small city",
                "system exact",
                "wire exact",
                1_800_000_000_000);
            store.CompleteClaudePromptExchange(
                succeeded.Id,
                "a vast bright city",
                "a vast bright city",
                "",
                1_800_000_000_100);

            var failed = store.StartClaudePromptExchange(
                "login:alice",
                "Alice",
                "claude-test",
                "fix it",
                "source exact",
                "system exact 2",
                "wire exact 2",
                1_800_000_000_200);
            store.CompleteClaudePromptExchange(
                failed.Id,
                "",
                "",
                "provider unavailable",
                1_800_000_000_300);

            var records = CreateStore(folder).ReadClaudePromptExchanges("login:alice");
            Assert.Collection(
                records,
                newest =>
                {
                    Assert.Equal("failed", newest.Status);
                    Assert.Equal("wire exact 2", newest.WirePrompt);
                    Assert.Equal("provider unavailable", newest.Error);
                },
                oldest =>
                {
                    Assert.Equal("succeeded", oldest.Status);
                    Assert.Equal("system exact", oldest.SystemPrompt);
                    Assert.Equal("wire exact", oldest.WirePrompt);
                    Assert.Equal("a vast bright city", oldest.RawResponse);
                    Assert.Equal("a vast bright city", oldest.ResultPrompt);
                });
            Assert.Throws<InvalidDataException>(() =>
                store.CompleteClaudePromptExchange(
                    succeeded.Id,
                    "changed",
                    "changed",
                    "",
                    1_800_000_000_400));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static UiCommunityStore CreateStore(string folder)
        => new(new Settings
        {
            ImageDownloadBaseFolder = folder,
            UiCommunityDbPath = Path.Combine(folder, "community.sqlite3"),
        });

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mic-community-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
