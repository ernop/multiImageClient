using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace MultiImageClient.promptGenerators
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class LoadFromFile : AbstractPromptGenerator
    {
        private string FilePath { get; set; }
        public LoadFromFile(Settings settings, string path) : base(settings)
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

        public override string Name => nameof(LoadFromFile);

        public override int ImageCreationLimit => 109;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 1;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override bool SaveFinalPrompt => true;
        public override bool SaveInitialIdea => true;
        public override bool SaveFullAnnotation => true;
        
        private IEnumerable<PromptDetails> GetPrompts()
        {
            var sourceFPs = new List<string>() {
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrivatePrompts.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts-private.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts.txt",
                "D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\prompts3.txt"
            };
            
            if (!string.IsNullOrEmpty(FilePath)) {
                sourceFPs.Add(FilePath);
            }

            var allPromptsRaw = new List<string>();

            foreach (var fp in sourceFPs)
            {
                var items = File.ReadAllLines(fp).ToList();
                foreach (var usePrompt in items)
                {
                    //if ((usePrompt.Contains("{{") || usePrompt.Contains("[[")) && !(usePrompt.Contains("[[[") || usePrompt.Contains("}}}")))
                    //{
                    //    continue;
                    //}
                    if (string.IsNullOrEmpty(usePrompt))
                    {
                        continue;
                    }
                    //var good = false;
                    //if (usePrompt.ToLower().Contains("japan") || usePrompt.ToLower().Contains("girl") || usePrompt.ToLower().Contains("woman") || usePrompt.ToLower().Contains("high school") || usePrompt.ToLower().Contains("chinese") || usePrompt.ToLower().Contains("asian"))
                    //{
                    //    good = true;
                    //}
                    //if (!good)
                    //{
                    //    continue;
                    //}
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
                pd.IdentifyingConcept = "";

                yield return pd;
            }
        }

        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

