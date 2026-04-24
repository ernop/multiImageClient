#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// One-shot "showcase" runner: fires N prompts at a single image generator
    /// (xAI Grok Imagine) in parallel, saves each result via the normal
    /// ImageManager pipeline, then composes ALL results into a single combined
    /// grid image (one cell per prompt) and pops that combined image open in
    /// the default viewer.
    ///
    /// Used for provider-acceptance smoke tests — e.g. "take the first N
    /// prompts from the current prompt source, send each one through Grok,
    /// give me one image to eyeball".
    ///
    /// Prompts come from the caller-provided AbstractPromptSource (just like
    /// BatchWorkflow) — no prompts are hardcoded here.
    public class GrokShowcaseWorkflow
    {
        /// <param name="pro">If true, routes through grok-imagine-image-pro
        ///   ($0.07/img, 30 rpm) at 2k resolution. Otherwise uses
        ///   grok-imagine-image ($0.02/img, 300 rpm) at 1k.</param>
        /// <param name="limit">Max number of prompts from <paramref name="promptSource"/>
        ///   to run. Defaults to 10, which fits comfortably in the combined
        ///   grid and costs $0.20 on the cheap tier.</param>
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            bool pro = false,
            int limit = 10)
        {
            if (string.IsNullOrWhiteSpace(settings.XAIGrokApiKey))
            {
                Console.Error.WriteLine("Grok showcase aborted: settings.json is missing XAIGrokApiKey.");
                return null;
            }

            var prompts = promptSource.Prompts
                .Select(p => p.Prompt)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(Math.Max(0, limit))
                .ToList();

            if (prompts.Count == 0)
            {
                Console.Error.WriteLine(
                    "Grok showcase aborted: prompt source produced no prompts. "
                    + "Supply --prompt \"...\" or point PromptFiles at a readable file.");
                return null;
            }

            var concurrency = pro ? 2 : 5;
            var apiType = pro ? ImageGeneratorApiType.GrokImaginePro : ImageGeneratorApiType.GrokImagine;

            var generator = new GrokImagineGenerator(
                settings.XAIGrokApiKey,
                concurrency,
                apiType,
                stats,
                name: "",
                aspectRatio: "1:1",
                quality: "high",
                resolution: "2k");

            var imageManager = new ImageManager(settings, stats);
            var modelLabel = pro ? "grok-imagine-image-pro" : "grok-imagine-image";
            Logger.Log($"Grok showcase: firing {prompts.Count} prompts at {modelLabel} (concurrency={concurrency}).");

            var tasks = prompts.Select(async promptText =>
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(promptText, promptText, TransformationType.InitialPrompt);
                try
                {
                    var result = await generator.ProcessPromptAsync(generator, pd);
                    await imageManager.ProcessAndSaveAsync(result, generator);
                    var label = result.IsSuccess ? "OK" : $"FAIL ({result.ErrorMessage})";
                    Logger.Log($"Grok showcase :: {label} :: {Trim(promptText, 80)}");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Grok showcase :: EXCEPTION on prompt '{Trim(promptText, 80)}': {ex.Message}");
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        PromptDetails = pd,
                        ImageGenerator = apiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    };
                }
            }).ToArray();

            stats.PrintStats();
            var results = await Task.WhenAll(tasks);
            stats.PrintStats();

            var okCount = results.Count(r => r.IsSuccess);
            Logger.Log($"Grok showcase: {okCount}/{results.Length} succeeded.");

            // Build a single combined grid image that contains EVERY prompt's
            // result (not one-grid-per-prompt like BatchWorkflow). The prompt
            // panel underneath lists the sub-prompts so the popped-open image
            // is self-documenting.
            var combinedPromptRecap = BuildRecapString(prompts, modelLabel);

            try
            {
                var outPath = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                    results,
                    combinedPromptRecap,
                    settings,
                    openWhenDone: true);
                Logger.Log($"Grok showcase combined image: {outPath}");
                return outPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"Grok showcase: failed to build combined image: {ex.Message}");
                return null;
            }
        }

        private static string BuildRecapString(IReadOnlyList<string> prompts, string modelLabel)
        {
            var header = $"Grok showcase ({modelLabel}) - {prompts.Count} prompts:";
            var lines = prompts.Select((p, i) => $"{i + 1}. {Trim(p, 180)}");
            return header + "\n\n" + string.Join("\n", lines);
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }
    }
}
