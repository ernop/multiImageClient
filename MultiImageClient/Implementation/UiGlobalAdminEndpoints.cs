#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    internal static class UiGlobalAdminEndpoints
    {
        public static void Map(WebApplication app, Settings settings, UiAuth auth, UiEnvironmentRegistry registry, UiJobRunner runner)
        {
            bool Allowed(HttpContext ctx, bool write = false) => settings.UiEnvironmentController
                && (ctx.Items["micUser"] as string) == UiLoginLinks.OwnerLogin
                && (!write || ctx.Request.Headers["X-Mic-Manage"] == "1");
            string Url(string environment, string token) => new Uri(new Uri(settings.UiPublicBaseUrl),
                "/" + registry.Get(environment).Slug + "/enter.html#" + token).AbsoluteUri;
            app.MapGet("/api/control/state", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!Allowed(ctx)) return Results.StatusCode(403);
                var linked = auth.LoginLinks!.List();
                var statusPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(settings.UiEnvironmentRegistryPath))!, "provisioning.json");
                using var provisioning = File.Exists(statusPath) && new FileInfo(statusPath).Length < 65536
                    ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusPath)) : System.Text.Json.JsonDocument.Parse("{}");
                return Results.Json(new { environments = registry.Read().Environments,
                    provisioning = provisioning.RootElement.Clone(),
                    accounts = auth.ListAccountNames().Select(login => new { login, name = login,
                        role = login == UiLoginLinks.OwnerLogin ? "admin" : "normal", id = "", revoked = false })
                        .Concat(linked.Select(a => new { login = a.Login, name = a.DisplayName, role = "normal", id = a.Id, revoked = a.Revoked })) });
            });
            app.MapPost("/api/control/environment", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!Allowed(ctx, true)) return Results.StatusCode(403);
                if (ctx.Request.ContentLength is null or > 65536) return Results.BadRequest();
                var value = await ctx.Request.ReadFromJsonAsync<UiEnvironmentRegistry.Environment>();
                if (value == null) return Results.BadRequest();
                try
                {
                    if (ctx.Request.Query["create"] == "true" && registry.Read().Environments.Any(e => e.Id == value.Id))
                        return Results.BadRequest(new { error = "An environment with that URL name already exists." });
                    var known = auth.ListAccountNames().Concat(auth.LoginLinks!.List().Select(a => a.Login)).ToHashSet(StringComparer.Ordinal);
                    if (value.Members.Any(m => !known.Contains(m))) return Results.BadRequest(new { error = "Select existing accounts." });
                    if (value.DefaultGenerators?.Any(k => !UiJobRunner.IsConfigurableEndpointKey(k)) == true)
                        return Results.BadRequest(new { error = "Unknown provider default." });
                    registry.Save(value); return Results.Json(new { ok = true });
                }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapPost("/api/control/accounts", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!Allowed(ctx, true)) return Results.StatusCode(403);
                var form = await ctx.Request.ReadFormAsync();
                try
                {
                    var environment = registry.Get(form["environment"].ToString());
                    if (auth.ListAccountNames().Contains(form["name"].ToString(), StringComparer.OrdinalIgnoreCase))
                        return Results.BadRequest(new { error = "That name belongs to an existing account." });
                    var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                    var issued = auth.LoginLinks!.Create(form["name"].ToString(), _ => { }, UiAuth.NewPasswordHash(password));
                    environment.Members.Add(issued.Account.Login); registry.Save(environment);
                    return Results.Json(new { url = Url(environment.Id, issued.Token), username = issued.Account.Login, password });
                }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapPost("/api/control/accounts/{id}/{action}", async (string id, string action, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!Allowed(ctx, true)) return Results.StatusCode(403);
                try
                {
                    var account = auth.LoginLinks!.List().SingleOrDefault(a => a.Id == id);
                    if (account == null || account.Login == UiLoginLinks.OwnerLogin) return Results.BadRequest();
                    if (action == "revoke") { auth.LoginLinks.Revoke(id); return Results.Json(new { ok = true }); }
                    if (action != "replace") return Results.BadRequest();
                    var form = await ctx.Request.ReadFormAsync(); var environment = registry.Get(form["environment"].ToString());
                    if (!environment.Members.Contains(account.Login)) return Results.BadRequest(new { error = "Assign membership before creating a link." });
                    return Results.Json(new { url = Url(environment.Id, auth.LoginLinks.Replace(id)) });
                }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });
        }
    }
}
