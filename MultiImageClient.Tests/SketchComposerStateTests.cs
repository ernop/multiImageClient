using System.IO;

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
            Assert.Equal(1, state.InputIndex);
            Assert.Equal("wide", state.Aspect);
            Assert.Equal(UiJobRunner.SketchComposerMeaningCount, state.Meanings.Count);
            Assert.Equal("planet", state.Meanings[0]);
            Assert.Equal("ground", state.Meanings[7]);
        }

        [Fact]
        public void BlankStateMeansNoLinkedSketch()
        {
            Assert.Null(UiJobRunner.ParseSketchComposerState("", inputCount: 0));
            Assert.Null(UiJobRunner.ParseSketchComposerState("  ", inputCount: 3));
        }

        [Theory]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["","","","","","","",""],"extra":true}""", 1)]
        [InlineData("""{"version":2,"inputIndex":0,"aspect":"square","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":1,"aspect":"square","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"auto","meanings":["","","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["","","","","","",""]}""", 1)]
        [InlineData("""{"version":1,"inputIndex":0,"aspect":"square","meanings":["bad\nlabel","","","","","","",""]}""", 1)]
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
