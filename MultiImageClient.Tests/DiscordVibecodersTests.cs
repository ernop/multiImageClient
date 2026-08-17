using System;
using System.IO;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class DiscordVibecodersTests
    {
        [Fact]
        public void ShareUrlUsesExactJobIdentity()
        {
            var url = DiscordVibecoders.BuildShareUrl(
                "https://host.example/instance-path/",
                "abc123def456",
                "gpt2",
                2);
            Assert.Equal(
                "https://host.example/instance-path?job=abc123def456&gen=gpt2&n=2",
                url);
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
