using System.Runtime.CompilerServices;

namespace MultiImageClient.Tests;

internal static class TestHost
{
    // Each configuration reload watcher holds one of the user's few inotify instances (often 128),
    // and parallel test hosts exhausted them. Test hosts never reload configuration.
    [ModuleInitializer]
    internal static void DisableConfigurationReload() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
