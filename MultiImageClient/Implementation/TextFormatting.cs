using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using IdeogramAPIClient;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Drawing;
using RectangleF = SixLabors.ImageSharp.RectangleF;
using Color = SixLabors.ImageSharp.Color;
using PointF = SixLabors.ImageSharp.PointF;
using SystemFonts = SixLabors.Fonts.SystemFonts;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Point = SixLabors.ImageSharp.Point;
using Image = SixLabors.ImageSharp.Image;
using FontStyle = SixLabors.Fonts.FontStyle;


namespace MultiImageClient
{
    public static class TextFormatting
    {
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        private static void DrawKeyValuePair(IImageProcessingContext ctx, string key, string value, float fontSize, float x, float keyWidth, float valueWidth, ref float y)
        {
            // just trunc. ugh.
            value = value.Length > 2500 ? value[..2500] + "..." : value;

            var font = SixLabors.Fonts.SystemFonts.CreateFont("Arial", fontSize, SixLabors.Fonts.FontStyle.Regular);
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
            Console.WriteLine(outputPath);
            using var originalImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
            var imageWidth = originalImage.Width;
            var intendedFontSize = 24;
            
            var theText = historySteps.First().Prompt ?? "failed to get text";
            SixLabors.Fonts.FontFamily fontFamily;
            if (!SystemFonts.TryGet("Segoe UI", out fontFamily))
            {
                if (!SystemFonts.TryGet("Arial", out fontFamily))
                {
                    fontFamily = SystemFonts.Families.First(); // Fallback
                }
            }
            var testFont = fontFamily.CreateFont(intendedFontSize, FontStyle.Regular);

            var horizontalPadding = 10;
            var verticalPadding = 10;
            var availableXPixels = imageWidth - (horizontalPadding * 2);

            //we can measure font length with:
            var textOptions = new RichTextOptions(testFont)
            {
                WrappingLength = availableXPixels,
                LineSpacing = 1.15f,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Dpi= 72,
                FallbackFontFamilies = new[] { SystemFonts.Families.First() }
            };

            // Using the correct TextMeasurer method from SixLabors.Fonts
            var keyBounds = TextMeasurer.MeasureBounds(theText, textOptions);

            int textHeight = (int)Math.Ceiling(keyBounds.Height);
            int yPixelsToAdd = textHeight + (verticalPadding * 2);
            int newHeight = originalImage.Height + yPixelsToAdd;

            using var annotatedImage = new Image<Rgba32>(originalImage.Width, newHeight);

            annotatedImage.Mutate(ctx =>
            {
                // antialiasing.
                ctx.SetGraphicsOptions(new GraphicsOptions
                {
                    Antialias = true,
                    AntialiasSubpixelDepth = 16
                });

                // Draw original image
                ctx.DrawImage(originalImage, new Point(0, 0), 1f);

                // Set up the annotation area at the bottom
                ctx.Fill(Color.Black, new Rectangle(0, originalImage.Height, originalImage.Width, yPixelsToAdd));

                // Position text with proper padding
                textOptions.Origin = new PointF(horizontalPadding, originalImage.Height + verticalPadding);

                ctx.DrawText(textOptions, theText, Color.White);


                var meta = imageInfo["Producer"] ?? "no gen";
                // Create smaller font for metadata
                var metaFont = fontFamily.CreateFont(intendedFontSize * 0.75f, FontStyle.Regular);
                var metaOptions = new RichTextOptions(metaFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Dpi = 72,
                    Origin = new PointF(imageWidth - horizontalPadding, newHeight - verticalPadding)
                };

                ctx.DrawText(metaOptions, meta, new Color(new Rgba32(200, 200, 200)));
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
                        originalImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
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
