using System.Collections.Concurrent;
using MultiImageClient;

namespace MultiImageClient.Web;

public sealed class WorkflowDescriptionService
{
    private const string DefaultDescribePrompt =
        "Describe the image clearly and concretely for use as a prompt-transfer description. Include the people or objects, composition, clothing, style, mood, lighting, colors, and any distinctive visual details. Do not invent hidden context.";

    private readonly WorkflowStore _store;
    private readonly WorkflowLogService _log;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, WorkflowJob> _jobs = new();

    public WorkflowDescriptionService(WorkflowStore store, WorkflowLogService log, Settings settings)
    {
        _store = store;
        _log = log;
        _settings = settings;
    }

    public IReadOnlyList<DescriberOption> GetDescribers()
    {
        return new List<DescriberOption>
        {
            new()
            {
                Id = "mock.visual",
                Label = "Mock visual describer (free)",
                Enabled = true,
            },
            new()
            {
                Id = "xai.grok-4.3",
                Label = "xAI Grok 4.3 Vision",
                Enabled = !string.IsNullOrWhiteSpace(_settings.XAIGrokApiKey),
                DisabledReason = string.IsNullOrWhiteSpace(_settings.XAIGrokApiKey)
                    ? "settings.json: XAIGrokApiKey is empty"
                    : "",
            },
            new()
            {
                Id = "local.qwen",
                Label = "Local Qwen via Ollama",
                Enabled = true,
            },
            new()
            {
                Id = "local.internvl",
                Label = "Local InternVL Flask",
                Enabled = true,
            },
        };
    }

    public WorkflowJob StartDescribeJob(string runId, DescribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ImageNodeId))
        {
            throw new ArgumentException("Image node is required.");
        }

        var job = _store.AddJob(runId, "describe", $"Queued describe for {request.ImageNodeId}.");
        _jobs[job.Id] = job;
        _log.Info("describe", $"Queued job {job.Id} for run {runId}, image node {request.ImageNodeId}, describer {request.DescriberId}.");
        _ = Task.Run(() => RunDescribeJobAsync(job, request));
        return job;
    }

    private async Task RunDescribeJobAsync(WorkflowJob job, DescribeRequest request)
    {
        try
        {
            job.Status = "running";
            PersistJob(job);

            var imageNode = _store.GetNode(job.RunId, request.ImageNodeId);
            if (imageNode.Type != "image" || string.IsNullOrWhiteSpace(imageNode.ImagePath))
            {
                throw new InvalidOperationException("Selected node is not an image node with an image path.");
            }

            var imagePath = _store.ResolveBrowserPath(imageNode.ImagePath);
            var imageBytes = await File.ReadAllBytesAsync(imagePath);
            var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? DefaultDescribePrompt : request.Prompt.Trim();
            var describer = CreateDescriber(request.DescriberId);

            job.Message = $"Describing {imageNode.Id} with {describer.GetModelName()}.";
            PersistJob(job);
            _log.Info("describe", $"Job {job.Id}: {job.Message}");

            var text = await describer.DescribeImageAsync(imageBytes, prompt, maxTokens: 1400, temperature: 0.2f);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Describer returned empty text.");
            }

            var nodeId = WorkflowStore.NewId("desc");
            var descriptionPath = Path.Combine(_store.GetDescriptionsPath(job.RunId), $"{nodeId}.txt");
            await File.WriteAllTextAsync(descriptionPath, text);
            var node = new WorkflowNode
            {
                Id = nodeId,
                Type = "description",
                Text = text,
                Provider = describer.GetModelName(),
                Meta =
                {
                    ["describerId"] = request.DescriberId,
                    ["prompt"] = prompt,
                    ["descriptionPath"] = _store.GetBrowserPath(descriptionPath),
                },
            };

            _store.AddNode(job.RunId, node, imageNode.Id, "described_by");
            job.NodeIds.Add(node.Id);
            job.Status = "done";
            job.Message = $"Done. Added description node {node.Id}.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            PersistJob(job);
            _log.Info("describe", $"Completed job {job.Id}: {job.Message}");
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Message = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            PersistJob(job);
            _log.Error("describe", $"Job {job.Id} failed", ex);
        }
    }

    private ILocalVisionModel CreateDescriber(string describerId)
    {
        return describerId.ToLowerInvariant() switch
        {
            "mock.visual" => new MockVisionDescriber(),
            "xai.grok-4.3" => new GrokVisionDescriber(_settings.XAIGrokApiKey, "grok-4.3"),
            "local.internvl" => new LocalInternVLClient(
                baseUrl: "http://127.0.0.1:11415",
                temperature: 0.2f,
                topP: 0.9f,
                topK: 50,
                repetitionPenalty: 1.1f,
                doSample: false),
            _ => new LocalQwenClient(temperature: 0.2f),
        };
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

    private sealed class MockVisionDescriber : ILocalVisionModel
    {
        public string GetModelName() => "mock.visual";

        public Task<string> DescribeImageAsync(byte[] imageBytes, string prompt, int maxTokens = 512, float temperature = 0.8f)
        {
            var text = string.Join(Environment.NewLine, new[]
            {
                "Mock visual description for fast workflow testing.",
                $"Image byte count: {imageBytes.Length}.",
                "The image appears as a simple local test graphic generated by the Workflow Mock provider.",
                "Use this node to test remix and repeat-generation graph behavior without paid API calls.",
            });
            return Task.FromResult(text);
        }
    }
}
