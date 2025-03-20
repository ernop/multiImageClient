using MultiImageClient.promptGenerators;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class PromptToImageWithStepsWorkflow : IWorkflow
    {
        private readonly WorkflowContext _workflowContext;
        private readonly BFLService _BFLService;
        private readonly IdeogramService _IdeogramService;
        private readonly Dalle3Service _Dalle3Service;
        private readonly RecraftService _RecraftService;
        private readonly ClaudeService _ClaudeService;

        public PromptToImageWithStepsWorkflow(
            WorkflowContext workflowContext,
            BFLService bflService,
            IdeogramService ideogramService,
            Dalle3Service dalle3Service,
            RecraftService recraftService,
            ClaudeService claudeService)
        {
            _workflowContext = workflowContext;
            _BFLService = bflService;
            _IdeogramService = ideogramService;
            _Dalle3Service = dalle3Service;
            _RecraftService = recraftService;
            _ClaudeService = claudeService;
        }

        public async Task RunAsync()
        {
            var basePromptGenerator = new LoadFromFile(_workflowContext.Settings, "");
            var steps = new List<ITransformationStep>();
            var stats = new MultiClientRunStats();
            var generators = new List<IImageGenerator>
            {
                new IdeogramGenerator(_IdeogramService),
                new BFLGenerator(_BFLService),
                new RecraftGenerator(_RecraftService)
            };

            foreach (var promptDetails in basePromptGenerator.Run())
            {
                stats.PrintStats();
                Logger.Log($"\n--- Processing prompt: {promptDetails.Show()} ---");

                // Apply transformation steps
                foreach (var step in steps)
                {
                    var res = await step.DoTransformation(promptDetails, stats);
                    if (!res)
                    {
                        Logger.Log($"\tStep {step.Name} failed: {promptDetails.Show()}");
                        continue;
                    }
                    Logger.Log($"\tStep:{step.Name} => {promptDetails.Show()}");
                }

                for (int jj = 0; jj < basePromptGenerator.FullyResolvedCopiesPer; jj++)
                {
                    foreach (var generator in generators)
                    {
                        var theCopy = promptDetails.Clone();
                        var result = await generator.ProcessPromptAsync(theCopy, stats);

                        await _workflowContext.ImageManager.ProcessAndSaveAsync(result, basePromptGenerator, stats);
                    }
                    await Task.Delay(500);
                }
            }

            Logger.Log("All tasks completed.");
            stats.PrintStats();
        }
    }
}
