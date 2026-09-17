using MultiImageClient;

public sealed class DiscordDailyThreadTests
{
    [Theory]
    [InlineData("2026-09-18T06:59:59Z", "2026-09-17")]
    [InlineData("2026-09-18T07:00:00Z", "2026-09-18")]
    [InlineData("2026-01-02T07:59:59Z", "2026-01-01")]
    [InlineData("2026-01-02T08:00:00Z", "2026-01-02")]
    [InlineData("2026-03-08T09:59:59Z", "2026-03-08")]
    [InlineData("2026-03-08T10:00:00Z", "2026-03-08")]
    [InlineData("2026-11-01T08:59:59Z", "2026-11-01")]
    [InlineData("2026-11-01T09:00:00Z", "2026-11-01")]
    public void DateUsesCaliforniaMidnightAndDaylightSaving(string utc, string day) =>
        Assert.Equal(day, DiscordDailyThreads.Day(DateTimeOffset.Parse(utc)));

    [Fact]
    public void NameIncludesWeekdayAndUnambiguousLongDate() =>
        Assert.Equal("Daily Thursday, September 17, 2026 image thread", DiscordDailyThreads.Name("2026-09-17"));

    [Fact]
    public async Task SharedStorePreventsConcurrentAndRestartDuplicates()
    {
        var folder = Directory.CreateTempSubdirectory("mic-daily-thread-").FullName;
        try
        {
            var started = new TaskCompletionSource();
            var finish = new TaskCompletionSource<string>();
            var first = DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-17",
                () => { started.SetResult(); return finish.Task; }, _ => Task.CompletedTask);
            await started.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-17",
                () => throw new Exception("Must not create twice"), _ => Task.CompletedTask));
            finish.SetResult("999");
            Assert.Equal("999", await first);
            var checkedId = "";
            Assert.Equal("999", await DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-17",
                () => throw new Exception("Must reuse persisted thread"), id => { checkedId = id; return Task.CompletedTask; }));
            Assert.Equal("999", checkedId);
            Assert.Equal("1000", await DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-18",
                () => Task.FromResult("1000"), _ => Task.CompletedTask));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task LostCreateResponseBlocksAnotherCreateAfterRestart()
    {
        var folder = Directory.CreateTempSubdirectory("mic-daily-thread-").FullName;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-17",
                () => throw new IOException("lost response"), _ => Task.CompletedTask));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DiscordDailyThreads.GetOrCreateAsync(folder, "111", "222", "2026-09-17",
                () => throw new Exception("Must not retry creation"), _ => Task.CompletedTask));
            Assert.Contains("unconfirmed", error.Message);
        }
        finally { Directory.Delete(folder, true); }
    }
}
