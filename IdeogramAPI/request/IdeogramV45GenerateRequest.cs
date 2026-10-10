namespace IdeogramAPIClient
{
    /// Request fields for POST /v2/image/generate/ideogram-4-5 without source
    /// images. A null Size lets Ideogram choose a 2K size from the prompt; an
    /// explicit Size must be one of the published 1K/2K presets. A null
    /// Quality uses the provider default, which is high for text prompts.
    public class IdeogramV45GenerateRequest
    {
        public IdeogramV45GenerateRequest(string prompt)
        {
            Prompt = prompt;
        }

        public string Prompt { get; set; }

        public string? Size { get; set; }

        public IdeogramV45Quality? Quality { get; set; }

        public int? NumImages { get; set; }

        public int? Seed { get; set; }
    }
}
