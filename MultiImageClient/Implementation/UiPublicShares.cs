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
        public bool Published => State is "pending" or "sent";
    }

    // Records contain a fixed allowlist, never event envelopes or arbitrary asset URLs.
    // Read one record per request; original bytes remain in the existing disk/B2 store.
    public sealed class UiPublicShareStore
    {
        private readonly string _folder;
        private readonly object _gate = new();
        public UiPublicShareStore(string dataRoot)
        {
            _folder = Path.Combine(dataRoot, "UiPublicShares");
            Directory.CreateDirectory(_folder);
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
                if (record.Version != 1 || record.Token != token || record.State is not ("draft" or "pending" or "sent"))
                    throw new InvalidDataException("Invalid public share identity or state.");
                return record;
            }
        }
        public void Save(UiPublicShareRecord record)
        {
            if (!ValidToken(record.Token)) throw new InvalidDataException("Invalid share token.");
            lock (_gate)
            {
                var path = Path.Combine(_folder, record.Token + ".json");
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(record));
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        public void PruneExpiredDrafts()
        {
            lock (_gate)
            {
                foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    var record = Get(Path.GetFileNameWithoutExtension(path));
                    if (record?.State == "draft" && record.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
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
            method == "GET" && Regex.IsMatch(path, "\\A/public/[a-f0-9]{64}/(?:asset/[0-9]+|reuse)?\\z")
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
        public static string Caption(string publicUrl) =>
            $"[{LinkLabel}](<{publicUrl}>) · [{ReuseLabel}](<{publicUrl}reuse>)";
        public static string DestinationHash(Settings settings) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            settings.DiscordVibecodersWebhookUrl + "\n" + settings.DiscordVibecodersBotToken + "\n" + settings.DiscordVibecodersThreadStorePath)));

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

        public static string Html(UiPublicShareRecord record, string assetBase, string? reuseUrl, string? loginError = null, bool login = false)
        {
            static string E(string value) => WebUtility.HtmlEncode(value);
            var body = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex,nofollow,noarchive\"><title>Shared prompt</title><style>body{font:16px/1.5 system-ui;margin:24px auto;padding:0 18px;max-width:1000px;color:#182034;background:#f7f8fc}h1{font-size:24px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;background:white;padding:16px;border:1px solid #d8dcec;border-radius:8px}figure{margin:16px 0}img,video{max-width:100%;max-height:75vh;border-radius:8px}a{color:#3548c8}button,.action{display:inline-block;padding:9px 15px;border:0;border-radius:6px;background:#3548c8;color:white;text-decoration:none}input{padding:9px;margin:6px}label{display:block}small,figcaption{color:#515b70}section{margin:24px 0}</style><h1>Shared prompt</h1>");
            if (reuseUrl != null) body.Append("<a class=\"action\" href=\"").Append(E(reuseUrl)).Append("\">Make your own</a>");
            if (login)
            {
                body.Append("<p>Log in to use or edit this prompt. Need an account? Ask Ernie in Discord.</p>");
                if (loginError != null) body.Append("<p role=\"alert\">").Append(E(loginError)).Append("</p>");
                body.Append("<form method=\"post\" action=\"reuse\"><label>Username <input name=\"username\" autocomplete=\"username\" required></label><label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label><button>Log in and make your own</button></form>");
            }
            body.Append("<h2>Prompt</h2><pre>").Append(E(record.Snapshot.Prompt)).Append("</pre>");
            foreach (var extra in record.Snapshot.Additions)
                body.Append("<details><summary>").Append(E(extra.Label)).Append(" appended text</summary><pre>").Append(E(extra.Text)).Append("</pre></details>");
            foreach (var group in new[] { "input", "image", "video", "grid" })
            {
                var matching = record.Snapshot.Assets.Select((asset, index) => (asset, index)).Where(pair => pair.asset.Kind == group).ToList();
                if (matching.Count == 0) continue;
                body.Append("<section><h2>").Append(group == "input" ? "Inputs" : group == "grid" ? "Contact sheet" : "Outputs").Append("</h2>");
                foreach (var (asset, index) in matching)
                {
                    var url = E(assetBase + index);
                    body.Append("<figure>");
                    if (group == "video") body.Append("<video controls preload=\"metadata\" src=\"").Append(url).Append("\"></video>");
                    else body.Append("<a href=\"").Append(url).Append("\"><img loading=\"lazy\" alt=\"").Append(E(asset.Label)).Append("\" src=\"").Append(url).Append("?thumb=1\"></a>");
                    body.Append("<figcaption>").Append(E(asset.Label)).Append("</figcaption></figure>");
                }
                body.Append("</section>");
            }
            foreach (var text in record.Snapshot.TextOutputs)
                body.Append("<h2>").Append(E(text.Label)).Append("</h2><pre>").Append(E(text.Text)).Append("</pre>");
            return body.Append("</html>").ToString();
        }
    }
}
