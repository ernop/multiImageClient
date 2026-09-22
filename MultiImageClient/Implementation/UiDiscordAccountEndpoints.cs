#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public static class UiDiscordAccountEndpoints
    {
        public static bool IsPublicRequest(string path, string method) =>
            method == "GET" && path is "/public/signup" or "/public/signup/request" or "/public/signup/claim" or "/public/signup/preview"
            || method == "POST" && path is "/public/signup/request" or "/public/signup/claim";

        public static void Map(WebApplication app, Settings settings, UiAuth? auth, UiEnvironmentRegistry? environments,
            UiAccountActivity? activity = null, UiDiscordAccountRequests? requests = null)
        {
            if (auth?.LoginLinks == null || environments == null || !settings.UiEnvironmentController) return;
            requests ??= new UiDiscordAccountRequests(settings, auth, environments);
            string SignupUrl() => UiDiscordAccountRequests.PublicUrl(settings, auth, environments)
                ?? throw new InvalidOperationException("Discord account requests are disabled.");
            bool OriginMatches(HttpContext ctx) => ctx.Request.Headers.Origin.ToString()
                == new Uri(UiPublicShares.BaseUrl(settings)).GetLeftPart(UriPartial.Authority);
            app.MapGet("/public/signup", (HttpContext ctx) =>
            {
                Headers(ctx, RequestScript);
                if (!requests.Enabled) return Results.NotFound();
                var share = ctx.Request.Query["share"].ToString();
                if (share.Length > 0 && !UiPublicShareStore.ValidToken(share)) return Results.BadRequest();
                return Results.Content(RequestPage(SignupUrl(), share), "text/html; charset=utf-8");
            });
            app.MapGet("/public/signup/request", (HttpContext ctx) =>
            {
                Headers(ctx);
                return !requests.Enabled ? Results.NotFound() : Results.Redirect(SignupUrl());
            });
            app.MapGet("/public/signup/preview", (HttpContext ctx) =>
            {
                Headers(ctx);
                return !requests.Enabled ? Results.NotFound() : Results.Content(Page("Account message test",
                    "<p>This is the test copy sent to Brouhahaha.</p><p>This link cannot create or sign in to an account.</p>"
                    + "<p>Return to Administration and select <strong>Confirmed, send to user</strong> to send the real link.</p>"), "text/html; charset=utf-8");
            });
            app.MapPost("/public/signup/request", async (HttpContext ctx) =>
            {
                Headers(ctx);
                if (!requests.Enabled) return Results.Json(new { error = "Account requests are paused. Try again later." }, statusCode: 404);
                if (!OriginMatches(ctx) || ctx.Request.Headers["X-Mic-Account"] != "1")
                    return Results.Json(new { error = "Reload the account request page before submitting." }, statusCode: 403);
                if (ctx.Request.ContentLength is null or > 2048 || !ctx.Request.HasFormContentType)
                    return Results.Json(new { error = "The account request is invalid or too large." }, statusCode: 400);
                try
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var result = await requests.RequestAsync(form["username"].ToString(), ClientIp(ctx), form["share"].ToString(), ctx.RequestAborted);
                    return Results.Json(new { state = result.State, message = result.Message });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Results.Json(new { error = ex is InvalidOperationException ? ex.Message : "The account request failed. Try again later." }, statusCode: 400);
                }
            });
            bool Owner(HttpContext ctx) => (ctx.Items["micUser"] as string) == UiLoginLinks.OwnerLogin;
            app.MapGet("/api/control/discord-account-requests", (HttpContext ctx) =>
            {
                Headers(ctx);
                if (!Owner(ctx)) return Results.StatusCode(403);
                return Results.Json(new { enabled = requests.Enabled, reviewer = "Brouhahaha", requests = requests.Reviews() });
            });
            app.MapPost("/api/control/discord-account-requests/{id}/{action}", async (string id, string action, HttpContext ctx) =>
            {
                Headers(ctx);
                if (!Owner(ctx) || ctx.Request.Headers["X-Mic-Manage"] != "1") return Results.StatusCode(403);
                try
                {
                    if (action == "reject") { await requests.RejectAsync(id, ctx.RequestAborted); return Results.Json(new { state = "rejected", message = "Request rejected. No account link was sent." }); }
                    var result = action switch
                    {
                        "test" => await requests.TestAsync(id, ctx.RequestAborted),
                        "send" => await requests.SendReviewedAsync(id, ctx.RequestAborted),
                        _ => throw new InvalidOperationException("Unknown account-request action.")
                    };
                    return Results.Json(new { state = result.State, message = result.Message });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Results.Json(new { error = ex is InvalidOperationException ? ex.Message : "The account request could not finish." }, statusCode: 400);
                }
            });
            app.MapGet("/public/signup/claim", (HttpContext ctx) =>
            {
                Headers(ctx, ClaimScript);
                return !requests.Enabled ? Results.NotFound() : Results.Content(ClaimPage(), "text/html; charset=utf-8");
            });
            app.MapPost("/public/signup/claim", async (HttpContext ctx) =>
            {
                Headers(ctx);
                if (!requests.Enabled) return Results.NotFound();
                if (!OriginMatches(ctx)) return Results.StatusCode(403);
                if (ctx.Request.ContentLength is null or > 256 || !ctx.Request.HasFormContentType) return Results.BadRequest();
                try
                {
                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                    var signedIn = await requests.RedeemAsync(form["token"].ToString(), ctx.RequestAborted);
                    activity?.Record(signedIn.Login, true);
                    ctx.Response.Cookies.Append(auth.SessionCookieName, signedIn.Cookie, new CookieOptions
                    { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = auth.SessionCookiePath, MaxAge = TimeSpan.FromDays(3650) });
                    return Results.Json(new { destination = signedIn.Destination });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return Results.Json(new { error = ex is InvalidOperationException or System.IO.InvalidDataException
                        ? ex.Message : "The account request could not finish. Try the link again." }, statusCode: 400);
                }
            });
        }

        private static string ClientIp(HttpContext ctx)
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote != null && IPAddress.IsLoopback(remote) && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
            {
                // nginx appends its actual client after any client-supplied entries.
                var value = forwarded.ToString().Split(',').Last().Trim();
                if (!IPAddress.TryParse(value, out var client)) throw new InvalidOperationException("Invalid request address.");
                return client.ToString();
            }
            return remote?.ToString() ?? "unknown";
        }

        private static void Headers(HttpContext ctx, string? script = null)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'"
                + (script != null ? "; connect-src 'self'; script-src 'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'" : "");
        }
        private static string E(string value) => WebUtility.HtmlEncode(value);
        public static string RequestPage(string signupUrl, string shareToken, string? error = null) => Page("Request an account",
            "<p>Enter your Discord username. Ernie reviews requests from members who can access #vibecoders.</p>"
            + (error == null ? "" : "<p role=\"alert\">" + E(error) + "</p>")
            + "<form id=\"request\" method=\"post\" action=\"" + E(signupUrl + "/request") + "\"><label for=\"username\">Discord username</label>"
            + "<input id=\"username\" name=\"username\" required maxlength=\"33\" autocomplete=\"username\" autocapitalize=\"none\" spellcheck=\"false\" placeholder=\"your.username\">"
            + "<p>Use your unique username, not your display name or server nickname.</p>"
            + "<input type=\"hidden\" name=\"share\" value=\"" + E(shareToken) + "\"><button disabled>Request account</button></form><p id=\"status\" role=\"status\"></p>"
            + "<noscript>Enable JavaScript to request an account.</noscript><script>" + RequestScript + "</script>");
        public static string ClaimPage() => Page("Log in to Vibecoders",
            "<p>Create your account or sign in to your existing account.</p><p>This grants access to Vibecoders AI Generation.</p>"
            + "<p>Keep the original DM link for future logins. It does not expire.</p>"
            + "<form id=\"claim\"><button disabled>Continue to Vibecoders</button></form><p id=\"status\" role=\"status\"></p>"
            + "<noscript>Enable JavaScript to use this account link.</noscript><script>" + ClaimScript + "</script>");
        private static string Page(string title, string body) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"referrer\" content=\"no-referrer\">"
            + "<title>" + E(title) + "</title><style>body{font:16px/1.5 system-ui;margin:8vh auto;padding:24px;max-width:520px;color:#182034;background:#f7f8fc}"
            + "h1{font-size:26px}label{display:block;font-weight:600}input{box-sizing:border-box;width:100%;padding:12px;font:inherit;border:1px solid #52659a;border-radius:6px}"
            + "button{font:inherit;padding:12px 18px;background:#3548c8;color:white;border:0;border-radius:6px;cursor:pointer}button:disabled{opacity:.6;cursor:default}"
            + "a{color:#3548c8}[role=alert]{color:#a32020}</style></head><body><h1>" + E(title) + "</h1>" + body + "</body></html>";
        public const string RequestScript = """
            (() => {
              const form = document.getElementById("request"), button = form.querySelector("button"), status = document.getElementById("status");
              button.disabled = false;
              form.addEventListener("submit", async event => {
                event.preventDefault(); button.disabled = true; status.textContent = "Submitting your request…";
                try {
                  const response = await fetch(form.action, { method: "POST", mode: "cors", cache: "no-store",
                    headers: { "X-Mic-Account": "1" }, body: new URLSearchParams(new FormData(form)) });
                  if (!response.headers.get("content-type")?.includes("application/json")) throw new Error("The server could not process this request. Try again later.");
                  const result = await response.json();
                  if (!response.ok) throw new Error(result.error || "The account request failed.");
                  status.textContent = result.message;
                } catch (error) { status.textContent = error.message; }
                finally { button.disabled = false; }
              });
            })();
            """;
        public const string ClaimScript = """
            (() => {
              const token = location.hash.slice(1);
              history.replaceState(null, "", location.pathname);
              const form = document.getElementById("claim"), button = form.querySelector("button"), status = document.getElementById("status");
              if (!/^[a-f0-9]{32}\.[A-Za-z0-9_-]{43}$/.test(token)) { status.textContent = "This account link is incomplete. Open the link from your Discord DM."; return; }
              button.disabled = false;
              form.addEventListener("submit", async event => {
                event.preventDefault(); button.disabled = true; status.textContent = "Checking your account…";
                try {
                  const response = await fetch("claim", { method: "POST", body: new URLSearchParams({ token }), cache: "no-store" });
                  const result = await response.json();
                  if (!response.ok) throw new Error(result.error || "The account request could not finish.");
                  location.replace(result.destination);
                } catch (error) { status.textContent = error.message; button.disabled = false; }
              });
            })();
            """;
    }
}
