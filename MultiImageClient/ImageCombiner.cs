
using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    internal class ImageCombiner
    {
        internal void SaveMultipleImagesWithSubtitle(Dictionary<ImageGeneratorApiType, TaskProcessResult> multiResults, Settings settings, string prompt)
        {
            // Download and load all images into memory
            var loadedImages = new List<(Image img, ImageGeneratorApiType engine)>();
            foreach (var kvp in multiResults)
            {
                var result = kvp.Value;
                if (result.IsSuccess && !string.IsNullOrEmpty(result.Url))
                {
                    try
                    {
                        using (var wc = new WebClient())
                        {
                            var data = wc.DownloadData(result.Url);
                            using (var ms = new MemoryStream(data))
                            {
                                var image = Image.FromStream(ms);
                                loadedImages.Add((image, kvp.Key));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Handle download or image load errors as needed
                        Console.WriteLine($"Could not load image from {result.Url}: {ex.Message}");
                    }
                }
            }

            if (loadedImages.Count == 0)
            {
                Console.WriteLine("No images to combine.");
                return;
            }

            // Determine layout
            // We'll place images horizontally, so total width = sum of all widths
            // Height = max image height + height for subtitle text + height for prompt text
            // We'll use a fixed font for subtitles and prompt.
            var fontForSubtitle = new Font("Arial", 24, FontStyle.Bold, GraphicsUnit.Pixel);
            var fontForPrompt = new Font("Arial", 24, FontStyle.Bold, GraphicsUnit.Pixel);

            int totalWidth = 0;
            int maxImageHeight = 0;
            foreach (var (img, engine) in loadedImages)
            {
                totalWidth += img.Width;
                if (img.Height > maxImageHeight)
                    maxImageHeight = img.Height;
            }

            // Measure text height
            // We'll create a temporary bitmap to measure the height of text lines.
            int subtitleHeight = 0;
            int promptHeight = 0;

            using (var measureBmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(measureBmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Measure max engine name height
                // We assume each engine name is a single word like "Midjourney", "Dalle3", etc.
                // We'll just measure the line height once since it's the same font and size.
                var engines = loadedImages.Select(x => x.engine.ToString()).ToArray();
                foreach (var e in engines)
                {
                    var size = g.MeasureString(e, fontForSubtitle);
                    var h = (int)Math.Ceiling(size.Height);
                    if (h > subtitleHeight)
                        subtitleHeight = h;
                }

                // Measure prompt line height
                var promptSize = g.MeasureString(prompt, fontForPrompt);
                promptHeight = (int)Math.Ceiling(promptSize.Height);
            }

            int totalHeight = maxImageHeight + subtitleHeight + promptHeight;

            // Create the output bitmap
            using (var finalBmp = new Bitmap(totalWidth, totalHeight))
            using (var g = Graphics.FromImage(finalBmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Draw all images horizontally
                int currentX = 0;
                foreach (var (img, engine) in loadedImages)
                {
                    // Draw the image at (currentX, 0)
                    g.DrawImage(img, currentX, 0, img.Width, img.Height);

                    // Draw the engine name below the image
                    var engineName = engine.ToString();
                    var engineNameSize = g.MeasureString(engineName, fontForSubtitle);
                    var engineTextX = currentX + (img.Width - (int)engineNameSize.Width) / 2;
                    if (engineTextX < currentX) engineTextX = currentX; // safety check
                    var engineTextY = maxImageHeight; // right below the image
                    g.DrawString(engineName, fontForSubtitle, Brushes.Black, new PointF(engineTextX, engineTextY));

                    currentX += img.Width;
                }

                // Draw the prompt line at the very bottom (after the subtitle line)
                // The prompt line starts after maxImageHeight + subtitleHeight
                int promptY = maxImageHeight + subtitleHeight;
                var promptSize = g.MeasureString(prompt, fontForPrompt);
                var promptX = (totalWidth - (int)promptSize.Width) / 2;
                if (promptX < 0) promptX = 0; // In case prompt is wider than totalWidth
                g.DrawString(prompt, fontForPrompt, Brushes.Black, new PointF(promptX, promptY));

                // Save the final image
                // Determine output file name and path from settings if needed
                var usePromptPrefix = prompt;
                if (prompt.Length > 50)
                {
                    usePromptPrefix = prompt.Substring(0, 50);
                }
                var ii = 1;
                var outputPath = Path.Combine(settings.ImageDownloadBaseFolder, $"combined_image{usePromptPrefix}_{ii}.png");

                while (System.IO.File.Exists(outputPath))
                {
                    //increment the filename
                    ii++;
                    outputPath = Path.Combine(settings.ImageDownloadBaseFolder, $"combined_image{usePromptPrefix}_{ii}.png");
                }
                finalBmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);

                Console.WriteLine($"Combined image saved at: {outputPath}");
            }

            // Dispose loaded images
            foreach (var (img, engine) in loadedImages)
            {
                img.Dispose();
            }

            fontForSubtitle.Dispose();
            fontForPrompt.Dispose();
        }
    }
}
