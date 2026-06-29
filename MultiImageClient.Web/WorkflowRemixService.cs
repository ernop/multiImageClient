namespace MultiImageClient.Web;

public sealed class WorkflowRemixService
{
    private readonly WorkflowStore _store;
    private readonly WorkflowLogService _log;

    public WorkflowRemixService(WorkflowStore store, WorkflowLogService log)
    {
        _store = store;
        _log = log;
    }

    public WorkflowNode CreateRemixNode(string runId, RemixRequest request)
    {
        var run = _store.GetRun(runId);
        var sourceNode = string.IsNullOrWhiteSpace(request.SourceNodeId)
            ? run.Nodes.FirstOrDefault(n => n.Type == "prompt")
            : run.Nodes.FirstOrDefault(n => n.Id == request.SourceNodeId);
        if (sourceNode == null)
        {
            throw new InvalidOperationException("Source node not found.");
        }

        var descriptionNode = string.IsNullOrWhiteSpace(request.DescriptionNodeId)
            ? null
            : run.Nodes.FirstOrDefault(n => n.Id == request.DescriptionNodeId);

        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? BuildPrompt(run.OriginalPrompt, descriptionNode?.Text ?? "", request.Instructions)
            : request.Prompt.Trim();

        var node = new WorkflowNode
        {
            Id = WorkflowStore.NewId("prompt"),
            Type = "prompt",
            Prompt = prompt,
            Text = prompt,
            Provider = "remix composer",
            Meta =
            {
                ["sourceNodeId"] = sourceNode.Id,
                ["descriptionNodeId"] = descriptionNode?.Id ?? "",
                ["instructions"] = request.Instructions,
                ["kind"] = "remix",
            },
        };

        _store.AddNode(runId, node, sourceNode.Id, "remix_prompt");
        if (descriptionNode != null)
        {
            _store.UpdateRun(runId, r => r.Edges.Add(new WorkflowEdge
            {
                From = descriptionNode.Id,
                To = node.Id,
                Kind = "description_input",
            }));
        }

        _log.Info("remix", $"Created remix prompt node {node.Id} in run {runId}.");
        return node;
    }

    private static string BuildPrompt(string originalPrompt, string description, string instructions)
    {
        var parts = new List<string>
        {
            "Original intent:",
            originalPrompt.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add("");
            parts.Add("Visual description of the selected image:");
            parts.Add(description.Trim());
        }

        parts.Add("");
        parts.Add("Generate a new image that preserves the strongest visual qualities of the selected image while respecting the original intent.");
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            parts.Add("");
            parts.Add("Additional user instructions:");
            parts.Add(instructions.Trim());
        }

        return string.Join(Environment.NewLine, parts);
    }
}
