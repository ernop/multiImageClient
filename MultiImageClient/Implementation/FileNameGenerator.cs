using IdeogramAPIClient;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class FilenameGenerator
    {
        public static string TruncatePrompt(string prompt, int maxLength)
        {
            return prompt.Length > maxLength ? prompt.Substring(0, maxLength) : prompt;
        }

        private static string DescribeResolution(PromptDetails details)
        {
            //if (details.BFL11Details != null && details.BFL11Details.Width != default && details.BFL11Details.Height != default)
            //    return $"{details.BFL11Details.Width}x{details.BFL11Details.Height}";
            //else if (details.Dalle3Details != null)
            //    return details.Dalle3Details.Size.ToString();
            //else if (details.IdeogramDetails?.AspectRatio != null)
            //    return IdeogramUtils.StringifyAspectRatio(details.IdeogramDetails.AspectRatio.Value);
            //else if (details.RecraftDetails != null)
            //    return details.RecraftDetails.size.ToString().TrimStart('_');
            //else if (details.GptImageOneDetails != null)
            //    return $"{details.GptImageOneDetails.size}";
            //else if (details.BFL11UltraDetails != null)
            //    return $"{details.BFL11UltraDetails.AspectRatio}";
            //else
            //    Console.WriteLine("failed to get any details for esolution description");

            return "resolution_unknown";
        }

        private static string DescribeSafetyTolerance(PromptDetails details)
        {
            //if (details.BFL11Details != null && details.BFL11Details.SafetyTolerance != default)
            //    return $"safety{details.BFL11Details.SafetyTolerance}";
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


        public static string GenerateUniqueFilename(string generatorFilenamePart, TaskProcessResult result, string baseFolder, SaveType saveType)
        {
            var components = new List<string>() { };

            //components.Add(promptGeneratorName);
            components.Add(DateTime.Now.ToString("yyyyMMddHHmmss"));
            components.Add(result.ImageGenerator.ToString());
            
            if (!string.IsNullOrEmpty(result.ContentType))
            {
                var ss = result.ContentType.ToString();
                if (ss.IndexOf('/') != -1)
                {
                    var parts = ss.Split('/');
                    ss = parts.Last();

                }
                components.Add(ss);
            }

            components.Add(DescribeResolution(result.PromptDetails));
            
            components.Add(saveType.ToString());

            string combined = string.Join("_", components.Where(c => !string.IsNullOrEmpty(c)));
            string sanitized = SanitizeFilename(combined);

            // Ensure the filename is unique
            int count = 0;
            string uniqueFilename;
            var ext = ".png";

            do
            {
                uniqueFilename = count == 0 ? $"{sanitized}{ext}" : $"{sanitized}_{count:D4}{ext}";
                count++;
            } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}")));

            return uniqueFilename;
        }


    }
}
