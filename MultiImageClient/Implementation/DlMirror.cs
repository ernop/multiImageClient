using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiImageClient
{
    /// Mirrors every image we write into a single flat folder (configured
    /// via <see cref="Settings.FlatImageMirrorPath"/>) so they're easy to
    /// grab / share. Copies are synchronous and best-effort: failures are
    /// logged but do not throw, since mirroring must never break a run.
    ///
    /// If the mirror path is null/empty, every call here is a no-op — this
    /// is the default, and nothing is written unless the user has opted in
    /// via settings.
    ///
    /// Collision policy: if a file with the same name already exists
    /// and has the same length, skip (assume identical). Otherwise
    /// append _1, _2, ... until unique.
    public static class DlMirror
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp",
        };

        /// Synchronously mirror one file to <paramref name="mirrorRoot"/>.
        /// No-op when <paramref name="mirrorRoot"/> is null/empty (feature
        /// disabled), or when the source doesn't exist. Returns true only
        /// when a new file was actually created at the destination.
        public static bool Copy(string sourceFilePath, string mirrorRoot)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot)) return false;
            try
            {
                if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath))
                {
                    return false;
                }
                Directory.CreateDirectory(mirrorRoot);
                var filename = Path.GetFileName(sourceFilePath);
                var dest = Path.Combine(mirrorRoot, filename);

                if (File.Exists(dest))
                {
                    var srcLen = new FileInfo(sourceFilePath).Length;
                    var dstLen = new FileInfo(dest).Length;
                    if (srcLen == dstLen) return false; // already mirrored
                    var stem = Path.GetFileNameWithoutExtension(filename);
                    var ext = Path.GetExtension(filename);
                    int i = 1;
                    do
                    {
                        dest = Path.Combine(mirrorRoot, $"{stem}_{i}{ext}");
                        i++;
                    } while (File.Exists(dest));
                }

                File.Copy(sourceFilePath, dest, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"DlMirror: failed to copy {sourceFilePath} -> {mirrorRoot}: {ex.Message}");
                return false;
            }
        }

        /// One-shot backfill: walk <paramref name="savesRoot"/> and copy
        /// every image file into <paramref name="mirrorRoot"/>. Returns
        /// the number of newly-copied files. No-op if either path is
        /// null/empty.
        public static int Backfill(string savesRoot, string mirrorRoot)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot))
            {
                Logger.Log("DlMirror.Backfill: no FlatImageMirrorPath configured; nothing to do.");
                return 0;
            }
            if (string.IsNullOrWhiteSpace(savesRoot) || !Directory.Exists(savesRoot))
            {
                Logger.Log($"DlMirror.Backfill: saves root '{savesRoot}' does not exist; nothing to do.");
                return 0;
            }
            Directory.CreateDirectory(mirrorRoot);

            var files = Directory.EnumerateFiles(savesRoot, "*", SearchOption.AllDirectories)
                .Where(p => ImageExtensions.Contains(Path.GetExtension(p)))
                .ToList();

            Logger.Log($"DlMirror.Backfill: scanning {files.Count} image file(s) under {savesRoot} -> {mirrorRoot}");

            int copied = 0;
            foreach (var f in files)
            {
                if (Copy(f, mirrorRoot)) copied++;
            }

            Logger.Log($"DlMirror.Backfill: copied {copied} new file(s) into {mirrorRoot} (the rest were already present).");
            return copied;
        }
    }
}
