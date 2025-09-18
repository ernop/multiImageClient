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
        private const int CombinedImageGeneratorFontSize = 40;
        private const int CombinedImagePromptFontSize = 56;
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

        public static async Task<string> SaveImageAsync(
            TaskProcessResult result,
            Settings settings,
            SaveType saveType,
            IImageGenerator generator)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);

            if (saveType != SaveType.Raw)
            {
                baseFolder = Path.Combine(baseFolder, saveType.ToString());
            }

            Directory.CreateDirectory(baseFolder);

            var usingPromptTextPart = FilenameGenerator.TruncatePrompt(result.PromptDetails.Prompt, 90);
            var generatorFilename = generator.GetFilenamePart(result.PromptDetails);

            var safeFilename = FilenameGenerator.GenerateUniqueFilename($"{generatorFilename}_{usingPromptTextPart}", result, baseFolder, saveType);
            var fullPath = Path.Combine(baseFolder, safeFilename);

            try
            {
                if (File.Exists(fullPath))
                {
                    throw new Exception("no overwriting!");
                }
                await File.WriteAllBytesAsync(fullPath, result.GetImageBytes());

                if (saveType == SaveType.Raw)
                {
                    //Logger.Log($"Saved {saveType} image. Fp: {fullPath}");
                    //_stats.SavedRawImageCount++;
                }
                else
                {
                    var imageInfo = GetAnnotationDefaultData(result, fullPath, saveType, generator);
                    var usingSteps = GetUsingSteps(saveType, result.PromptDetails);
                    if (saveType == SaveType.JustOverride)
                    {
                        await TextFormatting.JustAddSimpleTextToBottomAsync(
                            result.GetImageBytes(),
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
                        using var originalImage = SixLabors.ImageSharp.Image.Load<Rgba32>(result.GetImageBytes());
                        var label = MakeLabelGeneral(originalImage.Width, result.PromptDetails.Prompt, rightParts);

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
                            result.GetImageBytes(),
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

            int leftTextHeight = ImageUtils.MeasureTextHeight(prompt, font, UIConstants.LineSpacing, leftTextOptions.WrappingLength);

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
                // Draw left side box border and text
                var leftRect = new RectangleF(0, 0, leftSideWidth + 5, totalHeight);
                ctx.DrawTextWithBackground(leftRect, prompt, font, UIConstants.White, UIConstants.Black, HorizontalAlignment.Left);

                // Draw right side box border
                //ctx.Draw(UIConstants.SuccessGreen, 1f, new RectangleF(leftSideWidth, 0, LabelRightSideWidth, totalHeight));

                // Draw right side text (gold color, right-aligned)
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

        private static Dictionary<string, string> GetAnnotationDefaultData(
            TaskProcessResult result,
            string fullPath,
            SaveType saveType,
            IImageGenerator generator)
        {
            var imageInfo = new Dictionary<string, string>();
            var promptDetails = result.PromptDetails;

            switch (saveType)
            {
                case SaveType.FullAnnotation:
                    //AddFullAnnotationInfo(imageInfo, result.ImageGenerator, promptDetails, promptGeneratorName, result);
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
                    var initialPrompt2 = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Initial Prompt", initialPrompt2);
                    break;
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return imageInfo;
        }

        // Image combining functionality (previously in ImageCombiner.cs)
        public static async Task<string> CombineImagesHorizontallyAsync(
            IEnumerable<TaskProcessResult> results,
            string prompt,
            Settings settings)
        {


            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Regular);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Bold);

            var loadedImages = LoadResultImages(results, PlaceholderWidth);
            var dimensions = CalculateDimensions(loadedImages, prompt, generatorFont, promptFont);

            using var combinedImage = ImageUtils.CreateStandardImage(dimensions.TotalWidth, dimensions.TotalHeight, UIConstants.White);

            combinedImage.Mutate(ctx =>
            {
                // Draw images and subtitles
                DrawImagesAndLabels(ctx, loadedImages, dimensions.MaxImageHeight, generatorFont);

                // Draw prompt at bottom
                DrawPrompt(ctx, prompt, promptFont, dimensions);
            });

            // Save the combined image
            var outputPath = await SaveCombinedImage(combinedImage, prompt, settings);

            // Dispose loaded images
            foreach (var loadedImage in loadedImages)
            {
                loadedImage.Image?.Dispose();
            }

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
                        // Use GetImageBytes() to get the stored bytes
                        var imageBytes = result.GetImageBytes();
                        if (imageBytes != null && imageBytes.Length > 0)
                        {
                            var image = Image.Load<Rgba32>(imageBytes);
                            loadedImages.Add(new LoadedImage
                            {
                                Success = true,
                                Image = image,
                                GeneratorName = result.ImageGenerator.ToString(),
                                Width = image.Width,
                                Height = image.Height
                            });
                        }
                        else
                        {
                            // Success but no bytes somehow
                            Logger.Log($"No image bytes for successful result from {result.ImageGenerator}");
                            loadedImages.Add(CreatePlaceholder(result.ImageGenerator.ToString(), false, placeholderWidth));
                        }
                    }
                    catch (Exception ex)
                    {
                        // GetImageBytes() might throw if bytes weren't set, or image loading failed
                        Logger.Log($"Failed to get/load image from {result.ImageGenerator}: {ex.Message}");
                        loadedImages.Add(CreatePlaceholder(result.ImageGenerator.ToString(), false, placeholderWidth));
                    }
                }
                else
                {
                    // Failed result - show placeholder with error
                    var errorMsg = !string.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage
                        : result.GenericImageErrorType.ToString();
                    Logger.Log($"Result failed for {result.ImageGenerator}: {errorMsg}");
                    loadedImages.Add(CreatePlaceholder(result.ImageGenerator.ToString(), false, placeholderWidth));
                }
            }

            return loadedImages.OrderBy(el => el.GeneratorName);
        }

        /// when an image fails to generate we add in a fixed-width holder in any combined image, to show what happened and be able to
        /// at least see the filtering rules etc.
        private static LoadedImage CreatePlaceholder(string generatorName, bool success, int placeholderWidth)
        {
            return new LoadedImage
            {
                Success = success,
                Image = null,
                GeneratorName = generatorName,
                Width = placeholderWidth,
                Height = placeholderWidth
            };
        }

        private static ImageDimensions CalculateDimensions(
            IEnumerable<LoadedImage> loadedImages,
            string prompt,
            Font subtitleFont,
            Font promptFont)
        {
            int totalWidth = loadedImages.Sum(img => img.Width);
            int maxImageHeight = loadedImages.Where(img => img.Success).Any()
                ? loadedImages.Where(img => img.Success).Max(img => img.Height)
                : 300; // Default placeholder size

            // Calculate text heights
            var subtitleHeight = MeasureMaxHeight(loadedImages.Select(img => GetStatusText(img)), subtitleFont);

            // Calculate prompt height with wrapping support
            var wrappingWidth = totalWidth - (UIConstants.Padding * 4);
            var promptHeight = ImageUtils.MeasureTextHeight(prompt, promptFont, UIConstants.LineSpacing, wrappingWidth);

            // Add extra padding for better spacing around the prompt
            var extraPadding = UIConstants.Padding * 2;

            return new ImageDimensions
            {
                TotalWidth = totalWidth,
                MaxImageHeight = maxImageHeight,
                SubtitleHeight = subtitleHeight + UIConstants.Padding,
                PromptHeight = promptHeight + extraPadding + UIConstants.Padding,
                TotalHeight = maxImageHeight + subtitleHeight + promptHeight + extraPadding + (UIConstants.Padding * 3)
            };
        }

        private static int MeasureMaxHeight(IEnumerable<string> texts, Font font)
        {
            int maxHeight = 0;

            foreach (var text in texts)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    var height = ImageUtils.MeasureTextHeight(text, font, UIConstants.LineSpacing);
                    maxHeight = Math.Max(maxHeight, height);
                }
            }

            return maxHeight;
        }

        private static void DrawImagesAndLabels(
            IImageProcessingContext ctx,
            IEnumerable<LoadedImage> loadedImages,
            int maxImageHeight,
            Font subtitleFont)
        {
            int currentX = 0;

            foreach (var loadedImage in loadedImages)
            {
                // Draw image or placeholder
                if (loadedImage.Success && loadedImage.Image != null)
                {
                    ctx.DrawImage(loadedImage.Image, new Point(currentX, 0), 1f);
                }
                else
                {
                    // Draw placeholder rectangle with error text
                    var placeholderRect = new RectangleF(currentX, 0, loadedImage.Width, maxImageHeight);
                    ctx.DrawPlaceholder(placeholderRect, "Failed", subtitleFont);
                }

                // Draw subtitle
                var statusText = GetStatusText(loadedImage);
                var statusColor = loadedImage.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;

                var subtitleOptions = FontUtils.CreateTextOptions(subtitleFont,
                    HorizontalAlignment.Center, VerticalAlignment.Top, LabelTotalLineSpacing);
                subtitleOptions.Origin = new PointF(currentX + loadedImage.Width / 2f, maxImageHeight + UIConstants.Padding);

                ctx.DrawTextStandard(subtitleOptions, statusText, statusColor);

                currentX += loadedImage.Width;
            }
        }

        private static void DrawPrompt(
            IImageProcessingContext ctx,
            string prompt,
            Font promptFont,
            ImageDimensions dimensions)
        {
            var promptAreaTop = dimensions.MaxImageHeight + dimensions.SubtitleHeight + UIConstants.Padding;
            var promptAreaHeight = dimensions.PromptHeight;

            // Add extra padding above the prompt area for better spacing
            var extraPadding = UIConstants.Padding * 2;
            var promptY = promptAreaTop + extraPadding + (promptAreaHeight - extraPadding) / 2f;

            var promptOptions = FontUtils.CreateTextOptions(promptFont, HorizontalAlignment.Left, VerticalAlignment.Center, LabelTotalLineSpacing);
            promptOptions.Origin = new PointF(UIConstants.Padding * 2, promptY);
            promptOptions.WrappingLength = dimensions.TotalWidth - (UIConstants.Padding * 4);

            ctx.DrawTextStandard(promptOptions, prompt, UIConstants.Black);
        }

        private static async Task<string> SaveCombinedImage(
            Image<Rgba32> image,
            string prompt,
            Settings settings)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder, "Combined");
            Directory.CreateDirectory(baseFolder);

            var truncatedPrompt = FilenameGenerator.TruncatePrompt(prompt, 50);
            var baseFilename = $"combined_{truncatedPrompt}_{DateTime.Now:HHmmss}";
            var safeFilename = baseFilename;
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

        private static string GetStatusText(LoadedImage image)
        {
            return image.GeneratorName;
        }

        // Helper classes for image combining
        private class LoadedImage
        {
            public bool Success { get; set; }
            public Image<Rgba32> Image { get; set; }
            public string GeneratorName { get; set; }
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
