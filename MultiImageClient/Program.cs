using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class PromptLogger
    {
        private const string LogFileName = "prompt_log.json";

        public static void LogPrompt(string prompt)
        {
            var logEntry = new
            {
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                prompt = prompt
            };

            var jsonLine = JsonSerializer.Serialize(logEntry);

            try
            {
                File.AppendAllText(LogFileName, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to write to prompt log: {ex.Message}");
            }
        }
    }

    public class Program
    {
        private static ClaudeService _ClaudeService { get; set; }
        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 1;
            var stats = new MultiClientRunStats();
            var getter = new GeneratorGroups(settings, concurrency, stats);

            var ideogramClient = new IdeogramClient(settings.IdeogramApiKey);
            var promptSource = new ReadAllPromptsFromFile(settings, "");

            var claudeService = new ClaudeService(settings.AnthropicApiKey, concurrency, stats);
            //var claudeStep = new ClaudeRewriteStep("Please take the following topic and make it specific; cast the die, take a chance, and expand it to a longer, detailed, specific description of a scene with all the elements of it described. Describe how the thing looks, feels, appears, etc in high detail. Put the most important aspects first such as the overall description, then continue by expanding that and adding more detail, structure, theme. Be specific in whatevr you do. If it seems appropriate, if a man appears don't just say 'the man', but instead actually give him a name, traits, personality, etc. The goal is to deeply expand the world envisioned by the original topic creator. Overall, follow the implied theem and goals of the creator, but just expand it into much more specifics and concreate actualization. Never use phrases or words like 'diverse', 'vibrant' etc. Be very concrete and precise in your descriptions, similar to how ansel adams describing a new treasured species of bird would - detailed, caring, dense, clear, sharp, speculative and never wordy or fluffy. every single word you say must be relevant to the goal of increasing the info you share about this image or sitaution or scene. Be direct and clear.", "", claudeService, 0.4m, stats);

            var claudeStep = new ClaudeRewriteStep("Please take the following idea and expand it into a list of 10 specific items describing material, color, mood, tone, position in the image, and symbolic purpose of whatever the following prompt is about. the point is, intensify and make things very specific including LAYOUT and style and color andappearance and everything an artist would need. create and emit lots of dense, unusual, specific, random, dense acronym-filled sentences", "", claudeService, 0.4m, stats);


            //var steps = new List<ITransformationStep>() { claudeStep };
            var steps = new List<ITransformationStep>() {  };


            /// ------------------- MAKING SERVICES ----------------------------


            var imageManager = new ImageManager(settings, stats);

            /// -----------------------  APPLYING PROMPTS TO SERVICES ------------------------
            var generators = getter.GetAll();
            //var generators = getter.GetAllStylesOfRecraft();


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
                        var res = await ImageSaving.CombineImagesAsync(results, promptString.Prompt, settings, CombinedImageLayout.Square);
                        Logger.Log($"Combined images saved to: {res}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to combine images: {ex.Message}");
                    }
                }
            }

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
