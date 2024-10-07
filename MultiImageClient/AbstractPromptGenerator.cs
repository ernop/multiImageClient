using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace MultiClientRunner
{
    /// <summary>
    /// generate as much as you can of the prompt in here.
    /// </summary>
    public abstract class AbstractPromptGenerator
    {
        public abstract IEnumerable<PromptDetails> Prompts { get; }
        public abstract Func<string, string> CleanPrompt { get; }
        public abstract int ImageCreationLimit { get; }
        public abstract int CopiesPer { get; }
        public abstract string Prefix { get; }
        public abstract IEnumerable<string> Variants { get; }
        public abstract string Suffix { get; }
        public abstract bool RandomizeOrder { get; }
        public abstract string Name { get; }
        public Settings Settings { get; set; }

        public AbstractPromptGenerator(Settings settings)
        {
            Settings = settings;
        }

        /// Implementers should have their .Run method called from Program.cs to iterate through your prompts.
        public IEnumerable<PromptDetails> Run()
        {
            var returnedCount = 0;
            foreach (var variantText in Variants)
            {
                for (var ii = 0; ii < CopiesPer; ii++)
                {
                    foreach (var promptDetails in Prompts)
                    {
                        var cleanPrompt = CleanPrompt(promptDetails.Prompt);
                        if (string.IsNullOrWhiteSpace(cleanPrompt)) { 
                            continue; 
                        }
                        var usingPrompt = $"{Prefix} {variantText} \"{cleanPrompt}\" {Suffix}";
                        var promptToDisplayForUsers = usingPrompt;
                        if (promptToDisplayForUsers.Contains(promptDetails.Prompt))
                        {
                            promptToDisplayForUsers = promptToDisplayForUsers.Replace(promptDetails.Prompt, "{PROMPT}");
                        }
                        promptDetails.ReplacePrompt(usingPrompt, "cleaned and added pre/suffixes", promptToDisplayForUsers);
                        
                        var filename = $"{Name}_{promptDetails.Filename}";
                        if (!string.IsNullOrWhiteSpace(variantText))
                        {
                            filename = $"{Name}_{variantText}_{promptDetails.Filename}";
                        }

                        yield return promptDetails;
                        returnedCount++;
                        if (returnedCount >= ImageCreationLimit) yield break;
                    }
                    if (returnedCount >= ImageCreationLimit) yield break;
                }
                if (returnedCount >= ImageCreationLimit) yield break;
            }
        }
    }
}