using MultiImageClient;

namespace IdeogramAPIClient
{
    public class IdeogramDetails:IDetails
    {
        public IdeogramAspectRatio? AspectRatio { get; set; }
        public IdeogramModel Model { get; set; }
        public IdeogramMagicPromptOption MagicPromptOption { get; set; }
        public IdeogramStyleType? StyleType { get; set; }
        public string NegativePrompt { get; set; }
        
        public IdeogramDetails() { }
        public IdeogramDetails(IdeogramDetails other)
        {
            AspectRatio = other.AspectRatio;
            Model = other.Model;
            MagicPromptOption = other.MagicPromptOption;
            StyleType = other.StyleType;
            NegativePrompt = other.NegativePrompt;
        }

        public IDetails Clone()
        {
            throw new NotImplementedException();
        }
    }
}