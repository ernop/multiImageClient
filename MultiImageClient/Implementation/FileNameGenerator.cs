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

        public static string SanitizeFilename(string filename)
        {
            string sanitized = Regex.Replace(filename, @"[^a-zA-Z0-9_\-]", "_");
            while (sanitized.Contains("__"))
            {
                sanitized = sanitized.Replace("__", "_");
            }
            return sanitized.Length > 200 ? sanitized.Substring(0, 200) : sanitized;
        }


        public static string GenerateUniqueFilename(string generatorFilenamePart, int n, string contentType, string baseFolder, SaveType saveType)
        {
            var components = new List<string>() { };

            components.Add(DateTime.Now.ToString("yyyyMMddHHmmss"));
            components.Add(generatorFilenamePart);
            components.Add($"img{n.ToString()}");
            
            if (!string.IsNullOrEmpty(contentType))
            {
                var ss = contentType.ToString();
                if (ss.IndexOf('/') != -1)
                {
                    var parts = ss.Split('/');
                    ss = parts.Last();

                }
                components.Add(ss);
            }

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
