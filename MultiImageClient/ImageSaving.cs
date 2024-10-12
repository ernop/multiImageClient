using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

using IdeogramAPIClient;
using System.Linq;

namespace MultiClientRunner
{
    public enum SaveType
    {
        Raw = 1,
        FullAnnotation = 2,
        InitialIdea = 3,
        FinalPrompt = 4
    }

    public static class ImageSaving
    {
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
            var safeFilename = TextUtils.GenerateUniqueFilename(result, baseFolder, promptGeneratorName, saveType);
            var fullPath = Path.Combine(baseFolder, $"{safeFilename}{result.Generator.GetFileExtension()}");

            try
            {
                await File.WriteAllBytesAsync(fullPath, imageBytes);
                Console.WriteLine($"\tSaved {saveType} image. Fp: {fullPath}");

                if (saveType == SaveType.Raw)
                {
                    stats.SavedRawImageCount++;
                }
                else
                {
                    await AddAnnotationsAsync(imageBytes, result, fullPath, stats, saveType, promptGeneratorName);
                    stats.SavedAnnotatedImageCount++;
                }

                Console.WriteLine(stats.PrintStats());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\tError saving {saveType} image: {ex.Message}");
            }

            return fullPath;
        }

        private static async Task AddAnnotationsAsync(
            byte[] imageBytes,
            TaskProcessResult result,
            string fullPath,
            MultiClientRunStats stats,
            SaveType saveType,
            string promptGeneratorName)
        {
            var generator = result.Generator;
            var promptDetails = result.PromptDetails;
            var imageInfo = new Dictionary<string, string>();
            var usingSteps = promptDetails.ImageConstructionSteps;
            if (saveType == SaveType.FullAnnotation)
            {
                AddFullAnnotationInfo(imageInfo, generator, promptDetails, promptGeneratorName);
                imageInfo.Add("Filename", Path.GetFileName(fullPath));
                usingSteps = promptDetails.ImageConstructionSteps;
            }
            else if (saveType == SaveType.InitialIdea)
            {
                var initialPrompt = promptDetails.ImageConstructionSteps.First().Details;
                imageInfo.Add("Producer", generator.ToString());
                imageInfo.Add("Initial Prompt", initialPrompt);
                usingSteps = new List<ImageConstructionStep>();
            }
            else if (saveType == SaveType.FinalPrompt)
            {
                var finalPrompt = promptDetails.Prompt;
                imageInfo.Add("Producer", generator.ToString());
                imageInfo.Add("Final Prompt", finalPrompt);
                usingSteps = new List<ImageConstructionStep>();
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            await TextFormatting.SaveImageAndAnnotateText(
                imageBytes,
                usingSteps,
                imageInfo,
                fullPath
            );
        }

        private static void AddFullAnnotationInfo(Dictionary<string, string> imageInfo, GeneratorApiType generator, PromptDetails promptDetails, string promptGeneratorName)
        {
            imageInfo.Add("Kind", promptGeneratorName);
            switch (generator)
            {
                case GeneratorApiType.Ideogram:
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
                case GeneratorApiType.BFL:
                    imageInfo.Add("Generator", "BFL Flux 1.1");
                    var bflDetails = promptDetails.BFLDetails;
                    if (bflDetails.Width != default && bflDetails.Height != default)
                        imageInfo.Add("Size", $"{bflDetails.Width}x{bflDetails.Height}");
                    if (bflDetails.PromptUpsampling)
                        imageInfo.Add("PromptUpsampling", bflDetails.PromptUpsampling.ToString());
                    if (bflDetails.SafetyTolerance != default)
                        imageInfo.Add("SafetyTolerance", bflDetails.SafetyTolerance.ToString());
                    break;
                case GeneratorApiType.Dalle3:
                    imageInfo.Add("Generator", "Dall-e 3");
                    var dalle3Details = promptDetails.Dalle3Details;
                    imageInfo.Add("Size", $"{dalle3Details.Size}");
                    imageInfo.Add("Quality", dalle3Details.Quality.ToString());
                    break;
            }
            imageInfo.Add("Generated", DateTime.Now.ToString());
        }
    }
}
