using ImageMagick;

using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Emit;
using System.Security.AccessControl;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MultiImageClient
{
    public class ImageManager
    {
        private readonly Settings _settings;
        private readonly MultiClientRunStats _stats;

        public ImageManager(Settings settings, MultiClientRunStats stats)
        {
            _settings = settings;
            _stats = stats;
        }

        public async Task<Dictionary<SaveType, string>> DoSaveAsync(IImageGenerator generator, byte[] imageBytes, TaskProcessResult result, Settings settings)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Logger.Log($"Empty or null image bytes received");
                throw new Exception("Bad image.");
            }


            // just one time, convert the bytes to png if needed.
            if (result.ContentType == "image/webp")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.WebP);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (result.ContentType == "image/svg+xml")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Svg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (result.ContentType == "image/jpeg")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Jpg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (result.ContentType == "image/png")
            {
                //Console.WriteLine("png do nothing, all good");
            }
            else if (result.ContentType == null)
            {
                //Console.WriteLine("contentType null, so fall into .png");
            }
            else
            {
                Console.WriteLine("some other weird contenttype. {result.ContentType}");
            }


            //okay now we have the real bytes and they've been converted to png format if needed.
            result.SetImageBytes(imageBytes);


            var savedImagePaths = new Dictionary<SaveType, string>();

            savedImagePaths[SaveType.Raw] = await ImageSaving.SaveImageAsync(result, settings, SaveType.Raw, generator);
            savedImagePaths[SaveType.FullAnnotation] = await ImageSaving.SaveImageAsync(result, settings, SaveType.FullAnnotation, generator);
            savedImagePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(result, settings, SaveType.FinalPrompt, generator);
            savedImagePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(result, settings, SaveType.InitialIdea, generator);
            savedImagePaths[SaveType.JustOverride] = await ImageSaving.SaveImageAsync(result, settings, SaveType.JustOverride, generator);
            savedImagePaths[SaveType.Label] = await ImageSaving.SaveImageAsync(result, settings, SaveType.Label, generator);

            return savedImagePaths;
        }

        public async Task<TaskProcessResult> ProcessAndSaveAsync(TaskProcessResult result, IImageGenerator generator)
        {
            try
            {
                if (!result.IsSuccess)
                {
                    Console.WriteLine("failur.");
                    return result;
                }
                var sw = Stopwatch.StartNew();
                byte[] imageBytes;
                if (!string.IsNullOrEmpty(result.Url))
                {
                    imageBytes = await ImageSaving.DownloadImageAsync(result);
                    result.DownloadTotalMs = sw.ElapsedMilliseconds;
                    var downloadResults = await DoSaveAsync(generator, imageBytes, result, _settings);
                    await SaveJsonLogAsync(result, downloadResults);
                    return result;
                }
                else
                {
                    var ii = 0;
                    foreach (var qq in result.Base64ImageDatas)
                    {
                        imageBytes = Convert.FromBase64String(qq);
                        var downloadResults = await DoSaveAsync(generator, imageBytes, result, _settings);
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
                GeneratorUsed = result.ImageGenerator,
                result.ErrorMessage,
            };

            string jsonString = JsonConvert.SerializeObject(jsonLog, Formatting.Indented);

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
