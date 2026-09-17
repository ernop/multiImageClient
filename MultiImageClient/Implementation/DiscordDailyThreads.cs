#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed record DiscordDailyThreadRecord(string GuildId, string ChannelId, string Day, string Name, string? ThreadId);

    public static class DiscordDailyThreads
    {
        private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        public static string Day(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Pacific).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public static string Name(string day) => "Daily " + DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .ToString("dddd, MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) + " image thread";

        // All app instances posting into a channel must use this same durable directory.
        // A file lease serializes processes. A pending record survives an uncertain create response.
        public static async Task<string> GetOrCreateAsync(string folder, string guildId, string channelId, string day,
            Func<Task<string>> create, Func<string, Task> validate)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                throw new InvalidOperationException("Configure an absolute shared Discord daily-thread store path before sharing.");
            if (!ulong.TryParse(guildId, out _) || !ulong.TryParse(channelId, out _))
                throw new InvalidOperationException("Invalid Discord destination identity.");
            var name = Name(day);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, guildId + "-" + channelId + "-" + day + ".json");
            FileStream lease;
            try { lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Another image send is preparing this day's thread. Try again after it finishes."); }
            using (lease)
            {
                if (File.Exists(path))
                {
                    var saved = JsonSerializer.Deserialize<DiscordDailyThreadRecord>(File.ReadAllText(path));
                    if (saved == null || saved.GuildId != guildId || saved.ChannelId != channelId || saved.Day != day || saved.Name != name)
                        throw new InvalidOperationException("The daily thread record has a different identity. Sharing stopped.");
                    if (saved.ThreadId == null)
                        throw new InvalidOperationException("Daily thread creation is unconfirmed. Ask Ernie to check Discord before another send.");
                    await validate(saved.ThreadId);
                    return saved.ThreadId;
                }
                var record = new DiscordDailyThreadRecord(guildId, channelId, day, name, null);
                Save(path, record);
                // Do not remove this marker on failure: Discord may have created the thread.
                var threadId = await create();
                if (!ulong.TryParse(threadId, out var id) || id == 0)
                    throw new InvalidOperationException("Discord returned an invalid thread identity.");
                Save(path, record with { ThreadId = threadId });
                return threadId;
            }
        }

        private static void Save(string path, DiscordDailyThreadRecord record)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, record);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
