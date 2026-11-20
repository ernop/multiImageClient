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
        private static void MapGoalLoopRoutes(
            WebApplication app,
            Settings settings,
            UiAuth? auth,
            UiJobRunner runner,
            UiGoalLoopRegistry goalLoops,
            UiGoalLoopRunner goalLoopRunner,
            UiCommunityStore community)
        {
            // ---- goal loops ----
            // Manager-driven iterative loops. Every mutation is recorded as an
            // append-only entry on disk; the API exposes the loop metadata, a
            // cursor-based entry read for live pages, and stop / resume /
            // fork controls. Renders are ordinary jobs on the main feed.
            bool CanControlGoalLoop(UiGoalLoop loop, string authUser)
                => auth == null
                    || IsDeveloperLogin(authUser)
                    || (authUser.Length > 0
                        && string.Equals(authUser, loop.CreatorLogin, StringComparison.OrdinalIgnoreCase));

            app.MapGet("/api/goal-loops", (HttpContext ctx) =>
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                var loops = goalLoops.ListSummaries()
                    .Take(300)
                    .Select(loop => SummarizeGoalLoop(loop, CanControlGoalLoop(loop, authUser)))
                    .ToList();
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { loops, runningCount = goalLoopRunner.RunningCount }, UiGoalLoopJson.Options);
            });

            app.MapGet("/api/goal-loops/{id}", (string id, HttpContext ctx) =>
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                var after = 0;
                if (int.TryParse(ctx.Request.Query["after"], out var parsedAfter) && parsedAfter >= 0)
                {
                    after = parsedAfter;
                }
                // Wire payloads (exact provider request/response copies) are
                // omitted unless ?wire=1: they were half of a large loop's
                // bytes and the page shows them only on demand.
                var includeWire = ctx.Request.Query["wire"] == "1";
                UiGoalLoop loop;
                List<UiGoalLoopEntry> entries;
                int total;
                lock (state.Lock)
                {
                    loop = state.Loop.Clone();
                    total = state.Entries.Count;
                    entries = after >= total
                        ? new List<UiGoalLoopEntry>()
                        : state.Entries.Skip(after).Select(e => includeWire ? e.Clone() : e.CloneWithoutWire()).ToList();
                }
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    loop,
                    running = state.IsRunning,
                    canControl = CanControlGoalLoop(loop, authUser),
                    total,
                    after,
                    entries,
                }, UiGoalLoopJson.Options);
            });

            // The exact provider request (image base64 replaced by
            // placeholders) and raw response of one entry, by exact index.
            app.MapGet("/api/goal-loops/{id}/entries/{index:int}/wire", (string id, int index, HttpContext ctx) =>
            {
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                string? wireRequest;
                string? wireResponse;
                lock (state.Lock)
                {
                    if (index < 0 || index >= state.Entries.Count)
                    {
                        return Results.NotFound(new { error = $"loop {id} has no entry {index}" });
                    }
                    var entry = state.Entries[index];
                    wireRequest = entry.WireRequest;
                    wireResponse = entry.WireResponse;
                }
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { id, index, wireRequest, wireResponse }, UiGoalLoopJson.Options);
            });

            app.MapPost("/api/goal-loops", async (HttpRequest request) =>
            {
                var diskProblem = DescribeDiskCapacityProblem(settings);
                if (diskProblem != null)
                {
                    return Results.Json(new { error = diskProblem }, statusCode: 503);
                }
                var form = await request.ReadFormAsync();
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                var goal = (form["goal"].ToString() ?? "").Trim();
                if (goal.Length == 0)
                {
                    return Results.BadRequest(new { error = "goal text is required" });
                }
                if (goal.Length > UiGoalLoopRunner.MaxGoalChars)
                {
                    return Results.BadRequest(new { error = $"goal text exceeds {UiGoalLoopRunner.MaxGoalChars} characters" });
                }
                if (!TryResolveCreatorName(form["user"].ToString(), authUser, community, out var createdBy, out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }
                // Protocol 5: one or more generators. "generators" may repeat
                // or be comma-separated; the older single "generator" field
                // still works. Order is the source-letter order (A, B, …).
                var generatorKeys = form["generators"].Concat(form["generator"])
                    .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList();
                if (generatorKeys.Count == 0)
                {
                    return Results.BadRequest(new { error = "pick at least one image generator" });
                }
                if (generatorKeys.Count > UiGoalLoopSources.MaxGenerators)
                {
                    return Results.BadRequest(new { error = $"pick at most {UiGoalLoopSources.MaxGenerators} image generators" });
                }
                var duplicate = generatorKeys.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (duplicate != null)
                {
                    return Results.BadRequest(new { error = $"generator '{duplicate.Key}' was listed more than once" });
                }
                foreach (var key in generatorKeys)
                {
                    var generatorProblem = UiGoalLoopRunner.GeneratorProblem(runner, key);
                    if (generatorProblem.Length > 0)
                    {
                        return Results.BadRequest(new { error = $"generator {key} not usable: {generatorProblem}" });
                    }
                }
                var generatorKey = generatorKeys[0];
                var managerKey = (form["manager"].ToString() ?? "").Trim();
                var manager = ManagerCatalog.Find(managerKey);
                if (manager == null)
                {
                    return Results.BadRequest(new { error = "pick exactly one manager model" });
                }
                var managerProblem = ManagerCatalog.DescribeAvailabilityProblem(manager, settings);
                if (managerProblem != null)
                {
                    return Results.BadRequest(new { error = $"manager not available: {managerProblem}" });
                }
                // Protocol 6: zero or more independent critics from the same
                // catalog. "critics" may repeat or be comma-separated. The
                // manager's own model is allowed (a clean instance); the same
                // critic model twice is not.
                var criticKeys = form["critics"]
                    .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList();
                if (criticKeys.Count > UiGoalLoopCritics.MaxCritics)
                {
                    return Results.BadRequest(new { error = $"pick at most {UiGoalLoopCritics.MaxCritics} critics" });
                }
                var duplicateCritic = criticKeys.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (duplicateCritic != null)
                {
                    return Results.BadRequest(new { error = $"critic '{duplicateCritic.Key}' was listed more than once" });
                }
                var critics = new List<UiGoalLoopCritic>();
                foreach (var key in criticKeys)
                {
                    var critic = ManagerCatalog.Find(key);
                    if (critic == null)
                    {
                        return Results.BadRequest(new { error = $"unknown critic model '{key}'" });
                    }
                    var criticProblem = ManagerCatalog.DescribeAvailabilityProblem(critic, settings);
                    if (criticProblem != null)
                    {
                        return Results.BadRequest(new { error = $"critic {critic.Label} not available: {criticProblem}" });
                    }
                    critics.Add(new UiGoalLoopCritic { Key = critic.Key, Label = critic.Label, Model = critic.Model, Index = critics.Count });
                }
                var maxTurns = UiGoalLoopRunner.DefaultMaxTurns;
                var maxTurnsText = form["maxTurns"].ToString();
                if (!string.IsNullOrWhiteSpace(maxTurnsText))
                {
                    if (!int.TryParse(maxTurnsText, out maxTurns))
                    {
                        return Results.BadRequest(new { error = "max turns must be a whole number" });
                    }
                    try
                    {
                        UiGoalLoopRunner.ValidateMaxTurns(maxTurns);
                    }
                    catch (InvalidDataException ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }
                var shape = NormalizeOption(form["shape"].ToString(), "auto");
                if (!UiShapeMapping.IsKnownShape(shape))
                {
                    return Results.BadRequest(new { error = $"Unknown output shape '{shape}'. Expected one of: {string.Join(", ", UiShapeMapping.Shapes)}." });
                }
                var detail = NormalizeOption(form["detail"].ToString(), "standard");
                if (detail is not ("standard" or "high" or "max"))
                {
                    return Results.BadRequest(new { error = $"Unknown detail tier '{detail}'. Expected standard, high, or max." });
                }
                var quality = NormalizeOption(form["quality"].ToString(), "high");
                if (quality is not ("low" or "medium" or "high" or "xhigh" or "max" or "auto"))
                {
                    return Results.BadRequest(new { error = $"Unknown quality '{quality}'. Expected low, medium, high, xhigh, max, or auto." });
                }
                var moderation = NormalizeOption(form["moderation"].ToString(), "low");
                if (moderation is not ("low" or "auto"))
                {
                    return Results.BadRequest(new { error = $"Unknown moderation '{moderation}'. Expected low or auto." });
                }
                var goalKind = NormalizeOption(form["goalKind"].ToString(), UiGoalLoopGoalKinds.Auto);
                if (!UiGoalLoopGoalKinds.IsOperatorValue(goalKind))
                {
                    return Results.BadRequest(new { error = $"Unknown goal kind '{goalKind}'. Expected auto, bounded, or open-ended." });
                }

                var maxCandidates = UiGoalLoopFanout.DefaultMaxCandidates;
                var maxCandidatesText = form["maxCandidates"].ToString();
                if (!string.IsNullOrWhiteSpace(maxCandidatesText)
                    && (!int.TryParse(maxCandidatesText, out maxCandidates) || maxCandidates < 1 || maxCandidates > UiGoalLoopFanout.MaxCandidatesCap))
                    return Results.BadRequest(new { error = $"max candidates must be between 1 and {UiGoalLoopFanout.MaxCandidatesCap}" });

                var samplePolicy = UiGoalLoopSampling.DefaultPolicy;
                var sampleText = form["samplesPerPrompt"].ToString();
                if (!string.IsNullOrWhiteSpace(sampleText) && (!int.TryParse(sampleText, out samplePolicy) || samplePolicy is not (0 or 1 or 2 or 4)))
                    return Results.BadRequest(new { error = "samples per prompt must be auto (0), 1, 2, or 4" });

                var loop = new UiGoalLoop
                {
                    Goal = goal,
                    GoalKind = goalKind,
                    GeneratorKey = generatorKey,
                    GeneratorLabel = GeneratorPresentation.UiDisplayName(generatorKey),
                    Generators = generatorKeys
                        .Select(key => new UiGoalLoopGenerator { Key = key, Label = GeneratorPresentation.UiDisplayName(key) })
                        .ToList(),
                    ManagerKey = manager.Key,
                    ManagerLabel = manager.Label,
                    ManagerModel = manager.Model,
                    Critics = critics.Count == 0 ? null : critics,
                    MaxTurns = maxTurns,
                    MaxCandidates = maxCandidates,
                    SamplesPerPrompt = samplePolicy,
                    Shape = shape,
                    Detail = detail,
                    Quality = quality,
                    Moderation = moderation,
                    CreatedBy = createdBy,
                    CreatorLogin = authUser,
                };
                try
                {
                    goalLoopRunner.Start(loop);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 503);
                }
                Logger.Log($"[goal #{loop.Id}] started by '{createdBy}': generators={string.Join(",", generatorKeys)} manager={manager.Model} critics={(criticKeys.Count == 0 ? "none" : string.Join(",", critics.Select(c => c.Model)))} maxTurns={maxTurns} goalKind={goalKind}");
                return Results.Json(new { id = loop.Id }, UiGoalLoopJson.Options);
            });

            // The all-turns contact sheet: built on request from the entries
            // on file (any signed-in viewer may build; it is a read of the
            // loop), served from the loop folder. The page appends the
            // loop's sheetEntryCount to the GET URL so a rebuild is a new URL.
            app.MapPost("/api/goal-loops/{id}/sheet", async (string id, HttpContext ctx) =>
            {
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                try
                {
                    await goalLoopRunner.BuildSheetAsync(state, ctx.RequestAborted);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 409);
                }
                UiGoalLoop loop;
                lock (state.Lock) loop = state.Loop.Clone();
                return Results.Json(new
                {
                    ok = true,
                    sheetEntryCount = loop.SheetEntryCount,
                    sheetTurns = loop.SheetTurns,
                    url = $"/api/goal-loops/{id}/sheet?v={loop.SheetEntryCount}",
                }, UiGoalLoopJson.Options);
            });

            app.MapGet("/api/goal-loops/{id}/sheet", (string id, HttpContext ctx) =>
            {
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                var path = UiGoalLoopRunner.SheetPath(state);
                if (path == null)
                {
                    return Results.NotFound(new { error = "this loop has no sheet yet; build one first" });
                }
                ctx.Response.Headers.CacheControl = "no-cache";
                return Results.File(path, "image/png", fileDownloadName: null, lastModified: File.GetLastWriteTimeUtc(path), enableRangeProcessing: false);
            });

            app.MapPost("/api/goal-loops/{id}/feedback", async (string id, HttpContext ctx) =>
            {
                var state = goalLoops.Get(id);
                if (state == null) return Results.NotFound(new { error = "unknown goal loop" });
                var authUser = ctx.Items["micUser"] as string ?? "";
                if (!CanControlGoalLoop(state.Loop, authUser))
                    return Results.Json(new { error = "only the loop's creator can give operator points" }, statusCode: 403);
                if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "feedback requires form fields" });
                var form = await ctx.Request.ReadFormAsync();
                if (!int.TryParse(form["entryIndex"], out var index) || !int.TryParse(form["delta"], out var delta))
                    return Results.BadRequest(new { error = "entryIndex and delta must be integers" });
                try
                {
                    var feedback = goalLoopRunner.AddFeedback(state, form["requestId"].ToString(), form["scope"].ToString(), index, delta,
                        authUser.Length > 0 ? authUser : state.Loop.CreatedBy);
                    return Results.Json(new { ok = true, feedback }, UiGoalLoopJson.Options);
                }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            });

            app.MapPost("/api/goal-loops/{id}/stop", (string id, HttpContext ctx) =>
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                if (!CanControlGoalLoop(state.Loop, authUser))
                {
                    return Results.Json(new { error = "only the loop's creator can stop it" }, statusCode: 403);
                }
                try
                {
                    goalLoopRunner.Stop(state, authUser.Length > 0 ? authUser : state.Loop.CreatedBy);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 409);
                }
                Logger.Log($"[goal #{id}] stop requested");
                return Results.Json(new { ok = true });
            });

            app.MapPost("/api/goal-loops/{id}/resume", async (string id, HttpRequest request) =>
            {
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                var state = goalLoops.Get(id);
                if (state == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                if (!CanControlGoalLoop(state.Loop, authUser))
                {
                    return Results.Json(new { error = "only the loop's creator can resume it" }, statusCode: 403);
                }
                // maxTurns is optional, and so is the form body itself.
                var maxTurnsText = request.HasFormContentType
                    ? (await request.ReadFormAsync())["maxTurns"].ToString()
                    : "";
                int? maxTurns = null;
                if (!string.IsNullOrWhiteSpace(maxTurnsText))
                {
                    if (!int.TryParse(maxTurnsText, out var parsed))
                    {
                        return Results.BadRequest(new { error = "max turns must be a whole number" });
                    }
                    maxTurns = parsed;
                }
                try
                {
                    goalLoopRunner.Resume(state, maxTurns);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 409);
                }
                Logger.Log($"[goal #{id}] resumed (maxTurns={state.Loop.MaxTurns})");
                return Results.Json(new { ok = true, maxTurns = state.Loop.MaxTurns });
            });

            app.MapPost("/api/goal-loops/{id}/fork", async (string id, HttpRequest request) =>
            {
                var diskProblem = DescribeDiskCapacityProblem(settings);
                if (diskProblem != null)
                {
                    return Results.Json(new { error = diskProblem }, statusCode: 503);
                }
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                var parent = goalLoops.Get(id);
                if (parent == null)
                {
                    return Results.NotFound(new { error = "unknown goal loop" });
                }
                var form = await request.ReadFormAsync();
                if (!int.TryParse(form["entryIndex"].ToString(), out var entryIndex))
                {
                    return Results.BadRequest(new { error = "entryIndex is required" });
                }
                string? text = null;
                if (form.ContainsKey("text"))
                {
                    text = form["text"].ToString();
                    if (text.Length > UiGoalLoopRunner.MaxEditedTextChars)
                    {
                        return Results.BadRequest(new { error = $"edited text exceeds {UiGoalLoopRunner.MaxEditedTextChars} characters" });
                    }
                }
                int? maxTurns = null;
                var maxTurnsText = form["maxTurns"].ToString();
                if (!string.IsNullOrWhiteSpace(maxTurnsText))
                {
                    if (!int.TryParse(maxTurnsText, out var parsed))
                    {
                        return Results.BadRequest(new { error = "max turns must be a whole number" });
                    }
                    maxTurns = parsed;
                }
                if (!TryResolveCreatorName(form["user"].ToString(), authUser, community, out var createdBy, out var userError))
                {
                    return Results.BadRequest(new { error = userError });
                }
                // The fork re-validates the generator and manager because a
                // key may have been removed since the parent was created.
                foreach (var generator in parent.Loop.GeneratorList())
                {
                    var generatorProblem = UiGoalLoopRunner.GeneratorProblem(runner, generator.Key);
                    if (generatorProblem.Length > 0)
                    {
                        return Results.BadRequest(new { error = $"generator {generator.Key} not usable: {generatorProblem}" });
                    }
                }
                var manager = ManagerCatalog.Find(parent.Loop.ManagerKey);
                var managerProblem = manager == null ? "unknown manager" : ManagerCatalog.DescribeAvailabilityProblem(manager, settings);
                if (managerProblem != null)
                {
                    return Results.BadRequest(new { error = $"manager not available: {managerProblem}" });
                }
                foreach (var critic in parent.Loop.CriticList())
                {
                    var criticDefinition = ManagerCatalog.Find(critic.Key);
                    var criticProblem = criticDefinition == null ? "unknown critic model" : ManagerCatalog.DescribeAvailabilityProblem(criticDefinition, settings);
                    if (criticProblem != null)
                    {
                        return Results.BadRequest(new { error = $"critic {critic.Label} not available: {criticProblem}" });
                    }
                }
                UiGoalLoopState child;
                try
                {
                    child = goalLoopRunner.Fork(parent, entryIndex, text, maxTurns, createdBy, authUser);
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 503);
                }
                Logger.Log($"[goal #{child.Loop.Id}] forked from {id} at entry {entryIndex}{(text == null ? "" : " with edited text")} by '{createdBy}'");
                return Results.Json(new { id = child.Loop.Id }, UiGoalLoopJson.Options);
            });
        }

        // List projection for goal.html's sidebar: everything except the
        // (identical, multi-KB) system prompt, which the detail view carries.
        private static object SummarizeGoalLoop(UiGoalLoop loop, bool canControl) => new
        {
            id = loop.Id,
            goal = loop.Goal,
            generatorKey = loop.GeneratorKey,
            generatorLabel = loop.GeneratorLabel,
            generators = loop.GeneratorList().Select(g => new { key = g.Key, label = g.Label, source = g.Source }).ToList(),
            managerKey = loop.ManagerKey,
            managerLabel = loop.ManagerLabel,
            managerModel = loop.ManagerModel,
            critics = loop.CriticList().Select(c => new { key = c.Key, label = c.Label, model = c.Model, index = c.Index }).ToList(),
            maxTurns = loop.MaxTurns,
            protocolVersion = loop.ProtocolVersion,
            goalKind = loop.GoalKind,
            effectiveGoalKind = loop.EffectiveGoalKind,
            goalKindSource = loop.GoalKindSource,
            sheetFile = loop.SheetFile,
            sheetEntryCount = loop.SheetEntryCount,
            sheetTurns = loop.SheetTurns,
            turnsRendered = loop.TurnsRendered,
            entryCount = loop.EntryCount,
            status = loop.Status,
            statusDetail = loop.StatusDetail,
            activity = loop.Activity,
            activitySinceUnixMs = loop.ActivitySinceUnixMs,
            createdBy = loop.CreatedBy,
            createdAtUnixMs = loop.CreatedAtUnixMs,
            updatedAtUnixMs = loop.UpdatedAtUnixMs,
            lastScore = loop.LastScore,
            bestScore = loop.BestScore,
            bestTurn = loop.BestTurn,
            bestVariant = loop.BestVariant,
            bestSource = loop.BestSource,
            rendersPerTurn = loop.RendersPerTurn,
            parentLoopId = loop.ParentLoopId,
            forkedAtEntry = loop.ForkedAtEntry,
            managerCostUsd = loop.ManagerCostUsd,
            managerCostKnown = loop.ManagerCostKnown,
            renderCostUsd = loop.RenderCostUsd,
            criticCostUsd = loop.CriticCostUsd,
            criticCostKnown = loop.CriticCostKnown,
            criticInputTokens = loop.CriticInputTokens,
            criticOutputTokens = loop.CriticOutputTokens,
            canControl,
        };

    }
}
