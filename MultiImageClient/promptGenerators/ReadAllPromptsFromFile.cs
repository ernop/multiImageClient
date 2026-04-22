using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiImageClient
{
    /// Loads every line of every file listed in Settings.PromptFiles
    /// (plus the legacy LoadPromptsFrom, if set) into a single pool and
    /// yields prompts from that pool in random order, up to ImageCreationLimit.
    ///
    /// Fails hard with a clear message if:
    ///   - no prompt files are configured, or
    ///   - any configured file is missing or unreadable.
    public class ReadAllPromptsFromFile : AbstractPromptSource
    {
        public ReadAllPromptsFromFile(Settings settings, string _unusedLegacyPath) : base(settings)
        {
        }

        public override string Name => nameof(ReadAllPromptsFromFile);
        public override int ImageCreationLimit => int.MaxValue;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 1;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override string Suffix => "";

        public override IEnumerable<PromptDetails> Prompts
        {
            get
            {
                var files = new List<string>();
                if (Settings.PromptFiles != null)
                {
                    files.AddRange(Settings.PromptFiles.Where(p => !string.IsNullOrWhiteSpace(p)));
                }
                if (!string.IsNullOrWhiteSpace(Settings.LoadPromptsFrom))
                {
                    files.Add(Settings.LoadPromptsFrom);
                }

                if (files.Count == 0)
                {
                    throw new InvalidOperationException(
                        "settings.json: PromptFiles is empty. Add the path(s) to your prompts .txt file(s), e.g. \"PromptFiles\": [\"C:\\\\proj\\\\multiImageClient\\\\prompts.txt\"].");
                }

                var missing = files.Where(f => !File.Exists(f)).ToList();
                if (missing.Count > 0)
                {
                    throw new FileNotFoundException(
                        $"settings.json: PromptFiles contains file(s) that do not exist: {string.Join(", ", missing)}. Fix the path(s) in settings.json.");
                }

                var allPromptsRaw = new List<string>();
                foreach (var fp in files)
                {
                    foreach (var line in File.ReadAllLines(fp))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            allPromptsRaw.Add(line);
                        }
                    }
                }

                if (allPromptsRaw.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"settings.json: PromptFiles {string.Join(", ", files)} contained no non-blank lines.");
                }

                Logger.Log($"Loaded {allPromptsRaw.Count} prompts from {files.Count} file(s): {string.Join(", ", files)}");

                var order = Enumerable.Range(0, allPromptsRaw.Count).ToList();
                if (RandomizeOrder)
                {
                    for (int i = order.Count - 1; i > 0; i--)
                    {
                        var j = Random.Shared.Next(0, i + 1);
                        (order[i], order[j]) = (order[j], order[i]);
                    }
                }

                var limit = Math.Min(ImageCreationLimit, order.Count);
                for (var ii = 0; ii < limit; ii++)
                {
                    var usePrompt = allPromptsRaw[order[ii]];
                    var pd = new PromptDetails();
                    pd.ReplacePrompt(usePrompt, usePrompt, TransformationType.InitialPrompt);
                    yield return pd;
                }
            }
        }
    }
}
