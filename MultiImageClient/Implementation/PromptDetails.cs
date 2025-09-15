using System;
using System.Collections.Generic;
using System.Linq;

using IdeogramAPIClient;

using RecraftAPIClient;

using SixLabors.ImageSharp;

namespace MultiImageClient
{
    /// <summary>
    /// This is generic at above the generator level. This means that generators which allow specification of specific styles will be tough since other ones won't accept those params!
    /// </summary>
    public class PromptDetails
    {
        public static int NextIndex = 1;
        public int Index { get; private set; }
        /// This should track the "active" prompt which the next step in the process should care about.  Earlier versions are in ImageConstructionSteps. <summary>
        /// To modify it call ReplacePrompt to also fix up history.
        public string Prompt { get; set; }

        /// <summary>
        /// If a generator wants the file to have a specific filename, it should fill this in. that is, regardless of whatever happens to the prompt after the transformation, or even the initail actual prompt, just use this. For example if you are generating a deck of cards, you might just set this up to say "2 or hearts" even if your raw original prompt for even the first step is "two hearts entwined in the style of jack vance" etc. This way the resulting filenames will make sense.
        /// </summary>
        public string IdentifyingConcept { get; set; } = "";

        public IList<PromptHistoryStep> TransformationSteps { get; set; } = new List<PromptHistoryStep>();

        /// Send these along and maybe one of the image consumers can use them to make a better image path etc.?
        /// these are effectively specs and should also contain the methods to generate subtitles, filenames etc for this type of job.
        public BFL11Details BFL11Details { get; set; }
        public BFL11UltraDetails BFL11UltraDetails { get; set; }
        public RecraftDetails RecraftDetails { get; set; }
        public IdeogramDetails IdeogramDetails { get; set; }
        public Dalle3Details Dalle3Details { get; set; }
        public GptImageOneDetails GptImageOneDetails { get; set; }

        public PromptDetails()
        {
            Index = NextIndex;
            NextIndex++;
        }

        public void ReplacePrompt(string newPrompt, string explanation, TransformationType transformationType)
        {
            ReplacePrompt(newPrompt, explanation, transformationType, null);    
        }

        /// <summary>
        /// This item's "Prompt" field is always the active prompt which will be really used to send to external consuemrs
        /// however, often times it is really composed of like: $"Outer prompt stuff {previous version} Tail suffix stuff" and for user understanding we oftenw ant to show that.
        /// So when you revise a prompt, always call this with the actual prompt, the description of what this transformation step is, and include a details field which is what we'll use to explain to users in the annotation, etc.
        /// Explanation should be the full prompt, but can ALSO be preceded by any step-specific details such as temperature.
        /// 
        /// This is a helper method which forces callers to fill in the history of a request when they change the prompt. That way we won't lose track of it.
        /// </summary>
        public void ReplacePrompt(string newPrompt, string explanation, TransformationType transformationType, PromptReplacementMetadata promptReplacementMetadata)
        {
            var currentPromptText = Prompt;
            if (string.IsNullOrEmpty(currentPromptText))
            {
                
            }
            else
            {
                explanation = explanation.Replace(currentPromptText, "{PROMPT}");
            }
            
            var item = new PromptHistoryStep(newPrompt, explanation, transformationType, promptReplacementMetadata);
            TransformationSteps.Add(item);
            Prompt = newPrompt.Trim();
        }

        public string Show()
        {
            var parts = new List<string>();

            if (BFL11Details != null)
            {
                parts.Add(BFL11Details.GetDescription());
            }
            if (BFL11UltraDetails != null)
            {
                parts.Add(BFL11UltraDetails.GetDescription());
            }
            if (RecraftDetails != null)
            {
                parts.Add(RecraftDetails.GetDescription());
            }
            if (IdeogramDetails != null)
            {
                parts.Add(IdeogramDetails.GetDescription());
            }
            if (Dalle3Details != null)
            {
                parts.Add(Dalle3Details.GetDescription());
            }
            if (GptImageOneDetails!= null)
            {
                parts.Add(GptImageOneDetails.GetDescription());
            }
            var detailsPart = string.Empty;
            if (parts.Count > 0)
            {
                detailsPart = $" {string.Join(", ", parts)}";
            }
            return $"Index:{Index} \'{Prompt}\' {detailsPart}";
        }

        public void UndoLastStep()
        {
            if (TransformationSteps.Count > 0)
            {
                TransformationSteps.RemoveAt(TransformationSteps.Count - 1);
                
                if (TransformationSteps.Count > 0)
                {
                    Prompt = TransformationSteps[TransformationSteps.Count - 1].Prompt;
                }
                else
                {
                    Prompt = string.Empty;
                }
            }
        }

        public void AddStep(string stepDescription, TransformationType transformationType)
        {
            var item = new PromptHistoryStep(Prompt, stepDescription, transformationType);
            TransformationSteps.Add(item);
        }

        public PromptDetails Clone()
        {
            var clone = new PromptDetails
            {
                Prompt = Prompt,
                IdentifyingConcept = IdentifyingConcept,
                BFL11Details = BFL11Details != null ? new BFL11Details(BFL11Details) : null,
                BFL11UltraDetails = BFL11UltraDetails != null ? new BFL11UltraDetails(BFL11UltraDetails) : null,
                RecraftDetails = RecraftDetails != null ? new RecraftDetails(RecraftDetails) : null,
                IdeogramDetails = IdeogramDetails != null ? new IdeogramDetails(IdeogramDetails) : null,
                Dalle3Details = Dalle3Details != null ? new Dalle3Details(Dalle3Details) : null,
                TransformationSteps = new List<PromptHistoryStep>(),
            };

            foreach (var step in TransformationSteps)
            {
                clone.TransformationSteps.Add(new PromptHistoryStep(step));
            }

            return clone;
        }

        //a method to make mousing over this object show the basic info on it:
        public override string ToString()
        {

            return $"{Prompt}";
        }
    }
}