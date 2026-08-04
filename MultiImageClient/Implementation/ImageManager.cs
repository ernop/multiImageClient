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
            GenerationArchive.Initialize(settings);
        }

        public async Task<Dictionary<SaveType, string>> DoSaveAsync(
            int n,
            PromptDetails pd,
            string contentType,
            byte[] imageBytes,
            IImageGenerator generator,
            Settings settings,
            bool saveAnnotatedVariants = true)
        {
            var thesePaths = new Dictionary<SaveType, string>();
            if (imageBytes == null || imageBytes.Length == 0)
            {
                Logger.Log($"Empty or null image bytes received");
                throw new Exception("no bytes in the image data received; probably caller's problem.)");
            }

            thesePaths[SaveType.Raw] = await ImageSaving.SaveImageAsync(
                pd,
                imageBytes,
                n,
                contentType,
                settings,
                SaveType.Raw,
                generator);

            if (!saveAnnotatedVariants)
            {
                return thesePaths;
            }

            // Annotated variants decode to PNG for ImageSharp/Magick overlays.
            var annotatedBytes = ConvertBytesForAnnotatedVariants(imageBytes, contentType);
            const string annotatedContentType = "image/png";

            thesePaths[SaveType.FullAnnotation] = await ImageSaving.SaveImageAsync(pd, annotatedBytes, n, annotatedContentType, settings, SaveType.FullAnnotation, generator);
            thesePaths[SaveType.FinalPrompt] = await ImageSaving.SaveImageAsync(pd, annotatedBytes, n, annotatedContentType, settings, SaveType.FinalPrompt, generator);
            thesePaths[SaveType.InitialIdea] = await ImageSaving.SaveImageAsync(pd, annotatedBytes, n, annotatedContentType, settings, SaveType.InitialIdea, generator);
            thesePaths[SaveType.JustOverride] = await ImageSaving.SaveImageAsync(pd, annotatedBytes, n, annotatedContentType, settings, SaveType.JustOverride, generator);
            thesePaths[SaveType.Label] = await ImageSaving.SaveImageAsync(pd, annotatedBytes, n, annotatedContentType, settings, SaveType.Label, generator);


            return thesePaths;
        }

        private static byte[] ConvertBytesForAnnotatedVariants(byte[] imageBytes, string? contentType)
        {
            if (contentType == "image/png" || contentType == null)
            {
                return imageBytes;
            }

            if (contentType == "image/jpeg")
            {
                using var image = new MagickImage(imageBytes, MagickFormat.Jpg);
                return image.ToByteArray(MagickFormat.Png);
            }

            if (contentType == "image/webp")
            {
                using var image = new MagickImage(imageBytes, MagickFormat.WebP);
                return image.ToByteArray(MagickFormat.Png);
            }

            if (contentType == "image/svg+xml")
            {
                using var image = new MagickImage(imageBytes, MagickFormat.Svg);
                return image.ToByteArray(MagickFormat.Png);
            }

            Logger.Log($"\tUnexpected content type for annotation conversion: {contentType}; using bytes as-is.");
            return imageBytes;
        }

        public async Task<TaskProcessResult> ProcessAndSaveAsync(
            TaskProcessResult result,
            IImageGenerator generator,
            bool saveAnnotatedVariants = true)
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
                    var downloadResults = await DoSaveAsync(
                        0,
                        pd,
                        result.ContentType,
                        imageBytes,
                        generator,
                        _settings,
                        saveAnnotatedVariants);
                    result.SetSavedImagePaths(0, downloadResults);
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
                        var downloadResults = await DoSaveAsync(
                            ii,
                            pd,
                            result.ContentType,
                            imageBytes,
                            generator,
                            _settings,
                            saveAnnotatedVariants);
                        result.SetSavedImagePaths(ii, downloadResults);
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
                result.IsSuccess = false;
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
