using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiClientRunner
{
    /// <summary>
    /// General settings file.
    /// </summary>
    public class Settings
    {
        public string IdeogramApiKey { get; set; }
        public string OpenAIApiKey { get; set; }
        public string BFLApiKey {get;set;}
        public string AnthropicApiKey{ get; set; }
        public string LoadPromptsFrom { get; set; }
        public bool EnableLogging { get; set; }
        public string LogFilePath { get; set; }
        public bool SaveJsonLog { get; set; }
        public bool SaveRawImage { get; set; }
        public bool SaveAnnotatedImage { get; set; }
        public string ImageDownloadBaseFolder { get; set; }
        public string AnnotationSide { get; set; } = "right";

        public static Settings LoadFromFile(string filePath)
        {
            
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Settings file not found: {filePath}");
            }

            string json = File.ReadAllText(filePath);
            var settings = JsonConvert.DeserializeObject<Settings>(json);
            settings.Validate();
            return settings;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(IdeogramApiKey))
            {
                throw new ArgumentException("IdeogramApiKey is required");
            }

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
            if ( SaveAnnotatedImage)
            {
                var annotatedPath = Path.Combine(ImageDownloadBaseFolder, "annotated");
                Directory.CreateDirectory(annotatedPath);
            }
        }
    }
}