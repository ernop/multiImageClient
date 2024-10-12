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

        //defaults
        public virtual bool UseClaude => true;
        public virtual bool UseIdeogram => false;
        public virtual bool UseBFL => true;
        public virtual bool UseDalle3 => false;
        public virtual bool SaveRaw => true;
        public virtual bool SaveFullAnnotation => true;
        public virtual bool SaveFinalPrompt => true;
        public virtual bool SaveInitialIdea => true;

        public virtual bool AlsoDoVersionSkippingClaude => false;

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
                        var usingPrompt = $"{Prefix} {variantText} {cleanPrompt} {Suffix}".Trim();
                        if (usingPrompt == cleanPrompt)
                        {

                        }
                        else
                        {
                            var promptToDisplayForUsers = usingPrompt;
                            if (promptToDisplayForUsers.Contains(promptDetails.Prompt))
                            {
                                promptToDisplayForUsers = promptToDisplayForUsers.Replace(promptDetails.Prompt, "{PROMPT}");
                            }
                            promptDetails.ReplacePrompt(usingPrompt, "cleaned and added pre/suffixes", promptToDisplayForUsers);
                        }
                        
                        var revisedPromptIdea = $"{Name}_{promptDetails.OriginalPromptIdea}";
                        if (!string.IsNullOrWhiteSpace(variantText))
                        {
                            revisedPromptIdea = $"{Name}_{promptDetails.OriginalPromptIdea}_{variantText}";
                        }
                        promptDetails.OriginalPromptIdea = revisedPromptIdea;
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