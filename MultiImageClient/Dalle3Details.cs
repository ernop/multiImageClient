using IdeogramAPIClient;

using OpenAI.Images;

namespace MultiClientRunner
{
    public class Dalle3Details
    {
        public string Model { get; set; }
        public GeneratedImageSize Size { get; set; }
        public GeneratedImageQuality Quality { get; set; }
        public GeneratedImageFormat Format { get; set; }

        public Dalle3Details() { }
        public Dalle3Details(Dalle3Details other)
        {
            Model = other.Model;
            Size = other.Size;
            Quality = other.Quality;
            Format = other.Format;
        }
    }
}