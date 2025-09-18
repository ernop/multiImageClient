using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime;
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
            var recraft1 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.hard_comics,  null, stats, "");
            //var recraft2 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, stats, "");
            var recraft3 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.freehand_details, null, stats, "");
            var recraft4 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.studio_portrait, stats, "");
            var recraft5 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._1365x1024, RecraftStyle.vector_illustration, RecraftVectorIllustrationSubstyle.infographical, null, null, stats, "");
            var recraft6 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._2048x1024, RecraftStyle.realistic_image, null, null, RecraftRealisticImageSubstyle.real_life_glow, stats, "");
            //var recraft7 = new RecraftGenerator(settings.RecraftApiKey, concurrency, RecraftImageSize._2048x1024, RecraftStyle.digital_illustration, null, RecraftDigitalIllustrationSubstyle.bold_fantasy, null, stats, "");
            //var ideogram1 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_16_10, IdeogramStyleType.DESIGN, "", IdeogramModel.V_2, stats, "");
            var ideogram2 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_1_1, null, "", IdeogramModel.V_2_TURBO, stats, "");
            var ideogram3 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_4_3, null, "", IdeogramModel.V_2A, stats, "");
            var ideogram4 = new IdeogramGenerator(settings.IdeogramApiKey, concurrency, IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_4_3, null, "", IdeogramModel.V_2A_TURBO, stats, "");
            var bfl1 = new BFLGenerator(ImageGeneratorApiType.BFLv11, settings.BFLApiKey, concurrency, "3:2", false, 1024, 1024, stats, "");
            var bfl2 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, settings.BFLApiKey, concurrency, "1:1", false, 1024, 1024, stats, "");
            var bfl3 = new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, settings.BFLApiKey, concurrency, "3:2", false, 1024, 1024, stats, "");

            var gptimage1 = new GptImageOneGenerator(settings.OpenAIApiKey, concurrency, "1024x1024", "low", OpenAIGPTImageOneQuality.high, stats, "");

            //var myGenerators = new List<IImageGenerator>() { dalle3, ideogram2, bfl1, bfl2, bfl3, recraft6, ideogram4, };
            //var myGenerators = new List<IImageGenerator>() { dalle3, recraft1, recraft2, recraft3, recraft4, recraft5, recraft6, ideogram1, ideogram2, bfl1, bfl2 };
            var myGenerators = new List<IImageGenerator>() { ideogram3,  ideogram4, dalle3, recraft1}; 
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

                if (promptString.Prompt.Length == 0)
                {
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
                    var generatorTasks = myGenerators.Select(async generator =>
                    {
                        PromptDetails theCopy = null;
                        
                        try
                        {
                            theCopy = promptString.Copy();
                            
                            var result = await generator.ProcessPromptAsync(theCopy);
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
                        var res = ImageSaving.CombineImagesHorizontallyAsync(results, promptString.Prompt, settings);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to combine images: {ex.Message}");
                    }
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
    }
}
