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

        /// If true, run the Backblaze B2 hosting smoke test and exit: upload
        /// random bytes, fetch them back anonymously through the public
        /// bucket URL, byte-compare, verify a random wrong key 404s, delete.
        /// Requires EnableB2ImageHosting + the B2* settings.
        public bool B2Smoke { get; set; }

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

        /// If true, run ShowcaseWorkflow: pull prompts from the active prompt
        /// source (--prompt / --prompt-file / PromptFiles, honoring --limit,
        /// no default cap), run them through the selected generators, and
        /// compose one contact sheet per generator. Generators come from
        /// --gens; without --gens it uses the standard batch set
        /// (GeneratorGroups.GetAll — "whatever models we're currently using").
        public bool Showcase { get; set; }

        /// Comma-separated generator short names for --showcase. Same
        /// vocabulary as the REPL plus grok-web. The complete current list is
        /// GeneratorGroups.ShortNames. grok-web honors the --grok-web-* flags
        /// (cookies, aspect ratio, pro/fast tier).
        public string Gens { get; set; } = "";

        // NAMING RULE (user-facing surface): "grok-api" = the official
        // api.x.ai API-key version (public, GDPR-suitable ruleset);
        // "grok-web" = the consumer grok.com cookie-session version
        // (American web-app ruleset). Internal type names keep the
        // Grok* / GrokWeb* prefixes.

        /// If true, bypass every other workflow and run GrokShowcaseWorkflow:
        /// pull the first --limit prompts from the active prompt source, fire
        /// them at xAI Grok Imagine (grok-api) in parallel, save each, then
        /// compose one combined grid image and pop it open.
        public bool GrokApiShowcase { get; set; }

        /// Pair with --grok-api-showcase to route through grok-imagine-image-pro
        /// at 2k resolution instead of the standard grok-imagine-image at 1k.
        public bool GrokApiPro { get; set; }

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
        /// grok-api history, then copy every known image/video plus prompts.txt
        /// and the ledger into this folder (outside the repo). Defaults to
        /// C:\GrokArchive when --grok-api-export is passed without a path.
        public string? GrokApiExportPath { get; set; }

        /// If true, run GrokVideoModesWorkflow and exit: exercise all three
        /// grok-api video request modes with one prompt — text-to-video,
        /// grok-image-to-video, and extend-video — saving each clip and
        /// recording everything in grok_ledger.jsonl.
        public bool GrokApiVideoTest { get; set; }

        /// If true, run one grok-api image edit and exit. Uses --input-image
        /// as the source image and --prompt (or first prompt source entry) as
        /// edit instructions.
        public bool GrokApiEdit { get; set; }

        /// Optional aspect ratio override for --grok-api-edit. Empty means let
        /// xAI inherit the source image aspect ratio.
        public string GrokApiEditAspectRatio { get; set; } = "";

        /// If true, run GrokWebWorkflow: batch prompts through consumer
        /// grok.com session endpoints (browser cookies), not api.x.ai.
        public bool GrokWeb { get; set; }

        /// Tier for --grok-web image mode: the web app's only quality knob is
        /// its pro toggle (enable_pro on the wire). Defaults to pro/quality;
        /// pass --grok-web-fast to opt into the fast tier, or --grok-web-pro
        /// to state the default explicitly.
        public bool GrokWebPro { get; set; } = true;

        /// Cookie file for --grok-web. Overrides settings.json GrokWebCookiePath.
        public string GrokWebCookies { get; set; } = "";

        /// image | video | video-from-image | edit
        public string GrokWebMode { get; set; } = "image";

        /// Aspect ratio for --grok-web image/video runs (default 2:3).
        public string GrokWebAspectRatio { get; set; } = "2:3";

        /// Video length in seconds for --grok-web video modes.
        public int GrokWebVideoLength { get; set; } = 15;

        /// 480p or 720p for --grok-web video modes.
        public string GrokWebVideoResolution { get; set; } = "480p";

        /// Overall motion method for --grok-web video modes.
        public string GrokWebVideoMode { get; set; } = "normal";

        /// Show the Playwright browser used by grok-web video app-chat.
        public bool GrokWebHeaded { get; set; }

        /// When true, request side-by-side variants on grok.com web endpoints.
        public bool GrokWebSideBySide { get; set; } = true;

        /// When true (default), save full grok-web WebSocket capture under saves/.../grok-web-capture/.
        public bool GrokWebCapture { get; set; } = true;

        /// If true, run MetaWebWorkflow: batch prompts through the meta.ai
        /// consumer web app (Muse Image) via a Playwright browser session.
        public bool MetaWeb { get; set; }

        /// Cookie file for --meta-web. Overrides settings.json MetaWebCookiePath.
        public string MetaWebCookies { get; set; } = "";

        /// Show the meta-web browser window. Required for the one-time login
        /// into the persistent profile; useful for troubleshooting after that.
        public bool MetaWebHeaded { get; set; }

        /// If true, run Playwright's browser installer (chromium) and exit.
        /// One-time setup for --meta-web and grok-web video when no configured
        /// browser executable is available.
        public bool PlaywrightInstall { get; set; }

        /// If true, run deterministic offline vectors against the pure-C#
        /// grok.com x-statsig-id signer and exit. No settings, cookies,
        /// browser, network access, or provider request is involved.
        public bool GrokWebStatsigSelfTest { get; set; }

        /// If true, use a real Grok image-post Edit control once to capture
        /// current x-statsig-id deployment inputs, abort the signed edit before
        /// it reaches Grok, verify exact reproduction, write the pair to the
        /// loaded settings file, and exit.
        public bool GrokWebCaptureStatsig { get; set; }

        /// If true, run GrokArchive.SyncAsync and exit: back-read the entire
        /// reachable grok-api history (xAI Files API inventory + re-pollable
        /// video request_ids + local JSON logs) into grok_ledger.jsonl and
        /// download every asset we don't already have locally. Idempotent;
        /// run it whenever to keep local copies synced.
        public bool GrokApiSync { get; set; }

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

        /// Output resolution for local ComfyUI image generators.
        /// Set with --local-size WxH (e.g. 1536x1024). Defaults to 1024x1024.
        /// Only affects runs that include local generators (e.g.
        /// --provider-sample-providers local, --all-providers).
        public Flux2KleinResolution LocalFlux2KleinResolution { get; set; } = Flux2KleinResolution._1024x1024;

        /// If true, start the local web UI: a Kestrel server on localhost
        /// serving a browser control panel (paste image + prompt -> fan out to
        /// selected generators -> live side-by-side results). The C# process
        /// keeps doing everything it does today (generators, ImageManager
        /// saves, contact sheets); the browser is just the front door.
        /// See Workflows/UiWorkflow.cs.
        public bool Ui { get; set; }

        /// Port for --ui. The server binds 127.0.0.1 only.
        public int UiPort { get; set; } = 5960;

        /// Whether --ui should open a browser tab on start.
        /// null = default (open when interactive; skip under systemd).
        /// true/false = force via --ui-open / --ui-no-open.
        public bool? UiOpenBrowser { get; set; }

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
                    case "--b2-smoke":
                        o.B2Smoke = true;
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
                    case "--local-size":
                        {
                            var raw = args[++i];
                            if (!Flux2KleinResolutionExtensions.TryParseSize(raw, out var localRes))
                            {
                                Console.Error.WriteLine($"--local-size '{raw}' invalid. Valid sizes: {Flux2KleinResolutionExtensions.ValidSizesCsv()}");
                                Environment.Exit(2);
                            }
                            o.LocalFlux2KleinResolution = localRes;
                        }
                        break;
                    case "--repl":
                        o.Repl = true;
                        break;
                    case "--ui":
                        o.Ui = true;
                        break;
                    case "--ui-port":
                        o.UiPort = int.Parse(args[++i]);
                        break;
                    case "--ui-open":
                        o.UiOpenBrowser = true;
                        break;
                    case "--ui-no-open":
                        o.UiOpenBrowser = false;
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
                    case "--showcase":
                        o.Showcase = true;
                        break;
                    case "--gens":
                        o.Gens = args[++i];
                        break;
                    case "--grok-api-showcase":
                        o.GrokApiShowcase = true;
                        break;
                    case "--grok-api-pro":
                        o.GrokApiPro = true;
                        break;
                    case "--all-providers":
                        o.AllProviders = true;
                        break;
                    case "--with-video":
                        o.WithVideo = true;
                        break;
                    case "--grok-api-sync":
                        o.GrokApiSync = true;
                        break;
                    case "--grok-api-export":
                        // Optional path argument; default to C:\GrokArchive.
                        o.GrokApiExportPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                            ? args[++i]
                            : @"C:\GrokArchive";
                        break;
                    case "--grok-api-video-test":
                        o.GrokApiVideoTest = true;
                        break;
                    case "--grok-api-edit":
                        o.GrokApiEdit = true;
                        break;
                    case "--grok-api-edit-aspect-ratio":
                        o.GrokApiEditAspectRatio = args[++i];
                        break;
                    case "--grok-web":
                        o.GrokWeb = true;
                        if (o.Workflow == 0) o.Workflow = 1;
                        break;
                    case "--grok-web-pro":
                    case "--grok-web-quality":
                        o.GrokWebPro = true;
                        break;
                    case "--grok-web-fast":
                        o.GrokWebPro = false;
                        break;
                    case "--grok-web-cookies":
                        o.GrokWebCookies = args[++i];
                        break;
                    case "--grok-web-mode":
                        o.GrokWebMode = args[++i];
                        break;
                    case "--grok-web-aspect-ratio":
                        o.GrokWebAspectRatio = args[++i];
                        break;
                    case "--grok-web-length":
                        o.GrokWebVideoLength = int.Parse(args[++i]);
                        break;
                    case "--grok-web-resolution":
                        o.GrokWebVideoResolution = args[++i];
                        break;
                    case "--grok-web-video-method":
                        o.GrokWebVideoMode = args[++i];
                        break;
                    case "--grok-web-headed":
                        o.GrokWebHeaded = true;
                        break;
                    case "--grok-web-no-side-by-side":
                        o.GrokWebSideBySide = false;
                        break;
                    case "--grok-web-no-capture":
                        o.GrokWebCapture = false;
                        break;
                    case "--meta-web":
                        o.MetaWeb = true;
                        break;
                    case "--meta-web-cookies":
                        o.MetaWebCookies = args[++i];
                        break;
                    case "--meta-web-headed":
                        o.MetaWebHeaded = true;
                        break;
                    case "--playwright-install":
                        o.PlaywrightInstall = true;
                        break;
                    case "--grok-web-statsig-self-test":
                        o.GrokWebStatsigSelfTest = true;
                        break;
                    case "--grok-web-capture-statsig":
                        o.GrokWebCaptureStatsig = true;
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
                        if (RenamedGrokFlags.TryGetValue(a, out var newName))
                        {
                            Console.Error.WriteLine($"{a} was renamed to {newName} (grok-api = official api.x.ai key version, grok-web = grok.com cookie-session version).");
                            Environment.Exit(2);
                        }
                        Console.Error.WriteLine($"Unknown argument: {a}");
                        PrintUsage();
                        Environment.Exit(2);
                        break;
                }
            }
            return o;
        }

        // Old grok flag spellings -> current names, so stale shell history
        // fails with a pointer instead of a generic "unknown argument".
        private static readonly Dictionary<string, string> RenamedGrokFlags = new()
        {
            ["--grok-showcase"] = "--grok-api-showcase",
            ["--grok-pro"] = "--grok-api-pro (or --grok-web-pro for the cookie version)",
            ["--grok-sync"] = "--grok-api-sync",
            ["--grok-export"] = "--grok-api-export",
            ["--grok-video-test"] = "--grok-api-video-test",
            ["--grok-edit"] = "--grok-api-edit",
            ["--grok-edit-aspect-ratio"] = "--grok-api-edit-aspect-ratio",
        };

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
            Console.WriteLine("  --b2-smoke        One-shot: verify Backblaze B2 hosting config end to end (upload/fetch/compare/404-check/delete) and exit. Requires EnableB2ImageHosting + B2* settings. See docs/b2-image-hosting-plan.md.");
            Console.WriteLine("  --fast            Use cheapest/fastest generator set (gpt-image-2 low 1024x1024). Good for smoke tests.");
            Console.WriteLine("  --open-images     Pop finished images/contact-sheets open in the system default viewer. OFF by default (runs are headless and just save to disk). --quick-test enables this automatically.");
            Console.WriteLine("  --local-size WxH  Output resolution for local ComfyUI image generators such as FLUX.2 Klein and Z-Image (default 1024x1024). Valid: 1024x1024, 1536x1024, 1024x1536, 1152x896, 896x1152, 1344x768, 768x1344, 1408x1408.");
            Console.WriteLine("  --quick-test      Like --fast plus: save every streamed partial PNG and open each one in the default viewer as it arrives (implies --open-images). Still asks y/n/custom per prompt unless combined with --auto.");
            Console.WriteLine("  --ui              Start the local web UI (browser control panel): paste an image + prompt, fan out to selected generators (gpt-image-2 edit, grok-web pro, grok-api), watch results fill in live. Binds 127.0.0.1 only.");
            Console.WriteLine("  --ui-port N       Port for --ui (default 5960).");
            Console.WriteLine("  --ui-open         Force opening a browser tab when --ui starts (default for interactive runs; unused under systemd unless set).");
            Console.WriteLine("  --ui-no-open      Never open a browser tab when --ui starts (default under systemd so restarts don't pile up tabs).");
            Console.WriteLine("  --repl            Interactive prompt-by-prompt REPL. Prompts fire asynchronously (up to --repl-concurrency at a time); NO viewer pops. Commands: :help :size :quality :gens :status :wait :edit :retry :quit.");
            Console.WriteLine("  --repl-size WxH       REPL session default size for gpt-image-2 (default 2048x2048). Change at runtime with :size WxH.");
            Console.WriteLine("  --repl-quality L      REPL session default quality: low|medium|high (default high). Change at runtime with :quality <L>.");
            Console.WriteLine("  --repl-moderation M   REPL session default moderation: auto|low (default low). Change at runtime with :moderation <M>.");
            Console.WriteLine("  --repl-concurrency N  Max prompts in flight simultaneously in REPL mode (default 5). Change at runtime with :concurrency N.");
            Console.WriteLine("  --repl-n N            REPL session default n (images per gpt-image-2 call, default 1). Change at runtime with :n N, or per-prompt via [n=N] in the override prefix.");
            Console.WriteLine("  --showcase            Generic one-shot: run ALL prompts from the active prompt source (--prompt/--prompt-file/PromptFiles, honoring --limit; no default cap) through the selected generators and compose one contact sheet per generator (pops open only with --open-images).");
            Console.WriteLine($"  --gens csv            Pair with --showcase to pick generators by short name: {string.Join(' ', GeneratorGroups.ShortNames)}. Without --gens the standard batch set runs. grok-web honors the --grok-web-* flags; meta-web honors the --meta-web-* flags.");
            Console.WriteLine("Grok naming: grok-api = official api.x.ai key version (public/GDPR ruleset); grok-web = consumer grok.com cookie-session version (web-app ruleset).");
            Console.WriteLine("  --grok-api-showcase   One-shot: take the first --limit prompts from the active prompt source (--prompt or PromptFiles), fire them at grok-api in parallel, and compose a single combined grid image (pops open only with --open-images). Default --limit for this mode is 10.");
            Console.WriteLine("  --grok-api-pro        Pair with --grok-api-showcase to route through grok-imagine-image-pro at 2k resolution ($0.07/img, 30 rpm) instead of grok-imagine-image at 1k ($0.02/img, 300 rpm).");
            Console.WriteLine("  --all-providers       One-shot: fire ONE prompt (--prompt or first PromptFiles line) at current image endpoints (gpt-image-2, gpt-image-1, gpt-image-1-mini, Ideogram 4.0, flux-2-pro-preview, Recraft V4.1, Grok Imagine, Nano Banana Pro) and compose a single contact-sheet grid (pops open only with --open-images). Keyless providers show as error cells.");
            Console.WriteLine("  --with-video          Pair with --all-providers to also dispatch a Grok Imagine VIDEO (6s, 480p) for the same prompt; the mp4 is saved in the day folder's Video\\ subfolder. Videos are not composited into the PNG sheet.");
            Console.WriteLine("  --grok-api-video-test One-shot: exercise all three grok-api video modes with one prompt (--prompt or first PromptFiles line) — text-to-video, grok-image-to-video, and extend-video (3s, 480p each). Clips are saved, stored durably at xAI, and ledgered.");
            Console.WriteLine("  --grok-api-edit       One-shot: edit --input-image via grok-api using --prompt as edit instructions. Saves the result and a one-cell contact sheet. Pair with --grok-api-pro for grok-imagine-image-pro.");
            Console.WriteLine("  --grok-api-edit-aspect-ratio AR  Optional output aspect ratio for --grok-api-edit (e.g. 1:1, 16:9). Default: inherit source image AR.");
            Console.WriteLine("  --grok-web            Batch prompts through consumer grok.com session endpoints (browser cookies, not api.x.ai). Uses --prompt-file or PromptFiles.");
            Console.WriteLine("  --grok-web-pro        Use the web app's Pro/quality image tier. THIS IS THE DEFAULT; the flag exists to state it explicitly (alias: --grok-web-quality).");
            Console.WriteLine("  --grok-web-fast       Use the web app's fast (non-pro) image tier instead of the default Pro/quality tier.");
            Console.WriteLine("  --grok-web-cookies fp Override settings.json GrokWebCookiePath with a Netscape cookies.txt or raw Cookie header export.");
            Console.WriteLine("  --grok-web-mode M     image (default) | video | video-from-image | edit");
            Console.WriteLine("  --grok-web-aspect-ratio AR  Aspect ratio for grok-web image/video (default 2:3).");
            Console.WriteLine("  --grok-web-length N   Video length seconds for grok-web video modes (default 10).");
            Console.WriteLine("  --grok-web-resolution R  480p (default) or 720p for grok-web video modes.");
            Console.WriteLine("  --grok-web-video-method M  Required video method: normal (default), fun, custom, or spicy. The motion prompt may be empty for video-from-image.");
            Console.WriteLine("  --grok-web-headed     Show the Playwright browser used for grok-web video requests (troubleshooting only).");
            Console.WriteLine("  --grok-web-no-side-by-side  Request a single variant instead of side-by-side on grok-web.");
            Console.WriteLine("  --grok-web-no-capture  Disable full WebSocket session capture (on by default under saves/.../grok-web-capture/).");
            Console.WriteLine("  --meta-web            Batch prompts through the meta.ai consumer web app (Muse Image) by driving a real Playwright browser session. Best-effort/unofficial; text-to-image only; Meta decides the image count. Uses --prompt-file or PromptFiles.");
            Console.WriteLine("  --meta-web-headed     Show the meta-web browser window. Run this once to log in to meta.ai; the persistent profile (MetaWebBrowserProfilePath) keeps the session for headless runs.");
            Console.WriteLine("  --meta-web-cookies fp Override settings.json MetaWebCookiePath with a Netscape cookies.txt or raw Cookie header export from https://www.meta.ai (needs datr + ecto_1_sess; full jar preferred). Alternative to the headed login.");
            Console.WriteLine("  --playwright-install  One-time setup: download Playwright's Chromium for --meta-web and grok-web video, then exit. Not needed when the corresponding BrowserExecutablePath points at Chrome/Chromium.");
            Console.WriteLine("  --grok-web-statsig-self-test  One-shot offline test of the pure-C# x-statsig-id signer against fixed independent vectors; no settings, cookies, browser, network, or provider request.");
            Console.WriteLine("  --grok-web-capture-statsig  One-shot: use --input-image to create a Grok post, capture current signing inputs from the real Edit control, abort before generation, verify, and update the loaded settings file.");
            Console.WriteLine("  --provider-sample-showcase  One-shot: randomly sample --limit prompts (default 15), then make one contact sheet per provider: Grok, Recraft, BFL, Google, and gpt-image-2 low (pops open only with --open-images).");
            Console.WriteLine("  --provider-sample-file fp   Pair with --provider-sample-showcase to reuse a saved numbered/plain people-fixture prompt list.");
            Console.WriteLine("  --provider-sample-providers csv  Pair with --provider-sample-showcase to run only matching providers, e.g. gpt-image-2 or grok-api,recraft (grok-api-pro adds the pro tier).");
            Console.WriteLine("  --provider-sample-retry-failures N  Pair with --provider-sample-showcase to retry failed prompt slots N extra times before composing the sheet.");
            Console.WriteLine("  --grok-api-export [path]  One-shot: full grok-api history export OUTSIDE the repo. Runs --grok-api-sync first, then copies every known Grok image/video plus prompts.txt and grok_ledger.jsonl into [path] (default C:\\GrokArchive). Rerunnable; already-present files are skipped.");
            Console.WriteLine("  --grok-api-sync       One-shot: back-read/back-download the entire reachable grok-api history and exit. Sweeps the xAI Files API inventory, re-polls any ledger video request_ids whose local file is missing, backfills prompts from old JSON logs, and writes everything to grok_ledger.jsonl + saves\\GrokArchive\\. Idempotent — run it whenever to stay synced.");
        }
    }
}
