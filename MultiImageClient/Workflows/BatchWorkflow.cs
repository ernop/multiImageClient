using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// For each prompt from the source, asks the user whether to keep / skip / edit
    /// it, then sends the (possibly edited) prompt in parallel to every generator
    /// returned by GeneratorGroups.GetAll(). Results are composed into a single
    /// labelled grid image per prompt.
    public class BatchWorkflow
    {
        public async Task<bool> RunAsync(AbstractPromptSource promptSource, Settings settings, int concurrency, MultiClientRunStats stats)
        {
            var generators = new GeneratorGroups(settings, concurrency, stats).GetAll().ToList();
            var imageManager = new ImageManager(settings, stats);

            foreach (var promptString in promptSource.Prompts)
            {
                Logger.Log($"\n--- Processing prompt: {promptString.Prompt}");

                System.Console.WriteLine("\nDo you accept this? y for yes, n for skip, or type the prompt you want directly and hit enter. (q to quit)");
                var val = System.Console.ReadLine();
                if (val == null)
                {
                    Logger.Log("stdin closed; ending batch.");
                    return true;
                }
                val = val.Trim();

                if (val == "q")
                {
                    return true;
                }
                if (val == "n")
                {
                    continue;
                }
                if (val.Length > 0 && val != "y")
                {
                    PromptLogger.LogPrompt(val);
                    promptString.UndoLastStep();
                    promptString.ReplacePrompt(val, "explanation", TransformationType.InitialPrompt);
                }

                if (promptString.Prompt.Length == 0)
                {
                    System.Console.WriteLine("empty prompt, skipping.");
                    continue;
                }

                var generatorTasks = generators.Select(async generator =>
                {
                    PromptDetails theCopy = null;
                    try
                    {
                        theCopy = promptString.Copy();
                        Logger.Log($"  -> SENDING to {generator.GetGeneratorSpecPart()} : {theCopy.Prompt}");
                        var result = await generator.ProcessPromptAsync(generator, theCopy);
                        await imageManager.ProcessAndSaveAsync(result, generator);
                        var status = result.IsSuccess ? "OK" : $"FAIL ({result.ErrorMessage})";
                        Logger.Log($"  <- {status} from {generator.GetGeneratorSpecPart()} in {result.CreateTotalMs + result.DownloadTotalMs} ms");
                        return result;
                    }
                    catch (System.Exception ex)
                    {
                        Logger.Log($"  <- EXCEPTION from {generator.GetGeneratorSpecPart()}: {ex.Message}");
                        return new TaskProcessResult
                        {
                            IsSuccess = false,
                            ErrorMessage = ex.Message,
                            PromptDetails = theCopy ?? promptString,
                            ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                        };
                    }
                }).ToArray();

                stats.PrintStats();
                var results = await Task.WhenAll(generatorTasks);

                try
                {
                    var composed = await ImageCombiner.CreateBatchLayoutImageSquareAsync(results, promptString.Prompt, settings);
                    Logger.Log($"Combined images saved to: {composed}");
                }
                catch (System.Exception ex)
                {
                    Logger.Log($"Failed to combine images for prompt '{promptString.Prompt}': {ex.Message}");
                }
            }
            return true;
        }
    }
}
