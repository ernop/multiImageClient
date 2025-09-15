using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// generate as much as you can of the prompt in here.
    /// </summary>
    public abstract class AbstractPromptGenerator
    {
        public abstract IEnumerable<PromptDetails> Prompts { get; }
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
        public virtual bool SaveRaw => true;
        public virtual bool SaveFullAnnotation => true;
        public virtual bool SaveFinalPrompt => true;
        public virtual bool SaveInitialIdea => true;
        

        /// <summary>
        /// the version with just a simple below-image word/phrase set during prompt expansion
        /// </summary>
        public virtual bool SaveJustOverride => true;

        
        public Settings Settings { get; set; }

        public AbstractPromptGenerator(Settings settings)
        {
            Settings = settings;
        }

        

        /// Implementers should have their .Run method called from Program.cs to iterate through your prompts.
        // expand the things we've set up into all the prmopts wanted.
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
                var cleanPrompt = TextUtils.CleanPrompt(promptDetails.Prompt).Trim();
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
                        Console.WriteLine(usingPrompt);
                        Console.WriteLine(promptDetails.Prompt);
                        promptDetails.ReplacePrompt(usingPrompt, usingPrompt, TransformationType.Variants);
                    }
                    Console.WriteLine(promptDetails.Show());
                    Console.WriteLine("Do you accept this prompt? y for yes.");
                    var value = Console.ReadLine();
                    if (value.Trim() == "y")
                    {
                        Console.WriteLine("accepted.");
                        yield return promptDetails;
                    }
                    else if (value.TrimEnd() == "n")
                    {
                        continue;
                    }
                    else
                    {
                        var userPrompt = TextUtils.CleanPrompt(value.Trim()).Trim();
                        var newPd = new PromptDetails();
                        newPd.ReplacePrompt(userPrompt, userPrompt, TransformationType.InitialPrompt);
                        yield return newPd;
                        continue;
                    }

                    for (var ii = 0; ii < CopiesPer; ii++)
                    {
                        var copy = promptDetails.Clone();
                        returnedCount++;
                        yield return copy;
                        if (returnedCount >= ImageCreationLimit) yield break;
                    }
                    if (returnedCount >= ImageCreationLimit) yield break;
                }
                if (returnedCount >= ImageCreationLimit) yield break;
            }
        }
    }
}