using System.Text;

namespace MultiImageClient.Tests;

public class GrokWebPromptTests
{
    [Theory]
    [InlineData("a", 8192)]
    [InlineData("é", 4096)]
    [InlineData("界", 2730)]
    [InlineData("😀", 2048)]
    public void TruncationPreservesCompleteUnicodeCharacters(string unit, int count)
    {
        var expected = string.Concat(Enumerable.Repeat(unit, count));
        var result = GrokWebClient.TruncateImagePrompt(expected + unit);
        Assert.Equal(expected, result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= GrokWebClient.MaxPromptUtf8Bytes);
        Assert.Equal(expected, GrokWebClient.TruncateImagePrompt(expected));
    }

    [Fact]
    public void TruncationKeepsPrefixWhenNextCharacterDoesNotFit()
    {
        var prefix = new string('a', 8191);
        Assert.Equal(prefix, GrokWebClient.TruncateImagePrompt(prefix + "😀z"));
        Assert.Equal("", GrokWebClient.TruncateImagePrompt(""));
    }
}
