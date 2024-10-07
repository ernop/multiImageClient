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

namespace MultiClientRunner
{
    public static class TextFormatting
    {
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        private static void DrawKeyValuePair(Graphics graphics, string key, string value, Font font, float x, float keyWidth, float valueWidth, ref float y)
        {
            using (var whiteBrush = new SolidBrush(Color.White))
            using (var blackBrush = new SolidBrush(Color.Black))
            {
                float lineHeight = font.GetHeight(graphics) + 2;
                StringFormat stringFormat = new StringFormat
                {
                    Trimming = StringTrimming.Word,
                    FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces
                };

                // Measure the height required for key and value
                var keySizeMeasured = graphics.MeasureString(key, font, new SizeF(keyWidth, float.MaxValue), stringFormat);
                var valueSizeMeasured = graphics.MeasureString(value, font, new SizeF(valueWidth, float.MaxValue), stringFormat);

                float maxHeight = Math.Max(keySizeMeasured.Height, valueSizeMeasured.Height);

                // Draw key
                graphics.FillRectangle(Brushes.Black, x, y, keyWidth, maxHeight);
                graphics.DrawString(key, font, whiteBrush, new RectangleF(x, y, keyWidth, maxHeight), stringFormat);

                // Draw value
                graphics.FillRectangle(Brushes.White, x + keyWidth, y, valueWidth, maxHeight);
                graphics.DrawString(value, font, blackBrush, new RectangleF(x + keyWidth, y, valueWidth, maxHeight), stringFormat);

                y += maxHeight + 5; // Move to the next line with some spacing
            }
        }

        private static void DrawImageInfo(Graphics graphics, Dictionary<string, string> imageInfo, Font font, float x, float totalWidth, ref float y)
        {
            const float padding = 5f;
            float itemHeight = font.GetHeight(graphics);
            float currentX = x;
            float maxY = y;

            using (var whiteBrush = new SolidBrush(Color.White))
            using (var blackBrush = new SolidBrush(Color.Black))
            {
                foreach (var kvp in imageInfo)
                {
                    float itemKeyWidth = graphics.MeasureString(kvp.Key, font).Width + padding;
                    float itemValueWidth = graphics.MeasureString(kvp.Value, font).Width + padding;
                    float itemWidth = itemKeyWidth + itemValueWidth + padding;

                    if (currentX + itemWidth > x + totalWidth)
                    {
                        currentX = x;
                        y += itemHeight + padding;
                    }

                    // Draw key (white on black)
                    var keyTargetX = currentX + padding / 2;
                    graphics.FillRectangle(Brushes.Black, currentX, y, itemKeyWidth, itemHeight);
                    graphics.DrawString(kvp.Key, font, whiteBrush, keyTargetX, y);

                    // Draw value (black on white)
                    var valueTargetX = currentX + itemKeyWidth + padding / 2;
                    graphics.FillRectangle(Brushes.White, currentX + itemKeyWidth, y, itemValueWidth, itemHeight);
                    graphics.DrawString(kvp.Value, font, blackBrush, valueTargetX, y);

                    //Console.WriteLine($"drawing: { kvp.Key} at {keyTargetX}, {y}");
                    //Console.WriteLine($"drawing: {kvp.Value} at {valueTargetX}, {y}");
                    currentX += itemWidth;
                    maxY = y + itemHeight;
                }
            }

            y = maxY + padding; // Update final y position
        }

        
        public static async Task SaveImageAndAnnotateText(byte[] imageBytes, IEnumerable<ImageConstructionStep> texts, Dictionary<string, string> imageInfo, string outputPath)
        {
            using var ms = new MemoryStream(imageBytes);
            using var originalImage = Image.FromStream(ms);

            int tallHeight = 5000; // Arbitrary tall height which we will cut later.
            using var textImage = new Bitmap(originalImage.Width, tallHeight);
            using var graphics = Graphics.FromImage(textImage);
            graphics.Clear(Color.Black);
            float y = 0;
            using (var font = new Font("Arial", 12, FontStyle.Regular))
            {
                float leftMargin = 5;
                float rightMargin = originalImage.Width - 5;
                float totalWidth = rightMargin - leftMargin;
                float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;

                // Draw text into the tall black square
                foreach (var text in texts)
                {
                    DrawKeyValuePair(graphics, text.Description, text.Details, font, leftMargin, keyWidth, valueWidth, ref y);
                    y += 2; // Add small gap between text pairs
                }

                DrawImageInfo(graphics, imageInfo, font, leftMargin, totalWidth, ref y);
            }

            // Measure the actual height used
            int actualTextHeight = (int)y;

            // Create the final annotated image
            int newHeight = originalImage.Height + actualTextHeight;
            using var annotatedImage = new Bitmap(originalImage.Width, newHeight);
            using var finalGraphics = Graphics.FromImage(annotatedImage);
            finalGraphics.Clear(Color.Black);
            finalGraphics.DrawImage(originalImage, 0, 0);
            finalGraphics.DrawImage(textImage, 0, originalImage.Height, new Rectangle(0, 0, originalImage.Width, actualTextHeight), GraphicsUnit.Pixel);

            annotatedImage.Save(outputPath, ImageFormat.Png);
        }

        public static void TestImageAnnotationAndSaving()
        {
            var texts = new List<ImageConstructionStep>
            {
                new ImageConstructionStep("Prompt", "A beautiful landscape with a river and mountains"),
                new ImageConstructionStep("Rewritten longer much more detailed prompt:", "A beautiful landscape with a river and mountains, which is a detailed and long description of the image, with many adjectives and detailed descriptions of the elements in the image., including specifics, which start now: A beautiful landscape with a river and mountains, which is a detailed and long description of the image, with many adjectives and detailed descriptions of the elements in the image., including specifics, which start now      : "),
                new ImageConstructionStep("Seed", "12345")
            };
            var imageInfo = new Dictionary<string, string>
            {
                { "Generated", DateTime.Now.ToString() },
                { "Generator", "TestMethod." }, //the generatorLong does overlap incorrectly.
                { "GeneratorLong", "TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestTestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod.TestMethod.TestMethod. TestMethod.TestMethod. TestMethod.TestMethod." },
                { "Style", "Realistic" },
                { "Seed", "12345" }
            };

            var imageBytes = File.ReadAllBytesAsync("testImage.png").Result;
            SaveImageAndAnnotateText(imageBytes, texts, imageInfo, "annotated_image.png");
        }
    }
}