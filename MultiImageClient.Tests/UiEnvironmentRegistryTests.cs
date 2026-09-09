using System;
using System.IO;
using System.Text.Json;
using MultiImageClient;
using Xunit;

public sealed class UiEnvironmentRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mic-registry-" + Guid.NewGuid().ToString("N"));
    private readonly UiEnvironmentRegistry registry;
    public UiEnvironmentRegistryTests()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "registry.json");
        File.WriteAllText(path, """{"version":1,"environments":[{"id":"original","slug":"original-route","name":"Original","original":true,"members":["victor"]}]}""");
        registry = new(path);
    }
    [Fact] public void OnlyOwnerHasGlobalAccess()
    {
        registry.Save(new() { Id = "studio", Slug = "studio", Name = "Studio" });
        Assert.True(registry.CanEnter("studio", "ernieMultiZone"));
        Assert.False(registry.CanEnter("studio", "victor"));
        Assert.True(registry.CanEnter("original", "victor"));
    }
    [Fact] public void RemovingMembershipImmediatelyRevokesEnvironmentAccess()
    {
        var env = registry.Get("original"); env.Members.Clear(); registry.Save(env);
        Assert.False(registry.CanEnter("original", "victor"));
    }
    [Fact] public void NamesAndFeaturesPersistWithoutChangingIdentity()
    {
        var env = registry.Get("original"); env.Name = "Ernie Austin Victor"; env.GoalLoops = false; registry.Save(env);
        Assert.False(registry.Get("original").GoalLoops); Assert.Equal("Ernie Austin Victor", registry.Get("original").Name);
    }
    [Fact] public void OriginalRouteCannotBeRenamed()
    {
        var env = registry.Get("original"); env.Slug = "changed";
        Assert.Throws<InvalidDataException>(() => registry.Save(env));
    }
    [Fact] public void VibecodersIntegrationPreservesOriginalAndRequiresNewEnvironmentOptIn()
    {
        Assert.True(registry.Get("original").AllowVibecoders);
        registry.Save(new() { Id = "studio", Slug = "studio", Name = "Studio" });
        var env = registry.Get("studio"); Assert.False(env.AllowVibecoders);
        env.VibecodersSharing = true; registry.Save(env); Assert.True(registry.Get("studio").AllowVibecoders);
        env.VibecodersSharing = false; registry.Save(env); Assert.False(registry.Get("studio").AllowVibecoders);
    }
    [Theory] [InlineData("../escape")] [InlineData("original-route")]
    public void InvalidOrDuplicateRoutesFailClosed(string slug)
    { Assert.Throws<InvalidDataException>(() => registry.Save(new() { Id = "other", Slug = slug, Name = "Other" })); }
    [Fact] public void ActivityDistinguishesObservedVisitFromRecordedLogin()
    {
        var activity = new UiAccountActivity(root); activity.Record("victor");
        Assert.Null(activity.Snapshot()["victor"].FirstLogin);
        activity.Record("victor", true);
        Assert.NotNull(new UiAccountActivity(root).Snapshot()["victor"].FirstLogin);
    }
    public void Dispose() => Directory.Delete(root, true);
}
