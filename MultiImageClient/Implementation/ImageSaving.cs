using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

using IdeogramAPIClient;
using System.Linq;


namespace MultiImageClient
{
    public enum SaveType
    {
        Raw = 1,
        FullAnnotation = 2,
        InitialIdea = 3,
        FinalPrompt = 4,
        JustOverride = 5, // when w generate the prompt sometimes we just have a core word/phrase called the "IdentifyingConcept" which we want to make visible in both the filename and in this version with the subtitle for illustrative purposes. If you get one of these then just draw the text large, centered, in a nice font, with no other junk.
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
            var fullPath = Path.Combine(baseFolder, $"{safeFilename}{result.ImageGenerator.GetFileExtension()}");

            try
            {
                if (File.Exists(fullPath))
                {
                    Console.WriteLine("Overwriting!", fullPath);
                    throw new Exception("no overwriting!");
                }
                await File.WriteAllBytesAsync(fullPath, imageBytes);
                if (saveType == SaveType.Raw)
                {
                    Console.WriteLine($"\tSaved {saveType} image. Fp: {fullPath}");
                }

                if (saveType == SaveType.Raw)
                {
                    stats.SavedRawImageCount++;
                }
                else
                {
                    await AddAnnotationsAsync(imageBytes, result, fullPath, stats, saveType, promptGeneratorName);
                    stats.SavedAnnotatedImageCount++;
                }
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
            var generator = result.ImageGenerator;
            var promptDetails = result.PromptDetails;
            var imageInfo = new Dictionary<string, string>();
            var usingSteps = promptDetails.TransformationSteps;
            switch (saveType)
            {
                case SaveType.FullAnnotation:

                    AddFullAnnotationInfo(imageInfo, generator, promptDetails, promptGeneratorName);
                    imageInfo.Add("Filename", Path.GetFileName(fullPath));
                    usingSteps = promptDetails.TransformationSteps;
                    break;
                case SaveType.InitialIdea:

                    var initialPrompt = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", generator.ToString());
                    imageInfo.Add("Initial Prompt", initialPrompt);
                    usingSteps = new List<PromptHistoryStep>();
                    break;
                case SaveType.FinalPrompt:

                    var finalPrompt = promptDetails.Prompt;
                    imageInfo.Add("Producer", generator.ToString());
                    imageInfo.Add("Final Prompt", finalPrompt);
                    usingSteps = new List<PromptHistoryStep>();
                    break;
                case SaveType.Raw:
                    imageInfo = new Dictionary<string, string>();
                    usingSteps = new List<PromptHistoryStep>();
                    break;
                case SaveType.JustOverride:
                    imageInfo.Add("JUST", result.PromptDetails.IdentifyingConcept);
                    break;
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            await TextFormatting.SaveImageAndAnnotateText(
                imageBytes,
                usingSteps,
                imageInfo,
                fullPath,
                saveType
            );
        }

        private static void AddFullAnnotationInfo(Dictionary<string, string> imageInfo, ImageGeneratorApiType generator, PromptDetails promptDetails, string promptGeneratorName)
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
                    //imageInfo.Add("Rewriting prompt", bflDetails.PromptUpsampling.ToString());
                    if (bflDetails.Seed != default)
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
            }
            imageInfo.Add("Kind", promptGeneratorName);
            imageInfo.Add("Generated", DateTime.Now.ToString());
        }
    }
}
