﻿using System;
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
            //var basePromptGenerator = new LoadFromFile(settings, "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrivatePrompts.txt");
            //var basePromptGenerator = new WriteHere(settings);
            var steps = new List<ITransformationStep>();

            var stats = new MultiClientRunStats();
            var processingTasks = new List<Task>();

            var basePromptGenerator = new SinglePromptGenerator(new List<string>() { "A heist of IDEAS from the No-osphere, by a man undergoing 'Internal LLM Systems' therapy (the modern version of IFS; this new system hypothesizes that inside, we have enough similarities to LLMs that techniques of interacting with them will also work on ourselves) - @redphonecrypto" }, 10, 2, 100, settings);
            var rstep = new RandomizerStep();
            steps.Add(rstep);

            var stylizerStep = new StylizerStep();
            steps.Add(stylizerStep);

            //var claudeStep = new ClaudeRewriteStep("dear claude, please put this in terms an insane medieval hierophant would use, emitting 100 obscure words, line noise, negations, OVERLOAD acronyms, japonisme, latin rosicrutian texts with a flair for the visually appealing and brutalist architure settings of fascination, retaining  refs to $GOAT and backrooms. NO newlines, condensed abstract words, wildness. Include references to LLM-based AI cryptocoins, the Crypto is getting intelligent and 'waking up' to spend a buck on coffee. Who is the NEXT?  ", "", claudeService, 0.876456m);
            //steps.Add(claudeStep);

            var claudeStep2 = new ClaudeRewriteStep("Clearly summarize this and create a diagram as well as an illustration and schematic.  The consumer of your output will be a crack team of AI-human artist hybrids so please give them all detail you can in the transl8n, to help us fully illustrate it. Emit 200 words no newlines ", "", claudeService, 1.0m);
            steps.Add(claudeStep2);

            var generators = new List<IImageGenerator>();
            generators.Add(new BFLGenerator(_BFLService));


            //generators.Add(new IdeogramGenerator(_IdeogramService));
            //generators.Add(new Dalle3Generator(_Dalle3Service));
            //var claudeStep = new ClaudeRewriteStep("", "Expand the preceding INPUT data with unusual but fitting obscure, archiac, poetic, punning words which still focus on the ultimate goal, to elucidate new, created-by-you, well-chosen details on her style, beliefs, life, heart, mind, soul, appearance, and habits, to produce a hyper-ULTRA-condensed prose output which still encapsulates [mystery-universe-gaia-purity-edge-tao] in all wonder, while retaining a thrilling environment, glitchcore, unspoken musings of null-thought, arcana, jargon, mumbled transliterations of mega-negative dimensional thought space and poetry CONCRETE illustration style. DO IT.  ALSO: you must emit MANY words and arrange them in a beautiful ASCII ART style of width 50 characters.", claudeService, 1.0m);
            //steps.Add(claudeStep);
            //var llamaRewriteStep = new LLAMARewriteStep("Rewrite this, adding details: ","Output 100 words of clear, simple text, describing an image which you imagine in detail.",llamaService);
            //steps.Add(llamaRewriteStep);

            //var stylizerStep = new StylizerStep();
            //steps.Add(stylizerStep);


            //var claudeStep = new ClaudeRewriteStep("", "Draw this making it full of many details, into a specific, news-paper photography style description of an image, most important and largest elements first as well as textures, colors, styles, then going into super details about the rest of it, which you should create to make the visual effect intense, interesting, and exceptional. Generate MANY words such as 500 or even 600, and then add a caption/title in the form of a beautiful ASCII ART style of width 50 characters. Our overall theme is purity, simplicity, natural forms, SATISFYING images that are fun to look at;", claudeService, 1.0m);

            //var mmstep = new ManualModificationStep("A dramatically simple, emotional, lush and colorful razor-sharp vector art graphic illustration style glowing and pure muted or intense, evocative image on the following theme: ","");
            //steps.Add(mmstep);
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
