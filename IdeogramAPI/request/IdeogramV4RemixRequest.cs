namespace IdeogramAPIClient
{
    /// Request fields for POST /v1/ideogram-v4/remix.
    ///
    /// ImageWeight is optional because Ideogram 4.0 can choose it from the
    /// edit instruction. Supplying a value overrides that provider-selected
    /// weight; the published OpenAPI contract does not declare a numeric range.
    public class IdeogramV4RemixRequest
    {
        public IdeogramV4RemixRequest(string textPrompt, IdeogramFile image)
        {
            TextPrompt = textPrompt;
            Image = image;
        }

        public string TextPrompt { get; set; }

        public IdeogramFile Image { get; set; }

        public int? ImageWeight { get; set; }

        public string? Resolution { get; set; }

        public IdeogramRenderingSpeed? RenderingSpeed { get; set; }

        public bool? EnableCopyrightDetection { get; set; }
    }
}
