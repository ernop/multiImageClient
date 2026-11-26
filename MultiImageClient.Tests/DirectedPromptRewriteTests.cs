using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient;

public sealed class DirectedPromptRewriteTests
{
    [Fact]
    public void DefaultsUseGemini4NeonAndEveryCatalogModelResolves()
    {
        Assert.Equal(new[] { "gemini-4-neon", "claude-opus-5-5" }, DirectedPromptRewrite.Models);
        Assert.Equal("gemini-4-neon", DirectedPromptRewrite.AdviceModel);
        Assert.Equal("gemini-4-neon", DirectedPromptRewrite.CondenseModel);
        Assert.Equal(ManagerCatalog.KeyGemini4Neon, DirectedPromptRewrite.DefaultAdviceKey);
        Assert.Equal(ManagerCatalog.KeyGemini4Neon, DirectedPromptRewrite.DefaultFleshOutPrimaryKey);
        Assert.Equal(ManagerCatalog.KeyClaudeOpus55, DirectedPromptRewrite.DefaultFleshOutSecondaryKey);
        Assert.Equal(ManagerCatalog.KeyGemini4Neon, DirectedPromptRewrite.DefaultCondenseKey);
        Assert.Equal(ManagerCatalog.KeyGemini4Neon, PromptSplit.ModelKey);
        Assert.Equal(ManagerCatalog.KeyGemini4Neon, PromptSubjectSynthesis.DefaultModelKey);

        foreach (var entry in ManagerCatalog.All)
        {
            Assert.Same(entry, DirectedPromptRewrite.ResolveModel(entry.Key));
            Assert.Same(entry, DirectedPromptRewrite.ResolveModel(entry.Model));
        }

        Assert.Null(DirectedPromptRewrite.ResolveModel(""));
        Assert.Null(DirectedPromptRewrite.ResolveModel("not-a-real-model"));
    }

    [Fact]
    public async Task AvailabilityChecksExactProviderKeyAndRejectsUnknownModels()
    {
        var openAiOnly = new Settings { OpenAIApiKey = "sk-test" };
        Assert.All(DirectedPromptRewrite.Models, model => Assert.NotNull(DirectedPromptRewrite.AvailabilityProblem(model, openAiOnly)));
        Assert.Null(DirectedPromptRewrite.AvailabilityProblem(ManagerCatalog.KeyGpt62Sol, openAiOnly));
        Assert.NotNull(DirectedPromptRewrite.AvailabilityProblem(ManagerCatalog.KeyGemini4Neon, openAiOnly));

        var googleOnly = new Settings { GoogleGeminiApiKey = "AIza-test" };
        Assert.Null(DirectedPromptRewrite.AvailabilityProblem(ManagerCatalog.KeyGemini4Neon, googleOnly));
        Assert.NotNull(DirectedPromptRewrite.AvailabilityProblem(ManagerCatalog.KeyGpt62Sol, googleOnly));

        var allKeys = new Settings
        {
            AnthropicApiKey = "sk-ant-test",
            OpenAIApiKey = "sk-test",
            GoogleGeminiApiKey = "AIza-test",
            XAIGrokApiKey = "xai-test",
        };
        Assert.Equal("Unknown rewrite model.", DirectedPromptRewrite.AvailabilityProblem("no-such-model", allKeys));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DirectedPromptRewrite.RewriteAsync("no-such-model", DirectedPromptRewrite.Instruction, "A red apple.", allKeys, CancellationToken.None));
        Assert.Equal("Unknown rewrite model.", rejected.Message);
    }

    [Fact]
    public void EveryDefaultRewriteModelHasAShortCatalogLabel()
        => Assert.Equal(new[] { "Gemini 4 Neon", "Opus 5.5" },
            DirectedPromptRewrite.Models.Select(model => ManagerCatalog.All.Single(m => m.Model == model).Label));

    [Fact]
    public void ValidateReplyPreservesExactTextAndRejectsEmptyReplies()
    {
        static ManagerChatReply MakeReply(string text) => new()
        {
            Text = text,
            ProviderReasoning = "",
            RawResponse = "{}",
            RedactedRequest = "{}",
            RequestBytes = 2,
            Ms = 10,
            Model = "gemini-4-neon",
        };

        Assert.Equal("  Expanded\nprompt  ", DirectedPromptRewrite.ValidateReply(MakeReply("  Expanded\nprompt  ")));
        Assert.Throws<InvalidDataException>(() => DirectedPromptRewrite.ValidateReply(MakeReply("   \n  ")));
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
