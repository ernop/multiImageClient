using System.Text.Json.Serialization;

namespace MultiImageClient.Web;

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public string Title { get; set; } = "";
    public string OriginalPrompt { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkflowNode> Nodes { get; set; } = new();
    public List<WorkflowEdge> Edges { get; set; } = new();
    public List<WorkflowJob> Jobs { get; set; } = new();
}

public sealed class WorkflowNode
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "done";
    public string Prompt { get; set; } = "";
    public string Text { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, string> Meta { get; set; } = new();
}

public sealed class WorkflowEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string Kind { get; init; } = "";
}

public sealed class WorkflowJob
{
    public required string Id { get; init; }
    public required string RunId { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "queued";
    public string Message { get; set; } = "";
    public List<string> NodeIds { get; set; } = new();
}

public sealed class CreateRunRequest
{
    public string Prompt { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class GenerateRequest
{
    public string Prompt { get; set; } = "";
    public string ParentNodeId { get; set; } = "";
    public List<string> ProviderIds { get; set; } = new();
    public int Count { get; set; } = 1;
}

public sealed class DescribeRequest
{
    public string ImageNodeId { get; set; } = "";
    public string DescriberId { get; set; } = "local.qwen";
    public string Prompt { get; set; } = "";
}

public sealed class RemixRequest
{
    public string SourceNodeId { get; set; } = "";
    public string DescriptionNodeId { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string Prompt { get; set; } = "";
}

public sealed class ProviderOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string ApiType { get; init; }
    public decimal EstimatedCost { get; init; }
    public bool Enabled { get; init; }
    public string DisabledReason { get; init; } = "";
}

public sealed class DescriberOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool Enabled { get; init; }
    public string DisabledReason { get; init; } = "";
}

[JsonSerializable(typeof(WorkflowRun))]
[JsonSerializable(typeof(List<WorkflowRun>))]
[JsonSerializable(typeof(WorkflowJob))]
[JsonSerializable(typeof(List<WorkflowJob>))]
internal partial class WorkflowJsonContext : JsonSerializerContext;
