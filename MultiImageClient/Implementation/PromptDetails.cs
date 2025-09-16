
using System.Collections.Generic;

using IdeogramAPIClient;
using BFLAPIClient;
using RecraftAPIClient;
using System.Linq;

namespace MultiImageClient
{
    /// <summary>
    /// This is generic at above the generator level. This means that generators which allow specification of specific styles will be tough since other ones won't accept those params!
    /// </summary>
    public class PromptDetails
    {
        // ugh this is back now.
        public PromptDetails Copy()
        {
            var res = new PromptDetails();
            res.ReplacePrompt(Prompt, Prompt, TransformationType.InitialPrompt);
            foreach (var step in TransformationSteps)
            {
                res.AddStep(step.Explanation, step.TransformationType);
            }
            return res;
        }

        public string Prompt { get; set; }

        /// <summary>
        /// These are for programmatic manipulation. For example, a user of this program first adds a concept like "fire" then maps that into 4 different variants by using an internal Claude client or something like that.
        /// Then it'll be sent to the actual image generator (which may do more steps of its own). For now these steps are both handled by this class; the only latter type is when the remote thing say, rewrites the prompt again.
        /// For us things to do are cool ones like: choose an aspect ratio.
        /// </summary>
        public IList<PromptHistoryStep> TransformationSteps { get; set; } = new List<PromptHistoryStep>();

        public PromptDetails() { }

        /// <summary>
        /// These are generally for internal prompt manipulations before we send it out anywhere. So they'll mostly be shared before going out to an actual image generator.
        /// </summary>
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

            
            var detailsPart = string.Empty;
            if (parts.Count > 0)
            {
                detailsPart = $" {string.Join(", ", parts)}";
            }
            return $"\'{Prompt}\' {detailsPart}";
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
    }
}