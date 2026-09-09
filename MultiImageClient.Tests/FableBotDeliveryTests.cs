using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FableBot;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class FableBotDeliveryTests
    {
        private const ulong Channel = 123456789012345678;
        private const string Token = "MTAwMDAwMDAwMDAwMDAwMDAw.GaBcDe.fghijklmnopqrstuvwxyz0123456789ABCDEF";

        [Theory]
        [InlineData("999999999999999999", "[{\"size\":8}]")]
        [InlineData("123456789012345678", "[]")]
        [InlineData("123456789012345678", "[{\"size\":9}]")]
        public async Task RejectsWrongChannelOrIncompleteAttachment(string channel, string attachments)
        {
            using var http = new HttpClient(new ReplyHandler($"{{\"id\":\"333\",\"channel_id\":\"{channel}\",\"attachments\":{attachments}}}"));
            var client = new FableBotDiscordClient(Token, http);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostMessageAsync(Channel, "",
                new[] { new FableBotAttachment("test.png", new byte[8]) }, "111", CancellationToken.None));
        }

        [Fact]
        public async Task SendsVerbatimAttachmentAndDisablesMentions()
        {
            var handler = new ReplyHandler("{\"id\":\"333\",\"channel_id\":\"123456789012345678\",\"attachments\":[{\"size\":8}]}");
            using var http = new HttpClient(handler);
            var client = new FableBotDiscordClient(Token, http);
            var bytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 };
            var posted = await client.PostMessageAsync(Channel, "@everyone",
                new[] { new FableBotAttachment("test.png", bytes) }, "111", CancellationToken.None);
            Assert.Equal("https://discord.com/channels/111/123456789012345678/333", posted.JumpUrl);
            Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", Encoding.UTF8.GetString(handler.RequestBody));
            Assert.True(handler.RequestBody.AsSpan().IndexOf(bytes) >= 0);
            Assert.Equal("Bot " + Token, handler.Authorization);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task ChannelLookupMustMatchRequestedIdentity()
        {
            using var http = new HttpClient(new ReplyHandler("{\"id\":\"999\",\"type\":0}"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new FableBotDiscordClient(Token, http).GetChannelAsync(Channel, CancellationToken.None));
        }

        [Fact]
        public void PendingClaimSurvivesRestartAndSeparatesChannelsAndImages()
        {
            var folder = Directory.CreateTempSubdirectory("mic-fablebot-").FullName;
            try
            {
                var record = new UiFableBotSend(Channel.ToString(), "job", "gpt2", 0, "owner", 1);
                var store = new UiFableBotStore(folder);
                Assert.True(store.TryClaim(record));
                store = new UiFableBotStore(folder);
                Assert.False(store.TryClaim(record));
                Assert.Equal("pending", store.Get(record.ChannelId, "job", "gpt2", 0)?.State);
                Assert.Null(store.Get("different-channel", "job", "gpt2", 0));
                Assert.Null(store.Get(record.ChannelId, "job", "gpt2", 1));
                store.Complete(record, "https://discord.com/channels/111/222/333");
                Assert.Equal("sent", new UiFableBotStore(folder).Get(record.ChannelId, "job", "gpt2", 0)?.State);
            }
            finally { Directory.Delete(folder, true); }
        }

        private sealed class ReplyHandler(string body) : HttpMessageHandler
        {
            public byte[] RequestBody { get; private set; } = Array.Empty<byte>();
            public string Authorization { get; private set; } = "";
            public int Calls { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                Authorization = request.Headers.Authorization?.ToString() ?? "";
                if (request.Content != null) RequestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
        }
    }
}
