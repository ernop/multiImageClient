using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using IdeogramAPIClient;
using Newtonsoft.Json;
using System.IO;
using OpenAI.Images;
using System.Linq;

namespace MultiClientRunner
{
    public class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static BFLService _BFLService;
        private static IdeogramService _IdeogramService;
        private static Dalle3Service _Dalle3Service;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing MultiClientRunner...");
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);

            Console.WriteLine("Current settings:");
            Console.WriteLine($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Console.WriteLine($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Console.WriteLine($"Enable Logging:\t\t{settings.EnableLogging}");
            Console.WriteLine($"Annotation Side:\t{settings.AnnotationSide}");
            //TextFormatting.TestImageAnnotationAndSaving();
            _BFLService = new BFLService(settings.BFLApiKey, 10);
            _IdeogramService = new IdeogramService(settings.IdeogramApiKey, 10);
            _Dalle3Service = new Dalle3Service(settings.OpenAIApiKey, 5);
            var claudeService = new ClaudeService(settings.AnthropicApiKey, 10);

            // here is where you have a choice. Super specific stuff like managing a run with repeats, targets etc can be controlled
            // with specific classes which inherit from AbstractPromptGenerator. e.g. DeckOfCards
            var basePromptGenerator = new LoadFromFile(settings, "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts.txt");
            //var basePromptGenerator = new WriteHere(settings);
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
                            var preparedClaudePrompt = $"Help the user expand this idea they submitted for an image. Follow whatever theme they seem to indicate they would like: '{rawPromptDetails.Prompt}' Convert this kernel of an idea into a long, very specific and detailed description of an image in some particular format, describing specific aspects of it such as style, coloring, lighting, format, what's going on in the image etc. Pick and name specific characters, including their life histories and foibles.  Use about 90 words, as prose with no newlines, full of details matching the THEME which you should spend some time really thinking about and making sure to include rich, unusual, and creative details about. Make it thrilling and exciting, risky, super high resolution and detailed, pure clear and mischevious. The rule is more, more, more and more and more and then even more.  Yet, if they seem to want something simple, make it super simple. Just folow the mood you identify they have, and intensify that. Yet also be quite random! Just output the description with no preceding or trailing text.";
                            preparedClaudePrompt = $"use your wisdom and peace to imagine a description of a minimalistic, simple, pure and lovely, super 3d, detailed and immersive image of an imaginary image based on this: '{rawPromptDetails.Prompt}'. You just produce the simple text description which is still magnificently done as a masterwork of ART. Just output a short prose paragraph of about 60 special, obscure, unusual words about this completed, epochal work of art.";
                            var preparedClaudePromptForDisplay = preparedClaudePrompt.Replace(rawPromptDetails.Prompt, $"{{PROMPT}}", StringComparison.OrdinalIgnoreCase);

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
                            AspectRatio = IdeogramAspectRatio.ASPECT_16_9,
                            Model = IdeogramModel.V_2,
                            MagicPromptOption = IdeogramMagicPromptOption.OFF,
                            StyleType = IdeogramStyleType.GENERAL,

                        };
                        promptDetails.IdeogramDetails = ideogramDetails;

                        processingTasks.Add(ProcessAndDownloadAsync(
                            _IdeogramService.ProcessPromptAsync(promptDetails, stats),
                            settings,
                            basePromptGenerator,
                            stats
                        ));
                    }
                    if (basePromptGenerator.UseBFL)
                    {
                        var bflDetails = new BFLDetails
                        {
                            Width = 1440,
                            Height = 1024,
                            PromptUpsampling = false,
                            SafetyTolerance = 6
                        };

                        promptDetails.BFLDetails = bflDetails;

                        processingTasks.Add(ProcessAndDownloadAsync(
                            _BFLService.ProcessPromptAsync(promptDetails, stats),
                            settings,
                            basePromptGenerator,
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
                            _Dalle3Service.ProcessPromptAsync(promptDetails, stats),
                            settings,
                            basePromptGenerator,
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

        private static async Task ProcessAndDownloadAsync(Task<TaskProcessResult> processingTask, Settings settings, AbstractPromptGenerator abstractPromptGenerator, MultiClientRunStats stats)
        {
            try
            {
                var result = await processingTask;
                if (result.IsSuccess && !string.IsNullOrEmpty(result.Url))
                {
                    Console.WriteLine($"Downloading image from URL: {result.Url}");
                    byte[] imageBytes = await DownloadImageAsync(result.Url);

                    var savedImagePaths = new Dictionary<SaveType, string>();

                    if (abstractPromptGenerator.SaveRaw)
                    {
                        savedImagePaths[SaveType.Raw] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.Raw, abstractPromptGenerator.Name);
                    }

                    if (abstractPromptGenerator.SaveFullAnnotation)
                    {
                        savedImagePaths[SaveType.FullAnnotation] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.FullAnnotation, abstractPromptGenerator.Name);
                    }

                    if (abstractPromptGenerator.SaveFinalPrompt)
                    {
                        var theCopy = result.PromptDetails.Clone();
                        theCopy.ImageConstructionSteps = new List<ImageConstructionStep>();
                        theCopy.ReplacePrompt(result.PromptDetails.Prompt, "Final prompt used:", result.PromptDetails.Prompt);
                        result.PromptDetails = theCopy;
                        savedImagePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.FinalPrompt, abstractPromptGenerator.Name);
                    }

                    if (abstractPromptGenerator.SaveInitialIdea)
                    {
                        var theCopy = result.PromptDetails.Clone();
                        theCopy.ImageConstructionSteps = new List<ImageConstructionStep>();
                        var theOriginalPrompt = result.PromptDetails.ImageConstructionSteps.First().Details;
                        theCopy.ReplacePrompt(theOriginalPrompt, "Original prompt:", theOriginalPrompt);
                        result.PromptDetails = theCopy;
                        savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.InitialIdea, abstractPromptGenerator.Name);
                    }

                    if (settings.SaveJsonLog)
                    {
                        await SaveJsonLogAsync(result, savedImagePaths, settings);
                    }
                }
                else
                {
                    Console.WriteLine($"Task failed or no URL: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while processing a task: {ex.Message}");
            }
        }

        private static async Task SaveJsonLogAsync(TaskProcessResult result, Dictionary<SaveType, string> savedImagePaths, Settings settings)
        {
            var jsonLog = new
            {
                Timestamp = DateTime.UtcNow,
                PromptDetails = result.PromptDetails,
                GeneratedImageUrl = result.Url,
                SavedImagePaths = savedImagePaths,
                ServiceUsed = result.Generator,
                ErrorMessage = result.ErrorMessage,
                Settings = settings
            };

            string jsonString = JsonConvert.SerializeObject(jsonLog, Formatting.Indented);

            if (savedImagePaths.TryGetValue(SaveType.Raw, out string rawImagePath))
            {
                string jsonFilePath = Path.ChangeExtension(rawImagePath, ".json");
                await File.WriteAllTextAsync(jsonFilePath, jsonString);
                Console.WriteLine($"JSON log saved to: {jsonFilePath}");
            }
            else
            {
                Console.WriteLine("Unable to save JSON log: Raw image path not found.");
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
