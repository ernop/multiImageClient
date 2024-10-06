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
        public abstract IEnumerable<PromptDetails> GetPrompts();
        public abstract Func<string, string> GetCleanPrompt();
        public abstract int GetImageCreationLimit();
        public abstract int GetCopiesPer();
        public abstract string GetPrefix();
        public abstract IEnumerable<string> GetVariants();
        public abstract string GetSuffix();
        public abstract bool GetRandomizeOrder();
        public abstract string GetName();
        public Settings Settings { get; set; }

        public AbstractPromptGenerator(Settings settings)
        {
            Settings = settings;
        }

        public IEnumerable<PromptDetails> Run()
        {
            var returnedCount = 0;
            foreach (var variantText in GetVariants())
            {
                for (var ii = 0; ii < GetCopiesPer(); ii++)
                {
                    foreach (var prompt in GetPrompts())
                    {
                        var cleanPrompt = GetCleanPrompt()(prompt.Prompt);
                        if (string.IsNullOrWhiteSpace(cleanPrompt)) { 
                            continue; 
                        }
                        var usingPrompt = $"{GetPrefix()} {variantText} \"{cleanPrompt}\" {GetSuffix()}";
                        var initialStep = new ImageConstructionStep("original prompt", prompt.Prompt);
                        var cleanedStep = new ImageConstructionStep("prepared prompt", usingPrompt);
                        var filename = $"{GetName()}_{prompt.Filename}";
                        if (!string.IsNullOrWhiteSpace(variantText))
                        {
                            filename = $"{GetName()}_{variantText}_{prompt.Filename}";
                        }
                        yield return new PromptDetails
                        {
                            Prompt = usingPrompt,
                            Filename = filename,
                            ImageConstructionSteps = new List<ImageConstructionStep> { initialStep, cleanedStep }
                        };
                        returnedCount++;
                        if (returnedCount >= GetImageCreationLimit()) yield break;
                    }
                    if (returnedCount >= GetImageCreationLimit()) yield break;
                }
                if (returnedCount >= GetImageCreationLimit()) yield break;
            }
        }
    }
}