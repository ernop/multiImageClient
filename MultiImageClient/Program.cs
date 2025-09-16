using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MultiImageClient
{
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

            var dalle3 = new Dalle3Generator(settings.OpenAIApiKey, concurrency, GeneratedImageQuality.High, GeneratedImageSize.W1024xH1024, stats, "");
            var recraft1 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.digital_engraving,  null, stats, "");
            var recraft2 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, stats, "");
            var recraft3 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.freehand_details, null, stats, "");
            var recraft4 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.studio_portrait, stats, "");
            var recraft5 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.vector_illustration, RecraftVectorIllustrationSubstyle.infographical, null, null, stats, "");
            var recraft6 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.natural_light, stats, "");
            var ideogram1 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN, "", IdeogramModel.V_2, stats, "");
            var ideogram2 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_2_3, null, "", IdeogramModel.V_2, stats, "");
            var bfl1 = new BFLGenerator(ImageGeneratorApiType.BFLv11, settings.BFLApiKey, concurrency, false, "3:2", false, 1024, 1024, stats, "");
            var bfl2 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, settings.BFLApiKey, concurrency, false, "1:1", false, 2048, 1768, stats, "");
            
            var gptimage1 = new GptImageOneGenerator(settings.OpenAIApiKey, concurrency, "1024x1024", "low", OpenAIGPTImageOneQuality.high, stats, "");   

            //var myGenerators = new List<IImageGenerator>() { dalle3, ideogram1, ideogram2, bfl1, bfl2, recraft3, recraft4,  recraft6, gptimage1 };
            //var myGenerators = new List<IImageGenerator>() { dalle3, recraft1, recraft2, recraft3, recraft4, recraft5, recraft6, ideogram1, ideogram2, bfl1, bfl2 };
            var myGenerators = new List<IImageGenerator>() { gptimage1};
            var imageManager = new ImageManager(settings, stats);

            /// -----------------------  APPLYING PROMPTS TO SERVICES ------------------------


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
                    promptString.UndoLastStep();
                    //this is a bit silly since the original initial prompt will still be in history for no reason.
                    promptString.ReplacePrompt(usingVal, "explanation", TransformationType.InitialPrompt);
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
                    var generatorTasks = myGenerators.Select(async generator =>
                    {
                        try
                        {
                            var theCopy = promptString.Copy();
                            var result = await generator.ProcessPromptAsync(theCopy);
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
                    ii++;

                    stats.PrintStats();
                    Console.WriteLine($"Kicked off {generatorTasks.Count} tasks for prompt.");
                }
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
