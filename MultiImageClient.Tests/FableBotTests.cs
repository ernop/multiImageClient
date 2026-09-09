using System;
using System.IO;
using System.Text;

using FableBot;

using MultiImageClient;

using Xunit;

namespace MultiImageClient.Tests
{
    public sealed class FableBotTests
    {
        private const string PlausibleToken =
            "MTAwMDAwMDAwMDAwMDAwMDAw.GaBcDe.fghijklmnopqrstuvwxyz0123456789ABCDEF";

        [Fact]
        public void ChannelIdAcceptsOnlyNumericSnowflakes()
        {
            Assert.True(FableBotDiscord.TryNormalizeChannelId(
                "123456789012345678", out var id));
            Assert.Equal(123456789012345678UL, id);
            Assert.True(FableBotDiscord.TryNormalizeChannelId(
                "  123456789012345678  ", out _));
            Assert.False(FableBotDiscord.TryNormalizeChannelId("", out _));
            Assert.False(FableBotDiscord.TryNormalizeChannelId("general", out _));
            Assert.False(FableBotDiscord.TryNormalizeChannelId("12345", out _));
            Assert.False(FableBotDiscord.TryNormalizeChannelId(
                "12345678901234567x", out _));
            Assert.False(FableBotDiscord.TryNormalizeChannelId(
                "<#123456789012345678>", out _));
        }

        [Fact]
        public void BotTokenRejectsBlankShortAndWhitespace()
        {
            Assert.True(FableBotDiscord.TryNormalizeBotToken(PlausibleToken, out var token));
            Assert.Equal(PlausibleToken, token);
            Assert.True(FableBotDiscord.TryNormalizeBotToken(
                "  " + PlausibleToken + "  ", out _));
            Assert.False(FableBotDiscord.TryNormalizeBotToken("", out _));
            Assert.False(FableBotDiscord.TryNormalizeBotToken("shorttoken", out _));
            Assert.False(FableBotDiscord.TryNormalizeBotToken(
                "Bot " + PlausibleToken, out _));
        }

        [Fact]
        public void OutgoingMessageEnforcesDiscordLimits()
        {
            var oneFile = new[] { new FableBotAttachment("a.png", new byte[] { 1 }) };

            // Empty everything is rejected.
            Assert.Throws<InvalidOperationException>(() =>
                FableBotDiscord.ValidateOutgoingMessage("", Array.Empty<FableBotAttachment>()));

            // Content-only, file-only, and both are all valid.
            FableBotDiscord.ValidateOutgoingMessage("hi", Array.Empty<FableBotAttachment>());
            FableBotDiscord.ValidateOutgoingMessage("", oneFile);
            FableBotDiscord.ValidateOutgoingMessage("hi", oneFile);
            FableBotDiscord.ValidateOutgoingMessage(
                new string('x', FableBotDiscord.MaxMessageChars),
                Array.Empty<FableBotAttachment>());

            Assert.Throws<InvalidOperationException>(() =>
                FableBotDiscord.ValidateOutgoingMessage(
                    new string('x', FableBotDiscord.MaxMessageChars + 1),
                    Array.Empty<FableBotAttachment>()));

            var elevenFiles = new FableBotAttachment[11];
            for (int i = 0; i < elevenFiles.Length; i++)
            {
                elevenFiles[i] = new FableBotAttachment($"f{i}.png", new byte[] { 1 });
            }
            Assert.Throws<InvalidOperationException>(() =>
                FableBotDiscord.ValidateOutgoingMessage("", elevenFiles));

            Assert.Throws<InvalidOperationException>(() =>
                FableBotDiscord.ValidateOutgoingMessage(
                    "", new[] { new FableBotAttachment("empty.png", Array.Empty<byte>()) }));
        }

        [Fact]
        public void ContentTypeComesFromMagicBytes()
        {
            Assert.Equal("image/png", FableBotDiscord.DetectContentType(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
            Assert.Equal("image/jpeg", FableBotDiscord.DetectContentType(
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
            Assert.Equal("image/webp", FableBotDiscord.DetectContentType(
                Encoding.ASCII.GetBytes("RIFF....WEBP")));
            Assert.Equal("image/gif", FableBotDiscord.DetectContentType(
                Encoding.ASCII.GetBytes("GIF89a")));
            Assert.Equal("application/octet-stream", FableBotDiscord.DetectContentType(
                Encoding.ASCII.GetBytes("plain text")));
        }

        [Fact]
        public void PostedMessageBuildsJumpUrlFromExactIdentity()
        {
            var posted = new FableBotPostedMessage("333", "222", "111");
            Assert.Equal("https://discord.com/channels/111/222/333", posted.JumpUrl);
            var noGuild = new FableBotPostedMessage("333", "222", null);
            Assert.Equal("https://discord.com/channels/@me/222/333", noGuild.JumpUrl);
        }

        [Fact]
        public void SettingsRequireTokenAndChannelTogether()
        {
            var folder = Directory.CreateTempSubdirectory("mic-fablebot-").FullName;
            try
            {
                Settings MakeSettings() => new Settings
                {
                    LogFilePath = Path.Combine(folder, "test.log"),
                    ImageDownloadBaseFolder = folder,
                };

                // Both blank: FableBot disabled, settings valid.
                MakeSettings().Validate();

                var tokenOnly = MakeSettings();
                tokenOnly.FableBotDiscordBotToken = PlausibleToken;
                Assert.Throws<InvalidOperationException>(() => tokenOnly.Validate());

                var channelOnly = MakeSettings();
                channelOnly.FableBotDiscordChannelId = "123456789012345678";
                Assert.Throws<InvalidOperationException>(() => channelOnly.Validate());

                var badChannel = MakeSettings();
                badChannel.FableBotDiscordBotToken = PlausibleToken;
                badChannel.FableBotDiscordChannelId = "not-a-snowflake";
                Assert.Throws<InvalidOperationException>(() => badChannel.Validate());

                var badToken = MakeSettings();
                badToken.FableBotDiscordBotToken = "Bot " + PlausibleToken;
                badToken.FableBotDiscordChannelId = "123456789012345678";
                Assert.Throws<InvalidOperationException>(() => badToken.Validate());

                var good = MakeSettings();
                good.FableBotDiscordBotToken = PlausibleToken;
                good.FableBotDiscordChannelId = "123456789012345678";
                good.Validate();
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
