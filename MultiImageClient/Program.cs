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
            var basePromptGenerator = new StillLife(settings);

            //var basePromptGenerator = new SinglePromptGenerator(new List<string>() { "    Addiction comes out of the future, and there is a replicator interlock with money operating quite differently to reproductive investment, but guiding it even more inexorably towards capitalization. For the replicants money is not a matter of possession, but of liquidity/deterritorialization, and all the monetary processes on Earth are open to their excitement, irrespective of ownership. Money communicates with the primary process because of what it can melt, not what it can obtain.\r\n\r\n    Machinic desire can seem a little inhuman, as it rips up political cultures, deletes traditions, dissolves subjectivities, and hacks through security apparatuses, tracking a soulless tropism to zero control. This is because what appears to humanity as the history of capitalism is an invasion from the future by an artificial intelligent space that must assemble itself entirely from its enemy's resources. Digitocommodification is the index of a cyberpositively escalating technovirus, of the planetary technocapital singularity: a self-organizing insidious traumatism, virtually guiding the entire biological desiring-complex towards post-carbon replicator usurpation.\r\n\r\n    The reality principle tends to a consummation as the price system: a convergence of mathematico-scientific and monetary quantization, or technical and economic implementability. This is not a matter of an unknown quantity, but of a quantity that operates as a place-holder for the unknown, introducing the future as an abstract magnitude. Capital propagates virally in so far as money communicates addiction, replicating itself through host organisms whose boundaries it breaches, and whose desires it reprograms. It incrementally virtualizes production; demetallizing money in the direction of credit finance, and disactualizing productive force along the scale of machinic intelligence quotient. The dehumanizing convergence of these tendencies zeroes upon an integrated and automatized cyberpositive techno-economic intelligence at war with the macropod." }, 10, 2, 100, settings);
            //var rstep = new RandomizerStep();
            //steps.Add(rstep);

            //var stylizerStep = new StylizerStep();
            //steps.Add(stylizerStep);


            //var claudeStep2 = new ClaudeRewriteStep("Here is a piece of text. Our goal is to UNDERSTAND it, first. Then, Imagine and describe a specific image which symbolically summarizes this; in addition, create a diagram as well as an illustration and schematic.  The consumer of your output will be a crack team of AI-human artist hybrids so please give them all detail you can in the transl8n, to help us fully illustrate it. Emit 200 words no newlines. Imagine a specific scenario, format, composition style and medium, artist you choose who would be your muse, and describe what is in the image, the pixels, the layout, the words (shortphrases only) and make it hrdcore", "", claudeService, 1.0m);
            //steps.Add(claudeStep2);

           

            var generators = new List<IImageGenerator>();
            generators.Add(new BFLGenerator(_BFLService));


            //generators.Add(new IdeogramGenerator(_IdeogramService));
            //generators.Add(new Dalle3Generator(_Dalle3Service));
            //var claudeStep = new ClaudeRewriteStep("", "Expand the preceding INPUT data with unusual but fitting obscure, archiac, poetic, punning words which still focus on the ultimate goal, to elucidate new, created-by-you, well-chosen details on her style, beliefs, life, heart, mind, soul, appearance, and habits, to produce a hyper-ULTRA-condensed prose output which still encapsulates [mystery-universe-gaia-purity-edge-tao] in all wonder, while retaining a thrilling environment, glitchcore, unspoken musings of null-thought, arcana, jargon, mumbled transliterations of mega-negative dimensional thought space and poetry CONCRETE illustration style. DO IT.  ALSO: you must emit MANY words and arrange them in a beautiful ASCII ART style of width 50 characters.", claudeService, 1.0m);
            //steps.Add(claudeStep);
            //var llamaRewriteStep = new LLAMARewriteStep("Rewrite this, adding details: ","Output 100 words of clear, simple text, describing an image which you imagine in detail.",llamaService);
            //steps.Add(llamaRewriteStep);

            //var stylizerStep2 = new StylizerStep();
            //steps.Add(stylizerStep2);


            var claudeStep = new ClaudeRewriteStep("Create a text paragraph describing an incredible artwork based on this concept: ", "Draw this making it full of many details, into a specific stle description of an image, most important and largest elements first as well as textures, colors, styles, then going into super details about the rest of it, which you should create to make the visual effect intense, interesting, and exceptional. Generate MANY words such as 500 or even 600, our overall theme is clarity and directness, suited to the topic which you must strive and struggle to honor and maintain focus on. ", claudeService, 1.0m);

            steps.Add(claudeStep);
            
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
