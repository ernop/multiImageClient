#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiImageClient
{
    public sealed record UiPublicShareAsset(string Kind, string Generator, int Index, string Label);
    public sealed record UiPublicShareText(string Label, string Text);
    public sealed record UiPublicShareSnapshot(string Prompt, List<UiPublicShareAsset> Assets,
        List<UiPublicShareText> Additions, List<UiPublicShareText> TextOutputs);
    public sealed record UiPublicShareRecord
    {
        public int Version { get; init; } = 1;
        public string Token { get; init; } = "";
        public bool IsPage { get; init; }
        public string PageToken { get; init; } = "";
        public string JobId { get; init; } = "";
        public string Generator { get; init; } = "";
        public int ImageIndex { get; init; }
        public string Owner { get; init; } = "";
        public string State { get; init; } = "draft";
        public long CreatedAt { get; init; }
        public long ExpiresAt { get; init; }
        public string DestinationHash { get; init; } = "";
        public string GuildId { get; init; } = "";
        public string ChannelId { get; init; } = "";
        public string ServerName { get; init; } = "";
        public string ChannelName { get; init; } = "";
        public string PublicUrl { get; init; } = "";
        public string ThreadDay { get; init; } = "";
        public string ThreadName { get; init; } = "";
        public UiPublicShareSnapshot Snapshot { get; init; } = new("", new(), new(), new());
        public bool Published => IsPage ? State == "public" : State is "pending" or "sent";
    }

    // Records contain a fixed allowlist, never event envelopes or arbitrary asset URLs.
    // Read one record per request; original bytes remain in the existing disk/B2 store.
    public sealed class UiPublicShareStore
    {
        private readonly string _folder;
        private readonly string _pagesFolder;
        private readonly object _gate = new();
        private sealed record PageIndex(int Version, string JobId, string Token);
        public UiPublicShareStore(string dataRoot)
        {
            _folder = Path.Combine(dataRoot, "UiPublicShares");
            Directory.CreateDirectory(_folder);
            _pagesFolder = Path.Combine(_folder, "prompts");
            Directory.CreateDirectory(_pagesFolder);
        }
        public static bool ValidToken(string token) => Regex.IsMatch(token, "\\A[a-f0-9]{64}\\z");
        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        public UiPublicShareRecord? Get(string token)
        {
            if (!ValidToken(token)) return null;
            lock (_gate)
            {
                var path = Path.Combine(_folder, token + ".json");
                if (!File.Exists(path)) return null;
                var record = JsonSerializer.Deserialize<UiPublicShareRecord>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Invalid public share record.");
                if (record.Version != 1 || record.Token != token
                    || (record.IsPage ? record.State is not ("draft" or "public") : record.State is not ("draft" or "pending" or "sent"))
                    || (record.PageToken.Length > 0 && (record.IsPage || !ValidToken(record.PageToken))))
                    throw new InvalidDataException("Invalid public share identity or state.");
                return record;
            }
        }
        public void Save(UiPublicShareRecord record)
        {
            if (!ValidToken(record.Token)) throw new InvalidDataException("Invalid share token.");
            lock (_gate)
            {
                Write(Path.Combine(_folder, record.Token + ".json"), JsonSerializer.Serialize(record));
            }
        }

        // The exact job identity selects a durable page. Preview/send identities remain separate.
        public UiPublicShareRecord GetOrCreatePage(string jobId, UiPublicShareSnapshot snapshot)
        {
            lock (_gate)
            {
                var path = PageIndexPath(jobId);
                if (File.Exists(path)) return ReadPage(jobId, snapshot);
                UiPublicShareRecord? page = null;
                foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    var candidate = Get(Path.GetFileNameWithoutExtension(file))!;
                    if (candidate.JobId != jobId || candidate.PageToken.Length > 0 || (!candidate.IsPage && !candidate.Published)) continue;
                    RequireSameSnapshot(candidate, jobId, snapshot);
                    // Adopt the earliest existing publication for this exact run, preserving old links.
                    if (page == null || (candidate.Published && !page.Published)
                        || (candidate.Published == page.Published && (candidate.CreatedAt < page.CreatedAt
                            || (candidate.CreatedAt == page.CreatedAt && string.CompareOrdinal(candidate.Token, page.Token) < 0))))
                        page = candidate;
                }
                if (page == null)
                {
                    page = new UiPublicShareRecord { Token = NewToken(), IsPage = true, JobId = jobId,
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Snapshot = snapshot };
                    Save(page);
                }
                Write(path, JsonSerializer.Serialize(new PageIndex(1, jobId, page.Token)));
                return page;
            }
        }

        public UiPublicShareRecord PageForShare(UiPublicShareRecord share)
        {
            lock (_gate)
            {
                if (share.IsPage || !ValidToken(share.PageToken))
                    throw new InvalidOperationException("Open a new sharing preview before sending.");
                var page = ReadPage(share.JobId, share.Snapshot);
                if (page.Token != share.PageToken) throw new InvalidDataException("The public page identity changed.");
                return page;
            }
        }

        public void PublishPage(UiPublicShareRecord share)
        {
            lock (_gate)
            {
                var page = PageForShare(share);
                if (!page.Published) Save(page with { State = "public" });
            }
        }

        private UiPublicShareRecord ReadPage(string jobId, UiPublicShareSnapshot snapshot)
        {
            var index = JsonSerializer.Deserialize<PageIndex>(File.ReadAllText(PageIndexPath(jobId)))
                ?? throw new InvalidDataException("Missing public page identity.");
            if (index.Version != 1 || index.JobId != jobId || !ValidToken(index.Token))
                throw new InvalidDataException("Invalid public page identity.");
            var page = Get(index.Token) ?? throw new InvalidDataException("The public page record is missing.");
            if (page.PageToken.Length > 0 || (!page.IsPage && !page.Published))
                throw new InvalidDataException("The public page record is not a page.");
            RequireSameSnapshot(page, jobId, snapshot);
            return page;
        }

        private static void RequireSameSnapshot(UiPublicShareRecord page, string jobId, UiPublicShareSnapshot snapshot)
        {
            if (page.JobId != jobId || !UiPublicShares.SameSnapshot(page.Snapshot, snapshot))
                throw new InvalidOperationException("The prompt differs from its saved public page. Sharing stopped.");
        }

        private string PageIndexPath(string jobId) => Path.Combine(_pagesFolder,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))).ToLowerInvariant() + ".json");

        private static void Write(string path, string json)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void PruneExpiredDrafts()
        {
            lock (_gate)
            {
                foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    var record = Get(Path.GetFileNameWithoutExtension(path));
                    if (record is { IsPage: false, State: "draft" } && record.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        File.Delete(path);
                }
            }
        }
    }

    public static class UiPublicShares
    {
        public const string LinkLabel = "View prompt";
        public const string ReuseLabel = "Make your own";
        public const string Disclosure = "Anyone with this link can see the prompt, inputs, all outputs, and contact sheet.";

        public static bool IsPublicRequest(string path, string method) =>
            UiDiscordAccountEndpoints.IsPublicRequest(path, method)
            || method == "GET" && Regex.IsMatch(path, "\\A/public/[a-f0-9]{64}/(?:asset/[0-9]+|reuse)?\\z")
            || method == "POST" && Regex.IsMatch(path, "\\A/public/[a-f0-9]{64}/reuse\\z");

        public static string BaseUrl(Settings settings)
        {
            var raw = settings.UiPublicShareBaseUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
                || !Regex.IsMatch(uri.AbsolutePath, "\\A/shared/[a-z0-9][a-z0-9-]{0,63}\\z"))
                throw new InvalidOperationException("Configure a separate HTTPS public share address ending in /shared/<environment-name>.");
            if (Uri.TryCreate(settings.UiPublicBaseUrl, UriKind.Absolute, out var privateUri)
                && uri.Host == privateUri.Host
                && (uri.AbsolutePath.StartsWith(privateUri.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)
                    || uri.AbsolutePath == privateUri.AbsolutePath.TrimEnd('/')))
                throw new InvalidOperationException("The public share address must be outside the private site path.");
            return raw;
        }
        public static bool SameSnapshot(UiPublicShareSnapshot left, UiPublicShareSnapshot right) =>
            JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
        public static int SelectedSlot(UiPublicShareRecord record)
        {
            var slot = record.Snapshot.Assets.FindIndex(asset => asset.Generator == record.Generator
                && asset.Index == record.ImageIndex && asset.Kind is "image" or "video");
            return slot >= 0 ? slot : throw new InvalidDataException("The selected output is absent from the public page.");
        }
        public static string ViewUrl(string publicUrl, int? outputSlot) =>
            outputSlot is int slot ? publicUrl + "#output-" + slot : publicUrl;
        public static string Caption(string publicUrl, int? outputSlot = null) =>
            $"[{LinkLabel}](<{ViewUrl(publicUrl, outputSlot)}>) · [{ReuseLabel}](<{publicUrl}reuse>)";
        public static string DestinationHash(Settings settings, string target = "vibecoders") => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            target + "\n" + settings.DiscordBotTestingGuildId + "\n" + settings.DiscordBotTestingChannelId + "\n" + settings.DiscordVibecodersWebhookUrl + "\n" + settings.DiscordVibecodersBotToken + "\n" + settings.DiscordVibecodersThreadStorePath)));

        public static UiPublicShareSnapshot Capture(UiJob job)
        {
            if (!job.IsDone) throw new InvalidOperationException("Wait for the prompt and contact sheet to finish before sharing.");
            var assets = new List<UiPublicShareAsset>();
            var additions = new List<UiPublicShareText>();
            var texts = new List<UiPublicShareText>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < job.InputImageCount; i++) assets.Add(new("input", "input", i, $"Input {i + 1}"));
            foreach (var line in job.ReadFrom(0).Events)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "accepted" && root.TryGetProperty("generatorExtraTexts", out var extra))
                    foreach (var entry in extra.EnumerateObject())
                        if (!string.IsNullOrWhiteSpace(entry.Value.GetString())) additions.Add(new(entry.Name, entry.Value.GetString()!));
                if (type == "grid")
                {
                    if (!identities.Add("grid/0")) throw new InvalidDataException("Duplicate contact sheet identity.");
                    assets.Add(new("grid", "grid", 0, "Contact sheet"));
                }
                if (type != "gen-result" || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) continue;
                var gen = root.GetProperty("gen").GetString() ?? throw new InvalidDataException("Missing generator.");
                if (root.TryGetProperty("resultKind", out var resultKind) && resultKind.GetString() == "text")
                {
                    foreach (var text in root.GetProperty("texts").EnumerateArray())
                    {
                        texts.Add(new(gen, text.GetProperty("text").GetString() ?? ""));
                        if (text.TryGetProperty("comments", out var comments) && !string.IsNullOrEmpty(comments.GetString()))
                            texts.Add(new(gen + " comments", comments.GetString()!));
                    }
                    continue;
                }
                var kind = root.TryGetProperty("mediaType", out var mediaType) && mediaType.GetString() == "video" ? "video" : "image";
                var images = root.GetProperty("images");
                for (var i = 0; i < images.GetArrayLength(); i++)
                {
                    if (!identities.Add(gen + "/" + i)) throw new InvalidDataException("Duplicate output identity.");
                    assets.Add(new(kind, gen, i, gen + " · " + (i + 1)));
                }
            }
            if (assets.Count > 128 || additions.Count > 64 || texts.Count > 128)
                throw new InvalidDataException("This prompt exceeds the public page size limit.");
            return new(job.Prompt, assets, additions, texts);
        }

        public static string Html(UiPublicShareRecord record, string assetBase, string? reuseUrl, string? loginError = null, bool login = false,
            string? requestAccountUrl = null)
        {
            static string E(string value) => WebUtility.HtmlEncode(value);
            var body = new StringBuilder("""
                <!doctype html><html lang="en"><meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="robots" content="noindex,nofollow,noarchive"><title>Shared prompt</title>
                <style>
                body{font:16px/1.5 system-ui;margin:24px auto;padding:0 18px;max-width:1000px;color:#182034;background:#f7f8fc}
                h1{font-size:24px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;background:white;padding:16px;border:1px solid #d8dcec;border-radius:8px}
                figure{margin:16px 0;padding:12px;border:2px solid transparent;border-radius:10px;scroll-margin-top:16px}
                figure:target{border-color:#3548c8;background:#e9edff}
                .selected-output{display:none;margin-right:12px;font-weight:700;color:#2638ab}
                figure:target .selected-output{display:inline}
                .output-links{display:flex;flex-wrap:wrap;gap:12px;margin:8px 0}
                img,video{display:block;width:100%;height:auto;aspect-ratio:4/3;max-height:65vh;object-fit:contain;border-radius:8px;background:#edf1f9}
                a{color:#3548c8}button,.action{display:inline-block;padding:9px 15px;border:0;border-radius:6px;background:#3548c8;color:white;text-decoration:none}
                input{padding:9px;margin:6px}label{display:block}section{margin:24px 0}
                figcaption{margin-bottom:8px}nav{display:flex;gap:16px;flex-wrap:wrap;margin:16px 0}
                </style><h1>Shared prompt</h1>
                """);
            if (reuseUrl != null) body.Append("<a class=\"action\" href=\"").Append(E(reuseUrl)).Append("\">Make your own</a>");
            if (requestAccountUrl != null) body.Append(" <a class=\"action\" href=\"").Append(E(requestAccountUrl)).Append("\">Request account</a>");
            if (login)
            {
                body.Append(requestAccountUrl == null
                    ? "<p>Log in to use or edit this prompt. Need an account? Ask Ernie in Discord.</p>"
                    : "<p>Log in to use or edit this prompt. Select Request account to receive a signup link through Discord.</p>");
                if (loginError != null) body.Append("<p role=\"alert\">").Append(E(loginError)).Append("</p>");
                body.Append("<form method=\"post\" action=\"reuse\"><label>Username <input name=\"username\" autocomplete=\"username\" required></label><label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label><button>Log in and make your own</button></form>");
            }
            var outputCount = record.Snapshot.Assets.Count(asset => asset.Kind is "image" or "video");
            body.Append("<p>").Append(outputCount).Append(outputCount == 1 ? " output from this prompt.</p>" : " outputs from this prompt.</p>");
            body.Append("<nav aria-label=\"This prompt\"><a href=\"#prompt\">Prompt</a>");
            if (outputCount > 0) body.Append("<a href=\"#outputs\">All outputs</a>");
            if (record.Snapshot.Assets.Any(asset => asset.Kind == "grid")) body.Append("<a href=\"#contact-sheet\">Contact sheet</a>");
            body.Append("</nav>");
            body.Append("<h2 id=\"prompt\">Prompt</h2><pre>").Append(E(record.Snapshot.Prompt)).Append("</pre>");
            foreach (var extra in record.Snapshot.Additions)
                body.Append("<details><summary>").Append(E(extra.Label)).Append(" appended text</summary><pre>").Append(E(extra.Text)).Append("</pre></details>");
            foreach (var group in new[] { "input", "output", "grid" })
            {
                var matching = record.Snapshot.Assets.Select((asset, index) => (asset, index)).Where(pair => group == "output" ? pair.asset.Kind is "image" or "video" : pair.asset.Kind == group).ToList();
                if (matching.Count == 0) continue;
                var sectionId = group == "input" ? "inputs" : group == "grid" ? "contact-sheet" : "outputs";
                body.Append("<section id=\"").Append(sectionId).Append("\"><h2>").Append(group == "input" ? "Inputs" : group == "grid" ? "Contact sheet" : "Outputs").Append("</h2>");
                foreach (var (asset, index) in matching)
                {
                    var url = E(assetBase + index);
                    body.Append("<figure");
                    if (group == "output") body.Append(" id=\"output-").Append(index).Append("\"");
                    body.Append(">");
                    body.Append("<figcaption>");
                    if (group == "output") body.Append("<span class=\"selected-output\">Selected output</span>");
                    body.Append(E(asset.Label)).Append("</figcaption>");
                    if (group == "output")
                    {
                        body.Append("<div class=\"output-links\"><a href=\"#prompt\">View prompt</a><a href=\"#outputs\">All outputs</a>");
                        if (requestAccountUrl != null) body.Append("<a href=\"").Append(E(requestAccountUrl)).Append("\">Request account</a>");
                        body.Append("</div>");
                    }
                    if (asset.Kind == "video") body.Append("<video controls preload=\"metadata\" src=\"").Append(url).Append("\"></video>");
                    else body.Append("<a href=\"").Append(url).Append("\"><img loading=\"lazy\" alt=\"").Append(E(asset.Label)).Append("\" src=\"").Append(url).Append("?thumb=1\"></a>");
                    body.Append("</figure>");
                }
                body.Append("</section>");
            }
            foreach (var text in record.Snapshot.TextOutputs)
                body.Append("<h2>").Append(E(text.Label)).Append("</h2><pre>").Append(E(text.Text)).Append("</pre>");
            return body.Append("</html>").ToString();
        }
    }
}
