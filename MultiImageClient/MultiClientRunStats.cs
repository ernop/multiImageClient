using System;
using System.Collections.Generic;

namespace MultiClientRunner
{
    public class MultiClientRunStats
    {
        public int SavedRawImageCount { get; set; }
        public int SavedAnnotatedImageCount { get; set; }
        public int SavedJsonLogCount { get; set; }
        public int IdeogramRequestCount { get; set; }
        public int ClaudeRequestCount { get; set; }
        public int ClaudeRefusedCount { get; set; }
        public int ClaudeAcceptedCount { get; set; }
        public int Dalle3RequestCount { get; set; }
        public int BFLImageGenerationRequestcount { get; set; }

        public string PrintStats()
        {
            var nonZeroStats = new List<string>();

            if (SavedRawImageCount > 0)
                nonZeroStats.Add($"Raw Image Saved:{SavedRawImageCount}");
            if (SavedAnnotatedImageCount > 0)
                nonZeroStats.Add($"Annotated Images Saved:{SavedAnnotatedImageCount}");
            //if (SavedJsonLogCount > 0)
            //    nonZeroStats.Add($"JSON:{SavedJsonLogCount}");
            if (IdeogramRequestCount > 0)
                nonZeroStats.Add($"Ideogram Requests:{IdeogramRequestCount}");
            if (ClaudeRequestCount > 0)
                nonZeroStats.Add($"Claude Requests:{ClaudeRequestCount}");
            if (Dalle3RequestCount > 0)
                nonZeroStats.Add($"Dalle3 Requests:{Dalle3RequestCount}");
            if (BFLImageGenerationRequestcount > 0)
                nonZeroStats.Add($"BFL Image Generation requests:{BFLImageGenerationRequestcount}");
            if (ClaudeRefusedCount > 0)
                nonZeroStats.Add($"Claude Refused:{ClaudeRefusedCount}");
            if (ClaudeAcceptedCount > 0)
                nonZeroStats.Add($"Claude Accepted:{ClaudeAcceptedCount}");

            return ($"Stats: {string.Join(", ", nonZeroStats)}");
        }
    }
}