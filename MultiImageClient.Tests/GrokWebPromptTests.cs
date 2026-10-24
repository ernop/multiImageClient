using System.Text;
using System.Text.Json;

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

    [Theory]
    [InlineData(null)]
    [InlineData("asset-1")]
    public void ChatPayloadAttachesOnlyTheGivenSourceAsset(string? assetId)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            GrokWebClient.BuildImageChatPayload("a red apple", assetId, GrokWebClient.DefaultChatModel)));
        var root = doc.RootElement;

        Assert.Equal("a red apple", root.GetProperty("message").GetString());
        Assert.Equal("grok-3", root.GetProperty("modelName").GetString());
        Assert.False(root.GetProperty("temporary").GetBoolean());
        Assert.True(root.GetProperty("enableImageGeneration").GetBoolean());
        Assert.Equal(
            assetId == null ? Array.Empty<string>() : new[] { assetId },
            root.GetProperty("fileAttachments").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }
}
