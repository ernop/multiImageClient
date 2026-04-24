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


using GenerativeAI.Types.RagEngine;

namespace MultiImageClient
{
    // ImageCombiner creates visual comparison and documentation layouts by combining multiple images into a single output.
    //
    // Main functionality:
    // 1. Batch Layout Images: Combines multiple generated images from different image generators into horizontal  square grid layouts,
    //    with labels showing which generator produced each image and the original prompt used.
    //
    // 2. Roundtrip Layout Images: Creates comprehensive visual documentation of the image description ? regeneration workflow.
    //    Shows the full pipeline: original image ? describer instructions ? generated description ? regenerated images.
    //    This layout displays:
    //    - The original input image
    //    - The instructions/prompts given to the describer model
    //    - The description text output by the describer model
    //    - All images regenerated from that description by various image generators
    //    All sections are labeled with the relevant model names (e.g., "Qwen2-VL Description", "InternVL Instructions", etc.)
    //
    // 3. Smart Text Sizing: Automatically calculates optimal font sizes to fit description text and instructions within their panels,
    //    using binary search to maximize readability while ensuring text fits in the available space.
    //
    // 4. Error Handling: Displays error placeholders for failed image generations with descriptive error messages.
    //
    // All combined images are saved to disk in dated folders and automatically opened in the default image viewer.
    public static class ImageCombiner
    {
        private const int CombinedImageGeneratorFontSize = 12;
        private const int CombinedImagePromptFontSize = 16;
        private const int GeneratedImageLabelFontSize = 18;
        private const float LabelLineHeightMultiplier = 1.5f;
        private const int PlaceholderWidth = 300;
        private const int LabelFontSize = 12;

        /// Fraction of the generator-label font size added as extra
        /// descender reservation so letters with descenders (g, p, y)
        /// are never clipped by the band below.
        private const float LabelDescenderReserveFraction = 0.25f;


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
                                Result = result.IsSuccess ? "successX" : result.ErrorMessage ?? "missing error",
                                Generator = result.ImageGeneratorDescription,
                                TimingLabel = FormatTiming(result.CreateTotalMs + result.DownloadTotalMs),
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

            return loadedImages.OrderBy(el => el.Generator);
        }

        private static Image<Rgba32> RenderHorizontalLayout(IEnumerable<LoadedImage> loadedImages, Font generatorFont)
        {
            int totalWidth = loadedImages.Sum(img => img.Width);
            totalWidth = Math.Max(totalWidth, PlaceholderWidth);

            int maxImageHeight = loadedImages.Select(i => i.Height)
                                             .DefaultIfEmpty(PlaceholderWidth)
                                             .Max();

            int subtitleHeight = MeasureImageSubdescriptionHeight(loadedImages, generatorFont);
            int subtitleBlockHeight = Math.Max(UIConstants.Padding * 2, subtitleHeight + UIConstants.Padding * 2);
            int totalHeight = maxImageHeight + subtitleBlockHeight;

            var layoutImage = ImageUtils.CreateStandardImage(totalWidth, totalHeight, UIConstants.White);

            var timingFont = FontUtils.CreateFont(Math.Max(8, generatorFont.Size * 0.75f), FontStyle.Regular);

            layoutImage.Mutate(ctx =>
            {
                int currentX = 0;

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

                    float centerX = currentX + li.Width / 2f;
                    float labelY = maxImageHeight + UIConstants.Padding;

                    var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                    DrawCompositeLabelLine(ctx, li.Generator, li.TimingLabel, generatorFont, timingFont, labelColor, centerX, labelY);

                    currentX += li.Width;
                }
            });

            return layoutImage;
        }



        /// Renders a generator label as a single horizontal line with a clear
        /// typographic hierarchy:
        ///
        ///   [primary font (bold)] gpt-image-2   [secondary font] medium  square  1m 4s
        ///
        /// The first whitespace-delimited token of <paramref name="fullLabel"/>
        /// (typically the model id) stays in the primary font; the rest of
        /// the label plus the optional timing ("1m 4s") collapses into one
        /// secondary-font string drawn to its right. All pieces share a
        /// single bottom pixel (descenders included) so nothing sits visually
        /// lower than anything else. Color comes from <paramref name="color"/>
        /// � no gray text, per AGENTS.md.
        private static void DrawCompositeLabelLine(
            IImageProcessingContext ctx,
            string fullLabel,
            string? timingLabel,
            Font primaryFont,
            Font secondaryFont,
            Color color,
            float centerX,
            float labelY)
        {
            if (string.IsNullOrWhiteSpace(fullLabel) && string.IsNullOrWhiteSpace(timingLabel)) return;

            // Split: first whitespace-delimited token is the primary piece
            // (kept in the big bold font); everything after is secondary.
            // Extra whitespace in the source is normalized to exactly two
            // spaces between pieces in the rendered output.
            var trimmed = (fullLabel ?? string.Empty).Trim();
            string primary;
            var secondaryPieces = new List<string>();
            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace < 0)
            {
                primary = trimmed;
            }
            else
            {
                primary = trimmed.Substring(0, firstSpace);
                var rest = trimmed.Substring(firstSpace + 1);
                secondaryPieces.AddRange(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            if (!string.IsNullOrWhiteSpace(timingLabel))
            {
                secondaryPieces.Add(timingLabel);
            }
            string secondary = string.Join("  ", secondaryPieces);

            // Measure each piece independently so we can lay them out on one
            // shared baseline.
            var primaryMeasureOpts = FontUtils.CreateTextOptions(primaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
            primaryMeasureOpts.Origin = new PointF(0, 0);
            var primaryBounds = string.IsNullOrEmpty(primary)
                ? new FontRectangle(0, 0, 0, 0)
                : TextMeasurer.MeasureBounds(primary, primaryMeasureOpts);

            float gap = secondaryPieces.Count == 0 || string.IsNullOrEmpty(primary)
                ? 0f
                : Math.Max(8f, primaryFont.Size * 0.6f);

            float secondaryWidth = 0f;
            FontRectangle secondaryBounds = new FontRectangle(0, 0, 0, 0);
            if (!string.IsNullOrEmpty(secondary))
            {
                var secondaryMeasureOpts = FontUtils.CreateTextOptions(secondaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                secondaryMeasureOpts.Origin = new PointF(0, 0);
                secondaryBounds = TextMeasurer.MeasureBounds(secondary, secondaryMeasureOpts);
                secondaryWidth = secondaryBounds.Width;
            }

            float totalWidth = primaryBounds.Width + gap + secondaryWidth;
            float startX = centerX - totalWidth / 2f;

            // Draw primary first at (startX, labelY) with Top alignment so
            // labelY is the top of its ascenders. Then use its actual bottom
            // pixel as the shared baseline for the secondary text.
            float sharedBottom = labelY + primaryBounds.Height;
            if (!string.IsNullOrEmpty(primary))
            {
                var primaryDrawOpts = FontUtils.CreateTextOptions(primaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                primaryDrawOpts.Origin = new PointF(startX, labelY);
                ctx.DrawTextStandard(primaryDrawOpts, primary, color);
                var drawnBounds = TextMeasurer.MeasureBounds(primary, primaryDrawOpts);
                sharedBottom = drawnBounds.Bottom;
            }

            if (!string.IsNullOrEmpty(secondary))
            {
                var secondaryDrawOpts = FontUtils.CreateTextOptions(secondaryFont, HorizontalAlignment.Left, VerticalAlignment.Bottom, UIConstants.LineSpacing);
                secondaryDrawOpts.Origin = new PointF(startX + primaryBounds.Width + gap, sharedBottom);
                ctx.DrawTextStandard(secondaryDrawOpts, secondary, color);
            }
        }

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
            int subdescripitonHeight = Math.Max(UIConstants.Padding * 2, imageSubdescriptionHeight + UIConstants.Padding * 2);
            int layoutHeight = rowHeights.Sum() + subdescripitonHeight * rows;

            var layoutImage = ImageUtils.CreateStandardImage(layoutWidth, layoutHeight, UIConstants.White);

            var timingFont = FontUtils.CreateFont(Math.Max(8, generatorFont.Size * 0.75f), FontStyle.Regular);

            layoutImage.Mutate(ctx =>
            {
                int currentY = 0;
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

                        float centerX = columnX + Math.Max(columnWidth, 1) / 2f;
                        var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                        DrawCompositeLabelLine(ctx, li.Generator, li.TimingLabel, generatorFont, timingFont, labelColor, centerX, labelY);

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

        // openWhenDone controls whether the combined grid image is popped
        // open in the system default viewer after it's written. Default true
        // preserves the batch/round-trip workflow behavior. Callers that want
        // headless behavior (e.g. the REPL, where viewer popups would spam
        // the desktop) pass false.
        public static async Task<string> CreateBatchLayoutImageSquareAsync(IEnumerable<TaskProcessResult> results, string prompt, Settings settings, bool openWhenDone = true)
        {
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            var loadedImages = LoadResultImages(results);

            using var layoutImage = RenderSquareLayout(loadedImages, generatorFont);
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

            if (openWhenDone)
            {
                OpenImageWithDefaultApplication(outputPath);
            }

            return outputPath;
        }


        // this is how werender the "roundtrip workflow". In this case, the user provided an image to us.
        // we used an LLM to describe it, then we sent that description to a bunch of images.
        // the output format should be a long horizontal strip. Leftmost should be the image, labelled like "original iamge".
        // then to the right of that,  how it was described, in descriptionText taking up a big square column (sine this text might be long).
        // then further to the right, in order, just like we did in RenderHorizontalLayout, should be all the output images ortheir error placeholders.
        public async static Task<string> CreateRoundtripLayoutImageAsync(byte[] originalImageBytes, IEnumerable<TaskProcessResult> results, string descriptionText, string descriptionInstructions, string describerModelName, Settings settings)
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
            var descriptionWidth = 1024; // Fixed width for description
            var descriptionHeight = maxImageHeight; // Description takes up the same height as the original image.
            var loadedImages = LoadResultImages(results).ToList();

            // Actually measure how much space each label needs
            var tempLabelFont = FontUtils.CreateFont(36, FontStyle.Bold);
            
            // Measure each label text height, accounting for wrapping
            var labelHeights = new List<int>();
            foreach (var li in loadedImages)
            {
                var labelText = li.Result;
                if (!string.IsNullOrEmpty(labelText))
                {
                    // Handle newlines properly - they increase height
                    var wrappingWidth = li.Width - 10; // Account for padding
                    var labelHeight = ImageUtils.MeasureTextHeight(labelText, tempLabelFont, UIConstants.LineSpacing, wrappingWidth);
                    labelHeights.Add(labelHeight);
                }
                else
                {
                    labelHeights.Add(0);
                }
            }
            
            // Also measure the "Original Image" and "Description" labels
            var originalLabelHeight = ImageUtils.MeasureTextHeight("Original Image", tempLabelFont, UIConstants.LineSpacing, originalImage.Width - 10);
            var descriptionLabelHeight = ImageUtils.MeasureTextHeight("Description", tempLabelFont, UIConstants.LineSpacing, descriptionWidth - 10);
            
            // Find the maximum label height needed
            var maxLabelHeight = Math.Max(originalLabelHeight, descriptionLabelHeight);
            if (labelHeights.Any())
            {
                maxLabelHeight = Math.Max(maxLabelHeight, labelHeights.Max());
            }
            
            Logger.Log($"Max label height needed: {maxLabelHeight}px (original: {originalLabelHeight}, description: {descriptionLabelHeight}, generated images: {string.Join(",", labelHeights)})");
            
                // Calculate total width and height
                var instructionsWidth = 1024; // Same width as description
                var totalWidth = originalImage.Width + instructionsWidth + descriptionWidth + loadedImages.Sum(li => li.Width);
                var totalHeight = maxImageHeight + maxLabelHeight + UIConstants.Padding * 2;

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
                labelOptsOriginal.Dpi = UIConstants.TextDpi; // Ensure same DPI as measurement!
                labelOptsOriginal.Origin = new PointF(currentX + originalImage!.Width / 2f, maxImageHeight + UIConstants.Padding);
                ctx.DrawTextStandard(labelOptsOriginal, "Original Image", Color.Black);
                currentX += originalImage.Width;

                // 2a. Draw describer instructions
                var instructionsWidth = 1024; // Same width as description
                var instructionsHeight = maxImageHeight; // Same height as description
                
                if (!string.IsNullOrEmpty(descriptionInstructions))
                {
                    // Calculate optimal font size for instructions text
                    var instrTargetUtilization = 0.95;
                    var instrAvailableHeight = instructionsHeight - 2 * UIConstants.Padding; 
                    var instrWrappingWidth = instructionsWidth - 2 * UIConstants.Padding;
                    var instrTargetHeight = instrAvailableHeight * instrTargetUtilization;
                    
                    var instrNewlineCount = descriptionInstructions.Count(c => c == '\n');
                    
                    var instrMinFontSize = 12;
                    var instrMaxFontSize = 60;
                    var instrOptimalFontSize = instrMinFontSize;
                    
                    
                    // Binary search for optimal font size
                    while (instrMinFontSize <= instrMaxFontSize)
                    {
                        var instrMidFontSize = (instrMinFontSize + instrMaxFontSize) / 2;
                        var instrTestFont = FontUtils.CreateFont(instrMidFontSize, FontStyle.Regular);
                        var instrTestHeight = ImageUtils.MeasureTextHeight(descriptionInstructions, instrTestFont, UIConstants.LineSpacing, instrWrappingWidth);
                        
                        Logger.Log($"Describer instructions font {instrMidFontSize}pt: height={instrTestHeight}, target={instrTargetHeight}");
                        
                        if (instrTestHeight <= instrTargetHeight)
                        {
                            instrOptimalFontSize = instrMidFontSize;
                            instrMinFontSize = instrMidFontSize + 1;
                        }
                        else
                        {
                            instrMaxFontSize = instrMidFontSize - 1;
                        }
                    }
                    
                    if (instrOptimalFontSize < 14)
                    {
                        instrOptimalFontSize = 14; // Minimum readable size
                        Logger.Log($"Describer instructions font size was too small, forcing minimum of 14pt");
                    }
                    
                    Logger.Log($"FINAL DESCRIBER INSTRUCTIONS FONT SIZE: {instrOptimalFontSize}pt for {descriptionInstructions.Length} chars in {instrWrappingWidth}x{instrTargetHeight}px");
                    
                    // Create font and text options for describer instructions
                    var instructionsFont = FontUtils.CreateFont(instrOptimalFontSize, FontStyle.Regular);
                    var instructionsTextOptions = new RichTextOptions(instructionsFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        WrappingLength = instrWrappingWidth,
                        Origin = new PointF(currentX + UIConstants.Padding, UIConstants.Padding),
                        Dpi = UIConstants.TextDpi
                    };
                    
                    // Draw background and text for describer instructions
                    ctx.Fill(Color.LightBlue, new RectangleF(currentX, 0, instructionsWidth, instructionsHeight));
                    ctx.DrawTextStandard(instructionsTextOptions, descriptionInstructions, Color.Black);
                    
                    // Draw label for describer instructions
                    var instructionsLabelOpts = new RichTextOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    instructionsLabelOpts.Dpi = UIConstants.TextDpi;
                    instructionsLabelOpts.Origin = new PointF(currentX + instructionsWidth / 2f, maxImageHeight + UIConstants.Padding);
                    ctx.DrawTextStandard(instructionsLabelOpts, $"{describerModelName} Instructions", Color.Black);
                }
                else
                {
                    // Draw placeholder if no instructions
                    ctx.Fill(Color.LightGray, new RectangleF(currentX, 0, instructionsWidth, instructionsHeight));
                    var noInstructionsFont = FontUtils.CreateFont(18, FontStyle.Regular);
                    var noInstructionsTextOptions = new RichTextOptions(noInstructionsFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Dpi = UIConstants.TextDpi
                    };
                    noInstructionsTextOptions.Origin = new PointF(currentX + instructionsWidth / 2f, instructionsHeight / 2f);
                    ctx.DrawTextStandard(noInstructionsTextOptions, "No Instructions", Color.Gray);
                    
                    // Draw label even when no instructions
                    var instructionsLabelOpts = new RichTextOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    instructionsLabelOpts.Dpi = UIConstants.TextDpi;
                    instructionsLabelOpts.Origin = new PointF(currentX + instructionsWidth / 2f, instructionsHeight + UIConstants.Padding);
                    ctx.DrawTextStandard(instructionsLabelOpts, $"{describerModelName} Instructions", Color.Black);
                }
                currentX += instructionsWidth;

                // 2b. Draw Description Text
                var targetUtilization = 0.95; 
                var availableHeight = descriptionHeight - 2 * UIConstants.Padding;
                var wrappingWidth = descriptionWidth - 2 * UIConstants.Padding;
                var targetHeight = availableHeight * targetUtilization;
                
                // Count newlines in the text - they affect height calculations!
                var newlineCount = descriptionText.Count(c => c == '\n');
                
                Logger.Log($"Description panel: {descriptionWidth}x{descriptionHeight}");
                Logger.Log($"Target to fill: {wrappingWidth}x{targetHeight} (85% of available)");
                Logger.Log($"Text: {descriptionText.Length} chars with {newlineCount} newlines");

                // This is all so stupid. Why can't you just call MeasureText on the text, including newlines, plus the rectangle size? I really don't get it.
                // This whole guessing/semi-hardcoding guidance is wrong.

                // Reasonable font size bounds for long text
                var minFontSize = 12;
                var maxFontSize = 60; // Much more reasonable maximum for long text
                var optimalFontSize = minFontSize;
                
                Logger.Log($"Adjusted max font size to {maxFontSize} based on text length");

                
                
                // Binary search for the optimal size
                while (minFontSize <= maxFontSize)
                {
                    var midFontSize = (minFontSize + maxFontSize) / 2;
                    var testFont = FontUtils.CreateFont(midFontSize, FontStyle.Regular);
                    var testHeight = ImageUtils.MeasureTextHeight(descriptionText, testFont, UIConstants.LineSpacing, wrappingWidth);
                    
                    Logger.Log($"Font {midFontSize}pt: height={testHeight}, target={targetHeight}");
                    
                    if (testHeight <= targetHeight)
                    {
                        // Text fits, try larger
                        optimalFontSize = midFontSize;
                        minFontSize = midFontSize + 1;
                    }
                    else
                    {
                        // Text doesn't fit, try smaller
                        maxFontSize = midFontSize - 1;
                    }
                }
                
                // Make sure we found something reasonable
                if (optimalFontSize < 14)
                {
                    optimalFontSize = 14; // Minimum readable size
                    Logger.Log($"Font size was too small, forcing minimum of 14pt");
                }
                
                Logger.Log($"FINAL FONT SIZE: {optimalFontSize}pt for {descriptionText.Length} chars in {wrappingWidth}x{targetHeight}px");
                
                // Create the final font and text options with the optimal size
                var descriptionFont = FontUtils.CreateFont(optimalFontSize, FontStyle.Regular);
                var descriptionTextOptions = new RichTextOptions(descriptionFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    WrappingLength = wrappingWidth,
                    Origin = new PointF(currentX + UIConstants.Padding, UIConstants.Padding),
                    Dpi = UIConstants.TextDpi  // Ensure same DPI as measurement!
                };


                ctx.Fill(Color.LightGray, new RectangleF(currentX, 0, descriptionWidth, descriptionHeight)); // Background for text
                ctx.DrawTextStandard(descriptionTextOptions, descriptionText, Color.Black);

                var labelOptsDescription = new RichTextOptions(labelFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                labelOptsDescription.Dpi = UIConstants.TextDpi; // Ensure same DPI as measurement!
                labelOptsDescription.Origin = new PointF(currentX + descriptionWidth / 2f, maxImageHeight + UIConstants.Padding);
                ctx.DrawTextStandard(labelOptsDescription, $"{describerModelName} Description", Color.Black);
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
                            VerticalAlignment = VerticalAlignment.Center,
                            Dpi = UIConstants.TextDpi // Ensure same DPI as measurement!
                        };
                        errorTextOptions.Origin = new PointF(currentX + li.Width / 2f, imageYOffset + li.Height / 2f);
                        ctx.DrawTextStandard(errorTextOptions, "ERROR", Color.Red);
                    }

                    var labelText = li.Result ?? "";
                    
                    var labelOpts = new RichTextOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        WrappingLength = li.Width - 10, // Allow wrapping within image bounds
                        Dpi = UIConstants.TextDpi  // Ensure same DPI as measurement!
                    };
                    labelOpts.Origin = new PointF(currentX + li.Width / 2f, maxImageHeight + UIConstants.Padding);

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

            int wrappingWidth = Math.Max(1, width - UIConstants.Padding * 4);
            int promptHeight = ImageUtils.MeasureTextHeight(prompt, promptFont, UIConstants.LineSpacing, wrappingWidth);
            // Halved from the previous Padding*3 / Padding*2 so the
            // prompt sits closer to the top of its band.
            int topPadding = (int)Math.Ceiling(UIConstants.Padding * 1.5f);
            int bottomPadding = UIConstants.Padding;
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
            int measured = loadedImages
                .Select(li => $"{li.Result} {li.Generator}" ?? "missing")
                .Select(label => ImageUtils.MeasureTextHeight(label, generatorFont, UIConstants.LineSpacing))
                .DefaultIfEmpty(0)
                .Max();
            // Add descender reserve: TextMeasurer.MeasureBounds can
            // under-report for fonts at this size, clipping the tails
            // of g/p/y/j. See AGENTS.md > Visual & Typography Policy.
            int descenderReserve = (int)Math.Ceiling(generatorFont.Size * LabelDescenderReserveFraction);
            return measured + descenderReserve;
        }

        


        public static LoadedImage GetPlaceholder(TaskProcessResult result)
        {
            // Try to match what the real image WOULD have been, so the
            // placeholder occupies the same column footprint and the prompt
            // panel underneath doesn't get squeezed to a narrow 300-px strip.
            // Generators (currently just gpt-image-2) stash their chosen size
            // into PromptDetails.RuntimeMeta["size"] before the API call, so
            // it's available even on failure results. Fall back to a reasonable
            // 1024x1024 if no size metadata exists (older generators, unknown
            // cases), which is still far nicer than the old 300-px square.
            int width = 1024;
            int height = 1024;
            var meta = result.PromptDetails?.RuntimeMeta;
            if (meta != null && meta.TryGetValue("size", out var sizeStr) && !string.IsNullOrWhiteSpace(sizeStr))
            {
                if (TryParseSize(sizeStr, out var w, out var h))
                {
                    width = w;
                    height = h;
                }
            }

            return new LoadedImage
            {
                Image = null,
                Success = false,
                Result = result.ErrorMessage ?? "ErrorX",
                Generator = result.ImageGeneratorDescription,
                TimingLabel = FormatTiming(result.CreateTotalMs + result.DownloadTotalMs),
                Width = width,
                Height = height
            };
        }

        // "1024x1024" -> (1024, 1024). Returns false for "auto" or any
        // unparseable string so the caller can fall back to a default.
        private static bool TryParseSize(string s, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split('x');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)) return false;
            if (width <= 0 || height <= 0) return false;
            return true;
        }

        /// Formats a total-elapsed-ms value into a short "%dm %ds"
        /// string. Returns null for non-positive values so the caller
        /// can unambiguously decide not to render anything.
        private static string? FormatTiming(long totalMs)
        {
            if (totalMs <= 0) return null;
            int totalSeconds = (int)Math.Round(totalMs / 1000.0);
            int mins = totalSeconds / 60;
            int secs = totalSeconds % 60;
            return mins > 0 ? $"{mins}m {secs}s" : $"{secs}s";
        }

        private static Image<Rgba32> RenderEmptyLayout()
        {
            var empty = ImageUtils.CreateStandardImage(PlaceholderWidth, PlaceholderWidth + UIConstants.Padding * 2, UIConstants.White);

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
            DlMirror.Copy(outputPath, settings.FlatImageMirrorPath);

            return outputPath;
        }

        public class LoadedImage
        {
            public bool Success { get; set; }
            public string? Result { get; set; }
            public required string Generator { get; set; }
            /// Preformatted total generation time (e.g. "2m 15s"), or
            /// null if we have no usable timing for this result.
            public string? TimingLabel { get; set; }
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
