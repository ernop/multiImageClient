namespace IdeogramAPIClient
{
    public class BFLDetails
    {
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
        public bool PromptUpsampling { get; set; } = false;
        public int SafetyTolerance { get; set; } = 6;
        public int? Seed { get; set; }
        
        public BFLDetails() { }
        public BFLDetails(BFLDetails other)
        {
            Width = other.Width;
            Height = other.Height;
            PromptUpsampling = other.PromptUpsampling;
            SafetyTolerance = other.SafetyTolerance;
            Seed = other.Seed;
        }
    }
}