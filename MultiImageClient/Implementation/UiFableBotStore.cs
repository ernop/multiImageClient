#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiImageClient
{
    public sealed record UiFableBotSend(string ChannelId, string JobId, string Generator, int ImageIndex,
        string Sender, long CreatedAtUnixMs, string State = "pending", string? JumpUrl = null);

    // Read individual records from disk. Do not retain the full send history in RAM.
    public sealed class UiFableBotStore
    {
        private readonly string _folder;
        private readonly object _lock = new();

        public UiFableBotStore(string dataRoot)
        {
            _folder = Path.Combine(dataRoot, "UiFableBot");
            Directory.CreateDirectory(_folder);
        }

        public UiFableBotSend? Get(string channel, string job, string generator, int index)
        {
            lock (_lock)
            {
                var path = RecordPath(channel, job, generator, index);
                if (!File.Exists(path)) return null;
                var record = JsonSerializer.Deserialize<UiFableBotSend>(File.ReadAllText(path));
                if (record == null || record.ChannelId != channel || record.JobId != job
                    || record.Generator != generator || record.ImageIndex != index
                    || record.State is not ("pending" or "sent")
                    || string.IsNullOrWhiteSpace(record.Sender) || record.CreatedAtUnixMs <= 0
                    || (record.State == "sent" && string.IsNullOrWhiteSpace(record.JumpUrl)))
                    throw new InvalidDataException("The Discord send record is malformed.");
                return record;
            }
        }

        public bool TryClaim(UiFableBotSend record)
        {
            lock (_lock)
            {
                if (Get(record.ChannelId, record.JobId, record.Generator, record.ImageIndex) != null) return false;
                Write(record);
                return true;
            }
        }

        public void Complete(UiFableBotSend record, string jumpUrl)
        {
            lock (_lock)
            {
                if (Get(record.ChannelId, record.JobId, record.Generator, record.ImageIndex) != record)
                    throw new InvalidOperationException("The Discord send claim changed.");
                Write(record with { State = "sent", JumpUrl = jumpUrl });
            }
        }

        private string RecordPath(string channel, string job, string generator, int index)
        {
            var identity = JsonSerializer.Serialize(new { channel, job, generator, index });
            return Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
        }

        private void Write(UiFableBotSend record)
        {
            var path = RecordPath(record.ChannelId, record.JobId, record.Generator, record.ImageIndex);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(record));
                File.Move(temporary, path, true);
            }
            finally { File.Delete(temporary); }
        }
    }
}
