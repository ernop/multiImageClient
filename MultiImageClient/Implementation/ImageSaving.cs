using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using IdeogramAPIClient;

namespace MultiImageClient
{
    public static class ImageSaving
    {
        private static readonly HttpClient httpClient = new HttpClient();

        public static async Task<byte[]> DownloadImageAsync(TaskProcessResult result)
        {
            try
            {
                using var response = await httpClient.GetAsync(result.Url);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"Failed to download image: {response.StatusCode}");
                    return Array.Empty<byte>();
                }

                var res = await response.Content.ReadAsByteArrayAsync();
                if (res.Length == 0)
                {
                    Logger.Log($"Downloaded image is empty");
                    return Array.Empty<byte>();
                }

                Logger.Log($"\tDownloading image from: {result.Url}, bytes:{res.Length}");
                return res;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to download image from {result.Url}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public static async Task<string> SaveImageAsync(
            byte[] imageBytes,
            TaskProcessResult result,
            Settings settings,
            MultiClientRunStats stats,
            SaveType saveType,
            string promptGeneratorName)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);

            if (saveType != SaveType.Raw)
            {
                baseFolder = Path.Combine(baseFolder, saveType.ToString());
            }

            Directory.CreateDirectory(baseFolder);
            var safeFilename = FilenameGenerator.GenerateUniqueFilename(result, baseFolder, promptGeneratorName, saveType);
            var fullPath = Path.Combine(baseFolder, safeFilename);

            try
            {
                if (File.Exists(fullPath))
                {
                    throw new Exception("no overwriting!");
                }
                await File.WriteAllBytesAsync(fullPath, imageBytes);

                if (saveType == SaveType.Raw)
                {
                    Logger.Log($"\tSaved {saveType} image. Fp: {fullPath}");
                    stats.SavedRawImageCount++;
                }
                else
                {
                    if (!fullPath.EndsWith(".svg"))
                    {
                        var imageInfo = GetAnnotationDefaultData(result, fullPath, saveType, promptGeneratorName);
                        var usingSteps = GetUsingSteps(saveType, result.PromptDetails);
                        if (saveType == SaveType.JustOverride)
                        {
                            await TextFormatting.JustAddSimpleTextToBottomAsync(
                                imageBytes,
                                usingSteps,
                                imageInfo,
                                fullPath,
                                saveType
                            );
                        }
                        else
                        {
                            await TextFormatting.SaveImageAndAnnotate(
                                imageBytes,
                                usingSteps,
                                imageInfo,
                                fullPath,
                                saveType
                            );
                        }
                        stats.SavedAnnotatedImageCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"\tError saving {saveType} image: {ex.Message}\r\n{ex}");
            }

            return fullPath;
        }

        private static IEnumerable<PromptHistoryStep> GetUsingSteps(SaveType saveType, PromptDetails promptDetails)
        {
            return saveType switch
            {
                SaveType.FullAnnotation => promptDetails.TransformationSteps,
                SaveType.InitialIdea or SaveType.FinalPrompt or SaveType.Raw or SaveType.JustOverride => new List<PromptHistoryStep>(),
                _ => throw new Exception("Invalid SaveType")
            };
        }

        private static Dictionary<string, string> GetAnnotationDefaultData(
            TaskProcessResult result,
            string fullPath,
            SaveType saveType,
            string promptGeneratorName)
        {
            var imageInfo = new Dictionary<string, string>();
            var promptDetails = result.PromptDetails;

            switch (saveType)
            {
                case SaveType.FullAnnotation:
                    AddFullAnnotationInfo(imageInfo, result.ImageGenerator, promptDetails, promptGeneratorName, result);
                    imageInfo.Add("Filename", Path.GetFileName(fullPath));
                    break;
                case SaveType.InitialIdea:
                    var initialPrompt = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Initial Prompt", initialPrompt);
                    break;
                case SaveType.FinalPrompt:
                    var finalPrompt = promptDetails.Prompt;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Final Prompt", finalPrompt);
                    break;
                case SaveType.Raw:
                    // No annotation
                    break;
                case SaveType.JustOverride:
                    imageInfo.Add("JUST", result.PromptDetails.IdentifyingConcept);
                    break;
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return imageInfo;
        }

        private static void AddFullAnnotationInfo(Dictionary<string, string> imageInfo, ImageGeneratorApiType generator, PromptDetails promptDetails, string promptGeneratorName, TaskProcessResult result)
        {
            switch (generator)
            {
                case ImageGeneratorApiType.Ideogram:
                    imageInfo.Add("Generator", "Ideogram V2");
                    var ideogramDetails = promptDetails.IdeogramDetails;
                    if (ideogramDetails.Model != default)
                        imageInfo.Add("Model", ideogramDetails.Model.ToString());
                    if (ideogramDetails.AspectRatio.HasValue)
                        imageInfo.Add("AspectRatio", IdeogramUtils.StringifyAspectRatio(ideogramDetails.AspectRatio.Value));
                    if (ideogramDetails.StyleType.HasValue)
                        imageInfo.Add("StyleType", ideogramDetails.StyleType.Value.ToString());
                    if (!string.IsNullOrWhiteSpace(ideogramDetails.NegativePrompt))
                        imageInfo.Add("NegativePrompt", ideogramDetails.NegativePrompt);
                    break;
                case ImageGeneratorApiType.BFL:
                    var bflDetails = promptDetails.BFLDetails;
                    imageInfo.Add("Generator", "BFL Flux 1.1");
                    if (bflDetails.Seed.HasValue)
                        imageInfo.Add("Seed", bflDetails.Seed.Value.ToString());
                    if (bflDetails.Width != default && bflDetails.Height != default)
                        imageInfo.Add("Size", $"{bflDetails.Width}x{bflDetails.Height}");
                    if (bflDetails.SafetyTolerance != default)
                        imageInfo.Add("SafetyTolerance", bflDetails.SafetyTolerance.ToString());
                    break;
                case ImageGeneratorApiType.Dalle3:
                    imageInfo.Add("Generator", "Dall-e 3");
                    var dalle3Details = promptDetails.Dalle3Details;
                    imageInfo.Add("Size", $"{dalle3Details.Size}");
                    imageInfo.Add("Quality", dalle3Details.Quality.ToString());
                    break;
                case ImageGeneratorApiType.Recraft:
                    imageInfo.Add("Generator", "Recraft");
                    imageInfo.Add("Mimetype", result.ContentType);
                    var recraftDetails = promptDetails.RecraftDetails;
                    imageInfo.Add("Size", $"{recraftDetails.size}");
                    imageInfo.Add("Style", recraftDetails.GetFullStyleName());
                    break;
            }
            imageInfo.Add("Kind", promptGeneratorName);
            imageInfo.Add("Generated", DateTime.Now.ToString());
        }
    }
}
