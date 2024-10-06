using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;
using System.IO;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Anthropic.SDK;

using System.Threading;
using IdeogramAPIClient;

namespace MultiClientRunner
{
    public static class ImageAnnotation
    {
        public static async Task<MultiClientRunStats> SaveGeneratedImagesAsync(GenerateResponse response, PromptDetails promptDetails, IdeogramGenerateRequest request, Settings settings, MultiClientRunStats stats)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);
            Console.WriteLine($"\tSaving images to: {baseFolder}");

            // Ensure the base folder exists
            Directory.CreateDirectory(baseFolder);

            foreach (var imageObject in response.Data)
            {
                var safeFilename = TextUtils.GenerateUniqueFilename(promptDetails, baseFolder);
                var fullPath = Path.Combine(baseFolder, $"{safeFilename}.png");
                
                var imageBytes = await DownloadImageAsync(imageObject.Url);

                if (settings.SaveRawImage)
                {
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
                }
                else
                {
                    Console.WriteLine("\tSkipping raw image save (SaveRawImage is false)");
                }

                if (settings.SaveAnnotatedImage)
                {
                    if (!string.IsNullOrEmpty(imageObject.Prompt) && imageObject.Prompt != request.Prompt)
                    {
                        var magicPromptStep = new ImageConstructionStep("Rewritten by ideogram", imageObject.Prompt);
                        promptDetails.ImageConstructionSteps.Add(magicPromptStep);
                    }

                    var imageInfo = new Dictionary<string, string>
                    {
                        {"Model", request.Model.ToString()},
                        {"Seed", imageObject.Seed.ToString()},
                        {"Safe", imageObject.IsImageSafe.ToString()},
                        {"Generated", DateTime.Now.ToString()},
                        {"AspectRatio", IdeogramUtils.StringifyAspectRatio(request.AspectRatio.Value)},
                        {"StyleType", request.StyleType?.ToString() ?? "N/A"},
                        {"NegativePrompt", request.NegativePrompt ?? "N/A"}
                    };

                    Directory.CreateDirectory(Path.Combine(baseFolder, "Annotated"));
                    await TextUtils.SaveImageAndAnnotateText(
                        imageBytes,
                        promptDetails.ImageConstructionSteps, 
                        imageInfo, 
                        Path.Combine(baseFolder, $"Annotated/{safeFilename}_annotated.png")
                    );
                    Console.WriteLine($"\tSaved annotated image. Fp: {fullPath}");
                    stats.SavedAnnotatedImageCount++;
                    // savedImageCount++;
                }

                if (settings.SaveJsonLog)
                {
                    var jsonLog = new
                    {
                        Timestamp = DateTime.UtcNow,
                        Request = request,
                        Response = imageObject
                    };
                    await File.WriteAllTextAsync(
                        Path.Combine(baseFolder, $"{safeFilename}.json"), 
                        JsonConvert.SerializeObject(jsonLog, Formatting.Indented)
                    );
                }
            }

            return stats;
        }

        private static async Task<byte[]> DownloadImageAsync(string imageUrl)
        {
            using var httpClient = new HttpClient();
            return await httpClient.GetByteArrayAsync(imageUrl);
        }
    }
}