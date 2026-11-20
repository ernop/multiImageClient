#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        public static bool IsPromptArchiveAdmin(HttpContext ctx, UiAuth? auth)
        {
            if (auth == null || !auth.IsEnforced)
                return true;
            return string.Equals(ctx.Items["micUser"] as string, UiLoginLinks.OwnerLogin, StringComparison.Ordinal);
        }

        internal static void MapPromptArchiveRoutes(
            WebApplication app,
            Settings settings,
            UiAuth? auth,
            UiPromptArchive archive)
        {
            app.MapGet("/api/admin/prompt-archive/stats", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(403);
                try
                {
                    return Results.Json(archive.GetStats());
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI prompt archive stats error: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            app.MapGet("/api/admin/prompt-archive/random", (
                HttpContext ctx,
                int? count,
                string? q,
                string? source,
                string? year,
                string? kind,
                int? minChars,
                int? maxChars,
                string? exclude) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(403);
                try
                {
                    var parsedYear = ParseOptionalYear(year);
                    var excludeIds = ParseOptionalExcludeIds(exclude);
                    var filter = new UiPromptArchiveFilter(
                        Query: q,
                        Source: source,
                        Year: parsedYear,
                        Kind: string.IsNullOrWhiteSpace(kind) ? "image" : kind,
                        MinChars: minChars,
                        MaxChars: maxChars);
                    var result = archive.GetRandom(filter, count ?? 10, excludeIds);
                    return Results.Json(result);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI prompt archive random error: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            app.MapGet("/api/admin/prompt-archive/search", (
                HttpContext ctx,
                string? q,
                string? source,
                string? year,
                string? kind,
                int? minChars,
                int? maxChars,
                string? sort,
                int? page,
                int? pageSize) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(403);
                try
                {
                    var parsedYear = ParseOptionalYear(year);
                    var filter = new UiPromptArchiveFilter(
                        Query: q,
                        Source: source,
                        Year: parsedYear,
                        Kind: string.IsNullOrWhiteSpace(kind) ? "image" : kind,
                        MinChars: minChars,
                        MaxChars: maxChars);
                    var defaultSort = !string.IsNullOrWhiteSpace(q) ? "relevance" : "newest";
                    var result = archive.Search(
                        filter,
                        sort: string.IsNullOrWhiteSpace(sort) ? defaultSort : sort,
                        page: page ?? 1,
                        pageSize: pageSize ?? 25);
                    return Results.Json(result);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI prompt archive search error: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            app.MapGet("/api/admin/prompt-archive/items/{id}", (HttpContext ctx, string id) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(403);
                if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) || parsedId <= 0)
                    return Results.BadRequest(new { error = "Prompt id must be a positive integer." });
                try
                {
                    var detail = archive.GetItem(parsedId);
                    if (detail == null)
                        return Results.NotFound(new { error = "Archived prompt not found." });
                    return Results.Json(detail);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI prompt archive item error: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });

            app.MapPost("/api/admin/prompt-archive/sync", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!IsPromptArchiveAdmin(ctx, auth))
                    return Results.StatusCode(403);
                if (ctx.Request.Headers["X-Mic-Manage"] != "1")
                    return Results.StatusCode(403);
                try
                {
                    var allowEmptyCreator = auth == null || !auth.IsEnforced
                        || settings.UiEnvironmentController
                        || string.Equals(settings.UiEnvironmentId, "original", StringComparison.Ordinal);
                    var synced = archive.SyncFromInstance(settings.ImageDownloadBaseFolder, allowEmptyCreator);
                    return Results.Json(new
                    {
                        ok = true,
                        synced.AddedPrompts,
                        synced.AddedOccurrences,
                        synced.TotalPrompts,
                        synced.TotalOccurrences,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI prompt archive sync error: {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 500);
                }
            });
        }

        private static int? ParseOptionalYear(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim().ToLowerInvariant();
            if (trimmed == "undated" || trimmed == "0") return 0;
            if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new InvalidDataException($"Invalid year '{raw}'.");
            return parsed;
        }

        private static HashSet<long>? ParseOptionalExcludeIds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var result = new HashSet<long>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 200)
                throw new InvalidDataException("exclude supports at most 200 ids.");
            foreach (var part in parts)
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    throw new InvalidDataException($"Invalid exclude id '{part}'.");
                result.Add(id);
            }
            return result;
        }
    }
}
