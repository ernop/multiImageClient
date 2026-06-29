#nullable enable
using System;
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
                resolution: "2k",
                settings: settings,
                baseUrl: settings.XAIBaseUrl);

            var modelLabel = pro ? "grok-imagine-image-pro" : "grok-imagine-image";
            Logger.Log($"Grok showcase: firing {prompts.Count} prompts at {modelLabel} (concurrency={concurrency}).");

            return await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                generator,
                prompts,
                new ImageManager(settings, stats),
                settings,
                stats,
                runLabel: "Grok showcase",
                sheetHeader: $"Grok showcase ({modelLabel}) - {prompts.Count} prompts:");
        }
    }
}
