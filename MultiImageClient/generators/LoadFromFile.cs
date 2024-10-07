using MultiClientRunner;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;

namespace MultiClientRunner
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class LoadFromFile : AbstractPromptGenerator
    {
        private string FilePath { get; set; }
        public LoadFromFile(Settings settings, string filePath) : base(settings)
        {
            FilePath = filePath;
        }
        public override string Name => nameof(LoadFromFile);
        
        public override int ImageCreationLimit => 500;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => true;
        public override string Prefix => "";
        public override IEnumerable<string> Variants => new List<string> { "" };
        public override string Suffix => " Describe and expanded more intensified version of this initial kernel of an idea, using about 120 words, as prose with no newlines, full of details matching the THEME and optionally including text and/or the word 'typography' if you think it would benefit from having text within it, based on the input as you see and the apparent desire of the user!";
        public override Func<string, string> CleanPrompt => (arg) => arg.Trim().Trim('"').Trim('\'').Trim();

        private IEnumerable<PromptDetails> GetPrompts() {
        {
            var prompts = File.ReadAllLines(FilePath);
            var res = new List<PromptDetails>();
            foreach (var prompt in prompts)
            {
                if (prompt.Contains("{") || prompt.Contains("["))
                    continue;
                var pd = new PromptDetails();
                pd.ReplacePrompt(prompt, "initial prompt", prompt);
                pd.Filename = prompt;
                res.Add(pd);
            }

            return res.OrderBy(x => Random.Shared.Next());
        }
        }

        public override IEnumerable<PromptDetails> Prompts => GetPrompts();
    }
}

