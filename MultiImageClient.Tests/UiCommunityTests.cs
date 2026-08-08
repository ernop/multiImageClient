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
