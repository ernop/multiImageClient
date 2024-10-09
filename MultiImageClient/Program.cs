using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using IdeogramAPIClient;
using Newtonsoft.Json;
using System.IO;
using OpenAI.Images;

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
            BFLService.Initialize(settings.BFLApiKey, 10);
            IdeogramService.Initialize(settings.IdeogramApiKey, 10);
            Dalle3Service.Initialize(settings.OpenAIApiKey, 5);
            var claudeService = new ClaudeService(settings.AnthropicApiKey, 10);

            // here is where you have a choice. Super specific stuff like managing a run with repeats, targets etc can be controlled
            // with specific classes which inherit from AbstractPromptGenerator. e.g. DeckOfCards
            var basePromptGenerator = new WriteHere(settings);
            Console.WriteLine($"Starting prompt generation. Base generator: {basePromptGenerator.Name}");
            var stats = new MultiClientRunStats();
            var processingTasks = new List<Task>();

            var temperature = 1m;

            foreach (var rawPromptDetails in basePromptGenerator.Run())
            {
                Console.WriteLine($"\n******* Processing prompt: {rawPromptDetails.Prompt}...");
                Console.WriteLine(stats.PrintStats());

                var usingPromptDetails = new List<PromptDetails>();
                if (basePromptGenerator.AlsoDoVersionSkippingClaude)
                {
                    usingPromptDetails.Add(rawPromptDetails.Clone());
                }

                if (basePromptGenerator.UseClaude)
                {
                    string claudeResponse;
                    try
                    {
                        var claudeWillHateThis = ClaudeService.ClaudeWillHateThis(rawPromptDetails.Prompt);
                        if (claudeWillHateThis)
                        {
                            Console.WriteLine("Claude will hate this so just send it direct.");
                            if (basePromptGenerator.AlsoDoVersionSkippingClaude)
                            {
                                //do nothing since we already added it.
                            }
                            else
                            {
                                Console.Write("\tBackfilling the prompt to direct sending, since Claude would have hated it.");
                                usingPromptDetails.Add(rawPromptDetails.Clone());
                            }
                        }
                        if (!claudeWillHateThis)
                        {
                            var preparedClaudePrompt = $"Expand and improve this core idea for an image: '{rawPromptDetails.Prompt}' Convert this kernel of an idea into a long, very specific and detailed description of an image in some particular format, describing specific aspects of it such as style, coloring, lighting, format, what's going on in the image etc. Use about 130 words, as prose with no newlines, full of details matching the THEME which you should spend some time really thinking about and making sure to include rich, unusual, and creative details about.";
                            var preparedClaudePromptForDisplay = preparedClaudePrompt.Replace(rawPromptDetails.Prompt, $"{{Prompt}}", StringComparison.OrdinalIgnoreCase);

                            rawPromptDetails.ReplacePrompt(preparedClaudePrompt, "Asking claude to improve the prompt:", preparedClaudePromptForDisplay);
                            claudeResponse = await claudeService.RewritePromptAsync(preparedClaudePrompt, stats, temperature);
                            
                            var isClaudeUnhappy = ClaudeService.CheckClaudeUnhappiness(claudeResponse);
                            if (isClaudeUnhappy)
                            {
                                stats.ClaudeRefusedCount++;
                                Console.WriteLine($"\t\tClaude was unhappy about\n\t\t\t{preparedClaudePrompt}\n\t\t\t{claudeResponse}");
                            }
                            else
                            {
                                Console.WriteLine($"\tClaude response: " + claudeResponse);
                                rawPromptDetails.ReplacePrompt(claudeResponse, "claude's rewritten version:", claudeResponse);
                                stats.ClaudeAcceptedCount++;
                                usingPromptDetails.Add(rawPromptDetails);
                            }
                        }
                    }
                    catch (ClaudeRefusedException ex)
                    {
                        Console.WriteLine($"\tClaude refused to rewrite, due to policy. {ex.Message}");
                        //we fallback to drawing the original.
                        usingPromptDetails.Add(rawPromptDetails.Clone());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\tClaude error: {ex.Message}");
                    }
                }
                else
                {
                    usingPromptDetails.Add(rawPromptDetails);
                }

                foreach (var promptDetails in usingPromptDetails)
                {
                    
                    if (basePromptGenerator.UseIdeogram)
                    {
                        var ideogramDetails = new IdeogramDetails
                        {
                            AspectRatio = IdeogramAspectRatio.ASPECT_2_3,
                            Model = IdeogramModel.V_2,
                            MagicPromptOption = IdeogramMagicPromptOption.OFF,
                            StyleType = IdeogramStyleType.GENERAL,

                        };
                        promptDetails.IdeogramDetails = ideogramDetails;

                        processingTasks.Add(ProcessAndDownloadAsync(
                            IdeogramService.ProcessIdeogramPromptAsync(promptDetails, stats),
                            settings,
                            stats
                        ));
                    }
                    if (basePromptGenerator.UseBFL)
                    {
                        var bflDetails = new BFLDetails
                        {
                            Width = 768,
                            Height = 1440,
                            PromptUpsampling = false,
                            SafetyTolerance = 6
                        };

                        promptDetails.BFLDetails = bflDetails;

                        processingTasks.Add(ProcessAndDownloadAsync(
                            BFLService.ProcesBFLPromptAsync(promptDetails, stats),
                            settings,
                            stats
                        ));
                    }
                    if (basePromptGenerator.UseDalle3)
                    {
                        var dalle3Details = new Dalle3Details
                        {
                            Model = "dall-e-3",
                            Size = GeneratedImageSize.W1792xH1024,
                            Quality = GeneratedImageQuality.High,
                            Format = GeneratedImageFormat.Uri
                        };
                        promptDetails.Dalle3Details = dalle3Details;

                        processingTasks.Add(ProcessAndDownloadAsync(
                            Dalle3Service.ProcessDalle3PromptAsync(promptDetails, stats),
                            settings,
                            stats
                        ));
                    }
                    Console.WriteLine(stats.PrintStats());
                }
            }

            await Task.WhenAll(processingTasks);

            Console.WriteLine("All tasks completed.");
            stats.PrintStats();
        }

        private static async Task ProcessAndDownloadAsync(Task<TaskProcessResult> processingTask, Settings settings, MultiClientRunStats stats)
        {
            try
            {
                var result = await processingTask;
                if (result.IsSuccess && !string.IsNullOrEmpty(result.Url))
                {
                    Console.WriteLine($"Downloading image from URL: {result.Url}");
                    byte[] imageBytes = await DownloadImageAsync(result.Url);

                    if (settings.SaveRawImage)
                    {
                        await ImageSaving.SaveRawImageAsync(imageBytes, result.Generator, result.PromptDetails, settings, stats);

                        if (settings.SaveAnnotatedImage)
                        {
                            await ImageSaving.SaveAnnotatedImageAsync(imageBytes, result.Generator, result.PromptDetails, settings, stats);
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Task failed or no URL: {result.ErrorMessage}");
                }
                if (settings.SaveJsonLog)
                {
                    // Implement JSON logging here
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while processing a task: {ex.Message}");
            }
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