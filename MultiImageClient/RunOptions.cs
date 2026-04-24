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

        /// If non-empty, overrides the prompt source: use this single
        /// prompt instead of reading from PromptFiles.
        public string OverridePrompt { get; set; } = "";

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

        /// If true, bypass every other workflow and run GrokShowcaseWorkflow:
        /// pull the first --limit prompts from the active prompt source, fire
        /// them at xAI Grok Imagine in parallel, save each, then compose one
        /// combined grid image and pop it open.
        public bool GrokShowcase { get; set; }

        /// Pair with --grok-showcase to route through grok-imagine-image-pro
        /// at 2k resolution instead of the standard grok-imagine-image at 1k.
        public bool GrokPro { get; set; }

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
                    case "--prompt":
                        o.OverridePrompt = args[++i];
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
                    case "--grok-showcase":
                        o.GrokShowcase = true;
                        break;
                    case "--grok-pro":
                        o.GrokPro = true;
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
            Console.WriteLine("  --prompt \"text\"   Use this prompt instead of reading from PromptFiles.");
            Console.WriteLine("  --backfill-dl     One-shot: mirror all images under ImageDownloadBaseFolder to C:\\dl and exit.");
            Console.WriteLine("  --fast            Use cheapest/fastest generator set (gpt-image-2 low 1024x1024). Good for smoke tests.");
            Console.WriteLine("  --quick-test      Like --fast plus: save every streamed partial PNG and open each one in the default viewer as it arrives. Still asks y/n/custom per prompt unless combined with --auto.");
            Console.WriteLine("  --repl            Interactive prompt-by-prompt REPL. Prompts fire asynchronously (up to --repl-concurrency at a time); NO viewer pops. Commands: :help :size :quality :gens :status :wait :edit :retry :quit.");
            Console.WriteLine("  --repl-size WxH       REPL session default size for gpt-image-2 (default 2048x2048). Change at runtime with :size WxH.");
            Console.WriteLine("  --repl-quality L      REPL session default quality: low|medium|high (default high). Change at runtime with :quality <L>.");
            Console.WriteLine("  --repl-moderation M   REPL session default moderation: auto|low (default low). Change at runtime with :moderation <M>.");
            Console.WriteLine("  --repl-concurrency N  Max prompts in flight simultaneously in REPL mode (default 5). Change at runtime with :concurrency N.");
            Console.WriteLine("  --grok-showcase       One-shot: take the first --limit prompts from the active prompt source (--prompt or PromptFiles), fire them at xAI Grok Imagine in parallel, and open a single combined grid image. Default --limit for this mode is 10.");
            Console.WriteLine("  --grok-pro            Pair with --grok-showcase to route through grok-imagine-image-pro at 2k resolution ($0.07/img, 30 rpm) instead of grok-imagine-image at 1k ($0.02/img, 300 rpm).");
        }
    }
}
