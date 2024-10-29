using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
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

using IdeogramAPIClient;
using System.Text.RegularExpressions;

namespace MultiImageClient
{
    public static class TextFormatting
    {
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        private static void DrawKeyValuePair(Graphics graphics, PromptHistoryStep step, float fontSize, float x, float keyWidth, float valueWidth, ref float y)
        {
            using (var whiteBrush = new SolidBrush(Color.White))
            using (var blackBrush = new SolidBrush(Color.Black))
            {
                var key = $"{step.TransformationType}";
                var value = $"{step.Explanation}";
                if (step.PromptReplacementMetadata != null)
                {
                    //also write temp here.
                }
                if (value.Length > 1000)
                {
                    value = value.Substring(0, 1000)+"...";
                }
                using (var keyFont = GetSupportedFont(key, fontSize, FontStyle.Regular))
                using (var valueFont = GetMonospacedFont(value, fontSize, FontStyle.Regular))
                {
                    float lineHeight = Math.Max(keyFont.GetHeight(graphics), valueFont.GetHeight(graphics)) + 2;
                    StringFormat stringFormat = new StringFormat
                    {
                        Trimming = StringTrimming.Word,
                        FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces
                    };

                    // Measure the height required for key and value
                    var keySizeMeasured = graphics.MeasureString(key, keyFont, new SizeF(keyWidth, float.MaxValue), stringFormat);
                    var valueSizeMeasured = graphics.MeasureString(value, valueFont, new SizeF(valueWidth, float.MaxValue), stringFormat);

                    float maxHeight = Math.Max(keySizeMeasured.Height, valueSizeMeasured.Height);

                    // Draw key
                    graphics.FillRectangle(Brushes.Black, x, y, keyWidth, maxHeight);
                    graphics.DrawString(key, keyFont, whiteBrush, new RectangleF(x, y, keyWidth, maxHeight), stringFormat);

                    // Draw value
                    graphics.FillRectangle(Brushes.White, x + keyWidth, y, valueWidth, maxHeight);
                    graphics.DrawString(value, valueFont, blackBrush, new RectangleF(x + keyWidth, y, valueWidth, maxHeight), stringFormat);

                    y += maxHeight + 5; // Move to the next line with some spacing
                }
            }
        }

        private static void DrawJustOneLargeText(Graphics graphics, string theText, float leftMargin, float totalWidth, ref float y)
        {
            using (var whiteBrush = new SolidBrush(Color.White))
            using (var blackBrush = new SolidBrush(Color.Black))
            {
                // Define the font
                float fontSize = 48; // Large font size
                using (var font = new Font("Times New Roman", fontSize, FontStyle.Bold))
                {
                    // Measure the size of the text
                    var textSize = graphics.MeasureString(theText, font);

                    // Calculate the position to center the text
                    float x = leftMargin + (totalWidth - textSize.Width) / 2;

                    // Draw the background rectangle
                    graphics.FillRectangle(blackBrush, leftMargin, y, totalWidth, textSize.Height);

                    // Draw the text
                    graphics.DrawString(theText, font, whiteBrush, x, y);

                    // Update the y position
                    y += textSize.Height + 10; // Add some spacing below the text
                }
            }

        }
        private static void DrawImageInfo(Graphics graphics, Dictionary<string, string> imageInfo, Font font, float x, float totalWidth, ref float y)
        {
            const float padding = 5f;
            float lineHeight = font.GetHeight(graphics);
            float currentX = x;
            float maxY = y;

            using (var whiteBrush = new SolidBrush(Color.White))
            using (var blackBrush = new SolidBrush(Color.Black))
            {
                StringFormat stringFormat = new StringFormat
                {
                    Trimming = StringTrimming.Word,
                    FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces
                };

                foreach (var kvp in imageInfo)
                {
                    float itemKeyWidth = graphics.MeasureString(kvp.Key, font).Width + padding;
                    float maxValueWidth = totalWidth - itemKeyWidth;

                    // Measure the actual width needed for the value
                    SizeF valueSizeMeasured = graphics.MeasureString(kvp.Value, font, new SizeF(maxValueWidth, float.MaxValue), stringFormat);
                    float actualValueWidth = Math.Min(valueSizeMeasured.Width, maxValueWidth);

                    // Check if we need to move to the next line
                    if (currentX + itemKeyWidth + actualValueWidth > x + totalWidth)
                    {
                        currentX = x;
                        y = maxY + padding;
                    }

                    // Draw key (white on black)
                    graphics.FillRectangle(Brushes.Black, currentX, y, itemKeyWidth, valueSizeMeasured.Height);
                    graphics.DrawString(kvp.Key, font, whiteBrush, new RectangleF(currentX, y, itemKeyWidth, valueSizeMeasured.Height), stringFormat);

                    // Draw value (black on white)
                    graphics.FillRectangle(Brushes.White, currentX + itemKeyWidth, y, actualValueWidth, valueSizeMeasured.Height);
                    graphics.DrawString(kvp.Value, font, blackBrush, new RectangleF(currentX + itemKeyWidth, y, actualValueWidth, valueSizeMeasured.Height), stringFormat);

                    // Update positions
                    currentX += itemKeyWidth + actualValueWidth + padding;
                    maxY = Math.Max(maxY, y + valueSizeMeasured.Height);

                    // If we're close to the right edge, move to the next line
                    if (currentX + itemKeyWidth > x + totalWidth - padding)
                    {
                        currentX = x;
                        y = maxY + padding;
                    }
                }
            }

            y = maxY + padding; // Update final y position
        }

        public static async Task SaveImageAndAnnotateText(byte[] imageBytes, IEnumerable<PromptHistoryStep> historySteps, Dictionary<string, string> imageInfo, string outputPath, SaveType saveType)
        {
            using var ms = new MemoryStream(imageBytes);
            using var originalImage = Image.FromStream(ms);

            int annotationWidth = 1000; // Standard width for the right side annotation section
            int annotationHeight = 100; // Height for the bottom annotation section

            Bitmap annotatedImage;
            Graphics graphics;

            if (saveType == SaveType.JustOverride)
            {
                // Expand the image at the bottom
                annotatedImage = new Bitmap(originalImage.Width, originalImage.Height + annotationHeight);
                graphics = Graphics.FromImage(annotatedImage);

                // Draw the original image
                graphics.DrawImage(originalImage, 0, 0);

                // Set up the annotation area at the bottom
                graphics.FillRectangle(Brushes.Black, 0, originalImage.Height, originalImage.Width, annotationHeight);

                float y = originalImage.Height + 10; // Start a bit below the top of the annotation area
                float leftMargin = 10;
                float rightMargin = annotatedImage.Width - 10;
                float totalWidth = rightMargin - leftMargin;

                var theText = imageInfo.First().Value;
                DrawJustOneLargeText(graphics, theText, leftMargin, totalWidth, ref y);
            }
            else
            {
                // Expand the image on the right
                annotatedImage = new Bitmap(originalImage.Width + annotationWidth, originalImage.Height);
                graphics = Graphics.FromImage(annotatedImage);

                // Draw the original image
                graphics.DrawImage(originalImage, 0, 0);

                // Set up the annotation area on the right
                graphics.FillRectangle(Brushes.Black, originalImage.Width, 0, annotationWidth, originalImage.Height);

                float y = 5; // Start a bit below the top
                float fontSize = 12;

                float leftMargin = originalImage.Width + 5;
                float rightMargin = annotatedImage.Width - 5;
                float totalWidth = rightMargin - leftMargin;
                float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;

                // Draw text into the right side black rectangle
                foreach (var historyStep in historySteps)
                {
                    DrawKeyValuePair(graphics, historyStep, fontSize, leftMargin, keyWidth, valueWidth, ref y);
                    y += 2; // Add small gap between text pairs
                }

                DrawImageInfo(graphics, imageInfo, new Font("Arial", fontSize, FontStyle.Regular), leftMargin, totalWidth, ref y);
            }

            annotatedImage.Save(outputPath, ImageFormat.Png);
        }

        private static Font GetSupportedFont(string text, float fontSize, FontStyle style)
        {
            // List of fonts to try, in order of preference
            string[] fontFamilies = { "Arial", "Segoe UI", "Microsoft Sans Serif", "Arial Unicode MS" };

            foreach (string fontFamily in fontFamilies)
            {
                using (var font = new Font(fontFamily, fontSize, style))
                {
                    if (font.Name == fontFamily && CanDisplayText(font, text))
                    {
                        return new Font(fontFamily, fontSize, style);
                    }
                }
            }

            // If no font can display all characters, return a default font
            return new Font(FontFamily.GenericSansSerif, fontSize, style);
        }

        private static bool CanDisplayText(Font font, string text)
        {
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                return text.All(c => font.FontFamily.GetEmHeight(font.Style) != 0);
            }
        }

        private static Font GetMonospacedFont(string text, float fontSize, FontStyle style)
        {
            // List of monospaced fonts to try, in order of preference
            string[] monoFontFamilies = { "Consolas", "Courier New", "Lucida Console", "Monaco" };

            foreach (string fontFamily in monoFontFamilies)
            {
                using (var font = new Font(fontFamily, fontSize, style))
                {
                    if (font.Name == fontFamily && CanDisplayText(font, text))
                    {
                        Console.WriteLine($"chose font: {font.Name} cause it could display all.");
                        return new Font(fontFamily, fontSize, style);
                    }
                }
            }

            // If no monospaced font can display all characters, return a default monospaced font
            Console.WriteLine($"Failed to choose font, fallback to genri mono");
            return new Font(FontFamily.GenericMonospace, fontSize, style);
        }
    }
}
