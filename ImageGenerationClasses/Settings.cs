using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiImageClient
{
    
    /// <summary>
    ///  Note: this file is for general confiruation for any use of the system.  Don't put things like "preferredImageOutputType" gif/png/etc here
    /// because those are things that might vary over time.  This is only for global true stuff that'll be set one time and kinda done from that point.
    /// If you have specific stuff like layout type horizontal/vertical etc, that should just be inlined in the way we create the generator at runtime
    /// with edits in the program.cs file which is used by the person making the images.
    /// </summary>
    public class Settings
    {
        public string GoogleCloudProjectId { get; set; }
        /// ffs google
        public string GoogleServiceAccountKeyPath { get; set; }

        /// aka vertex
        public string GoogleCloudApiKey { get; set; }
        public string IdeogramApiKey { get; set; }
        public string OpenAIApiKey { get; set; }
        public string BFLApiKey { get; set; }
        public string AnthropicApiKey { get; set; }
        public string RecraftApiKey { get; set; }

        /// xAI (Grok) API key, format "xai-...". Required only when a Grok
        /// image generator is active. Obtain one at https://console.x.ai/.
        public string XAIGrokApiKey { get; set; }
        public string GoogleGeminiApiKey { get; set; }
        public string GoogleCloudLocation { get; set; }
        /// List of prompt-source text files. Every listed file is read and the
        /// lines are pooled together. All listed files must exist at run time;
        /// missing files are a hard error. Prefer this field over the legacy
        /// LoadPromptsFrom single-file setting.
        public List<string> PromptFiles { get; set; } = new List<string>();

        /// Legacy single-file prompt source. If set, appended to PromptFiles.
        /// Kept for backward compatibility with older settings.json files.
        public string LoadPromptsFrom { get; set; }
        public bool EnableLogging { get; set; }
        public string LogFilePath { get; set; }
        public bool SaveJsonLog { get; set; }
        /// save just image.jpg or image.png etc.

        public string ImageDownloadBaseFolder { get; set; }
        /// unused yet we always do RIGHT
        public string AnnotationSide { get; set; } = "bottom";

        /// Optional flat-folder mirror. If non-empty, every saved raw,
        /// annotated, and combined image is also copied (best-effort) into
        /// this single folder so you don't have to navigate date folders to
        /// grab the latest batch. Leave blank to disable; missing or
        /// unreachable paths are logged and skipped, never fatal.
        public string FlatImageMirrorPath { get; set; } = "";

        /// Optional "prompts I typed by hand" capture file. When the user
        /// types a free-form prompt at the interactive batch loop (anything
        /// other than y/n/q), the typed text is also appended as a single
        /// line to this file — handy for growing a personal prompt corpus
        /// over time. Leave blank to disable; the existing JSON prompt_log
        /// remains the machine-readable history regardless. Embedded
        /// newlines in the typed prompt are collapsed to spaces so the file
        /// is always one-prompt-per-line.
        public string TypedPromptsAppendFile { get; set; } = "";

        public static Settings LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Settings file not found: {filePath}");
            }

            string json = File.ReadAllText(filePath);
            var settings = JsonConvert.DeserializeObject<Settings>(json);
            settings.Validate();
            Logger.Initialize(settings.LogFilePath);
            Logger.Log("Current settings:");
            Logger.Log($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Logger.Log($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Logger.Log($"Enable Logging:\t\t{settings.EnableLogging}");
            Logger.Log($"Annotation Side:\t{settings.AnnotationSide}");
            if (!string.IsNullOrWhiteSpace(settings.FlatImageMirrorPath))
            {
                Logger.Log($"Flat Mirror Path:\t{settings.FlatImageMirrorPath}");
            }
            if (!string.IsNullOrWhiteSpace(settings.TypedPromptsAppendFile))
            {
                Logger.Log($"Typed Prompts File:\t{settings.TypedPromptsAppendFile}");
            }

            return settings;
        }

        /// Only validates things that EVERY run needs: the log file path and the
        /// image download folder. Per-generator requirements (Google Cloud fields,
        /// individual API keys, etc.) live in the generator that needs them.
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(LogFilePath))
            {
                throw new InvalidOperationException(
                    "settings.json: LogFilePath is required. Set it to a writable file path, e.g. \"C:\\\\proj\\\\multiImageClient\\\\ideogram.log\".");
            }
            var logDirectory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"settings.json: LogFilePath='{LogFilePath}' — could not create directory '{logDirectory}': {ex.Message}. Fix the path in settings.json.");
                }
            }

            if (string.IsNullOrWhiteSpace(ImageDownloadBaseFolder))
            {
                throw new InvalidOperationException(
                    "settings.json: ImageDownloadBaseFolder is required. Set it to a writable folder, e.g. \"C:\\\\proj\\\\multiImageClient\\\\saves\".");
            }
            try
            {
                Directory.CreateDirectory(ImageDownloadBaseFolder);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"settings.json: ImageDownloadBaseFolder='{ImageDownloadBaseFolder}' — could not create: {ex.Message}. Fix the path in settings.json.");
            }
        }
    }
}
