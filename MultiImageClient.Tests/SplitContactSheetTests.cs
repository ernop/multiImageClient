using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MultiImageClient;
using SkiaSharp;
using Xunit;

namespace MultiImageClientTests
{
    public class SplitContactSheetTests
    {
        private static UiJob BuildInMemorySplitPart(
            string jobId,
            string groupId,
            int index,
            int count,
            string title,
            string prompt,
            string[] gens,
            int inputCount = 0,
            string exchangeId = "ex-split-1",
            string model = PromptSplit.ModelKey)
        {
            var inputs = Enumerable.Range(0, inputCount).Select(i => $"/tmp/input-{i}.png").ToArray();
            return new UiJob
            {
                Id = jobId,
                Prompt = prompt,
                GeneratorKeys = gens,
                InputImagePaths = inputs,
                Split = new UiJobSplit(
                    groupId,
                    exchangeId,
                    index,
                    count,
                    title,
                    model,
                    "Separate visual moments keep each composition readable."),
            };
        }

        private static string AcceptedEvent(
            string jobId,
            string prompt,
            string[] gens,
            Dictionary<string, string>? extraTexts = null)
            => JsonSerializer.Serialize(new
            {
                type = "accepted",
                jobId,
                prompt,
                gens,
                generatorExtraTexts = extraTexts ?? new Dictionary<string, string>(),
            });

        private static string GenResultOk(
            string gen,
            string label,
            int imageCount,
            string size = "1024x1024",
            int ms = 12400)
            => JsonSerializer.Serialize(new
            {
                type = "gen-result",
                gen,
                label,
                ok = true,
                resultKind = "image",
                ms,
                size,
                images = Enumerable.Range(0, imageCount).Select(i => $"/api/jobs/x/images/{gen}/{i}").ToArray(),
            });

        private static string GenResultFail(string gen, string label, string error, int ms = 2100)
            => JsonSerializer.Serialize(new
            {
                type = "gen-result",
                gen,
                label,
                ok = false,
                resultKind = "image",
                ms,
                error,
                images = Array.Empty<string>(),
            });

        [Fact]
        public void ReadPreservesOrderExtraTextErrorsAndRejectsInvalidGroups()
        {
            var gens = new[] { UiJobRunner.KeyGpt2, UiJobRunner.KeyGrokWeb };
            var extras = new Dictionary<string, string> { [UiJobRunner.KeyGpt2] = "Bright daylight." };

            var part2 = BuildInMemorySplitPart("job-p2", "grp-1", 2, 2, "The workshop", "Prompt for page two.", gens);
            var part1 = BuildInMemorySplitPart("job-p1", "grp-1", 1, 2, "The arrival", "Prompt for page one.", gens);

            var eventsByJob = new Dictionary<string, (List<string> Events, bool Done)>
            {
                ["job-p1"] = (new List<string>
                {
                    AcceptedEvent("job-p1", "Prompt for page one.", gens, extras),
                    GenResultOk(UiJobRunner.KeyGpt2, "GPT Image 2", 1, "1024x1024", 11800),
                    GenResultOk(UiJobRunner.KeyGrokWeb, "grok-web", 2, "768x1024", 6400),
                }, true),
                ["job-p2"] = (new List<string>
                {
                    AcceptedEvent("job-p2", "Prompt for page two.", gens, extras),
                    GenResultOk(UiJobRunner.KeyGpt2, "GPT Image 2", 1, "1024x1024", 15200),
                    GenResultFail(UiJobRunner.KeyGrokWeb, "grok-web", "Rate limited by provider.", 1800),
                }, true),
            };

            // Passing parts out of order still returns pages 1..2 in exact order
            var data = UiSplitContactSheet.Read(
                new[] { part2, part1 },
                job => eventsByJob[job.Id],
                _ => "",
                "One big story spanning arrival and workshop.",
                "Crisp ink and watercolor.");

            Assert.Equal("grp-1", data.GroupId);
            Assert.Equal("One big story spanning arrival and workshop.", data.SourcePrompt);
            Assert.Equal("Crisp ink and watercolor.", data.StyleNote);
            Assert.Equal(new[] { UiJobRunner.KeyGpt2, UiJobRunner.KeyGrokWeb }, data.GeneratorKeys);
            Assert.Equal("Bright daylight.", data.GeneratorExtraTexts[UiJobRunner.KeyGpt2]);
            Assert.Equal("GPT Image 2", data.GeneratorLabels[UiJobRunner.KeyGpt2]);

            Assert.Equal(2, data.Parts.Count);
            Assert.Equal(1, data.Parts[0].Index);
            Assert.Equal("The arrival", data.Parts[0].Title);
            Assert.Equal(2, data.Parts[0].Cells.Single(c => c.GeneratorKey == UiJobRunner.KeyGrokWeb).ImageCount);

            Assert.Equal(2, data.Parts[1].Index);
            var failedCell = data.Parts[1].Cells.Single(c => c.GeneratorKey == UiJobRunner.KeyGrokWeb);
            Assert.False(failedCell.Ok);
            Assert.Equal("Rate limited by provider.", failedCell.Error);

            // Generator filter keeps only the requested generator
            var filtered = UiSplitContactSheet.Read(
                new[] { part1, part2 },
                job => eventsByJob[job.Id],
                _ => "",
                "Source",
                "",
                filterGeneratorKey: UiJobRunner.KeyGpt2);
            Assert.Equal(new[] { UiJobRunner.KeyGpt2 }, filtered.GeneratorKeys);
            Assert.All(filtered.Parts, p => Assert.Single(p.Cells));

            // Unknown generator filter fails closed
            Assert.Throws<ArgumentException>(() =>
                UiSplitContactSheet.Read(
                    new[] { part1, part2 },
                    job => eventsByJob[job.Id],
                    _ => "",
                    "",
                    "",
                    filterGeneratorKey: "ideogram"));

            // Missing part fails closed
            Assert.Throws<InvalidDataException>(() =>
                UiSplitContactSheet.Read(
                    new[] { part1 },
                    job => eventsByJob[job.Id],
                    _ => "",
                    "",
                    ""));

            // Unfinished part fails closed with InvalidOperationException (HTTP 409)
            Assert.Throws<InvalidOperationException>(() =>
                UiSplitContactSheet.Read(
                    new[] { part1, part2 },
                    job => job.Id == "job-p2" ? (eventsByJob[job.Id].Events, false) : eventsByJob[job.Id],
                    _ => "",
                    "",
                    ""));

            // Duplicate gen-result fails closed
            Assert.Throws<InvalidDataException>(() =>
                UiSplitContactSheet.Read(
                    new[] { part1, part2 },
                    job => job.Id == "job-p1"
                        ? (new List<string>
                        {
                            AcceptedEvent("job-p1", "Prompt for page one.", gens),
                            GenResultOk(UiJobRunner.KeyGpt2, "GPT Image 2", 1),
                            GenResultOk(UiJobRunner.KeyGpt2, "GPT Image 2", 1),
                            GenResultOk(UiJobRunner.KeyGrokWeb, "grok-web", 1),
                        }, true)
                        : eventsByJob[job.Id],
                    _ => "",
                    "",
                    ""));
        }

        [Fact]
        public async Task RendersMultiModelAndSingleModelSheetsWithAndWithoutFullText()
        {
            var folder = Path.Combine(Path.GetTempPath(), "split-sheet-render-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(folder);
            try
            {
                var imgRed = Path.Combine(folder, "red.png");
                var imgBlue = Path.Combine(folder, "blue.png");
                var imgGreen = Path.Combine(folder, "green.png");
                var imgYellow = Path.Combine(folder, "yellow.png");
                var imgInput = Path.Combine(folder, "input.png");

                using (var b = Raster.Create(320, 240, SKColors.Red)) Raster.SavePng(b, imgRed);
                using (var b = Raster.Create(240, 320, SKColors.Blue)) Raster.SavePng(b, imgBlue);
                using (var b = Raster.Create(320, 320, SKColors.Green)) Raster.SavePng(b, imgGreen);
                using (var b = Raster.Create(320, 200, SKColors.Yellow)) Raster.SavePng(b, imgYellow);
                using (var b = Raster.Create(200, 200, SKColors.Magenta)) Raster.SavePng(b, imgInput);

                var genKeys = new[] { UiJobRunner.KeyGpt2, UiJobRunner.KeyGrokWeb };
                var genLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UiJobRunner.KeyGpt2] = "GPT Image 2",
                    [UiJobRunner.KeyGrokWeb] = "grok-web",
                };
                var genExtras = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UiJobRunner.KeyGpt2] = "Bright daylight lighting.",
                };
                var parts = new[]
                {
                    new UiSplitContactSheetPart(
                        1, 2, "job-1", "Morning street",
                        "A sunlit bakery street with brass lettering and fresh bread in the window.",
                        new[]
                        {
                            new UiSplitContactSheetCell(UiJobRunner.KeyGpt2, 1, true, "GPT Image 2", "", 14200, "320x240", 1),
                            new UiSplitContactSheetCell(UiJobRunner.KeyGrokWeb, 1, true, "grok-web", "", 5100, "240x320", 2),
                        }),
                    new UiSplitContactSheetPart(
                        2, 2, "job-2", "Inside the bakery",
                        "Warm interior showing stacked sourdough loaves, chalk price board, and bakers.",
                        new[]
                        {
                            new UiSplitContactSheetCell(UiJobRunner.KeyGpt2, 2, true, "GPT Image 2", "", 16000, "320x200", 1),
                            new UiSplitContactSheetCell(UiJobRunner.KeyGrokWeb, 2, false, "grok-web", "Safety filter refused this prompt.", 1800, "", 0),
                        }),
                };
                var data = new UiSplitContactSheetData(
                    "grp-render", "ex-1", "Claude Sonnet 5.5",
                    "Two distinct scenes work best as two frames.",
                    "A two-part story about a neighborhood bakery at dawn.",
                    "Editorial illustration.",
                    1,
                    "GPT Image 2: edit source; grok-web: edit source",
                    genKeys,
                    genLabels,
                    genExtras,
                    parts);

                var cellPaths = new Dictionary<(int PartIndex, string GeneratorKey), IReadOnlyList<string>>
                {
                    [(1, UiJobRunner.KeyGpt2)] = new[] { imgRed },
                    [(1, UiJobRunner.KeyGrokWeb)] = new[] { imgBlue, imgGreen },
                    [(2, UiJobRunner.KeyGpt2)] = new[] { imgYellow },
                };
                var inputPaths = new[] { imgInput };

                var outNoText = Path.Combine(folder, "multi-notext.png");
                var outFullText = Path.Combine(folder, "multi-fulltext.png");
                await UiSplitContactSheet.RenderAsync(data, inputPaths, cellPaths, includeText: false, outNoText);
                await UiSplitContactSheet.RenderAsync(data, inputPaths, cellPaths, includeText: true, outFullText);

                using var bmNoText = Raster.Decode(outNoText);
                using var bmFullText = Raster.Decode(outFullText);
                Assert.True(bmNoText.Width >= 900);
                Assert.True(bmFullText.Height > bmNoText.Height + 120);
                Assert.True((long)bmFullText.Width * bmFullText.Height <= 32_000_000L);

                // Every source image color plus input magenta must appear on the rendered sheet
                var colors = new HashSet<SKColor>(bmFullText.Pixels);
                Assert.Contains(SKColors.Red, colors);
                Assert.Contains(SKColors.Blue, colors);
                Assert.Contains(SKColors.Green, colors);
                Assert.Contains(SKColors.Yellow, colors);
                Assert.Contains(SKColors.Magenta, colors);

                // Single-generator balanced 2-row grid (5 pages -> 3 + 2)
                var singleKey = new[] { UiJobRunner.KeyGpt2 };
                var singleLabels = new Dictionary<string, string>(StringComparer.Ordinal) { [UiJobRunner.KeyGpt2] = "GPT Image 2" };
                var fiveParts = Enumerable.Range(1, 5).Select(i =>
                    new UiSplitContactSheetPart(
                        i, 5, $"s-{i}", $"Scene {i}",
                        $"Detailed scene {i} with crisp composition and clear subjects.",
                        new[]
                        {
                            new UiSplitContactSheetCell(UiJobRunner.KeyGpt2, i, true, "GPT Image 2", "", 10000 + i * 1000, "320x240", 1),
                        })).ToArray();
                var singleData = new UiSplitContactSheetData(
                    "grp-single", "ex-2", "Claude Sonnet 5.5", "Five pages.",
                    "Source story.", "", 0, "", singleKey, singleLabels,
                    new Dictionary<string, string>(StringComparer.Ordinal), fiveParts);
                var singlePaths = fiveParts.ToDictionary(
                    p => (p.Index, UiJobRunner.KeyGpt2),
                    _ => (IReadOnlyList<string>)new[] { imgRed });

                var outSingleNoText = Path.Combine(folder, "single-notext.png");
                var outSingleFullText = Path.Combine(folder, "single-fulltext.png");
                await UiSplitContactSheet.RenderAsync(singleData, Array.Empty<string>(), singlePaths, includeText: false, outSingleNoText);
                await UiSplitContactSheet.RenderAsync(singleData, Array.Empty<string>(), singlePaths, includeText: true, outSingleFullText);

                var singleNoInfo = Raster.Identify(outSingleNoText);
                var singleFullInfo = Raster.Identify(outSingleFullText);
                Assert.True(singleNoInfo.Width >= 1000);
                Assert.True(singleFullInfo.Height > singleNoInfo.Height + 120);

                // Oversized text fails closed instead of overflowing 32 MP
                var hugeParts = fiveParts.Select((p, idx) => idx == 0
                    ? p with { Prompt = new string('W', 180_000) }
                    : p).ToArray();
                var hugeData = singleData with { Parts = hugeParts };
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    UiSplitContactSheet.RenderAsync(hugeData, Array.Empty<string>(), singlePaths, includeText: true, Path.Combine(folder, "huge.png")));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void RegistryListsArchivedSplitGroupsAndUpgradesV1IndexCache()
        {
            var root = Path.Combine(Path.GetTempPath(), "split-cache-v2-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(root);
            try
            {
                var historyRoot = Path.Combine(root, "UiHistory");
                Directory.CreateDirectory(historyRoot);
                var oldDay = DateTime.UtcNow.AddDays(-6);
                var gens = new[] { UiJobRunner.KeyGpt2 };

                void WriteArchivedSplitPart(string id, int index, string title, string prompt)
                {
                    var dir = Path.Combine(historyRoot, id);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(
                        Path.Combine(dir, "job.json"),
                        JsonSerializer.Serialize(new
                        {
                            Id = id,
                            Prompt = prompt,
                            CreatedAt = oldDay.AddSeconds(index),
                            CreatedBy = "owner",
                            CreatorLogin = "owner",
                            GeneratorKeys = gens,
                            Done = true,
                            Split = new
                            {
                                GroupId = "grp-arch-99",
                                ExchangeId = "ex-arch-1",
                                Index = index,
                                Count = 2,
                                Title = title,
                                Model = PromptSplit.ModelKey,
                                Reason = "Two separate moments.",
                            },
                        }));
                    File.WriteAllLines(
                        Path.Combine(dir, "events.jsonl"),
                        new[]
                        {
                            AcceptedEvent(id, prompt, gens),
                            GenResultOk(UiJobRunner.KeyGpt2, "GPT Image 2", 1),
                            JsonSerializer.Serialize(new { type = "job-done", jobId = id }),
                        });
                }

                WriteArchivedSplitPart("arch-p1", 1, "Part one", "First archived page.");
                WriteArchivedSplitPart("arch-p2", 2, "Part two", "Second archived page.");

                // Seed a synthetic v1 SQLite cache (schema_version = 1, no split_group_id column)
                GenerationArchive.EnsureSqliteAvailable();
                var cachePath = UiHistoryIndexCache.CachePath(historyRoot);
                using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = cachePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString()))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
INSERT INTO meta (key, value) VALUES ('schema_version', '1');
CREATE TABLE history_index (
  folder_name TEXT PRIMARY KEY,
  id TEXT NOT NULL,
  created_at_ticks INTEGER NOT NULL,
  created_by TEXT NOT NULL,
  creator_login TEXT NOT NULL,
  done INTEGER NOT NULL,
  job_json_mtime_ticks INTEGER NOT NULL,
  job_json_size INTEGER NOT NULL
);";
                    cmd.ExecuteNonQuery();
                }

                var settings = new Settings
                {
                    ImageDownloadBaseFolder = root,
                    EnableGenerationArchive = false,
                };

                // Startup detects v1 schema, resets to v2, indexes SplitGroupId from disk
                var reg1 = new UiJobRegistry(settings);
                var found1 = reg1.ListBySplitGroup("grp-arch-99");
                Assert.Equal(2, found1.Count);
                Assert.Equal(new[] { "arch-p1", "arch-p2" }, found1.Select(j => j.Id).ToArray());

                // Second startup reads warm v2 SQLite cache and still resolves the archived split group
                var reg2 = new UiJobRegistry(settings);
                var found2 = reg2.ListBySplitGroup("grp-arch-99");
                Assert.Equal(2, found2.Count);
                Assert.All(found2, j => Assert.Equal("grp-arch-99", j.Split?.GroupId));
                Assert.Empty(reg2.ListBySplitGroup("does-not-exist"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
