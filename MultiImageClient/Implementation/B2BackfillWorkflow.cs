#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// Stage 5 of docs/b2-image-hosting-plan.md: one-shot migration of
    /// pre-hosting UI history into B2. For every raster result/grid image in
    /// UiHistory without a CdnKey: verify the recorded SHA-256 against the
    /// file bytes, upload (retry x3), record CdnKey/CdnFileId, then rewrite
    /// the job's persisted events so gen-result images[] / grid url carry the
    /// B2 URLs (with index-aligned local thumbs[] added, matching live
    /// emission). Local raws are evicted only when B2KeepLocalRawImages is
    /// false AND the job's rewritten events verifiably contain no remaining
    /// local full-res reference to the evicted keys.
    ///
    /// Run with the UI server STOPPED — this tool and the server must not
    /// both write images.json/events.jsonl.
    ///
    /// Never touched: input images, videos/SVG (v1 scope), streamed-partial
    /// snapshots and kept previews (failure artifacts stay local), and hidden
    /// prompts/images (one-way hidden content is never published to the
    /// public bucket; its raws simply stay local).
    /// </summary>
    public static class B2BackfillWorkflow
    {
        private static readonly HashSet<string> HostableContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png", "image/jpeg", "image/webp", "image/gif",
        };

        public static async Task<int> RunAsync(Settings settings, bool dryRun)
        {
            var root = Path.Combine(settings.ImageDownloadBaseFolder, "UiHistory");
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"b2-backfill: no UiHistory folder at {root}; nothing to do.");
                return 2;
            }

            var b2 = new B2StorageClient(settings);
            var visibility = new UiVisibilityStore(settings);
            var mode = dryRun ? "DRY RUN" : "live";
            var evictionMode = settings.B2KeepLocalRawImages ? "keep local raws" : "evict verified raws";
            Logger.Log($"b2-backfill: starting ({mode}, {evictionMode}) over {root}");

            var entries = UiJobStorage.ScanIndex(root)
                .OrderBy(e => e.CreatedAt)
                .ToList();

            int jobsTouched = 0, uploads = 0, rewrites = 0, evictedFiles = 0;
            long uploadBytes = 0, evictedBytes = 0;
            int skippedHiddenJobs = 0, skippedHiddenImages = 0, missingFiles = 0;
            var failures = new List<string>();

            foreach (var entry in entries)
            {
                if (visibility.IsPromptHidden(entry.Id))
                {
                    skippedHiddenJobs++;
                    Logger.Log($"b2-backfill: [{entry.Id}] hidden prompt — whole job skipped, raws stay local.");
                    continue;
                }

                var job = UiJobStorage.TryLoad(root, entry.FolderName);
                if (job == null)
                {
                    failures.Add($"{entry.Id}: job folder failed to load");
                    continue;
                }

                var candidates = new List<(UiPersistedImageInfo Info, string Gen, int Index)>();
                foreach (var info in job.ListPersistedImages())
                {
                    if (!TrySplitKey(info.Key, out var gen, out var index)
                        || gen.StartsWith("input", StringComparison.OrdinalIgnoreCase)
                        || gen.Contains("~p", StringComparison.Ordinal)
                        || !HostableContentTypes.Contains(info.ContentType)
                        || string.IsNullOrWhiteSpace(info.Path)
                        || job.IsPartialsBackedPath(info.Path))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(info.CdnKey))
                    {
                        continue; // already hosted
                    }
                    if (visibility.IsImageHidden(job.Id, gen, index))
                    {
                        skippedHiddenImages++;
                        continue;
                    }
                    candidates.Add((info, gen, index));
                }

                var jobFailed = false;
                foreach (var (info, gen, index) in candidates)
                {
                    if (!File.Exists(info.Path))
                    {
                        missingFiles++;
                        Logger.Log($"b2-backfill: [{job.Id}] {info.Key}: local file missing ({info.Path}) — cannot host, event URL left as-is.");
                        continue;
                    }

                    var actualSha = ComputeSha256Hex(info.Path);
                    if (!string.IsNullOrWhiteSpace(info.ContentSha256)
                        && !string.Equals(info.ContentSha256, actualSha, StringComparison.OrdinalIgnoreCase))
                    {
                        jobFailed = true;
                        failures.Add($"{job.Id}/{info.Key}: file bytes do not match recorded SHA-256 — refusing to upload unverifiable data");
                        continue;
                    }

                    var size = new FileInfo(info.Path).Length;
                    if (dryRun)
                    {
                        uploads++;
                        uploadBytes += size;
                        continue;
                    }

                    try
                    {
                        await UploadWithRetryAsync(b2, job, gen, index, info.Path, info.ContentType);
                        uploads++;
                        uploadBytes += size;
                    }
                    catch (Exception ex)
                    {
                        jobFailed = true;
                        failures.Add($"{job.Id}/{info.Key}: upload failed after retries: {ex.Message}");
                    }
                }

                if (candidates.Count > 0)
                {
                    jobsTouched++;
                }

                if (dryRun)
                {
                    continue;
                }

                // Rewrite events for every hosted image (including ones hosted
                // by an earlier interrupted run whose rewrite never happened).
                var urlMap = BuildUrlMap(job, b2);
                if (urlMap.Count == 0)
                {
                    continue;
                }
                bool rewriteOk;
                try
                {
                    rewriteOk = RewriteEvents(job, urlMap, out var changed);
                    if (rewriteOk && changed)
                    {
                        rewrites++;
                    }
                }
                catch (Exception ex)
                {
                    rewriteOk = false;
                    failures.Add($"{job.Id}: event rewrite failed: {ex.Message}");
                }
                if (!rewriteOk)
                {
                    jobFailed = true;
                }

                // Closing check before any deletion: the persisted events must
                // contain no remaining local full-res reference to a key whose
                // local file is about to disappear.
                if (!settings.B2KeepLocalRawImages && !jobFailed)
                {
                    if (!VerifyNoLocalFullResReferences(job, urlMap, out var leftover))
                    {
                        failures.Add($"{job.Id}: events still reference local full-res URL {leftover} after rewrite — eviction skipped");
                        continue;
                    }
                    long before = MeasureEvictableBytes(job);
                    var evicted = job.EvictHostedLocalRaws();
                    evictedFiles += evicted;
                    evictedBytes += before;
                }
            }

            var summary =
                $"b2-backfill {mode}: {entries.Count} jobs scanned, {jobsTouched} with work, "
                + $"{uploads} upload(s) ({FormatBytes(uploadBytes)}), {rewrites} event log(s) rewritten, "
                + $"{evictedFiles} local file(s) evicted ({FormatBytes(evictedBytes)}), "
                + $"{missingFiles} missing file(s), {skippedHiddenJobs} hidden job(s) + {skippedHiddenImages} hidden image(s) skipped, "
                + $"{failures.Count} failure(s).";
            Logger.Log(summary);
            Console.WriteLine(summary);
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"b2-backfill FAILURE: {failure}");
            }
            return failures.Count == 0 ? 0 : 1;
        }

        /// Mirrors UiJobRunner.UploadResultToB2Async: 3 attempts with short
        /// backoff, CdnKey/CdnFileId persisted only after the checksum-verified
        /// upload, exception after the final attempt.
        private static async Task UploadWithRetryAsync(
            B2StorageClient b2, UiJob job, string gen, int index, string path, string contentType)
        {
            var objectKey = B2StorageClient.BuildObjectKey(
                job.Id, gen, index, UiJobRunner.ExtensionForHostedFile(contentType, path));
            const int attempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var fileId = await b2.UploadFileAsync(
                        path, objectKey, contentType, Path.GetFileName(path), CancellationToken.None);
                    if (!job.StoreCdnReference(gen, index, objectKey, fileId))
                    {
                        throw new InvalidOperationException(
                            $"uploaded {objectKey} but could not persist its CDN reference");
                    }
                    return;
                }
                catch (Exception ex) when (attempt < attempts)
                {
                    Logger.Log($"b2-backfill: [{job.Id}] upload attempt {attempt}/{attempts} for {gen}/{index} failed: {ex.Message}; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        /// Exact local URL -> B2 URL for every hosted, non-input, non-partial
        /// image of the job. Keys are the URL strings emitted by the live
        /// server, so replacement is exact-identity, never fuzzy.
        private static Dictionary<string, string> BuildUrlMap(UiJob job, B2StorageClient b2)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var info in job.ListPersistedImages())
            {
                if (string.IsNullOrWhiteSpace(info.CdnKey)
                    || !TrySplitKey(info.Key, out var gen, out _)
                    || gen.StartsWith("input", StringComparison.OrdinalIgnoreCase)
                    || gen.Contains("~p", StringComparison.Ordinal))
                {
                    continue;
                }
                map[$"/api/jobs/{job.Id}/images/{info.Key}"] = b2.DownloadUrlFor(info.CdnKey);
            }
            return map;
        }

        /// Rewrites gen-result images[] and grid url entries through the map.
        /// thumbs[] is added to a rewritten gen-result only when absent and
        /// only when every pre-rewrite entry was a local URL (the pre-hosting
        /// event shape); post-hosting events already carry thumbs. Unrelated
        /// lines are preserved byte-for-byte.
        private static bool RewriteEvents(UiJob job, Dictionary<string, string> map, out bool changed)
        {
            changed = false;
            var (lines, _) = job.ReadFrom(0);
            var output = new List<string>(lines.Count);

            foreach (var line in lines)
            {
                var node = JsonNode.Parse(line);
                var type = node?["type"]?.GetValue<string>();
                var lineChanged = false;

                if (type == "gen-result" && node!["images"] is JsonArray images && images.Count > 0)
                {
                    var originals = new List<string?>();
                    foreach (var element in images)
                    {
                        originals.Add(element?.GetValue<string>());
                    }
                    for (var i = 0; i < images.Count; i++)
                    {
                        var url = originals[i];
                        if (url != null && map.TryGetValue(url, out var b2Url))
                        {
                            images[i] = b2Url;
                            lineChanged = true;
                        }
                    }
                    if (lineChanged
                        && node["thumbs"] == null
                        && originals.All(u => u != null && u.StartsWith("/api/", StringComparison.Ordinal)))
                    {
                        var thumbs = new JsonArray();
                        foreach (var url in originals)
                        {
                            thumbs.Add(url + "?thumb=1");
                        }
                        node["thumbs"] = thumbs;
                    }
                }
                else if (type == "grid")
                {
                    var url = node!["url"]?.GetValue<string>();
                    if (url != null && map.TryGetValue(url, out var b2Url))
                    {
                        node["url"] = b2Url;
                        lineChanged = true;
                    }
                }

                if (lineChanged)
                {
                    changed = true;
                    output.Add(node!.ToJsonString());
                }
                else
                {
                    output.Add(line);
                }
            }

            if (!changed)
            {
                return true;
            }
            return job.TryReplacePersistedEvents(output);
        }

        /// Re-reads the PERSISTED events and confirms no gen-result images[]
        /// or grid url still carries a mapped local full-res URL. gen-partial
        /// and kept-preview (partialImages) URLs deliberately stay local and
        /// are not checked — their artifacts are never evicted.
        private static bool VerifyNoLocalFullResReferences(
            UiJob job, Dictionary<string, string> map, out string leftover)
        {
            leftover = "";
            var (lines, _) = job.ReadFrom(0);
            foreach (var line in lines)
            {
                var node = JsonNode.Parse(line);
                var type = node?["type"]?.GetValue<string>();
                if (type == "gen-result" && node!["images"] is JsonArray images)
                {
                    foreach (var element in images)
                    {
                        var url = element?.GetValue<string>();
                        if (url != null && map.ContainsKey(url))
                        {
                            leftover = url;
                            return false;
                        }
                    }
                }
                else if (type == "grid")
                {
                    var url = node!["url"]?.GetValue<string>();
                    if (url != null && map.ContainsKey(url))
                    {
                        leftover = url;
                        return false;
                    }
                }
            }
            return true;
        }

        private static long MeasureEvictableBytes(UiJob job)
        {
            long total = 0;
            foreach (var info in job.ListPersistedImages())
            {
                if (!string.IsNullOrWhiteSpace(info.CdnKey)
                    && !string.IsNullOrWhiteSpace(info.Path)
                    && !info.Key.StartsWith("input/", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(info.Path))
                {
                    total += new FileInfo(info.Path).Length;
                }
            }
            return total;
        }

        private static bool TrySplitKey(string key, out string gen, out int index)
        {
            gen = "";
            index = -1;
            var slash = key.LastIndexOf('/');
            if (slash <= 0 || !int.TryParse(key.Substring(slash + 1), out index))
            {
                return false;
            }
            gen = key.Substring(0, slash);
            return true;
        }

        private static string ComputeSha256Hex(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string FormatBytes(long bytes)
        {
            return bytes >= 1024L * 1024 * 1024
                ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB"
                : $"{bytes / (1024.0 * 1024):F1} MiB";
        }
    }
}
