using System.Text.Json;

namespace MultiImageClient.Web;

public sealed class WorkflowStore
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public WorkflowStore(IWebHostEnvironment environment)
    {
        RootPath = Path.Combine(environment.ContentRootPath, "..", "saves", "workflow-lab");
        RunsPath = Path.Combine(RootPath, "runs");
        Directory.CreateDirectory(RunsPath);
    }

    public string RootPath { get; }
    public string RunsPath { get; }

    public IReadOnlyList<WorkflowRun> ListRuns()
    {
        lock (_gate)
        {
            return Directory.GetDirectories(RunsPath)
                .Select(dir => Path.Combine(dir, "run.json"))
                .Where(File.Exists)
                .Select(ReadRunFile)
                .OrderByDescending(run => run.UpdatedAt)
                .ToList();
        }
    }

    public WorkflowRun CreateRun(string prompt, string title)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var id = NewId("run");
        var promptNodeId = NewId("prompt");
        var run = new WorkflowRun
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? prompt.Trim().Truncate(80) : title.Trim(),
            OriginalPrompt = prompt.Trim(),
            Nodes =
            {
                new WorkflowNode
                {
                    Id = promptNodeId,
                    Type = "prompt",
                    Prompt = prompt.Trim(),
                    Text = prompt.Trim(),
                },
            },
        };

        lock (_gate)
        {
            Directory.CreateDirectory(GetRunPath(id));
            Directory.CreateDirectory(GetImagesPath(id));
            SaveRunFile(run);
        }

        return run;
    }

    public WorkflowRun GetRun(string runId)
    {
        lock (_gate)
        {
            return ReadRunFile(GetRunFilePath(runId));
        }
    }

    public WorkflowRun UpdateRun(string runId, Action<WorkflowRun> update)
    {
        lock (_gate)
        {
            var run = ReadRunFile(GetRunFilePath(runId));
            update(run);
            run.UpdatedAt = DateTimeOffset.UtcNow;
            SaveRunFile(run);
            return run;
        }
    }

    public WorkflowJob AddJob(string runId, string type, string message)
    {
        var job = new WorkflowJob
        {
            Id = NewId("job"),
            RunId = runId,
            Type = type,
            Status = "queued",
            Message = message,
        };

        UpdateRun(runId, run => run.Jobs.Add(job));
        return job;
    }

    public WorkflowJob UpdateJob(string runId, string jobId, Action<WorkflowJob> update)
    {
        WorkflowJob? updated = null;
        UpdateRun(runId, run =>
        {
            updated = run.Jobs.FirstOrDefault(job => job.Id == jobId)
                ?? throw new InvalidOperationException($"Job not found: {jobId}");
            update(updated);
        });
        return updated!;
    }

    public WorkflowNode AddNode(string runId, WorkflowNode node, string parentNodeId, string edgeKind)
    {
        UpdateRun(runId, run =>
        {
            run.Nodes.Add(node);
            var from = parentNodeId;
            if (string.IsNullOrWhiteSpace(from))
            {
                from = run.Nodes.FirstOrDefault(n => n.Type == "prompt")?.Id ?? "";
            }

            if (!string.IsNullOrWhiteSpace(from))
            {
                run.Edges.Add(new WorkflowEdge { From = from, To = node.Id, Kind = edgeKind });
            }
        });
        return node;
    }

    public WorkflowNode GetNode(string runId, string nodeId)
    {
        return GetRun(runId).Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException($"Node not found: {nodeId}");
    }

    public string GetRunPath(string runId) => Path.Combine(RunsPath, runId);

    public string GetImagesPath(string runId)
    {
        var path = Path.Combine(GetRunPath(runId), "images");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetDescriptionsPath(string runId)
    {
        var path = Path.Combine(GetRunPath(runId), "descriptions");
        Directory.CreateDirectory(path);
        return path;
    }

    public string ResolveBrowserPath(string browserPath)
    {
        if (!browserPath.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported workflow file path: {browserPath}", nameof(browserPath));
        }

        var relative = Uri.UnescapeDataString(browserPath["/files/".Length..]).Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relative));
        var root = Path.GetFullPath(RootPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved path escaped workflow root.");
        }

        return fullPath;
    }

    public string GetBrowserPath(string absolutePath)
    {
        var relative = Path.GetRelativePath(RootPath, absolutePath).Replace('\\', '/');
        return "/files/" + Uri.EscapeDataString(relative).Replace("%2F", "/");
    }

    public static string NewId(string prefix) => $"{prefix}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 17 + 1 + 8, prefix.Length + 1 + 17 + 1 + 32)];

    private string GetRunFilePath(string runId) => Path.Combine(GetRunPath(runId), "run.json");

    private WorkflowRun ReadRunFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WorkflowRun>(json, _jsonOptions)
            ?? throw new InvalidOperationException($"Could not read workflow run: {path}");
    }

    private void SaveRunFile(WorkflowRun run)
    {
        Directory.CreateDirectory(GetRunPath(run.Id));
        File.WriteAllText(GetRunFilePath(run.Id), JsonSerializer.Serialize(run, _jsonOptions));
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].TrimEnd() + "...";
    }
}
