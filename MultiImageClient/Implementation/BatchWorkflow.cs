using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class BatchWorkflow
    {
        public async Task<bool> RunAsync(AbstractPromptSource promptSource, Settings settings, int concurrency, MultiClientRunStats stats)
        {
            var getter = new GeneratorGroups(settings, concurrency, stats);

            var generators = getter.GetAll();
            //var generators = getter.GetAllStylesOfRecraft();

            var imageManager = new ImageManager(settings, stats);
            var claudeService = new ClaudeService(settings.AnthropicApiKey, concurrency, stats);


            var claudeStep = new ClaudeRewriteStep("Please take the following idea and expand it into a list of specific items describing material, color, mood, tone, position in the image, and symbolic purpose of whatever the following prompt is about. the point is, intensify and make things very specific including LAYOUT and style and color andappearance and everything an artist would need. create and emit lots of dense, unusual, specific, random, dense sentences. Note: you may never shrink or remove information from the prompt. Your job is ONLY to expand and make it concrete.  What you output should always be longer than what you receive, with much more specific details, etc.", "", claudeService, 0.4m, stats);

            //var steps = new List<ITransformationStep>() { claudeStep };
            var steps = new List<ITransformationStep>() { };

            var allTasks = new List<Task>();

            foreach (var promptString in promptSource.Prompts)
            {
                Logger.Log($"\n--- Processing prompt: {promptString.Prompt}");

                Console.WriteLine($"Do you accept this? y for yes, n for no go to next, or type the prompt you want directly and hit enter.");
                var val = Console.ReadLine();
                if (val == "y")
                {

                }
                else if (val == "n")
                {
                    continue;
                }
                else
                {
                    var usingVal = val.Trim();
                    PromptLogger.LogPrompt(usingVal);
                    promptString.UndoLastStep();
                    promptString.ReplacePrompt(usingVal, "explanation", TransformationType.InitialPrompt);
                }

                if (promptString.Prompt.Length == 0)
                {
                    Console.WriteLine("no leng?");
                    continue;
                }

                foreach (var step in steps)
                {
                    var res = await step.DoTransformation(promptString);
                    if (!res)
                    {
                        Logger.Log($"\tStep {step.Name} failed: {promptString.Show()}");
                        continue;
                    }
                    Logger.Log($"\tStep:{step.Name} => {promptString.Show()}");
                }

                var ii = 0;
                while (ii < 1)
                {
                    // Create tasks for all generators for this prompt
                    var generatorTasks = generators.Select(async generator =>
                    {
                        PromptDetails theCopy = null;

                        try
                        {
                            theCopy = promptString.Copy();

                            var result = await generator.ProcessPromptAsync(generator, theCopy);
                            await imageManager.ProcessAndSaveAsync(result, generator);
                            Logger.Log($"Finished {generator.GetType().Name} in {result.CreateTotalMs + result.DownloadTotalMs} ms, {result.PromptDetails.Show()}");

                            return result;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"Task faulted for {generator.GetType().Name}: {ex.Message}");

                            var res = new TaskProcessResult
                            {
                                IsSuccess = false,
                                ErrorMessage = ex.Message,
                                PromptDetails = theCopy
                            };

                            return res;
                        }
                    }).ToArray();

                    allTasks.AddRange(generatorTasks);
                    ii++;

                    stats.PrintStats();
                    var results = await Task.WhenAll(generatorTasks);

                    try
                    {
                        var res = await ImageCombiner.CreateBatchLayoutImageHorizontalAsync(results, promptString.Prompt, settings);
                        Logger.Log($"Combined images saved to: {res}");
                        ImageCombiner.OpenImageWithDefaultApplication(res);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to combine images: {ex.Message}");
                        return false;
                    }
                }
            }
            return true;

            // Wait for all tasks
            // while (allTasks.Any(t => !t.IsCompleted))
            // {
            //     var completed = allTasks.Count(t => t.IsCompleted);
            //     var remaining = allTasks.Count - completed;
            //     Logger.Log($"Status: {completed}/{allTasks.Count} completed, {remaining} remaining...");
            //     await Task.WhenAny(Task.Delay(5000), Task.WhenAll(allTasks));
            // }
        }
    }
}