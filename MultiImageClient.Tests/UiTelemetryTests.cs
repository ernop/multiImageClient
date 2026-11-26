using System;
using System.IO;
using System.Text.Json;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests;

public sealed class UiTelemetryTests
{
    [Fact]
    public void RecordsDailyJsonlAndBuildsStreamingSummary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mic-telemetry-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
            var store = new UiTelemetryStore(dir, clock: () => now);
            var batch = UiTelemetryStore.ParseBatchJson("""
            {
              "tab": "abcd1234",
              "page": "composer",
              "events": [
                { "ev": "page.open", "seq": 0, "d": { "viewport": "desktop", "w": 1440 } },
                { "ev": "stage1.mode", "seq": 1, "d": { "mode": "split" } },
                { "ev": "viewer.close", "seq": 2, "d": { "dwellMs": 12400, "itemsViewed": 5 } }
              ]
            }
            """);

            var accepted = store.RecordBatch("ernieMultiZone", "admin", batch);
            Assert.Equal(3, accepted);

            var dailyFile = Path.Combine(dir, "telemetry-2026-11-26.jsonl");
            Assert.True(File.Exists(dailyFile));
            Assert.Equal(3, File.ReadAllLines(dailyFile).Length);

            var summaryJson = JsonSerializer.Serialize(store.BuildSummary(7));
            using var doc = JsonDocument.Parse(summaryJson);
            Assert.Equal(3, doc.RootElement.GetProperty("totalEvents").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("uniqueTabs").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("filesCount").GetInt32());
            Assert.Equal(12400, doc.RootElement.GetProperty("viewer").GetProperty("medianDwellMs").GetInt64());
            Assert.Equal(5, doc.RootElement.GetProperty("viewer").GetProperty("totalItemsViewed").GetInt64());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[{"ev":"composer.generate","d":{"prompt":"secret"}}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[{"ev":"composer.generate","d":{"instruction":"secret"}}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[{"ev":"composer.generate","d":{"ip":"1.2.3.4"}}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"unknown-page","events":[{"ev":"page.open"}]}""")]
    [InlineData("""{"tab":"x","page":"composer","events":[{"ev":"page.open"}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[{"ev":"BAD EVENT"}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[{"ev":"page.open","d":{"nested":{"a":1}}}]}""")]
    [InlineData("""{"tab":"abcd1234","page":"composer","events":[]}""")]
    public void RejectsDisallowedSchemaAndFreeTextFields(string json)
    {
        Assert.Throws<InvalidDataException>(() => UiTelemetryStore.ParseBatchJson(json));
    }

    [Fact]
    public void EnforcesPerActorRateLimitAndDailyByteCapAndRetention()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mic-telemetry-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var expiredFile = Path.Combine(dir, "telemetry-2026-01-01.jsonl");
            File.WriteAllText(expiredFile, "{}\n");

            var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
            var store = new UiTelemetryStore(
                dir,
                maxDailyFileBytes: 400,
                retentionDays: 90,
                maxEventsPerMinutePerActor: 2,
                clock: () => now);
            Assert.False(File.Exists(expiredFile));

            var oneEvent = UiTelemetryStore.ParseBatchJson("""
            {"tab":"abcd1234","page":"composer","events":[{"ev":"page.open"}]}
            """);
            Assert.Equal(1, store.RecordBatch("alice", "normal", oneEvent));
            Assert.Equal(1, store.RecordBatch("alice", "normal", oneEvent));
            Assert.Throws<UiTelemetryRateLimitException>(() => store.RecordBatch("alice", "normal", oneEvent));

            // Different actor hits the tiny daily byte cap next.
            Assert.Throws<InvalidDataException>(() =>
            {
                for (var i = 0; i < 10; i++)
                    store.RecordBatch($"bob{i}", "normal", oneEvent);
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildInfoEmbedsValidUtcBuildTimestamp()
    {
        Assert.False(string.IsNullOrWhiteSpace(UiBuildInfo.BuiltAtUtc));
        Assert.True(DateTimeOffset.TryParse(UiBuildInfo.BuiltAtUtc, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }
}
