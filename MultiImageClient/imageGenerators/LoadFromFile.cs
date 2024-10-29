using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace MultiImageClient
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class LoadFromFile : AbstractPromptGenerator
    {
        private string FilePath { get; set; }
        public LoadFromFile(Settings settings, string path) : base(settings)
        {
            if (!System.IO.File.Exists(path))
            {
                new Exception("Requested path: " + path + " does not exist, ending.");
            }
            FilePath = path;
        }
        public override string Name => nameof(LoadFromFile);

        public override int ImageCreationLimit => 900;
        public override int CopiesPer => 1;
        public override int FullyResolvedCopiesPer => 3;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override Func<string, string> CleanPrompt => (arg) => arg.Trim().Trim();
        public override bool UseIdeogram => false;
        public override bool AlsoDoVersionSkippingClaude => false;
        public override bool SaveFinalPrompt => true;
        public override bool SaveInitialIdea => true;
        public override bool SaveFullAnnotation => true;
        public override bool TryBothBFLUpsamplingAndNot => true;

        private IEnumerable<PromptDetails> GetPrompts()
        {

            var textPrompts = File.ReadAllLines(FilePath).ToList();
            var prompts2 = File.ReadAllLines("D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts-private.txt");
            var prompts3 = File.ReadAllLines("D:\\proj\\multiImageClient\\IdeogramHistoryExtractor\\myPrompts\\myPrompts.txt");
            var prompts4 = File.ReadAllLines("D:\\proj\\prompts3.txt");
            textPrompts.AddRange(prompts2);
            textPrompts.AddRange(prompts3);
            textPrompts.AddRange(prompts4);
            Console.WriteLine($"loaded {textPrompts.Count} prompts total.");
            var res = new List<PromptDetails>();
            foreach (var textPrompt in textPrompts)
            {
                var usePrompt = textPrompt;
                if ((usePrompt.Contains("{{") || usePrompt.Contains("[[")) && (!(usePrompt.Contains("[[[") || usePrompt.Contains("}}}"))))
                {
                    //Console.WriteLine($"Skipping: {usePrompt}");
                    continue;
                }

                while (usePrompt.Contains("  "))
                {
                    usePrompt = usePrompt.Replace("  ", " ");
                }

                usePrompt = usePrompt.Trim();
                var pd = new PromptDetails();
                pd.ReplacePrompt(usePrompt,usePrompt, TransformationType.InitialPrompt);
                pd.IdentifyingConcept = "";
                
                res.Add(pd);
            }

            Console.WriteLine($"loaded {res.Count} items.");
            return res.OrderBy(x => Random.Shared.Next());
        }

        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

