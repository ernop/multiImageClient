using IdeogramAPIClient;

using System.Collections.Generic;

namespace MultiClientRunner
{
    public class PromptDetails
    {
        public string Prompt { get; set; }
        public string Filename { get; set; }
        public IList<ImageConstructionStep> ImageConstructionSteps { get; set; }
        public IdeogramDetails IdeogramDetails { get; internal set; }
    }
}