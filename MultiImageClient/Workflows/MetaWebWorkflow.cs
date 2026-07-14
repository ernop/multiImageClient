#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// --meta-web: batch prompts through the meta.ai consumer web app (Muse
    /// Image) by driving a real Playwright browser session. Mirrors
    /// GrokWebWorkflow; single-session concurrency of 1. One contact sheet for
    /// the run. First run: --meta-web-headed to log in once; the persistent
    /// profile keeps the session afterwards.
    public class MetaWebWorkflow
    {
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var clientOptions = MetaWebClient.BuildOptions(
                settings,
                cookieOverride: options.MetaWebCookies,
                headedOverride: options.MetaWebHeaded);

            var problem = MetaWebClient.DescribeAvailabilityProblem(clientOptions);
            if (problem != null)
            {
                Console.Error.WriteLine($"Meta web aborted: {problem}");
                return null;
            }

            var prompts = promptSource.Prompts
                .Select(p => p.Prompt)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Take(options.Limit == int.MaxValue ? int.MaxValue : options.Limit)
                .ToList();

            if (prompts.Count == 0)
            {
                Console.Error.WriteLine("Meta web aborted: no prompts found. Use --prompt, --prompt-file, or PromptFiles.");
                return null;
            }

            MetaWebClient client;
            try
            {
                client = new MetaWebClient(clientOptions);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Meta web aborted: {ex.Message}");
                Logger.Log($"Meta web aborted: {ex.Message}");
                return null;
            }

            await using (client)
            {
                var generator = new MetaWebImagineGenerator(
                    client,
                    maxConcurrency: 1,
                    stats);

                Logger.Log($"Meta web: prompts={prompts.Count}, headed={clientOptions.Headed}, profile={clientOptions.BrowserProfilePath}");
                return await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                    generator,
                    prompts,
                    new ImageManager(settings, stats),
                    settings,
                    stats,
                    runLabel: "Meta web",
                    sheetHeader: "Meta Web Imagine (Muse Image)",
                    openWhenDone: options.OpenImages);
            }
        }
    }
}
