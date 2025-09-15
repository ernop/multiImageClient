using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class SamePromptMultipleTargetsWorkflow : IWorkflow
    {
        private readonly WorkflowContext _workflowContext;
        private readonly IEnumerable<IImageGenerator> _generators;
        private readonly AbstractPromptGenerator _abstractPromptGenerator;
        private readonly Settings _settings;

        public SamePromptMultipleTargetsWorkflow(
            WorkflowContext workflowContext,
            IEnumerable<IImageGenerator> generators,
            AbstractPromptGenerator abstractPromptGenerator,
            Settings settings)
        {
            _workflowContext = workflowContext;
            _generators = generators;
            _abstractPromptGenerator = abstractPromptGenerator;
            _settings = settings;
        }

        public async Task RunAsync()
        {
            var stats = new MultiClientRunStats();
            foreach (var promptDetails in _abstractPromptGenerator.Run())
            {
                // Process all generators asynchronously
                var tasks = _generators.Select(async generator =>
                {
                    var theCopy = promptDetails.Clone();
                    var result = await generator.ProcessPromptAsync(theCopy, stats);
                    await _workflowContext.ImageManager.ProcessAndSaveAsync(result, _abstractPromptGenerator, stats);
                }).ToList();
                
                await Task.WhenAll(tasks);

                var combiner = new ImageCombiner();
                //combiner.SaveMultipleImagesWithSubtitle(tasks, _settings, promptDetails.Prompt);
            }
        }
    }
}
