using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using SkiaSharp;


namespace MultiImageClient
{
    public static class TextFormatting
    {
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        private static void DrawKeyValuePair(SKCanvas canvas, string key, string value, float fontSize, float x, float keyWidth, float valueWidth, ref float y)
        {
            // just trunc. ugh.
            value = value.Length > 2500 ? value[..2500] + "..." : value;

            var font = FontUtils.CreateFont(fontSize, FontStyle.Regular);
            const float PADDING = 4;

            // Measure text height with wrapping
            var keyOptions = new TextBlockOptions(font)
            {
                WrappingLength = keyWidth - (2 * PADDING),
                LineSpacing = 1.2f
            };

            var valueOptions = new TextBlockOptions(font)
            {
                WrappingLength = valueWidth - (2 * PADDING),
                LineSpacing = 1.2f
            };

            var keyBounds = TextLayout.Measure(key, keyOptions);
            var valueBounds = TextLayout.Measure(value, valueOptions);
            var rowHeight = Math.Max(keyBounds.Height, valueBounds.Height) + (2 * PADDING);

            // Draw the background boxes based on measured height
            canvas.FillRect(SKRect.Create(x, y, keyWidth, rowHeight), SKColors.Black);
            canvas.FillRect(SKRect.Create(x + keyWidth, y, valueWidth, rowHeight), SKColors.White);

            // Update options with final positions
            keyOptions.Origin = new SKPoint(x + PADDING, y + PADDING);
            valueOptions.Origin = new SKPoint(x + keyWidth + PADDING, y + PADDING);

            // Draw the text
            canvas.DrawTextStandard(keyOptions, key, SKColors.White);
            canvas.DrawTextStandard(valueOptions, value, SKColors.Black);

            y += rowHeight + PADDING;
        }

        public static async Task JustAddSimpleTextToBottomAsync(byte[] imageBytes, IEnumerable<PromptHistoryStep> historySteps, Dictionary<string, string> imageInfo, string outputPath, SaveType saveType)
        {
            Console.WriteLine(outputPath);
            using var originalImage = Raster.Decode(imageBytes);
            var imageWidth = originalImage.Width;
            var intendedFontSize = 24;

            var theText = historySteps.First().Prompt ?? "failed to get text";
            var testFont = FontUtils.CreateFont(intendedFontSize, FontStyle.Regular);

            var horizontalPadding = 10;
            var verticalPadding = 10;
            var availableXPixels = imageWidth - (horizontalPadding * 2);

            var textOptions = new TextBlockOptions(testFont)
            {
                WrappingLength = availableXPixels,
                LineSpacing = 1.15f,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Dpi = UIConstants.TextDpi,
            };

            var keyBounds = TextLayout.Measure(theText, textOptions);

            int textHeight = (int)Math.Ceiling(keyBounds.Height);
            int yPixelsToAdd = textHeight + (verticalPadding * 2);
            int newHeight = originalImage.Height + yPixelsToAdd;

            using var annotatedImage = ImageUtils.CreateStandardImage(originalImage.Width, newHeight);

            using (var canvas = new SKCanvas(annotatedImage))
            {
                // Draw original image
                canvas.DrawBitmapAt(originalImage, 0, 0);

                // Set up the annotation area at the bottom
                canvas.FillRect(SKRect.Create(0, originalImage.Height, originalImage.Width, yPixelsToAdd), SKColors.Black);

                // Position text with proper padding
                textOptions.Origin = new SKPoint(horizontalPadding, originalImage.Height + verticalPadding);

                canvas.DrawTextStandard(textOptions, theText, SKColors.White);


                var meta = imageInfo["Producer"] ?? "no gen";
                // Create smaller font for metadata
                var metaFont = FontUtils.CreateFont(intendedFontSize * 0.75f, FontStyle.Regular);
                var metaOptions = new TextBlockOptions(metaFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Dpi = UIConstants.TextDpi,
                    Origin = new SKPoint(imageWidth - horizontalPadding, newHeight - verticalPadding)
                };

                canvas.DrawTextStandard(metaOptions, meta, SKColors.White);
            }

            await Task.Run(() => Raster.SavePng(annotatedImage, outputPath));
        }

        public static async Task SaveImageAndAnnotate(byte[] imageBytes, IEnumerable<PromptHistoryStep> historySteps, Dictionary<string, string> imageInfo, string outputPath, SaveType saveType)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new ArgumentException("Image bytes cannot be null or empty");
            }

            try
            {
                using var originalImage = Raster.Decode(imageBytes);

                // Define dimensions
                int annotationWidth = 850;
                float fontSize = 15;

                // Create new image with space for annotations
                using var annotatedImage = ImageUtils.CreateStandardImage(originalImage.Width + annotationWidth, originalImage.Height);

                using (var canvas = new SKCanvas(annotatedImage))
                {
                    // Draw original image
                    canvas.DrawBitmapAt(originalImage, 0, 0);

                    // Setup annotation area
                    float leftMargin = originalImage.Width + 5;
                    float rightMargin = annotatedImage.Width - 5;
                    float totalWidth = rightMargin - leftMargin;
                    float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                    float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;

                    // Fill right side with black background
                    canvas.FillRect(SKRect.Create(originalImage.Width, 0, annotationWidth, originalImage.Height), SKColors.Black);

                    // Draw history steps
                    float y = 5;
                    foreach (var historyStep in historySteps)
                    {
                        DrawKeyValuePair(canvas, historyStep.TransformationType.ToString(), historyStep.Explanation, fontSize, leftMargin, keyWidth, valueWidth, ref y);
                        y += 2; // Small gap between entries
                    }

                    // Draw image info if available
                    if (imageInfo.Any())
                    {
                        foreach (var kvp in imageInfo)
                        {
                            DrawKeyValuePair(canvas, kvp.Key, kvp.Value, fontSize, leftMargin, keyWidth, valueWidth, ref y);
                        }
                    }
                }

                await Task.Run(() => Raster.SavePng(annotatedImage, outputPath));
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception adding fuller text to image: {ex}");
            }
        }
    }
}
