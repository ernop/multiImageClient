#nullable enable
using Microsoft.Playwright;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class MetaWebException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public MetaWebException(string message, int statusCode = 0, string responseBody = "")
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }
    }

    public sealed class MetaWebImageGenerationResult
    {
        public required List<byte[]> Images { get; init; }
        public required List<string> ImageUrls { get; init; }
        public string? ConversationUrl { get; init; }
    }

    public sealed class MetaWebClientOptions
    {
        /// Persistent Chromium profile directory. Created on first use. Log in
        /// once with MetaWebHeaded/--meta-web-headed; the profile keeps the
        /// session for later headless runs.
        public string BrowserProfilePath { get; init; } = "";

        /// Optional cookie export (Netscape / DevTools / raw header). Injected
        /// into the browser context on startup as an alternative to the
        /// interactive login. The profile still persists them afterwards.
        public string CookiePath { get; init; } = "";

        /// Show the browser window. Required for the one-time login; useful
        /// for troubleshooting selectors and refusals.
        public bool Headed { get; init; }

        /// Optional path to a Chromium/Chrome binary. Blank = Playwright's own
        /// downloaded Chromium (install once with --playwright-install).
        public string BrowserExecutablePath { get; init; } = "";

        /// Per-prompt generation timeout.
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(480);

        /// Opt-in diagnostics: events.jsonl + failure screenshots under
        /// saves/<day>/meta-web-capture/. Never contains cookie values.
        public bool CaptureSessions { get; init; }

        /// Base folder used for capture output (Settings.ImageDownloadBaseFolder).
        public string ImageDownloadBaseFolder { get; init; } = "";
    }

    /// Browser-automation client for the meta.ai consumer web app (Muse Image).
    /// The cookie-session sibling of GrokWebClient — but unlike grok.com, meta.ai
    /// retired its HTTP persisted-query GraphQL generation path in favor of an
    /// integrity-checked WebSocket transport ("DGW") whose messages only the real
    /// browser can produce. So this client drives the actual web app with
    /// Playwright: type the prompt into the real composer, let Meta's own JS
    /// speak DGW, then harvest the newly rendered images from the page.
    ///
    /// One shared instance per CLI workflow / UI server lifetime; operations are
    /// serialized internally with a SemaphoreSlim. Do not create one per prompt.
    public sealed class MetaWebClient : IAsyncDisposable, IDisposable
    {
        public const string HomeUrl = "https://www.meta.ai/";
        public const string DefaultBrowserProfilePath = "~/.config/multi-image-client/meta-ai-profile";

        /// Minimum natural width/height for a rendered <img> to count as a
        /// generated output rather than an icon, avatar, or thumbnail.
        private const int MinCandidateEdge = 256;

        /// The candidate set must be non-empty and unchanged for this many
        /// consecutive 1-second polls before we treat generation as settled
        /// (Meta streams previews, then swaps in the finals).
        private const int SettleConsecutivePolls = 4;

        private readonly MetaWebClientOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private FileStream? _profileLease;

        private IPlaywright? _playwright;
        private IBrowserContext? _context;
        private IPage? _page;

        public MetaWebClient(MetaWebClientOptions options)
        {
            _options = options;
            if (string.IsNullOrWhiteSpace(options.BrowserProfilePath))
            {
                throw new ArgumentException("BrowserProfilePath is empty.", nameof(options));
            }
        }

        /// Cross-process guard for the persistent profile: only one
        /// MultiImageClient process may drive the browser at a time. Acquired
        /// lazily with the browser (NOT in the constructor) so a transient
        /// collision — e.g. a UI server starting while the previous one is
        /// still shutting down — fails only that generation and heals on the
        /// next one, instead of marking meta-web unavailable for the whole
        /// process lifetime.
        private void AcquireProfileLease()
        {
            if (_profileLease != null)
            {
                return;
            }

            var lockPath = _options.BrowserProfilePath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + ".multi-image-client.lock";
            var lockParent = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(lockParent))
            {
                Directory.CreateDirectory(lockParent);
            }

            FileStream? lease = null;
            try
            {
                lease = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                lease.Lock(0, 1);
                lease.SetLength(0);
                var owner = Encoding.UTF8.GetBytes(
                    $"pid={Environment.ProcessId}; started={DateTimeOffset.Now:O}; profile={_options.BrowserProfilePath}");
                lease.Write(owner);
                lease.Flush();
                _profileLease = lease;
            }
            catch (IOException ex)
            {
                lease?.Dispose();
                throw new MetaWebException(
                    $"Meta web: the browser profile is in use by another MultiImageClient process: "
                    + $"{_options.BrowserProfilePath}. Stop the other meta-web/UI process or configure a separate profile. "
                    + $"({ex.Message})");
            }
        }

        public static MetaWebClientOptions BuildOptions(Settings settings, string? cookieOverride = null, bool headedOverride = false)
        {
            return new MetaWebClientOptions
            {
                BrowserProfilePath = Settings.ExpandPath(
                    string.IsNullOrWhiteSpace(settings.MetaWebBrowserProfilePath)
                        ? DefaultBrowserProfilePath
                        : settings.MetaWebBrowserProfilePath),
                CookiePath = !string.IsNullOrWhiteSpace(cookieOverride) ? cookieOverride : settings.MetaWebCookiePath,
                Headed = headedOverride || settings.MetaWebHeaded,
                BrowserExecutablePath = Settings.ExpandPath(settings.MetaWebBrowserExecutablePath),
                Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.MetaWebTimeoutSeconds)),
                CaptureSessions = settings.MetaWebCaptureSessions,
                ImageDownloadBaseFolder = settings.ImageDownloadBaseFolder,
            };
        }

        /// Null when meta-web can plausibly run; otherwise an actionable
        /// explanation of what is missing. "Plausibly" = there is either an
        /// existing browser profile, a cookie file, or a headed window in which
        /// the user can log in interactively.
        public static string? DescribeAvailabilityProblem(MetaWebClientOptions options)
        {
            if (options.Headed)
            {
                return null;
            }

            var profile = Settings.ExpandPath(options.BrowserProfilePath);
            if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(options.CookiePath))
            {
                var cookies = Settings.ExpandPath(options.CookiePath);
                if (File.Exists(cookies))
                {
                    return null;
                }

                return $"meta-web cookie file not found: {cookies}";
            }

            return $"meta-web has no session yet: no browser profile at {profile} and no MetaWebCookiePath. "
                   + "Run once with --meta-web --meta-web-headed and log in to meta.ai, or export cookies "
                   + "(datr + ecto_1_sess, ideally the full meta.ai jar) and set MetaWebCookiePath / --meta-web-cookies.";
        }

        public async Task<MetaWebImageGenerationResult> GenerateImageAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Timeout);
            var ct = timeoutCts.Token;
            var gateAcquired = false;
            MetaWebSessionCapture? capture = null;
            try
            {
                await _gate.WaitAsync(ct);
                gateAcquired = true;
                capture = _options.CaptureSessions
                    ? MetaWebSessionCapture.Start(_options.ImageDownloadBaseFolder, prompt, _options.Timeout)
                    : null;

                await EnsureStartedAsync(ct);
                var page = _page!;

                await NavigateHomeAsync(page, capture, ct);
                await EnsureAuthenticatedAsync(page, capture, ct);

                var baseline = await CollectPageImagesAsync(page);
                capture?.Event("baseline", new { imageCount = baseline.Count, url = page.Url });

                await SubmitPromptAsync(page, prompt, capture, ct);

                var urls = await WaitForNewImagesAsync(page, baseline, capture, ct);
                capture?.Event("settled", new { urls });

                var images = new List<byte[]>();
                var keptUrls = new List<string>();
                foreach (var url in urls)
                {
                    var bytes = await DownloadImageAsync(url, capture, ct);
                    if (bytes != null)
                    {
                        images.Add(bytes);
                        keptUrls.Add(url);
                    }
                }

                if (images.Count == 0)
                {
                    throw new MetaWebException(
                        "Meta web: generated-image candidates were found on the page but none downloaded as a valid image.");
                }

                capture?.Complete("ok", keptUrls.Count);
                return new MetaWebImageGenerationResult
                {
                    Images = images,
                    ImageUrls = keptUrls,
                    ConversationUrl = page.Url,
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await CaptureFailureAsync(capture, "timeout");
                throw new MetaWebException(
                    $"Meta web: generation timed out after {_options.Timeout.TotalSeconds:0}s with no new images. "
                    + "Most likely Meta answered with TEXT instead of generating an image (we prefix prompts with "
                    + "\"Imagine\" to prevent this, but it is not guaranteed), or the prompt was refused, or the "
                    + "page layout changed. Re-run with --meta-web-headed and MetaWebCaptureSessions=true to inspect.");
            }
            catch (MetaWebException)
            {
                await CaptureFailureAsync(capture, "error");
                throw;
            }
            catch (PlaywrightException ex)
            {
                await CaptureFailureAsync(capture, "playwright-error");
                throw new MetaWebException($"Meta web: browser automation failed: {ex.Message}");
            }
            finally
            {
                capture?.Dispose();
                if (gateAcquired)
                {
                    _gate.Release();
                }
            }
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (_context != null)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            AcquireProfileLease();
            _playwright ??= await Playwright.CreateAsync();

            var profileDir = Settings.ExpandPath(_options.BrowserProfilePath);
            Directory.CreateDirectory(profileDir);

            var launch = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = !_options.Headed,
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                Args = new[] { "--disable-blink-features=AutomationControlled" },
            };
            if (!string.IsNullOrWhiteSpace(_options.BrowserExecutablePath))
            {
                launch.ExecutablePath = Settings.ExpandPath(_options.BrowserExecutablePath);
            }

            try
            {
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(profileDir, launch);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            {
                throw new MetaWebException(
                    "Playwright's Chromium is not installed. Run once with --playwright-install "
                    + "(or set MetaWebBrowserExecutablePath to an existing Chrome/Chromium binary).");
            }
            catch (PlaywrightException ex) when (
                ex.Message.Contains("ProcessSingleton", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("SingletonLock", StringComparison.OrdinalIgnoreCase))
            {
                throw new MetaWebException(
                    $"Meta web: the browser profile ({profileDir}) is already in use by another process — "
                    + "likely a running --ui server or another --meta-web run. Only one process can hold the "
                    + "profile at a time; stop the other one first.");
            }

            if (!string.IsNullOrWhiteSpace(_options.CookiePath))
            {
                var pairs = MetaWebCookieLoader.LoadCookiePairs(_options.CookiePath);
                await _context.AddCookiesAsync(pairs.Select(kvp => new Cookie
                {
                    Name = kvp.Key,
                    Value = kvp.Value,
                    Domain = ".meta.ai",
                    Path = "/",
                    Secure = true,
                }));
            }

            _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        }

        private static async Task NavigateHomeAsync(IPage page, MetaWebSessionCapture? capture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // ALWAYS load the home page fresh, even if the browser is already
            // somewhere on meta.ai. Two reasons, both observed live 2026-07-12:
            // Chromium's session restore reopens the previous run's
            // conversation page in a stale state where Enter submits nothing;
            // and reusing a conversation appends prompts to its history, which
            // can bleed context into later generations. A fresh home load
            // gives every prompt a rewired page and its own new chat.
            await page.GotoAsync(HomeUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });

            capture?.Event("navigated", new { url = page.Url });
        }

        /// Authenticated == the message composer exists and there is no login
        /// affordance. NOTE (observed live 2026-07-12): meta.ai's composer is
        /// <textarea data-testid="composer-input">, and it is HIDDEN by
        /// Playwright's visibility rules (an invisible textarea backing a
        /// styled display layer) — so we wait for Attached, never Visible.
        private async Task EnsureAuthenticatedAsync(IPage page, MetaWebSessionCapture? capture, CancellationToken ct)
        {
            if (await WaitForComposerAsync(page, 30_000))
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            capture?.Event("no-composer", new { url = page.Url });

            if (_options.Headed)
            {
                // Headed first-run mode: give the user time to complete the
                // login in the visible window, then continue.
                Logger.Log("Meta web: no composer found — assuming you need to log in. Waiting up to 5 minutes for you to finish logging in to meta.ai in the browser window...");
                if (await WaitForComposerAsync(page, 300_000))
                {
                    Logger.Log("Meta web: composer found — login complete, continuing.");
                    return;
                }
            }

            throw new MetaWebException(
                "Meta web: not authenticated (no usable message composer on the page). "
                + "Run once with --meta-web --meta-web-headed and log in, or refresh the cookie export "
                + "(needs datr + ecto_1_sess; abra_sess optional). Region/account eligibility can also block Meta AI.");
        }

        /// True when a composer is attached AND no login affordance is showing.
        /// The composer can be attached on the logged-out page too, so its
        /// presence alone does not prove an authenticated session.
        private static async Task<bool> WaitForComposerAsync(IPage page, float timeoutMs)
        {
            try
            {
                await ComposerLocator(page).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = timeoutMs,
                });
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (PlaywrightException)
            {
                return false;
            }

            var loginAffordances = await page.Locator("a[href*=\"login\" i], button:has-text(\"Log in\"), button:has-text(\"Sign in\")").CountAsync();
            return loginAffordances == 0;
        }

        private static ILocator ComposerLocator(IPage page)
            => page.Locator("textarea[data-testid=\"composer-input\"], div[contenteditable=\"true\"], [role=\"textbox\"]");

        private static async Task SubmitPromptAsync(IPage page, string prompt, MetaWebSessionCapture? capture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // The composer textarea itself is hidden to Playwright (an
            // invisible element backing a styled box), so neither Click nor
            // Focus works on it directly — observed live 2026-07-12: focus
            // lands on BODY and the typed text goes nowhere. Do what a real
            // user does instead: click the middle of the composer's nearest
            // VISIBLE ancestor; the site's own handlers then focus the input.
            //
            // Also observed live: clicking right after navigation, before the
            // page has hydrated, silently focuses nothing (BODY again). Focus
            // landing in the composer is the only reliable "page is
            // interactive" signal, so keep clicking until it does, bounded.
            string? focusedTag = null;
            string? focusedTestId = null;
            var interactiveDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var target = await page.EvaluateAsync<JsonElement>(
                    @"() => {
                        const ta = document.querySelector('textarea[data-testid=""composer-input""]')
                            || document.querySelector('div[contenteditable=""true""]')
                            || document.querySelector('[role=""textbox""]');
                        if (!ta) return null;
                        let el = ta;
                        while (el && el !== document.body) {
                            const r = el.getBoundingClientRect();
                            if (r.width > 0 && r.height > 0) break;
                            el = el.parentElement;
                        }
                        const r = el.getBoundingClientRect();
                        return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
                    }");
                if (target.ValueKind == JsonValueKind.Object)
                {
                    await page.Mouse.ClickAsync(
                        (float)target.GetProperty("x").GetDouble(),
                        (float)target.GetProperty("y").GetDouble());

                    var focused = await page.EvaluateAsync<JsonElement>(
                        @"() => { const el = document.activeElement; return {
                            tag: el ? el.tagName : null,
                            testId: el ? el.getAttribute('data-testid') : null,
                            editable: el ? (el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') : false,
                        }; }");
                    focusedTag = focused.GetProperty("tag").GetString();
                    focusedTestId = focused.GetProperty("testId").GetString();
                    if (focused.GetProperty("editable").GetBoolean() || focusedTestId == "composer-input")
                    {
                        break;
                    }
                }

                if (DateTime.UtcNow >= interactiveDeadline)
                {
                    capture?.Event("focused", new { tag = focusedTag, testId = focusedTestId });
                    throw new MetaWebException(
                        "Meta web: the composer never accepted focus within 15s (page not interactive, or layout changed). "
                        + $"Last focus target: {focusedTag ?? "none"}.");
                }

                await Task.Delay(500, ct);
            }

            capture?.Event("focused", new { tag = focusedTag, testId = focusedTestId });

            // InsertText goes through the real input event pipeline, which the
            // editor requires (Fill bypasses its state handling).
            await page.Keyboard.InsertTextAsync(BuildComposerText(prompt));

            // Confirm the text actually landed before submitting; failing here
            // beats a blind settle-loop timeout later.
            var typed = await ReadComposerValueAsync(page);
            capture?.Event("typed", new { composerLength = typed.Length });
            if (typed.Length == 0)
            {
                throw new MetaWebException(
                    "Meta web: prompt text did not reach the composer (focus went to "
                    + $"{focusedTag}). The page layout likely changed.");
            }

            await page.Keyboard.PressAsync("Enter");

            // The composer empties itself when a submission is accepted; if our
            // text is still sitting there the Enter did not submit.
            await Task.Delay(1500, ct);
            var residue = await ReadComposerValueAsync(page);
            capture?.Event("submitted", new { promptLength = prompt.Length, url = page.Url, composerResidueLength = residue.Length });
            if (residue.Length > 0)
            {
                throw new MetaWebException(
                    "Meta web: Enter did not submit the prompt (text still in the composer). The page layout likely changed.");
            }
        }

        private static async Task<string> ReadComposerValueAsync(IPage page)
        {
            var value = await page.EvaluateAsync<string?>(
                @"() => {
                    const ta = document.querySelector('textarea[data-testid=""composer-input""]');
                    if (ta) return ta.value || '';
                    const ce = document.querySelector('div[contenteditable=""true""]');
                    return ce ? (ce.textContent || '') : '';
                }");
            return value ?? "";
        }

        /// meta.ai is a chat product: a plain statement or question can get a
        /// TEXT answer, in which case no image ever appears. "Imagine <x>"
        /// (Meta's historical trigger, what the retired GraphQL path sent)
        /// proved too weak for abstract prompts — observed live 2026-07-12:
        /// "Imagine [[[ the most likely new emoji ... by 2029 ]]]" got an
        /// essay, because "imagine <hypothetical>" reads as a thought
        /// experiment. Use an unambiguous imperative instead, unless the
        /// prompt already leads with its own image-intent verb.
        public static string BuildComposerText(string prompt)
        {
            var trimmed = (prompt ?? string.Empty).TrimStart();
            foreach (var lead in new[] { "imagine", "generate", "create", "draw" })
            {
                if (trimmed.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                {
                    return prompt!;
                }
            }

            return "Generate an image: " + prompt;
        }

        private sealed record PageImage(string Url, int W, int H, bool IsCandidate);

        /// One poll of every rendered image on the page: best-resolution source
        /// (srcset-aware) plus natural dimensions, and whether it looks like
        /// generated media (CDN host, not a known UI/avatar/thumbnail asset).
        private static async Task<List<PageImage>> CollectPageImagesAsync(IPage page)
        {
            const string js = @"() => {
                const bad = /(emoji|static|rsrc|sticker|avatar|profile|favicon|safe_image|spinner|\.svg([?#]|$))/i;
                const cdn = /(fbcdn\.net|cdninstagram\.com|scontent)/i;
                const pick = (img) => {
                    const ss = img.getAttribute('srcset');
                    if (ss) {
                        let best = '', bestW = -1;
                        for (const part of ss.split(',')) {
                            const bits = part.trim().split(/\s+/);
                            if (!bits[0]) continue;
                            let w = 0;
                            const d = bits[1] || '';
                            if (d.endsWith('w')) w = parseFloat(d);
                            else if (d.endsWith('x')) w = parseFloat(d) * 1000;
                            if (w >= bestW) { bestW = w; best = bits[0]; }
                        }
                        if (best) return best;
                    }
                    return img.currentSrc || img.src || '';
                };
                const out = [];
                for (const img of document.querySelectorAll('img')) {
                    const url = pick(img);
                    if (!url || !url.startsWith('http')) continue;
                    out.push({
                        url,
                        w: img.naturalWidth || 0,
                        h: img.naturalHeight || 0,
                        isCandidate: cdn.test(url) && !bad.test(url),
                    });
                }
                for (const el of document.querySelectorAll('[style*=""background-image""]')) {
                    const m = /url\([""']?(https?:[^""')]+)[""']?\)/.exec(el.style.backgroundImage || '');
                    if (!m) continue;
                    out.push({
                        url: m[1],
                        w: el.clientWidth || 0,
                        h: el.clientHeight || 0,
                        isCandidate: cdn.test(m[1]) && !bad.test(m[1]),
                    });
                }
                return out;
            }";

            var result = await page.EvaluateAsync<JsonElement>(js);
            var images = new List<PageImage>();
            foreach (var item in result.EnumerateArray())
            {
                images.Add(new PageImage(
                    item.GetProperty("url").GetString() ?? "",
                    item.GetProperty("w").GetInt32(),
                    item.GetProperty("h").GetInt32(),
                    item.GetProperty("isCandidate").GetBoolean()));
            }

            return images;
        }

        /// A completed text answer with zero images means Meta declined to
        /// generate (refusal or it just chatted). The response text must be
        /// finished AND unchanged for this many 1-second polls, with no image,
        /// before we give up — generous so a slow image arriving after a text
        /// preamble is not mistaken for a refusal.
        private const int TextAnswerStablePolls = 30;

        /// The response text must have grown by at least this many characters
        /// over the just-after-submit snapshot to count as a text answer; short
        /// captions accompanying image generation stay below this.
        private const int TextAnswerMinChars = 80;

        /// Poll until new (post-submission) candidate images appear and the set
        /// stays stable for SettleConsecutivePolls polls. Anything already on
        /// the page before submission is ignored so a previous prompt's output
        /// can never leak into this one. If Meta instead finishes a TEXT answer
        /// (refusals look exactly like that — observed live 2026-07-12), fail
        /// fast with the response text rather than waiting out the timeout.
        private static async Task<List<string>> WaitForNewImagesAsync(
            IPage page,
            List<PageImage> baseline,
            MetaWebSessionCapture? capture,
            CancellationToken ct)
        {
            var baselineUrls = new HashSet<string>(baseline.Select(i => i.Url), StringComparer.Ordinal);
            string? lastSignature = null;
            var lastPageUrl = page.Url;
            var stablePolls = 0;
            var baselineTextLen = -1;
            var lastTextTail = "";
            var textStablePolls = 0;

            while (true)
            {
                await Task.Delay(1000, ct);
                if (page.Url != lastPageUrl)
                {
                    capture?.Event("page-url-changed", new { from = lastPageUrl, to = page.Url });
                    lastPageUrl = page.Url;
                }

                var current = await CollectPageImagesAsync(page);
                var fresh = current
                    .Where(i => i.IsCandidate
                                && !baselineUrls.Contains(i.Url)
                                && i.W >= MinCandidateEdge
                                && i.H >= MinCandidateEdge)
                    .Select(i => i.Url)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(u => u, StringComparer.Ordinal)
                    .ToList();

                var signature = string.Join("\n", fresh);
                if (fresh.Count > 0 && signature == lastSignature)
                {
                    stablePolls++;
                    if (stablePolls >= SettleConsecutivePolls)
                    {
                        return fresh;
                    }
                }
                else
                {
                    if (fresh.Count > 0 && signature != lastSignature)
                    {
                        capture?.Event("new-media", new { urls = fresh });
                    }

                    stablePolls = 0;
                    lastSignature = signature;
                }

                // Text-answer / refusal detection, only while no image at all.
                if (fresh.Count == 0)
                {
                    var text = await ReadConversationTextAsync(page);
                    if (baselineTextLen < 0)
                    {
                        baselineTextLen = text.Length;
                        lastTextTail = Tail(text, 500);
                        continue;
                    }

                    var tail = Tail(text, 500);
                    if (tail == lastTextTail && text.Length - baselineTextLen >= TextAnswerMinChars)
                    {
                        textStablePolls++;
                        if (textStablePolls >= TextAnswerStablePolls)
                        {
                            capture?.Event("text-answer", new { textLength = text.Length, tail });
                            throw new MetaWebException(
                                "Meta web: Meta answered with text and produced no image — most likely the prompt was "
                                + $"refused. End of Meta's response: \"{Tail(text, 300)}\"");
                        }
                    }
                    else
                    {
                        textStablePolls = 0;
                        lastTextTail = tail;
                    }
                }
                else
                {
                    textStablePolls = 0;
                }
            }
        }

        private static async Task<string> ReadConversationTextAsync(IPage page)
        {
            var text = await page.EvaluateAsync<string?>(
                @"() => {
                    const m = document.querySelector('main') || document.body;
                    return (m.innerText || '').replace(/\s+/g, ' ').trim();
                }");
            return text ?? "";
        }

        private static string Tail(string s, int max)
            => s.Length <= max ? s : s[^max..];

        /// Download through the browser context (shares its cookies), validate
        /// content type + magic bytes, and return the untouched original bytes.
        /// Returns null for candidates that turn out not to be images.
        private async Task<byte[]?> DownloadImageAsync(string url, MetaWebSessionCapture? capture, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _context!.APIRequest.GetAsync(url);
            var contentType = response.Headers.TryGetValue("content-type", out var v) ? v : "";
            if (response.Status != 200)
            {
                capture?.Event("download-rejected", new { url, status = response.Status, contentType });
                Logger.Log($"\t   Meta web: download failed ({response.Status}) for candidate {url}");
                return null;
            }

            var bytes = await response.BodyAsync();
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || !LooksLikeImageBytes(bytes))
            {
                capture?.Event("download-rejected", new { url, status = response.Status, contentType, byteLength = bytes.Length });
                Logger.Log($"\t   Meta web: candidate is not an image (content-type={contentType}), skipping: {url}");
                return null;
            }

            capture?.Event("downloaded", new { url, status = response.Status, contentType, byteLength = bytes.Length });
            return bytes;
        }

        private static bool LooksLikeImageBytes(byte[] bytes)
        {
            if (bytes.Length < 12)
            {
                return false;
            }

            // PNG
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return true;
            }

            // JPEG
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return true;
            }

            // WEBP
            if (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return true;
            }

            // GIF
            if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
            {
                return true;
            }

            return false;
        }

        private async Task CaptureFailureAsync(MetaWebSessionCapture? capture, string reason)
        {
            if (capture == null)
            {
                return;
            }

            try
            {
                if (_page != null)
                {
                    await capture.ScreenshotAsync(_page, reason);
                }
            }
            catch (Exception ex)
            {
                capture.Event("screenshot-failed", new { error = ex.Message });
            }

            capture.Complete(reason, 0);
        }

        public async ValueTask DisposeAsync()
        {
            if (_context != null)
            {
                await _context.CloseAsync();
                _context = null;
            }

            _playwright?.Dispose();
            _playwright = null;
            _profileLease?.Dispose();
            _profileLease = null;
            _gate.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
