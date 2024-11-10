using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiImageClient
{
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
        }
    }
}
