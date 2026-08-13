using System.IO;
using System.Text.Json;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public class SketchComposerStateTests
    {
        [Fact]
        public void ParsesExactSketchAttachmentIdentity()
        {
            var raw = """
                {
                  "version": 1,
                  "inputIndex": 1,
                  "aspect": "wide",
                  "meanings": ["planet", "sky", "", "", "", "", "", "ground"]
                }
                """;

            var state = UiJobRunner.ParseSketchComposerState(raw, inputCount: 2);

            Assert.NotNull(state);
            Assert.Equal(1, state.Version);
            Assert.Equal(1, state.InputIndex);
            Assert.Equal("wide", state.Aspect);
            Assert.Null(state.Palette);
            Assert.Equal(UiJobRunner.SketchComposerMeaningCount, state.Meanings.Count);
            Assert.Equal("planet", state.Meanings[0]);
            Assert.Equal("ground", state.Meanings[7]);
        }

        [Fact]
        public void ParsesVersion2PaletteIdentity()
        {
            var raw = """
                {
                  "version": 2,
                  "inputIndex": 0,
                  "aspect": "square",
                  "palette": [
                    {"name": "olive green", "hex": "#4a6b32"},
                    {"name": "steel blue", "hex": "#3d5a80"}
                  ],
                  "meanings": ["hillside", "water"]
                }
                """;

            var state = UiJobRunner.ParseSketchComposerState(raw, inputCount: 1);

            Assert.NotNull(state);
            Assert.Equal(2, state.Version);
            Assert.NotNull(state.Palette);
            Assert.Equal(2, state.Palette.Count);
            Assert.Equal("olive green", state.Palette[0].Name);
            Assert.Equal("#4a6b32", state.Palette[0].Hex);
            Assert.Equal(2, state.Meanings.Count);
            Assert.Equal("hillside", state.Meanings[0]);
            Assert.Equal("water", state.Meanings[1]);
        }

        [Fact]
        public void PaletteColorSerializesCamelCaseNameAndHex()
        {
            var json = JsonSerializer.Serialize(
                new UiSketchComposerPaletteColor("olive green", "#4a6b32"));
            Assert.Equal("""{"name":"olive green","hex":"#4a6b32"}""", json);
        }

        [Fact]
        public void BlankStateMeansNoLinkedSketch()
        {
            Assert.Null(UiJobRunner.ParseSketchComposerState("", inputCount: 0));
            Assert.Null(UiJobRunner.ParseSketchComposerState("  ", inputCount: 3));
        }

        [Theory]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["","","","","","","",""],"extra":true}""", 1)]
        [InlineData("""{"version":3,"inputIndex":0,"aspect":"square","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":1,"aspect":"square","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"auto","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["bad\nlabel","","","","","","",""]}""", 1)]
        [InlineData("""{"version":2,"inputIndex":0,"aspect":"square","palette":[{"name":"red","hex":"#e53935"}],"meanings":["ok",""]}""", 1)]
        [InlineData("""{"version":2,"inputIndex":0,"aspect":"square","palette":[{"name":"white","hex":"#ffffff"}],"meanings":[""]}""", 1)]
        [InlineData("""{"version":2,"inputIndex":0,"aspect":"square","palette":[{"name":"a","hex":"#111111"},{"name":"a","hex":"#222222"}],"meanings":["",""]}""", 1)]
        public void RejectsMalformedOrUncorrelatedState(string raw, int inputCount)
        {
            Assert.Throws<InvalidDataException>(
                () => UiJobRunner.ParseSketchComposerState(raw, inputCount));
        }

        [Fact]
        public void RejectsDuplicateFields()
        {
            var raw = """
                {
                  "version": 1,
                  "version": 1,
                  "inputIndex": 0,
                  "aspect": "square",
                  "meanings": ["", "", "", "", "", "", "", ""]
                }
                """;

            Assert.Throws<InvalidDataException>(
                () => UiJobRunner.ParseSketchComposerState(raw, inputCount: 1));
        }
    }
}
