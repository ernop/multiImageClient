#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GrokEditWorkflow
    {
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            if (string.IsNullOrWhiteSpace(settings.XAIGrokApiKey))
            {
                Console.Error.WriteLine("Grok edit aborted: settings.json is missing XAIGrokApiKey.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(options.InputImagePath))
            {
                Console.Error.WriteLine("Grok edit aborted: pass --input-image /path/to/source.png or an HTTPS image URL.");
                return null;
            }

            var prompt = promptSource.Prompts
                .Select(p => p.Prompt)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Console.Error.WriteLine("Grok edit aborted: supply edit instructions with --prompt \"...\" or a prompt file.");
                return null;
            }

            var generator = new GrokImagineEditGenerator(
                settings.XAIGrokApiKey,
                maxConcurrency: 1,
                stats,
                settings,
                options.InputImagePath,
                pro: options.GrokPro,
                aspectRatio: options.GrokEditAspectRatio);

            var modelLabel = options.GrokPro ? "grok-imagine-image-pro" : "grok-imagine-image";
            var arLabel = string.IsNullOrWhiteSpace(options.GrokEditAspectRatio)
                ? "source aspect ratio"
                : options.GrokEditAspectRatio;
            Logger.Log($"Grok edit: {modelLabel}, input={options.InputImagePath}, output AR={arLabel}");

            return await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                generator,
                new[] { prompt },
                new ImageManager(settings, stats),
                settings,
                stats,
                runLabel: "Grok edit",
                sheetHeader: $"Grok edit ({modelLabel})",
                openWhenDone: true);
        }
    }
}
