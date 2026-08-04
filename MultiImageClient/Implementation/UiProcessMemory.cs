#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace MultiImageClient
{
    /// Process + cgroup memory snapshot for the shared-site status UI.
    public static class UiProcessMemory
    {
        public static object Snapshot(UiJobRegistry? jobs = null, UiJobRunner? runner = null)
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var (previewEntries, previewBytes) = UiCardPreviewCache.Snapshot();
            var cgroupDir = ResolveProcessCgroupDir();
            return new
            {
                workingSetBytes = proc.WorkingSet64,
                privateMemoryBytes = proc.PrivateMemorySize64,
                managedHeapBytes = GC.GetTotalMemory(false),
                cgroupCurrentBytes = ReadCgroupBytes(cgroupDir, "memory.current"),
                cgroupHighBytes = ReadCgroupBytes(cgroupDir, "memory.high"),
                cgroupMaxBytes = ReadCgroupBytes(cgroupDir, "memory.max"),
                cardPreviewCacheEntries = previewEntries,
                cardPreviewCacheBytes = previewBytes,
                liveJobCount = jobs?.LiveJobCount ?? 0,
                hydratedJobCount = jobs?.HydratedJobCount ?? 0,
                indexedJobCount = jobs?.IndexedJobCount ?? 0,
                envelopeCount = jobs?.EnvelopeCount ?? 0,
                grokBrowserConfigured = runner?.GrokBrowserConfigured ?? false,
                grokBrowserWarm = runner?.IsGrokBrowserWarm ?? false,
                metaBrowserConfigured = runner?.MetaBrowserConfigured ?? false,
                metaBrowserWarm = runner?.IsMetaBrowserWarm ?? false,
            };
        }

        // Prefer this process's cgroup (from /proc/self/cgroup). Never read the
        // cgroupv2 root memory.current first — on many hosts that file is the
        // whole-machine usage and the header would show ~host RAM as "ours".
        private static string? ResolveProcessCgroupDir()
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/self/cgroup"))
                {
                    // cgroup v2: "0::/system.slice/multiimageclient-ui.service"
                    if (!line.StartsWith("0::", StringComparison.Ordinal)) continue;
                    var rel = line.Substring(3).Trim();
                    if (rel.Length == 0 || rel == "/") return null;
                    var dir = Path.Combine("/sys/fs/cgroup", rel.TrimStart('/'));
                    if (Directory.Exists(dir)) return dir;
                }
            }
            catch
            {
                // Not Linux, or cgroup unreadable.
            }

            var fallback = "/sys/fs/cgroup/system.slice/multiimageclient-ui.service";
            return Directory.Exists(fallback) ? fallback : null;
        }

        private static long? ReadCgroupBytes(string? cgroupDir, string fileName)
        {
            if (string.IsNullOrEmpty(cgroupDir)) return null;
            try
            {
                var path = Path.Combine(cgroupDir, fileName);
                if (!File.Exists(path)) return null;
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
            return null;
        }
    }
}
