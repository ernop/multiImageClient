using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;

using MultiImageClient.promptGenerators;
using System.Reflection.Emit;
using static MultiImageClient.Program;


namespace MultiImageClient
{   
    /// We only well-control the initial prompt text generation. The actual process of applying various steps, logging etc is all hardcoded in here which is not ideal.
    public class Program
    {
        private static BFLService _BFLService;
        private static IdeogramService _IdeogramService;
        private static Dalle3Service _Dalle3Service;
        private static GptImageOneService _GptImageOneService;
        private static RecraftService _RecraftService;
        private static ClaudeService _ClaudeService;
 
        private static void InitializeServices(Settings settings, int concurrency)
        {
            _BFLService = new BFLService(settings.BFLApiKey, concurrency);
            _IdeogramService = new IdeogramService(settings.IdeogramApiKey, concurrency);
            _Dalle3Service = new Dalle3Service(settings.OpenAIApiKey, concurrency);
            _GptImageOneService = new GptImageOneService(settings.OpenAIApiKey, concurrency);
            _RecraftService = new RecraftService(settings.RecraftApiKey, concurrency);
            _ClaudeService = new ClaudeService(settings.AnthropicApiKey, concurrency);
        }

        private static IWorkflow CreateWorkflow(GeneralActionType actionType, WorkflowContext context, AbstractPromptGenerator abstractPromptGenerator, Settings settings){
            
            var generators = new List<IImageGenerator>
            {
                new BFLGenerator(_BFLService, ImageGeneratorApiType.BFLv11),
                new BFLGenerator(_BFLService, ImageGeneratorApiType.BFLv11Ultra),
                new IdeogramGenerator(_IdeogramService, ImageGeneratorApiType.Ideogram),
                new Dalle3Generator(_Dalle3Service, ImageGeneratorApiType.Dalle3),
                new GptImageOneGenerator(_GptImageOneService, ImageGeneratorApiType.GptImage1),
                new RecraftGenerator(_RecraftService, ImageGeneratorApiType.Recraft),
            };
            return actionType switch
            {
                GeneralActionType.PromptToImageWithSteps =>
                   new PromptToImageWithStepsWorkflow(context, generators, abstractPromptGenerator, settings),
                GeneralActionType.SamePromptMultipleTargets =>
                    new SamePromptMultipleTargetsWorkflow(context, generators, abstractPromptGenerator, settings),

                GeneralActionType.ImageToTextToImageWithSteps => throw new NotImplementedException(),
                _ => throw new NotImplementedException("Unknown workflow")
            };
        }

        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 1;
            
            InitializeServices(settings, concurrency);

            GeneralActionType gat = GeneralActionType.SamePromptMultipleTargets;
            gat = GeneralActionType.PromptToImageWithSteps;
            var abstractPromptGenerator = new LoadFromFile(settings, "");

            var im = new ImageManager(settings);
            var workflowContext = new WorkflowContext(settings, new MultiClientRunStats(), im);
            var workflow = CreateWorkflow(gat, workflowContext, abstractPromptGenerator, settings);
            await workflow.RunAsync();
        }
    }
}
