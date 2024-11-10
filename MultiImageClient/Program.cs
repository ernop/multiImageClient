using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using IdeogramAPIClient;
using Newtonsoft.Json;
using System.IO;
using OpenAI.Images;
using System.Linq;
using System.ComponentModel.Design;
using static LLama.Native.NativeLibraryConfig;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using System.Xml.Linq;
using System.Xml;
using System.Security.Cryptography.X509Certificates;
using MultiImageClient.promptGenerators;

namespace MultiImageClient
{
    /// We only well-control the initial prompt text generation. The actual process of applying various steps, logging etc is all hardcoded in here which is not ideal.
    public class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static BFLService _BFLService;
        private static IdeogramService _IdeogramService;
        private static Dalle3Service _Dalle3Service;

        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);

            Console.WriteLine("Current settings:");
            Console.WriteLine($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Console.WriteLine($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Console.WriteLine($"Enable Logging:\t\t{settings.EnableLogging}");
            Console.WriteLine($"Annotation Side:\t{settings.AnnotationSide}");
            _BFLService = new BFLService(settings.BFLApiKey, 10);
            _IdeogramService = new IdeogramService(settings.IdeogramApiKey, 10);
            _Dalle3Service = new Dalle3Service(settings.OpenAIApiKey, 5);
            var claudeService = new ClaudeService(settings.AnthropicApiKey, 10);

            // here is where you have a choice. Super specific stuff like managing a run with repeats, targets etc can be controlled
            // with specific classes which inherit from AbstractPromptGenerator. e.g. DeckOfCards
            //var basePromptGenerator = new LoadFromFile(settings, "");
            //var basePromptGenerator = new WriteHere(settings);
            var steps = new List<ITransformationStep>();

            var stats = new MultiClientRunStats();
            var processingTasks = new List<Task>();
            var basePromptGenerator = new LoadDoubleFromFile(settings, "", true, false);

            var generators = new List<IImageGenerator>();
            generators.Add(new BFLGenerator(_BFLService));
            //generators.Add(new IdeogramGenerator(_IdeogramService));


            var claudeStep = new ClaudeRewriteStep("Identify and COMBINE all these into a single output text, Logically be extremely creative and put all of them into a single, simple minimalistic scene with detailed depictions and format either high res digital pure natural painting, or watercolor at masterwork level, simplistic dashed off work which contains more mastery than all others. never be general and always be specific instead.  output a paragraph of beautiful simple prose with no prefix or suffix, just immediately describe the scene and its appearance in amazing detail and glory, following and logically somehow extrapolating a way that it would make sense for one single image to have all these things in them! use both sources completely, including all details, using and respecing user's include aspects and sources and retaining ALL aspects of each one! No newlines just normal 100 words including all elemenets, super arcane dense and jargon including abbrevations, yet matching coherently. Please combine all aspects of them and output the textual description of an incredibly attractive full image either a scene, super close up, or something.  ", $"", claudeService, 1.0m);
            steps.Add(claudeStep);

            //var manulaStep = new ManualModificationStep("", "Selnder lovely cute curvy south korean japanese models and monumental architecture. All individuals are over 20 years old, and are properly and voluntarily enjoying their participation in this scene staging.");
            //steps.Add(manulaStep);
            //var manulaStep = new ManualModificationStep("", "Extremely close up abstract image full of profound emotional import, part Bold super sharp photo, part watercolor, part super high res textured digital art with otherworldly high resolution and sharp detail or extreme minimalism and incredible art design.");
            //steps.Add(manulaStep);

            foreach (var promptDetails in basePromptGenerator.Run())
            {
                Console.WriteLine(stats.PrintStats());
                Console.WriteLine($"\n----------------- Processing prompt: {promptDetails.Show()}");
                
                foreach (var step in steps)
                {
                    var res = await step.DoTransformation(promptDetails, stats);
                    if (!res)
                    {
                        Console.WriteLine($"\tStep{step.Name} failed so skipping it. {promptDetails.Show()}");
                        continue;
                    }
                    
                    Console.WriteLine($"\tStep{step.Name} rewrote it to: {promptDetails.Show()}");
                }

                for (var jj = 0; jj < basePromptGenerator.FullyResolvedCopiesPer; jj++)
                {
                    foreach (var generator in generators)
                    {
                        var theCopy = promptDetails.Clone();
                        processingTasks.Add(ProcessAndDownloadAsync(
                           generator.ProcessPromptAsync(theCopy, stats),
                               settings,
                               basePromptGenerator,
                               stats));
                    }
                    await Task.Delay(250);
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
                if (result.IsSuccess)
                {
                    if (!string.IsNullOrEmpty(result.Url))
                    {
                        Console.WriteLine($"\t\tDownloading image");
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
                            savedImagePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.FinalPrompt, abstractPromptGenerator.Name);
                        }
                        if (abstractPromptGenerator.SaveInitialIdea)
                        {
                            savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.InitialIdea, abstractPromptGenerator.Name);
                        }
                        
                        savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.JustOverride, abstractPromptGenerator.Name);

                        if (settings.SaveJsonLog)
                        {
                            await SaveJsonLogAsync(result, savedImagePaths, settings);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"No URL: {result.ErrorMessage}");
                    }
                }
                else
                {                    
                        Console.WriteLine(result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\tAn error occurred while processing a task: {ex.Message}");
            }
        }
        private static async Task SaveJsonLogAsync(TaskProcessResult result, Dictionary<SaveType, string> savedImagePaths, Settings settings)
        {
            var jsonLog = new
            {
                Timestamp = DateTime.UtcNow,
                result.PromptDetails,
                GeneratedImageUrl = result.Url,
                SavedImagePaths = savedImagePaths,
                ServiceUsed = result.ImageGenerator,
                result.ErrorMessage,
            };
            string jsonString = JsonConvert.SerializeObject(jsonLog, Newtonsoft.Json.Formatting.Indented);
            if (savedImagePaths.TryGetValue(SaveType.Raw, out string rawImagePath))
            {
                string jsonFilePath = Path.ChangeExtension(rawImagePath, ".json");
                await File.WriteAllTextAsync(jsonFilePath, jsonString);
                //Console.WriteLine($"\tJSON log saved to: {jsonFilePath}");
            }
            else
            {
                Console.WriteLine("\tUnable to save JSON log: Raw image path not found.");
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
