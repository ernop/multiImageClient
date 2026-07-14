#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Generic one-shot showcase (--showcase): pull prompts from the active
    /// prompt source, run every prompt through each selected generator, and
    /// compose one contact sheet per generator.
    ///
    /// Generator selection (--gens, comma-separated short names — same
    /// vocabulary as the REPL, plus grok-web and meta-web):
    ///   gpt2, grok-api, grok-api-pro, grok-web, meta-web, ideogram,
    ///   recraft, bfl, google, googlepro, local-klein, local-zimage
    /// Without --gens it runs the standard batch set (GeneratorGroups.GetAll),
    /// i.e. "whatever models we're currently using".
    ///
    /// grok-web honors the --grok-web-* flags: cookies, aspect ratio, and the
    /// pro/fast tier. meta-web honors the --meta-web-* flags: cookies,
    /// orientation, doc_id. Both run at single-session concurrency of 1.
    public class ShowcaseWorkflow
    {
        public async Task<List<string>> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var outputPaths = new List<string>();

            var prompts = promptSource.Prompts
                .Select(p => p.Prompt)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(options.Limit)
                .ToList();

            if (prompts.Count == 0)
            {
                Console.Error.WriteLine(
                    "Showcase aborted: prompt source produced no prompts. "
                    + "Supply --prompt \"...\", --prompt-file, or point PromptFiles at a readable file.");
                return outputPaths;
            }

            var groups = new GeneratorGroups(settings, concurrency: 5, stats,
                localFlux2KleinResolution: options.LocalFlux2KleinResolution);

            var names = options.Gens
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .Distinct()
                .ToList();

            GrokWebClient? webClient = null;
            MetaWebClient? metaClient = null;
            try
            {
                var generators = new List<IImageGenerator>();
                if (names.Count == 0)
                {
                    generators.AddRange(groups.GetAll());
                    Logger.Log("Showcase: no --gens given; using the standard batch generator set.");
                }
                else
                {
                    foreach (var name in names)
                    {
                        if (name is "grok-web" or "grokweb")
                        {
                            var gen = TryBuildGrokWeb(settings, stats, options, ref webClient);
                            if (gen != null) generators.Add(gen);
                            continue;
                        }

                        if (name is "meta-web" or "metaweb")
                        {
                            var gen = TryBuildMetaWeb(settings, stats, options, ref metaClient);
                            if (gen != null) generators.Add(gen);
                            continue;
                        }

                        // A typo'd name means the run wouldn't do what was
                        // asked, so fail fast instead of skipping silently.
                        try
                        {
                            generators.Add(groups.BuildByShortName(name));
                        }
                        catch (ArgumentException ex)
                        {
                            Console.Error.WriteLine($"Showcase aborted: {ex.Message}");
                            return outputPaths;
                        }
                    }
                }

                Logger.Log($"Showcase: {prompts.Count} prompt(s) x {generators.Count} generator(s):");
                foreach (var g in generators)
                {
                    var perImage = g.GetCost();
                    Logger.Log($"  - {GeneratorContactSheetRunner.Flatten(g.GetGeneratorSpecPart())}  (~${perImage:0.###}/img, ~${perImage * prompts.Count:0.##} total)");
                }

                var imageManager = new ImageManager(settings, stats);
                foreach (var generator in generators)
                {
                    // grok-web / meta-web cookie validity was already
                    // established during construction (against the effective
                    // per-run path), so only key-check the API-backed
                    // generators here.
                    if (generator is not GrokWebImagineGenerator and not MetaWebImagineGenerator)
                    {
                        var keyProblem = ProviderKeyValidator.DescribeKeyProblem(generator.ApiType, settings);
                        if (keyProblem != null)
                        {
                            Logger.Log($"Showcase :: {GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart())} :: SKIPPED ({keyProblem})");
                            continue;
                        }
                    }

                    var spec = GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart());
                    var outPath = await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                        generator,
                        prompts,
                        imageManager,
                        settings,
                        stats,
                        runLabel: "Showcase",
                        sheetHeader: $"Showcase - {spec} - {prompts.Count} prompts:",
                        openWhenDone: options.OpenImages);

                    if (!string.IsNullOrWhiteSpace(outPath))
                    {
                        outputPaths.Add(outPath);
                    }
                }

                Logger.Log($"Showcase complete: {outputPaths.Count}/{generators.Count} contact sheet(s) created.");
                return outputPaths;
            }
            finally
            {
                webClient?.Dispose();
                metaClient?.Dispose();
            }
        }

        private static GrokWebImagineGenerator? TryBuildGrokWeb(
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options,
            ref GrokWebClient? webClient)
        {
            var cookiePath = !string.IsNullOrWhiteSpace(options.GrokWebCookies)
                ? options.GrokWebCookies
                : settings.GrokWebCookiePath;

            if (string.IsNullOrWhiteSpace(cookiePath))
            {
                Logger.Log("Showcase :: grok-web :: SKIPPED (set GrokWebCookiePath in settings.json or pass --grok-web-cookies)");
                return null;
            }

            if (!File.Exists(Settings.ExpandPath(cookiePath)))
            {
                Logger.Log($"Showcase :: grok-web :: SKIPPED (cookie file not found: {Settings.ExpandPath(cookiePath)})");
                return null;
            }

            webClient ??= GrokWebClient.FromCookieFile(cookiePath);
            return new GrokWebImagineGenerator(
                webClient,
                maxConcurrency: 1,
                stats,
                pro: options.GrokWebPro,
                aspectRatio: options.GrokWebAspectRatio,
                enableSideBySide: options.GrokWebSideBySide,
                settings: settings,
                captureSessions: options.GrokWebCapture);
        }

        private static MetaWebImagineGenerator? TryBuildMetaWeb(
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options,
            ref MetaWebClient? metaClient)
        {
            var clientOptions = MetaWebClient.BuildOptions(
                settings,
                cookieOverride: options.MetaWebCookies,
                headedOverride: options.MetaWebHeaded);

            var problem = MetaWebClient.DescribeAvailabilityProblem(clientOptions);
            if (problem != null)
            {
                Logger.Log($"Showcase :: meta-web :: SKIPPED ({problem})");
                return null;
            }

            metaClient ??= new MetaWebClient(clientOptions);

            return new MetaWebImagineGenerator(
                metaClient,
                maxConcurrency: 1,
                stats);
        }
    }
}
