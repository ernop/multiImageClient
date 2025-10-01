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

        public async Task<Dictionary<SaveType, string>> DoSaveAsync(int n, PromptDetails pd, string contentType, byte[] imageBytes, IImageGenerator generator, Settings settings)
        {
            var thesePaths = new Dictionary<SaveType, string>();
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Logger.Log($"Empty or null image bytes received");
                throw new Exception("no bytes in the image data received; probably caller's problem.)");
            }

            // just one time, convert the bytes to png if needed.
            if (contentType == "image/webp")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.WebP);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (contentType == "image/svg+xml")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Svg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (contentType == "image/jpeg")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Jpg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (contentType == "image/png")
            {
                //Console.WriteLine("png do nothing, all good");
            }
            else if (contentType == null)
            {
                //Console.WriteLine("contentType null, so fall into .png");
            }
            else
            {
                Console.WriteLine("some other weird contenttype. {result.ContentType}");
            }

            thesePaths[SaveType.Raw] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.Raw, generator);
            thesePaths[SaveType.FullAnnotation] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.FullAnnotation, generator);
            thesePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.FinalPrompt, generator);
            thesePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.InitialIdea, generator);
            thesePaths[SaveType.JustOverride] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.JustOverride, generator);
            thesePaths[SaveType.Label] = await ImageSaving.SaveImageAsync(pd, imageBytes, n, contentType, settings, SaveType.Label, generator);


            return thesePaths;
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
                    // downloading it can just fail sometimes.
                    result.SetImageBytes(0, imageBytes);
                    var pd = result.PromptDetails.Copy();
                    var downloadResults = await DoSaveAsync(0, pd, result.ContentType, imageBytes, generator, _settings);
                    await SaveJsonLogAsync(result, downloadResults);
                    return result;
                }
                else
                {
                    var ii = 0;
                    foreach (var qq in result.Base64ImageDatas)
                    {
                        imageBytes = Convert.FromBase64String(qq.bytesBase64);
                        result.SetImageBytes(ii, imageBytes);
                        var pd = result.PromptDetails.Copy();
                        
                        if (pd.Prompt != qq.newPrompt && !string.IsNullOrEmpty(qq.newPrompt))
                        {

                            if (generator.ApiType == ImageGeneratorApiType.GoogleImagen4)
                            {
                                pd.AddStep(qq.newPrompt, TransformationType.Imagen4Rewrite);
                            }
                            else
                            {
                                Console.WriteLine("s");
                            }
                        }
                            var downloadResults = await DoSaveAsync(ii, pd, result.ContentType, imageBytes, generator, _settings);
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
