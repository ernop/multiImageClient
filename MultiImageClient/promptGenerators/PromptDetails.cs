using System;
using System.Collections.Generic;

using IdeogramAPIClient;


namespace MultiImageClient
{
    public class PromptDetails
    {
        
        /// This should track the "active" prompt which the next step in the process should care about.  Earlier versions are in ImageConstructionSteps. <summary>
        /// To modify it call ReplacePrompt to also fix up history.
        public string Prompt { get; set; }

        /// <summary>
        /// If a generator wants the file to have a specific filename, it should fill this in. that is, regardless of whatever happens to the prompt after the transformation, or even the initail actual prompt, just use this. For example if you are generating a deck of cards, you might just set this up to say "2 or hearts" even if your raw original prompt for even the first step is "two hearts entwined in the style of jack vance" etc. This way the resulting filenames will make sense.
        /// </summary>
        public string OverrideFilename { get; set; } = "";

        public IList<PromptHistoryStep> TransformationSteps { get; set; } = new List<PromptHistoryStep>();

        /// Send these along and maybe one of the image consumers can use them to make a better image path etc.?
        public BFLDetails BFLDetails { get; set; }

        public IdeogramDetails IdeogramDetails { get; set; }
        public Dalle3Details Dalle3Details { get; set; }

        /// <summary>
        /// This item's "Prompt" field is always the active prompt which will be really used to send to external consuemrs
        /// however, often times it is really composed of like: $"Outer prompt stuff {previous version} Tail suffix stuff" and for user understanding we oftenw ant to show that.
        /// So when you revise a prompt, always call this with the actual prompt, the description of what this transformation step is, and include a details field which is what we'll use to explain to users in the annotation, etc.
        /// Explanation should be the full prompt, but can ALSO be preceded by any step-specific details such as temperature.
        /// 
        /// This is a helper method which forces callers to fill in the history of a request when they change the prompt. That way we won't lose track of it.
        /// </summary>
        public void ReplacePrompt(string newPrompt, string explanation, TransformationType transformationType)
        {
            var currentPromptText = Prompt;
            if (string.IsNullOrEmpty(currentPromptText))
            {
                
            }
            else
            {
                explanation = explanation.Replace(currentPromptText, "{PROMPT}");
            }
            
            var item = new PromptHistoryStep(newPrompt, explanation, transformationType);
            TransformationSteps.Add(item);
            Prompt = newPrompt.Trim();
        }

        public string Show()
        {
            var shortText = Prompt.Length > 150 ? Prompt.Substring(0, 150) + "..." : Prompt;
            return shortText;
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
                OverrideFilename = OverrideFilename,
                BFLDetails = BFLDetails != null ? new BFLDetails(BFLDetails) : null,
                IdeogramDetails = IdeogramDetails != null ? new IdeogramDetails(IdeogramDetails) : null,
                Dalle3Details = Dalle3Details != null ? new Dalle3Details(Dalle3Details) : null,
                TransformationSteps = new List<PromptHistoryStep>()
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