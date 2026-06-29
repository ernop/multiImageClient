#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Samples the people-fixture prompt matrix once, then runs that exact
    /// prompt set through each provider generator and creates one contact sheet
    /// per provider.
    public class ProviderSampleShowcaseWorkflow
    {
        public async Task<List<string>> RunAsync(
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var sampleSize = options.Limit == int.MaxValue ? 15 : Math.Max(1, options.Limit);
            var prompts = LoadPrompts(options.ProviderSampleFilePath, sampleSize);
            var providerConcurrency = options.PromptConcurrency > 1 ? options.PromptConcurrency : 5;

            var groups = new GeneratorGroups(settings, providerConcurrency, stats);
            var generators = FilterGenerators(groups.GetProviderSampleReviewSet(), options.ProviderSampleProviders).ToList();
            var imageManager = new ImageManager(settings, stats);
            var samplePath = string.IsNullOrWhiteSpace(options.ProviderSampleFilePath)
                ? SavePromptSample(settings, prompts)
                : options.ProviderSampleFilePath;

            Logger.Log(string.IsNullOrWhiteSpace(options.ProviderSampleFilePath)
                ? $"Provider sample showcase: sampled {prompts.Count} people-fixture prompt(s) from {PortraitFixturePrompts.BuildAll().Count} total."
                : $"Provider sample showcase: loaded {prompts.Count} people-fixture prompt(s) from {options.ProviderSampleFilePath}.");
            Logger.Log($"Provider sample showcase: per-provider request concurrency = {providerConcurrency}.");
            Logger.Log($"Provider sample showcase: retry failed prompts = {options.ProviderSampleRetryFailures}.");
            Logger.Log($"Provider sample showcase prompt list: {samplePath}");
            if (!string.IsNullOrWhiteSpace(options.ProviderSampleProviders))
            {
                Logger.Log($"Provider sample showcase provider filter: {options.ProviderSampleProviders}");
            }
            foreach (var g in generators)
            {
                Logger.Log($"  - {g.GetGeneratorSpecPart()}  (~${g.GetCost():0.###})");
            }

            var outputPaths = new List<string>();
            foreach (var generator in generators)
            {
                var keyProblem = ProviderKeyValidator.DescribeKeyProblem(generator.ApiType, settings);
                if (keyProblem != null)
                {
                    Logger.Log($"Provider sample showcase :: {GeneratorContactSheetRunner.Flatten(generator.GetGeneratorSpecPart())} :: SKIPPED ({keyProblem})");
                    continue;
                }

                var header = $"Provider sample contact sheet - {generator.GetGeneratorSpecPart()} - {prompts.Count} people-fixture prompts";
                var outPath = await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                    generator,
                    prompts,
                    imageManager,
                    settings,
                    stats,
                    runLabel: "Provider sample showcase",
                    sheetHeader: header,
                    retryFailures: options.ProviderSampleRetryFailures,
                    openWhenDone: true);

                if (!string.IsNullOrWhiteSpace(outPath))
                {
                    outputPaths.Add(outPath);
                }
            }

            Logger.Log($"Provider sample showcase complete: {outputPaths.Count}/{generators.Count} contact sheet(s) created.");
            return outputPaths;
        }

        private static IReadOnlyList<string> LoadPrompts(string promptFilePath, int sampleSize)
        {
            if (string.IsNullOrWhiteSpace(promptFilePath))
            {
                return PortraitFixturePrompts.RandomSample(sampleSize);
            }

            if (!File.Exists(promptFilePath))
            {
                throw new FileNotFoundException($"Provider sample prompt file not found: {promptFilePath}", promptFilePath);
            }

            var prompts = File.ReadAllLines(promptFilePath)
                .Select(CleanPromptLine)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(sampleSize)
                .ToList();

            if (prompts.Count == 0)
            {
                throw new InvalidOperationException($"Provider sample prompt file contained no prompts: {promptFilePath}");
            }

            return prompts;
        }

        private static string CleanPromptLine(string line)
        {
            var trimmed = line.Trim();
            var dotIndex = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (dotIndex > 0 && trimmed.Take(dotIndex).All(char.IsDigit))
            {
                return trimmed.Substring(dotIndex + 2).Trim();
            }
            return trimmed;
        }

        private static IEnumerable<IImageGenerator> FilterGenerators(
            IEnumerable<IImageGenerator> generators,
            string providerFilter)
        {
            if (string.IsNullOrWhiteSpace(providerFilter))
            {
                return generators;
            }

            var filters = providerFilter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToList();

            return generators.Where(g =>
            {
                var label = $"{g.GetGeneratorSpecPart()} {g.ApiType}".ToLowerInvariant();
                return filters.Any(label.Contains);
            });
        }

        private static string SavePromptSample(Settings settings, IReadOnlyList<string> prompts)
        {
            var folder = Path.Combine(settings.ImageDownloadBaseFolder, DateTime.Now.ToString("yyyy-MM-dd-dddd"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"provider_people_fixture_sample_prompts_{DateTime.Now:yyyyMMddHHmmss}.txt");
            var lines = prompts.Select((p, i) => $"{i + 1}. {p}");
            File.WriteAllLines(path, lines);
            DlMirror.Copy(path, settings.FlatImageMirrorPath);
            return path;
        }
    }
}
