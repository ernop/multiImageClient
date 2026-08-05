#nullable enable
using System.Linq;
using System.Reflection;

namespace MultiImageClient
{
    /// Build identity embedded at compile time by the EmbedGitBuildInfo
    /// csproj target, so a running instance can state exactly which commit
    /// it was built from. A build made outside git reports "untracked-build"
    /// rather than any guessed identity.
    public static class UiBuildInfo
    {
        public static string Commit { get; }
        public static string CommitDate { get; }

        static UiBuildInfo()
        {
            var asm = typeof(UiBuildInfo).Assembly;
            var informational = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "";
            var plus = informational.IndexOf('+');
            Commit = plus >= 0 && plus + 1 < informational.Length
                ? informational[(plus + 1)..]
                : "untracked-build";
            CommitDate = asm
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "MicCommitDate")?.Value ?? "";
        }

        public static string Describe =>
            CommitDate.Length > 0 ? $"{Commit} ({CommitDate})" : Commit;
    }
}
