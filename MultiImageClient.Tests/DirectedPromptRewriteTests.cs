using System.Text.Json;

namespace MultiImageClient;

public sealed class DirectedPromptRewriteTests
{
    private static string Anthropic(string model, string stop = "end_turn", string type = "text", string text = "  Expanded\nprompt  ")
        => JsonSerializer.Serialize(new
        {
            model, stop_reason = stop,
            content = new object[] {
                new { type = "thinking", thinking = "Private reasoning is not prompt text." },
                new { type, text },
            },
        });

    [Fact]
    public void OffersOnlyOpusAndSonnet()
    {
        Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5" }, DirectedPromptRewrite.Models);
        var settings = new Settings { AnthropicApiKey = "sk-ant-test", OpenAIApiKey = "sk-test" };
        Assert.All(DirectedPromptRewrite.Models, model => Assert.Null(DirectedPromptRewrite.AvailabilityProblem(model, settings)));
        Assert.Equal("Unknown rewrite model.", DirectedPromptRewrite.AvailabilityProblem("gpt-6-astra", settings));
        Assert.Equal("Unknown rewrite model.", DirectedPromptRewrite.AvailabilityProblem("claude-fable-5-1", settings));
    }

    [Fact]
    public async Task OpenAiKeyNeitherEnablesNorReachesARewrite()
    {
        var openAiOnly = new Settings { OpenAIApiKey = "sk-test" };
        Assert.All(DirectedPromptRewrite.Models, model => Assert.NotNull(DirectedPromptRewrite.AvailabilityProblem(model, openAiOnly)));
        var both = new Settings { AnthropicApiKey = "sk-ant-test", OpenAIApiKey = "sk-test" };
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectedPromptRewrite.RewriteAsync("gpt-6-astra", DirectedPromptRewrite.Instruction, "A red apple.", both, CancellationToken.None));
        Assert.Equal("Unknown rewrite model.", rejected.Message);
    }

    [Fact]
    public void AdviceUsesOpus55()
    {
        Assert.Equal("claude-opus-5-5", DirectedPromptRewrite.AdviceModel);
        Assert.Contains(DirectedPromptRewrite.AdviceModel, DirectedPromptRewrite.Models);
        Assert.Equal("Opus 5.5", ManagerCatalog.All.Single(m => m.Model == DirectedPromptRewrite.AdviceModel).Label);
    }

    [Fact]
    public void EveryRewriteModelHasAShortCatalogLabel()
        => Assert.Equal(new[] { "Opus 5.5", "Sonnet 5.5" },
            DirectedPromptRewrite.Models.Select(model => ManagerCatalog.All.Single(m => m.Model == model).Label));

    [Theory]
    [InlineData(DirectedPromptRewrite.SonnetModel, DirectedPromptRewrite.Instruction)]
    [InlineData(DirectedPromptRewrite.AdviceModel, "Fix spelling and obvious typos.")]
    public void RequestCarriesTheInstructionWithoutTemperature(string model, string instruction)
    {
        var wire = ClaudeService.BuildPromptAdviceWirePrompt(instruction, "A red appel.");
        using var request = JsonDocument.Parse(DirectedPromptRewrite.BuildRequestJson(model, wire));
        var root = request.RootElement;
        Assert.Equal(new[] { "model", "max_tokens", "system", "messages" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(ClaudeService.PromptAdviceSystemPrompt, root.GetProperty("system").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(wire, message.GetProperty("content").GetString());
        Assert.StartsWith("Editing instruction:\n" + instruction + "\n\n", wire);
    }

    [Theory]
    [InlineData(DirectedPromptRewrite.OpusModel)]
    [InlineData(DirectedPromptRewrite.SonnetModel)]
    public void PreservesExactTextAndExcludesThinking(string model)
        => Assert.Equal("  Expanded\nprompt  ", DirectedPromptRewrite.ParseReplacement(model, Anthropic(model)));

    [Theory]
    [InlineData("end_turn", true)]
    [InlineData("max_tokens", false)]
    [InlineData("refusal", false)]
    [InlineData("tool_use", false)]
    public void RequiresCompletedText(string stop, bool accepted)
    {
        foreach (var model in DirectedPromptRewrite.Models)
        {
            var json = Anthropic(model, stop);
            if (accepted) Assert.Equal("  Expanded\nprompt  ", DirectedPromptRewrite.ParseReplacement(model, json));
            else Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(model, json));
        }
    }

    [Theory]
    [InlineData("tool_use", "Expanded prompt")]
    [InlineData("text", " ")]
    public void RejectsUnexpectedOrEmptyContent(string type, string text)
        => Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.SonnetModel, Anthropic(DirectedPromptRewrite.SonnetModel, type: type, text: text)));

    [Fact]
    public void RejectsWrongModelMissingStopAndOpenAiReplies()
    {
        Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.OpusModel, Anthropic(DirectedPromptRewrite.SonnetModel)));
        Assert.Throws<KeyNotFoundException>(() => DirectedPromptRewrite.ParseReplacement(
            DirectedPromptRewrite.SonnetModel, "{\"model\":\"claude-sonnet-5-5\",\"content\":[]}"));
        var openAi = JsonSerializer.Serialize(new
        {
            model = "gpt-6-astra", status = "completed",
            output = new object[] {
                new { type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = "Expanded" } } }
            },
        });
        Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ParseReplacement("gpt-6-astra", openAi));
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
                var item = store.StartClaudePromptExchange("login:alice", "Alice", DirectedPromptRewrite.OpusModel,
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
