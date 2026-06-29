using System.Collections.Concurrent;

namespace MultiImageClient.Web;

public sealed class WorkflowLogService
{
    private const int MaxBufferedEntries = 500;
    private readonly object _fileGate = new();
    private readonly ConcurrentQueue<WorkflowLogEntry> _entries = new();

    public WorkflowLogService(WorkflowStore store)
    {
        LogPath = Path.Combine(store.RootPath, "workflow-lab.log");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Info("server", $"Workflow log started. File: {LogPath}");
    }

    public string LogPath { get; }

    public IReadOnlyList<WorkflowLogEntry> Tail(int count = 200)
    {
        count = Math.Clamp(count, 1, MaxBufferedEntries);
        return _entries.TakeLast(count).ToList();
    }

    public void Info(string scope, string message) => Write("info", scope, message);

    public void Error(string scope, string message, Exception? exception = null)
    {
        var fullMessage = exception == null ? message : $"{message}: {exception}";
        Write("error", scope, fullMessage);
    }

    private void Write(string level, string scope, string message)
    {
        var entry = new WorkflowLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Scope = scope,
            Message = message,
        };
        _entries.Enqueue(entry);
        while (_entries.Count > MaxBufferedEntries && _entries.TryDequeue(out _))
        {
        }

        var line = $"{entry.Timestamp:O}\t{entry.Level}\t{entry.Scope}\t{entry.Message}{Environment.NewLine}";
        lock (_fileGate)
        {
            File.AppendAllText(LogPath, line);
        }
    }
}

public sealed class WorkflowLogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Message { get; init; } = "";
}
