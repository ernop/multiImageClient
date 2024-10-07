using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using IdeogramAPIClient;
using Newtonsoft.Json;
using System.IO;

namespace MultiClientRunner
{
    public class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();

        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing MultiClientRunner...");
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);

            Console.WriteLine("Current settings:");
            Console.WriteLine($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Console.WriteLine($"Save Raw:\t\t{settings.SaveRawImage}");
            Console.WriteLine($"Save Annotated?:\t{settings.SaveAnnotatedImage}");
            Console.WriteLine($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Console.WriteLine($"Enable Logging:\t\t{settings.EnableLogging}");
            Console.WriteLine($"Annotation Side:\t{settings.AnnotationSide}");
            //TextFormatting.TestImageAnnotationAndSaving();
            BFLService.Initialize(settings.BFLApiKey, 5);
            IdeogramService.Initialize(settings.IdeogramApiKey, 5);
            var claudeService = new ClaudeService(settings.AnthropicApiKey, 5);

            // here is where you have a choice. Super specific stuff like managing a run with repeats, targets etc can be controlled
            // with specific classes which inherit from AbstractPromptGenerator. e.g. DeckOfCards
            var basePromptGenerator = new LoadFromFile(settings, settings.LoadPromptsFrom);

            Console.WriteLine($"Starting prompt generation. Base generator: {basePromptGenerator.Name}");
            var tasks = new List<Task<TaskProcessResult>>();

            var doClaude = true;
            var useBfl = true;
            var useIdeogram = true;
            
            var stats = new MultiClientRunStats();

            foreach (var promptDetails in basePromptGenerator.Run())
            {
                Console.WriteLine($"\n * Processing prompt: {promptDetails.Prompt}...");
                if (doClaude)
                {
                    string claudeResponse;
                    try
                    {
                        // TODO I'm unhappy that there is more text hardcoded inside claudeService.`
                        claudeResponse = await claudeService.RewritePromptAsync(promptDetails.Prompt, stats);
                        promptDetails.ReplacePrompt(claudeResponse, "claude's rewritten version:", claudeResponse);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\tCladue error: {ex.Message}");
                    }
                }

                if (useIdeogram)
                {
                    var ideogramDetails = new IdeogramDetails
                    {
                        AspectRatio = IdeogramAspectRatio.ASPECT_1_1,
                        Model = IdeogramModel.V_2,
                        MagicPromptOption = IdeogramMagicPromptOption.OFF,
                        StyleType = IdeogramStyleType.GENERAL
                    };
                    promptDetails.IdeogramDetails = ideogramDetails;

                    tasks.Add(IdeogramService.ProcessIdeogramPromptAsync(promptDetails, stats));
                }
                if (useBfl)
                {
                    var bflDetails = new BFLDetails
                    {
                        Width = 1024,
                        Height = 1024,
                        PromptUpsampling = false,
                        SafetyTolerance = 6
                    };

                    promptDetails.BFLDetails = bflDetails;

                    tasks.Add(BFLService.ProcesBFLPromptAsync(promptDetails, stats));
                }


                while (tasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(tasks);
                    tasks.Remove(completedTask);

                    try
                    {
                        var result = await completedTask;
                        if (result.IsSuccess)
                        {
                            if (string.IsNullOrEmpty(result.Url))
                            {
                                Console.WriteLine($"No result Url though, {result.ErrorMessage}");
                            }
                            else
                            {
                                Console.WriteLine($"Downloading image from URL: {result.Url}");
                                byte[] imageBytes;


                                if (settings.SaveRawImage)
                                {
                                    imageBytes = await DownloadImageAsync(result.Url);

                                    await ImageSaving.SaveRawImageAsync(imageBytes, result.Generator, result.PromptDetails, settings, stats);

                                    if (settings.SaveAnnotatedImage)
                                    {
                                        // you can't save annotated unless you save the raw, fix later.
                                        await ImageSaving.SaveAnnotatedImageAsync(imageBytes, result.Generator, result.PromptDetails, settings, stats);
                                    }
                                }

                                if (settings.SaveJsonLog)
                                {
                                    // do nothing right now.
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Task failed: {result.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred while processing a task: {ex.Message}");
                    }
                }

                stats.PrintStats();
            }

            Console.WriteLine("All tasks completed.");
            stats.PrintStats();
        }

        public static async Task<byte[]> DownloadImageAsync(string imageUrl)
        {
            try
            {
                return await httpClient.GetByteArrayAsync(imageUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download image from {imageUrl}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }
    }
}