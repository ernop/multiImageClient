#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace MultiImageClient;

public class UiPromptArchiveTests
{
    private sealed class TempArchiveDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mic-prompt-archive-test-" + Guid.NewGuid().ToString("N"));
        public string DbPath => Path.Combine(Root, "prompt-archive.sqlite3");

        public TempArchiveDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static void WriteJobJson(string savesDir, string jobId, string prompt, string creatorLogin, DateTime createdUtc, params string[] generators)
    {
        var dir = Path.Combine(savesDir, "UiHistory", jobId);
        Directory.CreateDirectory(dir);
        var payload = new
        {
            Id = jobId,
            Prompt = prompt,
            CreatorLogin = creatorLogin,
            CreatedAt = createdUtc,
            GeneratorKeys = generators,
        };
        File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(payload));
    }

    private static void WriteGoalLoopJson(string savesDir, string loopId, string goalText, string creatorLogin, DateTime createdUtc, params string[] generators)
    {
        var dir = Path.Combine(savesDir, "UiGoalLoops", loopId);
        Directory.CreateDirectory(dir);
        var payload = new
        {
            id = loopId,
            goal = goalText,
            creatorLogin,
            createdAtUnixMs = new DateTimeOffset(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            managerLabel = "Opus 5.5",
            generators,
        };
        File.WriteAllText(Path.Combine(dir, "loop.json"), JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void MissingDatabaseReturnsUnavailableCleanly()
    {
        using var tmp = new TempArchiveDir();
        var archive = new UiPromptArchive(tmp.DbPath, "local");

        Assert.False(archive.Exists);
        Assert.False(archive.GetStats().Exists);
        Assert.False(archive.GetRandom(new UiPromptArchiveFilter(), 10, Array.Empty<long>()).Exists);
        Assert.False(archive.Search(new UiPromptArchiveFilter(), "newest", 1, 20).Exists);
        Assert.Null(archive.GetItem(1));
    }

    [Fact]
    public void WrongSchemaVersionFailsClosed()
    {
        GenerationArchive.EnsureSqliteAvailable();
        using var tmp = new TempArchiveDir();
        var csb = new SqliteConnectionStringBuilder { DataSource = tmp.DbPath, Pooling = false };
        using (var conn = new SqliteConnection(csb.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES ('schema_version', '999');";
            cmd.ExecuteNonQuery();
        }

        var archive = new UiPromptArchive(tmp.DbPath, "local");
        Assert.Throws<InvalidDataException>(() => archive.GetStats());
        Assert.Throws<InvalidDataException>(() => archive.GetRandom(new UiPromptArchiveFilter(), 5, Array.Empty<long>()));
        Assert.Throws<InvalidDataException>(() => archive.Search(new UiPromptArchiveFilter(), "newest", 1, 10));
        Assert.Throws<InvalidDataException>(() => archive.GetItem(1));
        Assert.Throws<InvalidDataException>(() => archive.SyncFromInstance(tmp.Root, allowEmptyCreator: true));
    }

    [Fact]
    public void CorruptDatabaseFailsClosed()
    {
        using var tmp = new TempArchiveDir();
        File.WriteAllText(tmp.DbPath, "this is definitely not a valid sqlite database");
        var archive = new UiPromptArchive(tmp.DbPath, "local");

        Assert.ThrowsAny<Exception>(() => archive.GetStats());
        Assert.ThrowsAny<Exception>(() => archive.GetRandom(new UiPromptArchiveFilter(), 5, Array.Empty<long>()));
    }

    [Fact]
    public void SyncEnforcesOwnerIsolationAndIsIdempotent()
    {
        using var tmp = new TempArchiveDir();
        WriteJobJson(tmp.Root, "job-owner-1", "A sunlit stone lighthouse beside calm emerald water", "ernieMultiZone", new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), "gpt2");
        WriteJobJson(tmp.Root, "job-owner-2", "A sunlit stone lighthouse beside calm emerald water", "ernieMultiZone", new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc), "ideogram-v45");
        WriteJobJson(tmp.Root, "job-empty", "A bright ceramic bowl of tangerines on white linen", "", new DateTime(2025, 8, 1, 9, 0, 0, DateTimeKind.Utc), "bfl");
        WriteJobJson(tmp.Root, "job-other-user", "Secret prompt belonging to another member", "dallasVille", new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc), "gpt2");
        WriteJobJson(tmp.Root, "job-other-user-2", "Another member prompt that must never enter", "governorOfThings", new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc), "gpt2");
        WriteJobJson(tmp.Root, "job-default-desc", UiJobRunner.DefaultDescribeInstruction, "ernieMultiZone", new DateTime(2026, 4, 3, 9, 0, 0, DateTimeKind.Utc), "describe-openai");
        WriteJobJson(tmp.Root, "job-url-only", "https://example.com/image.png", "ernieMultiZone", new DateTime(2026, 4, 4, 9, 0, 0, DateTimeKind.Utc), "gpt2");
        WriteGoalLoopJson(tmp.Root, "loop-owner", "Make a clean infographic showing solar system scale", "ernieMultiZone", new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), "gpt2");
        WriteGoalLoopJson(tmp.Root, "loop-other", "Non-owner goal loop", "dallasVille", new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc), "gpt2");

        // Vibecoders mode: allowEmptyCreator = false (ONLY ernieMultiZone)
        var vibeDb = Path.Combine(tmp.Root, "vibe.sqlite3");
        var vibeArchive = new UiPromptArchive(vibeDb, "vibecoders");
        var vibeSync = vibeArchive.SyncFromInstance(tmp.Root, allowEmptyCreator: false);

        Assert.Equal(2, vibeSync.AddedPrompts); // lighthouse + goal loop
        Assert.Equal(3, vibeSync.AddedOccurrences); // 2 lighthouse jobs + 1 goal loop
        var vibeAll = vibeArchive.Search(new UiPromptArchiveFilter(Kind: "all"), "newest", 1, 50);
        Assert.Equal(2, vibeAll.TotalMatching);
        Assert.DoesNotContain(vibeAll.Items, i => i.PromptText.Contains("another member", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vibeAll.Items, i => i.PromptText.Contains("tangerines", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(vibeAll.Items, i => i.PromptText.Contains("example.com", StringComparison.OrdinalIgnoreCase));

        // Second sync is completely idempotent
        var vibeSync2 = vibeArchive.SyncFromInstance(tmp.Root, allowEmptyCreator: false);
        Assert.Equal(0, vibeSync2.AddedPrompts);
        Assert.Equal(0, vibeSync2.AddedOccurrences);

        // Local/AVE mode: allowEmptyCreator = true (ernieMultiZone + legacy empty creator, STILL excludes other users)
        var localArchive = new UiPromptArchive(tmp.DbPath, "local");
        var localSync = localArchive.SyncFromInstance(tmp.Root, allowEmptyCreator: true);
        Assert.Equal(3, localSync.AddedPrompts); // lighthouse + tangerines + goal loop
        Assert.Equal(4, localSync.AddedOccurrences);

        var localAll = localArchive.Search(new UiPromptArchiveFilter(Kind: "all"), "newest", 1, 50);
        Assert.Equal(3, localAll.TotalMatching);
        Assert.DoesNotContain(localAll.Items, i => i.PromptText.Contains("another member", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(localAll.Items, i => i.PromptText.Contains("tangerines", StringComparison.OrdinalIgnoreCase));

        var lighthouse = localAll.Items.Single(i => i.PromptText.Contains("lighthouse", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, lighthouse.OccurrenceCount);
        Assert.Contains("gpt2", lighthouse.Models);
        Assert.Contains("ideogram-v45", lighthouse.Models);
        Assert.True(lighthouse.CanOpenJob);
        Assert.Equal("job-owner-2", lighthouse.OriginJobId);
    }

    [Fact]
    public void LiveRecordSearchRandomAndDetailsWorkEndToEnd()
    {
        using var tmp = new TempArchiveDir();
        for (int i = 1; i <= 15; i++)
        {
            WriteJobJson(
                tmp.Root,
                $"job-{i:D2}",
                $"Sunlit botanical watercolor study number {i} featuring alpine wildflowers and crisp ink labels",
                "ernieMultiZone",
                new DateTime(2024 + (i % 3), 1 + (i % 12), 10, 12, 0, 0, DateTimeKind.Utc),
                i % 2 == 0 ? "gpt2" : "ideogram-v45");
        }
        WriteJobJson(tmp.Root, "job-video", "Gentle morning breeze rippling tall grass in golden sunlight", "ernieMultiZone", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), "grok-web-video");

        var archive = new UiPromptArchive(tmp.DbPath, "local");
        archive.SyncFromInstance(tmp.Root, allowEmptyCreator: true);

        // Live job insert updates FTS5 and stats immediately
        var liveJob = new UiJob
        {
            Id = "job-live-99",
            Prompt = "Architectural cutaway diagram of a bright Scandinavian wooden observatory",
            CreatorLogin = "ernieMultiZone",
            CreatedAt = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc),
            GeneratorKeys = new[] { "gpt26-sunburst" },
        };
        Assert.True(archive.TryRecordLiveJob(liveJob, allowEmptyCreator: true));

        // Non-owner live job is silently skipped
        var otherJob = new UiJob
        {
            Id = "job-live-other",
            Prompt = "Must not enter owner archive",
            CreatorLogin = "dallasVille",
            CreatedAt = new DateTime(2026, 11, 20, 18, 5, 0, DateTimeKind.Utc),
            GeneratorKeys = new[] { "gpt2" },
        };
        Assert.False(archive.TryRecordLiveJob(otherJob, allowEmptyCreator: true));

        // FTS5 search finds live-recorded prompt
        var fts = archive.Search(new UiPromptArchiveFilter(Query: "Scandinavian observatory"), "relevance", 1, 10);
        Assert.Equal(1, fts.TotalMatching);
        Assert.Equal("job-live-99", fts.Items[0].OriginJobId);

        // Kind filter separates video from image
        var videos = archive.Search(new UiPromptArchiveFilter(Kind: "video"), "newest", 1, 10);
        Assert.Equal(1, videos.TotalMatching);
        Assert.Equal("video", videos.Items[0].Kind);

        // Multi-source filter accepts comma-separated IDs without duplicates
        var multiSrc = archive.Search(new UiPromptArchiveFilter(Source: "mic-local,mic-ave"), "newest", 1, 50);
        Assert.Equal(17, multiSrc.TotalMatching); // 15 botanical + 1 video + 1 live observatory
        var multiSrcImages = archive.Search(new UiPromptArchiveFilter(Source: "mic-local,mic-ave", Kind: "image"), "newest", 1, 50);
        Assert.Equal(16, multiSrcImages.TotalMatching);

        // Random returns exact count and respects excludeIds
        var r1 = archive.GetRandom(new UiPromptArchiveFilter(Kind: "image"), 10, Array.Empty<long>());
        Assert.True(r1.Exists);
        Assert.Equal(16, r1.MatchingCount);
        Assert.Equal(10, r1.Items.Count);
        Assert.Equal(10, r1.Items.Select(x => x.Id).Distinct().Count());

        var r2 = archive.GetRandom(new UiPromptArchiveFilter(Kind: "image"), 10, r1.Items.Select(x => x.Id).ToArray());
        Assert.Equal(6, r2.Items.Count); // 6 unseen image prompts remain
        Assert.Empty(r1.Items.Select(x => x.Id).Intersect(r2.Items.Select(x => x.Id)));

        // Excluding all matching IDs wraps cleanly with ExhaustedSeen = true
        var allSeen = r1.Items.Concat(r2.Items).Select(x => x.Id).ToArray();
        var r3 = archive.GetRandom(new UiPromptArchiveFilter(Kind: "image"), 5, allSeen);
        Assert.True(r3.ExhaustedSeen);
        Assert.Equal(5, r3.Items.Count);

        // Item detail returns occurrence metadata and CanOpenJob reflects current instance
        var detail = archive.GetItem(fts.Items[0].Id);
        Assert.NotNull(detail);
        Assert.Equal(1, detail!.TotalOccurrences);
        Assert.True(detail.Occurrences[0].CanOpenJob);

        // Opening the same DB from another instance disables CanOpenJob for local jobs
        var otherInstanceArchive = new UiPromptArchive(tmp.DbPath, "vibecoders");
        var detailOther = otherInstanceArchive.GetItem(fts.Items[0].Id);
        Assert.NotNull(detailOther);
        Assert.False(detailOther!.Item.CanOpenJob);
        Assert.False(detailOther.Occurrences[0].CanOpenJob);

        // Invalid parameters fail closed
        Assert.Throws<InvalidDataException>(() => archive.GetRandom(new UiPromptArchiveFilter(), 0, Array.Empty<long>()));
        Assert.Throws<InvalidDataException>(() => archive.GetRandom(new UiPromptArchiveFilter(), 51, Array.Empty<long>()));
        Assert.Throws<InvalidDataException>(() => archive.Search(new UiPromptArchiveFilter(MinChars: 200, MaxChars: 50), "newest", 1, 10));
        Assert.Throws<InvalidDataException>(() => archive.Search(new UiPromptArchiveFilter(), "invalid_sort", 1, 10));
    }

    [Fact]
    public void AdminGateAllowsOnlyOwnerWhenAuthEnforced()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), "mic-auth-pa-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var salt = Enumerable.Range(0, 16).Select(v => (byte)v).ToArray();
            var hash = Rfc2898DeriveBytes.Pbkdf2("test-passphrase-1234", salt, UiAuth.Pbkdf2Iterations, HashAlgorithmName.SHA256, UiAuth.Pbkdf2HashBytes);
            var json = JsonSerializer.Serialize(new
            {
                version = 2,
                enabled = true,
                secret = Convert.ToHexString(Enumerable.Range(0, 32).Select(v => (byte)(v + 1)).ToArray()),
                accounts = new[]
                {
                    new
                    {
                        username = UiLoginLinks.OwnerLogin,
                        passwordHash = $"{UiAuth.PasswordHashPrefix}${UiAuth.Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}",
                    },
                },
            });
            File.WriteAllText(tmpFile, json);
            var auth = UiAuth.CreateFromSettings(new Settings { UiAuthFilePath = tmpFile });
            Assert.NotNull(auth);

            // Auth null (local open mode) -> allowed
            var localCtx = new DefaultHttpContext();
            Assert.True(UiWorkflow.IsPromptArchiveAdmin(localCtx, null));

            // Auth enforced + owner -> allowed
            var ownerCtx = new DefaultHttpContext();
            ownerCtx.Items["micUser"] = UiLoginLinks.OwnerLogin;
            Assert.True(UiWorkflow.IsPromptArchiveAdmin(ownerCtx, auth));

            // Auth enforced + normal member -> denied
            var memberCtx = new DefaultHttpContext();
            memberCtx.Items["micUser"] = "dallasVille";
            Assert.False(UiWorkflow.IsPromptArchiveAdmin(memberCtx, auth));

            // Auth enforced + anonymous/empty -> denied
            var anonCtx = new DefaultHttpContext();
            Assert.False(UiWorkflow.IsPromptArchiveAdmin(anonCtx, auth));
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }
}
