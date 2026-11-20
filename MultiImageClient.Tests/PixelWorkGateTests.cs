namespace MultiImageClient;

public sealed class PixelWorkGateTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentWorkRunsOneAtATime()
    {
        var running = 0;
        var maxRunning = 0;
        async Task<int> Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return now;
        }

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => PixelWorkGate.RunAsync(Work)))
            .Append(Task.Run(() => PixelWorkGate.Run(() => Work().GetAwaiter().GetResult())))
            .ToArray();
        await Task.WhenAll(tasks).WaitAsync(Limit);

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task NestedWorkInsideTheGatePassesThrough()
    {
        var result = await PixelWorkGate.RunAsync(async () =>
        {
            var inner = await PixelWorkGate.RunAsync(() => Task.FromResult(2));
            var sync = PixelWorkGate.Run(() => 3);
            var offThread = await Task.Run(() => PixelWorkGate.Run(() => 4));
            return inner + sync + offThread;
        }).WaitAsync(Limit);

        Assert.Equal(9, result);
    }

    [Fact]
    public async Task AFailureReleasesTheGate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PixelWorkGate.RunAsync<int>(() => throw new InvalidOperationException("decode failed")));
        Assert.Throws<InvalidOperationException>(() =>
            PixelWorkGate.Run<int>(() => throw new InvalidOperationException("decode failed")));

        var next = await Task.Run(() => PixelWorkGate.Run(() => 1)).WaitAsync(Limit);
        Assert.Equal(1, next);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
