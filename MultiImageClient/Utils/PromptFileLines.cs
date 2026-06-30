using System.Collections.Generic;
using System.IO;

namespace MultiImageClient
{
    public static class PromptFileLines
    {
        public static bool IsCommentOrBlank(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return true;
            }

            return line.TrimStart()[0] == '#';
        }

        public static IEnumerable<string> ReadNonCommentLines(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (IsCommentOrBlank(line))
                {
                    continue;
                }

                yield return line.Trim();
            }
        }
    }
}
