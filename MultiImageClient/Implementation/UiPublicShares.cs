#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiImageClient
{
    public sealed record UiPublicShareAsset(string Kind, string Generator, int Index, string Label);
    public sealed record UiPublicShareText(string Label, string Text);
    public sealed record UiPublicShareSnapshot(string Prompt, List<UiPublicShareAsset> Assets,
        List<UiPublicShareText> Additions, List<UiPublicShareText> TextOutputs);
    public sealed record UiPublicSeriesPage(
        int Index,
        int Count,
        string JobId,
        string Title,
        string Prompt,
        int ImageIndex,
        int Width,
        int Height);
    public sealed record UiPublicSeriesSnapshot(
        string GroupId,
        string ExchangeId,
        string Generator,
        string GeneratorLabel,
        string SplitModelLabel,
        string SplitReason,
        string SourcePrompt,
        string StyleNote,
        string GeneratorExtraText,
        List<UiPublicShareAsset> Inputs,
        List<UiPublicSeriesPage> Pages,
        bool HasContactSheet);
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public UiPublicSeriesSnapshot? Series { get; init; }
        [JsonIgnore]
        public bool IsSeries => Series != null;
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

        // The exact series identity selects one durable reader page.
        public UiPublicShareRecord GetOrCreateSeriesPage(UiPublicSeriesSnapshot series)
        {
            var key = UiPublicShares.SeriesPageKey(series);
            lock (_gate)
            {
                var path = PageIndexPath(key);
                if (File.Exists(path)) return ReadSeriesPage(key, series);
                var page = new UiPublicShareRecord
                {
                    Token = NewToken(),
                    IsPage = true,
                    JobId = key,
                    Generator = series.Generator,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Series = series,
                };
                Save(page);
                Write(path, JsonSerializer.Serialize(new PageIndex(1, key, page.Token)));
                return page;
            }
        }

        public UiPublicShareRecord PageForShare(UiPublicShareRecord share)
        {
            lock (_gate)
            {
                if (share.IsPage || !ValidToken(share.PageToken))
                    throw new InvalidOperationException("Open a new sharing preview before sending.");
                var page = share.Series != null
                    ? ReadSeriesPage(UiPublicShares.SeriesPageKey(share.Series), share.Series)
                    : ReadPage(share.JobId, share.Snapshot);
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

        private UiPublicShareRecord ReadSeriesPage(string key, UiPublicSeriesSnapshot series)
        {
            var index = JsonSerializer.Deserialize<PageIndex>(File.ReadAllText(PageIndexPath(key)))
                ?? throw new InvalidDataException("Missing public series page identity.");
            if (index.Version != 1 || index.JobId != key || !ValidToken(index.Token))
                throw new InvalidDataException("Invalid public series page identity.");
            var page = Get(index.Token) ?? throw new InvalidDataException("The public series page record is missing.");
            if (page.PageToken.Length > 0 || !page.IsPage || page.Series == null)
                throw new InvalidDataException("The public series page record is malformed.");
            if (page.JobId != key || !UiPublicShares.SameSeriesSnapshot(page.Series, series))
                throw new InvalidOperationException("This series differs from its saved public page. Sharing stopped.");
            return page;
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
            || UiWorkflow.IsShareLinkRequest(path, method)
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
        public static bool SameSeriesSnapshot(UiPublicSeriesSnapshot? left, UiPublicSeriesSnapshot? right) =>
            left != null && right != null && JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
        public static string SeriesClaimJobId(string groupId) => "series:" + groupId;
        public static string SeriesPageKey(UiPublicSeriesSnapshot series) =>
            "series:" + series.GroupId + ":" + series.Generator + ":"
            + string.Join(",", series.Pages.Select(p => p.ImageIndex.ToString(CultureInfo.InvariantCulture)));
        public static string SeriesDisclosure(UiPublicSeriesSnapshot series) =>
            $"Anyone with this link can see the source prompt, inputs, all {series.Pages.Count} pages from {series.GeneratorLabel}, their prompts, and the series contact sheet.";
        public static string SeriesCaption(string publicUrl, int totalPages, int batchStartZeroBased, int batchCount, bool includeReuse)
        {
            var readLink = $"[Read 1–{totalPages}](<{publicUrl}>)";
            var reuseLink = includeReuse ? $" · [{ReuseLabel}](<{publicUrl}reuse>)" : "";
            if (batchStartZeroBased == 0 && batchCount == totalPages)
                return $"{totalPages} pages · {readLink}{reuseLink}";
            var first = batchStartZeroBased + 1;
            var last = batchStartZeroBased + batchCount;
            var range = first == last ? $"Page {first} of {totalPages}" : $"Pages {first}–{last} of {totalPages}";
            return $"{range} · {readLink}{reuseLink}";
        }
        public static (string Kind, string JobId, string Generator, int Index)? SeriesAssetSlot(UiPublicSeriesSnapshot series, int slot)
        {
            if (slot < 0) return null;
            if (slot < series.Inputs.Count)
                return ("input", series.Pages[0].JobId, "input", slot);
            var pageIdx = slot - series.Inputs.Count;
            if (pageIdx < series.Pages.Count)
            {
                var page = series.Pages[pageIdx];
                return ("image", page.JobId, series.Generator, page.ImageIndex);
            }
            if (series.HasContactSheet && pageIdx == series.Pages.Count)
                return ("series-grid", series.GroupId, series.Generator, 0);
            return null;
        }
        public static UiPublicSeriesSnapshot CaptureSeries(
            IReadOnlyList<UiJob> parts,
            string generator,
            IReadOnlyList<int> imageIndices,
            string sourcePrompt,
            string styleNote)
        {
            if (parts == null || parts.Count < 2)
                throw new InvalidOperationException("A series share requires at least two split parts.");
            if (string.IsNullOrWhiteSpace(generator))
                throw new InvalidOperationException("Select an image model for this series share.");
            var firstSplit = parts[0].Split ?? throw new InvalidDataException("The first part is missing split metadata.");
            var expectedCount = firstSplit.Count;
            if (expectedCount < 2 || expectedCount > PromptSplit.MaxImages || parts.Count != expectedCount)
                throw new InvalidOperationException($"This series has {parts.Count} part(s), but its plan expects {expectedCount}.");
            if (imageIndices == null || imageIndices.Count != expectedCount)
                throw new InvalidOperationException($"Select exactly one image per page ({expectedCount} pages).");

            var ordered = parts.OrderBy(p => p.Split?.Index ?? 0).ToList();
            for (var i = 0; i < expectedCount; i++)
            {
                var split = ordered[i].Split ?? throw new InvalidDataException($"Part {i + 1} is missing split metadata.");
                if (split.GroupId != firstSplit.GroupId || split.ExchangeId != firstSplit.ExchangeId
                    || split.Count != expectedCount || split.Index != i + 1)
                    throw new InvalidDataException("The split series parts do not form a contiguous sequence.");
                if (!ordered[i].IsDone)
                    throw new InvalidOperationException("Wait for every part of this series to finish before sharing.");
            }

            var inputs = new List<UiPublicShareAsset>();
            for (var i = 0; i < ordered[0].InputImageCount; i++)
                inputs.Add(new("input", "input", i, $"Input {i + 1}"));

            var genLabel = "";
            var extraText = "";
            var pages = new List<UiPublicSeriesPage>(expectedCount);
            for (var i = 0; i < expectedCount; i++)
            {
                var job = ordered[i];
                var split = job.Split!;
                var chosenIndex = imageIndices[i];
                if (chosenIndex < 0)
                    throw new InvalidOperationException($"Page {i + 1} has an invalid image selection.");

                JsonElement? matchedResult = null;
                foreach (var line in job.ReadFrom(0).Events)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    if (type == "accepted" && extraText.Length == 0
                        && root.TryGetProperty("generatorExtraTexts", out var extras)
                        && extras.TryGetProperty(generator, out var extraEl)
                        && extraEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(extraEl.GetString()))
                    {
                        extraText = extraEl.GetString()!;
                    }
                    if (type != "gen-result") continue;
                    if (root.GetProperty("gen").GetString() != generator) continue;
                    if (matchedResult != null)
                        throw new InvalidDataException($"Page {split.Index} recorded duplicate results for {generator}.");
                    matchedResult = root.Clone();
                }

                if (matchedResult == null)
                    throw new InvalidOperationException($"Page {split.Index} has no completed result for {generator}.");
                var evt = matchedResult.Value;
                if (!evt.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException($"Page {split.Index} failed for {generator}; complete series only can be shared.");
                if (evt.TryGetProperty("resultKind", out var rk) && rk.GetString() == "text")
                    throw new InvalidOperationException("Text describe results cannot be shared as an image series.");
                if (evt.TryGetProperty("mediaType", out var mt) && mt.GetString() == "video")
                    throw new InvalidOperationException("Video results cannot be shared as an image series.");
                if (!evt.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array
                    || chosenIndex >= imagesEl.GetArrayLength()
                    || string.IsNullOrWhiteSpace(imagesEl[chosenIndex].GetString()))
                    throw new InvalidOperationException($"Page {split.Index} does not contain image #{chosenIndex + 1} for {generator}.");

                if (string.IsNullOrWhiteSpace(genLabel))
                {
                    genLabel = evt.TryGetProperty("label", out var labelEl) && !string.IsNullOrWhiteSpace(labelEl.GetString())
                        ? labelEl.GetString()!
                        : GeneratorPresentation.UiContactSheetLabel(generator);
                }

                var width = 0;
                var height = 0;
                if (evt.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.String)
                {
                    var partsDims = (sizeEl.GetString() ?? "").Split('x', '×');
                    if (partsDims.Length == 2
                        && int.TryParse(partsDims[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                        && int.TryParse(partsDims[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                        && w > 0 && h > 0)
                    {
                        width = w;
                        height = h;
                    }
                }

                pages.Add(new UiPublicSeriesPage(
                    split.Index,
                    split.Count,
                    job.Id,
                    split.Title,
                    job.Prompt,
                    chosenIndex,
                    width,
                    height));
            }

            if (inputs.Count + pages.Count + 1 > 128)
                throw new InvalidDataException("This series exceeds the public page size limit.");

            return new UiPublicSeriesSnapshot(
                firstSplit.GroupId,
                firstSplit.ExchangeId,
                generator,
                genLabel,
                PromptSplit.ModelLabel(firstSplit.Model),
                firstSplit.Reason,
                sourcePrompt ?? "",
                styleNote ?? "",
                extraText,
                inputs,
                pages,
                HasContactSheet: true);
        }
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

        public static readonly string SeriesReaderScript = """
(() => {
  const shell = document.getElementById("reader-shell");
  const allSection = document.getElementById("all-pages");
  if (!shell || !allSection) return;
  const cards = [...allSection.querySelectorAll("article.page-card")];
  if (cards.length < 2) return;
  const pages = cards.map((card, idx) => ({
    number: Number(card.dataset.page || idx + 1),
    title: card.dataset.title || "",
    fullSrc: card.dataset.fullSrc || "",
    thumbSrc: card.dataset.thumbSrc || "",
    prompt: card.querySelector(".page-prompt")?.textContent || "",
  }));
  const stage = document.getElementById("reader-stage");
  const img = document.getElementById("reader-img");
  const badge = document.getElementById("reader-res-badge");
  const counter = document.getElementById("reader-counter");
  const titleEl = document.getElementById("reader-page-title");
  const openFull = document.getElementById("reader-open-full");
  const prevBtn = document.getElementById("reader-prev");
  const nextBtn = document.getElementById("reader-next");
  const strip = document.getElementById("reader-strip");
  const promptDetails = document.getElementById("reader-prompt-details");
  const promptSummary = document.getElementById("reader-prompt-summary");
  const promptPre = document.getElementById("reader-prompt-text");
  const modePageBtn = document.getElementById("mode-page");
  const modeAllBtn = document.getElementById("mode-all");
  const preloadCache = new Map();
  let mode = "page";
  let current = 0;
  let seq = 0;
  function preload(src) {
    if (!src) return null;
    if (preloadCache.has(src)) return preloadCache.get(src);
    const entry = { img: new Image(), ready: false, failed: false, promise: null };
    entry.promise = new Promise((resolve) => {
      entry.img.onload = () => { entry.ready = true; resolve(true); };
      entry.img.onerror = () => { entry.failed = true; resolve(false); };
      entry.img.src = src;
    });
    preloadCache.set(src, entry);
    return entry;
  }
  const stripButtons = pages.map((p, i) => {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "strip-btn";
    b.textContent = String(p.number);
    b.title = p.title ? `Page ${p.number}: ${p.title}` : `Page ${p.number}`;
    b.setAttribute("aria-label", b.title);
    b.addEventListener("click", () => show(i, true));
    strip.appendChild(b);
    return b;
  });
  function updateMode(nextMode) {
    mode = nextMode;
    const isPage = mode === "page";
    shell.hidden = !isPage;
    allSection.hidden = isPage;
    modePageBtn.classList.toggle("active", isPage);
    modePageBtn.setAttribute("aria-pressed", String(isPage));
    modeAllBtn.classList.toggle("active", !isPage);
    modeAllBtn.setAttribute("aria-pressed", String(!isPage));
  }
  function show(index, pushHash) {
    const next = Math.max(0, Math.min(pages.length - 1, index));
    current = next;
    const mySeq = ++seq;
    const p = pages[next];
    counter.textContent = `${p.number} / ${pages.length}`;
    titleEl.textContent = p.title;
    openFull.href = p.fullSrc;
    prevBtn.disabled = next === 0;
    nextBtn.disabled = next === pages.length - 1;
    stripButtons.forEach((b, i) => {
      b.classList.toggle("active", i === next);
      b.setAttribute("aria-current", i === next ? "page" : "false");
    });
    promptSummary.textContent = p.title ? `Prompt for page ${p.number} (${p.title})` : `Prompt for page ${p.number}`;
    promptPre.textContent = p.prompt;
    promptDetails.open = false;
    if (pushHash) {
      const targetHash = `#page-${p.number}`;
      if (location.hash !== targetHash) history.replaceState(null, "", targetHash);
    }
    const fullEntry = preload(p.fullSrc);
    const thumbEntry = p.thumbSrc ? preload(p.thumbSrc) : null;
    if (fullEntry && fullEntry.ready) {
      stage.classList.remove("loading");
      img.hidden = false;
      img.alt = p.title ? `Page ${p.number} of ${pages.length}: ${p.title}` : `Page ${p.number} of ${pages.length}`;
      img.src = p.fullSrc;
      badge.hidden = true;
    } else if (thumbEntry && thumbEntry.ready) {
      stage.classList.remove("loading");
      img.hidden = false;
      img.alt = p.title ? `Page ${p.number} of ${pages.length}: ${p.title}` : `Page ${p.number} of ${pages.length}`;
      img.src = p.thumbSrc;
      badge.hidden = false;
    } else {
      img.hidden = true;
      img.removeAttribute("src");
      img.alt = "";
      badge.hidden = true;
      stage.classList.add("loading");
    }
    if (fullEntry && !fullEntry.ready) {
      fullEntry.promise.then((ok) => {
        if (mySeq !== seq) return;
        stage.classList.remove("loading");
        badge.hidden = true;
        if (ok) {
          img.alt = p.title ? `Page ${p.number} of ${pages.length}: ${p.title}` : `Page ${p.number} of ${pages.length}`;
          img.src = p.fullSrc;
          img.hidden = false;
        }
      });
    }
    if (next + 1 < pages.length) preload(pages[next + 1].fullSrc);
    if (next - 1 >= 0) preload(pages[next - 1].fullSrc);
  }
  function indexFromHash() {
    const m = /^#page-([1-9][0-9]*)$/.exec(location.hash);
    if (!m) return 0;
    const n = Number(m[1]);
    return n >= 1 && n <= pages.length ? n - 1 : 0;
  }
  prevBtn.addEventListener("click", () => show(current - 1, true));
  nextBtn.addEventListener("click", () => show(current + 1, true));
  modePageBtn.addEventListener("click", () => updateMode("page"));
  modeAllBtn.addEventListener("click", () => updateMode("all"));
  window.addEventListener("hashchange", () => {
    if (/^#page-[1-9][0-9]*$/.test(location.hash)) {
      updateMode("page");
      show(indexFromHash(), false);
    }
  });
  document.addEventListener("keydown", (e) => {
    if (mode !== "page") return;
    const tag = e.target && e.target.tagName ? e.target.tagName.toLowerCase() : "";
    if (tag === "input" || tag === "textarea" || tag === "select") return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (e.key === "ArrowLeft") { e.preventDefault(); show(current - 1, true); }
    else if (e.key === "ArrowRight") { e.preventDefault(); show(current + 1, true); }
    else if (e.key === "Home") { e.preventDefault(); show(0, true); }
    else if (e.key === "End") { e.preventDefault(); show(pages.length - 1, true); }
  });
  let touchX = null, touchY = null;
  stage.addEventListener("touchstart", (e) => {
    if (e.touches.length !== 1) return;
    touchX = e.touches[0].clientX;
    touchY = e.touches[0].clientY;
  }, { passive: true });
  stage.addEventListener("touchend", (e) => {
    if (touchX === null || !e.changedTouches.length) return;
    const dx = e.changedTouches[0].clientX - touchX;
    const dy = e.changedTouches[0].clientY - touchY;
    touchX = null; touchY = null;
    if (Math.abs(dx) >= 44 && Math.abs(dx) > Math.abs(dy) * 1.4) {
      if (dx < 0) show(current + 1, true); else show(current - 1, true);
    }
  }, { passive: true });
  updateMode("page");
  show(indexFromHash(), false);
})();
""".Replace("\r\n", "\n");

        public static readonly string SeriesReaderScriptHash =
            "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(SeriesReaderScript)));

        public static string Html(UiPublicShareRecord record, string assetBase, string? reuseUrl, bool login = false,
            string? requestAccountUrl = null)
        {
            if (record.Series != null)
                return SeriesHtml(record.Series, assetBase, reuseUrl, login, requestAccountUrl);
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
                button:disabled{cursor:progress}#status{min-height:1.5em;font-weight:700}#status.error{color:#b00020}
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
                body.Append("<form id=\"login\" method=\"post\"><label>Username <input name=\"username\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" required></label>")
                    .Append("<label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label>")
                    .Append("<button disabled>Log in and make your own</button><p id=\"status\" role=\"status\"></p></form>")
                    .Append("<noscript><p>Enable JavaScript to log in.</p></noscript><script>").Append(UiWorkflow.ShareLinkLoginScript).Append("</script>");
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

        public static string SeriesHtml(
            UiPublicSeriesSnapshot series,
            string assetBase,
            string? reuseUrl,
            bool login = false,
            string? requestAccountUrl = null)
        {
            static string E(string value) => WebUtility.HtmlEncode(value);
            var pageCount = series.Pages.Count;
            var sheetSlot = series.Inputs.Count + pageCount;
            var body = new StringBuilder("""
                <!doctype html><html lang="en"><meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <meta name="robots" content="noindex,nofollow,noarchive">
                """);
            body.Append("<title>").Append(pageCount).Append("-page series · ").Append(E(series.GeneratorLabel)).Append("</title>");
            body.Append("""
                <style>
                *{box-sizing:border-box}
                [hidden]{display:none!important}
                body{font:16px/1.5 system-ui,-apple-system,sans-serif;margin:0;padding:clamp(12px,2vw,28px);width:100%;color:#000;background:#f4f6fa}
                header.top{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;margin-bottom:14px;padding-bottom:12px;border-bottom:2px solid #c5d0e0}
                h1{margin:0;font-size:clamp(22px,2.4vw,30px);font-weight:800;color:#000}
                h2{margin:0 0 10px;font-size:21px;font-weight:800;color:#000}
                h3{margin:0 0 8px;font-size:18px;font-weight:800;color:#000}
                .top-actions{display:flex;flex-wrap:wrap;align-items:center;gap:8px}
                a{color:#0b4cb3;font-weight:600}
                button,.action{display:inline-flex;align-items:center;justify-content:center;min-height:38px;padding:7px 14px;border:2px solid #0b4cb3;border-radius:7px;background:#fff;color:#000;font:700 15px/1.2 system-ui,sans-serif;text-decoration:none;cursor:pointer}
                .action.primary,button.active{background:#0b4cb3;color:#fff}
                button:disabled{background:#e8edf5;border-color:#7c8ea8;color:#000;cursor:default}
                .reader-shell{background:#fff;border:2px solid #c5d0e0;border-radius:12px;padding:clamp(10px,1.8vw,20px);margin-bottom:20px}
                .reader-bar{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:10px;margin-bottom:10px}
                .reader-nav{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
                #reader-counter{font-size:22px;font-weight:800;font-variant-numeric:tabular-nums;color:#000}
                #reader-page-title{font-size:18px;font-weight:700;color:#000}
                .reader-strip{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:12px}
                .strip-btn{min-width:42px;min-height:38px;padding:6px 12px;font-variant-numeric:tabular-nums}
                #reader-stage{position:relative;display:flex;align-items:center;justify-content:center;width:100%;min-height:min(72vh,640px);background:#e8edf5;border:1px solid #c5d0e0;border-radius:10px;overflow:hidden}
                #reader-stage.loading::after{content:"";position:absolute;inset:0;background:linear-gradient(110deg,#dce4f0 20%,#eef2f9 50%,#dce4f0 80%);background-size:200% 100%;animation:pulse 1.2s linear infinite}
                @keyframes pulse{0%{background-position:200% 0}100%{background-position:-200% 0}}
                #reader-img{display:block;max-width:100%;max-height:82vh;width:auto;height:auto;object-fit:contain}
                #reader-res-badge{position:absolute;top:10px;right:10px;padding:4px 10px;border-radius:6px;background:#fff;border:2px solid #000;color:#000;font:700 13px system-ui,sans-serif}
                details{margin-top:12px;background:#fff;border:1px solid #c5d0e0;border-radius:8px;padding:10px 14px}
                summary{cursor:pointer;font-weight:700;color:#000}
                pre{white-space:pre-wrap;overflow-wrap:anywhere;font:15px/1.5 system-ui,sans-serif;background:#f8fafe;color:#000;padding:14px;border:1px solid #c5d0e0;border-radius:8px;margin:10px 0 0}
                .all-pages-list{display:flex;flex-direction:column;gap:24px}
                .page-card{background:#fff;border:2px solid #c5d0e0;border-radius:12px;padding:clamp(12px,2vw,20px);scroll-margin-top:16px}
                .page-card-head{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;gap:10px;margin-bottom:10px}
                .page-card-num{font-size:20px;font-weight:800;font-variant-numeric:tabular-nums;color:#000}
                .page-card img{display:block;max-width:100%;max-height:86vh;width:auto;height:auto;margin:0 auto;border-radius:8px;background:#e8edf5}
                section.details-panel{background:#fff;border:2px solid #c5d0e0;border-radius:12px;padding:clamp(12px,2vw,20px);margin-top:20px}
                .inputs-grid{display:flex;flex-wrap:wrap;gap:12px;margin-top:10px}
                .inputs-grid img{display:block;max-width:260px;max-height:260px;object-fit:contain;border:2px solid #b58900;border-radius:8px;background:#fff}
                form#login{margin:16px 0;padding:16px;background:#fff;border:2px solid #0b4cb3;border-radius:10px}
                input{padding:9px;margin:6px 0;border:1px solid #000;border-radius:6px;font:16px system-ui,sans-serif;color:#000;background:#fff}
                label{display:block;font-weight:700;color:#000}
                #status{min-height:1.5em;font-weight:700;color:#000}#status.error{color:#a8001c}
                </style>
                """);
            body.Append("<header class=\"top\"><h1>").Append(pageCount).Append("-page series · ").Append(E(series.GeneratorLabel)).Append("</h1>");
            body.Append("<div class=\"top-actions\">");
            body.Append("<button id=\"mode-page\" type=\"button\" aria-pressed=\"false\">Page by page</button>");
            body.Append("<button id=\"mode-all\" type=\"button\" class=\"active\" aria-pressed=\"true\">All pages</button>");
            body.Append("<a class=\"action\" href=\"#details\">Prompts &amp; details</a>");
            if (series.HasContactSheet)
                body.Append("<a class=\"action\" href=\"").Append(E(assetBase + sheetSlot)).Append("\">Contact sheet (PNG)</a>");
            if (reuseUrl != null)
                body.Append("<a class=\"action primary\" href=\"").Append(E(reuseUrl)).Append("\">Make your own</a>");
            if (requestAccountUrl != null)
                body.Append("<a class=\"action\" href=\"").Append(E(requestAccountUrl)).Append("\">Request account</a>");
            body.Append("</div></header>");

            if (login)
            {
                body.Append(requestAccountUrl == null
                    ? "<p>Log in to use or edit this source prompt. Need an account? Ask Ernie in Discord.</p>"
                    : "<p>Log in to use or edit this source prompt. Select Request account to receive a signup link through Discord.</p>");
                body.Append("<form id=\"login\" method=\"post\"><label>Username <input name=\"username\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" required></label>")
                    .Append("<label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label>")
                    .Append("<button type=\"submit\" disabled>Log in and make your own</button><p id=\"status\" role=\"status\"></p></form>")
                    .Append("<noscript><p>Enable JavaScript to log in.</p></noscript><script>").Append(UiWorkflow.ShareLinkLoginScript).Append("</script>");
            }

            // Interactive 1-by-1 reader (activated immediately by the hash-pinned inline script; hidden if JS is off).
            body.Append("""
                <section id="reader-shell" class="reader-shell" hidden aria-label="Page by page reader">
                  <div class="reader-bar">
                    <div class="reader-nav">
                      <button id="reader-prev" type="button">← Prev</button>
                      <span id="reader-counter"></span>
                      <button id="reader-next" type="button">Next →</button>
                      <span id="reader-page-title"></span>
                    </div>
                    <a id="reader-open-full" class="action" href="#" target="_blank" rel="noopener">Open full image</a>
                  </div>
                  <div id="reader-strip" class="reader-strip" role="navigation" aria-label="Jump to page"></div>
                  <div id="reader-stage">
                    <img id="reader-img" alt="" hidden>
                    <span id="reader-res-badge" hidden>Preview — loading full image</span>
                  </div>
                  <details id="reader-prompt-details">
                    <summary id="reader-prompt-summary">Show page prompt</summary>
                    <pre id="reader-prompt-text"></pre>
                  </details>
                </section>
                """);

            // Full ordered vertical stack (also serves as the no-JS reading layout and DOM data source for the reader).
            body.Append("<section id=\"all-pages\" class=\"all-pages-list\" aria-label=\"All pages in order\">");
            for (var i = 0; i < pageCount; i++)
            {
                var page = series.Pages[i];
                var slot = series.Inputs.Count + i;
                var fullUrl = E(assetBase + slot);
                var thumbUrl = E(assetBase + slot + "?thumb=1");
                var titleText = string.IsNullOrWhiteSpace(page.Title) ? "" : page.Title;
                var alt = string.IsNullOrWhiteSpace(titleText)
                    ? $"Page {page.Index} of {pageCount}"
                    : $"Page {page.Index} of {pageCount}: {titleText}";
                body.Append("<article class=\"page-card\" id=\"page-").Append(page.Index)
                    .Append("\" data-page=\"").Append(page.Index)
                    .Append("\" data-title=\"").Append(E(titleText))
                    .Append("\" data-full-src=\"").Append(fullUrl)
                    .Append("\" data-thumb-src=\"").Append(thumbUrl).Append("\">");
                body.Append("<div class=\"page-card-head\"><span class=\"page-card-num\">Page ").Append(page.Index)
                    .Append(" of ").Append(pageCount);
                if (!string.IsNullOrWhiteSpace(titleText))
                    body.Append(" · ").Append(E(titleText));
                body.Append("</span><a href=\"").Append(fullUrl).Append("\">Open full image</a></div>");
                body.Append("<a href=\"").Append(fullUrl).Append("\"><img loading=\"lazy\" alt=\"").Append(E(alt))
                    .Append("\" src=\"").Append(fullUrl).Append("\"");
                if (page.Width > 0 && page.Height > 0)
                    body.Append(" width=\"").Append(page.Width).Append("\" height=\"").Append(page.Height).Append("\"");
                body.Append("></a>");
                body.Append("<details><summary>Prompt for page ").Append(page.Index).Append("</summary><pre class=\"page-prompt\">")
                    .Append(E(page.Prompt)).Append("</pre></details>");
                body.Append("</article>");
            }
            body.Append("</section>");

            body.Append("<section id=\"details\" class=\"details-panel\"><h2>Series details</h2>");
            if (!string.IsNullOrWhiteSpace(series.SourcePrompt))
                body.Append("<h3>Original source prompt</h3><pre>").Append(E(series.SourcePrompt)).Append("</pre>");
            if (!string.IsNullOrWhiteSpace(series.SplitReason))
                body.Append("<details><summary>Split plan notes (").Append(E(series.SplitModelLabel)).Append(")</summary><pre>")
                    .Append(E(series.SplitReason)).Append("</pre></details>");
            if (!string.IsNullOrWhiteSpace(series.StyleNote))
                body.Append("<details><summary>Shared style note</summary><pre>").Append(E(series.StyleNote)).Append("</pre></details>");
            if (!string.IsNullOrWhiteSpace(series.GeneratorExtraText))
                body.Append("<details><summary>").Append(E(series.GeneratorLabel)).Append(" appended text</summary><pre>")
                    .Append(E(series.GeneratorExtraText)).Append("</pre></details>");
            if (series.Inputs.Count > 0)
            {
                body.Append("<h3>Input images (").Append(series.Inputs.Count).Append(")</h3><div class=\"inputs-grid\">");
                for (var i = 0; i < series.Inputs.Count; i++)
                {
                    var u = E(assetBase + i);
                    body.Append("<a href=\"").Append(u).Append("\"><img loading=\"lazy\" alt=\"").Append(E(series.Inputs[i].Label))
                        .Append("\" src=\"").Append(u).Append("?thumb=1\"></a>");
                }
                body.Append("</div>");
            }
            if (series.HasContactSheet)
            {
                body.Append("<p><a class=\"action\" href=\"").Append(E(assetBase + sheetSlot))
                    .Append("\">Open series contact sheet (PNG)</a></p>");
            }
            body.Append("</section>");
            body.Append("<script>").Append(SeriesReaderScript).Append("</script>");
            return body.Append("</html>").ToString();
        }
    }
}
