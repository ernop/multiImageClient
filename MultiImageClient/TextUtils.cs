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
    public static class TextUtils
    {
        private static readonly object _lockObject = new object();
        private static readonly Dictionary<string, int> _filenameCounts = new Dictionary<string, int>();
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;

        public static string GenerateUniqueFilename(PromptDetails promptDetails, string baseFolder)
        {
            var truncatedPrompt = "";
            if (!string.IsNullOrWhiteSpace(promptDetails.Filename))
            {
                truncatedPrompt = promptDetails.Filename.Length > 100 ? promptDetails.Filename.Substring(0, 100) : promptDetails.Filename;
            }
            else
            {
                truncatedPrompt = promptDetails.Prompt.Length > 100 ? promptDetails.Prompt.Substring(0, 100) : promptDetails.Prompt;
            }

            // Convert AspectRatio to a short text
            string aspectRatioText = "";
            if (promptDetails.IdeogramDetails.AspectRatio.HasValue)
            {
                aspectRatioText = IdeogramUtils.StringifyAspectRatio(promptDetails.IdeogramDetails.AspectRatio.Value);
            }

            // Combine relevant metadata
            string combined = $"{truncatedPrompt}_{aspectRatioText}_{promptDetails.IdeogramDetails.Model}_{promptDetails.IdeogramDetails.MagicPromptOption}";

            // Include StyleType if present
            if (promptDetails.IdeogramDetails.StyleType.HasValue)
            {
                combined += $"_{promptDetails.IdeogramDetails.StyleType}";
            }

            // Include NegativePrompt if present and not empty
            if (!string.IsNullOrWhiteSpace(promptDetails.IdeogramDetails.NegativePrompt))
            {
                combined += $"_{promptDetails.IdeogramDetails.NegativePrompt}";
            }

            // Remove invalid characters
            string sanitized = Regex.Replace(combined, @"[^a-zA-Z0-9_\-]", "_");

            // Truncate to a reasonable length if necessary
            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            // Ensure the filename is unique by appending a timestamp and a sequential number if needed
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string baseFilename = $"{sanitized}_{timestamp}";

            lock (_lockObject)
            {
                int count = 0;
                string uniqueFilename;
                do
                {
                    if (count == 0)
                    {
                        uniqueFilename = baseFilename;
                    }
                    else
                    {
                        uniqueFilename = $"{baseFilename}_{count:D4}";
                    }
                    count++;
                } while (File.Exists(Path.Combine(baseFolder, $"{uniqueFilename}.png")));

                _filenameCounts[baseFilename] = count;
                return uniqueFilename;
            }
        }

        public static async Task SaveImageAndAnnotateText(byte[] imageBytes,IEnumerable<ImageConstructionStep> texts, Dictionary<string, string> imageInfo, string outputPath)
        {
            using var ms = new MemoryStream(imageBytes);
            using var originalImage = Image.FromStream(ms);
            
            int textHeight = CalculateTextHeight(texts, imageInfo, originalImage.Width);
            int newHeight = originalImage.Height + textHeight;

            using (var annotatedImage = new Bitmap(originalImage.Width, newHeight))
            using (var graphics = Graphics.FromImage(annotatedImage))
            {
                graphics.Clear(Color.Black);
                graphics.DrawImage(originalImage, 0, 0);

                using (var font = new Font("Arial", 12, FontStyle.Regular))
                {
                    float y = originalImage.Height + 2; // Start 2 pixels below the original image
                    float leftMargin = 5;
                    float rightMargin = originalImage.Width - 5;
                    float totalWidth = rightMargin - leftMargin;
                    float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                    float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;

                    // Handle all items except the image info
                    foreach (var text in texts)
                    {
                        DrawKeyValuePair(graphics, text.Description, text.Details, font, leftMargin, keyWidth, valueWidth, ref y);
                        y += 2; // Add small gap between text pairs
                    }

                    // Handle the image info
                    DrawImageInfo(graphics, imageInfo, font, leftMargin, totalWidth, ref y);
                }

                annotatedImage.Save(outputPath, ImageFormat.Png);
            }
        }

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
                var keyRect = new RectangleF(x, y, keyWidth, 0);
                var valueRect = new RectangleF(x + keyWidth, y, valueWidth, 0);

                var keySizeMeasured = graphics.MeasureString(key, font, new SizeF(keyWidth, float.MaxValue), stringFormat);
                var valueSizeMeasured = graphics.MeasureString(value, font, new SizeF(valueWidth, float.MaxValue), stringFormat);

                float maxHeight = Math.Max(keySizeMeasured.Height, valueSizeMeasured.Height);

                // Draw key
                graphics.FillRectangle(Brushes.Black, keyRect.X, keyRect.Y, keyRect.Width, maxHeight);
                graphics.DrawString(key, font, whiteBrush, new RectangleF(keyRect.X, keyRect.Y, keyRect.Width, maxHeight), stringFormat);

                // Draw value
                graphics.FillRectangle(Brushes.White, valueRect.X, valueRect.Y, valueRect.Width, maxHeight);
                graphics.DrawString(value, font, blackBrush, new RectangleF(valueRect.X, valueRect.Y, valueRect.Width, maxHeight), stringFormat);

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
                    graphics.FillRectangle(Brushes.Black, currentX, y, itemKeyWidth, itemHeight);
                    graphics.DrawString(kvp.Key, font, whiteBrush, currentX + padding / 2, y);

                    // Draw value (black on white)
                    graphics.FillRectangle(Brushes.White, currentX + itemKeyWidth, y, itemValueWidth, itemHeight);
                    graphics.DrawString(kvp.Value, font, blackBrush, currentX + itemKeyWidth + padding / 2, y);

                    currentX += itemWidth;
                    maxY = y + itemHeight;
                }
            }

            y = maxY + padding; // Update final y position
        }

        private static IEnumerable<string> WrapText(Graphics g, string text, Font font, float maxWidth)
        {
            // Split the text into lines, preserving empty lines
            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    yield return string.Empty;
                    continue;
                }

                string[] words = line.Split(' ');
                StringBuilder currentLine = new StringBuilder();

                foreach (string word in words)
                {
                    string testLine = currentLine.Length > 0 ? currentLine + " " + word : word;
                    var size = g.MeasureString(testLine, font);

                    if (size.Width > maxWidth && currentLine.Length > 0)
                    {
                        yield return currentLine.ToString();
                        currentLine.Clear();
                        currentLine.Append(word);
                    }
                    else
                    {
                        if (currentLine.Length > 0)
                        {
                            currentLine.Append(" ");
                        }
                        currentLine.Append(word);
                    }
                }

                if (currentLine.Length > 0)
                {
                    yield return currentLine.ToString();
                }
            }
        }

       

        private static int CalculateTextHeight(IEnumerable<ImageConstructionStep> texts, Dictionary<string, string> imageInfo, int imageWidth)
        {
            int height = 0;
            using (var font = new Font("Arial", 12, FontStyle.Regular))
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                float leftMargin = 5;
                float rightMargin = imageWidth - 5;
                float totalWidth = rightMargin - leftMargin;
                float keyWidth = totalWidth * KEY_WIDTH_PROPORTION;
                float valueWidth = totalWidth * VALUE_WIDTH_PROPORTION;
                float lineHeight = font.GetHeight(graphics) + 2;
                StringFormat stringFormat = new StringFormat
                {
                    Trimming = StringTrimming.Word,
                    FormatFlags = StringFormatFlags.LineLimit | StringFormatFlags.NoClip
                };

                // Add initial padding
                height += 2; // Start 2 pixels below the original image

                // Calculate height for text pairs
                foreach (var textPair in texts)
                {
                    SizeF keySize = graphics.MeasureString(textPair.Description, font, new SizeF(keyWidth, float.MaxValue), stringFormat, out int keyCharsFitted, out int keyLinesFilled);
                    SizeF valueSize = graphics.MeasureString(textPair.Details, font, new SizeF(valueWidth, float.MaxValue), stringFormat, out int valueCharsFitted, out int valueLinesFilled);
                    float maxHeight = Math.Max(keySize.Height, valueSize.Height);

                    height += (int)(maxHeight + 5); // Add spacing between pairs
                }

                // Add height for image info (assuming it always fits in one line)
                if (imageInfo.Count > 0)
                {
                    height += (int)(lineHeight + 7); // One line height plus some padding
                }

                return height;
            }
        }
    }
}