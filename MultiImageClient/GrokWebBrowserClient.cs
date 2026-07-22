#nullable enable
using Microsoft.Playwright;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class GrokWebBrowserClientOptions
    {
        public required string CookiePath { get; init; }
        public string BrowserExecutablePath { get; init; } = "";
        public bool Headed { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
    }

    public sealed class GrokWebBrowserResponse
    {
        public int StatusCode { get; init; }
        public required string Body { get; init; }
        public required string Url { get; init; }
    }

    // grok.com's app-chat endpoint rejects standalone HTTP clients even when
    // they copy browser headers. Video generation therefore performs only that
    // POST inside a real logged-in Chromium page. Uploads, image generation,
    // media polling, downloads, and saving remain in GrokWebClient.
    public sealed class GrokWebBrowserClient : IAsyncDisposable, IDisposable
    {
        public const string ImagineUrl = "https://grok.com/imagine";

        private readonly GrokWebBrowserClientOptions _options;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IBrowserContext? _context;
        private IPage? _page;

        public GrokWebBrowserClient(GrokWebBrowserClientOptions options)
        {
            _options = options;
            if (string.IsNullOrWhiteSpace(options.CookiePath))
            {
                throw new ArgumentException("CookiePath is empty.", nameof(options));
            }
        }

        public static GrokWebBrowserClientOptions BuildOptions(
            Settings settings,
            string cookiePath,
            bool headedOverride = false)
        {
            return new GrokWebBrowserClientOptions
            {
                CookiePath = Settings.ExpandPath(cookiePath),
                BrowserExecutablePath = Settings.ExpandPath(settings.GrokWebBrowserExecutablePath),
                Headed = headedOverride || settings.GrokWebBrowserHeaded,
                Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.GrokWebVideoTimeoutSeconds)),
            };
        }

        public async Task<GrokWebBrowserResponse> PostAppChatAsync(
            object payload,
            string? triggerPostId,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.Timeout);
            var ct = timeoutCts.Token;
            var gateAcquired = false;
            try
            {
                await _gate.WaitAsync(ct);
                gateAcquired = true;
                await EnsureStartedAsync(ct);
                await PrepareImaginePageAsync(triggerPostId, ct);

                var payloadJson = JsonSerializer.Serialize(payload);
                if (!string.IsNullOrWhiteSpace(triggerPostId))
                {
                    return await TriggerAppChatFromPageAsync(payloadJson, ct);
                }

                var responseTask = _page!.EvaluateAsync<JsonElement>(
                    """
                    async ({ payloadJson, timeoutMs }) => {
                        const controller = new AbortController();
                        const timer = setTimeout(() => controller.abort(), timeoutMs);
                        try {
                            const response = await fetch("/rest/app-chat/conversations/new", {
                                method: "POST",
                                credentials: "include",
                                headers: { "Content-Type": "application/json" },
                                body: payloadJson,
                                signal: controller.signal,
                            });
                            return {
                                statusCode: response.status,
                                body: await response.text(),
                                url: response.url,
                            };
                        } catch (error) {
                            return {
                                statusCode: 0,
                                body: String(error),
                                url: location.origin + "/rest/app-chat/conversations/new",
                            };
                        } finally {
                            clearTimeout(timer);
                        }
                    }
                    """,
                    new
                    {
                        payloadJson,
                        timeoutMs = (int)_options.Timeout.TotalMilliseconds,
                    });
                var result = await responseTask.WaitAsync(ct);
                return new GrokWebBrowserResponse
                {
                    StatusCode = result.GetProperty("statusCode").GetInt32(),
                    Body = result.GetProperty("body").GetString() ?? "",
                    Url = result.GetProperty("url").GetString() ?? GrokWebClient.Origin + "/rest/app-chat/conversations/new",
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GrokWebException(
                    $"Grok web browser video request timed out after {_options.Timeout.TotalSeconds:0} seconds.");
            }
            catch (PlaywrightException ex)
            {
                throw new GrokWebException($"Grok web browser transport failed: {ex.Message}");
            }
            finally
            {
                if (gateAcquired)
                {
                    _gate.Release();
                }
            }
        }

        private async Task<GrokWebBrowserResponse> TriggerAppChatFromPageAsync(
            string payloadJson,
            CancellationToken ct)
        {
            const string endpointPattern = "**/rest/app-chat/conversations/new";
            var responseSource = new TaskCompletionSource<IResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var requestSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void HandleResponse(object? sender, IResponse response)
            {
                if (response.Request.Method == "POST"
                    && response.Url.Contains(
                        "/rest/app-chat/conversations/new",
                        StringComparison.Ordinal))
                {
                    responseSource.TrySetResult(response);
                }
            }

            Func<IRoute, Task> routeHandler = async route =>
            {
                requestSource.TrySetResult();
                await route.ContinueAsync(new RouteContinueOptions
                {
                    PostData = Encoding.UTF8.GetBytes(payloadJson),
                });
            };

            _page!.Response += HandleResponse;
            await _page.RouteAsync(endpointPattern, routeHandler);
            try
            {
                var makeVideo = _page.Locator(
                    "button[aria-label=\"Make video\" i], button:has-text(\"Make Video\")").First;
                await makeVideo.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 30_000,
                });

                // Clicking the site's real control is important: its generated
                // API client computes x-statsig-id for this path and method.
                // Route interception preserves those integrity headers while
                // replacing only the request body with our requested options.
                await makeVideo.ClickAsync(new LocatorClickOptions { Force = true });
                var firstWait = await Task.WhenAny(
                    responseSource.Task,
                    requestSource.Task,
                    Task.Delay(TimeSpan.FromSeconds(2), ct));
                if (firstWait != responseSource.Task && firstWait != requestSource.Task)
                {
                    // On a post detail page Make Video opens a menu. Quick
                    // Animate is the one-click action that creates the real
                    // integrity-signed request; route interception supplies
                    // the user's selected method and optional prompt.
                    var quickAnimate = _page.GetByRole(
                        AriaRole.Menuitem,
                        new PageGetByRoleOptions
                        {
                            Name = "Quick Animate",
                            Exact = true,
                        });
                    try
                    {
                        await quickAnimate.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 10_000,
                        });
                        await quickAnimate.ClickAsync(new LocatorClickOptions { Force = true });
                    }
                    catch (TimeoutException)
                    {
                        Logger.Log(
                            "Grok web browser: Quick Animate did not appear after Make Video.");
                    }
                }

                var secondWait = await Task.WhenAny(
                    responseSource.Task,
                    requestSource.Task,
                    Task.Delay(TimeSpan.FromSeconds(2), ct));
                if (secondWait != responseSource.Task && secondWait != requestSource.Task)
                {
                    // The current post page has separate Image/Video composer
                    // toggles. Select Video, enter harmless text to enable the
                    // submit arrow, then click that arrow. Route interception
                    // replaces the outgoing body with the requested options.
                    var videoToggle = _page.Locator("button[aria-label=\"Video\" i]").Last;
                    if (await videoToggle.CountAsync() > 0 && await videoToggle.IsVisibleAsync())
                    {
                        await videoToggle.ClickAsync(new LocatorClickOptions { Force = true });
                    }

                    var videoComposer = _page.Locator(
                        "textarea[aria-label=\"Make a video\" i], "
                        + "textarea[name*=\"prompt\" i], "
                        + "textarea[placeholder*=\"video\" i], "
                        + "[contenteditable=\"true\"]").Last;
                    if (await videoComposer.CountAsync() > 0 && await videoComposer.IsVisibleAsync())
                    {
                        await videoComposer.FillAsync("Animate this image.");
                    }

                    // The composer submit uses lower-case "video"; the large
                    // side-panel mode selector uses "Make Video".
                    var composerSubmit = _page.Locator(
                        "button[aria-label=\"Make video\"]").Last;
                    if (await composerSubmit.CountAsync() > 0 && await composerSubmit.IsVisibleAsync())
                    {
                        await composerSubmit.ClickAsync(new LocatorClickOptions { Force = true });
                    }
                }

                IResponse response;
                try
                {
                    response = await responseSource.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                }
                catch (TimeoutException)
                {
                    var controls = await _page.EvaluateAsync<string>(
                        """
                        () => JSON.stringify({
                            url: location.href,
                            buttons: [...document.querySelectorAll("button")]
                                .filter(x => {
                                    const r = x.getBoundingClientRect();
                                    return r.width > 0 && r.height > 0;
                                })
                                .map(x => ({
                                    text: (x.innerText || "").trim().slice(0, 80),
                                    aria: x.getAttribute("aria-label"),
                                    title: x.getAttribute("title"),
                                    disabled: x.disabled,
                                })),
                            inputs: [...document.querySelectorAll("textarea, input, [contenteditable=true]")]
                                .filter(x => {
                                    const r = x.getBoundingClientRect();
                                    return r.width > 0 && r.height > 0;
                                })
                                .map(x => ({
                                    tag: x.tagName,
                                    aria: x.getAttribute("aria-label"),
                                    name: x.getAttribute("name"),
                                    placeholder: x.getAttribute("placeholder"),
                                })),
                        })
                        """);
                    var screenshotPath = Path.Combine(
                        Path.GetTempPath(),
                        $"grok-web-video-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    await _page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = screenshotPath,
                        FullPage = true,
                    });
                    Logger.Log(
                        $"Grok web video controls: {controls}; screenshot: {screenshotPath}");
                    throw new GrokWebException(
                        "Grok web: the real Make Video control did not start an app-chat request. "
                        + "The Imagine page layout or account controls may have changed; retry with --grok-web-headed.");
                }
                string body;
                try
                {
                    // The endpoint streams. On silent moderation failures Grok can
                    // leave the body open indefinitely after returning HTTP 200.
                    // The durable media-post poll is the source of truth, so do
                    // not hold the whole video job for the 15-minute browser limit.
                    var bodyTimeout = TimeSpan.FromSeconds(
                        Math.Min(30, Math.Max(5, _options.Timeout.TotalSeconds)));
                    body = await response.TextAsync().WaitAsync(bodyTimeout, ct);
                }
                catch (TimeoutException)
                {
                    body = "";
                    Logger.Log(
                        "Grok web browser: app-chat returned headers but left its streaming body open; "
                        + "continuing with media-post polling.");
                }
                catch (PlaywrightException ex)
                {
                    body = "";
                    Logger.Log(
                        $"Grok web browser: could not read the accepted app-chat stream ({ex.Message}); "
                        + "continuing with media-post polling.");
                }

                return new GrokWebBrowserResponse
                {
                    StatusCode = response.Status,
                    Body = body,
                    Url = response.Url,
                };
            }
            finally
            {
                _page.Response -= HandleResponse;
                await _page.UnrouteAsync(endpointPattern, routeHandler);
            }
        }

        private async Task EnsureStartedAsync(CancellationToken ct)
        {
            if (_context != null)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            _playwright ??= await Playwright.CreateAsync();
            var launch = new BrowserTypeLaunchOptions
            {
                Headless = !_options.Headed,
                Args = new[] { "--disable-blink-features=AutomationControlled" },
            };
            if (!string.IsNullOrWhiteSpace(_options.BrowserExecutablePath))
            {
                launch.ExecutablePath = Settings.ExpandPath(_options.BrowserExecutablePath);
            }

            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(launch);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            {
                throw new GrokWebException(
                    "Playwright's Chromium is not installed. Run once with --playwright-install "
                    + "(or set GrokWebBrowserExecutablePath to an existing Chrome/Chromium binary).");
            }

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            });
            var pairs = GrokWebCookieLoader.LoadCookiePairs(_options.CookiePath);
            await _context.AddCookiesAsync(pairs.Select(kvp => new Cookie
            {
                Name = kvp.Key,
                Value = kvp.Value,
                Domain = ".grok.com",
                Path = "/",
                Secure = true,
            }));
            _page = await _context.NewPageAsync();
        }

        private async Task PrepareImaginePageAsync(string? triggerPostId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = string.IsNullOrWhiteSpace(triggerPostId)
                ? ImagineUrl
                : $"{ImagineUrl}/post/{Uri.EscapeDataString(triggerPostId)}";
            await _page!.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });

            var quotaTask = _page.EvaluateAsync<JsonElement>(
                """
                async () => {
                    const response = await fetch("/rest/media/imagine/quota_info", {
                        method: "POST",
                        credentials: "include",
                        headers: { "Content-Type": "application/json" },
                        body: "{}",
                    });
                    return {
                        statusCode: response.status,
                        body: await response.text(),
                    };
                }
                """);
            var quota = await quotaTask.WaitAsync(ct);
            var statusCode = quota.GetProperty("statusCode").GetInt32();
            var body = quota.GetProperty("body").GetString() ?? "";
            var logicalError = statusCode == 200 ? ReadQuotaError(body) : null;
            if (statusCode != 200 || logicalError != null)
            {
                throw new GrokWebException(
                    $"Grok web browser session is not authenticated or quota lookup failed ({statusCode}). "
                    + "Refresh the GrokWebCookiePath export."
                    + (logicalError == null ? "" : $" Provider response: {logicalError}"),
                    statusCode,
                    body);
            }
        }

        private static string? ReadQuotaError(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return "quota endpoint did not return a JSON object";
                }
                if (root.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False)
                {
                    return "quota endpoint returned success=false";
                }
                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                {
                    return error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : error.GetRawText();
                }
                return null;
            }
            catch (JsonException)
            {
                return "quota endpoint returned a non-JSON response";
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_context != null)
            {
                await _context.CloseAsync();
                _context = null;
            }
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }
            _playwright?.Dispose();
            _playwright = null;
            _gate.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
