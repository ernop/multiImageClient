namespace IdeogramAPIClient
{
    public class IdeogramDetails
    {
        public IdeogramAspectRatio? AspectRatio { get; set; }
        public IdeogramModel Model { get; set; }
        public IdeogramMagicPromptOption MagicPromptOption { get; set; }
        public IdeogramStyleType? StyleType { get; set; }
        public string NegativePrompt { get; set; }
    }
}