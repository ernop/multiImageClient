#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        internal static void MapTelemetryRoutes(
            WebApplication app,
            UiAuth? auth,
            UiTelemetryStore telemetry)
        {
            app.MapPost("/api/telemetry", async (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (ctx.Request.ContentLength > UiTelemetryStore.MaxRequestBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                string raw;
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[4096];
                    int read;
                    while ((read = await ctx.Request.Body.ReadAsync(chunk, 0, chunk.Length, ctx.RequestAborted)) > 0)
                    {
                        if (buffer.Length + read > UiTelemetryStore.MaxRequestBytes)
                        {
                            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                        }
                        buffer.Write(chunk, 0, read);
                    }
                    raw = Encoding.UTF8.GetString(buffer.ToArray());
                }

                UiTelemetryBatchInput batch;
                try
                {
                    batch = UiTelemetryStore.ParseBatchJson(raw);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                var authUser = ctx.Items["micUser"] as string ?? "";
                var actor = auth == null || !auth.IsEnforced ? "local" : authUser;
                var role = auth == null || !auth.IsEnforced
                    ? "local"
                    : string.Equals(authUser, UiLoginLinks.OwnerLogin, StringComparison.Ordinal)
                        ? "admin"
                        : "normal";

                try
                {
                    var accepted = telemetry.RecordBatch(actor, role, batch);
                    return Results.Json(new { accepted });
                }
                catch (UiTelemetryRateLimitException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapGet("/api/admin/telemetry/summary", (HttpContext ctx, int? days) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Json(telemetry.BuildSummary(days ?? 14));
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
        }
    }
}
