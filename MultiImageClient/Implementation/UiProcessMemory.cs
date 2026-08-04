using System;
using System.Diagnostics;
using System.IO;

namespace MultiImageClient
{
    /// Process + cgroup memory snapshot for the shared-site status UI.
    public static class UiProcessMemory
    {
        public static object Snapshot()
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var (previewEntries, previewBytes) = UiCardPreviewCache.Snapshot();
            return new
            {
                workingSetBytes = proc.WorkingSet64,
                privateMemoryBytes = proc.PrivateMemorySize64,
                managedHeapBytes = GC.GetTotalMemory(false),
                cgroupCurrentBytes = ReadCgroupBytes("memory.current"),
                cgroupHighBytes = ReadCgroupBytes("memory.high"),
                cgroupMaxBytes = ReadCgroupBytes("memory.max"),
                cardPreviewCacheEntries = previewEntries,
                cardPreviewCacheBytes = previewBytes,
            };
        }

        private static long? ReadCgroupBytes(string fileName)
        {
            foreach (var path in CandidatePaths(fileName))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length == 0 || text.Equals("max", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    if (long.TryParse(text, out var value) && value >= 0)
                    {
                        return value;
                    }
                }
                catch
                {
                    // Not in a cgroup, or unreadable — omit the field.
                }
            }
            return null;
        }

        private static string[] CandidatePaths(string fileName) => new[]
        {
            Path.Combine("/sys/fs/cgroup", fileName),
            Path.Combine("/sys/fs/cgroup/system.slice/multiimageclient-ui.service", fileName),
        };
    }
}
