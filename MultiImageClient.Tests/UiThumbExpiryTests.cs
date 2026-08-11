using System;
using System.IO;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    public class UiThumbExpiryTests
    {
        [Theory]
        [InlineData("gpt2_0.jpg", "gpt2/0")]
        [InlineData("gpt2~p1_0.png", "gpt2~p1/0")]
        [InlineData("grok-web-video_0.jpg", "grok-web-video/0")]
        [InlineData("input_2.jpg", "input/2")]
        [InlineData("grid_0.png", "grid/0")]
        public void ThumbFileMapsBackToImageKey(string fileName, string expectedKey)
        {
            Assert.True(UiThumbExpiry.TryThumbFileToImageKey(fileName, out var key));
            Assert.Equal(expectedKey, key);
        }

        [Theory]
        [InlineData("noindex.jpg")]
        [InlineData("gpt2_.jpg")]
        [InlineData("gpt2_abc.jpg")]
        [InlineData("_0.jpg")]
        public void UnmappableThumbFileIsRejected(string fileName)
        {
            Assert.False(UiThumbExpiry.TryThumbFileToImageKey(fileName, out _));
        }

        [Fact]
        public void SweepDeletesOnlyOldRegenerableThumbs()
        {
            var root = Path.Combine(Path.GetTempPath(), "mic-thumb-expiry-test-" + Guid.NewGuid().ToString("N"));
            var jobFolder = Path.Combine(root, "UiHistory", "job1");
            var thumbs = Path.Combine(jobFolder, "thumbs");
            Directory.CreateDirectory(thumbs);
            try
            {
                // A local original that still exists (regenerable from disk).
                var localOriginal = Path.Combine(jobFolder, "local-original.png");
                File.WriteAllBytes(localOriginal, new byte[] { 1 });

                File.WriteAllText(Path.Combine(jobFolder, "images.json"), $$"""
                {
                  "gpt2/0": { "Path": {{System.Text.Json.JsonSerializer.Serialize(localOriginal)}}, "ContentType": "image/png", "ContentSha256": "", "CdnKey": "", "CdnFileId": "" },
                  "bfl/0": { "Path": "/nonexistent/evicted.png", "ContentType": "image/png", "ContentSha256": "ABC123", "CdnKey": "ui/job1/bfl/0-deadbeef.png", "CdnFileId": "x" },
                  "recraft/0": { "Path": "/nonexistent/gone-forever.png", "ContentType": "image/png", "ContentSha256": "", "CdnKey": "", "CdnFileId": "" }
                }
                """);

                var old = DateTime.UtcNow.AddDays(-3);
                string Write(string name, DateTime mtime)
                {
                    var p = Path.Combine(thumbs, name);
                    File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
                    File.SetLastWriteTimeUtc(p, mtime);
                    return p;
                }

                var oldLocalSource = Write("gpt2_0.jpg", old);
                var oldHosted = Write("bfl_0.jpg", old);
                var oldIrreplaceable = Write("recraft_0.jpg", old);
                var oldUnknownKey = Write("mystery_0.jpg", old);
                var freshHosted = Write("bfl_1.jpg", DateTime.UtcNow);

                var settings = new Settings
                {
                    ImageDownloadBaseFolder = root,
                    EnableB2ImageHosting = true,
                };
                UiThumbExpiry.SweepOnce(settings);

                Assert.False(File.Exists(oldLocalSource));   // regenerable from local original
                Assert.False(File.Exists(oldHosted));        // regenerable from B2
                Assert.True(File.Exists(oldIrreplaceable));  // source gone forever — kept
                Assert.True(File.Exists(oldUnknownKey));     // key not in images.json — kept
                Assert.True(File.Exists(freshHosted));       // too young — kept

                // With hosting off, a hosted-only source is no longer obtainable.
                var oldHostedAgain = Write("bfl_0.jpg", old);
                settings.EnableB2ImageHosting = false;
                UiThumbExpiry.SweepOnce(settings);
                Assert.True(File.Exists(oldHostedAgain));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
