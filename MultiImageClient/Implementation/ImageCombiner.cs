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



using static MultiImageClient.ImageSaving;
using GenerativeAI.Types.RagEngine;

namespace MultiImageClient
{
    public static class ImageCombiner
    {
        private const int CombinedImageGeneratorFontSize = 24;
        private const int CombinedImagePromptFontSize = 32;
        private const int GeneratedImageLabelFontSize = 18;
        private const float LabelLineHeightMultiplier = 1.5f;
        private const int PlaceholderWidth = 300;
        private const int LabelFontSize = 12;


        private static IEnumerable<LoadedImage> LoadResultImages(IEnumerable<TaskProcessResult> results)
        {
            var loadedImages = new List<LoadedImage>();

            foreach (var result in results.Where(el => el.IsSuccess))
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
                                Success = true,
                                Result = result.IsSuccess ? (result.ImageGeneratorDescription ?? "successX") : ($"{result.ImageGeneratorDescription} - {result.ErrorMessage}" ?? "failedX"),
                                Image = image,
                                Width = image.Width,
                                Height = image.Height
                            });
                        }
                        else
                        {
                            // Success but no bytes somehow
                            Logger.Log($"No image bytes for successful result from {result.ImageGenerator}");
                            loadedImages.Add(GetPlaceholder(result));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // GetImageBytes() might throw if bytes weren't set, or image loading failed
                    Logger.Log($"Failed to get/load image from {result.ImageGenerator}: {ex.Message}");
                    loadedImages.Add(GetPlaceholder(result));
                }
            }

            // Failed result - show placeholder with error
            foreach (var result in results.Where(el => !el.IsSuccess))
            {

                loadedImages.Add(GetPlaceholder(result));
            }

            return loadedImages.OrderBy(el => el.Result);
        }

        private static Image<Rgba32> RenderHorizontalLayout(IEnumerable<LoadedImage> loadedImages, Font generatorFont)
        {
            int totalWidth = loadedImages.Sum(img => img.Width);
            totalWidth = Math.Max(totalWidth, PlaceholderWidth);

            int maxImageHeight = loadedImages.Select(i => i.Height)
                                             .DefaultIfEmpty(PlaceholderWidth)
                                             .Max();

            int subtitleHeight = MeasureImageSubdescriptionHeight(loadedImages, generatorFont);
            int subtitleBlockHeight = Math.Max(UIConstants.Padding * 2, subtitleHeight + (UIConstants.Padding * 2));
            int totalHeight = maxImageHeight + subtitleBlockHeight;

            var layoutImage = ImageUtils.CreateStandardImage(totalWidth, totalHeight, UIConstants.White);

            layoutImage.Mutate(ctx =>
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
                        var errorText = li.Result;
                        ctx.DrawErrorPlaceholder(rect, errorText, generatorFont);
                    }

                    labelOpts.Origin = new PointF(currentX + li.Width / 2f, maxImageHeight + UIConstants.Padding);

                    var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                    var labelText = li.Result;
                    ctx.DrawTextStandard(labelOpts, labelText, labelColor);

                    currentX += li.Width;
                }
            });

            return layoutImage;
        }



        // This is a square image where the top images are all the outputs of the original 1 prompt => many render attempts of it.
        private static Image<Rgba32> RenderSquareLayout(IEnumerable<LoadedImage> loadedImages, Font generatorFont)
        {
            int totalImages = loadedImages.Count();

            if (totalImages == 0)
            {
                return RenderEmptyLayout();
            }

            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(totalImages)));
            int rows = (int)Math.Ceiling(totalImages / (double)columns);

            var columnWidths = new int[columns];
            var rowHeights = new int[rows];

            var index = 0;
            foreach (var li in loadedImages)
            {
                int column = index % columns;
                int row = index / columns;

                columnWidths[column] = Math.Max(columnWidths[column], li.Width);
                rowHeights[row] = Math.Max(rowHeights[row], li.Height);
                index++;
            }

            int layoutWidth = Math.Max(columnWidths.Sum(), PlaceholderWidth);

            var columnOffsets = new int[columns];
            int xAccumulator = 0;
            for (int col = 0; col < columns; col++)
            {
                columnOffsets[col] = xAccumulator;
                xAccumulator += columnWidths[col];
            }

            // every image has its own description section
            int imageSubdescriptionHeight = MeasureImageSubdescriptionHeight(loadedImages, generatorFont);
            int subdescripitonHeight = Math.Max(UIConstants.Padding * 2, imageSubdescriptionHeight + (UIConstants.Padding * 2));
            int layoutHeight = rowHeights.Sum() + (subdescripitonHeight * rows);

            var layoutImage = ImageUtils.CreateStandardImage(layoutWidth, layoutHeight, UIConstants.White);

            layoutImage.Mutate(ctx =>
            {
                int currentY = 0;
                var labelOpts = FontUtils.CreateTextOptions(generatorFont, HorizontalAlignment.Center, VerticalAlignment.Top, UIConstants.LineSpacing);
                int imageIndex = 0;

                for (int row = 0; row < rows; row++)
                {
                    int rowHeight = rowHeights[row];
                    int labelY = currentY + rowHeight + UIConstants.Padding;

                    for (int col = 0; col < columns; col++)
                    {
                        if (imageIndex >= totalImages)
                        {
                            break;
                        }

                        var li = loadedImages.ToList()[imageIndex];
                        int columnWidth = columnWidths[col];
                        int columnX = columnOffsets[col];

                        int imageX = columnX + Math.Max(0, (columnWidth - li.Width) / 2);
                        int imageY = currentY;

                        if (li.Success && li.Image != null)
                        {
                            ctx.DrawImage(li.Image, new Point(imageX, imageY), 1f);
                        }
                        else
                        {
                            var rect = new RectangleF(columnX, imageY, Math.Max(columnWidth, 1), Math.Max(rowHeight, 1));
                            var errorText = li.Result;
                            ctx.DrawErrorPlaceholder(rect, errorText, generatorFont);
                        }

                        labelOpts.Origin = new PointF(columnX + Math.Max(columnWidth, 1) / 2f, labelY);

                        var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                        var labelText = li.Result;
                        ctx.DrawTextStandard(labelOpts, labelText, labelColor);

                        imageIndex++;
                    }

                    currentY += rowHeight + subdescripitonHeight;
                }
            });

            return layoutImage;
        }

        public static async Task<string> CreateBatchLayoutImageHorizontalAsync(IEnumerable<TaskProcessResult> results, string prompt, Settings settings)
        {
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            var loadedImages = LoadResultImages(results);

            using var layoutImage = RenderHorizontalLayout(loadedImages, generatorFont);
            var totalWidth = layoutImage.Width;

            using var promptPanel = RenderPromptPanel(totalWidth, prompt, promptFont);

            int totalHeight = layoutImage.Height + promptPanel.Height;
            var combinedImage = ImageUtils.CreateStandardImage(layoutImage.Width, totalHeight, UIConstants.White);

            combinedImage.Mutate(ctx =>
            {
                ctx.DrawImage(layoutImage, new Point(0, 0), 1f);
                ctx.DrawImage(promptPanel, new Point(0, layoutImage.Height), 1f);
            });


            var outputPath = await SaveCombinedImageToDisk(combinedImage, prompt, settings);

            foreach (var li in loadedImages)
            {
                li.Image?.Dispose();
            }

            OpenImageWithDefaultApplication(outputPath);

            return outputPath;
        }

        public static async Task<string> CreateBatchLayoutImageSquareAsync(IEnumerable<TaskProcessResult> results, string prompt, Settings settings)
        {
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            var loadedImages = LoadResultImages(results);

            using var layoutImage = RenderHorizontalLayout(loadedImages, generatorFont);
            using var promptPanel = RenderPromptPanel(layoutImage.Width, prompt, promptFont);

            int totalHeight = layoutImage.Height + promptPanel.Height;
            var combinedImage = ImageUtils.CreateStandardImage(layoutImage.Width, totalHeight, UIConstants.White);

            combinedImage.Mutate(ctx =>
            {
                ctx.DrawImage(layoutImage, new Point(0, 0), 1f);
                ctx.DrawImage(promptPanel, new Point(0, layoutImage.Height), 1f);
            });


            var outputPath = await SaveCombinedImageToDisk(combinedImage, prompt, settings);

            foreach (var li in loadedImages)
            {
                li.Image?.Dispose();
            }

            OpenImageWithDefaultApplication(outputPath);

            return outputPath;
        }






        // this is how werender the "roundtrip workflow". In this case, the user provided an image to us.
        // we used an LLM to describe it, then we sent that description to a bunch of images.
        // the output format should be a long horizontal strip. Leftmost should be the image, labelled like "original iamge".
        // then to the right of that,  how it was described, in descriptionText taking up a big square column (sine this text might be long).
        // then further to the right, in order, just like we did in RenderHorizontalLayout, should be all the output images ortheir error placeholders.
        public async static Task<string> CreateRoundtripLayoutImageAsync(byte[] originalImageBytes, IEnumerable<TaskProcessResult> results, string descriptionText, Settings settings)
        {
            var labelFont = FontUtils.CreateFont(36, FontStyle.Bold); // Large font for labels
            var originalImage = Image.Load<Rgba32>(originalImageBytes);
            if (originalImage == null)
            {
                throw new Exception("no original image.");
            }

            // Resize original image if height > 1024px, keeping proportions
            if (originalImage.Height > 1024)
            {
                var aspectRatio = (float)originalImage.Width / originalImage.Height;
                var newHeight = 1024;
                var newWidth = (int)(newHeight * aspectRatio);

                Logger.Log($"Resizing original image from {originalImage.Width}x{originalImage.Height} to {newWidth}x{newHeight} for combining");
                originalImage.Mutate(x => x.Resize(newWidth, newHeight));
            }

            var maxImageHeight = originalImage.Height;
            var descriptionWidth = 600; // Fixed width for description
            var descriptionHeight = maxImageHeight; // Description takes up the same height as the original image.
            var loadedImages = LoadResultImages(results).ToList();

            // Measure label heights to account for them in total height
            var labelHeight = 50; // Approximate height for 36pt font labels
            
            // Calculate total width and height
            var totalWidth = originalImage.Width + descriptionWidth + loadedImages.Sum(li => li.Width);
            var totalHeight = maxImageHeight + labelHeight + UIConstants.Padding * 2;

            var layoutImage = new Image<Rgba32>(totalWidth, totalHeight);

            layoutImage.Mutate(ctx =>
            {
                ctx.ApplyStandardGraphicsOptions();
                ctx.Fill(UIConstants.White);
                float currentX = 0;

                // 1. Draw Original Image
                ctx.DrawImage(originalImage!, new Point((int)currentX, 0), 1f);
                var labelOptsOriginal = new RichTextOptions(labelFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                labelOptsOriginal.Origin = new PointF(currentX + originalImage!.Width / 2f, maxImageHeight + UIConstants.Padding);
                ctx.DrawTextStandard(labelOptsOriginal, "Original Image", Color.Black);
                currentX += originalImage.Width;

                // 2. Draw Description Text
                // Use binary search to find the optimal font size that fills the available space
                var minFontSize = 8;
                var maxFontSize = 150; // Start with a large maximum
                var optimalFontSize = minFontSize;
                var availableHeight = descriptionHeight - 2 * UIConstants.Padding;
                var wrappingWidth = descriptionWidth - 2 * UIConstants.Padding;
                
                // Binary search for the optimal font size
                while (minFontSize <= maxFontSize)
                {
                    var midFontSize = (minFontSize + maxFontSize) / 2;
                    var testFont = FontUtils.CreateFont(midFontSize, FontStyle.Regular);
                    var testHeight = ImageUtils.MeasureTextHeight(descriptionText, testFont, UIConstants.LineSpacing, wrappingWidth);
                    
                    if (testHeight <= availableHeight)
                    {
                        // Text fits, try a larger size
                        optimalFontSize = midFontSize;
                        minFontSize = midFontSize + 1;
                    }
                    else
                    {
                        // Text doesn't fit, try a smaller size
                        maxFontSize = midFontSize - 1;
                    }
                }
                
                // Create the final font and text options with the optimal size
                var descriptionFont = FontUtils.CreateFont(optimalFontSize, FontStyle.Regular);
                var descriptionTextOptions = new RichTextOptions(descriptionFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    WrappingLength = wrappingWidth,
                    Origin = new PointF(currentX + UIConstants.Padding, UIConstants.Padding)
                };


                ctx.Fill(Color.LightGray, new RectangleF(currentX, 0, descriptionWidth, descriptionHeight)); // Background for text
                ctx.DrawTextStandard(descriptionTextOptions, descriptionText, Color.Black);

                var labelOptsDescription = new RichTextOptions(labelFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                labelOptsDescription.Origin = new PointF(currentX + descriptionWidth / 2f, maxImageHeight + UIConstants.Padding);
                ctx.DrawTextStandard(labelOptsDescription, "Description", Color.Black);
                currentX += descriptionWidth;

                // 3. Draw Loaded Images with their labels
                foreach (var li in loadedImages)
                {
                    // Calculate vertical offset to align bottom of image with maxImageHeight
                    var imageYOffset = maxImageHeight - li.Height;
                    
                    if (li.Image != null)
                    {
                        ctx.DrawImage(li.Image, new Point((int)currentX, imageYOffset), 1);
                    }
                    else
                    {
                        // Draw placeholder for error images
                        ctx.Fill(Color.LightGray, new Rectangle((int)currentX, imageYOffset, li.Width, li.Height));
                        var errorTextOptions = new RichTextOptions(labelFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        errorTextOptions.Origin = new PointF(currentX + li.Width / 2f, imageYOffset + li.Height / 2f);
                        ctx.DrawTextStandard(errorTextOptions, "ERROR", Color.Red);
                    }

                    var labelOpts = new RichTextOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    var labelText = li.Result;
                    // Calculate the vertical position based on maxImageHeight, padding, and actual label text height.
                    labelOpts.Origin = new PointF(currentX + li.Width / 2f, maxImageHeight + UIConstants.Padding); // Start UIConstants.Padding below the image

                    var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                    ctx.DrawTextStandard(labelOpts, labelText, labelColor);

                    currentX += li.Width;
                }


               
            });
            var outputPath = await SaveCombinedImageToDisk(layoutImage, descriptionText, settings);

            foreach (var li in loadedImages)
            {
                li.Image?.Dispose();
            }

            OpenImageWithDefaultApplication(outputPath);

            return outputPath;
        }
        private static Image<Rgba32> RenderPromptPanel(int width, string prompt, Font promptFont)
        {
            width = Math.Max(width, PlaceholderWidth);

            int wrappingWidth = Math.Max(1, width - (UIConstants.Padding * 4));
            int promptHeight = ImageUtils.MeasureTextHeight(prompt, promptFont, UIConstants.LineSpacing, wrappingWidth);
            int topPadding = UIConstants.Padding * 3;
            int bottomPadding = UIConstants.Padding * 2;
            int totalHeight = topPadding + promptHeight + bottomPadding;

            var promptPanel = ImageUtils.CreateStandardImage(width, totalHeight, UIConstants.White);

            promptPanel.Mutate(ctx =>
            {
                var promptOpts = FontUtils.CreateTextOptions(promptFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                promptOpts.Origin = new PointF(UIConstants.Padding * 2, topPadding);
                promptOpts.WrappingLength = wrappingWidth;

                ctx.DrawTextStandard(promptOpts, prompt, UIConstants.Black);
            });

            return promptPanel;
        }

        
        public static void OpenImageWithDefaultApplication(string imagePath)
        {
            if (File.Exists(imagePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error opening image {imagePath}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"Image file not found at {imagePath}");
            }
        }

        private static int MeasureImageSubdescriptionHeight(IEnumerable<LoadedImage> loadedImages, Font generatorFont)
        {
            return loadedImages
                .Select(li => li.Result ?? "missing")
                .Select(label => ImageUtils.MeasureTextHeight(label, generatorFont, UIConstants.LineSpacing))
                .DefaultIfEmpty(0)
                .Max();
        }

        


        public static LoadedImage GetPlaceholder(TaskProcessResult result)
        {
            return new LoadedImage
            {
                Image = null,
                Success = false,
                Result = result.ErrorMessage ?? "ErrorX",
                Width = PlaceholderWidth,
                Height = PlaceholderWidth
            };
        }

        private static Image<Rgba32> RenderEmptyLayout()
        {
            var empty = ImageUtils.CreateStandardImage(PlaceholderWidth, PlaceholderWidth + (UIConstants.Padding * 2), UIConstants.White);

            empty.Mutate(ctx =>
            {
                var font = FontUtils.CreateFont(LabelFontSize, FontStyle.Regular);
                var opts = FontUtils.CreateTextOptions(font, HorizontalAlignment.Center, VerticalAlignment.Center, UIConstants.LineSpacing);
                opts.Origin = new PointF(empty.Width / 2f, empty.Height / 2f);
                ctx.DrawTextStandard(opts, "No images", UIConstants.ErrorRed);
            });

            return empty;
        }


        private static async Task<string> SaveCombinedImageToDisk(Image<Rgba32> image, string prompt, Settings settings)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder, "Combined");
            Directory.CreateDirectory(baseFolder);

            var truncatedPrompt = FilenameGenerator.TruncatePrompt(prompt, 50);
            var baseFilename = $"combined_{truncatedPrompt}_{DateTime.Now:HHmmss}";
            var safeFilename = FilenameGenerator.SanitizeFilename(baseFilename);
            var outputPath = Path.Combine(baseFolder, $"{safeFilename}.png");

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

        public class LoadedImage
        {
            public bool Success { get; set; }
            public string? Result { get; set; }
            public Image<Rgba32>? Image { get; set; }
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
