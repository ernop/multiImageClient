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
        private static void MapPromptToolRoutes(
            WebApplication app,
            Settings settings,
            UiAuth? auth,
            UiJobRegistry jobs,
            UiVisibilityStore visibility,
            UiCommunityStore community)
        {
            app.MapPost("/api/prompt/advice", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var requestedModel = form["model"].ToString().Trim();
                var restore = requestedModel == "restore";
                var rawInstruction = form["instruction"].ToString().Trim();
                var directed = !restore && rawInstruction.Length == 0;
                ManagerDefinition? definition = null;
                if (!restore)
                {
                    if (directed && requestedModel.Length == 0)
                    {
                        return Results.BadRequest(new { error = "Prompt edit instruction must be between 1 and 4,000 characters" });
                    }
                    var modelInput = requestedModel.Length > 0 ? requestedModel : DirectedPromptRewrite.AdviceModel;
                    definition = DirectedPromptRewrite.ResolveModel(modelInput);
                    if (definition == null)
                    {
                        return Results.BadRequest(new { error = $"Unknown prompt rewrite model '{modelInput}'." });
                    }
                    var problem = ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
                    if (problem != null) return Results.BadRequest(new { error = problem });
                }
                var model = restore ? "restore" : definition!.Model;
                var prompt = form["prompt"].ToString();
                var instruction = directed ? DirectedPromptRewrite.Instruction
                    : restore ? "Restore a saved prompt version." : rawInstruction;
                if (!restore && string.IsNullOrWhiteSpace(prompt))
                {
                    return Results.BadRequest(new { error = "prompt is empty" });
                }
                if (prompt.Length > 100_000)
                {
                    return Results.BadRequest(new { error = "prompt must be 100,000 characters or fewer" });
                }
                if (instruction.Length == 0 || instruction.Length > 4000)
                {
                    return Results.BadRequest(new { error = "Prompt edit instruction must be between 1 and 4,000 characters" });
                }
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = PromptExchangeIdentityKey(authUser, actorDisplay);
                string? restoredPrompt = null;
                if (restore)
                {
                    var sourceId = form["exchangeId"].ToString();
                    var side = form["side"].ToString();
                    if (sourceId.Length == 0 || (side != "original" && side != "result"))
                        return Results.BadRequest(new { error = "Select an exact saved prompt version." });
                    var source = community.ReadClaudePromptExchanges(identityKey, 1, exchangeId: sourceId).SingleOrDefault();
                    if (source == null || (side == "result" && source.Status != "succeeded"))
                        return Results.BadRequest(new { error = "This saved prompt version is unavailable." });
                    if (side == "result" && source.Purpose == PromptSplit.Purpose)
                        return Results.BadRequest(new { error = "A split holds one prompt per image. Restore its source prompt, or use set active on a part's job card." });
                    restoredPrompt = side == "original" ? source.OriginalPrompt : source.ResultPrompt;
                    instruction = $"Restore {sourceId} / {side}.";
                }
                var wirePrompt = restore ? "" : ClaudeService.BuildPromptAdviceWirePrompt(instruction, prompt);
                var exchange = community.StartClaudePromptExchange(
                    identityKey,
                    actorDisplay,
                    model,
                    instruction,
                    prompt,
                    restore ? "" : ClaudeService.PromptAdviceSystemPrompt,
                    wirePrompt,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                string rawResponse;
                string replacement;
                string failure;
                try
                {
                    var result = restore
                        ? new ClaudePromptAdviceResult { ResultPrompt = restoredPrompt! }
                        : await DirectedPromptRewrite.RewriteAsync(model, instruction, prompt, settings, request.HttpContext.RequestAborted);
                    rawResponse = result.RawResponse;
                    replacement = result.ResultPrompt;
                    failure = result.Error;
                    if (!directed && !restore && failure.Length == 0 && ClaudeService.DidClaudeRefuse(replacement))
                    {
                        failure = $"{definition!.Label} refused to edit this prompt: {replacement}";
                        replacement = "";
                    }
                }
                catch (Exception ex)
                {
                    rawResponse = "";
                    replacement = "";
                    failure = ex.Message;
                }
                community.CompleteClaudePromptExchange(
                    exchange.Id,
                    rawResponse,
                    replacement,
                    failure,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (failure.Length > 0)
                {
                    var actionHint = definition == null
                        ? null
                        : ProviderActionHints.ForProvider(definition.Provider, failure);
                    Logger.Log($"UI prompt edit {exchange.Id} failed for '{actorDisplay}': {failure}"
                        + (actionHint != null
                            ? $"\nUI prompt edit next step: {actionHint.Text} -> {actionHint.Url}"
                            : ""));
                    return Results.Json(
                        new
                        {
                            error = failure,
                            exchangeId = exchange.Id,
                            errorHint = actionHint?.Text,
                            errorHintUrl = actionHint?.Url,
                        },
                        statusCode: 502);
                }
                Logger.Log($"UI prompt edit {exchange.Id} completed for '{actorDisplay}' ({model}).");
                return Results.Json(new
                {
                    replacement,
                    model,
                    modelLabel = definition?.Label ?? "",
                    originalPrompt = prompt,
                    exchangeId = exchange.Id,
                });
            });

            // Condenses the prompt for the named targets only. The reply never
            // edits the composer prompt; a job sends the condensed prompt to
            // exactly those targets. See docs/prompt-length-limits.md.
            // One request per identity, prompt, and limit runs at a time, so
            // several tabs cannot send the same text to the provider twice.
            var condenseInFlight = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            app.MapPost("/api/prompt/condense", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var requestedModel = form["model"].ToString().Trim();
                var modelInput = requestedModel.Length > 0 ? requestedModel : DirectedPromptRewrite.CondenseModel;
                var definition = DirectedPromptRewrite.ResolveModel(modelInput);
                if (definition == null)
                {
                    return Results.BadRequest(new { error = $"Unknown prompt condense model '{modelInput}'." });
                }
                var problem = ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
                if (problem != null) return Results.BadRequest(new { error = problem });
                var model = definition.Model;
                var modelLabel = definition.Label;
                var prompt = UiJobRunner.PromptFromFormField(form["prompt"].ToString());
                if (prompt.Length == 0)
                {
                    return Results.BadRequest(new { error = "prompt is empty" });
                }
                if (prompt.Length > 100_000)
                {
                    return Results.BadRequest(new { error = "prompt must be 100,000 characters or fewer" });
                }
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = PromptExchangeIdentityKey(authUser, actorDisplay);
                UiPromptCondensePlan plan;
                try
                {
                    if (!form.ContainsKey("generatorExtraTexts"))
                    {
                        throw new InvalidDataException("A condense request must include generatorExtraTexts.");
                    }
                    var keys = form["generators"].ToString()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    plan = UiJobRunner.PlanPromptCondense(
                        keys,
                        prompt,
                        ParseGeneratorExtraTexts(form, keys),
                        imageAttached: form["imageAttached"].ToString() == "true");
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                var instruction = DirectedPromptRewrite.CondenseInstruction(plan.Budget, plan.SourceCharacters);
                object Reply(UiClaudePromptExchangeRecord exchange, bool reused) => new
                {
                    exchangeId = exchange.Id,
                    model,
                    modelLabel,
                    condensedPrompt = exchange.ResultPrompt,
                    budget = plan.Budget,
                    generators = plan.Keys,
                    reused,
                };
                if (form["fresh"].ToString() != "true")
                {
                    var stored = community.FindSucceededClaudePromptExchange(
                        identityKey,
                        DirectedPromptRewrite.CondensePurpose,
                        model,
                        instruction,
                        prompt);
                    if (stored != null)
                    {
                        return Results.Json(Reply(stored, reused: true));
                    }
                }
                var flightKey = $"{identityKey}\n{model}\n{instruction}\n{prompt}";
                if (!condenseInFlight.TryAdd(flightKey, 0))
                {
                    return Results.Conflict(new
                    {
                        error = "This prompt is already being condensed for this limit, possibly in another tab. "
                            + "Press condense again after it finishes.",
                    });
                }
                try
                {
                    var exchange = community.StartClaudePromptExchange(
                        identityKey,
                        actorDisplay,
                        model,
                        instruction,
                        prompt,
                        ClaudeService.PromptAdviceSystemPrompt,
                        ClaudeService.BuildPromptAdviceWirePrompt(instruction, prompt),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        DirectedPromptRewrite.CondensePurpose);
                    string rawResponse;
                    string condensed;
                    string failure;
                    try
                    {
                        var result = await DirectedPromptRewrite.RewriteAsync(
                            model, instruction, prompt, settings, request.HttpContext.RequestAborted);
                        rawResponse = result.RawResponse;
                        failure = result.Error;
                        condensed = failure.Length == 0
                            ? result.ResultPrompt.Replace("\r\n", "\n").Replace('\r', '\n').Trim()
                            : "";
                        // A refusal phrase the source already contains can be
                        // ordinary prompt text.
                        if (failure.Length == 0
                            && ClaudeService.DidClaudeRefuse(condensed)
                            && !ClaudeService.DidClaudeRefuse(prompt))
                        {
                            failure = $"{modelLabel} refused to condense this prompt: {condensed}";
                        }
                        if (failure.Length == 0)
                        {
                            failure = UiJobRunner.DescribeCondensedPromptProblem(condensed, plan, modelLabel) ?? "";
                        }
                        if (failure.Length > 0)
                        {
                            condensed = "";
                        }
                    }
                    catch (Exception ex)
                    {
                        rawResponse = "";
                        condensed = "";
                        failure = ex.Message;
                    }
                    community.CompleteClaudePromptExchange(
                        exchange.Id,
                        rawResponse,
                        condensed,
                        failure,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (failure.Length > 0)
                    {
                        var actionHint = ProviderActionHints.ForProvider(definition.Provider, failure);
                        Logger.Log($"UI prompt condense {exchange.Id} failed for '{actorDisplay}': {failure}"
                            + (actionHint != null
                                ? $"\nUI prompt condense next step: {actionHint.Text} -> {actionHint.Url}"
                                : ""));
                        return Results.Json(
                            new
                            {
                                error = failure,
                                exchangeId = exchange.Id,
                                errorHint = actionHint?.Text,
                                errorHintUrl = actionHint?.Url,
                            },
                            statusCode: 502);
                    }
                    Logger.Log($"UI prompt condense {exchange.Id} completed for '{actorDisplay}': "
                        + $"{condensed.EnumerateRunes().Count():N0} of {plan.Budget:N0} characters for {string.Join(", ", plan.Keys)}.");
                    return Results.Json(Reply(
                        new UiClaudePromptExchangeRecord { Id = exchange.Id, ResultPrompt = condensed },
                        reused: false));
                }
                finally
                {
                    condenseInFlight.TryRemove(flightKey, out _);
                }
            });

            // Asks the configured writer model (default Opus 5.5) whether the
            // prompt serves better as several images and stores its plan. The
            // browser then submits the plan to /api/jobs, which makes one job
            // per part. Every press asks again. See docs/prompt-split-prd.md.
            app.MapPost("/api/prompt/split", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var requestedModel = form["model"].ToString().Trim();
                var definition = PromptSplit.ResolveModel(requestedModel.Length > 0 ? requestedModel : PromptSplit.ModelKey);
                if (definition == null)
                {
                    return Results.BadRequest(new { error = $"Unknown prompt split model '{requestedModel}'." });
                }
                var problem = ManagerCatalog.DescribeAvailabilityProblem(definition, settings);
                if (problem != null) return Results.BadRequest(new { error = problem });
                var prompt = UiJobRunner.PromptFromFormField(form["prompt"].ToString());
                if (prompt.Length == 0)
                {
                    return Results.BadRequest(new { error = "prompt is empty" });
                }
                if (prompt.Length > PromptSplit.MaxSourceChars)
                {
                    return Results.BadRequest(new { error = $"prompt must be {PromptSplit.MaxSourceChars:N0} characters or fewer" });
                }
                var instruction = UiJobRunner.PromptFromFormField(form["instruction"].ToString());
                if (instruction.Length == 0 || instruction.Length > PromptSplit.MaxInstructionChars)
                {
                    return Results.BadRequest(new
                    {
                        error = $"The split instruction must be between 1 and {PromptSplit.MaxInstructionChars:N0} characters.",
                    });
                }
                var styleNote = UiJobRunner.PromptFromFormField(form["styleNote"].ToString());
                if (styleNote.Length > PromptSplit.MaxStyleNoteChars)
                {
                    return Results.BadRequest(new
                    {
                        error = $"The style note must be {PromptSplit.MaxStyleNoteChars:N0} characters or fewer.",
                    });
                }
                var authUser = request.HttpContext.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    form["user"].ToString(),
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = PromptExchangeIdentityKey(authUser, actorDisplay);
                int partBudget;
                try
                {
                    var keys = form["generators"].ToString()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (keys.Length == 0)
                    {
                        partBudget = PromptSplit.DefaultUnconstrainedPartBudget;
                    }
                    else
                    {
                        if (!form.ContainsKey("generatorExtraTexts"))
                        {
                            throw new InvalidDataException("A split request must include generatorExtraTexts.");
                        }
                        partBudget = PromptSplit.PartBudget(
                            keys,
                            ParseGeneratorExtraTexts(form, keys),
                            imageAttached: form["imageAttached"].ToString() == "true");
                    }
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                using var lease = PromptSplit.TryBegin(identityKey, out var busy);
                if (lease == null)
                {
                    return Results.Conflict(new { error = busy });
                }
                var userMessage = PromptSplit.BuildUserMessage(instruction, styleNote, partBudget, prompt);
                var exchange = community.StartClaudePromptExchange(
                    identityKey,
                    actorDisplay,
                    definition.Model,
                    PromptSplit.RecordedInstruction(instruction, styleNote),
                    prompt,
                    PromptSplit.SystemPrompt,
                    userMessage,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PromptSplit.Purpose);
                var outcome = await PromptSplit.RequestAsync(
                    ManagerCatalog.Build(definition, settings),
                    userMessage,
                    partBudget,
                    definition.Label,
                    request.HttpContext.RequestAborted);
                community.CompleteClaudePromptExchange(
                    exchange.Id,
                    outcome.RawResponse,
                    outcome.Plan == null ? "" : PromptSplit.Serialize(outcome.Plan),
                    outcome.Error,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var tokens = $"{outcome.InputTokens?.ToString("N0") ?? "unknown"} input and "
                    + $"{outcome.OutputTokens?.ToString("N0") ?? "unknown"} output tokens";
                if (outcome.Plan == null)
                {
                    var actionHint = PromptSplit.ActionHint(outcome, definition.Provider);
                    Logger.Log($"UI prompt split {exchange.Id} failed for '{actorDisplay}' ({tokens}): {outcome.Error}"
                        + (actionHint != null
                            ? $"\nUI prompt split next step: {actionHint.Text} -> {actionHint.Url}"
                            : ""));
                    return Results.Json(
                        new
                        {
                            error = outcome.Error,
                            exchangeId = exchange.Id,
                            errorHint = actionHint?.Text,
                            errorHintUrl = actionHint?.Url,
                        },
                        statusCode: 502);
                }
                Logger.Log($"UI prompt split {exchange.Id} completed for '{actorDisplay}': "
                    + $"{outcome.Plan.ImageCount} image(s), {tokens}.");
                return Results.Json(new
                {
                    exchangeId = exchange.Id,
                    modelKey = definition.Key,
                    model = definition.Model,
                    modelLabel = definition.Label,
                    partBudget,
                    imageCount = outcome.Plan.ImageCount,
                    reason = outcome.Plan.Reason,
                    images = outcome.Plan.Parts.Select(part => new { title = part.Title, prompt = part.Prompt }),
                });
            });

            // Returns the stored source prompt, instruction, style note, and plan
            // for one split exchange so split groups in the feed can inspect,
            // re-split, or re-render without hunting in prompt history.
            app.MapGet("/api/prompt/split/{exchangeId}", (string exchangeId, string? jobId, HttpContext ctx) =>
            {
                var exchange = community.GetClaudePromptExchangeById(exchangeId);
                if (exchange == null || exchange.Purpose != PromptSplit.Purpose)
                {
                    return Results.NotFound(new { error = $"Split {exchangeId} was not found." });
                }
                var authUser = ctx.Items["micUser"] as string ?? "";
                var permitted = auth == null;
                if (!permitted && authUser.Length > 0)
                {
                    var callerIdentity = PromptExchangeIdentityKey(authUser, authUser);
                    if (string.Equals(exchange.IdentityKey, callerIdentity, StringComparison.Ordinal))
                    {
                        permitted = true;
                    }
                }
                if (!permitted)
                {
                    var candidate = !string.IsNullOrWhiteSpace(jobId) ? jobs.Get(jobId.Trim()) : null;
                    if (candidate != null
                        && !visibility.IsPromptHidden(candidate.Id)
                        && string.Equals(candidate.Split?.ExchangeId, exchange.Id, StringComparison.Ordinal))
                    {
                        permitted = true;
                    }
                    else
                    {
                        permitted = jobs.ListChronological().Any(j =>
                            !visibility.IsPromptHidden(j.Id)
                            && string.Equals(j.Split?.ExchangeId, exchange.Id, StringComparison.Ordinal));
                    }
                }
                if (!permitted)
                {
                    return Results.NotFound(new { error = $"Split {exchangeId} was not found." });
                }
                var (instruction, styleNote) = PromptSplit.ParseRecordedInstruction(exchange.Instruction);
                PromptSplitPlan? plan = null;
                if (exchange.Status == "succeeded" && !string.IsNullOrWhiteSpace(exchange.ResultPrompt))
                {
                    try { plan = PromptSplit.ParseStored(exchange.ResultPrompt); }
                    catch (InvalidDataException) { plan = null; }
                }
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    exchangeId = exchange.Id,
                    status = exchange.Status,
                    model = exchange.Model,
                    modelKey = ManagerCatalog.FindByModel(exchange.Model)?.Key ?? "",
                    modelLabel = PromptSplit.ModelLabel(exchange.Model),
                    sourcePrompt = exchange.OriginalPrompt,
                    instruction,
                    styleNote,
                    createdAtUnixMs = exchange.RequestedAtUnixMs,
                    error = exchange.Error,
                    imageCount = plan?.ImageCount ?? 0,
                    reason = plan?.Reason ?? "",
                    images = plan?.Parts.Select(part => new { title = part.Title, prompt = part.Prompt }).ToArray()
                        ?? Array.Empty<object>(),
                });
            });

            app.MapGet("/api/prompt/advice/history", (
                string? user,
                int? limit,
                long? beforeTime,
                string? beforeId,
                HttpContext ctx) =>
            {
                if (beforeTime.HasValue != !string.IsNullOrEmpty(beforeId))
                    return Results.BadRequest(new { error = "History requires both cursor fields." });
                var authUser = ctx.Items["micUser"] as string ?? "";
                if (!TryResolveCreatorName(
                    user,
                    authUser,
                    community,
                    out var actorDisplay,
                    out var identityError))
                {
                    return Results.BadRequest(new { error = identityError });
                }
                var identityKey = PromptExchangeIdentityKey(authUser, actorDisplay);
                var records = community.ReadClaudePromptExchanges(identityKey, limit ?? 50, beforeTime, beforeId);
                return Results.Json(new
                {
                    exchanges = records.Select(record => new
                    {
                        record.Id,
                        record.RequestedAtUnixMs,
                        record.CompletedAtUnixMs,
                        actor = record.ActorDisplay,
                        record.Model,
                        record.Instruction,
                        record.OriginalPrompt,
                        record.SystemPrompt,
                        record.WirePrompt,
                        record.RawResponse,
                        record.ResultPrompt,
                        record.Status,
                        record.Error,
                        record.Purpose,
                    }),
                });
            });

            static object SerializeSubjectTag(UiSubjectTagRecord record) => new
            {
                id = record.Id,
                name = record.Name,
                kind = record.Kind,
                searchQuery = record.SearchQuery,
                model = record.Model,
                description = record.Description,
                sourceSummary = record.SourceSummary,
                matchedPromptsCount = record.MatchedPromptsCount,
                updatedAtUnixMs = record.UpdatedAtUnixMs,
            };

            static object SerializeMatchedPrompts(IEnumerable<UiSubjectTagMatchedPrompt> matches) => matches.Select(m => new
            {
                source = m.Source,
                recordId = m.RecordId,
                createdAtUtc = m.CreatedAtUtc,
                prompt = m.Prompt,
            }).ToList();

            // Characters belong to the signed-in account. The auth-off local
            // instance has one owner, so every "creating as" name shares one
            // collection there.
            (string IdentityKey, string Actor, PromptSubjectSearchScope Scope) CharacterIdentity(HttpContext ctx)
            {
                var authUser = ctx.Items["micUser"] as string ?? "";
                return authUser.Length > 0
                    ? ("login:" + authUser, authUser, PromptSubjectSearchScope.ForAccount(authUser))
                    : ("local", "local", PromptSubjectSearchScope.LocalOwner);
            }

            app.MapGet("/api/prompt/subjects", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var identity = CharacterIdentity(ctx);
                return Results.Json(new
                {
                    subjects = community.ListSubjectTags(identity.IdentityKey).Select(SerializeSubjectTag),
                });
            });

            app.MapPost("/api/prompt/subjects/search", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var identity = CharacterIdentity(request.HttpContext);
                try
                {
                    var matches = PromptSubjectSynthesis.SearchAllHistory(
                        settings, form["name"].ToString(), form["query"].ToString(),
                        jobs, community, visibility, identity.Scope);
                    return Results.Json(new { matches = SerializeMatchedPrompts(matches) });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/prompt/subjects/synthesize", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var identity = CharacterIdentity(request.HttpContext);
                var id = form["id"].ToString();
                var name = form["name"].ToString();
                var kind = form["kind"].ToString();
                var query = form["query"].ToString();
                using var lease = PromptSubjectSynthesis.TryBeginSynthesis(identity.IdentityKey);
                if (lease == null)
                {
                    return Results.Json(
                        new { error = "A description is already being written for your account. Wait for it to finish." },
                        statusCode: 409);
                }
                try
                {
                    // Check the save target before the paid model call.
                    if (id.Length > 0)
                    {
                        var target = community.GetSubjectTagById(identity.IdentityKey, id)
                            ?? throw new InvalidDataException("That character no longer exists.");
                        var holder = community.GetSubjectTagByName(identity.IdentityKey, name);
                        if (holder != null && holder.Id != target.Id)
                        {
                            throw new InvalidDataException($"You already have a character named {holder.Name}. Edit that one instead.");
                        }
                    }
                    else if (community.GetSubjectTagByName(identity.IdentityKey, name) is { } existingByName)
                    {
                        throw new InvalidDataException($"You already have a character named {existingByName.Name}. Edit that one instead.");
                    }
                    var matches = PromptSubjectSynthesis.SearchAllHistory(
                        settings, name, query, jobs, community, visibility, identity.Scope);
                    var synthesized = await PromptSubjectSynthesis.SynthesizeSubjectAsync(
                        settings, name, kind, query, form["model"].ToString(), matches,
                        form["notes"].ToString(), ct: request.HttpContext.RequestAborted);
                    var saved = community.SaveSubjectTag(
                        identity.IdentityKey, identity.Actor, id,
                        synthesized.Name, synthesized.Kind, query, synthesized.Model,
                        synthesized.Description, synthesized.SourceSummary,
                        synthesized.MatchedPrompts, synthesized.RawResponse,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log(
                        $"UI character '{saved.Name}' ({saved.Kind}) written by {saved.Model} from {saved.MatchedPromptsCount} past prompt(s) for '{identity.Actor}'.");
                    return Results.Json(new
                    {
                        subject = SerializeSubjectTag(saved),
                        matches = SerializeMatchedPrompts(synthesized.MatchedPrompts),
                    });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    return Results.Empty;
                }
                catch (Exception ex)
                {
                    Logger.Log($"UI character synthesis failed for '{identity.Actor}': {ex.Message}");
                    return Results.Json(new { error = ex.Message }, statusCode: 502);
                }
            });

            app.MapPost("/api/prompt/subjects", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var identity = CharacterIdentity(request.HttpContext);
                var id = form["id"].ToString();
                try
                {
                    // A hand edit keeps the record's sources but is no longer
                    // the model's text, so the model becomes "manual".
                    var existing = id.Length > 0
                        ? community.GetSubjectTagById(identity.IdentityKey, id)
                            ?? throw new InvalidDataException("That character no longer exists.")
                        : null;
                    var description = form["description"].ToString().Trim();
                    var unchanged = existing != null && existing.Description == description;
                    var saved = community.SaveSubjectTag(
                        identity.IdentityKey, identity.Actor, id,
                        form["name"].ToString(), form["kind"].ToString(), form["query"].ToString(),
                        unchanged ? existing!.Model : "manual",
                        description,
                        existing?.SourceSummary ?? "",
                        existing?.MatchedPrompts,
                        unchanged ? existing!.RawResponse : "",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Logger.Log($"UI character '{saved.Name}' ({saved.Kind}) saved for '{identity.Actor}'.");
                    return Results.Json(new { subject = SerializeSubjectTag(saved) });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });

            app.MapPost("/api/prompt/subjects/delete", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var identity = CharacterIdentity(request.HttpContext);
                try
                {
                    if (!community.DeleteSubjectTag(identity.IdentityKey, form["id"].ToString()))
                    {
                        return Results.NotFound(new { error = "That character no longer exists." });
                    }
                    Logger.Log($"UI character {form["id"]} deleted by '{identity.Actor}'.");
                    return Results.Json(new { deleted = true });
                }
                catch (InvalidDataException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
        }

        // Owner of stored Claude prompt exchanges: the login when signed in,
        // otherwise the resolved creator name.
        private static string PromptExchangeIdentityKey(string authUser, string actorDisplay)
            => authUser.Length > 0
                ? "login:" + authUser
                : "display:" + actorDisplay.ToUpperInvariant();

    }
}
