using System;
using System.Collections.Generic;

namespace IdeogramAPIClient
{
    public class IdeogramV3GenerateRequest
    {
        public IdeogramV3GenerateRequest(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt is required.", nameof(prompt));

            Prompt = prompt;
        }

        public string Prompt { get; }

        public string AspectRatio { get; set; }


        public IdeogramRenderingSpeed? RenderingSpeed { get; set; }

        public IdeogramMagicPromptOption? MagicPrompt { get; set; }

        public IdeogramV3StyleType StyleType { get; set; }

        public string? StylePreset { get; set; }

        public string? NegativePrompt { get; set; }

        public int? NumImages { get; set; }

        public int? Seed { get; set; }

        public IList<string> StyleCodes { get; } = new List<string>();

        public IList<IdeogramFile> StyleReferenceImages { get; } = new List<IdeogramFile>();

        public IList<IdeogramFile> CharacterReferenceImages { get; } = new List<IdeogramFile>();

        public IList<IdeogramFile> CharacterReferenceImageMasks { get; } = new List<IdeogramFile>();
    }
}

