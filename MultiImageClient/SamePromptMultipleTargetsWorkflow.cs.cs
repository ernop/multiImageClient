using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class SamePromptMultipleTargetsWorkflow : IWorkflow
    {
        private readonly WorkflowContext _workflowContext;
        private readonly IdeogramService _IdeogramService;
        private readonly BFLService _BFLService;
        private readonly Dalle3Service _Dalle3Service;
        private readonly RecraftService _RecraftService;
        private readonly ClaudeService _ClaudeService;
        private readonly AbstractPromptGenerator _AbstractPromptGenerator;
        private readonly Settings _settings;

        public SamePromptMultipleTargetsWorkflow(WorkflowContext workflowContext,
            BFLService bflService,
            IdeogramService ideogramService,
            Dalle3Service dalle3Service,
            RecraftService recraftService,
            ClaudeService claudeService,
            AbstractPromptGenerator apg,
            Settings settings)
        {
            _workflowContext = workflowContext;
            _BFLService = bflService;
            _IdeogramService = ideogramService;
            _Dalle3Service = dalle3Service;
            _RecraftService = recraftService;
            _ClaudeService = claudeService;
            _AbstractPromptGenerator = apg;
            _settings = settings;
        }

        public async Task RunAsync()
        {
            var generators = new List<IImageGenerator>
            {
                new IdeogramGenerator(_IdeogramService),
                new BFLGenerator(_BFLService),
                new RecraftGenerator(_RecraftService)
            };

            var multiResults = new MultiGeneratorResults();
            foreach (var prompt in _AbstractPromptGenerator.Run())
            {

                foreach (var generator in generators)
                {
                    var theCopy = prompt.Clone();
                    var res = await generator.ProcessPromptAsync(theCopy, _workflowContext.Stats);
                    if (!res.IsSuccess)
                    {
                        Logger.Log(res.ToString());
                        continue;
                    }
                    multiResults.results[generator.GetApiType] = res;

                    // Always handle image saving, caching, etc:
                    await _workflowContext.ImageManager.ProcessAndSaveAsync(res, _AbstractPromptGenerator, _workflowContext.Stats);
                }

                var ic = new ImageCombiner();
                ic.SaveMultipleImagesWithSubtitle(multiResults.results, _settings, prompt.Prompt);
            }
        }
    }
}
