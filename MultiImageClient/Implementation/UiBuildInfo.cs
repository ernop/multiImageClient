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
        // The public repository this code lives in; the UI links the running
        // build's hash straight to its commit page.
        private const string CommitUrlBase = "https://github.com/ernop/multiImageClient/commit/";

        public static string Commit { get; }
        public static string CommitDate { get; }

        /// Link to the exact commit in the public repo, or null when the
        /// build carries no git identity (untracked-build).
        public static string? CommitUrl { get; }

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

            // git describe may append "-dirty"; the link targets the hash
            // itself (the page then can't show uncommitted local edits, which
            // is exactly what the visible -dirty suffix warns about).
            var hash = Commit.EndsWith("-dirty", System.StringComparison.Ordinal)
                ? Commit[..^"-dirty".Length]
                : Commit;
            CommitUrl = hash.Length > 0 && hash.All(System.Uri.IsHexDigit)
                ? CommitUrlBase + hash
                : null;
        }

        public static string Describe =>
            CommitDate.Length > 0 ? $"{Commit} ({CommitDate})" : Commit;
    }
}
