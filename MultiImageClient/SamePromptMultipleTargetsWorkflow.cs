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
        private readonly Dictionary<ImageGeneratorApiType, IImageGenerator> _generators;
        private readonly AbstractPromptSource _abstractPromptSource;
        private readonly Settings _settings;
        private readonly ImageManager _imageManager;
        private readonly MultiClientRunStats _stats;

        public SamePromptMultipleTargetsWorkflow(
            Dictionary<ImageGeneratorApiType, IImageGenerator> generators,
            AbstractPromptSource abstractPromptSource,
            Settings settings, MultiClientRunStats stats)
        {
            _generators = generators;
            _abstractPromptSource = abstractPromptSource;
            _settings = settings;
            _stats = stats;
        }

        public async Task RunAsync()
        {
            var stats = new MultiClientRunStats();
            foreach (var promptDetails in _abstractPromptSource.Prompts)
            {
                var tasks = new List<Task<TaskProcessResult>>();
                foreach (var kvp in _generators)
                {
                    var nam = kvp.Key;
                    var generator = kvp.Value;
                    TaskProcessResult result = await generator.ProcessPromptAsync(promptDetails);
                    await _imageManager.ProcessAndSaveAsync(result, generator);
                    tasks.Add(Task.FromResult(result));
                }

                foreach (var t in tasks)
                {
                    await t.ContinueWith(OnFinished, TaskScheduler.Default);
                }

                //// Process all generators asynchronously
                //var tasks = _generators.Select(async generator =>
                //{
                //    var theCopy = promptDetails.Clone();
                //    var result = await generator.ProcessPromptAsync(theCopy, stats);
                //    await _workflowContext.ImageManager.ProcessAndSaveAsync(result, _abstractPromptSource, stats);
                //}).ToList();
                
                await Task.WhenAll(tasks);

                var combiner = new ImageCombiner();
                //combiner.Save(tasks, _settings, promptDetails.Prompt);
            }
        }

        private void OnFinished(Task<TaskProcessResult> task)
        {

            if (task.IsFaulted)
            {
                Logger.Log($"Task faulted: {task.Exception?.Flatten().InnerException}");
                return;
            }
            if (task.IsCanceled)
            {
                Logger.Log("Task was canceled.");
                return;
            }

            var res = task.Result;
            Logger.Log($"Finished in {res.CreateTotalMs + res.DownloadTotalMs} ms, {res.PromptDetails.Show()}");
        }
    }
}
