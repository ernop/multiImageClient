using System.Text.Json.Serialization;

namespace MultiImageClient
{
    // Shared response models for Google Imagen API
    public class GoogleImagenResponse
    {
        public GoogleImagenGeneratedImage[] GeneratedImages { get; set; }
    }

    public class GoogleImagenGeneratedImage
    {
        public string BytesBase64Encoded { get; set; }
        public string MimeType { get; set; }
    }

    // Response models for Gemini native image generation (Nano Banana)
    public class GeminiGenerateContentResponse
    {
        public GeminiCandidate[] candidates { get; set; }
    }

    public class GeminiCandidate
    {
        public GeminiContent content { get; set; }
    }

    public class GeminiContent
    {
        public GeminiPart[] parts { get; set; }
    }

    public class GeminiPart
    {
        public string text { get; set; }
        public GeminiInlineData inlineData { get; set; }
    }

    public class GeminiInlineData
    {
        public string mimeType { get; set; }
        public string data { get; set; }
    }
}
