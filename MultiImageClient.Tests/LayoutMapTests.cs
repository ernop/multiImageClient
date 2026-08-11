using System.IO;
using System.Linq;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public class LayoutMapTests
    {
        [Fact]
        public void ParsesConformingReply()
        {
            var raw = "{\"summary\": \"A planet hangs over a small house.\", \"regions\": ["
                + "{\"label\": \"sky\", \"box\": [0, 0, 400, 1000]},"
                + "{\"label\": \"red planet\", \"box\": [50, 600, 350, 950]},"
                + "{\"label\": \"house\", \"box\": [500, 100, 900, 450]}]}";
            var (summary, regions) = UiJobRunner.ParseLayoutMapJsonReply(raw);
            Assert.Equal("A planet hangs over a small house.", summary);
            Assert.Equal(3, regions.Count);
            Assert.Equal("red planet", regions[1].Label);
            Assert.Equal(50, regions[1].YMin);
            Assert.Equal(600, regions[1].XMin);
            Assert.Equal(350, regions[1].YMax);
            Assert.Equal(950, regions[1].XMax);
        }

        [Fact]
        public void StripsMarkdownFenceAndRoundsNumericCoordinates()
        {
            var raw = "```json\n{\"summary\": \"One region.\", \"regions\": ["
                + "{\"label\": \"everything\", \"box\": [0.4, 0, 999.6, 1000]}]}\n```";
            var (summary, regions) = UiJobRunner.ParseLayoutMapJsonReply(raw);
            Assert.Equal("One region.", summary);
            var region = Assert.Single(regions);
            Assert.Equal(0, region.YMin);
            Assert.Equal(1000, region.YMax);
        }

        [Theory]
        // Not JSON at all.
        [InlineData("The image shows a planet over a house.")]
        // Root is not an object.
        [InlineData("[1, 2, 3]")]
        // Missing summary.
        [InlineData("{\"regions\": [{\"label\": \"sky\", \"box\": [0, 0, 500, 1000]}]}")]
        // Blank summary.
        [InlineData("{\"summary\": \" \", \"regions\": [{\"label\": \"sky\", \"box\": [0, 0, 500, 1000]}]}")]
        // Missing regions.
        [InlineData("{\"summary\": \"x\"}")]
        // Zero regions.
        [InlineData("{\"summary\": \"x\", \"regions\": []}")]
        // Blank label.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"\", \"box\": [0, 0, 500, 1000]}]}")]
        // Wrong box arity.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"sky\", \"box\": [0, 0, 500]}]}")]
        // Coordinate out of range.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"sky\", \"box\": [0, 0, 500, 1001]}]}")]
        // Negative coordinate.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"sky\", \"box\": [-5, 0, 500, 1000]}]}")]
        // Inverted ymin/ymax.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"sky\", \"box\": [500, 0, 100, 1000]}]}")]
        // Non-numeric coordinate.
        [InlineData("{\"summary\": \"x\", \"regions\": [{\"label\": \"sky\", \"box\": [0, \"left\", 500, 1000]}]}")]
        public void RejectsNonConformingReplies(string raw)
        {
            Assert.Throws<InvalidDataException>(() => UiJobRunner.ParseLayoutMapJsonReply(raw));
        }

        [Fact]
        public void RejectsTooManyRegions()
        {
            var regions = string.Join(",", Enumerable.Range(0, 9).Select(i =>
                $"{{\"label\": \"region {i}\", \"box\": [{i * 100}, 0, {i * 100 + 50}, 1000]}}"));
            var raw = $"{{\"summary\": \"x\", \"regions\": [{regions}]}}";
            Assert.Throws<InvalidDataException>(() => UiJobRunner.ParseLayoutMapJsonReply(raw));
        }

        [Fact]
        public void RendererKeepsSourceAspectAndAddsLegendBand()
        {
            var regions = new[]
            {
                new UiLayoutMapRegion("sky", 0, 0, 400, 1000),
                new UiLayoutMapRegion("red planet", 50, 600, 350, 950),
            };
            using var map = UiLayoutMapRenderer.Render(1536, 1024, regions, "A planet over a plain.");
            // Landscape 3:2 source: map area is 1024 wide, 683 tall, and the
            // legend band adds height below it.
            Assert.Equal(1024, map.Width);
            Assert.True(map.Height > 683, $"expected a legend band below the 683px map; total height {map.Height}");
        }

        [Fact]
        public void RendererRejectsEmptyAndOversizedRegionLists()
        {
            Assert.ThrowsAny<System.ArgumentException>(() =>
                UiLayoutMapRenderer.Render(1000, 1000, new UiLayoutMapRegion[0], "x"));
            var nine = Enumerable.Range(0, 9)
                .Select(i => new UiLayoutMapRegion($"r{i}", 0, 0, 100, 100))
                .ToArray();
            Assert.ThrowsAny<System.ArgumentException>(() =>
                UiLayoutMapRenderer.Render(1000, 1000, nine, "x"));
        }

        [Fact]
        public void RendererRequiresSummaryAndPositiveDimensions()
        {
            var one = new[] { new UiLayoutMapRegion("sky", 0, 0, 500, 1000) };
            Assert.ThrowsAny<System.ArgumentException>(() =>
                UiLayoutMapRenderer.Render(1000, 1000, one, " "));
            Assert.ThrowsAny<System.ArgumentException>(() =>
                UiLayoutMapRenderer.Render(0, 1000, one, "x"));
        }
    }
}
