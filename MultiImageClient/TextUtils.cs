using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Reflection;
using System.Timers;
using System.Text;
using System.Windows.Forms;

using IdeogramAPIClient;
using System.Text.RegularExpressions;
using static MultiClientRunner.TextFormatting;

namespace MultiClientRunner
{
    public static class TextUtils
    {
        private static readonly object _lockObject = new object();
        private static readonly Dictionary<string, int> _filenameCounts = new Dictionary<string, int>();
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        public static string GenerateUniqueFilename(string saveType, PromptDetails promptDetails, string baseFolder, GeneratorApiType generator)
        {
            var truncatedPrompt = !string.IsNullOrWhiteSpace(promptDetails.OriginalPromptIdea)
                ? (promptDetails.OriginalPromptIdea.Length > 100 ? promptDetails.OriginalPromptIdea.Substring(0, 100) : promptDetails.OriginalPromptIdea)
                : (promptDetails.Prompt.Length > 100 ? promptDetails.Prompt.Substring(0, 100) : promptDetails.Prompt);

            var combined = truncatedPrompt;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            switch (generator)
            {
                case GeneratorApiType.Ideogram:

                    var aspectRatioText = promptDetails.IdeogramDetails.AspectRatio.HasValue
                        ? IdeogramUtils.StringifyAspectRatio(promptDetails.IdeogramDetails.AspectRatio.Value)
                        : "";

                    combined = $"{truncatedPrompt}_{generator}_{aspectRatioText}_{promptDetails.IdeogramDetails.Model}_{promptDetails.IdeogramDetails.MagicPromptOption}";

                    if (promptDetails.IdeogramDetails.StyleType.HasValue)
                    {
                        combined += $"_{promptDetails.IdeogramDetails.StyleType}";
                    }

                    if (!string.IsNullOrWhiteSpace(promptDetails.IdeogramDetails.NegativePrompt))
                    {
                        combined += $"_{promptDetails.IdeogramDetails.NegativePrompt}";
                    }
                    combined += $"_{timestamp}_{saveType}";
                    break;

                case GeneratorApiType.BFL:
                    combined = $"{truncatedPrompt}_{generator}_{promptDetails.BFLDetails.Width}x{promptDetails.BFLDetails.Height}_safety{promptDetails.BFLDetails.SafetyTolerance}_{timestamp}_{saveType}";
                    break;

                case GeneratorApiType.Dalle3:
                    combined = $"{truncatedPrompt}_{generator}_{promptDetails.Dalle3Details.Size}_{promptDetails.Dalle3Details.Quality}_{timestamp}_{saveType}";
                    break;

                default:
                    throw new Exception($"Unknown generator type: {generator}");
            }

            // Add generator type to the combined string

            // Remove invalid characters
            string sanitized = Regex.Replace(combined, @"[^a-zA-Z0-9_\-]", "_");

            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }

            // Truncate to a reasonable length if necessary
            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            // Ensure the filename is unique by appending a timestamp and a sequential number if needed
            
            lock (_lockObject)
            {
                int count = 0;
                string uniqueFilename;
                do
                {
                    uniqueFilename = count == 0 ? sanitized : $"{sanitized}_{count:D4}";
                    count++;
                } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}.png")));

                _filenameCounts[sanitized] = count;
                return uniqueFilename + ".png";
            }
        }
    }
}