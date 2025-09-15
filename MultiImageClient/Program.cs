using MultiImageClient;
using IdeogramAPIClient;
using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Emit;
using System.Runtime;
using System.Threading.Tasks;

using static MultiImageClient.Program;


namespace MultiImageClient
{
    /// We only well-control the initial prompt text generation. The actual process of applying various steps, logging etc is all hardcoded in here which is not ideal.
    public class Program
    {
        
        private static ClaudeService _ClaudeService { get; set; }

        

        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 1;
            var stats = new MultiClientRunStats();

            var promptSource = new ReadAllPromptsFromFile(settings, "");
            var steps = new List<ITransformationStep>();


            /// ------------------- MAKING SERVICES ----------------------------
            
            var dalle3 = new Dalle3Generator(settings.OpenAIApiKey, concurrency, stats);
            var recraft = new RecraftGenerator(settings.RecraftApiKey, concurrency, stats);
            var ideogram1 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, stats, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN);
            var ideogram2 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, stats, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN);
            var bfl1 = new BFLGenerator(ImageGeneratorApiType.BFLv11, settings.BFLApiKey, concurrency, stats, false, "1:1", false, 1024, 1024);
            var bfl2 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, settings.BFLApiKey, concurrency, stats, false, "1:1", false, 2048, 1768);

            var myGenerators = new List<IImageGenerator>() { dalle3, recraft, ideogram1, ideogram2, bfl1, bfl2};
            var imageManager = new ImageManager(settings, stats);

            /// -----------------------  APPLYING PROMPTS TO SERVICES ------------------------


            var allTasks = new List<Task>();

            foreach (var promptString in promptSource.Prompts)
            {
                Logger.Log($"\n--- Processing prompt: {promptString.Prompt}");

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

                // Create tasks for all generators for this prompt
                var generatorTasks = myGenerators.Select(async generator =>
                {
                    try
                    {
                        var result = await generator.ProcessPromptAsync(promptString);
                        await imageManager.ProcessAndSaveAsync(result, generator);
                        Logger.Log($"Finished {generator.GetType().Name} in {result.CreateTotalMs + result.DownloadTotalMs} ms, {result.PromptDetails.Show()}");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Task faulted for {generator.GetType().Name}: {ex.Message}");
                        throw;
                    }
                }).ToList();

                allTasks.AddRange(generatorTasks);
                stats.PrintStats();
                Console.WriteLine($"Kicked off {generatorTasks.Count} tasks for prompt.");
            }

            // Wait for all tasks
            while (allTasks.Any(t => !t.IsCompleted))
            {
                var completed = allTasks.Count(t => t.IsCompleted);
                var remaining = allTasks.Count - completed;
                Logger.Log($"Status: {completed}/{allTasks.Count} completed, {remaining} remaining...");
                await Task.WhenAny(Task.Delay(5000), Task.WhenAll(allTasks));
            }
        }

        //private static void OnFinished(Task<TaskProcessResult> task, object arg2)
        //{
        //    if (task.IsFaulted)
        //    {
        //        Logger.Log($"Task faulted: {task.Exception?.Flatten().InnerException}");
        //        return;
        //    }
        //    if (task.IsCanceled)
        //    {
        //        Logger.Log("Task was canceled.");
        //        return;
        //    }

        //    var res = task.Result;
        //    Logger.Log($"Finished in {res.CreateTotalMs + res.DownloadTotalMs} ms, {res.PromptDetails.Show()}");
        //}
    }
}
