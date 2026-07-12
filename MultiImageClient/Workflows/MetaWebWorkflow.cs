#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// --meta-web: batch prompts through the meta.ai consumer web app (Muse
    /// Image) using browser cookies. Mirrors GrokWebWorkflow; single-session
    /// concurrency of 1. One contact sheet for the run.
    public class MetaWebWorkflow
    {
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var cookiePath = !string.IsNullOrWhiteSpace(options.MetaWebCookies)
                ? options.MetaWebCookies
                : settings.MetaWebCookiePath;

            if (string.IsNullOrWhiteSpace(cookiePath))
            {
                Console.Error.WriteLine("Meta web aborted: set MetaWebCookiePath in settings.json or pass --meta-web-cookies /path/to/cookies.txt");
                return null;
            }

            if (!File.Exists(Settings.ExpandPath(cookiePath)))
            {
                Console.Error.WriteLine($"Meta web aborted: cookie file not found: {Settings.ExpandPath(cookiePath)}");
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

            // Per-run doc_id override wins over settings.json; settings supplies the fallback.
            var docId = !string.IsNullOrWhiteSpace(options.MetaWebDocId)
                ? options.MetaWebDocId
                : settings.MetaWebImageDocId;

            using var client = new MetaWebClient(
                MetaWebCookieLoader.LoadCookieHeader(cookiePath),
                imageDocId: docId,
                endpoint: settings.MetaWebGraphqlEndpoint,
                pollDocId: settings.MetaWebPollMediaDocId);

            var generator = new MetaWebImagineGenerator(
                client,
                maxConcurrency: 1,
                stats,
                orientation: options.MetaWebOrientation,
                numImages: options.MetaWebNumImages);

            Logger.Log($"Meta web: prompts={prompts.Count}, orientation={options.MetaWebOrientation}, doc_id={client.ImageDocId}, cookies={Settings.ExpandPath(cookiePath)}");
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
