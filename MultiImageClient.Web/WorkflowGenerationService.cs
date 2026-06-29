using System.Collections.Concurrent;
using MultiImageClient;

namespace MultiImageClient.Web;

public sealed class WorkflowGenerationService
{
    private readonly Settings _settings;
    private readonly WorkflowStore _store;
    private readonly WorkflowLogService _log;
    private readonly ConcurrentDictionary<string, WorkflowJob> _jobs = new();

    public WorkflowGenerationService(Settings settings, WorkflowStore store, WorkflowLogService log)
    {
        _settings = settings;
        _store = store;
        _log = log;
    }

    public IReadOnlyList<WorkflowJob> ListJobs() => _store.ListRuns()
        .SelectMany(run => run.Jobs)
        .OrderByDescending(j => j.CreatedAt)
        .ToList();

    public WorkflowJob? GetJob(string jobId) => _store.ListRuns()
        .SelectMany(run => run.Jobs)
        .FirstOrDefault(job => string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ProviderOption> GetProviders()
    {
        var stats = new MultiClientRunStats();
        var groups = new GeneratorGroups(_settings, concurrency: 1, stats);
        return groups.GetOnePerProviderCatalog(includeVideo: false)
            .Select(preset =>
            {
                var generator = preset.CreateGenerator();
                var disabledReason = ProviderKeyValidator.DescribeKeyProblem(generator.ApiType, _settings) ?? "";
                return new ProviderOption
                {
                    Id = preset.Id,
                    Label = preset.DisplayName,
                    ApiType = generator.ApiType.ToString(),
                    EstimatedCost = generator.GetCost(),
                    Enabled = string.IsNullOrWhiteSpace(disabledReason),
                    DisabledReason = disabledReason,
                };
            })
            .ToList();
    }

    public WorkflowJob StartGenerateJob(string runId, GenerateRequest request)
    {
        var run = _store.GetRun(runId);
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? run.OriginalPrompt : request.Prompt.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.");
        }

        var count = Math.Clamp(request.Count, 1, 20);
        var job = _store.AddJob(runId, "generate", $"Queued {count} generation round(s).");
        _jobs[job.Id] = job;
        _log.Info("generate", $"Queued job {job.Id} for run {runId}: {count} round(s), {request.ProviderIds.Count} selected provider(s).");

        _ = Task.Run(() => RunGenerateJobAsync(job, prompt, request.ParentNodeId, request.ProviderIds, count));
        return job;
    }

    private async Task RunGenerateJobAsync(WorkflowJob job, string prompt, string parentNodeId, IReadOnlyList<string> providerIds, int count)
    {
        try
        {
            job.Status = "running";
            PersistJob(job);
            _log.Info("generate", $"Started job {job.Id} for run {job.RunId}.");
            var stats = new MultiClientRunStats();
            var groups = new GeneratorGroups(_settings, concurrency: 1, stats);
            var presets = groups.ResolvePresets(providerIds, includeVideo: false);
            var runner = new PromptGenerationRunner(_settings);

            if (presets.Count == 0)
            {
                throw new InvalidOperationException("No providers selected.");
            }
            _log.Info("generate", $"Job {job.Id} will call {presets.Count} provider preset(s): {string.Join(", ", presets.Select(p => p.Id))}");

            for (var round = 1; round <= count; round++)
            {
                foreach (var preset in presets)
                {
                    job.Message = $"Running {preset.DisplayName} ({round}/{count})";
                    PersistJob(job);
                    _log.Info("provider", $"Job {job.Id}: {job.Message}");
                    var nodeId = WorkflowStore.NewId("image");

                    try
                    {
                        var result = await runner.GenerateAsync(preset, prompt);
                        if (!result.IsSuccess)
                        {
                            _log.Error("provider", $"Job {job.Id}: {preset.Id} failed: {result.Error}");
                            AddErrorNode(job, nodeId, prompt, parentNodeId, preset.DisplayName, result.Error);
                            continue;
                        }

                        foreach (var image in result.Images)
                        {
                            var imageNodeId = image.Index == 0 ? nodeId : WorkflowStore.NewId("image");
                            var extension = ExtensionForContentType(image.ContentType);
                            var imageFileName = $"{imageNodeId}{extension}";
                            var imagePath = Path.Combine(_store.GetImagesPath(job.RunId), imageFileName);
                            await File.WriteAllBytesAsync(imagePath, image.Bytes);

                            var node = new WorkflowNode
                            {
                                Id = imageNodeId,
                                Type = "image",
                                Prompt = prompt,
                                Provider = preset.DisplayName,
                                ImagePath = _store.GetBrowserPath(imagePath),
                                Meta =
                                {
                                    ["providerPresetId"] = preset.Id,
                                    ["apiType"] = result.Generator.ApiType.ToString(),
                                    ["round"] = round.ToString(),
                                    ["imageIndex"] = image.Index.ToString(),
                                    ["contentType"] = image.ContentType,
                                    ["createMs"] = (result.TaskResult?.CreateTotalMs ?? 0).ToString(),
                                    ["revisedPrompt"] = image.RevisedPrompt,
                                },
                            };
                            AddNode(job, node, parentNodeId);
                            _log.Info("node", $"Job {job.Id}: added image node {imageNodeId} from {preset.Id} -> {node.ImagePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error("provider", $"Job {job.Id}: {preset.Id} threw", ex);
                        AddErrorNode(job, nodeId, prompt, parentNodeId, preset.DisplayName, ex.Message);
                    }
                }
            }

            job.Status = "done";
            job.Message = $"Done. Added {job.NodeIds.Count} node(s).";
            job.CompletedAt = DateTimeOffset.UtcNow;
            PersistJob(job);
            _log.Info("generate", $"Completed job {job.Id}: {job.Message}");
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Message = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            PersistJob(job);
            _log.Error("generate", $"Job {job.Id} failed", ex);
        }
    }

    private void AddErrorNode(WorkflowJob job, string nodeId, string prompt, string parentNodeId, string provider, string error)
    {
        var node = new WorkflowNode
        {
            Id = nodeId,
            Type = "image",
            Status = "failed",
            Prompt = prompt,
            Provider = provider,
            Error = error,
        };
        AddNode(job, node, parentNodeId);
    }

    private void AddNode(WorkflowJob job, WorkflowNode node, string parentNodeId)
    {
        _store.AddNode(job.RunId, node, parentNodeId, node.Type);
        job.NodeIds.Add(node.Id);
        PersistJob(job);
    }

    private void PersistJob(WorkflowJob job)
    {
        _store.UpdateJob(job.RunId, job.Id, persisted =>
        {
            persisted.Status = job.Status;
            persisted.Message = job.Message;
            persisted.CompletedAt = job.CompletedAt;
            persisted.NodeIds = job.NodeIds.ToList();
        });
    }

    private static string ExtensionForContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        _ => ".png",
    };
}
