#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace MultiImageClient
{
    // Members-only share links. The public address carries no credential. It reveals the
    // private site address only after authentication and current environment membership.
    public partial class UiWorkflow
    {
        public static bool IsShareLinkRequest(string path, string method) =>
            method is "GET" or "POST" && Regex.IsMatch(path, "\\A/public/prompt/[a-f0-9]{12}\\z");

        public static string? ShareLinksProblem(Settings settings, UiAuth? auth)
        {
            if (auth == null) return "Share links need site login.";
            try { UiPublicShares.BaseUrl(settings); }
            catch (InvalidOperationException ex) { return ex.Message; }
            return Uri.TryCreate(settings.UiPublicBaseUrl.TrimEnd('/'), UriKind.Absolute, out var site) && site.Scheme == "https"
                ? null : "Configure the private HTTPS site address.";
        }

        // A link names a whole prompt, or one result through both its generator and index.
        public static bool TryParseShareTarget(StringValues generators, StringValues indexes, out string? generator, out int? imageIndex)
        {
            generator = null;
            imageIndex = null;
            if (generators.Count == 0 && indexes.Count == 0) return true;
            if (generators.Count != 1 || indexes.Count != 1
                || !Regex.IsMatch(generators[0] ?? "", "\\A[a-z0-9][a-z0-9-]{0,63}\\z")
                || !Regex.IsMatch(indexes[0] ?? "", "\\A(?:0|[1-9][0-9]{0,3})\\z"))
                return false;
            generator = generators[0];
            imageIndex = int.Parse(indexes[0]!, CultureInfo.InvariantCulture);
            return true;
        }

        public static string ShareLinkUrl(Settings settings, string jobId, string? generator, int? imageIndex) =>
            UiPublicShares.BaseUrl(settings) + "/prompt/" + jobId + ShareTargetQuery("?", generator, imageIndex);

        public static string ShareLinkDestination(Settings settings, string jobId, string? generator, int? imageIndex) =>
            settings.UiPublicBaseUrl.TrimEnd('/') + "/?job=" + jobId + ShareTargetQuery("&", generator, imageIndex);

        private static string ShareTargetQuery(string separator, string? generator, int? imageIndex) => generator == null ? ""
            : separator + "gen=" + Uri.EscapeDataString(generator) + "&n=" + imageIndex!.Value.ToString(CultureInfo.InvariantCulture);

        private static bool ValidShareJobId(string jobId) => Regex.IsMatch(jobId, "\\A[a-f0-9]{12}\\z");

        // Exactly one successful result must own the identity. Videos have no viewer entry.
        private static bool TryResolveShareResult(UiJob job, string generator, int imageIndex, UiVisibilityStore visibility, out string error)
        {
            if (visibility.IsImageHidden(job.Id, generator, imageIndex))
            {
                error = "This result was deleted.";
                return false;
            }
            var matches = 0;
            foreach (var line in job.ReadFrom(0).Events)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "gen-result"
                    || !root.TryGetProperty("gen", out var gen) || gen.GetString() != generator
                    || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    continue;
                if (root.TryGetProperty("mediaType", out var mediaType)
                    && (mediaType.GetString() ?? "").StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Share a video through its prompt link.";
                    return false;
                }
                if (root.TryGetProperty("resultKind", out var kind) && kind.GetString() == "text")
                {
                    if (root.TryGetProperty("texts", out var texts) && texts.ValueKind == JsonValueKind.Array)
                        foreach (var text in texts.EnumerateArray())
                            if (text.TryGetProperty("inputIndex", out var input) && input.TryGetInt32(out var value) && value == imageIndex)
                                matches++;
                    continue;
                }
                if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
                    && imageIndex < images.GetArrayLength() && images[imageIndex].ValueKind == JsonValueKind.String
                    && images[imageIndex].GetString()!.Length > 0)
                    matches++;
            }
            error = matches == 1 ? "" : matches == 0 ? "This result is unavailable." : "This result has more than one record.";
            return matches == 1;
        }

        private static void ShareLinkHeaders(HttpContext ctx, bool script)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"
                + (script ? "; connect-src 'self'; script-src '" + ShareLinkLoginScriptHash + "'" : "");
        }

        // Browsers hash the parsed script, where every line break is LF, whatever this source file uses.
        public static readonly string ShareLinkLoginScript = """
            (() => {
              const form = document.getElementById("login"), button = form.querySelector("button"), status = document.getElementById("status");
              button.disabled = false;
              form.addEventListener("submit", async event => {
                event.preventDefault(); button.disabled = true; status.className = ""; status.textContent = "Checking your login…";
                try {
                  const response = await fetch(location.pathname + location.search, { method: "POST", mode: "cors", cache: "no-store",
                    headers: { "X-Mic-Login": "1" }, body: new URLSearchParams(new FormData(form)) });
                  if (!response.headers.get("content-type")?.includes("application/json")) throw new Error("The server could not check this login. Try again later.");
                  const result = await response.json();
                  if (!response.ok) throw new Error(result.error || "The login failed.");
                  status.textContent = "Opening the prompt…";
                  location.replace(result.destination);
                } catch (error) { status.className = "error"; status.textContent = error.message; button.disabled = false; }
              });
            })();
            """.ReplaceLineEndings("\n");

        private static readonly string ShareLinkLoginScriptHash =
            "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ShareLinkLoginScript)));

        public static string ShareLinkPage(string title, string body) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"referrer\" content=\"no-referrer\">"
            + "<meta name=\"robots\" content=\"noindex,nofollow,noarchive\"><title>" + WebUtility.HtmlEncode(title) + "</title><style>"
            + "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;font:16px/1.5 system-ui,sans-serif;color:#000;background:#f5f2ea}"
            + "main{box-sizing:border-box;width:min(460px,calc(100% - 32px));margin:16px;padding:28px;background:#fff;border:1px solid #b9b09c;border-radius:8px}"
            + "h1{margin:0 0 8px;font-size:24px;line-height:1.25}p{margin:0 0 16px}form{display:flex;flex-direction:column;gap:14px;margin-bottom:16px}"
            + "label{display:flex;flex-direction:column;gap:4px;font-weight:700}input{font:inherit;padding:9px 11px;color:#000;border:1px solid #5b5446;border-radius:5px}"
            + "button{font:inherit;font-weight:700;padding:10px;color:#fff;background:#2456b8;border:0;border-radius:5px;cursor:pointer}button:disabled{cursor:progress}"
            + "#status{min-height:1.5em;margin:0;font-weight:700}#status.error{color:#b00020}a{color:#2456b8}"
            + "</style></head><body><main><h1>" + WebUtility.HtmlEncode(title) + "</h1>" + body + "</main></body></html>";

        public static string ShareLinkLoginPage(string siteName, bool signedInWithoutAccess, string? requestAccountUrl)
        {
            static string E(string value) => WebUtility.HtmlEncode(value);
            return ShareLinkPage("Log in to " + siteName,
                (signedInWithoutAccess
                    ? "<p>Your current account cannot open " + E(siteName) + ". Log in with an account that has access.</p>"
                    : "<p>Log in to open the shared prompt.</p>")
                + "<form id=\"login\" method=\"post\"><label>Username <input name=\"username\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" required autofocus></label>"
                + "<label>Password <input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label>"
                + "<button disabled>Log in and open</button><p id=\"status\" role=\"status\"></p></form>"
                + (requestAccountUrl == null
                    ? "<p>Need an account? Ask Ernie.</p>"
                    : "<p>Need an account? <a href=\"" + E(requestAccountUrl) + "\">Request an account</a> through Discord.</p>")
                + "<noscript><p>Enable JavaScript to log in.</p></noscript><script>" + ShareLinkLoginScript + "</script>");
        }

        private static void MapShareLinks(WebApplication app, Settings settings, UiAuth? auth, UiJobRegistry jobs,
            UiVisibilityStore visibility, UiEnvironmentRegistry? environments, UiAccountActivity? accountActivity)
        {
            if (auth == null) return;
            UiAuth siteAuth = auth;
            string SiteName()
            {
                var name = environments?.Get(settings.UiEnvironmentId).Name ?? settings.UiEnvironmentName;
                return string.IsNullOrWhiteSpace(name) ? "MultiImageClient" : name;
            }
            bool Member(string login) => environments?.CanEnter(settings.UiEnvironmentId, login) ?? true;
            bool Available(string jobId) => jobs.Get(jobId) != null && !visibility.IsPromptHidden(jobId);
            string? RequestAccountUrl() => settings.UiEnvironmentId == UiDiscordAccountRequests.EnvironmentId
                ? UiDiscordAccountRequests.PublicUrl(settings, auth, environments) : null;
            IResult Page(HttpContext ctx, int status, string title, string body)
            {
                ShareLinkHeaders(ctx, false);
                return Results.Content(ShareLinkPage(title, body), "text/html; charset=utf-8", statusCode: status);
            }

            app.MapGet("/api/share-link", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var problem = ShareLinksProblem(settings, siteAuth);
                if (problem != null) return Results.Json(new { error = problem }, statusCode: 404);
                var jobId = ctx.Request.Query["jobId"].ToString();
                if (!ValidShareJobId(jobId)) return Results.BadRequest(new { error = "A valid jobId is required." });
                if (!TryParseShareTarget(ctx.Request.Query["generator"], ctx.Request.Query["imageIndex"], out var generator, out var imageIndex))
                    return Results.BadRequest(new { error = "Give a valid generator and imageIndex together, or neither." });
                var job = jobs.Get(jobId);
                if (job == null || visibility.IsPromptHidden(jobId)) return Results.NotFound(new { error = "This prompt is unavailable." });
                if (generator != null && !TryResolveShareResult(job, generator, imageIndex!.Value, visibility, out var error))
                    return Results.NotFound(new { error });
                return Results.Json(new { url = ShareLinkUrl(settings, jobId, generator, imageIndex), siteName = SiteName() });
            });

            app.MapGet("/public/prompt/{jobId}", (string jobId, HttpContext ctx) =>
            {
                if (!ValidShareJobId(jobId)) return Results.NotFound();
                if (ShareLinksProblem(settings, siteAuth) != null) return Page(ctx, 404, "Link unavailable", "<p>Share links are not available on this site.</p>");
                if (!TryParseShareTarget(ctx.Request.Query["gen"], ctx.Request.Query["n"], out var generator, out var imageIndex))
                    return Page(ctx, 400, "Damaged link", "<p>This link is damaged. Ask the sender for a new link.</p>");
                var signedIn = siteAuth.TryValidateCookie(ctx.Request.Cookies[siteAuth.SessionCookieName], out var login);
                if (signedIn && Member(login))
                {
                    if (!Available(jobId))
                        return Page(ctx, 404, "Prompt unavailable", "<p>This prompt is unavailable. Its owner may have deleted it.</p>");
                    ShareLinkHeaders(ctx, false);
                    return Results.Redirect(ShareLinkDestination(settings, jobId, generator, imageIndex));
                }
                ShareLinkHeaders(ctx, true);
                return Results.Content(ShareLinkLoginPage(SiteName(), signedIn, RequestAccountUrl()), "text/html; charset=utf-8");
            });

            app.MapPost("/public/prompt/{jobId}", async (string jobId, HttpContext ctx) =>
            {
                ShareLinkHeaders(ctx, false);
                if (!ValidShareJobId(jobId) || ShareLinksProblem(settings, siteAuth) != null) return Results.NotFound();
                if (ctx.Request.Headers.Origin.ToString() != new Uri(UiPublicShares.BaseUrl(settings)).GetLeftPart(UriPartial.Authority)
                    || ctx.Request.Headers["X-Mic-Login"] != "1")
                    return Results.Json(new { error = "Reload this page and try again." }, statusCode: 403);
                if (ctx.Request.ContentLength is null or > 4096 || !ctx.Request.HasFormContentType)
                    return Results.Json(new { error = "The login request is invalid." }, statusCode: 400);
                if (!TryParseShareTarget(ctx.Request.Query["gen"], ctx.Request.Query["n"], out var generator, out var imageIndex))
                    return Results.Json(new { error = "This link is damaged. Ask the sender for a new link." }, statusCode: 400);
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                var username = form["username"].ToString().Trim();
                var password = form["password"].ToString();
                if (username.Length == 0 || password.Length == 0)
                    return Results.Json(new { error = "Enter your username and password." }, statusCode: 400);
                var address = ClientIpForThrottle(ctx);
                if (!siteAuth.TryLogin(username, password, address, out var cookie, out var error))
                {
                    Logger.Log($"UI auth: failed share-link login for '{username}' from {address}.");
                    return Results.Json(new { error }, statusCode: 401);
                }
                if (!siteAuth.TryValidateCookie(cookie, out var login) || !Member(login))
                {
                    Logger.Log($"UI auth: share-link login for '{username}' from {address} lacks access to this environment.");
                    return Results.Json(new { error = $"This account cannot open {SiteName()}. Log in with an account that has access." }, statusCode: 403);
                }
                ctx.Response.Cookies.Append(siteAuth.SessionCookieName, cookie, new CookieOptions
                {
                    HttpOnly = true, Secure = IsEffectivelyHttps(ctx), SameSite = SameSiteMode.Lax,
                    Path = siteAuth.SessionCookiePath, MaxAge = TimeSpan.FromDays(3650),
                });
                accountActivity?.Record(login, true);
                Logger.Log($"UI auth: '{login}' logged in through a share link from {address}.");
                if (!Available(jobId))
                    return Results.Json(new { error = "You are logged in. This prompt is unavailable. Its owner may have deleted it." }, statusCode: 404);
                return Results.Json(new { destination = ShareLinkDestination(settings, jobId, generator, imageIndex) });
            });
        }
    }
}
