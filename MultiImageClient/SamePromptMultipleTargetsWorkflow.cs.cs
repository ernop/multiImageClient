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
            foreach (var prompt in _abstractPromptGenerator.Run())
            {
                // Process all generators asynchronously
                var tasks = _generators.Select(generator=> ProcessGeneratorAsync(generator, prompt)).ToList();
                (bool IsSuccess, Image Image, ImageGeneratorApiType ImageGenerator)[] results = await Task.WhenAll(tasks);

                var combiner = new ImageCombiner();
                combiner.SaveMultipleImagesWithSubtitle(results, _settings, prompt.Prompt);
            }
        }

        private async Task<(bool IsSuccess, Image Image, ImageGeneratorApiType ImageGenerator)> ProcessGeneratorAsync(
            IImageGenerator generator,
            PromptDetails prompt)
        {
            try
            {
                var result = await generator.ProcessPromptAsync(prompt, _workflowContext.Stats);
                if (!result.IsSuccess || string.IsNullOrEmpty(result.Url))
                {
                    Logger.Log($"Failed to process prompt for {generator.GetApiType}: {result.ErrorMessage}");
                    return (false, null, generator.GetApiType);
                }

                var imageBytes = await ImageSaving.DownloadImageAsync(result);
                if (imageBytes.Length == 0)
                {
                    Logger.Log($"Failed to download image for {generator.GetApiType}");
                    return (false, null, generator.GetApiType);
                }

                using var ms = new MemoryStream(imageBytes);
                var image = Image.FromStream(ms);

                return (true, image, generator.GetApiType);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error processing generator {generator.GetApiType}: {ex.Message}");
                return (false, null, generator.GetApiType);
            }
        }
    }
}
