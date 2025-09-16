
using RecraftAPIClient;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MultiImageClient
{
    /// If you have a file of a bunch of prompts, you can use this to load them rather than using some kind of custom iteration system.
    public class RecraftAllsStyles : AbstractPromptSource
    {
        public RecraftAllsStyles(Settings settings) : base(settings)
        {
        }
        public override string Name => nameof(WriteHere);

        public override int ImageCreationLimit => 350;
        public override int CopiesPer => 1;
        public override bool RandomizeOrder => false;
        public override string Prefix => "";
        public override string Suffix => "";

        private IEnumerable<PromptDetails> GetPrompts()
        {
            var res = new List<PromptDetails>();
            foreach (RecraftStyle style in Enum.GetValues(typeof(RecraftStyle)))
            {
                var substyles = style switch
                {
                    RecraftStyle.realistic_image => Enum.GetValues(typeof(RecraftRealisticImageSubstyle)),
                    RecraftStyle.vector_illustration => Enum.GetValues(typeof(RecraftVectorIllustrationSubstyle)),
                    RecraftStyle.digital_illustration => Enum.GetValues(typeof(RecraftDigitalIllustrationSubstyle)),
                    _ => throw new ArgumentException($"Unknown style: {style}")
                };
                
                foreach (object substyle in substyles)
                {
                    var usingSub = substyle.ToString().TrimStart('_');
                    var pd = new PromptDetails();
                    var prompt = "A magnificent tower in an epic plain, ruins and hidden secrets, super detailed and high resolution, incredibly deep and profound, with hidden creatures and erosion, and a cute semi-hidden kitten.";
                    pd.ReplacePrompt(prompt, prompt , TransformationType.InitialPrompt);
                    
                    Logger.Log($"Trying style, substyle: {style} {usingSub}");
                    res.Add(pd);
                }
            }
            return res;
        }
        
        public override IEnumerable<PromptDetails> Prompts => GetPrompts().OrderBy(el => Random.Shared.Next());
    }
}


