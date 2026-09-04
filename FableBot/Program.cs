#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using MultiImageClient;

namespace FableBot
{
    /// FableBot: post a message (text and/or files) into one Discord channel
    /// as a bot account. Configuration comes from the standard
    /// MultiImageClient settings.json (FableBotDiscordBotToken +
    /// FableBotDiscordChannelId). See docs/fablebot-discord-prd.md.
    public static class Program
    {
        private const string Usage = """
            FableBot — post to Discord as a bot account.

            usage:
              dotnet run --project FableBot -- --check
              dotnet run --project FableBot -- --message "text" [--file path]... [--channel id]

            options:
              --check          Verify the token and channel without posting:
                               prints the bot's username and the channel name.
              --message TEXT   Message text (max 2000 characters).
              --file PATH      Attach a file (repeatable, max 10, each <= 10 MiB).
              --channel ID     Post to this channel id instead of the settings
                               default FableBotDiscordChannelId.
              --help           Show this text.

            configuration (settings.json, or MULTIIMAGECLIENT_SETTINGS):
              FableBotDiscordBotToken    raw bot token, no "Bot " prefix
              FableBotDiscordChannelId   default target channel id (snowflake)

            exit codes: 0 posted/verified, 1 usage or configuration error,
            2 Discord rejected the request.
            """;

        public static async Task<int> Main(string[] args)
        {
            bool check = false;
            string message = "";
            string channelOverride = "";
            var filePaths = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h" or "/?":
                        Console.WriteLine(Usage);
                        return 0;
                    case "--check":
                        check = true;
                        break;
                    case "--message" when i + 1 < args.Length:
                        message = args[++i];
                        break;
                    case "--file" when i + 1 < args.Length:
                        filePaths.Add(args[++i]);
                        break;
                    case "--channel" when i + 1 < args.Length:
                        channelOverride = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                        Console.Error.WriteLine(Usage);
                        return 1;
                }
            }

            if (!check && message.Length == 0 && filePaths.Count == 0)
            {
                Console.Error.WriteLine(Usage);
                return 1;
            }

            Settings settings;
            try
            {
                settings = Settings.LoadFromFile(ResolveSettingsPath());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Settings error: {ex.Message}");
                return 1;
            }

            if (!FableBotDiscord.TryNormalizeBotToken(
                settings.FableBotDiscordBotToken, out var token))
            {
                Console.Error.WriteLine(
                    "FableBotDiscordBotToken is not set in settings.json. Fill in FableBotDiscordBotToken and FableBotDiscordChannelId; see docs/fablebot-discord-prd.md.");
                return 1;
            }
            var channelRaw = channelOverride.Length > 0
                ? channelOverride
                : settings.FableBotDiscordChannelId;
            if (!FableBotDiscord.TryNormalizeChannelId(channelRaw, out var channelId))
            {
                Console.Error.WriteLine(
                    channelOverride.Length > 0
                        ? $"--channel '{channelOverride}' is not a numeric Discord channel id."
                        : "FableBotDiscordChannelId is not set in settings.json (or is not a numeric channel id). See docs/fablebot-discord-prd.md.");
                return 1;
            }

            var attachments = new List<FableBotAttachment>();
            foreach (var path in filePaths)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"--file '{path}' does not exist.");
                    return 1;
                }
                attachments.Add(new FableBotAttachment(
                    Path.GetFileName(path),
                    await File.ReadAllBytesAsync(path)));
            }

            var client = new FableBotDiscordClient(token);
            try
            {
                var identity = await client.GetBotIdentityAsync(CancellationToken.None);
                var channel = await client.GetChannelAsync(channelId, CancellationToken.None);
                Console.WriteLine(
                    $"bot: {identity.Username} (id {identity.Id})  channel: #{channel.Name} (id {channel.Id}, type {DescribeChannelType(channel.Type)})");

                if (check)
                {
                    Console.WriteLine("check: PASS — the token works and the bot can see the channel.");
                    return 0;
                }

                var posted = await client.PostMessageAsync(
                    channelId, message, attachments, channel.GuildId, CancellationToken.None);
                Console.WriteLine($"posted message {posted.MessageId}: {posted.JumpUrl}");
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        private static string DescribeChannelType(int type)
        {
            return type switch
            {
                0 => "guild text",
                1 => "DM",
                2 => "guild voice",
                5 => "announcement",
                10 or 11 or 12 => "thread",
                15 => "forum",
                _ => $"unknown ({type})",
            };
        }

        // Mirrors MultiImageClient/Program.ResolveSettingsPath: an explicit
        // MULTIIMAGECLIENT_SETTINGS path wins and stays fail-closed; otherwise
        // search the obvious repo locations for settings.json.
        private static string ResolveSettingsPath()
        {
            var configuredPath = Environment.GetEnvironmentVariable("MULTIIMAGECLIENT_SETTINGS");
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(Settings.ExpandPath(configuredPath.Trim()));
            }

            var candidates = new[]
            {
                "settings.json",
                Path.Combine("MultiImageClient", "settings.json"),
                Path.Combine("..", "MultiImageClient", "settings.json"),
                Path.Combine(AppContext.BaseDirectory, "settings.json"),
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return "settings.json";
        }
    }
}
