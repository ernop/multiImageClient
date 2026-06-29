#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// "Every provider, one image" runner: takes each prompt from the active
    /// prompt source and fires it at one flagship generator per provider in parallel (see
    /// GeneratorGroups.GetOnePerProvider — gpt-image-2, Ideogram 4.0,
    /// flux-2-pro-preview, Recraft V4.1, Grok Imagine, Nano Banana Pro, and
    /// optionally Grok video). Each prompt's results are saved via the normal
    /// ImageManager pipeline, then composed into that prompt's contact-sheet grid.
    ///
    /// Providers whose API keys are missing/invalid fail soft: their cell in
    /// the sheet shows the error placeholder instead of an image, so a single
    /// run doubles as a key/auth health check across every provider.
    public class AllProvidersShowcaseWorkflow
    {
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var promptTexts = promptSource.Prompts
                .Select(p => p.Prompt?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(options.Limit)
                .Cast<string>()
                .ToList();

            if (promptTexts.Count == 0)
            {
                Console.Error.WriteLine(
                    "All-providers showcase aborted: no prompt available. "
                    + "Supply --prompt \"...\", --prompt-file <path>, or point PromptFiles at a readable file.");
                return null;
            }

            var groups = new GeneratorGroups(settings, concurrency: 1, stats);
            var generators = groups.GetOnePerProvider(includeVideo: options.WithVideo).ToList();

            var imageManager = new ImageManager(settings, stats);
            var promptConcurrency = Math.Max(1, options.PromptConcurrency);
            Logger.Log($"All-providers showcase: {promptTexts.Count} prompt(s) x {generators.Count} providers"
                + $" (prompt concurrency {promptConcurrency})"
                + (options.WithVideo ? " (including Grok video)" : "") + ".");
            foreach (var g in generators)
            {
                Logger.Log($"  - {g.GetGeneratorSpecPart()}  (~${g.GetCost():0.###})");
            }

            using var limiter = new SemaphoreSlim(promptConcurrency);
            var promptTasks = promptTexts.Select(async (promptText, index) =>
            {
                var promptNumber = index + 1;
                await limiter.WaitAsync();
                try
                {
                    return await RunOnePromptAsync(
                        promptText,
                        promptNumber,
                        totalPrompts: promptTexts.Count,
                        generators,
                        imageManager,
                        settings,
                        stats);
                }
                finally
                {
                    limiter.Release();
                }
            }).ToArray();

            var outputPaths = (await Task.WhenAll(promptTasks))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToList();

            Logger.Log($"All-providers showcase complete: {outputPaths.Count}/{promptTexts.Count} contact sheet(s) created.");
            return outputPaths.LastOrDefault();
        }

        private static async Task<string?> RunOnePromptAsync(
            string promptText,
            int promptNumber,
            int totalPrompts,
            IReadOnlyList<IImageGenerator> generators,
            ImageManager imageManager,
            Settings settings,
            MultiClientRunStats stats)
        {
            var promptLabel = $"prompt {promptNumber}/{totalPrompts}";
            Logger.Log($"\nAll-providers showcase {promptLabel}: {promptText}");

            var tasks = generators.Select(async generator =>
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(promptText, promptText, TransformationType.InitialPrompt);

                // Fail fast on obviously-bad credentials (empty, or still the
                // "Optional: get your key from..." template placeholder text)
                // so the cell explains the real problem instead of whatever
                // confusing 400/401 the provider would return.
                var keyProblem = ProviderKeyValidator.DescribeKeyProblem(generator.ApiType, settings);
                if (keyProblem != null)
                {
                    Logger.Log($"All-providers {promptLabel} :: {GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart())} :: SKIPPED ({keyProblem})");
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = keyProblem,
                        PromptDetails = pd,
                        ImageGenerator = generator.ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    };
                }

                try
                {
                    var result = await generator.ProcessPromptAsync(generator, pd);
                    await imageManager.ProcessAndSaveAsync(result, generator);
                    var label = result.IsSuccess ? "OK" : $"FAIL ({GeneratorContactSheetRunner.Trim(result.ErrorMessage ?? "", 160)})";
                    Logger.Log($"All-providers {promptLabel} :: {GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart())} :: {label}");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Log($"All-providers {promptLabel} :: {GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart())} :: EXCEPTION {ex.Message}");
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        PromptDetails = pd,
                        ImageGenerator = generator.ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    };
                }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            stats.PrintStats();

            var okCount = results.Count(r => r.IsSuccess);
            Logger.Log($"All-providers showcase {promptLabel}: {okCount}/{results.Length} providers succeeded.");

            // Videos don't belong in a still-image mosaic — the mp4 is already
            // saved to disk (and noted in the log/ledger), so just report it
            // and keep the contact sheet images-only.
            var sheetResults = results.Where(r => r.ImageGenerator != ImageGeneratorApiType.GrokImagineVideo).ToArray();
            foreach (var v in results.Where(r => r.ImageGenerator == ImageGeneratorApiType.GrokImagineVideo))
            {
                Logger.Log(v.IsSuccess
                    ? $"All-providers {promptLabel}: Grok video succeeded; mp4 saved under the day folder's Video\\ subfolder (excluded from the PNG sheet)."
                    : $"All-providers {promptLabel}: Grok video FAILED ({GeneratorContactSheetRunner.Trim(v.ErrorMessage ?? "", 160)}).");
            }

            var recap = BuildRecapString(promptText, promptNumber, totalPrompts);
            try
            {
                var outPath = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                    sheetResults, recap, settings, openWhenDone: true);
                Logger.Log($"All-providers {promptLabel} contact sheet: {outPath}");
                return outPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"All-providers showcase {promptLabel}: failed to build contact sheet: {ex.Message}");
                return null;
            }
        }

        private static string BuildRecapString(string prompt, int promptNumber, int totalPrompts)
        {
            var header = $"All-providers contact sheet - prompt {promptNumber}/{totalPrompts}";
            return $"{header}\n\nPrompt: {prompt}";
        }

    }
}
