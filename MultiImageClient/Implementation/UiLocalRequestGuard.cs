#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    // Local open mode (blank UiAuthFilePath) has no login, so any web page the
    // browser opens could otherwise drive this UI. DNS rebinding points an
    // attacker's domain at 127.0.0.1, after which its page reads and writes the
    // UI with that domain as Host; a cross-site form or fetch can also post
    // straight to 127.0.0.1:5960. Shared deployments are reached through nginx
    // under their public hostname and use the login gate instead, so UiWorkflow
    // registers this guard only when auth is off. See docs/local-ui-server.md.
    public static class UiLocalRequestGuard
    {
        private const int MaxLoggedRefusals = 20;
        private static int _loggedRefusals;

        public static bool IsLocalHost(string? host) =>
            !string.IsNullOrEmpty(host)
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "[::1]");

        // Null when the request may proceed; otherwise why it is refused.
        // Requests without Origin or Sec-Fetch-Site (curl, agent scripts) pass.
        public static string? Problem(HttpRequest request)
        {
            if (!IsLocalHost(request.Host.Host))
                return "host";
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
                return null;
            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0)
                return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLocalHost(uri.Host) ? null : "origin";
            return request.Headers["Sec-Fetch-Site"].ToString() == "cross-site" ? "cross-site" : null;
        }

        public static async Task Middleware(HttpContext ctx, RequestDelegate next)
        {
            var problem = Problem(ctx.Request);
            if (problem == null)
            {
                await next(ctx);
                return;
            }
            if (Interlocked.Increment(ref _loggedRefusals) <= MaxLoggedRefusals)
                Logger.Log($"UI local guard: refused {ctx.Request.Method} {Clip(ctx.Request.Path.Value)} ({problem}; host '{Clip(ctx.Request.Host.Value)}', origin '{Clip(ctx.Request.Headers.Origin.ToString())}').");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsJsonAsync(new { error = "This local UI only answers its own pages on localhost." });
        }

        private static string Clip(string? value) =>
            value == null ? "" : value.Length <= 120 ? value : value[..120];
    }
}
