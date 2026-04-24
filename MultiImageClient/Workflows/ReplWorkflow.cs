using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BFLAPIClient;
using IdeogramAPIClient;
using OpenAI.Images;
using RecraftAPIClient;

namespace MultiImageClient
{
    /// Interactive prompt-by-prompt REPL with asynchronous dispatch.
    ///
    /// Each non-empty stdin line is either:
    ///   - a `:command` (size / quality / gens / status / wait / edit / retry / quit / ...)
    ///   - a prompt, which is fired off as a Task against the current active
    ///     generator set and returned to the caller immediately so they can
    ///     keep typing.
    ///
    /// Up to <see cref="RunOptions.ReplConcurrency"/> prompts are in flight at
    /// once; extra prompts queue on a <see cref="SemaphoreSlim"/>. Results are
    /// saved to disk as they arrive via the existing <see cref="ImageManager"/>
    /// pipeline (so filenames, the Label-annotated copy, FlatImageMirror copy,
    /// and json logs all behave exactly as in Batch). Unlike Batch, no
    /// per-prompt combined-grid image is popped open — the grid is still
    /// built and saved, but the viewer launch is suppressed.
    ///
    /// Session state the user can mutate at runtime:
    ///   - gpt-image-2 size / quality / moderation (rebuilds the gpt2 slot)
    ///   - the active generator set (add / remove by name from a fixed catalog)
    ///   - concurrency limit (takes effect for subsequent dispatches)
    ///
    /// Design notes:
    ///   - The `_active` dictionary is snapshotted into a List at dispatch time
    ///     so mutations after a prompt has been submitted never affect an
    ///     in-flight job.
    ///   - Per-prompt overrides like `[size=1024x1024,q=low] <prompt text>` are
    ///     parsed off the front of the line; unrecognized keys cause the line
    ///     to be treated as a plain prompt.
    public class ReplWorkflow
    {
        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;
        private readonly ImageManager _imageManager;

        private readonly object _lock = new object();
        private int _nextId = 0;
        private readonly List<InFlight> _inFlight = new List<InFlight>();
        private string _lastPrompt;

        // Lazily loaded pool of prompts read from Settings.PromptFiles /
        // LoadPromptsFrom, used by the `:random` command. Null until the
        // first :random call; a null-but-asked result means "no files /
        // no lines" and is surfaced to the user.
        private List<string> _promptPool;

        // Session defaults for the gpt-image-2 slot. Editing any of these
        // rebuilds the "gpt2" entry in _active (if present).
        private string _size;
        private string _quality;
        private string _moderation;
        private int _concurrency;
        private SemaphoreSlim _concurrencyLimit;

        // Active generator set, keyed by short name (e.g. "gpt2", "ideogram").
        // Order is preserved for deterministic grid layout.
        private readonly Dictionary<string, IImageGenerator> _active =
            new Dictionary<string, IImageGenerator>(StringComparer.OrdinalIgnoreCase);

        // Catalog of known per-name factories. ':gens add <name>' looks here.
        // Keep in sync with PrintHelp()'s generator list.
        private static readonly string[] KnownGenerators =
            { "gpt2", "grok", "grokpro", "dalle3", "ideogram", "recraft", "bfl", "google", "imagen4" };

        private class InFlight
        {
            public int Id;
            public string Prompt;
            public DateTime StartedAt;
            public Task Task;
        }

        public ReplWorkflow(Settings settings, MultiClientRunStats stats, RunOptions options)
        {
            _settings = settings;
            _stats = stats;
            _imageManager = new ImageManager(settings, stats);

            // Preflight the REPL default size so bad --repl-size values get
            // caught at startup instead of after we've built a generator that
            // will fail on every call.
            var startSize = string.IsNullOrWhiteSpace(options.ReplSize) ? "2048x2048" : options.ReplSize;
            _size = NormalizeSizeOrFallback(startSize, "2048x2048", "--repl-size");
            _quality = string.IsNullOrWhiteSpace(options.ReplQuality) ? "high" : options.ReplQuality;
            _moderation = string.IsNullOrWhiteSpace(options.ReplModeration) ? "low" : options.ReplModeration;
            _concurrency = options.ReplConcurrency < 1 ? 1 : options.ReplConcurrency;
            _concurrencyLimit = new SemaphoreSlim(_concurrency);

            // Default active set: gpt-image-2 + Grok Imagine (standard tier).
            // Both fire in parallel for each dispatched prompt. Grok is only
            // added if an API key is present so the REPL still works on
            // gpt-only setups. Users can :gens add / remove at runtime.
            _active["gpt2"] = BuildGpt2(_size, _quality, _moderation);
            if (!string.IsNullOrWhiteSpace(_settings.XAIGrokApiKey))
            {
                _active["grok"] = BuildNamed("grok");
            }
        }

        public async Task RunAsync()
        {
            PrintBanner();
            PrintHelp();
            PrintShow();
            Console.WriteLine();

            while (true)
            {
                var line = Console.ReadLine();
                if (line is null)
                {
                    Console.WriteLine("(stdin closed; waiting for in-flight jobs then exiting)");
                    break;
                }
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith(":"))
                {
                    var exit = await HandleCommandAsync(line);
                    if (exit) break;
                    continue;
                }

                // Bare exit aliases (no leading colon). These win over the
                // short-prompt guard so users don't get prompted to confirm
                // that yes, they really did mean to quit.
                var lower = line.ToLowerInvariant();
                if (lower == "q" || lower == "x" || lower == "exit" || lower == "quit")
                {
                    break;
                }

                // Typing `foo` or `bug` is almost always a mistake — a real
                // prompt is meaningfully longer. Treat anything under 5 chars
                // (after stripping any `[size=..]`-style override prefix) as
                // suspicious and require explicit confirmation before burning
                // an API call on it.
                var (_, _, promptOnly) = ParseOverrides(line);
                if ((promptOnly ?? "").Trim().Length < 5)
                {
                    Console.Write($"short prompt ({(promptOnly ?? "").Trim().Length} chars): '{line}'. Send anyway? [y/N] ");
                    var confirm = Console.ReadLine();
                    if (confirm == null) break;
                    var c = confirm.Trim().ToLowerInvariant();
                    if (c != "y" && c != "yes")
                    {
                        Console.WriteLine("(skipped)");
                        continue;
                    }
                }

                DispatchPrompt(line);
            }

            await WaitAllAsync();
            Console.WriteLine("REPL finished.");
        }

        // ---------------------------------------------------------------
        // Command handling
        // ---------------------------------------------------------------

        // Returns true if the REPL should exit after this command.
        private async Task<bool> HandleCommandAsync(string line)
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].TrimStart(':').ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (cmd)
            {
                case "help":
                case "h":
                case "?":
                    PrintHelp();
                    return false;

                case "show":
                    PrintShow();
                    return false;

                case "size":
                    if (string.IsNullOrEmpty(arg)) { Console.WriteLine($"size = {_size}"); return false; }
                    if (!GptImage2Generator.TryNormalizeSize(arg, out var sized, out var sizeNote, out var sizeErr))
                    {
                        Console.WriteLine($"(size rejected: {sizeErr}. kept {_size})");
                        return false;
                    }
                    if (sizeNote != null) Console.WriteLine($"(size {sizeNote})");
                    _size = sized;
                    RebuildGpt2IfActive();
                    Console.WriteLine($"size = {_size}");
                    return false;

                case "quality":
                case "q":
                    if (string.IsNullOrEmpty(arg)) { Console.WriteLine($"quality = {_quality}"); return false; }
                    _quality = arg;
                    RebuildGpt2IfActive();
                    Console.WriteLine($"quality = {_quality}");
                    return false;

                case "moderation":
                case "mod":
                    if (string.IsNullOrEmpty(arg)) { Console.WriteLine($"moderation = {_moderation}"); return false; }
                    _moderation = arg;
                    RebuildGpt2IfActive();
                    Console.WriteLine($"moderation = {_moderation}");
                    return false;

                case "concurrency":
                    if (int.TryParse(arg, out var n) && n >= 1)
                    {
                        _concurrency = n;
                        // SemaphoreSlim isn't resizable; swap in a fresh one.
                        // In-flight tasks still hold permits on the old
                        // instance — that's fine, they'll release on the old
                        // semaphore which then gets garbage-collected. Only
                        // subsequent dispatches see the new limit.
                        _concurrencyLimit = new SemaphoreSlim(_concurrency);
                        Console.WriteLine($"concurrency = {_concurrency}");
                    }
                    else Console.WriteLine("usage: :concurrency N  (N >= 1)");
                    return false;

                case "gens":
                    HandleGensCommand(arg);
                    return false;

                case "status":
                    PrintStatus();
                    return false;

                case "wait":
                    await WaitAllAsync();
                    return false;

                case "last":
                    Console.WriteLine(string.IsNullOrEmpty(_lastPrompt) ? "(no prior prompt)" : _lastPrompt);
                    return false;

                case "retry":
                    if (string.IsNullOrEmpty(_lastPrompt))
                    {
                        Console.WriteLine("(no prior prompt)");
                    }
                    else
                    {
                        DispatchPrompt(_lastPrompt);
                    }
                    return false;

                case "random":
                case "r":
                    HandleRandomPrompt();
                    return false;

                case "edit":
                    if (string.IsNullOrEmpty(_lastPrompt))
                    {
                        Console.WriteLine("(no prior prompt to edit)");
                        return false;
                    }
                    Console.WriteLine($"last: {_lastPrompt}");
                    Console.Write("edit (blank = reuse, q = cancel): ");
                    var newLine = Console.ReadLine();
                    if (newLine is null) return false;
                    var trimmed = newLine.Trim();
                    if (trimmed == "q") { Console.WriteLine("(cancelled)"); return false; }
                    DispatchPrompt(string.IsNullOrEmpty(trimmed) ? _lastPrompt : trimmed);
                    return false;

                case "quit":
                case "exit":
                    return true;

                default:
                    Console.WriteLine($"unknown command ':{cmd}'. Try :help");
                    return false;
            }
        }

        private void HandleGensCommand(string arg)
        {
            var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
            var rest = parts.Length > 1 ? parts[1].Trim() : "";

            switch (sub)
            {
                case "list":
                    if (_active.Count == 0) { Console.WriteLine("(no active generators)"); return; }
                    foreach (var kv in _active)
                    {
                        Console.WriteLine($"  {kv.Key}: {kv.Value.GetGeneratorSpecPart()}");
                    }
                    return;

                case "add":
                    if (string.IsNullOrEmpty(rest))
                    {
                        Console.WriteLine($"usage: :gens add <name>. Known: {string.Join(", ", KnownGenerators)}");
                        return;
                    }
                    try
                    {
                        var key = rest.ToLowerInvariant();
                        var g = BuildNamed(key);
                        _active[key] = g;
                        Console.WriteLine($"added {key}: {g.GetGeneratorSpecPart()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"add failed: {ex.Message}");
                    }
                    return;

                case "remove":
                case "rm":
                    if (string.IsNullOrEmpty(rest)) { Console.WriteLine("usage: :gens remove <name>"); return; }
                    if (_active.Remove(rest.ToLowerInvariant())) Console.WriteLine($"removed {rest}");
                    else Console.WriteLine($"not active: {rest}");
                    return;

                case "reset":
                    _active.Clear();
                    _active["gpt2"] = BuildGpt2(_size, _quality, _moderation);
                    if (!string.IsNullOrWhiteSpace(_settings.XAIGrokApiKey))
                    {
                        _active["grok"] = BuildNamed("grok");
                    }
                    Console.WriteLine($"reset to defaults ({string.Join(" + ", _active.Keys)})");
                    return;

                default:
                    Console.WriteLine($"usage: :gens [list | add <name> | remove <name> | reset]  names: {string.Join(", ", KnownGenerators)}");
                    return;
            }
        }

        // ---------------------------------------------------------------
        // :random — pick a prompt from the configured PromptFiles and
        // prefill it on the input line so the user can tweak it in place
        // or just hit Enter to accept.
        // ---------------------------------------------------------------

        private void HandleRandomPrompt()
        {
            var pool = LoadPromptPool();
            if (pool == null || pool.Count == 0)
            {
                Console.WriteLine("(no prompts found — set Settings.PromptFiles to point at a .txt file with one prompt per line)");
                return;
            }

            // Keep rolling new picks until the user picks an action. Ctrl-C
            // is reserved for "abort the whole program" — it shouldn't be
            // the UX for rejecting a suggestion.
            while (true)
            {
                var pick = pool[Random.Shared.Next(pool.Count)];
                Console.WriteLine();
                Console.WriteLine($"(random from {pool.Count}):");
                Console.WriteLine($"  {pick}");
                Console.Write("[y]es send  [n]o skip  [r]eroll  [e]dit: ");

                var resp = Console.ReadLine();
                if (resp is null) return;
                var c = resp.Trim().ToLowerInvariant();

                // Treat bare Enter as "reroll" — it's the lowest-commitment
                // action and matches how fast you can just keep hitting
                // Enter to flip through suggestions.
                if (c.Length == 0 || c == "r" || c == "reroll")
                {
                    continue;
                }
                if (c == "y" || c == "yes")
                {
                    DispatchPrompt(pick);
                    return;
                }
                if (c == "n" || c == "no" || c == "skip")
                {
                    Console.WriteLine("(skipped)");
                    return;
                }
                if (c == "e" || c == "edit")
                {
                    Console.Write("> ");
                    // Prefill the candidate so it appears on the next input
                    // line as if typed, letting the user tweak it in place.
                    // If the injection fails (redirected stdin etc.) just
                    // print it above the prompt and ask them to retype.
                    if (!ConsolePrefill.TryPrefill(pick))
                    {
                        Console.WriteLine();
                        Console.WriteLine(pick);
                        Console.Write("> ");
                    }
                    var edited = Console.ReadLine();
                    if (edited is null) return;
                    edited = edited.Trim();
                    if (edited.Length == 0)
                    {
                        Console.WriteLine("(empty — skipping)");
                        return;
                    }
                    DispatchPrompt(edited);
                    return;
                }

                Console.WriteLine($"(unrecognized '{resp}' — expected y/n/r/e)");
            }
        }

        // Reads Settings.PromptFiles (+ legacy LoadPromptsFrom) on first use,
        // pooling all non-blank lines. Caches for the life of the REPL;
        // restart the session to pick up edits.
        private List<string> LoadPromptPool()
        {
            if (_promptPool != null) return _promptPool;

            var files = new List<string>();
            if (_settings.PromptFiles != null)
            {
                files.AddRange(_settings.PromptFiles.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            if (!string.IsNullOrWhiteSpace(_settings.LoadPromptsFrom))
            {
                files.Add(_settings.LoadPromptsFrom);
            }

            var lines = new List<string>();
            foreach (var fp in files)
            {
                if (!File.Exists(fp))
                {
                    Console.WriteLine($"(prompt file missing, skipping: {fp})");
                    continue;
                }
                foreach (var raw in File.ReadAllLines(fp))
                {
                    var t = raw?.Trim();
                    if (!string.IsNullOrEmpty(t)) lines.Add(t);
                }
            }

            _promptPool = lines;
            if (lines.Count > 0)
            {
                Console.WriteLine($"(loaded {lines.Count} prompts from {files.Count} file(s))");
            }
            return _promptPool;
        }

        // ---------------------------------------------------------------
        // Dispatch
        // ---------------------------------------------------------------

        private void DispatchPrompt(string rawLine)
        {
            if (_active.Count == 0)
            {
                Console.WriteLine("no generators active. Use ':gens add <name>' or ':gens reset'.");
                return;
            }

            // Optional leading override: "[size=1024x1024,q=low] actual prompt"
            var (overrideSize, overrideQuality, promptText) = ParseOverrides(rawLine);
            if (string.IsNullOrWhiteSpace(promptText))
            {
                Console.WriteLine("(empty prompt after parsing overrides, skipping)");
                return;
            }

            // Validate per-prompt size override up-front so a typo like
            // "[size=1526x2048]" doesn't burn an API round-trip before the
            // server rejects it. Auto-snap near-miss multiples; reject the
            // rest with the session default kept intact.
            if (overrideSize != null)
            {
                if (!GptImage2Generator.TryNormalizeSize(overrideSize, out var normOvr, out var noteOvr, out var errOvr))
                {
                    Console.WriteLine($"(override size rejected: {errOvr}. not dispatching)");
                    return;
                }
                if (noteOvr != null) Console.WriteLine($"(override size {noteOvr})");
                overrideSize = normOvr;
            }

            _lastPrompt = promptText;

            // Snapshot the active set so :gens mutations after dispatch don't
            // affect this job. If there's a per-call override AND gpt2 is
            // active, swap in a one-off gpt2 for this dispatch only.
            var gens = _active.ToList();
            if ((overrideSize != null || overrideQuality != null)
                && _active.ContainsKey("gpt2"))
            {
                var s = overrideSize ?? _size;
                var q = overrideQuality ?? _quality;
                for (int i = 0; i < gens.Count; i++)
                {
                    if (gens[i].Key.Equals("gpt2", StringComparison.OrdinalIgnoreCase))
                    {
                        gens[i] = new KeyValuePair<string, IImageGenerator>("gpt2", BuildGpt2(s, q, _moderation));
                    }
                }
            }

            int id;
            lock (_lock) id = ++_nextId;

            var inflight = new InFlight
            {
                Id = id,
                Prompt = promptText,
                StartedAt = DateTime.Now,
            };
            inflight.Task = Task.Run(() => ProcessOneAsync(inflight, gens.Select(kv => kv.Value).ToList()));
            lock (_lock) _inFlight.Add(inflight);

            var overrideNote = (overrideSize != null || overrideQuality != null)
                ? $"  override: size={overrideSize ?? "(sess)"} q={overrideQuality ?? "(sess)"}"
                : "";
            Logger.Log($"[#{id}] queued ({gens.Count} gen{(gens.Count == 1 ? "" : "s")}){overrideNote}: {promptText}");

            // Reprint the command cheat-sheet after each dispatch so the
            // available commands stay visible while long generations run —
            // keeps the user from having to scroll up to remember flags.
            PrintHelp();
        }

        private async Task ProcessOneAsync(InFlight inflight, List<IImageGenerator> gens)
        {
            var id = inflight.Id;
            var prompt = inflight.Prompt;

            // Grab a concurrency slot. Snapshot the semaphore reference so a
            // runtime :concurrency swap doesn't cause us to Release() on a
            // different instance than we WaitAsync'd on.
            var limiter = _concurrencyLimit;
            await limiter.WaitAsync();

            try
            {
                Logger.Log($"[#{id}] START: {prompt}  ({gens.Count} generator(s))");

                var pd = new PromptDetails();
                pd.ReplacePrompt(prompt, prompt, TransformationType.InitialPrompt);

                var tasks = gens.Select(async g =>
                {
                    PromptDetails copy = null;
                    try
                    {
                        copy = pd.Copy();
                        Logger.Log($"[#{id}]   -> {g.GetGeneratorSpecPart()}");
                        var r = await g.ProcessPromptAsync(g, copy);
                        await _imageManager.ProcessAndSaveAsync(r, g);
                        var status = r.IsSuccess ? "OK" : $"FAIL ({r.ErrorMessage})";
                        Logger.Log($"[#{id}]   <- {status} from {g.GetGeneratorSpecPart()} in {r.CreateTotalMs + r.DownloadTotalMs} ms");
                        return r;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[#{id}]   <- EXCEPTION from {g.GetGeneratorSpecPart()}: {ex.Message}");
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = ex.Message,
                            PromptDetails = copy ?? pd,
                            ImageGeneratorDescription = g.GetGeneratorSpecPart(),
                        };
                    }
                }).ToArray();

                var results = await Task.WhenAll(tasks);

                // Build the grid without popping it in the viewer. The grid
                // is still saved to disk (and mirrored) so REPL sessions can
                // review final comparison images after the fact.
                try
                {
                    var combined = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                        results, prompt, _settings, openWhenDone: false);
                    Logger.Log($"[#{id}] DONE grid saved: {combined}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[#{id}] grid build failed: {ex.Message}");
                }
            }
            finally
            {
                limiter.Release();
                lock (_lock) _inFlight.Remove(inflight);
            }
        }

        private async Task WaitAllAsync()
        {
            Task[] snapshot;
            lock (_lock) snapshot = _inFlight.Select(i => i.Task).ToArray();
            if (snapshot.Length == 0)
            {
                Console.WriteLine("(no jobs in flight)");
                return;
            }
            Console.WriteLine($"waiting for {snapshot.Length} job(s)...");
            try { await Task.WhenAll(snapshot); }
            catch { /* each task already logs its own failures */ }
        }

        // ---------------------------------------------------------------
        // Builders
        // ---------------------------------------------------------------

        // Reconstruct the gpt-image-2 slot in _active with the current
        // session-level size / quality / moderation so the next dispatch
        // picks them up. No-op if gpt2 isn't currently active.
        private void RebuildGpt2IfActive()
        {
            if (_active.ContainsKey("gpt2"))
            {
                _active["gpt2"] = BuildGpt2(_size, _quality, _moderation);
            }
        }

        private IImageGenerator BuildGpt2(string size, string quality, string moderation)
        {
            if (!Enum.TryParse<OpenAIGPTImageOneQuality>(quality, true, out var q))
            {
                Console.WriteLine($"(unknown quality '{quality}', falling back to high)");
                q = OpenAIGPTImageOneQuality.high;
            }
            // Pass maxConcurrency = _concurrency so gpt-image-2's own internal
            // semaphore doesn't become the bottleneck when the REPL has
            // multiple prompts in flight. The prompt-level semaphore
            // (_concurrencyLimit) is still the authoritative cap.
            //
            // partialSaveFolder is set so streamed partial PNGs go to disk,
            // but popUpPartials is deliberately false — REPL mode never
            // pops anything in the viewer.
            return new GptImage2Generator(
                _settings.OpenAIApiKey,
                maxConcurrency: _concurrency,
                sizePool: new[] { size },
                moderation: moderation,
                qualityPool: new[] { q },
                stats: _stats,
                name: "repl",
                partialSaveFolder: _settings.ImageDownloadBaseFolder,
                popUpPartials: false);
        }

        private IImageGenerator BuildNamed(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "gpt2":
                    return BuildGpt2(_size, _quality, _moderation);

                case "grok":
                    // Standard tier priced per-image regardless of resolution
                    // — 2k is a free upgrade over 1k so we take it.
                    RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", "grok");
                    return new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                        ImageGeneratorApiType.GrokImagine, _stats, "repl",
                        aspectRatio: "1:1", quality: "high", resolution: "2k");

                case "grokpro":
                case "grok_pro":
                    RequireKey(_settings.XAIGrokApiKey, "XAIGrokApiKey", "grokpro");
                    return new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                        ImageGeneratorApiType.GrokImaginePro, _stats, "repl",
                        aspectRatio: "1:1", quality: "high", resolution: "2k");

                case "dalle3":
                    RequireKey(_settings.OpenAIApiKey, "OpenAIApiKey", "dalle3");
                    return new Dalle3Generator(_settings.OpenAIApiKey, _concurrency,
                        GeneratedImageQuality.High, GeneratedImageSize.W1024xH1024, _stats, "repl");

                case "ideogram":
                    RequireKey(_settings.IdeogramApiKey, "IdeogramApiKey", "ideogram");
                    return new IdeogramV3Generator(_settings.IdeogramApiKey, _concurrency,
                        IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON,
                        IdeogramAspectRatio.ASPECT_16_10, IdeogramRenderingSpeed.QUALITY,
                        "", _stats, "repl");

                case "recraft":
                    RequireKey(_settings.RecraftApiKey, "RecraftApiKey", "recraft");
                    return new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                        RecraftImageSize._1365x1024, RecraftStyle.any, null, null, null,
                        _stats, "repl");

                case "bfl":
                    RequireKey(_settings.BFLApiKey, "BFLApiKey", "bfl");
                    return new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra,
                        _settings.BFLApiKey, _concurrency, "1:1", false, 1024, 1024,
                        _stats, "repl");

                case "google":
                case "nanobanana":
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", "google");
                    return new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana,
                        _settings.GoogleGeminiApiKey, _concurrency, _stats);

                case "imagen4":
                    RequireKey(_settings.GoogleGeminiApiKey, "GoogleGeminiApiKey", "imagen4");
                    return new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency,
                        _stats, "repl", "2:5", "BLOCK_NONE",
                        location: _settings.GoogleCloudLocation,
                        projectId: _settings.GoogleCloudProjectId,
                        googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath);

                default:
                    throw new ArgumentException(
                        $"unknown generator '{name}'. Known: {string.Join(", ", KnownGenerators)}");
            }
        }

        private static void RequireKey(string value, string settingName, string genName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Settings.{settingName} is not set; cannot activate '{genName}'. Populate it in settings.json.");
            }
        }

        // ---------------------------------------------------------------
        // Parsing: "[size=1024x1024,q=low] actual prompt"
        // ---------------------------------------------------------------

        // Only applies the override to the gpt2 slot; other generators
        // ignore per-call size/quality flags since they're parametrized
        // at construction time.
        private static (string size, string quality, string prompt) ParseOverrides(string line)
        {
            if (!line.StartsWith("[")) return (null, null, line);
            var close = line.IndexOf(']');
            if (close < 0) return (null, null, line);
            var inside = line.Substring(1, close - 1);
            var rest = line.Substring(close + 1).TrimStart();

            string size = null, quality = null;
            foreach (var tok in inside.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = tok.Split('=', 2);
                if (kv.Length != 2) return (null, null, line);
                var k = kv[0].Trim().ToLowerInvariant();
                var v = kv[1].Trim();
                switch (k)
                {
                    case "size":
                    case "s":
                        size = v;
                        break;
                    case "quality":
                    case "q":
                        quality = v;
                        break;
                    default:
                        // Unrecognized key — fall back to treating the whole
                        // line as a plain prompt (safer than silently dropping).
                        return (null, null, line);
                }
            }
            return (size, quality, rest);
        }

        // ---------------------------------------------------------------
        // Printing helpers
        // ---------------------------------------------------------------

        private void PrintStatus()
        {
            InFlight[] snap;
            lock (_lock) snap = _inFlight.ToArray();
            if (snap.Length == 0) { Console.WriteLine("(no jobs in flight)"); return; }
            Console.WriteLine($"{snap.Length} in flight:");
            foreach (var i in snap.OrderBy(x => x.Id))
            {
                var elapsed = (DateTime.Now - i.StartedAt).TotalSeconds;
                Console.WriteLine($"  #{i.Id}  {elapsed,5:F1}s  {Trunc(i.Prompt, 90)}");
            }
        }

        private void PrintShow()
        {
            Console.WriteLine($"session: size={_size}  quality={_quality}  moderation={_moderation}  concurrency={_concurrency}");
            var names = _active.Count == 0 ? "(none)" : string.Join(" ", _active.Keys);
            Console.WriteLine($"active gens: {names}");
        }

        private static void PrintBanner()
        {
            Console.WriteLine();
            Console.WriteLine("=== MultiImageClient REPL ===");
            Console.WriteLine("Non-command lines are prompts, dispatched asynchronously.");
            Console.WriteLine("Up to ':concurrency' prompts run in parallel. Grids are saved but NOT opened.");
        }

        private static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  :show                    print current session defaults and active generators");
            Console.WriteLine("  :size WxH                set gpt-image-2 size. Edges snap to multiples of 16, each <=3840,");
            Console.WriteLine("                           total pixels 655360..8294400, aspect <=3:1, or 'auto'.");
            Console.WriteLine("  :quality low|medium|high set gpt-image-2 quality");
            Console.WriteLine("  :moderation auto|low     set gpt-image-2 moderation");
            Console.WriteLine("  :concurrency N           max prompts in flight (applies to subsequent dispatches)");
            Console.WriteLine("  :gens list               list active generators");
            Console.WriteLine("  :gens add <name>         add a generator: gpt2 grok grokpro dalle3 ideogram recraft bfl google imagen4");
            Console.WriteLine("  :gens remove <name>      remove a generator from the active set");
            Console.WriteLine("  :gens reset              back to defaults (gpt2 + grok when XAIGrokApiKey is set)");
            Console.WriteLine("  :status                  list in-flight jobs");
            Console.WriteLine("  :wait                    block until every in-flight job finishes");
            Console.WriteLine("  :last                    reprint last-submitted prompt");
            Console.WriteLine("  :retry                   resubmit last prompt with current settings");
            Console.WriteLine("  :edit                    interactively edit and resubmit last prompt");
            Console.WriteLine("  :random  /  :r           show a random prompt from PromptFiles; y=send n=skip r=reroll e=edit");
            Console.WriteLine("  :help                    show this help");
            Console.WriteLine("  :quit  /  :exit          wait for in-flight jobs then exit");
            Console.WriteLine("                           (bare 'q', 'x', 'quit', 'exit' on a line by themselves also exit)");
            Console.WriteLine();
            Console.WriteLine("Per-prompt override (gpt2 only): [size=1024x1024,q=low] a red apple on a white plate");
        }

        private static string Trunc(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 3) + "...");

        // Used at session startup for --repl-size. Logs to stderr/stdout and
        // falls back to `fallback` (which is assumed to be a known-valid
        // canonical size) rather than aborting the whole REPL — the user can
        // correct it at runtime with `:size WxH`.
        private static string NormalizeSizeOrFallback(string raw, string fallback, string source)
        {
            if (GptImage2Generator.TryNormalizeSize(raw, out var norm, out var note, out var err))
            {
                if (note != null) Console.WriteLine($"({source} size {note})");
                return norm;
            }
            Console.WriteLine($"({source} size '{raw}' rejected: {err}. falling back to {fallback})");
            return fallback;
        }
    }
}
