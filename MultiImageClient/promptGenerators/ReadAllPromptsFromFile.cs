using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace MultiImageClient
{
    // the most common method for getting prompts to test, just load them from a big file of old ones I have.
    public class ReadAllPromptsFromFile : AbstractPromptSource
    {
        private string FilePath { get; set; }

        public ReadAllPromptsFromFile(Settings settings, string path) : base(settings)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                FilePath = path;
            }
            else
            {
                FilePath = "";
            }
        }

        public override string Name => nameof(ReadAllPromptsFromFile);
        public override int ImageCreationLimit => 100;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 1;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override string Suffix => "";
        public override IEnumerable<PromptDetails> Prompts
        {
            get
            {
                var sourceFPs = new List<string>() {
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrivatePrompts.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts-private.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\prompts3.txt"
            };

                if (!string.IsNullOrEmpty(FilePath))
                {
                    sourceFPs.Add(FilePath);
                }

                var allPromptsRaw = new List<string>();

                foreach (var fp in sourceFPs)
                {
                    var items = File.ReadAllLines(fp).ToList();
                    foreach (var usePrompt in items)
                    {
                        if (string.IsNullOrEmpty(usePrompt))
                        {
                            continue;
                        }
                        allPromptsRaw.Add(usePrompt);
                    }
                }

                Logger.Log($"loaded {allPromptsRaw.Count} prompts total.");

                for (var ii = 0; ii < ImageCreationLimit; ii++)
                {
                    var aa = Random.Shared.Next(0, allPromptsRaw.Count);
                    var pd = new PromptDetails();
                    var usePrompt = allPromptsRaw[aa];
                    pd.ReplacePrompt(usePrompt, usePrompt, TransformationType.InitialPrompt);

                    yield return pd;
                }
            }
        }

        
    }
}

