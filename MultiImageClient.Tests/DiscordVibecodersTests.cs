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

        [Fact]
        public void PlanAttachmentBatchesKeepsOneMessageUnderBudgetAndBalancesMultiBatch()
        {
            const long MiB = 1024L * 1024L;
            var small = new[] { 2 * MiB, 2 * MiB, 2 * MiB, 2 * MiB, 2 * MiB, 2 * MiB, 2 * MiB, 2 * MiB };
            var single = DiscordVibecoders.PlanAttachmentBatches(small);
            Assert.Single(single);
            Assert.Equal(0, single[0].StartIndex);
            Assert.Equal(8, single[0].Count);

            // 8 x 4 MiB = 32 MiB > 24 MiB -> minimum 2 batches, balanced 4 + 4.
            var eightFourMb = new[] { 4 * MiB, 4 * MiB, 4 * MiB, 4 * MiB, 4 * MiB, 4 * MiB, 4 * MiB, 4 * MiB };
            var twoBalanced = DiscordVibecoders.PlanAttachmentBatches(eightFourMb);
            Assert.Equal(2, twoBalanced.Count);
            Assert.Equal(new DiscordVibecodersBatchPlan(0, 4, 16 * MiB), twoBalanced[0]);
            Assert.Equal(new DiscordVibecodersBatchPlan(4, 4, 16 * MiB), twoBalanced[1]);

            // 11 tiny items exceed MaxAttachmentsPerMessage (10) -> balanced 6 + 5.
            var elevenTiny = new long[11];
            Array.Fill(elevenTiny, 100_000L);
            var twoCountSplit = DiscordVibecoders.PlanAttachmentBatches(elevenTiny);
            Assert.Equal(2, twoCountSplit.Count);
            Assert.Equal(6, twoCountSplit[0].Count);
            Assert.Equal(5, twoCountSplit[1].Count);

            // Reject empty list and over-20-MiB single file.
            Assert.Throws<InvalidOperationException>(() => DiscordVibecoders.PlanAttachmentBatches(Array.Empty<long>()));
            Assert.Throws<InvalidOperationException>(() => DiscordVibecoders.PlanAttachmentBatches(new[] { DiscordVibecoders.MaxAttachmentBytes + 1 }));
        }

        [Fact]
        public async Task SendBatchAsyncPostsAllAttachmentsInOrderWithStrictVerification()
        {
            var settings = new Settings
            {
                DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/tokenvalue",
                UiPublicBaseUrl = "https://private.example/secret-path",
            };
            var handler = new BatchCaptureHandler();
            using var http = new HttpClient(handler);
            using var m1 = new MemoryStream(new byte[] { 1, 2, 3 });
            using var m2 = new MemoryStream(new byte[] { 4, 5, 6 });
            var attachments = new[]
            {
                new DiscordVibecodersAttachment(m1, "image/png", "page-1.png", "Page 1 of 2: Opening"),
                new DiscordVibecodersAttachment(m2, "image/png", "page-2.png", "Page 2 of 2: Ending"),
            };
            await new DiscordVibecodersClient(settings, http).SendBatchAsync(
                "alice",
                attachments,
                "Pages 1–2 of 2 · [View series (2 pages)](<https://public.example/public/tok/>)",
                "999",
                "999",
                CancellationToken.None);

            Assert.Contains("files[0]", handler.Body);
            Assert.Contains("files[1]", handler.Body);
            Assert.Contains("page-1.png", handler.Body);
            Assert.Contains("page-2.png", handler.Body);
            Assert.Contains("Page 1 of 2: Opening", handler.Body);
            Assert.Contains("\"flags\":4", handler.Body);
            Assert.Contains("\"allowed_mentions\":{\"parse\":[]}", handler.Body);
            Assert.DoesNotContain("private.example", handler.Body);
            Assert.DoesNotContain("secret-path", handler.Body);
        }

        private sealed class BatchCaptureHandler : HttpMessageHandler
        {
            public string Body { get; private set; } = "";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Body = Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(ct));
                var parts = (MultipartFormDataContent)request.Content!;
                var payload = parts.First(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "payload_json");
                using var json = System.Text.Json.JsonDocument.Parse(await payload.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id = "msg-777",
                        channel_id = "999",
                        content = json.RootElement.GetProperty("content").GetString(),
                        attachments = new[] { new { id = "a1" }, new { id = "a2" } },
                    })),
                };
            }
        }
    }
}
