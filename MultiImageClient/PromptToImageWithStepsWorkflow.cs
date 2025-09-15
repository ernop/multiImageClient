using MultiImageClient.promptGenerators;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class PromptToImageWithStepsWorkflow : IWorkflow
    {
        private readonly WorkflowContext _workflowContext;
        private readonly IEnumerable<IImageGenerator> _generators;
        private readonly AbstractPromptGenerator _abstractPromptGenerator;
        private readonly Settings _settings;

        public PromptToImageWithStepsWorkflow(
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
            var basePromptGenerator = new LoadFromFile(_workflowContext.Settings, "");
            var steps = new List<ITransformationStep>();
            var stats = new MultiClientRunStats();

            var allTasks = new List<Task>();

            foreach (var promptDetails in basePromptGenerator.Run())
            {
                Logger.Log($"\n--- Processing prompt: {promptDetails.Index}");

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


                var tasks = _generators.Select(async generator =>
                {
                    var theCopy = promptDetails.Clone();
                    TaskProcessResult result = await generator.ProcessPromptAsync(theCopy, stats);
                    await _workflowContext.ImageManager.ProcessAndSaveAsync(result, basePromptGenerator, stats);
                    return result;
                }).ToList();

                foreach (var t in tasks)
                {
                    await t.ContinueWith(OnFinished, TaskScheduler.Default);
                }

                allTasks.AddRange(tasks);
                stats.PrintStats();
                Console.WriteLine($"kicked off task.");
            }

            while (allTasks.Any(t => !t.IsCompleted))
            {
                var completed = allTasks.Count(t => t.IsCompleted);
                var remaining = allTasks.Count - completed;
                Logger.Log($"Status: {completed}/{allTasks.Count} completed, {remaining} remaining …");
                await Task.WhenAny(Task.Delay(5000), Task.WhenAll(allTasks));
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