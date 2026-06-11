#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// One-shot "every provider, one image" runner: takes a SINGLE prompt and
    /// fires it at one flagship generator per provider in parallel (see
    /// GeneratorGroups.GetOnePerProvider — gpt-image-2, Ideogram 4.0,
    /// flux-2-pro-preview, Recraft V4.1, Grok Imagine, Nano Banana Pro, and
    /// optionally Grok video). Each result is saved via the normal
    /// ImageManager pipeline, then everything is composed into ONE combined
    /// contact-sheet grid which pops open in the default viewer.
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
            var promptText = promptSource.Prompts
                .Select(p => p.Prompt)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            if (string.IsNullOrWhiteSpace(promptText))
            {
                Console.Error.WriteLine(
                    "All-providers showcase aborted: no prompt available. "
                    + "Supply --prompt \"...\" or point PromptFiles at a readable file.");
                return null;
            }

            var groups = new GeneratorGroups(settings, concurrency: 1, stats);
            var generators = groups.GetOnePerProvider(includeVideo: options.WithVideo).ToList();

            var imageManager = new ImageManager(settings, stats);
            Logger.Log($"All-providers showcase: 1 prompt x {generators.Count} providers"
                + (options.WithVideo ? " (including Grok video)" : "") + ".");
            Logger.Log($"  prompt: {promptText}");
            foreach (var g in generators)
            {
                Logger.Log($"  - {g.GetGeneratorSpecPart()}  (~${g.GetCost():0.###})");
            }

            var tasks = generators.Select(async generator =>
            {
                var pd = new PromptDetails();
                pd.ReplacePrompt(promptText, promptText, TransformationType.InitialPrompt);

                // Fail fast on obviously-bad credentials (empty, or still the
                // "Optional: get your key from..." template placeholder text)
                // so the cell explains the real problem instead of whatever
                // confusing 400/401 the provider would return.
                var keyProblem = DescribeKeyProblem(generator.ApiType, settings);
                if (keyProblem != null)
                {
                    Logger.Log($"All-providers :: {Flatten(generator.GetGeneratorSpecPart())} :: SKIPPED ({keyProblem})");
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
                    var label = result.IsSuccess ? "OK" : $"FAIL ({Trim(result.ErrorMessage ?? "", 160)})";
                    Logger.Log($"All-providers :: {Flatten(generator.GetGeneratorSpecPart())} :: {label}");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Log($"All-providers :: {Flatten(generator.GetGeneratorSpecPart())} :: EXCEPTION {ex.Message}");
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
            Logger.Log($"All-providers showcase: {okCount}/{results.Length} providers succeeded.");

            // Videos don't belong in a still-image mosaic — the mp4 is already
            // saved to disk (and noted in the log/ledger), so just report it
            // and keep the contact sheet images-only.
            var sheetResults = results.Where(r => r.ImageGenerator != ImageGeneratorApiType.GrokImagineVideo).ToArray();
            foreach (var v in results.Where(r => r.ImageGenerator == ImageGeneratorApiType.GrokImagineVideo))
            {
                Logger.Log(v.IsSuccess
                    ? "All-providers: Grok video succeeded; mp4 saved under the day folder's Video\\ subfolder (excluded from the PNG sheet)."
                    : $"All-providers: Grok video FAILED ({Trim(v.ErrorMessage ?? "", 160)}).");
            }

            var sheetGenerators = generators.Where(g => g.ApiType != ImageGeneratorApiType.GrokImagineVideo).ToList();
            var recap = BuildRecapString(promptText, sheetGenerators.Select(g => g.GetGeneratorSpecPart()).ToList());
            try
            {
                var outPath = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                    sheetResults, recap, settings, openWhenDone: true);
                Logger.Log($"All-providers contact sheet: {outPath}");
                return outPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"All-providers showcase: failed to build contact sheet: {ex.Message}");
                return null;
            }
        }

        /// Returns a human-actionable description of what's wrong with the
        /// API key this generator needs, or null when the key looks usable.
        /// "Looks usable" is deliberately shallow — real validation happens
        /// at the provider — but it catches the two states that otherwise
        /// produce baffling provider errors: empty, and "still contains the
        /// settings-template placeholder sentence".
        private static string? DescribeKeyProblem(ImageGeneratorApiType apiType, Settings settings)
        {
            var (keyName, keyValue) = apiType switch
            {
                ImageGeneratorApiType.Dalle3 or ImageGeneratorApiType.GptImage1
                    or ImageGeneratorApiType.GptImage1Mini or ImageGeneratorApiType.GptImage2
                    => ("OpenAIApiKey", settings.OpenAIApiKey),
                ImageGeneratorApiType.Ideogram or ImageGeneratorApiType.IdeogramV3 or ImageGeneratorApiType.IdeogramV4
                    => ("IdeogramApiKey", settings.IdeogramApiKey),
                ImageGeneratorApiType.BFLv11 or ImageGeneratorApiType.BFLv11Ultra
                    or ImageGeneratorApiType.BFLFlux2Pro or ImageGeneratorApiType.BFLFlux2Max
                    or ImageGeneratorApiType.BFLFlux2Flex or ImageGeneratorApiType.BFLFlux2Klein4b
                    or ImageGeneratorApiType.BFLFlux2Klein9b or ImageGeneratorApiType.BFLFluxKontextPro
                    or ImageGeneratorApiType.BFLFluxKontextMax or ImageGeneratorApiType.BFLFlux2ProPreview
                    => ("BFLApiKey", settings.BFLApiKey),
                ImageGeneratorApiType.Recraft or ImageGeneratorApiType.RecraftV4
                    or ImageGeneratorApiType.RecraftV4Pro or ImageGeneratorApiType.RecraftV41
                    or ImageGeneratorApiType.RecraftV41Pro
                    => ("RecraftApiKey", settings.RecraftApiKey),
                ImageGeneratorApiType.GrokImagine or ImageGeneratorApiType.GrokImaginePro
                    or ImageGeneratorApiType.GrokImagineVideo
                    => ("XAIGrokApiKey", settings.XAIGrokApiKey),
                ImageGeneratorApiType.GoogleNanoBanana or ImageGeneratorApiType.GoogleNanoBananaPro
                    or ImageGeneratorApiType.GoogleImagen4
                    => ("GoogleGeminiApiKey", settings.GoogleGeminiApiKey),
                _ => ((string?)null, (string?)null),
            };
            if (keyName == null)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(keyValue))
            {
                return $"settings.json: {keyName} is empty — paste a real key to enable this provider";
            }
            // Real keys from every provider here are single tokens; the
            // settings template's descriptive sentences contain spaces.
            if (keyValue.Contains(' ') || keyValue.StartsWith("Optional", StringComparison.OrdinalIgnoreCase))
            {
                return $"settings.json: {keyName} still contains the template placeholder text — paste a real key to enable this provider";
            }
            return null;
        }

        private static string BuildRecapString(string prompt, IReadOnlyList<string> generatorLabels)
        {
            var header = $"All-providers contact sheet — {generatorLabels.Count} providers, one image each:";
            var list = string.Join("  |  ", generatorLabels);
            return $"{header}\n{list}\n\nPrompt: {prompt}";
        }

        /// Some generators (e.g. Recraft) embed newlines in their spec part
        /// for annotation layout; collapse them so log lines stay one-line.
        private static string Flatten(string s)
            => s.Replace("\r", " ").Replace("\n", " ");

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }
    }
}
