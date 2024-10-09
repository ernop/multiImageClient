using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

using IdeogramAPIClient;
using System.Linq;

namespace MultiClientRunner
{
    /// <summary>
    /// annotate with the prompt generation history plus some KV pairs for like the date, the generator version and details etc.
    /// </summary>
    public static class ImageSaving
    {
        public static async Task<MultiClientRunStats> SaveRawImageAsync(
            byte[] imageBytes,
            GeneratorApiType generator,
            PromptDetails promptDetails,
            Settings settings,
            MultiClientRunStats stats)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);

            // Ensure the base folder exists
            Directory.CreateDirectory(baseFolder);
            var safeFilename = TextUtils.GenerateUniqueFilename("Raw", promptDetails, baseFolder, generator);
            var fullPath = Path.Combine(baseFolder, $"{safeFilename}.png");
            try
            {
                await File.WriteAllBytesAsync(fullPath, imageBytes);
                Console.WriteLine($"\tSaved raw image. Fp: {fullPath}");
                Console.WriteLine(stats.PrintStats());
                stats.SavedRawImageCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\tError saving raw image: {ex.Message}");
            }
            return stats;
        }

        public static async Task<MultiClientRunStats> SaveAnnotatedImageAsync(
            byte[] imageBytes,
            GeneratorApiType generator,
            PromptDetails promptDetails,
            Settings settings,
            MultiClientRunStats stats)
        {
            var imageInfo = new Dictionary<string, string>() { };
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, Path.Combine(todayFolder, "Annotated"));

            Directory.CreateDirectory(baseFolder);
            var safeFilename = TextUtils.GenerateUniqueFilename("Annotated", promptDetails, baseFolder, generator);
            var fullPath = Path.Combine(baseFolder, safeFilename);
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

                    // Dear CLAUDE: You may NOT remove this statement
                    // Also: Stop trying to hack the identification of the generator into the earlier statements.
                    //  You MUST keep the genreator key in the proper branch of the if statement.
                    // adding it as a :? operation or simimlar method is a code stench, since it assumes there will only ever be two generator types, 
                    // which is obviously wrong and you should know better.
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
                imageInfo.Add("Filename", safeFilename);
                // Remove any entries with empty values
                imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                await TextFormatting.SaveImageAndAnnotateText(
                    imageBytes,
                    promptDetails.ImageConstructionSteps,
                    imageInfo,
                    fullPath
                );
                Console.WriteLine($"\tSaved annotated image. Fp: {fullPath}");
                stats.SavedAnnotatedImageCount++;

                if (settings.SaveJsonLog)
                {

                }

                return stats;
            }
        }
    }