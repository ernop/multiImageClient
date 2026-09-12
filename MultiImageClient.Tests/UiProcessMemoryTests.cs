using System.Reflection;
using System.Text.Json;

namespace MultiImageClient;

public sealed class UiProcessMemoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mic-memory-" + Guid.NewGuid().ToString("N"));

    public UiProcessMemoryTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "system.slice", "multiimageclient-ui.service"));
    }

    [Theory]
    [InlineData("0::/user.slice/local-ui", "user.slice/local-ui")]
    [InlineData("0::/system.slice/multiimageclient-ui.service", "system.slice/multiimageclient-ui.service")]
    [InlineData("0::/", null)]
    [InlineData("0::/missing", null)]
    [InlineData("0::/../system.slice/multiimageclient-ui.service", null)]
    [InlineData("0::system.slice/multiimageclient-ui.service", null)]
    [InlineData("1:memory:/system.slice/multiimageclient-ui.service", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CgroupMemoryUsesOnlyThisProcessMembership(string? membership, string? expected)
    {
        Directory.CreateDirectory(Path.Combine(root, "user.slice", "local-ui"));
        var membershipFile = Path.Combine(root, "membership");
        if (membership != null) File.WriteAllText(membershipFile, membership);
        var resolve = typeof(UiProcessMemory).GetMethod("ResolveProcessCgroupDir", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = resolve.Invoke(null, [membershipFile, root]);
        Assert.Equal(expected == null ? null : Path.Combine(root, expected), actual);
    }

    [Fact]
    public void SnapshotIdentifiesTheRunningProcessAndRuntime()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(UiProcessMemory.Snapshot()));
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(Environment.Version.ToString(), json.RootElement.GetProperty("runtimeVersion").GetString());
        Assert.True(json.RootElement.GetProperty("processStartedAtUtc").GetDateTime() <= DateTime.UtcNow);
        Assert.True(json.RootElement.GetProperty("workingSetBytes").GetInt64() > 0);
    }

    public void Dispose() => Directory.Delete(root, true);
}
