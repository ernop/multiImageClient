#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    internal static class UiEnvironmentEndpoints
    {
        public static string Identity(HttpContext ctx, UiAuth auth) => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Cookies[auth.SessionCookieName] ?? "")))[..32];

        public static void Map(WebApplication app, Settings settings, UiAuth? auth, UiCommunityStore community,
            UiJobRunner runner, string wwwroot, UiEnvironmentRegistry? environments = null, UiAccountActivity? activity = null)
        {
            app.MapGet("/environment.js", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (settings.UiEnvironmentId.Length == 0 || auth == null) return Results.Text("", "text/javascript");
                var user = ctx.Items["micUser"] as string ?? "";
                var policy = environments?.Get(settings.UiEnvironmentId);
                var original = environments?.Read().Environments.Single(e => e.Original);
                var config = JsonSerializer.Serialize(new { id = settings.UiEnvironmentId, name = policy?.Name ?? settings.UiEnvironmentName,
                    role = user == UiLoginLinks.OwnerLogin ? "admin" : "normal",
                    legacyStorage = policy?.Original ?? false,
                    adminUrl = original == null ? "people.html" : "/" + original.Slug + "/admin.html",
                    goalLoops = policy?.GoalLoops ?? true, video = policy?.Video ?? true, promptRewrite = policy?.PromptRewrite ?? true,
                    user, identity = Identity(ctx, auth) });
                return Results.Text("window.MicEnvironment=" + config + ";\n"
                    + File.ReadAllText(Path.Combine(wwwroot, "environment-storage.js")), "text/javascript");
            });
            if (auth?.LoginLinks == null) return;
            var links = auth.LoginLinks;
            app.MapGet("/enter.html", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Results.Text(EntryPage, "text/html");
            });
            app.MapPost("/api/auth/link", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (ctx.Request.ContentLength > 256) return Results.BadRequest(new { error = "Invalid login link." });
                var form = await ctx.Request.ReadFormAsync();
                if (!auth.TryLoginLink(form["token"].ToString(), ClientIp(ctx), out var cookie, out var account, out var error))
                    return Results.Json(new { error }, statusCode: 401);
                if (environments != null && !environments.CanEnter(settings.UiEnvironmentId, account!.Login))
                    return Results.Json(new { error = "This account does not belong to this environment." }, statusCode: 403);
                // Preserve later profile renames. First entry initializes the bootstrap owner's display name.
                if (!community.SnapshotProfiles().Profiles.Any(p => p.Login == account!.Login))
                    community.SetProfileName(account!.Login, account.DisplayName, Array.Empty<string>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                activity?.Record(account!.Login, true);
                ctx.Response.Cookies.Append(auth.SessionCookieName, cookie, CookieOptions(auth));
                return Results.Json(new { ok = true });
            });

            if (environments != null)
            {
                UiGlobalAdminEndpoints.Map(app, settings, auth, environments, runner);
                app.MapGet("/people.html", () => Results.Redirect("/" + environments.Read().Environments.Single(e => e.Original).Slug + "/admin.html"));
                return;
            }
            app.MapGet("/api/people", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsOwner(ctx)) return Results.StatusCode(403);
                var profiles = community.SnapshotProfiles().Profiles;
                return Results.Json(new { name = settings.UiEnvironmentName,
                    accounts = links.List().Select(a => new { a.Id, a.Login,
                        displayName = profiles.FirstOrDefault(p => p.Login == a.Login)?.DisplayName ?? a.DisplayName,
                        a.Revoked, owner = a.Login == UiLoginLinks.OwnerLogin }), defaults = links.Defaults() });
            });
            app.MapPost("/api/people", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!CanManage(ctx)) return Results.StatusCode(403);
                var form = await ctx.Request.ReadFormAsync();
                try
                {
                    var issued = links.Create(form["name"].ToString(), a => community.SetProfileName(
                        a.Login, a.DisplayName, Array.Empty<string>(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    return Results.Json(new { id = issued.Account.Id, url = Link(settings, issued.Token) });
                }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
                catch (UiProfileNameConflictException) { return Results.BadRequest(new { error = "That display name is already in use." }); }
            });
            app.MapPost("/api/people/{id}/replace", (string id, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!CanManage(ctx)) return Results.StatusCode(403);
                try { return Results.Json(new { url = Link(settings, links.Replace(id)) }); }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapPost("/api/people/{id}/revoke", (string id, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!CanManage(ctx)) return Results.StatusCode(403);
                try { links.Revoke(id); return Results.Json(new { ok = true }); }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapPost("/api/environment/defaults", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!CanManage(ctx)) return Results.StatusCode(403);
                var form = await ctx.Request.ReadFormAsync();
                var keys = form["keys"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (keys.Any(k => !runner.IsAvailable(k)))
                    return Results.BadRequest(new { error = "Select only available providers." });
                try { links.SetDefaults(keys); return Results.Json(new { ok = true }); }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
        }

        private static string Link(Settings settings, string token) => settings.UiPublicBaseUrl.TrimEnd('/') + "/enter.html#" + token;
        private static bool IsOwner(HttpContext ctx) => string.Equals(ctx.Items["micUser"] as string,
            UiLoginLinks.OwnerLogin, StringComparison.Ordinal);
        // Custom headers require a CORS preflight; this server does not permit cross-origin management.
        private static bool CanManage(HttpContext ctx) => IsOwner(ctx) && ctx.Request.Headers["X-Mic-Manage"] == "1";
        private static CookieOptions CookieOptions(UiAuth auth) => new()
        { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(3650), Path = auth.SessionCookiePath };
        private static string ClientIp(HttpContext ctx)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
            return ip != null && System.Net.IPAddress.IsLoopback(ip) && forwarded.Length > 0
                ? forwarded.Split(',')[0].Trim() : ip?.ToString() ?? "unknown";
        }

        private const string EntryPage = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer"><title>Log in</title>
<style>body{font:16px system-ui;background:#f5f2ea;color:#222;margin:12vh auto;padding:24px;max-width:440px}a{color:inherit}</style></head>
<body><h1>Log in</h1><p id="status" role="status">Signing in…</p><a href="./">Return to the site</a>
<script>
(async () => {
 const token = location.hash.slice(1);
 history.replaceState(null, "", location.pathname);
 const status = document.getElementById("status");
 if (!/^[a-f0-9]{32}\.[A-Za-z0-9_-]{43}$/.test(token)) { status.textContent = "This login link is incomplete."; return; }
 try {
  const response = await fetch("api/auth/link", {method:"POST",body:new URLSearchParams({token}),cache:"no-store"});
  const data = await response.json();
  if (!response.ok) { status.textContent = data.error || "This login link did not work."; return; }
  location.replace("./");
 } catch { status.textContent = "Could not reach the site. Open your original login link to try again."; }
})();
</script></body></html>
""";
    }
}
