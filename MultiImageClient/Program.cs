//using GenerativeAI.Types.RagEngine;

using IdeogramAPIClient;



//using OpenAI.Images;

//using RecraftAPIClient;

using System;
//using System.Collections.Generic;
//using System.Diagnostics.Metrics;
//using System.Drawing.Printing;
//using System.Linq;
//using System.Reflection.Metadata.Ecma335;
//using System.Runtime;
//using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Program
    {
        static async Task Main(string[] args)
        {
            var options = RunOptions.Parse(args);

            // Global headless-by-default gate: nothing is popped open in the
            // system default viewer unless --open-images was passed (or implied
            // by --quick-test). Every viewer launch funnels through
            // ImageCombiner.OpenImageWithDefaultApplication, which honors this.
            ImageCombiner.ViewerPopupsEnabled = options.OpenImages;

            // Look for settings.json in the obvious places so `dotnet run`
            // works from either the repo root OR the MultiImageClient folder:
            //   1. current working directory (legacy: run from MultiImageClient\)
            //   2. CWD\MultiImageClient\settings.json (run from repo root)
            //   3. next to the exe (AppContext.BaseDirectory)
            // First one that exists wins. If none do, fall back to the legacy
            // path so the error message matches the old behavior.
            var settingsFilePath = ResolveSettingsPath();
            var settings = Settings.LoadFromFile(settingsFilePath);

            if (options.BackfillDl)
            {
                DlMirror.Backfill(settings.ImageDownloadBaseFolder, settings.FlatImageMirrorPath);
                return;
            }

            if (options.GrokSync)
            {
                await GrokArchive.SyncAsync(settings);
                return;
            }

            if (options.GrokExportPath != null)
            {
                await GrokArchive.ExportAsync(settings, options.GrokExportPath);
                return;
            }

            var concurrency = 1;
            var stats = new MultiClientRunStats();

            // REPL mode bypasses the usual prompt-source + workflow menu
            // entirely — prompts come from stdin one line at a time, fire
            // off as async tasks, and results are saved silently (no viewer
            // pops). See ReplWorkflow.cs for the full command set.
            if (options.Repl)
            {
                var repl = new ReplWorkflow(settings, stats, options);
                await repl.RunAsync();
                return;
            }

            AbstractPromptSource promptSource = !string.IsNullOrEmpty(options.OverridePrompt)
                ? new InlinePromptSource(settings, options.OverridePrompt)
                : !string.IsNullOrEmpty(options.PromptFilePath)
                    ? new PromptFileSource(settings, options.PromptFilePath)
                    : new ReadAllPromptsFromFile(settings, "");

            if (options.GrokVideoTest)
            {
                // Exercises text-to-video, grok-image-to-video, and
                // extend-video with one prompt; saves + ledgers every clip.
                var videoModes = new GrokVideoModesWorkflow();
                await videoModes.RunAsync(promptSource, settings);
                return;
            }

            if (options.AllProviders)
            {
                // One prompt -> one flagship generator per provider -> one
                // combined contact sheet. Keyless providers fail soft into
                // error cells, so this is also the cross-provider auth check.
                var allProviders = new AllProvidersShowcaseWorkflow();
                await allProviders.RunAsync(promptSource, settings, stats, options);
                return;
            }

            if (options.ProviderSampleShowcase)
            {
                var providerSample = new ProviderSampleShowcaseWorkflow();
                await providerSample.RunAsync(settings, stats, options);
                return;
            }

            if (options.GrokShowcase)
            {
                // --limit defaults to int.MaxValue; clamp to 10 for the showcase
                // so the grid stays readable and the cheap tier stays ~$0.20.
                var showcaseLimit = options.Limit == int.MaxValue ? 10 : options.Limit;
                var showcase = new GrokShowcaseWorkflow();
                await showcase.RunAsync(promptSource, settings, stats, pro: options.GrokPro, limit: showcaseLimit);
                return;
            }

            int workflow = options.Workflow;
            if (workflow == 0)
            {
                while (workflow == 0)
                {
                    Console.WriteLine($"What do you want to do: \n\n1. Batch Workflow (make a bunch images for each prompt you choose or write yourself)\r\n2. Image2desc2image take an image, then describe it, then batch that out into a bunch of images again.\r\nq. quit");
                    var line = Console.ReadLine();
                    if (line is null)
                    {
                        Console.WriteLine("stdin closed, exiting.");
                        return;
                    }
                    var val = line.Trim();
                    if (val == "1") workflow = 1;
                    else if (val == "2") workflow = 2;
                    else if (val.Equals("q", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("quitting.");
                        return;
                    }
                    else Console.WriteLine("not recognized.");
                }
            }

            if (workflow == 1)
            {
                var bw = new BatchWorkflow();
                await bw.RunAsync(promptSource, settings, concurrency, stats, options);
            }
            else if (workflow == 2)
            {
                var rw = new RoundTripWorkflow();
                await rw.RunAsync(settings, concurrency, stats, options.InputImagePath);
            }
        }

        // Searches the obvious places for `settings.json`. Returns "settings.json"
        // (i.e. relative to CWD, the legacy path) if none of the candidates exist,
        // which preserves the old error text for anyone used to it.
        private static string ResolveSettingsPath()
        {
            var candidates = new[]
            {
                "settings.json",
                System.IO.Path.Combine("MultiImageClient", "settings.json"),
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "settings.json"),
            };
            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) return c;
            }
            return "settings.json";
        }
    }
}
