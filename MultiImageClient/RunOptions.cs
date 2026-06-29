#nullable enable
using System;
using System.Collections.Generic;

namespace MultiImageClient
{
    /// Command-line options for non-interactive runs. Parsed in
    /// Program.Main from args. Passing no args keeps the old fully
    /// interactive behavior.
    public class RunOptions
    {
        /// If true, skip the top-level workflow menu and every per-prompt
        /// confirmation; auto-accept everything.
        public bool Auto { get; set; }

        /// Max number of prompts to process. int.MaxValue = no cap.
        public int Limit { get; set; } = int.MaxValue;

        /// Max number of prompts allowed in flight for workflows that support
        /// prompt-level overlap. Provider-level semaphores still limit each
        /// backend independently.
        public int PromptConcurrency { get; set; } = 1;

        /// If non-empty, overrides the prompt source: use this single
        /// prompt instead of reading from PromptFiles.
        public string OverridePrompt { get; set; } = "";

        /// If non-empty, overrides the prompt source with a newline-delimited
        /// prompt file. Lines are processed in file order.
        public string PromptFilePath { get; set; } = "";

        /// Image file to feed into the round-trip workflow. When set, workflow
        /// 2 runs once against this file instead of asking for an image path.
        public string InputImagePath { get; set; } = "";

        /// 1 = Batch, 2 = RoundTrip, 0 = ask interactively.
        public int Workflow { get; set; }

        /// If true, mirror every image under Settings.ImageDownloadBaseFolder
        /// into C:\dl and exit. Does not run any workflow.
        public bool BackfillDl { get; set; }

        /// If true, use the smallest/cheapest/fastest generator set
        /// (gpt-image-2 low quality, 1024x1024 square, moderation=low).
        /// Intended for iteration/smoke-testing, not production runs.
        public bool Fast { get; set; }

        /// If true, use a single gpt-image-2 call per prompt configured for
        /// maximum interactive feedback: 1024x1024 low quality, moderation=low,
        /// n=1, and every streamed partial PNG is saved to disk and opened
        /// with the system default viewer the moment it arrives. Intended for
        /// human-in-the-loop development where seeing the generation refine
        /// in real time is the point.
        public bool QuickTest { get; set; }

        /// If true, start an interactive prompt-by-prompt REPL. Each line is
        /// either a `:command` or a prompt fired off asynchronously against
        /// the current active generator set. Up to ReplConcurrency prompts
        /// run in parallel; results are saved to disk as they arrive and
        /// NO images are popped open in the default viewer. See ReplWorkflow
        /// for the command list.
        public bool Repl { get; set; }

        /// Default gpt-image-2 size for REPL sessions. Can be changed at
        /// runtime via `:size WxH`. 2048x2048 matches the "large, high
        /// quality" iteration profile the REPL is designed for.
        public string ReplSize { get; set; } = "2048x2048";

        /// Default gpt-image-2 quality for REPL sessions. low | medium | high.
        /// Can be changed at runtime via `:quality <level>`.
        public string ReplQuality { get; set; } = "high";

        /// Default gpt-image-2 moderation for REPL sessions. auto | low.
        /// Can be changed at runtime via `:moderation <level>`.
        public string ReplModeration { get; set; } = "low";

        /// How many prompts can be in flight at once in REPL mode. Higher
        /// values let you fire prompts faster than the backend completes
        /// them; the REPL will queue beyond this limit.
        public int ReplConcurrency { get; set; } = 5;

        /// Default `n` (images per call) for the gpt-image-2 slot in REPL
        /// sessions. Useful for variant exploration (e.g. logo design). Can
        /// be changed at runtime via `:n N` or per-prompt via `[n=N] ...`.
        public int ReplImageCount { get; set; } = 1;

        /// If true, bypass every other workflow and run GrokShowcaseWorkflow:
        /// pull the first --limit prompts from the active prompt source, fire
        /// them at xAI Grok Imagine in parallel, save each, then compose one
        /// combined grid image and pop it open.
        public bool GrokShowcase { get; set; }

        /// Pair with --grok-showcase to route through grok-imagine-image-pro
        /// at 2k resolution instead of the standard grok-imagine-image at 1k.
        public bool GrokPro { get; set; }

        /// If true, run AllProvidersShowcaseWorkflow: take ONE prompt and
        /// fire it at one flagship generator per provider (gpt-image-2,
        /// Ideogram 4.0, flux-2-pro-preview, Recraft V4.1, Grok Imagine,
        /// Nano Banana Pro), then compose every result into a single
        /// contact-sheet grid and pop it open. Failed/keyless providers
        /// show as error cells, so this doubles as a key health check.
        public bool AllProviders { get; set; }

        /// Pair with --all-providers to ALSO dispatch a Grok Imagine video
        /// (grok-imagine-video, 6s 480p) for the same prompt. The mp4 is
        /// saved under the day folder's Video\ subfolder; videos are NOT
        /// composited into the PNG contact sheet (stills only).
        public bool WithVideo { get; set; }

        /// If non-null, run GrokArchive.ExportAsync and exit: sync the full
        /// Grok history, then copy every known image/video plus prompts.txt
        /// and the ledger into this folder (outside the repo). Defaults to
        /// C:\GrokArchive when --grok-export is passed without a path.
        public string? GrokExportPath { get; set; }

        /// If true, run GrokVideoModesWorkflow and exit: exercise all three
        /// Grok video request modes with one prompt — text-to-video,
        /// grok-image-to-video, and extend-video — saving each clip and
        /// recording everything in grok_ledger.jsonl.
        public bool GrokVideoTest { get; set; }

        /// If true, run GrokArchive.SyncAsync and exit: back-read the entire
        /// reachable Grok history (xAI Files API inventory + re-pollable
        /// video request_ids + local JSON logs) into grok_ledger.jsonl and
        /// download every asset we don't already have locally. Idempotent;
        /// run it whenever to keep local copies synced.
        public bool GrokSync { get; set; }

        /// If true, randomly sample prompts once, then run that same sample
        /// through the provider review set and create one contact sheet per
        /// provider. Defaults to 15 prompts when --limit is omitted.
        public bool ProviderSampleShowcase { get; set; }

        /// Optional saved prompt list for --provider-sample-showcase. Lines may
        /// be plain prompts or numbered as "1. prompt".
        public string ProviderSampleFilePath { get; set; } = "";

        /// Optional comma-separated provider filter for --provider-sample-showcase.
        /// Matches generator labels, e.g. "gpt-image-2" or "grok,recraft".
        public string ProviderSampleProviders { get; set; } = "";

        /// Extra attempts for failed prompts before composing the provider
        /// sample contact sheet. Zero means no retry.
        public int ProviderSampleRetryFailures { get; set; }

        /// Master switch for popping finished images/contact-sheets open in the
        /// system default viewer. Defaults to false: runs are headless and just
        /// save to disk. Set with --open-images. Drives
        /// ImageCombiner.ViewerPopupsEnabled, the single gate every viewer
        /// launch funnels through. --quick-test turns this on automatically
        /// since live partial viewing is the whole point of that mode.
        public bool OpenImages { get; set; }

        public static RunOptions Parse(string[] args)
        {
            var o = new RunOptions();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--auto":
                        o.Auto = true;
                        // --auto means "just run the default workflow, don't
                        // prompt me for anything". Default to Batch (1) unless
                        // --workflow was already passed.
                        if (o.Workflow == 0) o.Workflow = 1;
                        break;
                    case "--limit":
                        o.Limit = int.Parse(args[++i]);
                        break;
                    case "--prompt-concurrency":
                        o.PromptConcurrency = int.Parse(args[++i]);
                        if (o.PromptConcurrency < 1)
                        {
                            Console.Error.WriteLine($"--prompt-concurrency must be >= 1 (got {o.PromptConcurrency})");
                            Environment.Exit(2);
                        }
                        break;
                    case "--prompt":
                        o.OverridePrompt = args[++i];
                        break;
                    case "--prompt-file":
                        o.PromptFilePath = args[++i];
                        break;
                    case "--input-image":
                        o.InputImagePath = args[++i];
                        if (o.Workflow == 0) o.Workflow = 2;
                        break;
                    case "--workflow":
                        o.Workflow = int.Parse(args[++i]);
                        break;
                    case "--backfill-dl":
                        o.BackfillDl = true;
                        break;
                    case "--fast":
                        o.Fast = true;
                        break;
                    case "--quick-test":
                        o.QuickTest = true;
                        // Skip the workflow menu (there's only one thing
                        // quick-test does) but deliberately DO NOT force
                        // --auto: the user still wants the per-prompt
                        // y/n/custom loop for iterative work. Pair with
                        // --auto explicitly for fully unattended runs.
                        if (o.Workflow == 0) o.Workflow = 1;
                        // Watching partials refine live IS the point of
                        // quick-test, so opt into viewer popups automatically.
                        o.OpenImages = true;
                        break;
                    case "--open-images":
                        o.OpenImages = true;
                        break;
                    case "--repl":
                        o.Repl = true;
                        break;
                    case "--repl-size":
                        o.ReplSize = args[++i];
                        break;
                    case "--repl-quality":
                        o.ReplQuality = args[++i];
                        break;
                    case "--repl-moderation":
                        o.ReplModeration = args[++i];
                        break;
                    case "--repl-concurrency":
                        o.ReplConcurrency = int.Parse(args[++i]);
                        break;
                    case "--repl-n":
                        o.ReplImageCount = int.Parse(args[++i]);
                        if (o.ReplImageCount < 1)
                        {
                            Console.Error.WriteLine($"--repl-n must be >= 1 (got {o.ReplImageCount})");
                            Environment.Exit(2);
                        }
                        break;
                    case "--grok-showcase":
                        o.GrokShowcase = true;
                        break;
                    case "--grok-pro":
                        o.GrokPro = true;
                        break;
                    case "--all-providers":
                        o.AllProviders = true;
                        break;
                    case "--with-video":
                        o.WithVideo = true;
                        break;
                    case "--grok-sync":
                        o.GrokSync = true;
                        break;
                    case "--grok-export":
                        // Optional path argument; default to C:\GrokArchive.
                        o.GrokExportPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                            ? args[++i]
                            : @"C:\GrokArchive";
                        break;
                    case "--grok-video-test":
                        o.GrokVideoTest = true;
                        break;
                    case "--provider-sample-showcase":
                        o.ProviderSampleShowcase = true;
                        break;
                    case "--provider-sample-file":
                        o.ProviderSampleFilePath = args[++i];
                        break;
                    case "--provider-sample-providers":
                        o.ProviderSampleProviders = args[++i];
                        break;
                    case "--provider-sample-retry-failures":
                        o.ProviderSampleRetryFailures = int.Parse(args[++i]);
                        if (o.ProviderSampleRetryFailures < 0)
                        {
                            Console.Error.WriteLine($"--provider-sample-retry-failures must be >= 0 (got {o.ProviderSampleRetryFailures})");
                            Environment.Exit(2);
                        }
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        Environment.Exit(0);
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {a}");
                        PrintUsage();
                        Environment.Exit(2);
                        break;
                }
            }
            return o;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: MultiImageClient [--auto] [--workflow 1|2] [--limit N] [--prompt \"...\"]");
            Console.WriteLine("  --auto            Non-interactive: skip menu, auto-accept every prompt.");
            Console.WriteLine("  --workflow 1|2    1 = batch, 2 = round-trip. Default: ask.");
            Console.WriteLine("  --limit N         Stop after N prompts.");
            Console.WriteLine("  --prompt-concurrency N  Max prompts in flight where supported (all-providers).");
            Console.WriteLine("  --prompt \"text\"   Use this prompt instead of reading from PromptFiles.");
            Console.WriteLine("  --prompt-file fp  Use a newline-delimited prompt file instead of PromptFiles.");
            Console.WriteLine("  --input-image path  Use this image file for workflow 2 (round-trip image -> description -> images).");
            Console.WriteLine("  --backfill-dl     One-shot: mirror all images under ImageDownloadBaseFolder to C:\\dl and exit.");
            Console.WriteLine("  --fast            Use cheapest/fastest generator set (gpt-image-2 low 1024x1024). Good for smoke tests.");
            Console.WriteLine("  --open-images     Pop finished images/contact-sheets open in the system default viewer. OFF by default (runs are headless and just save to disk). --quick-test enables this automatically.");
            Console.WriteLine("  --quick-test      Like --fast plus: save every streamed partial PNG and open each one in the default viewer as it arrives (implies --open-images). Still asks y/n/custom per prompt unless combined with --auto.");
            Console.WriteLine("  --repl            Interactive prompt-by-prompt REPL. Prompts fire asynchronously (up to --repl-concurrency at a time); NO viewer pops. Commands: :help :size :quality :gens :status :wait :edit :retry :quit.");
            Console.WriteLine("  --repl-size WxH       REPL session default size for gpt-image-2 (default 2048x2048). Change at runtime with :size WxH.");
            Console.WriteLine("  --repl-quality L      REPL session default quality: low|medium|high (default high). Change at runtime with :quality <L>.");
            Console.WriteLine("  --repl-moderation M   REPL session default moderation: auto|low (default low). Change at runtime with :moderation <M>.");
            Console.WriteLine("  --repl-concurrency N  Max prompts in flight simultaneously in REPL mode (default 5). Change at runtime with :concurrency N.");
            Console.WriteLine("  --repl-n N            REPL session default n (images per gpt-image-2 call, default 1). Change at runtime with :n N, or per-prompt via [n=N] in the override prefix.");
            Console.WriteLine("  --grok-showcase       One-shot: take the first --limit prompts from the active prompt source (--prompt or PromptFiles), fire them at xAI Grok Imagine in parallel, and compose a single combined grid image (pops open only with --open-images). Default --limit for this mode is 10.");
            Console.WriteLine("  --grok-pro            Pair with --grok-showcase to route through grok-imagine-image-pro at 2k resolution ($0.07/img, 30 rpm) instead of grok-imagine-image at 1k ($0.02/img, 300 rpm).");
            Console.WriteLine("  --all-providers       One-shot: fire ONE prompt (--prompt or first PromptFiles line) at current image endpoints (gpt-image-2, gpt-image-1, gpt-image-1-mini, Ideogram 4.0, flux-2-pro-preview, Recraft V4.1, Grok Imagine, Nano Banana Pro) and compose a single contact-sheet grid (pops open only with --open-images). Keyless providers show as error cells.");
            Console.WriteLine("  --with-video          Pair with --all-providers to also dispatch a Grok Imagine VIDEO (6s, 480p) for the same prompt; the mp4 lands in the day folder's Video\\ subfolder. Videos are not composited into the PNG sheet.");
            Console.WriteLine("  --grok-video-test     One-shot: exercise all three Grok video modes with one prompt (--prompt or first PromptFiles line) — text-to-video, grok-image-to-video, and extend-video (3s, 480p each). Clips are saved, stored durably at xAI, and ledgered.");
            Console.WriteLine("  --provider-sample-showcase  One-shot: randomly sample --limit prompts (default 15), then make one contact sheet per provider: Grok, Recraft, BFL, Google, and gpt-image-2 low (pops open only with --open-images).");
            Console.WriteLine("  --provider-sample-file fp   Pair with --provider-sample-showcase to reuse a saved numbered/plain people-fixture prompt list.");
            Console.WriteLine("  --provider-sample-providers csv  Pair with --provider-sample-showcase to run only matching providers, e.g. gpt-image-2 or grok,recraft.");
            Console.WriteLine("  --provider-sample-retry-failures N  Pair with --provider-sample-showcase to retry failed prompt slots N extra times before composing the sheet.");
            Console.WriteLine("  --grok-export [path]  One-shot: full Grok history export OUTSIDE the repo. Runs --grok-sync first, then copies every known Grok image/video plus prompts.txt and grok_ledger.jsonl into [path] (default C:\\GrokArchive). Rerunnable; already-present files are skipped.");
            Console.WriteLine("  --grok-sync           One-shot: back-read/back-download the entire reachable Grok history and exit. Sweeps the xAI Files API inventory, re-polls any ledger video request_ids whose local file is missing, backfills prompts from old JSON logs, and writes everything to grok_ledger.jsonl + saves\\GrokArchive\\. Idempotent — run it whenever to stay synced.");
        }
    }
}
