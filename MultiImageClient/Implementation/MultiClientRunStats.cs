using System;
using System.Collections.Generic;

namespace MultiImageClient
{
    public class MultiClientRunStats
    {
        public int SavedRawImageCount { get; set; }
        public int SavedAnnotatedImageCount { get; set; }
        public int SavedJsonLogCount { get; set; }
        
        public int IdeogramRequestCount { get; set; }
        public int IdeogramRefusedCount { get; set; }
        
        public int ClaudeRequestCount { get; set; }
        public int ClaudeWouldRefuseCount { get; set; }
        public int ClaudeRefusedCount { get; set; }
        public int ClaudeRewroteCount { get; set; }

        public int Dalle3RequestCount { get; set; }
        public int Dalle3RefusedCount { get; set; }

        public int GptImageOneRequestCount { get; set; }
        public int GptImageOneRefusedCount  { get; set; }

        public int BFLImageGenerationRequestCount { get; set; }
        public int BFLImageGenerationSuccessCount { get; set; }
        public int BFLImageGenerationErrorCount { get; set; }
        
        public int RecraftImageGenerationRequestCount { get; set; }
        public int RecraftImageGenerationSuccessCount { get; set; }
        public int RecraftImageGenerationErrorCount { get; set; }

        public int GoogleRequestCount { get; set; }
        public int GoogleRefusedCount { get; set; }

        public void PrintStats()
        {
            var nonZeroStats = new List<string>();

            if (SavedRawImageCount > 0)
                nonZeroStats.Add($"Raw Image Saved:{SavedRawImageCount}");
            if (SavedJsonLogCount > 0)
                nonZeroStats.Add($"JSON:{SavedJsonLogCount}");
            
            if (ClaudeWouldRefuseCount > 0)
                nonZeroStats.Add($"Claude Would Refuse:{ClaudeWouldRefuseCount}");
            if (ClaudeRequestCount > 0)
                nonZeroStats.Add($"Claude Requests:{ClaudeRequestCount}");
            if (ClaudeRefusedCount > 0)
                nonZeroStats.Add($"Claude Refused:{ClaudeRefusedCount}");
            if (ClaudeRewroteCount > 0)
                nonZeroStats.Add($"Claude Accepted:{ClaudeRewroteCount}");

            if (IdeogramRequestCount > 0)
                nonZeroStats.Add($"Ideogram Requests:{IdeogramRequestCount}");
            if (IdeogramRefusedCount > 0)
                nonZeroStats.Add($"Ideogram Refused:{IdeogramRefusedCount}");

            if (Dalle3RequestCount > 0)
                nonZeroStats.Add($"Dalle3 Requests:{Dalle3RequestCount}");
            if (Dalle3RefusedCount > 0)
                nonZeroStats.Add($"Dalle3 Refused:{Dalle3RefusedCount}");

            if (GptImageOneRequestCount > 0)
                nonZeroStats.Add($"GPT Image One Requests:{GptImageOneRequestCount}");
            if (GptImageOneRefusedCount > 0)
                nonZeroStats.Add($"GPT Image One Refused:{GptImageOneRefusedCount}");

            if (GoogleRequestCount > 0)
                nonZeroStats.Add($"Google Requests:{GoogleRequestCount}");
            if (GoogleRefusedCount > 0)
                nonZeroStats.Add($"Google Refused:{GoogleRefusedCount}");

            if (BFLImageGenerationRequestCount > 0 | BFLImageGenerationErrorCount > 0 | BFLImageGenerationSuccessCount > 0)
                nonZeroStats.Add($"BFL: total:{BFLImageGenerationRequestCount}, ok:{BFLImageGenerationSuccessCount}, bad:{BFLImageGenerationErrorCount} ");

            if (RecraftImageGenerationRequestCount > 0 | RecraftImageGenerationErrorCount > 0 | RecraftImageGenerationSuccessCount > 0)
                nonZeroStats.Add($"Recraft: total:{RecraftImageGenerationRequestCount}, ok:{RecraftImageGenerationSuccessCount}, bad:{RecraftImageGenerationErrorCount} ");

            var res = $"Stats: {string.Join(", ", nonZeroStats)}";
            Console.WriteLine(res);
        }
    }
}