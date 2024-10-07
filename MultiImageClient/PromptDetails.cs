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

        public string Filename { get; set; }

        public IList<ImageConstructionStep> ImageConstructionSteps { get; set; } = new List<ImageConstructionStep>();

        /// Send these along and maybe one of the image consumers can use them to make a better image path etc.?
        public BFLDetails BFLDetails { get; set; }

        public IdeogramDetails IdeogramDetails { get; set; }

        public void ReplacePrompt(string prompt, string kind, string details)
        {
            var item = new ImageConstructionStep(kind.Trim(), details.Trim());
            ImageConstructionSteps.Add(item);
            Prompt = prompt.Trim();
        }
    }
}