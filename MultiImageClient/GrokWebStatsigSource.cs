#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Verification-key byte positions read by one deployment's signer chunk.
    public sealed record GrokWebStatsigSignerParameters(
        int RowIndexByteIndex,
        IReadOnlyList<int> FrameTimeByteIndices,
        int FrameSetByteIndex,
        int FrameSetCount);

    public sealed class GrokWebStatsigPage
    {
        public required string VerificationKeyBase64 { get; init; }

        // [set][row] = 11 numbers: from-RGB, to-RGB, rotation, 4 bezier values.
        public required IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> FrameSets { get; init; }

        public required IReadOnlyList<Uri> ScriptUrls { get; init; }
    }

    // Parsers for the public grok.com page and its Turbopack script chunks.
    // Each requires one unambiguous match; a changed layout throws instead of
    // yielding a guessed signing input.
    public static class GrokWebStatsigParser
    {
        public const string VerificationMetaName = "grok-site-verification";
        private const int VerificationKeyBytes = 48;
        private const int FramesPerSet = 16;
        private const int MaxSignerAssignmentDistance = 4000;
        private const string CounterEpochHex = "0x644f6370";
        private const string CounterEpochDecimal = "1682924400";
        private static readonly string[] ScriptHosts = { "grok.com", "cdn.grok.com" };

        private static readonly Regex MetaTagRegex = new(
            @"<meta\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NameAttributeRegex = new(
            @"\bname\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ContentAttributeRegex = new(
            @"\bcontent\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex EscapedFrameSetsRegex = new(
            @"\[\[\{\\""color\\"".*?\}\]\]",
            RegexOptions.CultureInvariant);
        private static readonly Regex PlainFrameSetsRegex = new(
            @"\[\[\{""color"".*?\}\]\]",
            RegexOptions.CultureInvariant);
        private static readonly Regex ScriptSrcRegex = new(
            @"<script\b[^>]*?\bsrc\s*=\s*(?:""([^""]+)""|'([^']+)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SignerExportRegex = new(
            @"\.s\(\[\s*""botoxSign""\s*,\s*\d+\s*,\s*([A-Za-z_$][\w$]*)\s*\]",
            RegexOptions.CultureInvariant);
        private static readonly Regex SignerImportRegex = new(
            @"\(\s*await\s+[A-Za-z_$][\w$]*\.A\(\s*(\d+)\s*\)\s*\)\s*\.default\(\)",
            RegexOptions.CultureInvariant);
        private static readonly Regex AsyncLoaderRegex = new(
            @"(?<![\w$.])(\d+)\s*,\s*([A-Za-z_$][\w$]*)\s*=>\s*\{\s*\2\.v\(\s*[A-Za-z_$][\w$]*\s*=>\s*Promise\.all\(\s*\[([^\]]*)\]\s*\.map\(",
            RegexOptions.CultureInvariant);
        private static readonly Regex QuotedPathRegex = new(
            @"""([^""\\]+)""",
            RegexOptions.CultureInvariant);
        private static readonly Regex KeyByteModulo16Regex = new(
            @"(?<![\w$])[A-Za-z_$][\w$]*\[(\d{1,2})\]\s*,\s*16\s*\)",
            RegexOptions.CultureInvariant);
        private static readonly Regex FrameSetSelectorRegex = new(
            @"\[\s*(?<![\w$])[A-Za-z_$][\w$]*\[(\d{1,2})\]\s*%\s*(\d{1,2})\s*\]",
            RegexOptions.CultureInvariant);

        public static GrokWebStatsigPage ParsePage(string html, Uri pageUri)
        {
            ArgumentNullException.ThrowIfNull(html);
            ArgumentNullException.ThrowIfNull(pageUri);
            return new GrokWebStatsigPage
            {
                VerificationKeyBase64 = ParseVerificationKey(html),
                FrameSets = ParseFrameSets(html),
                ScriptUrls = ParseScriptUrls(html, pageUri),
            };
        }

        // The page writes the meta name with U+2015 instead of a hyphen, so
        // every Unicode dash compares as '-'.
        public static string ParseVerificationKey(string html)
        {
            ArgumentNullException.ThrowIfNull(html);
            var contents = new List<string>();
            foreach (Match tag in MetaTagRegex.Matches(html))
            {
                var name = AttributeValue(NameAttributeRegex, tag.Value);
                if (name == null
                    || !string.Equals(
                        NormalizeDashes(name),
                        VerificationMetaName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                contents.Add(AttributeValue(ContentAttributeRegex, tag.Value)
                    ?? throw FormatError("the grok-site-verification meta tag has no content"));
            }
            if (contents.Count != 1)
            {
                throw FormatError(
                    $"expected one grok-site-verification meta tag, found {contents.Count}");
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(contents[0]);
            }
            catch (FormatException)
            {
                throw FormatError("the grok-site-verification content is not base64");
            }
            if (key.Length != VerificationKeyBytes)
            {
                throw FormatError(
                    $"the grok-site-verification key has {key.Length} bytes instead of {VerificationKeyBytes}");
            }
            return Convert.ToBase64String(key);
        }

        // The table appears in the page's server-component payload, usually
        // as escaped JSON inside a script string. Every copy must agree.
        public static IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> ParseFrameSets(string html)
        {
            ArgumentNullException.ThrowIfNull(html);
            var copies = EscapedFrameSetsRegex.Matches(html)
                .Select(m => m.Value.Replace("\\\"", "\"", StringComparison.Ordinal))
                .Concat(PlainFrameSetsRegex.Matches(html).Select(m => m.Value))
                .Select(ParseFrameSetsJson)
                .ToList();
            if (copies.Count == 0)
            {
                throw FormatError("the animation frame table was not found");
            }
            var first = Flatten(copies[0]);
            if (copies.Skip(1).Any(copy => !Flatten(copy).SequenceEqual(first)))
            {
                throw FormatError($"the page holds {copies.Count} animation frame tables that differ");
            }
            return copies[0];
        }

        public static IReadOnlyList<Uri> ParseScriptUrls(string html, Uri pageUri)
        {
            ArgumentNullException.ThrowIfNull(html);
            ArgumentNullException.ThrowIfNull(pageUri);
            var urls = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in ScriptSrcRegex.Matches(html))
            {
                var raw = WebUtility.HtmlDecode(
                    match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                if (Uri.TryCreate(pageUri, raw, out var uri)
                    && IsGrokScriptUrl(uri)
                    && seen.Add(uri.AbsoluteUri))
                {
                    urls.Add(uri);
                }
            }
            if (urls.Count == 0)
            {
                throw FormatError("the page lists no grok.com script chunks");
            }
            return urls;
        }

        // Returns the async module id behind the botoxSign export, or null
        // when this chunk does not export botoxSign.
        public static long? FindSignerModuleId(string chunkText)
        {
            ArgumentNullException.ThrowIfNull(chunkText);
            var exports = SignerExportRegex.Matches(chunkText);
            if (exports.Count == 0)
            {
                return null;
            }
            if (exports.Count > 1)
            {
                throw FormatError("a script chunk exports botoxSign more than once");
            }

            var export = exports[0];
            var exportedName = export.Groups[1].Value;
            var windowStart = Math.Max(0, export.Index - MaxSignerAssignmentDistance);
            var window = chunkText.Substring(windowStart, export.Index - windowStart);
            var assignment = Regex
                .Matches(window, $@"(?<![\w$.]){Regex.Escape(exportedName)}\s*=(?![=>])")
                .LastOrDefault()
                ?? throw FormatError(
                    $"the botoxSign export '{exportedName}' has no assignment within "
                    + $"{MaxSignerAssignmentDistance} characters");
            var imports = SignerImportRegex.Matches(window.Substring(assignment.Index));
            if (imports.Count != 1)
            {
                throw FormatError(
                    $"the botoxSign loader imports {imports.Count} async modules instead of one");
            }
            return long.Parse(imports[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        // Module id -> chunk paths for each Turbopack async loader in the
        // chunk. A module listed twice with different paths maps to null.
        public static IReadOnlyDictionary<long, IReadOnlyList<string>?> ParseAsyncChunkLoaders(string chunkText)
        {
            ArgumentNullException.ThrowIfNull(chunkText);
            var loaders = new Dictionary<long, IReadOnlyList<string>?>();
            foreach (Match match in AsyncLoaderRegex.Matches(chunkText))
            {
                if (!long.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var moduleId))
                {
                    continue;
                }
                var paths = QuotedPathRegex
                    .Matches(match.Groups[3].Value)
                    .Select(m => m.Groups[1].Value)
                    .ToArray();
                if (loaders.TryGetValue(moduleId, out var existing))
                {
                    if (existing != null && !existing.SequenceEqual(paths, StringComparer.Ordinal))
                    {
                        loaders[moduleId] = null;
                    }
                    continue;
                }
                loaders[moduleId] = paths;
            }
            return loaders;
        }

        // Returns null when the chunk has no signer key-byte reads at all.
        public static GrokWebStatsigSignerParameters? TryParseSignerParameters(string chunkText)
        {
            ArgumentNullException.ThrowIfNull(chunkText);
            var reads = KeyByteModulo16Regex.Matches(chunkText);
            if (reads.Count == 0)
            {
                return null;
            }
            // Order of appearance: row index, then the three frame-time factors.
            if (reads.Count != 4)
            {
                throw FormatError(
                    $"the signer chunk has {reads.Count} key-byte modulo-16 reads instead of 4");
            }
            var indices = reads
                .Select(m => int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture))
                .ToArray();

            var selectors = FrameSetSelectorRegex.Matches(chunkText);
            if (selectors.Count != 1)
            {
                throw FormatError(
                    $"the signer chunk has {selectors.Count} frame-set selectors instead of one");
            }
            var frameSetByte = int.Parse(selectors[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var frameSetCount = int.Parse(selectors[0].Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);

            if (!chunkText.Contains(CounterEpochHex, StringComparison.OrdinalIgnoreCase)
                && !chunkText.Contains(CounterEpochDecimal, StringComparison.Ordinal))
            {
                throw FormatError(
                    $"the signer chunk no longer contains the counter epoch {CounterEpochDecimal}");
            }
            if (indices.Append(frameSetByte).Any(i => i >= VerificationKeyBytes))
            {
                throw FormatError("the signer chunk reads a byte beyond the 48-byte verification key");
            }
            if (frameSetCount < 1)
            {
                throw FormatError("the signer chunk selects among zero frame sets");
            }

            return new GrokWebStatsigSignerParameters(
                RowIndexByteIndex: indices[0],
                FrameTimeByteIndices: new[] { indices[1], indices[2], indices[3] },
                FrameSetByteIndex: frameSetByte,
                FrameSetCount: frameSetCount);
        }

        public static bool IsGrokScriptUrl(Uri uri)
            => uri.IsAbsoluteUri
                && uri.Scheme == Uri.UriSchemeHttps
                && ScriptHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                && uri.AbsolutePath.EndsWith(".js", StringComparison.Ordinal);

        public static GrokWebException FormatError(string detail)
            => new($"grok.com page format changed: {detail}.");

        private static IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> ParseFrameSetsJson(string json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw FormatError($"the animation frame table is not valid JSON ({ex.Message})");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    throw FormatError("the animation frame table is empty");
                }

                var sets = new List<IReadOnlyList<IReadOnlyList<int>>>();
                foreach (var set in root.EnumerateArray())
                {
                    if (set.ValueKind != JsonValueKind.Array || set.GetArrayLength() != FramesPerSet)
                    {
                        throw FormatError(
                            $"animation frame set {sets.Count} does not have {FramesPerSet} rows");
                    }
                    var rows = new List<IReadOnlyList<int>>();
                    foreach (var row in set.EnumerateArray())
                    {
                        rows.Add(ParseFrameRow(row, sets.Count, rows.Count));
                    }
                    sets.Add(rows);
                }
                return sets;
            }
        }

        private static IReadOnlyList<int> ParseFrameRow(JsonElement row, int set, int index)
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw FormatError($"animation frame {set}/{index} is not an object");
            }
            int[]? color = null;
            int? degrees = null;
            int[]? bezier = null;
            var fields = 0;
            foreach (var property in row.EnumerateObject())
            {
                fields++;
                switch (property.Name)
                {
                    case "color":
                        color = ReadBytes(property.Value, 6, set, index);
                        break;
                    case "deg":
                        degrees = ReadByte(property.Value, set, index);
                        break;
                    case "bezier":
                        bezier = ReadBytes(property.Value, 4, set, index);
                        break;
                    default:
                        throw FormatError(
                            $"animation frame {set}/{index} has unexpected field '{property.Name}'");
                }
            }
            if (fields != 3 || color == null || degrees == null || bezier == null)
            {
                throw FormatError(
                    $"animation frame {set}/{index} must have exactly color, deg, and bezier");
            }
            return color.Append(degrees.Value).Concat(bezier).ToArray();
        }

        private static int[] ReadBytes(JsonElement value, int count, int set, int index)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
            {
                throw FormatError($"animation frame {set}/{index} needs {count} numbers in an array");
            }
            return value.EnumerateArray().Select(v => ReadByte(v, set, index)).ToArray();
        }

        private static int ReadByte(JsonElement value, int set, int index)
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt32(out var number)
                || number is < 0 or > 255)
            {
                throw FormatError($"animation frame {set}/{index} has a value outside 0-255");
            }
            return number;
        }

        private static IEnumerable<int> Flatten(IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> sets)
            => sets.SelectMany(set => set.SelectMany(row => row));

        private static string? AttributeValue(Regex attribute, string tag)
        {
            var match = attribute.Match(tag);
            if (!match.Success)
            {
                return null;
            }
            return WebUtility.HtmlDecode(
                match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        }

        private static string NormalizeDashes(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                builder.Append(
                    CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.DashPunctuation
                        || c == '\u2212'
                        ? '-'
                        : c);
            }
            return builder.ToString();
        }
    }

    // Supplies the x-statsig-id signer. Page mode (no pinned keys in
    // settings) fetches grok.com's per-load verification key and derives the
    // animation key from the deployment's public frame table and signer
    // chunk. grok.com stops accepting a key after about 7 days, so a page
    // key is replaced after PageKeyMaxAge, after grok.com rejects it, and when
    // the cookie file changes. Any parse or fetch failure fails the request.
    public sealed class GrokWebStatsigSource : IDisposable
    {
        public static readonly Uri PageUri = new("https://grok.com/");
        public static readonly TimeSpan PageKeyMaxAge = TimeSpan.FromHours(1);
        public static readonly TimeSpan RefreshFailureCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
        private const int MaxPageBytes = 8 * 1024 * 1024;
        private const int MaxScriptBytes = 16 * 1024 * 1024;
        private const int ScriptDownloadConcurrency = 4;

        private readonly GrokWebStatsigSigner? _pinned;
        private readonly HttpClient? _http;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private PageKey? _current;
        private RefreshFailure? _lastFailure;
        private Dictionary<string, ScriptFacts> _scriptFacts = new(StringComparer.Ordinal);
        private Dictionary<string, GrokWebStatsigSignerParameters?> _signerScan = new(StringComparer.Ordinal);
        private Uri? _lastSignerChunk;

        private sealed record PageKey(GrokWebStatsigSigner Signer, string CookieIdentity);

        private sealed record RefreshFailure(DateTimeOffset AtUtc, string Message);

        private sealed record ScriptFacts(
            long? SignerModuleId,
            IReadOnlyDictionary<long, IReadOnlyList<string>?> Loaders);

        private sealed record Deployment(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> FrameSets,
            Uri SignerChunk,
            GrokWebStatsigSignerParameters Parameters,
            int DownloadedScripts,
            long DownloadedBytes);

        private GrokWebStatsigSource(
            GrokWebStatsigSigner? pinned,
            string? problem,
            HttpMessageHandler? pageHandler,
            Func<DateTimeOffset>? utcNow)
        {
            _pinned = pinned;
            Problem = problem;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            if (pinned == null && problem == null)
            {
                _http = new HttpClient(
                    pageHandler ?? new SocketsHttpHandler
                    {
                        AutomaticDecompression = DecompressionMethods.All,
                        UseCookies = false,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    },
                    disposeHandler: true)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
            }
        }

        // Non-null when settings hold an unusable pinned pair. Signed grok-web
        // features are unavailable until settings are fixed.
        public string? Problem { get; }

        public bool IsPinned => _pinned != null;

        public static GrokWebStatsigSource FromSettings(
            Settings settings,
            HttpMessageHandler? pageHandler = null,
            Func<DateTimeOffset>? utcNow = null)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var verificationKey = settings.GrokWebStatsigVerificationKey?.Trim() ?? "";
            var animationKey = settings.GrokWebStatsigAnimationKey?.Trim() ?? "";
            var capturedAt = settings.GrokWebStatsigCapturedAtUtc?.Trim() ?? "";

            if (verificationKey.Length == 0 && animationKey.Length == 0)
            {
                return capturedAt.Length == 0
                    ? ForPage(pageHandler, utcNow)
                    : new GrokWebStatsigSource(
                        null,
                        "GrokWebStatsigCapturedAtUtc is set without a pinned key pair; "
                        + "clear it or pin both GrokWebStatsig keys",
                        null,
                        utcNow);
            }
            if (verificationKey.Length == 0 || animationKey.Length == 0)
            {
                return new GrokWebStatsigSource(
                    null,
                    "the pinned Grok web signing pair is incomplete; set both "
                    + "GrokWebStatsigVerificationKey and GrokWebStatsigAnimationKey, "
                    + "or clear both to fetch keys from the grok.com page",
                    null,
                    utcNow);
            }

            DateTimeOffset? capturedAtUtc = null;
            if (capturedAt.Length > 0)
            {
                if (!DateTimeOffset.TryParse(
                        capturedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    return new GrokWebStatsigSource(
                        null,
                        $"GrokWebStatsigCapturedAtUtc '{capturedAt}' is not an ISO-8601 time",
                        null,
                        utcNow);
                }
                capturedAtUtc = parsed;
            }

            try
            {
                return new GrokWebStatsigSource(
                    new GrokWebStatsigSigner(
                        verificationKey,
                        animationKey,
                        GrokWebStatsigOrigin.Pinned,
                        capturedAtUtc),
                    null,
                    null,
                    utcNow);
            }
            catch (ArgumentException ex)
            {
                return new GrokWebStatsigSource(
                    null,
                    $"the pinned Grok web signing pair is malformed: {ex.Message}",
                    null,
                    utcNow);
            }
        }

        public static GrokWebStatsigSource ForPage(
            HttpMessageHandler? pageHandler = null,
            Func<DateTimeOffset>? utcNow = null)
            => new(null, null, pageHandler, utcNow);

        public string DescribeMode()
        {
            if (Problem != null)
            {
                return $"Grok web signing unavailable: {Problem}.";
            }
            if (_pinned != null)
            {
                return $"Grok web signing uses the {_pinned.DescribeProvenance(_utcNow())} from settings. "
                    + "grok.com stops accepting a key after about 7 days; clear both GrokWebStatsig "
                    + "keys to fetch keys from the grok.com page instead.";
            }
            return $"Grok web signing fetches keys from {PageUri} when a signed request needs one. "
                + $"Each key is replaced after {PageKeyMaxAge.TotalMinutes:0} min, after grok.com "
                + "rejects it, or when the cookie file changes.";
        }

        public async Task<GrokWebStatsigSigner> GetSignerAsync(
            string cookieHeader,
            CancellationToken cancellationToken = default)
        {
            if (Problem != null)
            {
                throw new GrokWebException($"Grok web signing is unavailable: {Problem}.");
            }
            if (_pinned != null)
            {
                if (_pinned.IsRejected)
                {
                    throw new GrokWebException(
                        $"grok.com already rejected the {_pinned.DescribeProvenance(_utcNow())} "
                        + $"({_pinned.RejectionReason}). Clear both GrokWebStatsig keys from settings "
                        + "to fetch keys from the grok.com page, or pin a new pair with "
                        + "--grok-web-capture-statsig --grok-web-statsig-pin, then restart.");
                }
                return _pinned;
            }
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                throw new ArgumentException("Cookie header is empty.", nameof(cookieHeader));
            }

            var cookieIdentity = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(cookieHeader.Trim())));
            var current = Volatile.Read(ref _current);
            if (IsUsable(current, cookieIdentity))
            {
                return current!.Signer;
            }

            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                current = _current;
                if (IsUsable(current, cookieIdentity))
                {
                    return current!.Signer;
                }
                if (_lastFailure is { } failure
                    && _utcNow() - failure.AtUtc < RefreshFailureCooldown)
                {
                    throw new GrokWebException(
                        $"Could not get a grok.com signing key "
                        + $"({GrokWebStatsigSigner.FormatAge(_utcNow() - failure.AtUtc)} ago): "
                        + $"{failure.Message} The next attempt runs "
                        + $"{RefreshFailureCooldown.TotalSeconds:0} s after that failure.");
                }

                var signer = await RefreshAsync(
                    cookieHeader.Trim(),
                    DescribeRefreshReason(current, cookieIdentity),
                    cancellationToken);
                Volatile.Write(ref _current, new PageKey(signer, cookieIdentity));
                _lastFailure = null;
                return signer;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        // Derives the animation key that the current deployment pairs with
        // an arbitrary verification key. The capture tool compares it with a
        // browser-captured pair to check this derivation end to end.
        public async Task<string> DeriveAnimationKeyAsync(
            string verificationKeyBase64,
            string cookieHeader,
            CancellationToken cancellationToken = default)
        {
            if (_http == null)
            {
                throw new InvalidOperationException(
                    "Animation-key derivation needs a page-mode Grok web signing source.");
            }
            var key = Convert.FromBase64String(verificationKeyBase64);
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                var html = await DownloadTextAsync(PageUri, cookieHeader.Trim(), MaxPageBytes, cancellationToken);
                var page = GrokWebStatsigParser.ParsePage(html, PageUri);
                var deployment = await ResolveDeploymentAsync(page, cancellationToken);
                return Derive(key, deployment, out _, out _, out _);
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void Dispose()
        {
            _http?.Dispose();
            _refreshGate.Dispose();
        }

        private bool IsUsable(PageKey? key, string cookieIdentity)
            => key != null
                && key.CookieIdentity == cookieIdentity
                && !key.Signer.IsRejected
                && _utcNow() - key.Signer.ObtainedAtUtc!.Value < PageKeyMaxAge;

        private string DescribeRefreshReason(PageKey? previous, string cookieIdentity)
        {
            if (previous == null)
            {
                return "first signed request since start";
            }
            var fingerprint = previous.Signer.KeyFingerprint;
            if (previous.CookieIdentity != cookieIdentity)
            {
                return $"cookie file changed; dropped key {fingerprint}";
            }
            var age = GrokWebStatsigSigner.FormatAge(_utcNow() - previous.Signer.ObtainedAtUtc!.Value);
            return previous.Signer.IsRejected
                ? $"grok.com rejected key {fingerprint} after {age}: {previous.Signer.RejectionReason}"
                : $"key {fingerprint} reached {age}";
        }

        private async Task<GrokWebStatsigSigner> RefreshAsync(
            string cookieHeader,
            string reason,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var html = await DownloadTextAsync(PageUri, cookieHeader, MaxPageBytes, cancellationToken);
                var fetchedAtUtc = _utcNow();
                var page = GrokWebStatsigParser.ParsePage(html, PageUri);
                var deployment = await ResolveDeploymentAsync(page, cancellationToken);
                var key = Convert.FromBase64String(page.VerificationKeyBase64);
                var animationKey = Derive(key, deployment, out var frameSet, out var row, out var frameTime);
                var signer = new GrokWebStatsigSigner(
                    page.VerificationKeyBase64,
                    animationKey,
                    GrokWebStatsigOrigin.Page,
                    fetchedAtUtc);

                var parameters = deployment.Parameters;
                var scanned = deployment.DownloadedScripts == 0
                    ? ""
                    : $" Downloaded {deployment.DownloadedScripts} script chunks "
                        + $"({deployment.DownloadedBytes / 1024} KB) to locate the signer.";
                var changed = _lastSignerChunk != null && _lastSignerChunk != deployment.SignerChunk
                    ? $" Signer chunk changed from {ChunkName(_lastSignerChunk)}."
                    : "";
                _lastSignerChunk = deployment.SignerChunk;
                Logger.Log(
                    $"Grok web signing: fetched key {signer.KeyFingerprint} from grok.com in "
                    + $"{stopwatch.ElapsedMilliseconds} ms ({reason}). Signer chunk "
                    + $"{ChunkName(deployment.SignerChunk)} reads key bytes {parameters.RowIndexByteIndex} "
                    + $"(row), {string.Join("/", parameters.FrameTimeByteIndices)} (frame time), "
                    + $"{parameters.FrameSetByteIndex} (frame set of {parameters.FrameSetCount}); "
                    + $"this key uses frame set {frameSet}, row {row}, frame time {frameTime} ms."
                    + scanned
                    + changed);
                return signer;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var message = ex is GrokWebException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                _lastFailure = new RefreshFailure(_utcNow(), message);
                Logger.Log(
                    $"Grok web signing: key refresh failed after {stopwatch.ElapsedMilliseconds} ms "
                    + $"({reason}): {message}");
                throw new GrokWebException($"Could not get a grok.com signing key: {message}");
            }
        }

        private static string Derive(
            byte[] key,
            Deployment deployment,
            out int frameSet,
            out int row,
            out long frameTime)
        {
            var parameters = deployment.Parameters;
            frameSet = key[parameters.FrameSetByteIndex] % parameters.FrameSetCount;
            row = key[parameters.RowIndexByteIndex] % 16;
            frameTime = GrokWebStatsigSigner.FrameTime(key, parameters.FrameTimeByteIndices);
            return GrokWebStatsigSigner.DeriveAnimationKey(
                key,
                deployment.FrameSets[frameSet],
                parameters.RowIndexByteIndex,
                parameters.FrameTimeByteIndices);
        }

        // Callers hold _refreshGate. Script facts are cached by URL; chunk
        // file names are content hashes, so a cached URL never changes meaning.
        private async Task<Deployment> ResolveDeploymentAsync(
            GrokWebStatsigPage page,
            CancellationToken cancellationToken)
        {
            var facts = new Dictionary<string, ScriptFacts>(StringComparer.Ordinal);
            var missing = new List<Uri>();
            foreach (var url in page.ScriptUrls)
            {
                if (_scriptFacts.TryGetValue(url.AbsoluteUri, out var known))
                {
                    facts[url.AbsoluteUri] = known;
                }
                else
                {
                    missing.Add(url);
                }
            }

            var downloadedScripts = 0;
            long downloadedBytes = 0;
            if (missing.Count > 0)
            {
                using var gate = new SemaphoreSlim(ScriptDownloadConcurrency);
                var scanned = await Task.WhenAll(missing.Select(async url =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        var text = await DownloadTextAsync(url, null, MaxScriptBytes, cancellationToken);
                        return (Url: url, Bytes: (long)Encoding.UTF8.GetByteCount(text), Facts: new ScriptFacts(
                            GrokWebStatsigParser.FindSignerModuleId(text),
                            GrokWebStatsigParser.ParseAsyncChunkLoaders(text)));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                foreach (var result in scanned)
                {
                    facts[result.Url.AbsoluteUri] = result.Facts;
                    downloadedBytes += result.Bytes;
                }
                downloadedScripts += scanned.Length;
            }
            _scriptFacts = facts;

            var exporters = page.ScriptUrls
                .Where(url => facts[url.AbsoluteUri].SignerModuleId != null)
                .ToList();
            if (exporters.Count != 1)
            {
                throw GrokWebStatsigParser.FormatError(
                    $"expected one script chunk exporting botoxSign, found {exporters.Count}");
            }
            var moduleId = facts[exporters[0].AbsoluteUri].SignerModuleId!.Value;

            var loaderChunks = page.ScriptUrls
                .Where(url => facts[url.AbsoluteUri].Loaders.ContainsKey(moduleId))
                .ToList();
            if (loaderChunks.Count == 0)
            {
                throw GrokWebStatsigParser.FormatError($"no script chunk loads signer module {moduleId}");
            }
            var pathLists = loaderChunks.Select(url => facts[url.AbsoluteUri].Loaders[moduleId]).ToList();
            if (pathLists.Any(paths => paths == null)
                || pathLists.Skip(1).Any(paths => !paths!.SequenceEqual(pathLists[0]!, StringComparer.Ordinal)))
            {
                throw GrokWebStatsigParser.FormatError($"signer module {moduleId} has conflicting loaders");
            }
            var chunkBase = TurbopackChunkBase(loaderChunks[0]);
            var candidates = pathLists[0]!
                .Where(path => path.EndsWith(".js", StringComparison.Ordinal))
                .Select(path => new Uri(chunkBase, path))
                .ToList();
            if (candidates.Count == 0 || candidates.Any(url => !GrokWebStatsigParser.IsGrokScriptUrl(url)))
            {
                throw GrokWebStatsigParser.FormatError(
                    $"signer module {moduleId} does not load grok.com script chunks");
            }

            var scan = new Dictionary<string, GrokWebStatsigSignerParameters?>(StringComparer.Ordinal);
            var found = new List<(Uri Url, GrokWebStatsigSignerParameters Parameters)>();
            foreach (var url in candidates)
            {
                if (!_signerScan.TryGetValue(url.AbsoluteUri, out var parameters))
                {
                    var text = await DownloadTextAsync(url, null, MaxScriptBytes, cancellationToken);
                    downloadedScripts++;
                    downloadedBytes += Encoding.UTF8.GetByteCount(text);
                    parameters = GrokWebStatsigParser.TryParseSignerParameters(text);
                }
                scan[url.AbsoluteUri] = parameters;
                if (parameters != null)
                {
                    found.Add((url, parameters));
                }
            }
            _signerScan = scan;
            if (found.Count != 1)
            {
                throw GrokWebStatsigParser.FormatError(
                    $"signer module {moduleId} loads {found.Count} chunks with signer parameters instead of one");
            }

            var signer = found[0];
            if (page.FrameSets.Count != signer.Parameters.FrameSetCount)
            {
                throw GrokWebStatsigParser.FormatError(
                    $"the page has {page.FrameSets.Count} animation frame sets but signer chunk "
                    + $"{ChunkName(signer.Url)} selects among {signer.Parameters.FrameSetCount}");
            }
            return new Deployment(
                page.FrameSets,
                signer.Url,
                signer.Parameters,
                downloadedScripts,
                downloadedBytes);
        }

        // One deadline covers headers and body; HttpClient.Timeout does not
        // cover a body read after ResponseHeadersRead, and a stalled read
        // would hold the refresh gate for every signed request.
        private async Task<string> DownloadTextAsync(
            Uri uri,
            string? cookieHeader,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DownloadTimeout);
            try
            {
                return await DownloadTextCoreAsync(uri, cookieHeader, maxBytes, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GrokWebException(
                    $"GET {uri.GetLeftPart(UriPartial.Path)} did not finish within "
                    + $"{DownloadTimeout.TotalSeconds:0} s.");
            }
        }

        private async Task<string> DownloadTextCoreAsync(
            Uri uri,
            string? cookieHeader,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", GrokWebClient.BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (cookieHeader != null)
            {
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
                request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Referer", PageUri.AbsoluteUri);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            }

            using var response = await _http!.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var target = uri.GetLeftPart(UriPartial.Path);
            if (!response.IsSuccessStatusCode)
            {
                throw new GrokWebException(
                    $"GET {target} returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new GrokWebException($"GET {target} declares more than {maxBytes} bytes.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + read > maxBytes)
                {
                    throw new GrokWebException($"GET {target} returned more than {maxBytes} bytes.");
                }
                body.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }

        private static Uri TurbopackChunkBase(Uri loaderChunk)
        {
            const string marker = "/_next/";
            var path = loaderChunk.AbsolutePath;
            var at = path.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                throw GrokWebStatsigParser.FormatError($"script chunk {loaderChunk} is not under {marker}");
            }
            return new Uri(loaderChunk, path[..(at + marker.Length)]);
        }

        private static string ChunkName(Uri chunk)
            => chunk.Segments.Length == 0 ? chunk.AbsoluteUri : chunk.Segments[^1];
    }
}
