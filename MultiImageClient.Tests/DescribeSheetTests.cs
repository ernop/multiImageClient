using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MultiImageClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MultiImageClientTests
{
    public class DescribeSheetTests
    {
        private static string Reply(string key, params object[] texts) => JsonSerializer.Serialize(new
        {
            type = "gen-result", gen = key, label = "Recorded model v1", ok = true, texts,
        });

        [Fact]
        public void PreservesHistoricalModelsFullTextCommentsAndExactInputIndexes()
        {
            var key = UiJobRunner.KeyDescribeOpenAi;
            var events = new[]
            {
                JsonSerializer.Serialize(new { type = "accepted", generatorExtraTexts = new Dictionary<string, string> { [key] = "Read all signs.\nKeep accents." } }),
                Reply(key, new { inputIndex = 1, text = "Second input.", comments = "" },
                    new { inputIndex = 0, text = "First paragraph.\n\nSecond paragraph — café.", comments = "Uncertain lettering." }),
            };
            var data = UiDescribeSheet.Read("Exact\ninput text", 2, new[] { key, UiJobRunner.KeyGpt2 }, events);
            var endpoint = Assert.Single(data.Endpoints);
            Assert.Equal("Recorded model v1", endpoint.Label);
            Assert.Equal("Read all signs.\nKeep accents.", endpoint.ExtraText);
            Assert.Equal("Exact\ninput text", data.Prompt);
            Assert.Equal("First paragraph.\n\nSecond paragraph — café.", endpoint.Replies.Single(x => x.InputIndex == 0).Text);
            Assert.Equal("Uncertain lettering.", endpoint.Replies.Single(x => x.InputIndex == 0).Comments);
        }

        [Fact]
        public void RejectsIncompleteAmbiguousAndUncorrelatedResults()
        {
            var key = UiJobRunner.KeyDescribeOpenAi;
            var entry = new { inputIndex = 0, text = "One image.", comments = "" };
            var evt = Reply(key, entry);
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 1, new[] { key }, Array.Empty<string>()));
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 1, new[] { key }, new[] { evt, evt }));
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 2, new[] { key }, new[] { evt }));
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 2, new[] { key }, new[] { Reply(key, entry, entry) }));
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 1, new[] { key }, new[] { Reply(key, new { inputIndex = 1, text = "Wrong input" }) }));
        }

        [Fact]
        public void KeepsProviderFailuresVisibleAndRequiresEveryLayoutMap()
        {
            var key = UiJobRunner.KeyDescribeClaude;
            var failed = JsonSerializer.Serialize(new { type = "gen-result", gen = key, label = "Claude", ok = false, error = "Provider refused this request." });
            var data = UiDescribeSheet.Read("x", 1, new[] { key }, new[] { failed });
            Assert.Equal("Provider refused this request.", data.Endpoints[0].Error);
            Assert.Empty(data.Endpoints[0].Replies);
            var map = JsonSerializer.Serialize(new { type = "gen-result", gen = "layout-map", label = "Recorded Gemini layout map", ok = true, images = new[] { "/api/jobs/exact/images/layout-map/0" } });
            Assert.Single(UiDescribeSheet.Read("x", 1, new[] { "layout-map" }, new[] { map }).Endpoints);
            Assert.Throws<InvalidDataException>(() => UiDescribeSheet.Read("x", 2, new[] { "layout-map" }, new[] { map }));
        }

        [Fact]
        public async Task RendersAllInputsAndMapsWithoutCroppingAndGrowsForCompleteText()
        {
            var folder = Path.Combine(Path.GetTempPath(), "describe-sheet-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var inputPath = Path.Combine(folder, "input.png");
                var mapPath = Path.Combine(folder, "map.png");
                using (var input = new Image<Rgba32>(180, 90, Color.Red)) await input.SaveAsPngAsync(inputPath);
                using (var map = new Image<Rgba32>(100, 180, Color.Green)) await map.SaveAsPngAsync(mapPath);
                var endpoints = new[]
                {
                    new UiDescribeSheetEndpoint("describe-openai", "Recorded OpenAI", "", "", new[] {
                        new UiDescribeSheetReply(0, "Paragraph one.\n\nParagraph two with gypj descenders.", "Model comment."),
                        new UiDescribeSheetReply(1, "Distinct second input description.", "") }),
                    new UiDescribeSheetEndpoint("layout-map", "Recorded Gemini layout map", "", "", Array.Empty<UiDescribeSheetReply>()),
                };
                var data = new UiDescribeSheetData("Input instruction", 2, endpoints);
                var paths = new[] { inputPath, inputPath };
                var maps = new Dictionary<int, string> { [0] = mapPath, [1] = mapPath };
                var output = Path.Combine(folder, "sheet.png");
                await UiDescribeSheet.RenderAsync(data, paths, maps, output);
                using var sheet = Image.Load<Rgba32>(output);
                Assert.Equal(1800, sheet.Width);
                Assert.True(sheet.Height > 3000);
                var redRows = new HashSet<int>();
                var greenRows = new HashSet<int>();
                sheet.ProcessPixelRows(accessor => {
                    for (var y = 0; y < accessor.Height; y++)
                        foreach (var pixel in accessor.GetRowSpan(y))
                        {
                            if (pixel == Color.Red.ToPixel<Rgba32>()) redRows.Add(y);
                            if (pixel == Color.Green.ToPixel<Rgba32>()) greenRows.Add(y);
                        }
                });
                Assert.True(redRows.Count > 600);
                Assert.True(greenRows.Count > 2000);
                var longReply = new UiDescribeSheetReply(0, string.Join("\n", Enumerable.Repeat("Full returned text, without shortening.", 120)), "Last comment.");
                var longData = data with { InputCount = 1, Endpoints = new[] { endpoints[0] with { Replies = new[] { longReply } } } };
                await UiDescribeSheet.RenderAsync(longData, new[] { inputPath }, new Dictionary<int, string>(), output);
                Assert.True(Image.Identify(output).Height > sheet.Height);
                var oversized = longData with { Endpoints = new[] { endpoints[0] with { Replies = new[] { longReply with { Text = new string('x', 100_001) } } } } };
                await Assert.ThrowsAsync<InvalidDataException>(() => UiDescribeSheet.RenderAsync(oversized, new[] { inputPath }, maps, output));
            }
            finally { Directory.Delete(folder, recursive: true); }
        }
    }
}
