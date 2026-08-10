namespace IdeogramAPIClient
{
    /// Request fields for POST /v1/ideogram-v4/generate (Ideogram 4.0,
    /// released 2026-06-03). The current API consumes multipart form data.
    ///
    /// Only TextPrompt is required. We deliberately expose the simple
    /// text_prompt path; the alternative structured `json_prompt` contract
    /// (mutually exclusive with text_prompt, disables magic-prompt) can be
    /// added later if we want compositional control.
    ///
    /// Docs: https://developer.ideogram.ai/api-reference/api-reference/generate-v4
    public class IdeogramV4GenerateRequest
    {
        public IdeogramV4GenerateRequest(string textPrompt)
        {
            TextPrompt = textPrompt;
        }

        public string TextPrompt { get; set; }

        /// One of the documented 2K-class resolutions, e.g. "2048x2048"
        /// (square, the default), "2304x1728" (4:3), "1728x2304" (3:4),
        /// "2560x1440" (16:9), "1440x2560" (9:16), "2496x1664" (3:2), etc.
        /// Null lets the API pick (2048x2048).
        public string? Resolution { get; set; }

        /// TURBO | DEFAULT | QUALITY. Null = DEFAULT. Although FLASH remains
        /// in the shared enum, Ideogram's current v4 endpoint rejects it.
        public IdeogramRenderingSpeed? RenderingSpeed { get; set; }

        public bool? EnableCopyrightDetection { get; set; }
    }
}
