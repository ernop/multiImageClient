using IdeogramAPIClient;
using OpenAI.Images;

namespace MultiImageClient
{
    public class GptImageOneDetails : IDetails
    {
        public string Model { get; set; } = "gpt-image-1";
        public string moderation { get; set; } = "low";
        public string output_format { get; set; } = "png";
        public string quality { get; set; } = "high";
        public string size { get; set; } = "auto";

        //public GeneratedImageSize Size { get; set; }
        //public GeneratedImageQuality Quality { get; set; }
        //public GeneratedImageFormat Format { get; set; }

        public GptImageOneDetails() { }
        public GptImageOneDetails(GptImageOneDetails other)
        {
            moderation =  other.moderation;
            output_format = other.output_format;
            quality= other.quality;
            size= other.size;
        }

        public string GetDescription() { 
            return $"{moderation}-{size}-{quality}-{size}";
        }
    }
}