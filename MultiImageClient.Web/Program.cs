using Microsoft.Extensions.FileProviders;
using MultiImageClient;
using MultiImageClient.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5127");
builder.Services.AddSingleton(_ => Settings.LoadFromFile(ResolveSettingsPath()));
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<WorkflowLogService>();
builder.Services.AddSingleton<WorkflowGenerationService>();
builder.Services.AddSingleton<WorkflowDescriptionService>();
builder.Services.AddSingleton<WorkflowRemixService>();

var app = builder.Build();
var store = app.Services.GetRequiredService<WorkflowStore>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(store.RootPath),
    RequestPath = "/files",
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    port = 5127,
    time = DateTimeOffset.UtcNow,
}));

app.MapGet("/api/providers", (WorkflowGenerationService generation) => generation.GetProviders());
app.MapGet("/api/describers", (WorkflowDescriptionService descriptions) => descriptions.GetDescribers());
app.MapGet("/api/runs", (WorkflowStore workflowStore) => workflowStore.ListRuns());
app.MapGet("/api/runs/{runId}", (WorkflowStore workflowStore, string runId) => workflowStore.GetRun(runId));
app.MapGet("/api/logs", (WorkflowLogService log, int? count) => Results.Ok(new
{
    logPath = log.LogPath,
    entries = log.Tail(count ?? 200),
}));

app.MapPost("/api/runs", (WorkflowStore workflowStore, WorkflowLogService log, CreateRunRequest request) =>
{
    var run = workflowStore.CreateRun(request.Prompt, request.Title);
    log.Info("run", $"Created {run.Id}: {run.Title}");
    return Results.Created($"/api/runs/{run.Id}", run);
});

app.MapPost("/api/runs/{runId}/generate", (WorkflowGenerationService generation, WorkflowLogService log, string runId, GenerateRequest request) =>
{
    var job = generation.StartGenerateJob(runId, request);
    log.Info("job", $"Accepted {job.Type} job {job.Id} for run {runId}");
    return Results.Accepted($"/api/jobs/{job.Id}", job);
});

app.MapPost("/api/runs/{runId}/describe", (WorkflowDescriptionService descriptions, WorkflowLogService log, string runId, DescribeRequest request) =>
{
    var job = descriptions.StartDescribeJob(runId, request);
    log.Info("job", $"Accepted {job.Type} job {job.Id} for run {runId}");
    return Results.Accepted($"/api/jobs/{job.Id}", job);
});

app.MapPost("/api/runs/{runId}/remix", (WorkflowRemixService remix, string runId, RemixRequest request) =>
{
    var node = remix.CreateRemixNode(runId, request);
    return Results.Created($"/api/runs/{runId}", node);
});

app.MapGet("/api/jobs", (WorkflowGenerationService generation) => generation.ListJobs());
app.MapGet("/api/jobs/{jobId}", (WorkflowGenerationService generation, string jobId) =>
{
    var job = generation.GetJob(jobId);
    return job == null ? Results.NotFound() : Results.Ok(job);
});

app.Run();

static string ResolveSettingsPath()
{
    var candidates = new[]
    {
        "settings.json",
        Path.Combine("MultiImageClient", "settings.json"),
        Path.Combine(AppContext.BaseDirectory, "settings.json"),
    };

    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return "settings.json";
}
