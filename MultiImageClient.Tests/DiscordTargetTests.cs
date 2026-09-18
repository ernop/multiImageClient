using System.Net;
using System.Text;
using System.Text.Json;
using MultiImageClient;

public sealed class DiscordTargetTests
{
    [Fact]
    public async Task TestingTargetUploadsThroughBotToExactThreadWithoutWebhook()
    {
        var settings = new Settings { DiscordVibecodersBotToken = new string('t', 60), DiscordBotTestingGuildId = "111", DiscordBotTestingChannelId = "222" };
        var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new DiscordVibecodersClient(settings, http, "bot-testing");
        Assert.Equal(("111", "222"), await client.GetDestinationAsync(default));
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
        await client.SendAsync("owner", bytes, "image/png", "test.png", default, "https://example.test/shared/test/abc/", "333", "333");
        Assert.Equal("https://discord.com/api/v10/channels/333/messages", handler.Url);
        Assert.Equal("Bot", handler.Scheme);
        Assert.DoesNotContain("username", handler.Body);
        Assert.Contains("files[0]", handler.Body);
        Assert.NotEqual(UiPublicShares.DestinationHash(settings), UiPublicShares.DestinationHash(settings, "bot-testing"));
    }
    [Fact]
    public async Task MissingTestConfigurationNeverFallsBackToVibecoders()
    {
        var client = new DiscordVibecodersClient(new Settings { DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/token" }, target: "bot-testing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDestinationAsync(default));
    }
    sealed class Handler : HttpMessageHandler
    {
        public string? Url, Scheme, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url=request.RequestUri!.ToString(); Scheme=request.Headers.Authorization?.Scheme; Body=await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { channel_id="333", content=UiPublicShares.Caption("https://example.test/shared/test/abc/"), attachments=new[]{new {id="444"}} }), Encoding.UTF8,"application/json") };
        }
    }
}
