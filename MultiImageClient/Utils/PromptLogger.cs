using System;
using System.IO;
using System.Text.Json;

namespace MultiImageClient
{
    public class PromptLogger
    {
        private const string LogFileName = "../../../prompt_log.json";

        public static void LogPrompt(string prompt)
        {
            var logEntry = new
            {
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                prompt
            };

            var jsonLine = JsonSerializer.Serialize(logEntry);

            try
            {
                File.AppendAllText(LogFileName, jsonLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to write to prompt log: {ex.Message}");
            }
        }

        /// Appends a freshly-typed prompt to the configured human-readable
        /// prompts file (Settings.TypedPromptsAppendFile) as a single line.
        /// No-op when the setting is blank. Never throws — on any failure
        /// we just log and move on, because losing one entry must not break
        /// a batch.
        ///
        /// Carefulness contract:
        ///   - Trims whitespace.
        ///   - Collapses any embedded CR/LF into a single space so one
        ///     prompt is always exactly one line on disk (users can paste
        ///     multi-line prompts without splitting the file).
        ///   - Skips blank / whitespace-only prompts.
        ///   - Creates the parent directory if the user gave a nested path.
        ///   - If the file exists and doesn't already end in a newline (can
        ///     happen after a hand-edit), inserts a leading newline so the
        ///     new prompt doesn't get glued onto the previous last line.
        ///   - Always ends the appended line with a single Environment.NewLine.
        public static void AppendPromptLine(string prompt, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (string.IsNullOrWhiteSpace(prompt)) return;

            try
            {
                // Collapse any internal newlines so the file remains strictly
                // one-prompt-per-line regardless of what got pasted.
                var single = prompt
                    .Replace("\r\n", " ")
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                if (single.Length == 0) return;

                // Only create the parent directory when the user actually
                // specified one (a bare filename yields "" from
                // GetDirectoryName and we deliberately don't touch the CWD).
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Defensive: if the file exists and its last byte isn't a
                // newline, prepend one so we don't accidentally concat onto
                // an unterminated last line (happens with hand-edited files).
                string leading = "";
                if (File.Exists(filePath))
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        int last = fs.ReadByte();
                        if (last != '\n') leading = Environment.NewLine;
                    }
                }

                File.AppendAllText(filePath, leading + single + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Log($"PromptLogger.AppendPromptLine: failed to append to '{filePath}': {ex.Message}");
            }
        }
    }
}
