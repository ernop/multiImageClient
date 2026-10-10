#nullable enable
using System;
using System.Linq;

namespace MultiImageClient
{
    // Owner decision 2026-10-10: Ideogram 4.5 joins every saved default selection
    // list once, and a later removal stays removed. Each instance migrates its own
    // account rows. Only the writer of an environment list migrates that list.
    // See docs/ideogram-45-support.md.
    public partial class UiWorkflow
    {
        public const string Ideogram45AccountDefaultsMigration = "2026-10-10-ideogram-v45-account-defaults";
        public const string Ideogram45EnvironmentDefaultsMigration = "2026-10-10-ideogram-v45-environment-defaults";

        public static int AddIdeogram45ToAccountDefaultsOnce(UiCommunityStore community) =>
            community.AddDefaultSelectedKeyOnce(
                Ideogram45AccountDefaultsMigration,
                UiJobRunner.KeyIdeogramV45,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // In registry mode the controller writes the registry. The login-link list
        // there has no editor, and older servers refuse to start when it holds an
        // unknown key, so it stays unchanged.
        public static int AddIdeogram45ToEnvironmentDefaultsOnce(
            Settings settings, UiCommunityStore community, UiEnvironmentRegistry? environments, UiLoginLinks? loginLinks)
        {
            if (environments != null ? !settings.UiEnvironmentController : loginLinks == null) return 0;
            if (community.MigrationApplied(Ideogram45EnvironmentDefaultsMigration)) return 0;
            var key = UiJobRunner.KeyIdeogramV45;
            var changed = 0;
            if (environments != null)
            {
                foreach (var environment in environments.Read().Environments)
                {
                    if (environment.DefaultGenerators == null
                        || environment.DefaultGenerators.Contains(key, StringComparer.Ordinal)) continue;
                    environment.DefaultGenerators.Add(key);
                    environments.Save(environment);
                    changed++;
                }
            }
            else
            {
                var defaults = loginLinks!.Defaults();
                if (defaults != null && !defaults.Contains(key, StringComparer.Ordinal))
                {
                    loginLinks.SetDefaults([.. defaults, key]);
                    changed++;
                }
            }
            community.RecordMigration(Ideogram45EnvironmentDefaultsMigration, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return changed;
        }
    }
}
