using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiImageClient;

namespace MultiImageClient.Tests;

public class UiHostedRawCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mic-cleanup-test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] bytes = [1, 4, 9, 16];
    private Settings Settings => new() { ImageDownloadBaseFolder = root, EnableB2ImageHosting = true, B2KeepLocalRawImages = false };

    private (string Folder, string Path) Job(string id, bool done = true, string? input = null)
    {
        var folder = Path.Combine(root, "UiHistory", id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "original.png");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(folder, "job.json"), JsonSerializer.Serialize(new
        {
            Id = id,
            Done = done,
            InputImagePaths = input == null ? Array.Empty<string>() : new[] { input }
        }));
        Record(folder, path);
        File.WriteAllText(Path.Combine(folder, "events.jsonl"), "{\"type\":\"job-done\",\"interrupted\":true}\n");
        return (folder, path);
    }

    private void Record(string folder, string path, string? sha = null, string fileId = "version-1")
    {
        File.WriteAllText(Path.Combine(folder, "images.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gpt2/0"] = new
            {
                Path = path,
                ContentType = "image/png",
                ContentSha256 = sha ?? Convert.ToHexString(SHA256.HashData(bytes)),
                CdnKey = "ui/exact-object.png",
                CdnFileId = fileId
            }
        }));
    }

    [Fact]
    public async Task CleanupVerifiesExactBytesAndPreservesHistory()
    {
        var job = Job("one");
        var history = File.ReadAllBytes(Path.Combine(job.Folder, "events.jsonl"));
        var index = File.ReadAllBytes(Path.Combine(job.Folder, "images.json"));
        var called = 0;
        var result = await UiHostedRawCleanup.SweepAsync(Settings, false, default, (info, length, _) =>
        {
            Assert.Equal("version-1", info.CdnFileId);
            Assert.Equal("ui/exact-object.png", info.CdnKey);
            Assert.Equal(bytes.Length, length);
            called++;
            return Task.CompletedTask;
        });
        Assert.Equal((1, 4L, 0), result);
        Assert.Equal(1, called);
        Assert.False(File.Exists(job.Path));
        Assert.Equal(history, File.ReadAllBytes(Path.Combine(job.Folder, "events.jsonl")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(job.Folder, "images.json")));
        Assert.Equal((0, 0L, 0), await UiHostedRawCleanup.SweepAsync(Settings, false, default));
    }

    [Fact]
    public async Task DryRunVerifiesWithoutDeleting()
    {
        var job = Job("one");
        var result = await UiHostedRawCleanup.SweepAsync(Settings, true, default, (_, _, _) => Task.CompletedTask);
        Assert.Equal(1, result.Files);
        Assert.True(File.Exists(job.Path));
    }

    [Fact]
    public async Task FailedRemoteVerificationRetainsOriginal()
    {
        var job = Job("one");
        var result = await UiHostedRawCleanup.SweepAsync(Settings, false, default,
            (_, _, _) => throw new InvalidDataException("remote mismatch"));
        Assert.Equal((0, 0L, 1), result);
        Assert.True(File.Exists(job.Path));
    }

    [Fact]
    public async Task ChangedRecordAfterVerificationRetainsOriginal()
    {
        var job = Job("one");
        var result = await UiHostedRawCleanup.SweepAsync(Settings, false, default, (_, _, _) =>
        {
            Record(job.Folder, job.Path, fileId: "version-2");
            return Task.CompletedTask;
        });
        Assert.Equal(1, result.Errors);
        Assert.True(File.Exists(job.Path));
    }

    [Fact]
    public async Task RunningJobsAndSharedInputFilesArePreserved()
    {
        var source = Job("source");
        var active = Job("active", done: false, input: source.Path);
        var result = await UiHostedRawCleanup.SweepAsync(Settings, false, default,
            (_, _, _) => throw new Exception("No verification should run"));
        Assert.Equal((0, 0L, 0), result);
        Assert.True(File.Exists(source.Path));
        Assert.True(File.Exists(active.Path));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task DevelopmentAndNonHostedInstallsKeepOriginals(bool enabled, bool keep)
    {
        var job = Job("one");
        var settings = Settings;
        settings.EnableB2ImageHosting = enabled;
        settings.B2KeepLocalRawImages = keep;
        Assert.Equal((0, 0L, 0), await UiHostedRawCleanup.SweepAsync(settings, false, default));
        Assert.True(File.Exists(job.Path));
    }

    [Theory]
    [InlineData("invalid", "version-1")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "version-1")]
    [InlineData(null, "")]
    public async Task IncompleteIdentityOrWrongLocalHashPreventsDownloadAndDeletion(string? sha, string fileId)
    {
        var job = Job("one");
        Record(job.Folder, job.Path, sha, fileId);
        var called = false;
        var result = await UiHostedRawCleanup.SweepAsync(Settings, false, default, (_, _, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });
        Assert.Equal(1, result.Errors);
        Assert.False(called);
        Assert.True(File.Exists(job.Path));
    }

    [Fact]
    public async Task FileOutsideDataRootIsPreserved()
    {
        var job = Job("one");
        var outside = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(outside, bytes);
            Record(job.Folder, outside);
            Assert.Equal(1, (await UiHostedRawCleanup.SweepAsync(Settings, false, default)).Errors);
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task SymbolicLinkOriginalIsPreserved()
    {
        if (OperatingSystem.IsWindows()) return;
        var job = Job("one");
        var target = Path.Combine(job.Folder, "target.png");
        File.Move(job.Path, target);
        File.CreateSymbolicLink(job.Path, target);
        Assert.Equal(1, (await UiHostedRawCleanup.SweepAsync(Settings, false, default)).Errors);
        Assert.True(File.Exists(target));
    }

    [Theory]
    [InlineData("version-1", false, 200, true)]
    [InlineData("different-version", false, 200, false)]
    [InlineData("version-1", true, 200, false)]
    [InlineData("version-1", false, 404, false)]
    public async Task RemoteObjectVersionAndBytesControlDeletion(string version, bool corrupt, int status, bool deleted)
    {
        var job = Job("one");
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serving = Task.Run(async () =>
        {
            using var connection = await server.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            var body = corrupt ? new byte[] { 9, 9, 9, 9 } : bytes;
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Length: {body.Length}\r\nX-Bz-File-Id: {version}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }, timeout.Token);
        var settings = Settings;
        settings.B2DownloadBaseUrl = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
        var result = await UiHostedRawCleanup.SweepAsync(settings, false, timeout.Token);
        await serving;
        Assert.Equal(deleted ? 1 : 0, result.Files);
        Assert.Equal(!deleted, File.Exists(job.Path));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
