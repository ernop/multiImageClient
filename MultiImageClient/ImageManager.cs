using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;

using Newtonsoft.Json;

namespace MultiImageClient
{
    public class ImageManager
    {
        private readonly Settings _settings;

        public ImageManager(Settings settings)
        {
            _settings = settings;
        }

        public async Task<Dictionary<SaveType, string>> DoSaveAsync(AbstractPromptGenerator generator, byte[] imageBytes, TaskProcessResult result, MultiClientRunStats stats, Settings settings)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Logger.Log($"Empty or null image bytes received");
                throw new Exception("Bad image.");
            }
            var savedImagePaths = new Dictionary<SaveType, string>();
            if (generator.SaveRaw)
            {
                savedImagePaths[SaveType.Raw] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.Raw, generator.Name);
            }
            if (generator.SaveFullAnnotation)
            {
                savedImagePaths[SaveType.FullAnnotation] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.FullAnnotation, generator.Name);
            }
            if (generator.SaveFinalPrompt)
            {
                savedImagePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.FinalPrompt, generator.Name);
            }
            if (generator.SaveInitialIdea)
            {
                savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.InitialIdea, generator.Name);
            }
            if (generator.SaveJustOverride)
            {
                savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(imageBytes, result, settings, stats, SaveType.JustOverride, generator.Name);
            }

            return savedImagePaths;
        }

        public async Task<TaskProcessResult> ProcessAndSaveAsync(TaskProcessResult result, AbstractPromptGenerator promptGenerator, MultiClientRunStats stats)
        {
            try
            {
                if (!result.IsSuccess)
                {
                    return result;
                }
                var sw = Stopwatch.StartNew();
                byte[] imageBytes;
                if (!string.IsNullOrEmpty(result.Url))
                {
                    imageBytes = await ImageSaving.DownloadImageAsync(result);
                    result.DownloadTotalMs = sw.ElapsedMilliseconds;
                    var downloadResults = await DoSaveAsync(promptGenerator, imageBytes, result, stats, _settings);
                    await SaveJsonLogAsync(result, downloadResults);
                    return result;
                }
                else
                {
                    var ii = 0;
                    foreach (var qq in result.Base64ImageDatas)
                    {
                        //Console.WriteLine($"Saving one things: {ii}");
                        imageBytes = Convert.FromBase64String(qq);
                        var downloadResults = await DoSaveAsync(promptGenerator, imageBytes, result, stats, _settings);
                        ii++;
                        await SaveJsonLogAsync(result, downloadResults);
                    }
                    result.DownloadTotalMs = sw.ElapsedMilliseconds;
                    return result;
                }

                
            }
            catch (Exception ex)
            {
                Logger.Log($"\tAn error occurred while processing a task: {ex.Message}");
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        private async Task SaveJsonLogAsync(TaskProcessResult result, Dictionary<SaveType, string> savedImagePaths)
        {
            if (!_settings.SaveJsonLog) return;

            var jsonLog = new
            {
                Timestamp = DateTime.UtcNow,
                result.PromptDetails,
                GeneratedImageUrl = result.Url,
                SavedImagePaths = savedImagePaths,
                ServiceUsed = result.ImageGenerator,
                result.ErrorMessage,
            };

            string jsonString = JsonConvert.SerializeObject(jsonLog, Newtonsoft.Json.Formatting.Indented);

            if (savedImagePaths.TryGetValue(SaveType.Raw, out string rawImagePath))
            {
                string baseDirectory = Path.GetDirectoryName(rawImagePath);
                string logsDirectory = Path.Combine(baseDirectory, "logs");
                Directory.CreateDirectory(logsDirectory);

                string logFileName = Path.GetFileNameWithoutExtension(rawImagePath) + ".json";
                string jsonFilePath = Path.Combine(logsDirectory, logFileName);

                await File.WriteAllTextAsync(jsonFilePath, jsonString);
            }
            else
            {
                Logger.Log("\tUnable to save JSON log: Raw image path not found.");
            }
        }
    }
}
