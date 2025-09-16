using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// this a way of loading a bunch of user configs for prompts. like, what do you want the text of the prompt to say? (and optionally, should it be big, landscape, whatever?)
    /// things like actually mapping that to whatever options are provided by the image generator is another job, for future, IJobSpec or something like that)
    public abstract class AbstractPromptSource
    {
        public abstract IEnumerable<PromptDetails> Prompts { get; }

        ///  ----------- SETTINGS -----------------

        public abstract int ImageCreationLimit { get; }

        /// how many times we send this prompt as of this level.
        public abstract int CopiesPer { get; }

        /// i.e. how many times we send it to each image generator, after applying all prior manipulation steps
        public virtual int FullyResolvedCopiesPer { get; } = 1;
        public abstract string Prefix { get; }
        public abstract string Suffix { get; }

        ///  Variant leading texts, if any, which you want to include after the global prefix.
        /// The overall structure we'll iterate through is: (copiesPer *) Prefix + Variant + Prompt + Suffix
        public abstract bool RandomizeOrder { get; }
        public abstract string Name { get; }
        public Settings Settings { get; set; }
        

        public AbstractPromptSource(Settings settings)
        {
            Settings = settings;
        }

        /// Implementers should have their .Iter method called from Program.cs to iterate through your prompts.
        // expand the things we've set up into all the prmopts wanted.
        public IEnumerable<PromptDetails> Iter(IEnumerable<PromptDetails> thePromptDetails)
        {
            var returnedCount = 0;
            foreach (var promptDetails in thePromptDetails)
            {
                var cleanPrompt = TextUtils.CleanPrompt(promptDetails.Prompt).Trim();
                if (string.IsNullOrWhiteSpace(cleanPrompt))
                {
                    continue;
                }

                var usingPrompt = cleanPrompt;
                if (!string.IsNullOrWhiteSpace(Prefix))
                {
                    usingPrompt = $"{Prefix} {cleanPrompt}".Trim();
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

                //for (var ii = 0; ii < CopiesPer; ii++)
                //{
                //    var copy = promptDetails.Clone();
                //    returnedCount++;
                //    yield return copy;
                //    if (returnedCount >= ImageCreationLimit) yield break;
                //}

                if (returnedCount >= ImageCreationLimit) yield break;
            }
        }
    }
}