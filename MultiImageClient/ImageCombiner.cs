using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace MultiImageClient
{
    public static class ImageCombiner
    {
        private const int PLACEHOLDER_WIDTH = 300;
        private const int PADDING = 10;
        private const int GENERATOR_NAME_FONT_SIZE = 40;
        private const int PROMPT_FONT_SIZE = 80;
        private const float LINE_SPACING = 1.15f;

        public static async Task<string> CombineImagesHorizontallyAsync(
            IEnumerable<TaskProcessResult> results,
            string prompt,
            Settings settings)
        {
            // Prepare fonts using shared utilities
            var generatorFont = FontUtils.CreateFont(GENERATOR_NAME_FONT_SIZE, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(PROMPT_FONT_SIZE, FontStyle.Regular);

            // Load all images and calculate dimensions
            var loadedImages = LoadImages(results);
            var dimensions = CalculateCombinedDimensions(loadedImages, prompt, generatorFont, promptFont);

            // Create combined image with standard settings
            using var combinedImage = ImageUtils.CreateStandardImage(dimensions.TotalWidth, dimensions.TotalHeight, Color.White);

            combinedImage.Mutate(ctx =>
            {
                // Draw images and subtitles
                DrawImagesWithSubtitles(ctx, loadedImages, dimensions.MaxImageHeight, generatorFont);

                // Draw prompt at bottom
                DrawPromptText(ctx, prompt, promptFont, dimensions);
            });

            // Save the combined image
            var outputPath = await SaveCombinedImageAsync(combinedImage, prompt, settings);

            // Dispose loaded images
            foreach (var loadedImage in loadedImages)
            {
                loadedImage.Image?.Dispose();
            }

            return outputPath;
        }

        private static IEnumerable<LoadedImage> LoadImages(IEnumerable<TaskProcessResult> results)
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
                            loadedImages.Add(CreatePlaceholderImage(result.ImageGenerator.ToString(), false));
                        }
                    }
                    catch (Exception ex)
                    {
                        // GetImageBytes() might throw if bytes weren't set, or image loading failed
                        Logger.Log($"Failed to get/load image from {result.ImageGenerator}: {ex.Message}");
                        loadedImages.Add(CreatePlaceholderImage(result.ImageGenerator.ToString(), false));
                    }
                }
                else
                {
                    // Failed result - show placeholder with error
                    var errorMsg = !string.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage
                        : result.GenericImageErrorType.ToString();
                    Logger.Log($"Result failed for {result.ImageGenerator}: {errorMsg}");
                    loadedImages.Add(CreatePlaceholderImage(result.ImageGenerator.ToString(), false));
                }
            }

            return loadedImages.OrderBy(el=>el.GeneratorName);
        }

        private static LoadedImage CreatePlaceholderImage(string generatorName, bool success)
        {
            return new LoadedImage
            {
                Success = success,
                Image = null,
                GeneratorName = generatorName,
                Width = PLACEHOLDER_WIDTH,
                Height = PLACEHOLDER_WIDTH
            };
        }

        private static ImageDimensions CalculateCombinedDimensions(
            IEnumerable<LoadedImage> loadedImages,
            string prompt,
            Font subtitleFont,
            Font promptFont)
        {
            int totalWidth = loadedImages.Sum(img => img.Width);
            int maxImageHeight = loadedImages.Where(img => img.Success).Any()
                ? loadedImages.Where(img => img.Success).Max(img => img.Height)
                : PLACEHOLDER_WIDTH;

            // Calculate text heights
            var subtitleHeight = MeasureTextHeight(loadedImages.Select(img => GetStatusText(img)), subtitleFont);
            var promptHeight = MeasureTextHeight(new[] { prompt }, promptFont);

            return new ImageDimensions
            {
                TotalWidth = totalWidth,
                MaxImageHeight = maxImageHeight,
                SubtitleHeight = subtitleHeight + PADDING,
                PromptHeight = promptHeight + PADDING,
                TotalHeight = maxImageHeight + subtitleHeight + promptHeight + (PADDING * 3)
            };
        }

        private static int MeasureTextHeight(IEnumerable<string> texts, Font font)
        {
            int maxHeight = 0;

            foreach (var text in texts)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    var height = ImageUtils.MeasureTextHeight(text, font, LINE_SPACING);
                    maxHeight = Math.Max(maxHeight, height);
                }
            }

            return maxHeight;
        }

        private static void DrawImagesWithSubtitles(
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
                    // Draw placeholder rectangle
                    var placeholderRect = new RectangleF(currentX, 0, loadedImage.Width, maxImageHeight);
                    ctx.Fill(Color.FromRgb(240, 240, 240), placeholderRect);
                    ctx.Draw(Color.FromRgb(200, 200, 200), 2f, placeholderRect);

                    // Draw error text in center
                    var errorTextOptions = FontUtils.CreateTextOptions(subtitleFont, 
                        HorizontalAlignment.Center, VerticalAlignment.Center);
                    errorTextOptions.Origin = new PointF(currentX + loadedImage.Width / 2f, maxImageHeight / 2f);
                    ctx.DrawTextStandard(errorTextOptions, "Failed", Color.FromRgb(180, 0, 0));
                }

                // Draw subtitle
                var statusText = GetStatusText(loadedImage);
                var statusColor = loadedImage.Success ? Color.FromRgb(0, 120, 0) : Color.FromRgb(180, 0, 0);

                var subtitleOptions = FontUtils.CreateTextOptions(subtitleFont, 
                    HorizontalAlignment.Center, VerticalAlignment.Top);
                subtitleOptions.Origin = new PointF(currentX + loadedImage.Width / 2f, maxImageHeight + PADDING);

                ctx.DrawTextStandard(subtitleOptions, statusText, statusColor);

                currentX += loadedImage.Width;
            }
        }

        private static void DrawPromptText(
            IImageProcessingContext ctx,
            string prompt,
            Font promptFont,
            ImageDimensions dimensions)
        {
            var promptY = dimensions.MaxImageHeight + dimensions.SubtitleHeight + PADDING;

            var promptOptions = FontUtils.CreateTextOptions(promptFont, 
                HorizontalAlignment.Center, VerticalAlignment.Top);
            promptOptions.Origin = new PointF(dimensions.TotalWidth / 2f, promptY);
            promptOptions.WrappingLength = dimensions.TotalWidth - (PADDING * 4);

            ctx.DrawTextStandard(promptOptions, prompt, Color.Black);
        }

        private static async Task<string> SaveCombinedImageAsync(
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
            return image.Success
                ? $"{image.GeneratorName}"
                : $"{image.GeneratorName}";
        }

        // Helper classes
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