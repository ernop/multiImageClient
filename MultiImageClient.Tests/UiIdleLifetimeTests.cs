using System;
using MultiImageClient;
using Xunit;

public sealed class UiIdleLifetimeTests
{
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public void HealthProbesDoNotPreventSleepAndSleepingRejectsNewWork()
    {
        var clock = new Clock();
        var idle = new UiIdleLifetime(TimeSpan.FromSeconds(60), clock);
        clock.Seconds = 61;
        Assert.True(idle.TryEnter());
        Assert.False(idle.TrySleep(() => false));
        idle.Exit(activity: false);
        Assert.True(idle.TrySleep(() => false));
        Assert.False(idle.TryEnter());
    }

    [Fact]
    public void BrowserRequestsAndBackgroundWorkResetTheFullIdlePeriod()
    {
        var clock = new Clock();
        var idle = new UiIdleLifetime(TimeSpan.FromSeconds(60), clock);
        clock.Seconds = 61;
        Assert.False(idle.TrySleep(() => true));
        clock.Seconds = 120;
        Assert.False(idle.TrySleep(() => false));
        Assert.True(idle.TryEnter());
        idle.Exit(activity: true);
        clock.Seconds = 179;
        Assert.False(idle.TrySleep(() => false));
        clock.Seconds = 180;
        Assert.True(idle.TrySleep(() => false));
    }

    [Fact]
    public void BusyProbeFailureDoesNotCommitToSleep()
    {
        var clock = new Clock { Seconds = 1 };
        var idle = new UiIdleLifetime(TimeSpan.FromSeconds(60), clock);
        clock.Seconds = 100;
        Assert.Throws<InvalidOperationException>(() => idle.TrySleep(() => throw new InvalidOperationException()));
        Assert.True(idle.TryEnter());
        idle.Exit(activity: true);
    }
}
