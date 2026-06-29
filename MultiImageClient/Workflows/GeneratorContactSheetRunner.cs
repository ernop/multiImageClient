#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class GeneratorContactSheetRunner
    {
        public static async Task<string?> RunOneGeneratorAsync(
            IImageGenerator generator,
            IReadOnlyList<string> prompts,
            ImageManager imageManager,
            Settings settings,
            MultiClientRunStats stats,
            string runLabel,
            string sheetHeader,
            int retryFailures = 0,
            bool openWhenDone = true)
        {
            var generatorLabel = Flatten(generator.GetGeneratorSpecPart());
            Logger.Log($"\n{runLabel}: starting {generatorLabel} for {prompts.Count} prompt(s).");

            async Task<TaskProcessResult> RunPromptAsync(string promptText, int index, int attemptNumber)
            {
                var promptNumber = index + 1;
                var pd = new PromptDetails();
                pd.ReplacePrompt(promptText, promptText, TransformationType.InitialPrompt);

                try
                {
                    var result = await generator.ProcessPromptAsync(generator, pd);
                    await imageManager.ProcessAndSaveAsync(result, generator);
                    var status = result.IsSuccess ? "OK" : $"FAIL ({Trim(result.ErrorMessage ?? "", 160)})";
                    Logger.Log($"{runLabel} {generatorLabel} prompt {promptNumber}/{prompts.Count} attempt {attemptNumber} :: {status}");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Log($"{runLabel} {generatorLabel} prompt {promptNumber}/{prompts.Count} attempt {attemptNumber} :: EXCEPTION {ex.Message}");
                    return new TaskProcessResult
                    {
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        PromptDetails = pd,
                        ImageGenerator = generator.ApiType,
                        ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    };
                }
            }

            stats.PrintStats();
            var tasks = prompts.Select((promptText, index) => RunPromptAsync(promptText, index, attemptNumber: 1)).ToArray();
            var results = await Task.WhenAll(tasks);

            for (var retry = 1; retry <= retryFailures; retry++)
            {
                var failedIndexes = results
                    .Select((result, index) => new { result, index })
                    .Where(x => !x.result.IsSuccess)
                    .Select(x => x.index)
                    .ToList();

                if (failedIndexes.Count == 0)
                {
                    break;
                }

                Logger.Log($"{runLabel} {generatorLabel}: retry {retry}/{retryFailures} for prompt slot(s) {string.Join(", ", failedIndexes.Select(i => i + 1))}.");
                var retryTasks = failedIndexes
                    .Select(index => RunPromptAsync(prompts[index], index, attemptNumber: retry + 1))
                    .ToArray();
                var retryResults = await Task.WhenAll(retryTasks);

                for (var i = 0; i < failedIndexes.Count; i++)
                {
                    results[failedIndexes[i]] = retryResults[i];
                }
            }

            for (var i = 0; i < results.Length; i++)
            {
                if (!results[i].IsSuccess)
                {
                    var reason = string.IsNullOrWhiteSpace(results[i].ErrorMessage)
                        ? "Unknown error"
                        : results[i].ErrorMessage;
                    results[i].ErrorMessage = $"Prompt {i + 1}/{results.Length} failed: {reason}";
                }
            }

            stats.PrintStats();

            var okCount = results.Count(r => r.IsSuccess);
            Logger.Log($"{runLabel} {generatorLabel}: {okCount}/{results.Length} succeeded.");

            try
            {
                var outPath = await ImageCombiner.CreateBatchLayoutImageSquareAsync(
                    results,
                    sheetHeader,
                    settings,
                    openWhenDone: openWhenDone,
                    showPerImagePrompts: true);
                Logger.Log($"{runLabel} {generatorLabel} contact sheet: {outPath}");
                return outPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"{runLabel} {generatorLabel}: failed to build contact sheet: {ex.Message}");
                return null;
            }
        }

        public static string BuildRecapString(IReadOnlyList<string> prompts, string header)
        {
            var lines = prompts.Select((p, i) => $"{i + 1}. {p}");
            return header + "\n\n" + string.Join("\n", lines);
        }

        public static string Flatten(string s)
            => s.Replace("\r", " ").Replace("\n", " ");

        public static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }
    }
}
