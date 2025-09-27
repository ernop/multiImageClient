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
        public string GoogleGeminiApiKey { get; set; }
        public string GoogleCloudLocation { get; set; }
        public string LoadPromptsFrom { get; set; }
        public bool EnableLogging { get; set; }
        public string LogFilePath { get; set; }
        public bool SaveJsonLog { get; set; }
        /// save just image.jpg or image.png etc.

        public string ImageDownloadBaseFolder { get; set; }
        /// unused yet we always do RIGHT
        public string AnnotationSide { get; set; } = "bottom";

        public static Settings LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Settings file not found: {filePath}");
            }

            string json = File.ReadAllText(filePath);
            var settings = JsonConvert.DeserializeObject<Settings>(json);
            settings.Validate();
            Logger.Log("Current settings:");
            Logger.Log($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Logger.Log($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Logger.Log($"Enable Logging:\t\t{settings.EnableLogging}");
            Logger.Log($"Annotation Side:\t{settings.AnnotationSide}");

            return settings;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(LogFilePath))
            {
                throw new ArgumentException("LogFilePath is required");
            }

            var logDirectory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            if (!Directory.Exists(ImageDownloadBaseFolder))
            {
                Directory.CreateDirectory(ImageDownloadBaseFolder);
            }

            if (string.IsNullOrWhiteSpace(GoogleCloudLocation))
            {
                throw new ArgumentException("GoogleCloudLocation is required for Imagen 4");
            }
            if (string.IsNullOrWhiteSpace(GoogleCloudProjectId))
            {
                throw new ArgumentException("GoogleCloudProjectId is required for Imagen 4");
            }
            if (string.IsNullOrWhiteSpace(GoogleServiceAccountKeyPath))
            {
                throw new ArgumentException("GoogleServiceAccountKeyPath is required for Imagen 4");
            }
        }
    }
}
