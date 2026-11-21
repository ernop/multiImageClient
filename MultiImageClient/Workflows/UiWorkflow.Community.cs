#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;


namespace MultiImageClient
{
    public partial class UiWorkflow
    {
        private static void MapCommunityRoutes(
            WebApplication app,
            Settings settings,
            UiAuth? auth,
            UiJobRegistry jobs,
            UiJobRunner runner,
            UiVisibilityStore visibility,
            UiFavoriteStore favorites,
            UiCommunityStore community,
            UiDiscordVibecodersStore vibecoders,
            UiEnvironmentRegistry? environments,
            UiAccountActivity? accountActivity)
        {
            app.MapGet("/api/discord/vibecoders", (string? version, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (!DiscordVibecoders.IsConfigured(settings))
                {
                    return Results.Json(new { available = false, version = "", items = Array.Empty<object>() });
                }
                var snapshot = vibecoders.Snapshot();
                if (string.Equals(version, snapshot.Version, StringComparison.Ordinal))
                {
                    return Results.Json(new { available = true, version = snapshot.Version, unchanged = true });
                }
                return Results.Json(new
                {
                    available = true,
                    version = snapshot.Version,
                    items = snapshot.Records.Select(record => new
                    {
                        record.Kind,
                        state = record.State,
                        jobId = record.JobId,
                        generator = record.Generator,
                        imageIndex = record.ImageIndex,
                    }),
                });
            });

            MapPublicShares(app, settings, auth, jobs, visibility, runner, vibecoders, environments, community: community);
            MapShareLinks(app, settings, auth, jobs, visibility, environments, accountActivity);
            UiDiscordAccountEndpoints.Map(app, settings, auth, environments, accountActivity);

            // Shared persistent image + prompt favorites. POST takes the
            // desired boolean state instead of "toggle", so a retried request
            // is idempotent. New records are accepted only after correlating
            // their exact identity to a persisted job/result. Removing an
            // existing record needs only its stored identity, so a user can
            // still clean it up after old job files are removed.
            app.MapGet("/api/favorites", (string? version, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                try
                {
                    var snapshot = favorites.Snapshot();
                    var visibilitySnapshot = visibility.Snapshot();
                    var profileSnapshot = community.SnapshotProfiles();
                    var combinedVersion = snapshot.Version
                        + "." + visibilitySnapshot.Version
                        + "." + profileSnapshot.Version;
                    if (string.Equals(version, combinedVersion, StringComparison.Ordinal))
                    {
                        return Results.Json(new { version = combinedVersion, unchanged = true });
                    }
                    var visibleRecords = snapshot.Records
                        .Where(record =>
                            !visibility.IsPromptHidden(record.JobId)
                            && (!string.Equals(record.Kind, "image", StringComparison.Ordinal)
                                || !visibility.IsImageHidden(
                                    record.JobId,
                                    record.Generator,
                                    record.ImageIndex)))
                        .ToList();
                    return Results.Json(BuildFavoritesResponse(
                        combinedVersion,
                        visibleRecords,
                        jobs,
                        ctx.Items["micUser"] as string ?? "",
                        profileSnapshot,
                        auth != null));
                }
                catch (InvalidDataException ex)
                {
                    Logger.Log($"UI favorites: listing failed: {ex.Message}");
                    return Results.Json(
                        new { error = "Favorites contain conflicting stored image identities." },
                        statusCode: 500);
                }
            });

            app.MapPost("/api/favorites", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var favoriteLogin = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    favoriteLogin,
                    community,
                    out var user,
                    out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }

                var jobId = form["jobId"].ToString().Trim();
                var kind = form["kind"].ToString().Trim();
                if (kind.Length == 0)
                {
                    kind = "image";
                }
                if (kind != "image" && kind != "prompt")
                {
                    return Results.BadRequest(new { error = "kind must be image or prompt." });
                }
                if (jobId.Length == 0)
                {
                    return Results.BadRequest(new { error = "jobId is required." });
                }
                if (visibility.IsPromptHidden(jobId))
                {
                    return Results.NotFound(new { error = "The selected job is hidden." });
                }
                if (!bool.TryParse(form["favorite"].ToString(), out var favorite))
                {
                    return Results.BadRequest(new { error = "favorite must be true or false." });
                }

                var generator = "";
                var imageIndex = -1;
                if (kind == "image")
                {
                    generator = form["generator"].ToString().Trim();
                    if (generator.Length == 0)
                    {
                        return Results.BadRequest(new { error = "generator is required for an image favorite." });
                    }
                    if (!int.TryParse(form["imageIndex"].ToString(), out imageIndex)
                        || imageIndex < 0)
                    {
                        return Results.BadRequest(
                            new { error = "imageIndex must be a non-negative integer for an image favorite." });
                    }
                    if (visibility.IsImageHidden(jobId, generator, imageIndex))
                    {
                        return Results.NotFound(new { error = "The selected image is hidden." });
                    }
                }

                try
                {
                    var records = kind == "image"
                        ? favorites.ListImage(jobId, generator, imageIndex)
                        : favorites.ListPrompt(jobId);
                    var existing = records
                        .FirstOrDefault(record =>
                            favoriteLogin.Length > 0
                                ? string.Equals(
                                    record.UserLogin,
                                    favoriteLogin,
                                    StringComparison.OrdinalIgnoreCase)
                                : record.UserLogin.Length == 0
                                    && string.Equals(record.User, user, StringComparison.Ordinal));
                    var addedFavorite = false;
                    UiJob? favoritedJob = null;

                    if (favorite && existing == null)
                    {
                        var job = jobs.Get(jobId);
                        if (job == null)
                        {
                            return Results.NotFound(new { error = "The selected job is no longer available." });
                        }
                        if (kind == "prompt")
                        {
                            existing = new UiFavoriteRecord
                            {
                                Kind = "prompt",
                                User = user,
                                UserLogin = favoriteLogin,
                                JobId = job.Id,
                                Prompt = job.Prompt,
                                CreatedBy = job.CreatedBy,
                                JobCreatedAtUnixMs = new DateTimeOffset(job.CreatedAt).ToUnixTimeMilliseconds(),
                                HasInputImage = job.HasInputImage,
                                FavoritedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        }
                        else
                        {
                            if (!TryResolveFavoriteImage(
                                    job,
                                    generator,
                                    imageIndex,
                                    out var image,
                                    out var imageError))
                            {
                                return Results.NotFound(new { error = imageError });
                            }
                            existing = new UiFavoriteRecord
                            {
                                Kind = "image",
                                User = user,
                                UserLogin = favoriteLogin,
                                JobId = job.Id,
                                Generator = generator,
                                ImageIndex = imageIndex,
                                GeneratorImageCount = image.GeneratorImageCount,
                                Prompt = job.Prompt,
                                CreatedBy = job.CreatedBy,
                                JobCreatedAtUnixMs = new DateTimeOffset(job.CreatedAt).ToUnixTimeMilliseconds(),
                                HasInputImage = job.HasInputImage,
                                ImageUrl = image.ImageUrl,
                                ThumbUrl = image.ThumbUrl,
                                Size = image.Size,
                                FavoritedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        }
                        favoritedJob = job;
                        addedFavorite = true;
                    }

                    if (existing != null)
                    {
                        favorites.Set(existing, favorite);
                        if (addedFavorite && favoritedJob != null)
                        {
                            try
                            {
                                community.RecordFavorite(
                                    request.HttpContext.Items["micUser"] as string ?? "",
                                    user,
                                    favoritedJob.CreatorLogin,
                                    favoritedJob.CreatedBy,
                                    favoritedJob.Id,
                                    kind,
                                    generator,
                                    imageIndex,
                                    existing.FavoritedAtUnixMs);
                            }
                            catch (Exception ex)
                            {
                                // The favorite itself is already durable and
                                // remains the source of truth. A notification
                                // write failure must not make a retry produce
                                // a false favorite failure or duplicate edge.
                                Logger.Log(
                                    $"UI activity: could not record favorite {user}/{kind}/{jobId}: {ex.Message}");
                            }
                        }
                    }

                    records = kind == "image"
                        ? favorites.ListImage(jobId, generator, imageIndex)
                        : favorites.ListPrompt(jobId);
                    var item = records.Count == 0
                        ? null
                        : kind == "image"
                            ? BuildFavoriteImageItem(
                                records,
                                CanManageVisibility(jobs.Get(jobId), request.HttpContext.Items["micUser"] as string ?? "", auth != null),
                                jobs.Get(jobId),
                                community.SnapshotProfiles())
                            : BuildFavoritePromptItem(
                                records,
                                CanManageVisibility(jobs.Get(jobId), request.HttpContext.Items["micUser"] as string ?? "", auth != null),
                                jobs.Get(jobId),
                                community.SnapshotProfiles());
                    return Results.Json(new { favorite, kind, item });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    Logger.Log(
                        $"UI favorites: could not persist {user}/{kind}/{jobId}/{generator}/{imageIndex}: "
                        + ex.Message);
                    return Results.Json(
                        new { error = "The favorite could not be persisted." },
                        statusCode: 500);
                }
            });

            // Shared social activity uses a bounded SQLite log and short
            // polling. Omitting `after` initializes a browser at the current
            // tail without replaying old alerts; subsequent polls use the
            // returned exact cursor.
            app.MapGet("/api/activity/poll", (long? after, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var result = community.ReadActivityAfter(after);
                var authUser = ctx.Items["micUser"] as string ?? "";
                var isDeveloper = IsDeveloperLogin(authUser);
                var profileSnapshot = community.SnapshotProfiles();
                var entries = result.Records
                    .Where(record =>
                        string.Equals(record.Audience, "all", StringComparison.Ordinal)
                        || (isDeveloper
                            && string.Equals(record.Audience, "developer", StringComparison.Ordinal)))
                    .Where(record =>
                        record.JobId.Length == 0
                        || (!visibility.IsPromptHidden(record.JobId)
                            && (record.ImageIndex < 0
                                || !visibility.IsImageHidden(
                                    record.JobId,
                                    record.Generator,
                                    record.ImageIndex))))
                    .Select(record => new
                    {
                        id = record.Id,
                        at = record.AtUnixMs,
                        kind = record.Kind,
                        actor = profileSnapshot.ResolveDisplay(
                            record.ActorLogin,
                            record.ActorDisplay),
                        target = profileSnapshot.ResolveDisplay(
                            record.TargetLogin,
                            record.TargetDisplay),
                        jobId = record.JobId,
                        generator = record.Generator,
                        imageIndex = record.ImageIndex,
                        resourceKind = record.ResourceKind,
                        isActor = authUser.Length > 0
                            && string.Equals(record.ActorLogin, authUser, StringComparison.Ordinal),
                        isTarget = authUser.Length > 0
                            && string.Equals(record.TargetLogin, authUser, StringComparison.Ordinal),
                    });
                return Results.Json(new
                {
                    cursor = result.Cursor,
                    reset = result.Reset,
                    entries,
                });
            });

            app.MapPost("/api/requests", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    request.HttpContext.Items["micUser"] as string ?? "",
                    community,
                    out var submitter,
                    out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }
                try
                {
                    var stored = community.SubmitRequest(
                        request.HttpContext.Items["micUser"] as string ?? "",
                        submitter,
                        form["body"].ToString(),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log($"UI request: stored {stored.Id} from {submitter}.");
                    return Results.Json(new
                    {
                        id = stored.Id,
                        sequence = stored.Sequence,
                        submittedAtUnixMs = stored.SubmittedAtUnixMs,
                    });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI request: persistence failed for {submitter}: {ex.Message}");
                    return Results.Json(
                        new { error = "The request could not be stored." },
                        statusCode: 500);
                }
            });

            app.MapGet("/api/requests", (long? after, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var authUser = ctx.Items["micUser"] as string ?? "";
                if (!IsDeveloperLogin(authUser))
                {
                    return Results.Json(
                        new { error = "Developer access is required." },
                        statusCode: 403);
                }
                var result = community.ReadRequestsAfter(after ?? 0);
                var profiles = community.SnapshotProfiles();
                return Results.Json(new
                {
                    cursor = result.Cursor,
                    reset = result.Reset,
                    requests = result.Records.Select(record => new
                    {
                        sequence = record.Sequence,
                        id = record.Id,
                        submittedAtUnixMs = record.SubmittedAtUnixMs,
                        submitterLogin = record.SubmitterLogin,
                        submitter = profiles.ResolveDisplay(
                            record.SubmitterLogin,
                            record.SubmitterDisplay),
                        body = record.Body,
                    }),
                });
            });

            // Same zero-persistent-connection rule as /api/events/poll: the
            // logs panel used to hold an SSE connection per window, which
            // counted against the same 6-connection browser pool.
            app.MapGet("/api/logs/poll", (long? after, HttpContext ctx) =>
            {
                var buffered = Logger.ReadBuffered(after ?? 0).ToList();
                var next = buffered.Count > 0
                    ? buffered[^1].Sequence
                    : after ?? 0;
                var hiddenPromptIds = visibility.Snapshot().Records
                    .Where(record => string.Equals(record.Kind, "prompt", StringComparison.Ordinal))
                    .Select(record => record.JobId)
                    .ToList();
                var entries = buffered
                    .Where(entry => !hiddenPromptIds.Any(jobId =>
                        entry.Line.Contains($"[ui #{jobId}]", StringComparison.Ordinal)))
                    .Select(entry => new { sequence = entry.Sequence, line = entry.Line });
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { entries, next });
            });
        }

        private sealed class FavoriteImageDescriptor
        {
            public required string ImageUrl { get; init; }
            public required string ThumbUrl { get; init; }
            public string Size { get; init; } = "";
            public int GeneratorImageCount { get; init; }
        }

        private sealed class SendMediaDescriptor
        {
            public required string Kind { get; init; }
            public required string Url { get; init; }
            public required string ContentType { get; init; }
        }

        private static bool TryResolveSendMedia(
            UiJob job,
            string generator,
            int imageIndex,
            out SendMediaDescriptor media,
            out string error)
        {
            SendMediaDescriptor? found = null;
            var (events, _) = job.ReadFrom(0);
            foreach (var eventJson in events)
            {
                using var document = JsonDocument.Parse(eventJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "gen-result", StringComparison.Ordinal)
                    || !root.TryGetProperty("gen", out var gen)
                    || !string.Equals(gen.GetString(), generator, StringComparison.Ordinal)
                    || !root.TryGetProperty("ok", out var ok)
                    || ok.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if (root.TryGetProperty("resultKind", out var resultKind)
                    && string.Equals(resultKind.GetString(), "text", StringComparison.Ordinal))
                {
                    error = "Describe results cannot be sent to vibecoders.";
                    media = null!;
                    return false;
                }

                if (!root.TryGetProperty("images", out var images)
                    || images.ValueKind != JsonValueKind.Array
                    || imageIndex >= images.GetArrayLength())
                {
                    continue;
                }

                var url = images[imageIndex].GetString() ?? "";
                if (url.Length == 0)
                {
                    error = "The selected result has no recorded URL.";
                    media = null!;
                    return false;
                }

                var isVideo = root.TryGetProperty("mediaType", out var mediaType)
                    && (mediaType.GetString() ?? "").StartsWith("video/", StringComparison.OrdinalIgnoreCase);
                found = new SendMediaDescriptor
                {
                    Kind = isVideo ? "video" : "image",
                    Url = url,
                    ContentType = isVideo
                        ? (mediaType.GetString() ?? "video/mp4")
                        : "application/octet-stream",
                };
            }

            if (found == null)
            {
                error = "Only a successful image or video result can be sent.";
                media = null!;
                return false;
            }

            media = found;
            error = "";
            return true;
        }

        private static bool TryResolveFavoriteImage(
            UiJob job,
            string generator,
            int imageIndex,
            out FavoriteImageDescriptor image,
            out string error)
        {
            FavoriteImageDescriptor? found = null;
            var (events, _) = job.ReadFrom(0);
            foreach (var eventJson in events)
            {
                using var document = JsonDocument.Parse(eventJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "gen-result", StringComparison.Ordinal)
                    || !root.TryGetProperty("gen", out var gen)
                    || !string.Equals(gen.GetString(), generator, StringComparison.Ordinal)
                    || !root.TryGetProperty("ok", out var ok)
                    || ok.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if ((root.TryGetProperty("resultKind", out var resultKind)
                        && !string.Equals(resultKind.GetString(), "image", StringComparison.Ordinal))
                    || (root.TryGetProperty("mediaType", out var mediaType)
                        && (mediaType.GetString() ?? "").StartsWith(
                            "video/",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Only successful image results can be favorited.";
                    image = null!;
                    return false;
                }
                if (!root.TryGetProperty("images", out var images)
                    || images.ValueKind != JsonValueKind.Array
                    || imageIndex >= images.GetArrayLength())
                {
                    continue;
                }

                var imageUrl = images[imageIndex].GetString() ?? "";
                if (imageUrl.Length == 0)
                {
                    error = "The selected image result has no recorded URL.";
                    image = null!;
                    return false;
                }

                var thumbUrl = "";
                if (root.TryGetProperty("thumbs", out var thumbs)
                    && thumbs.ValueKind == JsonValueKind.Array
                    && imageIndex < thumbs.GetArrayLength())
                {
                    thumbUrl = thumbs[imageIndex].GetString() ?? "";
                }
                if (thumbUrl.Length == 0 && imageUrl.StartsWith("/", StringComparison.Ordinal))
                {
                    thumbUrl = imageUrl + (imageUrl.Contains('?') ? "&thumb=1" : "?thumb=1");
                }
                if (thumbUrl.Length == 0)
                {
                    error = "The selected hosted image has no exact recorded card thumbnail.";
                    image = null!;
                    return false;
                }

                var candidate = new FavoriteImageDescriptor
                {
                    ImageUrl = imageUrl,
                    ThumbUrl = thumbUrl,
                    Size = root.TryGetProperty("size", out var size) ? size.GetString() ?? "" : "",
                    GeneratorImageCount = images.GetArrayLength(),
                };
                if (found != null)
                {
                    error = "The selected image identity has more than one successful result event.";
                    image = null!;
                    return false;
                }
                found = candidate;
            }

            if (found == null)
            {
                error = "The selected image result is no longer available.";
                image = null!;
                return false;
            }
            image = found;
            error = "";
            return true;
        }

        private static object BuildFavoritesResponse(
            string version,
            List<UiFavoriteRecord> records,
            UiJobRegistry jobs,
            string authUser,
            UiProfileSnapshot profiles,
            bool authenticationEnabled)
        {
            var imageItems = records
                .Where(record => record.Kind == "image")
                .GroupBy(
                    record => (record.JobId, record.Generator, record.ImageIndex),
                    record => record)
                .Select(group => BuildFavoriteImageItem(
                    group.ToList(),
                    CanManageVisibility(jobs.Get(group.Key.JobId), authUser, authenticationEnabled),
                    jobs.Get(group.Key.JobId),
                    profiles))
                .ToList();
            var promptItems = records
                .Where(record => record.Kind == "prompt")
                .GroupBy(record => record.JobId, StringComparer.Ordinal)
                .Select(group => BuildFavoritePromptItem(
                    group.ToList(),
                    CanManageVisibility(jobs.Get(group.Key), authUser, authenticationEnabled),
                    jobs.Get(group.Key),
                    profiles))
                .ToList();
            var users = records
                .GroupBy(
                    record => profiles.ResolveDisplay(record.UserLogin, record.User),
                    StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    user = group.Key,
                    count = group.Count(),
                    imageCount = group.Count(record => record.Kind == "image"),
                    promptCount = group.Count(record => record.Kind == "prompt"),
                })
                .ToList();
            return new
            {
                version,
                unchanged = false,
                favorites = imageItems,
                promptFavorites = promptItems,
                users,
            };
        }

        private static object BuildFavoriteImageItem(
            List<UiFavoriteRecord> records,
            bool canHide,
            UiJob? job = null,
            UiProfileSnapshot? profiles = null)
        {
            if (records.Count == 0)
            {
                throw new InvalidDataException("Cannot build an empty favorite item.");
            }
            var first = records[0];
            if (records.Any(record =>
                record.Kind != "image"
                || !string.Equals(record.JobId, first.JobId, StringComparison.Ordinal)
                || !string.Equals(record.Generator, first.Generator, StringComparison.Ordinal)
                || record.ImageIndex != first.ImageIndex
                || record.GeneratorImageCount != first.GeneratorImageCount
                || !string.Equals(record.Prompt, first.Prompt, StringComparison.Ordinal)
                || !string.Equals(record.CreatedBy, first.CreatedBy, StringComparison.Ordinal)
                || record.JobCreatedAtUnixMs != first.JobCreatedAtUnixMs
                || record.HasInputImage != first.HasInputImage
                || !string.Equals(record.ImageUrl, first.ImageUrl, StringComparison.Ordinal)
                || !string.Equals(record.ThumbUrl, first.ThumbUrl, StringComparison.Ordinal)
                || !string.Equals(record.Size, first.Size, StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Favorite records disagree for {first.JobId}/{first.Generator}/{first.ImageIndex}.");
            }

            return new
            {
                jobId = first.JobId,
                generator = first.Generator,
                imageIndex = first.ImageIndex,
                generatorImageCount = first.GeneratorImageCount,
                prompt = first.Prompt,
                createdBy = job != null && profiles != null
                    ? profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy)
                    : first.CreatedBy,
                ownerId = job == null
                    ? ""
                    : UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                jobCreatedAtUnixMs = first.JobCreatedAtUnixMs,
                hasInputImage = first.HasInputImage,
                imageUrl = first.ImageUrl,
                thumbUrl = first.ThumbUrl,
                size = first.Size,
                canHide,
                users = records
                    .Select(record => profiles?.ResolveDisplay(
                        record.UserLogin,
                        record.User) ?? record.User)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private static object BuildFavoritePromptItem(
            List<UiFavoriteRecord> records,
            bool canHide,
            UiJob? job = null,
            UiProfileSnapshot? profiles = null)
        {
            if (records.Count == 0)
            {
                throw new InvalidDataException("Cannot build an empty prompt favorite item.");
            }
            var first = records[0];
            if (records.Any(record =>
                record.Kind != "prompt"
                || !string.Equals(record.JobId, first.JobId, StringComparison.Ordinal)
                || !string.Equals(record.Prompt, first.Prompt, StringComparison.Ordinal)
                || !string.Equals(record.CreatedBy, first.CreatedBy, StringComparison.Ordinal)
                || record.JobCreatedAtUnixMs != first.JobCreatedAtUnixMs
                || record.HasInputImage != first.HasInputImage))
            {
                throw new InvalidDataException(
                    $"Prompt favorite records disagree for job {first.JobId}.");
            }

            return new
            {
                jobId = first.JobId,
                prompt = first.Prompt,
                createdBy = job != null && profiles != null
                    ? profiles.ResolveDisplay(job.CreatorLogin, job.CreatedBy)
                    : first.CreatedBy,
                ownerId = job == null
                    ? ""
                    : UiCommunityStore.PublicIdentityId(job.CreatorLogin),
                jobCreatedAtUnixMs = first.JobCreatedAtUnixMs,
                hasInputImage = first.HasInputImage,
                canHide,
                users = records
                    .Select(record => profiles?.ResolveDisplay(
                        record.UserLogin,
                        record.User) ?? record.User)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

    }
}
