using MultiImageClient;

namespace IdeogramAPIClient
{
    public class IdeogramOptions
    {
        public IdeogramAspectRatio? AspectRatio { get; set; }
        public IdeogramModel Model { get; set; }
        public IdeogramMagicPromptOption MagicPromptOption { get; set; }
        public IdeogramStyleType? StyleType { get; set; }
        public string NegativePrompt { get; set; } = "";

        public IdeogramOptions() { }
        public IdeogramOptions(IdeogramOptions other)
        {
            AspectRatio = other.AspectRatio;
            Model = other.Model;
            MagicPromptOption = other.MagicPromptOption;
            StyleType = other.StyleType;
            NegativePrompt = other.NegativePrompt;
        }
    }
}