using System;
using System.Collections.Generic;

using IdeogramAPIClient;


namespace MultiClientRunner
{
    public class PromptDetails
    {
        /// This should track the "active" prompt which the next step in the process should care about.  Earlier versions are in ImageConstructionSteps. <summary>
        /// TO modify it call ReplacePrompt to also fix up history.
        public string Prompt { get; private set; }

        public string OriginalPromptIdea { get; set; }

        public IList<ImageConstructionStep> ImageConstructionSteps { get; set; } = new List<ImageConstructionStep>();

        /// Send these along and maybe one of the image consumers can use them to make a better image path etc.?
        public BFLDetails BFLDetails { get; set; }

        public IdeogramDetails IdeogramDetails { get; set; }
        public Dalle3Details Dalle3Details { get; set; }

        /// <summary>
        /// This item's "Prompt" field is always the active prompt which will be really used to send to external consuemrs
        /// however, often times it is really composed of like: $"Outer prompt stuff {previous version} Tail suffix stuff" and for user understanding we oftenw ant to show that.
        /// So when you revise a prompt, always call this with the actual prompt, the description of what this transformation step is, and include a details field which is what we'll use to explain to users in the annotation, etc.
        /// </summary>
        public void ReplacePrompt(string prompt, string kind, string details)
        {
            var item = new ImageConstructionStep(kind.Trim(), details.Trim());
            ImageConstructionSteps.Add(item);
            Prompt = prompt.Trim();
        }

        public PromptDetails Clone()
        {
            var clone = new PromptDetails
            {
                Prompt = this.Prompt,
                OriginalPromptIdea = this.OriginalPromptIdea,
                BFLDetails = this.BFLDetails != null ? new BFLDetails(this.BFLDetails) : null,
                IdeogramDetails = this.IdeogramDetails != null ? new IdeogramDetails(this.IdeogramDetails) : null,
                Dalle3Details = this.Dalle3Details!= null ? new Dalle3Details(this.Dalle3Details) : null,
                ImageConstructionSteps = new List<ImageConstructionStep>(this.ImageConstructionSteps.Count)
            };

            foreach (var step in this.ImageConstructionSteps)
            {
                clone.ImageConstructionSteps.Add(new ImageConstructionStep(step));
            }

            return clone;
        }

        //a method to make mousing over this object show the basic info on it:
        public override string ToString()
        {
            return $"{Prompt} {OriginalPromptIdea}";
        }
    }
}