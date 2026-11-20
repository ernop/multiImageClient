using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class ArchitectureRefactorTests
    {
        [Fact]
        public void TargetCatalogCoversEveryUiJobRunnerKeyAndStaysConsistent()
        {
            var runnerKeyConstants = typeof(UiJobRunner)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string) && f.Name.StartsWith("Key", StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();

            Assert.NotEmpty(runnerKeyConstants);
            Assert.Equal(UiTargetCatalog.All.Count, UiTargetCatalog.All.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count());

            foreach (var key in runnerKeyConstants)
            {
                var descriptor = UiTargetCatalog.Find(key);
                Assert.NotNull(descriptor);
                Assert.False(string.IsNullOrWhiteSpace(descriptor!.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.ContactSheetProvider));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.SchedulerLane));
                Assert.Equal(descriptor.DisplayName, GeneratorPresentation.UiDisplayName(key));
                if (descriptor.ProviderAccountKey != null)
                {
                    var hint = ProviderActionHints.For(key, "HTTP 402 insufficient credits");
                    Assert.NotNull(hint);
                }
            }

            // meta-web and grok-web-video stay outside /api/config's interactive generator catalog
            Assert.DoesNotContain(UiTargetCatalog.UiConfigTargets, t => t.Key == UiJobRunner.KeyMetaWeb);
            Assert.DoesNotContain(UiTargetCatalog.UiConfigTargets, t => t.Key == UiJobRunner.KeyGrokWebVideo);

            // Every standard group references valid image generators in the catalog
            Assert.NotEmpty(UiTargetCatalog.StandardGroups);
            foreach (var group in UiTargetCatalog.StandardGroups)
            {
                Assert.False(string.IsNullOrWhiteSpace(group.Id));
                Assert.False(string.IsNullOrWhiteSpace(group.Name));
                Assert.NotEmpty(group.GeneratorKeys);
                foreach (var key in group.GeneratorKeys)
                {
                    var d = UiTargetCatalog.Find(key);
                    Assert.NotNull(d);
                    Assert.Equal(UiTargetKind.Image, d!.Kind);
                    Assert.True(d.InUiConfig);
                }
            }
        }

        [Fact]
        public void HistoryIndexCacheColdBuildWarmHitAndRevalidationWorkFailClosed()
        {
            var root = Path.Combine(Path.GetTempPath(), "mic-index-cache-test-" + Guid.NewGuid().ToString("N")[..10]);
            var historyRoot = Path.Combine(root, "UiHistory");
            Directory.CreateDirectory(historyRoot);
            try
            {
                var localToday = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Local);
                var archivedDay = new DateTime(2026, 11, 10, 14, 0, 0, DateTimeKind.Utc);
                var todayTime = new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc);

                WriteFakeJobFolder(historyRoot, "job-archived-done", "archived prompt", archivedDay, done: true, creatorLogin: "alice");
                WriteFakeJobFolder(historyRoot, "job-archived-open", "unfinished prompt", archivedDay, done: false, creatorLogin: "bob");
                WriteFakeJobFolder(historyRoot, "job-today-done", "today prompt", todayTime, done: true, creatorLogin: "alice");

                // 1. Cold scan reads all 3 folders from disk and creates SQLite cache
                var first = UiHistoryIndexCache.Scan(historyRoot, ReadFakeJobFromDisk, localToday, out var hits1, out var reads1);
                Assert.Equal(0, hits1);
                Assert.Equal(3, reads1);
                Assert.Equal(3, first.Count);
                Assert.True(File.Exists(UiHistoryIndexCache.CachePath(historyRoot)));

                // 2. Warm scan serves completed archived job from cache, re-reads today + unfinished from disk
                var second = UiHistoryIndexCache.Scan(historyRoot, ReadFakeJobFromDisk, localToday, out var hits2, out var reads2);
                Assert.Equal(1, hits2);
                Assert.Equal(2, reads2);
                Assert.Equal(3, second.Count);
                var cachedArchived = second.Single(e => e.Id == "job-archived-done");
                Assert.Equal("alice", cachedArchived.CreatorLogin);
                Assert.True(cachedArchived.Done);

                // 3. New folder picked up and deleted folder pruned cleanly
                WriteFakeJobFolder(historyRoot, "job-archived-new", "new archived prompt", archivedDay.AddDays(-1), done: true, creatorLogin: "carol");
                Directory.Delete(Path.Combine(historyRoot, "job-archived-open"), recursive: true);

                var third = UiHistoryIndexCache.Scan(historyRoot, ReadFakeJobFromDisk, localToday, out var hits3, out var reads3);
                Assert.Equal(1, hits3);  // job-archived-done
                Assert.Equal(2, reads3); // job-archived-new + job-today-done
                Assert.Equal(3, third.Count);
                Assert.DoesNotContain(third, e => e.Id == "job-archived-open");
                Assert.Contains(third, e => e.Id == "job-archived-new" && e.CreatorLogin == "carol");

                // 4. Upsert updates cached row immediately (e.g. creator-login transfer)
                UiHistoryIndexCache.Upsert(historyRoot, new UiHistoryIndexEntry
                {
                    FolderName = "job-archived-done",
                    Id = "job-archived-done",
                    CreatedAt = archivedDay,
                    CreatedBy = "Alice Renamed",
                    CreatorLogin = "alice_new",
                    Done = true,
                });
                var fourth = UiHistoryIndexCache.Scan(historyRoot, ReadFakeJobFromDisk, localToday, out var hits4, out var reads4);
                Assert.Equal(2, hits4);  // job-archived-done + job-archived-new
                Assert.Equal(1, reads4); // job-today-done
                Assert.Equal("alice_new", fourth.Single(e => e.Id == "job-archived-done").CreatorLogin);

                // 5. Corrupt SQLite file fails closed by deleting & rebuilding from disk job.json
                File.WriteAllText(UiHistoryIndexCache.CachePath(historyRoot), "not a valid sqlite database!");
                var fifth = UiHistoryIndexCache.Scan(historyRoot, ReadFakeJobFromDisk, localToday, out var hits5, out var reads5);
                Assert.Equal(0, hits5);
                Assert.Equal(3, reads5);
                Assert.Equal(3, fifth.Count);
                Assert.Equal("alice", fifth.Single(e => e.Id == "job-archived-done").CreatorLogin);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void UiJobRegistryUsesAndMaintainsHistoryIndexCacheAcrossRestarts()
        {
            var root = Path.Combine(Path.GetTempPath(), "mic-registry-cache-test-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(root);
            try
            {
                var settings = new Settings
                {
                    ImageDownloadBaseFolder = root,
                    EnableGenerationArchive = false,
                };
                var historyRoot = Path.Combine(root, "UiHistory");
                Directory.CreateDirectory(historyRoot);
                var oldDate = DateTime.UtcNow.AddDays(-5);
                WriteFakeJobFolder(historyRoot, "hist-001", "historic scene", oldDate, done: true, creatorLogin: "olduser");

                var reg1 = new UiJobRegistry(settings);
                Assert.True(File.Exists(UiHistoryIndexCache.CachePath(historyRoot)));
                Assert.Single(reg1.ListArchivedDays());

                // RewriteCreatorLogin persists both job.json and cache
                reg1.RewriteCreatorLogin("olduser", "newuser");
                var reg2 = new UiJobRegistry(settings);
                var attrs = reg2.ListAuthenticatedAttributions();
                Assert.Contains(attrs, a => a.CreatorLogin == "newuser");
                Assert.DoesNotContain(attrs, a => a.CreatorLogin == "olduser");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static void WriteFakeJobFolder(
            string historyRoot,
            string id,
            string prompt,
            DateTime createdAtUtc,
            bool done,
            string creatorLogin)
        {
            var folder = Path.Combine(historyRoot, id);
            Directory.CreateDirectory(folder);
            var json = JsonSerializer.Serialize(new
            {
                Id = id,
                Prompt = prompt,
                CreatedBy = creatorLogin,
                CreatorLogin = creatorLogin,
                GeneratorKeys = new[] { UiJobRunner.KeyGpt2 },
                CreatedAt = createdAtUtc,
                Done = done,
            });
            File.WriteAllText(Path.Combine(folder, "job.json"), json);
        }

        private static UiHistoryIndexEntry? ReadFakeJobFromDisk(string folder)
        {
            var path = Path.Combine(folder, "job.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new UiHistoryIndexEntry
            {
                FolderName = Path.GetFileName(folder),
                Id = root.GetProperty("Id").GetString()!,
                CreatedAt = root.GetProperty("CreatedAt").GetDateTime(),
                CreatedBy = root.GetProperty("CreatedBy").GetString() ?? "",
                CreatorLogin = root.GetProperty("CreatorLogin").GetString() ?? "",
                Done = root.GetProperty("Done").GetBoolean(),
            };
        }
    }
}
