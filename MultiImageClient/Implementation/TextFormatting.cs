using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Reflection;
using System.Timers;
using System.Text;
using System.Windows.Forms;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using IdeogramAPIClient;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp.Processing;
using System.Drawing.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Threading;


namespace MultiImageClient
{
    public static class TextFormatting
    {
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        private static void DrawKeyValuePair(IImageProcessingContext ctx, string key, string value, float fontSize, float x, float keyWidth, float valueWidth, ref float y)
        {
            //var key = $"{step.TransformationType}";
            //var value = step.Explanation;
            value = value.Length > 2500 ? value[..2500] + "..." : value;

            var font = SystemFonts.CreateFont("Arial", fontSize, FontStyle.Regular);
            const float PADDING = 4;

            // Measure text height with wrapping
            var keyOptions = new RichTextOptions(font)
            {
                WrappingLength = keyWidth - (2 * PADDING),
                LineSpacing = 1.2f
            };

            var valueOptions = new RichTextOptions(font)
            {
                WrappingLength = valueWidth - (2 * PADDING),
                LineSpacing = 1.2f
            };

            // Using the correct TextMeasurer method from SixLabors.Fonts
            var keyBounds = TextMeasurer.MeasureBounds(key, keyOptions);
            var valueBounds = TextMeasurer.MeasureBounds(value, valueOptions);
            var rowHeight = Math.Max(keyBounds.Height, valueBounds.Height) + (2 * PADDING);

            // Draw the background boxes based on measured height
            var keyRect = new RectangleF(x, y, keyWidth, rowHeight);
            var valueRect = new RectangleF(x + keyWidth, y, valueWidth, rowHeight);

            ctx.Fill(Color.Black, keyRect)
               .Fill(Color.White, valueRect);

            // Update options with final positions
            keyOptions.Origin = new PointF(x + PADDING, y + PADDING);
            valueOptions.Origin = new PointF(x + keyWidth + PADDING, y + PADDING);

            // Draw the text
            ctx.DrawText(keyOptions, key, Color.White);
            ctx.DrawText(valueOptions, value, Color.Black);

            y += rowHeight + PADDING;
        }

        public static async Task JustAddSimpleTextToBottomAsync(byte[] imageBytes, IEnumerable<PromptHistoryStep> historySteps, Dictionary<string, string> imageInfo, string outputPath, SaveType saveType)
        {
            using var originalImage = Image.Load<Rgba32>(imageBytes);
            int annotationHeight = 100;
            int newHeight = originalImage.Height + annotationHeight;

            using var annotatedImage = new Image<Rgba32>(originalImage.Width, newHeight);

            annotatedImage.Mutate(ctx =>
            {
                // Draw original image
                ctx.DrawImage(originalImage, new Point(0, 0), 1f);

                // Set up the annotation area at the bottom
                ctx.Fill(Color.Black, new Rectangle(0, originalImage.Height, originalImage.Width, annotationHeight));

                // Setup text positioning
                float y = originalImage.Height + 10;
                float leftMargin = 10;
                float rightMargin = annotatedImage.Width - 10;
                float totalWidth = rightMargin - leftMargin;

                // Get text from imageInfo if available
                var theText = imageInfo.FirstOrDefault().Value ?? "";

                // Draw the text in white on the black background
                var font = SystemFonts.CreateFont("Arial", 48, FontStyle.Bold);
                var textOptions = new TextOptions(font)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    WrappingLength = totalWidth
                };

                var textBounds = TextMeasurer.MeasureBounds(theText, textOptions);
                float x = leftMargin + (totalWidth - textBounds.Width) / 2;

                ctx.DrawText(theText, font, Color.White, new PointF(x, y));
            });

            await annotatedImage.SaveAsPngAsync(outputPath);
        }

        public static async Task SaveImageAndAnnotate(byte[] imageBytes, IEnumerable<PromptHistoryStep> historySteps, Dictionary<string, string> imageInfo, string outputPath, SaveType saveType)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                throw new ArgumentException("Image bytes cannot be null or empty");
            }
            
            try
            {
                Image originalImage;
                try
                {
                    originalImage = Image.Load(imageBytes);
                }
                catch (Exception ex2)
                {
                    Logger.Log($"{ex2} failed normal load");
                    try
                    {
                        originalImage = Image.Load<Rgba32>(imageBytes);
                    }
                    catch (Exception ex3)
                    {
                        Logger.Log($"{ex2} failed rgba32 load");
                        throw ex3;
                    }
                }

                // Define dimensions
                int annotationWidth = 850;
                float fontSize = 15;

                // Create new image with space for annotations
                using var annotatedImage = new Image<Rgba32>(originalImage.Width + annotationWidth, originalImage.Height);

                annotatedImage.Mutate(ctx =>
                {
                    // Draw original image
                    ctx.DrawImage(originalImage, new Point(0, 0), 1f);

                    // Setup annotation area
                    float leftMargin = originalImage.Width + 5;
                    float rightMargin = annotatedImage.Width - 5;
                    float totalWidth = rightMargin - leftMargin;
                    float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                    float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;

                    // Fill right side with black background
                    ctx.Fill(Color.Black, new Rectangle(originalImage.Width, 0, annotationWidth, originalImage.Height));

                    // Draw history steps
                    float y = 5;
                    foreach (var historyStep in historySteps)
                    {
                        DrawKeyValuePair(ctx, historyStep.TransformationType.ToString(), historyStep.Explanation, fontSize, leftMargin, keyWidth, valueWidth, ref y);
                        y += 2; // Small gap between entries
                    }

                    // Draw image info if available
                    if (imageInfo.Any())
                    {
                        foreach (var kvp in imageInfo)
                        {
                            DrawKeyValuePair(ctx, kvp.Key, kvp.Value, fontSize, leftMargin, keyWidth, valueWidth, ref y);
                        }
                    }
                });

                await annotatedImage.SaveAsPngAsync(outputPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"Exception adding fuller text to image: {ex}");
            }
        }
    }
}
