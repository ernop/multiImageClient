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
        private static string TruncatePrompt(string prompt, int maxLength)
        {
            return prompt.Length > maxLength ? prompt.Substring(0, maxLength) : prompt;
        }


        private static string DescribeResolution(PromptDetails details)
        {
            if (details.BFLDetails != null && details.BFLDetails.Width != default && details.BFLDetails.Height != default)
                return $"{details.BFLDetails.Width}x{details.BFLDetails.Height}";
            if (details.Dalle3Details != null)
                return details.Dalle3Details.Size.ToString();
            if (details.IdeogramDetails?.AspectRatio != null)
                return IdeogramUtils.StringifyAspectRatio(details.IdeogramDetails.AspectRatio.Value);
            if (details.RecraftDetails != null)
                return details.RecraftDetails.size.ToString().TrimStart('_');
            return "NONE?";
        }

        private static string DescribeSafetyTolerance(PromptDetails details)
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


        public static string GenerateUniqueFilename(TaskProcessResult result, string baseFolder, string promptGeneratorName, SaveType saveType)
        {
            var usingPromptTextPart = TruncatePrompt(result.PromptDetails.Prompt, 90);
            if (!string.IsNullOrEmpty(result.PromptDetails.IdentifyingConcept))
            {
                usingPromptTextPart = result.PromptDetails.IdentifyingConcept;
            }

            var components = new List<string>() { };

            components.Add(promptGeneratorName);
            components.Add(usingPromptTextPart);
            components.Add(result.ImageGenerator.ToString());
            if (result.PromptDetails.RecraftDetails != null)
            {
                components.Add(result.PromptDetails.RecraftDetails.style.ToString());
                components.Add(result.PromptDetails.RecraftDetails.substyle.ToString());
            }
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
            components.Add(GetIfAPIServiceDoesRewrites(result.PromptDetails));
            components.Add(DateTime.Now.ToString("yyyyMMddHHmmss"));
            components.Add(saveType.ToString());

            string combined = string.Join("_", components.Where(c => !string.IsNullOrEmpty(c)));
            string sanitized = SanitizeFilename(combined);

            // Ensure the filename is unique
            int count = 0;
            string uniqueFilename;
            var ext = "";
            if (string.IsNullOrEmpty(result.ContentType))
            {
                //in future, this should never happen; we 
                ext = result.ImageGenerator.GetFileExtension();
            }
            else
            {
                if (result.ContentType == "image/webp")
                {
                    ext = ".webp";
                }
                else if (result.ContentType == "image/svg+xml")
                {
                    ext = ".svg";
                }
                else if (result.ContentType == "image/jpeg")
                {
                    ext = ".jpg";
                }
                else
                {
                    ext = result.ContentType; //.Split("/")[1]
                }
            }

            do
            {
                uniqueFilename = count == 0 ? $"{sanitized}{ext}" : $"{sanitized}_{count:D4}{ext}";
                count++;
            } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}")));

            return uniqueFilename;
        }


    }
}
