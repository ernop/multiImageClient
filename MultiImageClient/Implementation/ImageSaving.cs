using IdeogramAPIClient;

using ImageMagick;

using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


namespace MultiImageClient
{
    public static class ImageSaving
    {
        private static readonly HttpClient httpClient = new HttpClient();

        private const int LabelRightSideWidth = 200;
        private const float LabelTotalLineSpacing = 1.3f;
        private const int PlaceholderWidth = 300;
        private const int LabelFontSize = 12;
        private const int LabelRightFontSize = 10;
        private const int CombinedImageGeneratorFontSize = 24;
        private const int CombinedImagePromptFontSize = 32;
        private const float LabelLineHeightMultiplier = 1.5f;

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


        public static async Task<string> CombineImagesHorizontallyAsync(IEnumerable<TaskProcessResult> results, string prompt, Settings settings)
        {

            // --- fonts ------------------------------------------------------
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            // --- load images (incl. placeholders for failures) -------------
            var loadedImages = LoadResultImages(results, PlaceholderWidth).ToList();

            // --- measure ----------------------------------------------------
            int totalWidth = loadedImages.Sum(img => img.Width);
            int maxImageHeight = loadedImages.Where(i => i.Success)
                                             .Select(i => i.Height)
                                             .DefaultIfEmpty(PlaceholderWidth)
                                             .Max();

            // height of status (generator) labels
            int subtitleHeight = loadedImages
                .Select(li => ImageUtils.MeasureTextHeight(
                                  li.OriginalTaskProcessResult.ImageGeneratorDescription,
                                  generatorFont,
                                  UIConstants.LineSpacing))
                .DefaultIfEmpty(0)
                .Max();

            // height of the prompt block (with wrapping)
            int wrappingWidth = totalWidth - (UIConstants.Padding * 4);
            int promptHeight = ImageUtils.MeasureTextHeight(prompt, promptFont, UIConstants.LineSpacing, wrappingWidth) + (UIConstants.Padding * 3);
            int trailingHangdownHeight = UIConstants.Padding * 2;

            int totalHeight = maxImageHeight + subtitleHeight + promptHeight + trailingHangdownHeight;

            // --- render -----------------------------------------------------
            using var combinedImage = ImageUtils.CreateStandardImage(totalWidth, totalHeight, UIConstants.White);

            combinedImage.Mutate(ctx =>
            {
                int currentX = 0;
                var labelOpts = FontUtils.CreateTextOptions(generatorFont, HorizontalAlignment.Center, VerticalAlignment.Top, UIConstants.LineSpacing);

                foreach (var li in loadedImages)
                {
                    if (li.Success && li.Image != null)
                    {
                        ctx.DrawImage(li.Image, new Point(currentX, 0), 1f);
                    }
                    else
                    {
                        var rect = new RectangleF(currentX, 0, li.Width, maxImageHeight);
                        ctx.DrawErrorPlaceholder(rect, li.OriginalTaskProcessResult.ErrorMessage, generatorFont);
                    }

                    labelOpts.Origin = new PointF(currentX + li.Width / 2f, maxImageHeight + UIConstants.Padding);

                    var labelColor = li.Success ? UIConstants.SuccessGreen
                                                : UIConstants.ErrorRed;
                    var labelText = li.OriginalTaskProcessResult.ImageGeneratorDescription ?? "missing";
                    ctx.DrawTextStandard(labelOpts, labelText, labelColor);

                    currentX += li.Width;
                }

                // draw prompt
                var promptOpts = FontUtils.CreateTextOptions(promptFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                promptOpts.Origin = new PointF(UIConstants.Padding * 2, maxImageHeight + subtitleHeight + (UIConstants.Padding * 3));
                promptOpts.WrappingLength = wrappingWidth;

                ctx.DrawTextStandard(promptOpts, prompt, UIConstants.Black);
            });

            // --- save & clean up -------------------------------------------
            var outputPath = await SaveCombinedImage(combinedImage, prompt, settings);

            foreach (var li in loadedImages) { li.Image?.Dispose(); }

            return outputPath;
        }

        private static IEnumerable<LoadedImage> LoadResultImages(IEnumerable<TaskProcessResult> results, int placeholderWidth)
        {
            var loadedImages = new List<LoadedImage>();

            foreach (var result in results)
            {
                if (result.IsSuccess)
                {
                    try
                    {
                        foreach (var imageBytes in result.GetAllImages)
                        {
                            if (imageBytes != null && imageBytes.Length > 0)
                            {
                                var image = Image.Load<Rgba32>(imageBytes);

                                // Resize if height > 1300px, keeping proportions and max height of 1024px
                                if (image.Height > 1300)
                                {
                                    var aspectRatio = (float)image.Width / image.Height;
                                    var newHeight = 1024;
                                    var newWidth = (int)(newHeight * aspectRatio);

                                    Logger.Log($"Resizing image from {image.Width}x{image.Height} to {newWidth}x{newHeight} for combining");
                                    image.Mutate(x => x.Resize(newWidth, newHeight));
                                }

                                loadedImages.Add(new LoadedImage
                                {
                                    OriginalTaskProcessResult = result,
                                    Success = true,
                                    Image = image,
                                    Width = image.Width,
                                    Height = image.Height
                                });
                            }
                            else
                            {
                                // Success but no bytes somehow
                                Logger.Log($"No image bytes for successful result from {result.ImageGenerator}");
                                loadedImages.Add(CreateFailedGenerationPlaceholder(result, false, placeholderWidth));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // GetImageBytes() might throw if bytes weren't set, or image loading failed
                        Logger.Log($"Failed to get/load image from {result.ImageGenerator}: {ex.Message}");
                        loadedImages.Add(CreateFailedGenerationPlaceholder(result, false, placeholderWidth));
                    }
                }
                else
                {
                    // Failed result - show placeholder with error
                    var errorMsg = !string.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage
                        : result.GenericImageErrorType.ToString();
                    Logger.Log($"Result failed for {result.ImageGenerator}: {errorMsg}");
                    loadedImages.Add(CreateFailedGenerationPlaceholder(result, false, placeholderWidth));
                }
            }

            return loadedImages.OrderBy(el => el.OriginalTaskProcessResult.ImageGeneratorDescription);
        }

        /// when an image fails to generate we add in a fixed-width holder in any combined image, to show what happened and be able to
        /// at least see the filtering rules etc.
        private static LoadedImage CreateFailedGenerationPlaceholder(TaskProcessResult result, bool successFindingImage, int placeholderWidth)
        {
            return new LoadedImage
            {
                OriginalTaskProcessResult = result,
                Image = null,
                Success = successFindingImage,
                Width = placeholderWidth,
                Height = placeholderWidth
            };
        }

        private static async Task<string> SaveCombinedImage(Image<Rgba32> image, string prompt, Settings settings)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder, "Combined");
            Directory.CreateDirectory(baseFolder);

            var truncatedPrompt = FilenameGenerator.TruncatePrompt(prompt, 50);
            var baseFilename = $"combined_{truncatedPrompt}_{DateTime.Now:HHmmss}";
            var safeFilename = FilenameGenerator.SanitizeFilename(baseFilename);
            var outputPath = Path.Combine(baseFolder, $"{safeFilename}.png");

            // Ensure unique filename
            int counter = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(baseFolder, $"{safeFilename}_{counter}.png");
                counter++;
            }

            await image.SaveAsPngAsync(outputPath);
            Logger.Log($"Combined image saved: {outputPath}");

            return outputPath;
        }

        private class LoadedImage
        {
            public TaskProcessResult OriginalTaskProcessResult { get; set; }
            public bool Success { get; set; }
            public Image<Rgba32> Image { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private class ImageDimensions
        {
            public int TotalWidth { get; set; }
            public int MaxImageHeight { get; set; }
            public int SubtitleHeight { get; set; }
            public int PromptHeight { get; set; }
            public int TotalHeight { get; set; }
        }
    }
}
