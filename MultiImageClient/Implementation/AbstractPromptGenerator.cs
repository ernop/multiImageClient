using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

namespace MultiImageClient
{
    /// <summary>
    /// generate as much as you can of the prompt in here.
    /// </summary>
    public abstract class AbstractPromptGenerator
    {
        public abstract IEnumerable<PromptDetails> Prompts { get; }
        public abstract Func<string, string> CleanPrompt { get; }
        public abstract int ImageCreationLimit { get; }
        /// how many times we send this prompt as of this level.
        public abstract int CopiesPer { get; }

        /// i.e. how many times we send it to each image generator, after applying all prior manipulation steps
        public virtual int FullyResolvedCopiesPer { get; } = 1;
        public abstract string Prefix { get; }
        /// <summary>
        ///  Variant leading texts, if any, which you want to include after the global prefix.
        ///  
        /// The overall structure we'll iterate through is: (copiesPer *) Prefix + Variant + Prompt + Suffix
        /// </summary>
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
        public virtual bool TryBothBFLUpsamplingAndNot => false;

        /// Whether or not claude rewriting succeeds, just toss in a raw direct version of whatever you have to the image generator anyway.
        public virtual bool AlsoDoVersionSkippingClaude => false;

        public Settings Settings { get; set; }

        public AbstractPromptGenerator(Settings settings)
        {
            Settings = settings;
        }

        /// Implementers should have their .Run method called from Program.cs to iterate through your prompts.
        public IEnumerable<PromptDetails> Run()
        {
            var useVariants = new List<string>() { "" };
            if (Variants != null && Variants.Count() > 0)
            {
                useVariants = Variants.ToList();
            }
            var returnedCount = 0;

            foreach (var promptDetails in Prompts)
            {
                var cleanPrompt = CleanPrompt(promptDetails.Prompt).Trim();
                if (string.IsNullOrWhiteSpace(cleanPrompt))
                {
                    continue;
                }

                foreach (var variantText in useVariants)
                {
                    var usingPrompt = cleanPrompt;
                    if (!string.IsNullOrWhiteSpace(Prefix))
                    {
                        usingPrompt = $"{Prefix} {cleanPrompt}".Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(variantText))
                    {
                        usingPrompt = $"{variantText} {usingPrompt}".Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(Suffix))
                    {
                        usingPrompt = $"{usingPrompt} {Suffix}".Trim();
                    }

                    if (usingPrompt != promptDetails.Prompt)
                    {
                        promptDetails.ReplacePrompt(usingPrompt, usingPrompt, TransformationType.Variants);
                    }

                    for (var ii = 0; ii < CopiesPer; ii++)
                    {
                        var copy = promptDetails.Clone();
                        yield return copy;
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