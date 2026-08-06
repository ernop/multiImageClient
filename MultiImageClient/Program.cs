//using GenerativeAI.Types.RagEngine;

using IdeogramAPIClient;



//using OpenAI.Images;

//using RecraftAPIClient;

using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
//using System.Collections.Generic;
//using System.Diagnostics.Metrics;
//using System.Drawing.Printing;
//using System.Linq;
//using System.Reflection.Metadata.Ecma335;
//using System.Runtime;
//using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{

    public class Program
    {
        static async Task Main(string[] args)
        {
            // Production publishes carry the Playwright revision they were
            // built against. Point Playwright at that immutable payload before
            // either the Grok or Meta browser client is initialized.
            var bundledPlaywrightBrowsers = Path.Combine(
                AppContext.BaseDirectory,
                ".playwright-browsers");
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"))
                && Directory.Exists(bundledPlaywrightBrowsers))
            {
                Environment.SetEnvironmentVariable(
                    "PLAYWRIGHT_BROWSERS_PATH",
                    bundledPlaywrightBrowsers);
            }

            var options = RunOptions.Parse(args);

            if (options.GrokWebStatsigSelfTest)
            {
                GrokWebStatsigSigner.RunSelfTest();
                Console.WriteLine("grok-web-statsig-self-test: PASS");
                return;
            }

            if (options.Ui)
            {
                // The shared-site daemon repeatedly decodes large originals to
                // build annotations, grids, and thumbs. ImageSharp's 64-bit
                // default permits a very large process-wide retained pool,
                // which drove the 1.2 GiB service into reclaim thrash after
                // browsing only ~50 jobs. Keep enough reuse for throughput
                // while bounding the resident pool for the long-lived server.
                Configuration.Default.MemoryAllocator = MemoryAllocator.Create(
                    new MemoryAllocatorOptions
                    {
                        MaximumPoolSizeMegabytes = 64,
                        AllocationLimitMegabytes = 512,
                    });
            }

            if (options.PlaywrightInstall)
            {
                // One-time browser download for --meta-web and grok-web video.
                // Exits with the installer's own status; no settings needed.
                Environment.Exit(Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            }

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
            GenerationArchive.Initialize(settings);

            if (options.GrokWebCaptureStatsig)
            {
                await GrokWebStatsigCapture.RunAsync(
                    settings,
                    options,
                    settingsFilePath,
                    CancellationToken.None);
                return;
            }

            if (options.BackfillDl)
            {
                DlMirror.Backfill(settings.ImageDownloadBaseFolder, settings.FlatImageMirrorPath);
                return;
            }

            if (options.B2Smoke)
            {
                if (!settings.EnableB2ImageHosting)
                {
                    Console.Error.WriteLine("--b2-smoke requires EnableB2ImageHosting=true plus the B2* settings in settings.json. See docs/b2-image-hosting-plan.md.");
                    Environment.Exit(2);
                }
                await new B2StorageClient(settings).RunSmokeTestAsync(CancellationToken.None);
                Console.WriteLine("b2-smoke: PASS (details in log)");
                return;
            }

            if (options.GrokApiSync)
            {
                await GrokArchive.SyncAsync(settings);
                return;
            }

            if (options.GrokApiExportPath != null)
            {
                await GrokArchive.ExportAsync(settings, options.GrokApiExportPath);
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

            // Local web UI: Kestrel on 127.0.0.1, browser front door onto the
            // same generators + ImageManager pipeline. Runs until Ctrl-C.
            if (options.Ui)
            {
                var ui = new UiWorkflow();
                await ui.RunAsync(settings, stats, options);
                return;
            }

            AbstractPromptSource promptSource = !string.IsNullOrEmpty(options.OverridePrompt)
                ? new InlinePromptSource(settings, options.OverridePrompt)
                : !string.IsNullOrEmpty(options.PromptFilePath)
                    ? new PromptFileSource(settings, options.PromptFilePath)
                    : new ReadAllPromptsFromFile(settings, "");

            if (options.Showcase)
            {
                // Generic contact-sheet one-shot: all prompts from the active
                // prompt source through the generators picked by --gens (or
                // the standard batch set), one sheet per generator.
                var showcaseWorkflow = new ShowcaseWorkflow();
                await showcaseWorkflow.RunAsync(promptSource, settings, stats, options);
                return;
            }

            if (options.GrokApiEdit)
            {
                var grokEdit = new GrokEditWorkflow();
                await grokEdit.RunAsync(promptSource, settings, stats, options);
                return;
            }

            if (options.GrokWeb)
            {
                var grokWeb = new GrokWebWorkflow();
                await grokWeb.RunAsync(promptSource, settings, stats, options);
                return;
            }

            if (options.MetaWeb)
            {
                var metaWeb = new MetaWebWorkflow();
                await metaWeb.RunAsync(promptSource, settings, stats, options);
                return;
            }

            if (options.GrokApiVideoTest)
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

            if (options.GrokApiShowcase)
            {
                // --limit defaults to int.MaxValue; clamp to 10 for the showcase
                // so the grid stays readable and the cheap tier stays ~$0.20.
                var showcaseLimit = options.Limit == int.MaxValue ? 10 : options.Limit;
                var showcase = new GrokShowcaseWorkflow();
                await showcase.RunAsync(promptSource, settings, stats, pro: options.GrokApiPro, limit: showcaseLimit);
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

        // An explicit environment path is useful for locked-down services
        // whose code, configuration, and writable data live in separate
        // directories. If declared, it wins and remains fail-closed: a missing
        // file is returned to Settings.LoadFromFile, which raises the normal
        // configuration error instead of silently selecting another file.
        //
        // Without it, search the obvious places for `settings.json`. Returns
        // "settings.json" (relative to CWD, the legacy path) when none exist.
        private static string ResolveSettingsPath()
        {
            var configuredPath = System.Environment.GetEnvironmentVariable(
                "MULTIIMAGECLIENT_SETTINGS");
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return System.IO.Path.GetFullPath(
                    Settings.ExpandPath(configuredPath.Trim()));
            }

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
