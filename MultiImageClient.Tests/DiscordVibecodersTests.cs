using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class DiscordVibecodersTests
    {
        [Fact]
        public async Task PostingNeverIncludesPrivateSiteOrLoginLinks()
        {
            var settings = new Settings {
                DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/tokenvalue",
                UiPublicBaseUrl = "https://private.example/secret-path",
            };
            var handler = new CaptureHandler();
            using var http = new HttpClient(handler);
            using var media = new MemoryStream(new byte[] { 1, 2, 3 });
            await new DiscordVibecodersClient(settings, http).SendAsync(
                "alice", media, "image/png", "image.png", CancellationToken.None);
            Assert.DoesNotContain("private.example", handler.Body);
            Assert.DoesNotContain("secret-path", handler.Body);
            Assert.DoesNotContain("\"content\"", handler.Body);
            Assert.DoesNotContain("\"embeds\"", handler.Body);
            Assert.Contains("files[0]", handler.Body);
            Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", handler.Body);
            settings.UiPublicBaseUrl = "";
            Assert.True(DiscordVibecoders.IsConfigured(settings));
        }

        private sealed class CaptureHandler : HttpMessageHandler
        {
            public string Body { get; private set; } = "";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Body = Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        [Fact]
        public void PublicBaseUrlRejectsQueryFragmentAndRoot()
        {
            Assert.False(DiscordVibecoders.TryNormalizePublicBaseUrl(
                "https://host.example/", out _));
            Assert.False(DiscordVibecoders.TryNormalizePublicBaseUrl(
                "https://host.example/path?x=1", out _));
            Assert.False(DiscordVibecoders.TryNormalizePublicBaseUrl(
                "http://host.example/path", out _));
            Assert.True(DiscordVibecoders.TryNormalizePublicBaseUrl(
                "https://host.example/path/", out var url));
            Assert.Equal("https://host.example/path", url);
        }

        [Fact]
        public void WebhookUrlAcceptsOnlyDiscordIncomingWebhooks()
        {
            Assert.False(DiscordVibecoders.TryNormalizeWebhookUrl(
                "https://example.com/api/webhooks/1/token", out _));
            Assert.True(DiscordVibecoders.TryNormalizeWebhookUrl(
                "https://discord.com/api/webhooks/123/tokenvalue", out var url));
            Assert.StartsWith("https://discord.com/api/webhooks/", url);
        }

        [Fact]
        public void StoreClaimsEachResultOnceAndReleasesFailedSends()
        {
            var folder = Directory.CreateTempSubdirectory("mic-vibecoders-").FullName;
            try
            {
                var store = new UiDiscordVibecodersStore(new Settings
                {
                    ImageDownloadBaseFolder = folder,
                });
                var record = new UiDiscordVibecodersSend
                {
                    Kind = "image",
                    JobId = "job1",
                    Generator = "gpt2",
                    ImageIndex = 0,
                    SentByLogin = "alice",
                    SentAtUnixMs = 1_800_000_000_000,
                };
                Assert.True(store.TryClaim(record));
                Assert.True(store.IsSent("job1", "gpt2", 0));
                Assert.False(store.TryClaim(record));
                store.ReleaseClaim("job1", "gpt2", 0, "alice");
                Assert.False(store.IsSent("job1", "gpt2", 0));
                Assert.True(store.TryClaim(record));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
