#nullable enable
using IdeogramAPIClient;

using ImageMagick;

using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;
using System.Text.Json.Serialization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;


namespace MultiImageClient
{
    public static class ImageSaving
    {
        private static readonly HttpClient httpClient = new HttpClient();

        private const int LabelRightSideWidth = 200;
        private const float LabelTotalLineSpacing = 1.3f;
        
        private const int LabelFontSize = 12;
        private const int LabelRightFontSize = 10;
        

        public static void ConvertWebpTopng(string inputFp)
        {
            var im = new MagickImage(inputFp);
            var newFp = Path.ChangeExtension(inputFp, ".png");
            im.Write(newFp);
        }

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

        public static async Task<string> SaveImageAsync(PromptDetails promptDetails, byte[] imageBytes, int imageCountN, string contentType, Settings settings, SaveType saveType, IImageGenerator generator)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);

            if (saveType != SaveType.Raw)
            {
                baseFolder = Path.Combine(baseFolder, saveType.ToString());
            }

            Directory.CreateDirectory(baseFolder);

            var usingPromptTextPart = FilenameGenerator.TruncatePrompt(promptDetails.Prompt, 90);
            var generatorFilename = generator.GetFilenamePart(promptDetails);

            var safeFilename = FilenameGenerator.GenerateUniqueFilename($"{generatorFilename}_{usingPromptTextPart}", imageCountN, contentType, baseFolder, saveType);
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
                    //Logger.Log($"Saved {saveType} image. Fp: {fullPath}");
                    //_stats.SavedRawImageCount++;
                }
                else
                {
                    var specPart = generator.GetGeneratorSpecPart();
                    var imageInfo = GetAnnotationDefaultData(specPart, promptDetails, fullPath, saveType, generator);
                    var usingSteps = GetUsingSteps(saveType, promptDetails);
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
                    else if (saveType == SaveType.Label)
                    {
                        var rightParts = generator.GetRightParts();
                        var costPart = $"{generator.GetCost()} $USD";
                        rightParts.Add(costPart);
                        rightParts.Add("MultiImageClient");
                        using var originalImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
                        var label = MakeLabelGeneral(originalImage.Width, promptDetails.Prompt, rightParts);

                        using var labelImage = SixLabors.ImageSharp.Image.Load<Rgba32>(label);
                        int newHeight = originalImage.Height + labelImage.Height;
                        using var combinedImage = new Image<Rgba32>(originalImage.Width, newHeight);
                        combinedImage.Mutate(ctx =>
                        {
                            ctx.ApplyStandardGraphicsOptions();
                            ctx.DrawImage(originalImage, new Point(0, 0), 1f);
                            ctx.DrawImage(labelImage, new Point(0, originalImage.Height), 1f);
                        });

                        // Save the combined image
                        await combinedImage.SaveAsPngAsync(fullPath);
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
                    //stats.SavedAnnotatedImageCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"\tError saving {saveType} image: {ex.Message}\r\n{ex}");
            }

            return fullPath;
        }

        public static byte[] MakeLabelGeneral(int width, string prompt, List<string> rightParts)
        {
            var leftSideWidth = width - LabelRightSideWidth;
            var fontSize = LabelFontSize;
            var font = FontUtils.CreateFont(fontSize, FontStyle.Regular);
            var labelRightFont = FontUtils.CreateFont(LabelRightFontSize, FontStyle.Regular);

            // Measure left side text to determine height
            var leftTextOptions = FontUtils.CreateTextOptions(font,
                HorizontalAlignment.Left, VerticalAlignment.Top, LabelTotalLineSpacing);
            leftTextOptions.WrappingLength = leftSideWidth - (UIConstants.Padding * 2);

            int leftTextHeight = ImageUtils.MeasureTextHeight(prompt, font, LabelTotalLineSpacing, leftTextOptions.WrappingLength);

            // Calculate right side font size (scale down if needed to fit width)


            // Find the maximum width needed for right side text
            //foreach (var text in rightParts.Where(s => !string.IsNullOrEmpty(s)))
            //{
            //    var testOptions = FontUtils.CreateTextOptions(rightFont, HorizontalAlignment.Right);
            //    var textBounds = TextMeasurer.MeasureBounds(text, testOptions);

            // rightFont = ImageUtils.AutoSizeFont(text, LabelRightSideWidth, rightFontSize, UIConstants.MinFontSize, FontStyle.Regular);
            // rightFontSize = (int)rightFont.Size;
            //}

            // Calculate right side height using proper text height measurement
            var rightLineHeight = ImageUtils.MeasureTextHeight("Sample", labelRightFont, UIConstants.LineSpacing);
            var rightTextHeight = rightParts.Where(s => !string.IsNullOrEmpty(s)).Count() * rightLineHeight;

            // Overall height is the maximum of left and right sides plus padding
            int contentHeight = Math.Max(leftTextHeight, rightTextHeight);
            int totalHeight = contentHeight + (UIConstants.Padding * 2);

            using var image = ImageUtils.CreateStandardImage(width, totalHeight, UIConstants.Black);

            image.Mutate(ctx =>
            {
                var leftRect = new RectangleF(0, 0, leftSideWidth + 5, totalHeight);
                ctx.DrawTextWithBackground(leftRect, prompt, font, UIConstants.White, UIConstants.Black, HorizontalAlignment.Left, LabelTotalLineSpacing);

                // Draw right side text
                var yOffset = UIConstants.Padding;
                foreach (var text in rightParts.Where(s => !string.IsNullOrEmpty(s)))
                {
                    var rightTextOptions = FontUtils.CreateTextOptions(labelRightFont, HorizontalAlignment.Right, VerticalAlignment.Top, LabelTotalLineSpacing);
                    rightTextOptions.Origin = new PointF(width - UIConstants.Padding, yOffset);

                    ctx.DrawTextStandard(rightTextOptions, text, UIConstants.Gold);
                    yOffset += rightLineHeight;
                }
            });

            // Convert to byte array
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static IEnumerable<PromptHistoryStep> GetUsingSteps(SaveType saveType, PromptDetails promptDetails)
        {
            return saveType switch
            {
                SaveType.FullAnnotation => promptDetails.TransformationSteps,
                SaveType.InitialIdea or SaveType.FinalPrompt or SaveType.Raw or SaveType.JustOverride or SaveType.Label => new List<PromptHistoryStep>() { promptDetails.TransformationSteps.First() },
                _ => throw new Exception("Invalid SaveType")
            };
        }

        private static Dictionary<string, string> GetAnnotationDefaultData(string generatorIdentifier, PromptDetails promptDetails, string fullPath, SaveType saveType, IImageGenerator generator)
        {
            var imageInfo = new Dictionary<string, string>();

            switch (saveType)
            {
                case SaveType.FullAnnotation:
                    imageInfo.Add("Filename", Path.GetFileName(fullPath));
                    break;
                case SaveType.InitialIdea:
                    var initialPrompt = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", generatorIdentifier);
                    imageInfo.Add("Initial Prompt", initialPrompt);
                    break;
                case SaveType.FinalPrompt:
                    var finalPrompt = promptDetails.Prompt;
                    imageInfo.Add("Producer", generatorIdentifier);
                    imageInfo.Add("Final Prompt", finalPrompt);
                    break;
                case SaveType.Raw:
                    // No annotation
                    break;
                case SaveType.JustOverride:
                    var initialPrompt2 = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", generatorIdentifier);
                    imageInfo.Add("Initial Prompt", initialPrompt2);
                    break;
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return imageInfo;
        }
    }
}
