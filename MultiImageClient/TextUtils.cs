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
            var truncatedPrompt = !string.IsNullOrWhiteSpace(promptDetails.Filename)
                ? (promptDetails.Filename.Length > 100 ? promptDetails.Filename.Substring(0, 100) : promptDetails.Filename)
                : (promptDetails.Prompt.Length > 100 ? promptDetails.Prompt.Substring(0, 100) : promptDetails.Prompt);

            var combined = truncatedPrompt;
            if (generator == GeneratorApiType.Ideogram)
            {
                var aspectRatioText = promptDetails.IdeogramDetails.AspectRatio.HasValue
                    ? IdeogramUtils.StringifyAspectRatio(promptDetails.IdeogramDetails.AspectRatio.Value)
                    : "";

                combined = $"{truncatedPrompt}_{aspectRatioText}_{promptDetails.IdeogramDetails.Model}_{promptDetails.IdeogramDetails.MagicPromptOption}";

                if (promptDetails.IdeogramDetails.StyleType.HasValue)
                {
                    combined += $"_{promptDetails.IdeogramDetails.StyleType}";
                }

                if (!string.IsNullOrWhiteSpace(promptDetails.IdeogramDetails.NegativePrompt))
                {
                    combined += $"_{promptDetails.IdeogramDetails.NegativePrompt}";
                }
            }
            else if (generator == GeneratorApiType.BFL)
            {
                combined = $"{truncatedPrompt}_{promptDetails.BFLDetails.Width}x{promptDetails.BFLDetails.Height}";
                
                if (promptDetails.BFLDetails.PromptUpsampling)
                {
                    combined += "_upsampled";
                }

                combined += $"_safety{promptDetails.BFLDetails.SafetyTolerance}";
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
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string baseFilename = $"{sanitized}_{timestamp}_{generator}_{saveType}";

            lock (_lockObject)
            {
                int count = 0;
                string uniqueFilename;
                do
                {
                    uniqueFilename = count == 0 ? baseFilename : $"{baseFilename}_{count:D4}";
                    count++;
                } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}.png")));

                _filenameCounts[baseFilename] = count;
                return uniqueFilename+".png";
            }
        }
    }
}