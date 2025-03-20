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
        private static RecraftService _RecraftService;
        private static ClaudeService _ClaudeService;
 
        private static void InitializeServices(Settings settings, int concurrency)
        {
            _BFLService = new BFLService(settings.BFLApiKey, concurrency);
            _IdeogramService = new IdeogramService(settings.IdeogramApiKey, concurrency);
            _Dalle3Service = new Dalle3Service(settings.OpenAIApiKey, concurrency);
            _RecraftService = new RecraftService(settings.RecraftApiKey, concurrency);
            _ClaudeService = new ClaudeService(settings.AnthropicApiKey, concurrency);
        }

        private static IWorkflow CreateWorkflow(GeneralActionType actionType, WorkflowContext context, AbstractPromptGenerator apg, Settings settings){
            return actionType switch
            {
                GeneralActionType.PromptToImageWithSteps =>
                    new PromptToImageWithStepsWorkflow(context, _BFLService, _IdeogramService, _Dalle3Service, _RecraftService, _ClaudeService),
                GeneralActionType.SamePromptMultipleTargets =>
                    new SamePromptMultipleTargetsWorkflow(context, _BFLService, _IdeogramService, _Dalle3Service, _RecraftService, _ClaudeService, apg, settings),
                GeneralActionType.ImageToTextToImageWithSteps => throw new NotImplementedException(),
                _ => throw new NotImplementedException("Unknown workflow")
            };
        }

        static async Task Main(string[] args)
        {
            var settingsFilePath = "settings.json";
            var settings = Settings.LoadFromFile(settingsFilePath);
            var concurrency = 6;
            
            InitializeServices(settings, concurrency);

            GeneralActionType gat = GeneralActionType.SamePromptMultipleTargets;
            var apg = new LoadFromFile(settings, "");

            var im = new ImageManager(settings);
            var workflowContext = new WorkflowContext(settings, new MultiClientRunStats(), im);
            var workflow = CreateWorkflow(gat, workflowContext, apg, settings);
            await workflow.RunAsync();
        }
    }
}
