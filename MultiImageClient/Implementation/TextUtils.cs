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
using System.Xml.Linq;


namespace MultiImageClient
{
    public static class TextUtils
    {
        private static readonly object _lockObject = new object();
        private static readonly Dictionary<string, int> _filenameCounts = new Dictionary<string, int>();
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        public static string GenerateUniqueFilename(TaskProcessResult result, string baseFolder, string promptGeneratorName, SaveType saveType)
        {
            var components = new List<string>
            {
                promptGeneratorName,
                result.ImageGenerator.ToString(),
                TruncatePrompt(result.PromptDetails.Prompt, 90),

                GetResolution(result.PromptDetails),
                GetIfAPIServiceDoesRewrites(result.PromptDetails),

                DateTime.Now.ToString("yyyyMMddHHmmss"),
                saveType.ToString(),
            };

            string combined = string.Join("_", components.Where(c => !string.IsNullOrEmpty(c)));
            string sanitized = SanitizeFilename(combined);

            // Ensure the filename is unique
            int count = 0;
            string uniqueFilename;
            do
            {
                uniqueFilename = count == 0 ? sanitized : $"{sanitized}_{count:D4}";
                count++;
            } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}{result.ImageGenerator.GetFileExtension()}")));

            return uniqueFilename;
        }

        private static string TruncatePrompt(string prompt, int maxLength)
        {
            return prompt.Length > maxLength ? prompt.Substring(0, maxLength) : prompt;
        }

        private static string GetIfAPIServiceDoesRewrites(PromptDetails details)
        {
            if (details.BFLDetails != null)
            {
                if (details.BFLDetails.PromptUpsampling)
                {
                    return "BFL_upsampling";
                }
                else
                {
                    return "";
                }
            }
            if (details.IdeogramDetails != null)
            {
                if (details.IdeogramDetails.MagicPromptOption == IdeogramMagicPromptOption.ON)
                {
                    return "MagicPrompt_YES";
                }
                else if (details.IdeogramDetails.MagicPromptOption == IdeogramMagicPromptOption.AUTO)
                {
                    return "MagicPrompt_Auto";
                }
                else
                {
                    return "";
                }
            }
            return "";
        }

        private static string GetResolution(PromptDetails details)
        {
            if (details.BFLDetails != null && details.BFLDetails.Width != default && details.BFLDetails.Height != default)
                return $"{details.BFLDetails.Width}x{details.BFLDetails.Height}";
            if (details.Dalle3Details != null)
                return details.Dalle3Details.Size.ToString();
            if (details.IdeogramDetails?.AspectRatio != null)
                return IdeogramUtils.StringifyAspectRatio(details.IdeogramDetails.AspectRatio.Value);
            return "";
        }

        private static string GetSafetyTolerance(PromptDetails details)
        {
            if (details.BFLDetails != null && details.BFLDetails.SafetyTolerance != default)
                return $"safety{details.BFLDetails.SafetyTolerance}";
            return "";
        }

        private static string SanitizeFilename(string filename)
        {
            string sanitized = Regex.Replace(filename, @"[^a-zA-Z0-9_\-]", "_");
            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }
            return sanitized.Length > 200 ? sanitized.Substring(0, 200) : sanitized;
        }
    }
}
