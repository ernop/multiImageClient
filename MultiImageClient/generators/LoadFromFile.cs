using MultiClientRunner;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace MultiClientRunner
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

        public override int ImageCreationLimit => 400;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => "";
        public override Func<string, string> CleanPrompt => (arg) => arg.Trim().Trim('"').Trim('\'').Trim();
        public override bool UseIdeogram => true;
        public override bool AlsoDoVersionSkippingClaude => true;

        private IEnumerable<PromptDetails> GetPrompts()
        {
            {
                var prompts = File.ReadAllLines(FilePath);
                var res = new List<PromptDetails>();
                foreach (var prompt in prompts)
                {
                    var usePrompt = prompt;
                    if (usePrompt.Contains("{") || usePrompt.Contains("["))
                        continue;
                    var pd = new PromptDetails();
                    while (usePrompt.Contains("  "))
                    {
                        usePrompt = usePrompt.Replace("  ", " ");
                    }
                    usePrompt = usePrompt.Trim();
                    pd.ReplacePrompt(usePrompt, "initial prompt", usePrompt);
                    pd.OriginalPromptIdea = usePrompt;
                    res.Add(pd);
                }

                return res.OrderBy(x => Random.Shared.Next());
            }
        }

        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

