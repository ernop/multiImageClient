using System.Text.Json;

namespace MultiImageClient;

public sealed class DirectedPromptRewriteTests
{
    private static string OpenAi(string status = "completed", string type = "output_text", string text = "  Expanded\nprompt  ")
        => JsonSerializer.Serialize(new
        {
            model = DirectedPromptRewrite.OpenAiModel, status,
            output = new object[] {
                new { type = "reasoning", summary = new[] { new { text = "Private summary is not prompt text." } } },
                new { type = "message", role = "assistant", status = "completed", content = new[] { new { type, text } } }
            },
        });

    [Fact]
    public void OpenAiPreservesExactTextAndExcludesReasoning()
        => Assert.Equal("  Expanded\nprompt  ", DirectedPromptRewrite.ParseReplacement(DirectedPromptRewrite.OpenAiModel, OpenAi()));

    [Theory]
    [InlineData("incomplete", "output_text", "partial")]
    [InlineData("completed", "refusal", "refused")]
    [InlineData("completed", "output_text", " ")]
    public void RejectsIncompleteRefusedOrEmptyOpenAi(string status, string type, string text)
        => Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.OpenAiModel, OpenAi(status, type, text)));

    [Fact]
    public void RejectsWrongModelAndMissingCompletionStatus()
    {
        Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.OpenAiModel, OpenAi().Replace("gpt-6-astra", "gpt-5.6-sol")));
        Assert.Throws<KeyNotFoundException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.OpenAiModel, "{\"model\":\"gpt-6-astra\",\"output\":[]}"));
    }

    [Theory]
    [InlineData("end_turn", true)]
    [InlineData("max_tokens", false)]
    [InlineData("refusal", false)]
    [InlineData("tool_use", false)]
    public void FableRequiresCompletedText(string stop, bool accepted)
    {
        var json = JsonSerializer.Serialize(new {
            model = DirectedPromptRewrite.FableModel, stop_reason = stop,
            content = new object[] { new { type = "thinking", thinking = "Excluded." }, new { type = "text", text = "Expanded prompt" } },
        });
        if (accepted) Assert.Equal("Expanded prompt", DirectedPromptRewrite.ParseReplacement(DirectedPromptRewrite.FableModel, json));
        else Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(DirectedPromptRewrite.FableModel, json));
    }

    [Fact]
    public void HistoryPagesPreserveTimestampTiesAndExactUserIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mic-rewrite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = folder, UiCommunityDbPath = Path.Combine(folder, "community.sqlite3") };
            var store = new UiCommunityStore(settings);
            for (var i = 0; i < 25; i++)
            {
                var item = store.StartClaudePromptExchange("login:alice", "Alice", DirectedPromptRewrite.FableModel,
                    "Expand", $" original {i}\n", "system", "wire", 1000);
                store.CompleteClaudePromptExchange(item.Id, "raw", $" replacement {i}\n", "", 2000);
            }
            var first = store.ReadClaudePromptExchanges("login:alice", 20);
            var second = new UiCommunityStore(settings).ReadClaudePromptExchanges("login:alice", 20, first[^1].RequestedAtUnixMs, first[^1].Id);
            Assert.Equal(20, first.Count);
            Assert.Equal(5, second.Count);
            Assert.Equal(25, first.Concat(second).Select(x => x.Id).Distinct().Count());
            Assert.Empty(store.ReadClaudePromptExchanges("login:bob", 1, exchangeId: first[0].Id));
            var exact = Assert.Single(store.ReadClaudePromptExchanges("login:alice", 1, exchangeId: first[0].Id));
            Assert.Equal(first[0].OriginalPrompt, exact.OriginalPrompt);
            Assert.EndsWith("\n", exact.ResultPrompt);
        }
        finally { Directory.Delete(folder, true); }
    }
}
