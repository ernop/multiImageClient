using System;
using System.Collections.Generic;

namespace IdeogramAPIClient
{
    /// Request fields for POST /v2/image/precise-edit/ideogram-4-5. The output
    /// keeps Image's width and height, and Ideogram copies pixels the edit did
    /// not change. ReferenceImages guide the edit and are never edited. A null
    /// Quality uses the provider default, which is medium for edits.
    public class IdeogramV45PreciseEditRequest
    {
        public IdeogramV45PreciseEditRequest(string prompt, IdeogramFile image)
        {
            Prompt = prompt;
            Image = image;
        }

        public string Prompt { get; set; }

        public IdeogramFile Image { get; set; }

        public IReadOnlyList<IdeogramFile> ReferenceImages { get; set; } = Array.Empty<IdeogramFile>();

        public IdeogramV45Quality? Quality { get; set; }

        public int? NumImages { get; set; }

        public int? Seed { get; set; }
    }
}
