using IdeogramAPIClient;

using ImageMagick;

using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using static System.Net.Mime.MediaTypeNames;

namespace MultiImageClient
{
    public static class ImageSaving
    {
        private static readonly HttpClient httpClient = new HttpClient();

        public static void ConvertWebpTopng(string inputFp)
        {
            var im = new MagickImage(inputFp);
            var newFp = Path.ChangeExtension(inputFp, ".png");
            im.Write(newFp);
        }

        public static async Task<byte[]> DownloadImageAsync(TaskProcessResult result)
        {
            try
            {
                using var response = await httpClient.GetAsync(result.Url);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"Failed to download image: {response.StatusCode}");
                    return Array.Empty<byte>();
                }

                var res = await response.Content.ReadAsByteArrayAsync();
                if (res.Length == 0)
                {
                    Logger.Log($"Downloaded image is empty");
                    return Array.Empty<byte>();
                }

                Logger.Log($"\tDownloading image from: {result.Url}, bytes:{res.Length}");
                return res;
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to download image from {result.Url}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public static async Task<string> SaveImageAsync(
            byte[] imageBytes,
            TaskProcessResult result,
            Settings settings,
            SaveType saveType,
            IImageGenerator generator)
        {
            string todayFolder = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            string baseFolder = Path.Combine(settings.ImageDownloadBaseFolder, todayFolder);

            if (saveType != SaveType.Raw)
            {
                baseFolder = Path.Combine(baseFolder, saveType.ToString());
            }

            Directory.CreateDirectory(baseFolder);

            if (result.ContentType == "image/webp")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.WebP);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);
            }
            else if (result.ContentType == "image/svg+xml")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Svg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png);             
            }
            else if (result.ContentType == "image/jpeg")
            {
                var fakeImage = new MagickImage(imageBytes, MagickFormat.Jpg);
                imageBytes = fakeImage.ToByteArray(MagickFormat.Png); 
            }
            else if (result.ContentType == "image/png")
            {
                //Console.WriteLine("png do nothing, all good");
            }
            else if (result.ContentType == null)
            {
                //Console.WriteLine("contentType null, so fall into .png");
            }
            else
            {
                Console.WriteLine("some other weird contenttype. {result.ContentType}");
            }

            var usingPromptTextPart = FilenameGenerator.TruncatePrompt(result.PromptDetails.Prompt, 90);
            var generatorFilename = generator.GetFilenamePart(result.PromptDetails);

            var safeFilename = FilenameGenerator.GenerateUniqueFilename($"{generatorFilename}_{usingPromptTextPart}", result, baseFolder, saveType);
            var fullPath = Path.Combine(baseFolder, safeFilename);

            try
            {
                if (File.Exists(fullPath))
                {
                    throw new Exception("no overwriting!");
                }
                await File.WriteAllBytesAsync(fullPath, imageBytes);

                if (saveType == SaveType.Raw)
                {
                    //Logger.Log($"Saved {saveType} image. Fp: {fullPath}");
                    //stats.SavedRawImageCount++;
                }
                else
                {
                    var imageInfo = GetAnnotationDefaultData(result, fullPath, saveType, generator);
                    var usingSteps = GetUsingSteps(saveType, result.PromptDetails);
                    if (saveType == SaveType.JustOverride)
                    {
                        await TextFormatting.JustAddSimpleTextToBottomAsync(
                            imageBytes,
                            usingSteps,
                            imageInfo,
                            fullPath,
                            saveType
                        );
                    }
                    else if (saveType == SaveType.Label)
                    {
                        var rightParts = generator.GetRightParts();
                        var costPart = $"{generator.GetCost()} $USD";
                        rightParts.Add(costPart);
                        rightParts.Add("MultiImageClient");
                        using var originalImage = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
                        var label = MakeLabelGeneral(originalImage.Width, result.PromptDetails.Prompt, rightParts);
                        
                        using var labelImage = SixLabors.ImageSharp.Image.Load<Rgba32>(label);
                        int newHeight = originalImage.Height + labelImage.Height;
                        using var combinedImage = new Image<Rgba32>(originalImage.Width, newHeight);
                        combinedImage.Mutate(ctx =>
                        {
                            // Set antialiasing
                            ctx.SetGraphicsOptions(new GraphicsOptions
                            {
                                Antialias = true,
                                AntialiasSubpixelDepth = 16
                            });

                            // Draw original image at top
                            ctx.DrawImage(originalImage, new Point(0, 0), 1f);

                            // Draw label at bottom
                            ctx.DrawImage(labelImage, new Point(0, originalImage.Height), 1f);
                        });

                        // Save the combined image
                        await combinedImage.SaveAsPngAsync(fullPath);
                    }
                    else
                    {
                        await TextFormatting.SaveImageAndAnnotate(
                            imageBytes,
                            usingSteps,
                            imageInfo,
                            fullPath,
                            saveType
                        );
                    }
                    //stats.SavedAnnotatedImageCount++;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"\tError saving {saveType} image: {ex.Message}\r\n{ex}");
            }

            return fullPath;
        }

        public static byte[] MakeLabelGeneral(int width, string prompt, List<string> rightParts)
        {
            var rightSideWidth = 200;
            var leftSideWidth = width - rightSideWidth;
            var padding = 5;
            var fontSize = 24;
            SixLabors.Fonts.FontFamily fontFamily;
            if (!SystemFonts.TryGet("Segoe UI", out fontFamily))
            {
                if (!SystemFonts.TryGet("Arial", out fontFamily))
                {
                    fontFamily = SystemFonts.Families.First(); // Fallback
                }
            }
            var font = fontFamily.CreateFont(fontSize, FontStyle.Regular);

            // Measure left side text to determine height
            var leftTextOptions = new RichTextOptions(font)
            {
                WrappingLength = leftSideWidth - (padding * 2),
                LineSpacing = 1.15f,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Dpi = 72,
                FallbackFontFamilies = new[] { SystemFonts.Families.First() }
            };

            var leftTextBounds = TextMeasurer.MeasureBounds(prompt, leftTextOptions);
            int leftTextHeight = (int)Math.Ceiling(leftTextBounds.Height);

            // Calculate right side font size (scale down if needed to fit width)
            var rightFont = font;
            var rightFontSize = fontSize;

            // Find the maximum width needed for right side text
            foreach (var text in rightParts.Where(s => !string.IsNullOrEmpty(s)))
            {
                var testOptions = new RichTextOptions(rightFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Dpi = 72
                };
                var textBounds = TextMeasurer.MeasureBounds(text, testOptions);

                // If text is too wide, scale down font
                while (textBounds.Width > rightSideWidth - (padding * 2) && rightFontSize > 8)
                {
                    rightFontSize--;
                    rightFont = fontFamily.CreateFont(rightFontSize, FontStyle.Regular);
                    testOptions = new RichTextOptions(rightFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Dpi = 72
                    };
                    textBounds = TextMeasurer.MeasureBounds(text, testOptions);
                }
            }

            // Calculate right side height (number of lines * line height)
            var rightLineHeight = rightFontSize * 1.5f; // Line spacing
            var rightTextHeight = (int)(rightParts.Where(s => !string.IsNullOrEmpty(s)).Count() * rightLineHeight);

            // Overall height is the maximum of left and right sides plus padding
            int contentHeight = Math.Max(leftTextHeight, rightTextHeight);
            int totalHeight = contentHeight + (padding * 2);

            // Create the image
            using var image = new Image<Rgba32>(width, totalHeight);

            image.Mutate(ctx =>
            {
                // Set antialiasing
                ctx.SetGraphicsOptions(new GraphicsOptions
                {
                    Antialias = true,
                    AntialiasSubpixelDepth = 16
                });

                // Fill background with black
                ctx.Fill(Color.Black);

                // Draw left side box border (optional - you can remove if not wanted)
                ctx.Draw(Color.White, 1f, new RectangleF(0, 0, leftSideWidth, totalHeight));

                // Draw left side text
                leftTextOptions.Origin = new PointF(padding, padding);
                ctx.DrawText(leftTextOptions, prompt, Color.White);

                // Draw right side box border (optional - you can remove if not wanted)
                ctx.Draw(Color.White, 1f, new RectangleF(leftSideWidth, 0, rightSideWidth, totalHeight));

                // Draw right side text (gold color, right-aligned)
                var goldColor = Color.FromRgb(255, 215, 0); // Gold color
                var yOffset = padding;

                foreach (var text in rightParts.Where(s => !string.IsNullOrEmpty(s)))
                {
                    var rightTextOptions = new RichTextOptions(rightFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Dpi = 72,
                        Origin = new PointF(width - padding, yOffset)
                    };

                    ctx.DrawText(rightTextOptions, text, goldColor);
                    yOffset += (int)rightLineHeight;
                }
            });

            // Convert to byte array
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static IEnumerable<PromptHistoryStep> GetUsingSteps(SaveType saveType, PromptDetails promptDetails)
        {
            return saveType switch
            {
                SaveType.FullAnnotation => promptDetails.TransformationSteps,
                SaveType.InitialIdea or SaveType.FinalPrompt or SaveType.Raw or SaveType.JustOverride or SaveType.Label => new List<PromptHistoryStep>() { promptDetails.TransformationSteps.First() },
                _ => throw new Exception("Invalid SaveType")
            };
        }

        private static Dictionary<string, string> GetAnnotationDefaultData(
            TaskProcessResult result,
            string fullPath,
            SaveType saveType,
            IImageGenerator generator)
        {
            var imageInfo = new Dictionary<string, string>();
            var promptDetails = result.PromptDetails;

            switch (saveType)
            {
                case SaveType.FullAnnotation:
                    //AddFullAnnotationInfo(imageInfo, result.ImageGenerator, promptDetails, promptGeneratorName, result);
                    imageInfo.Add("Filename", Path.GetFileName(fullPath));
                    break;
                case SaveType.InitialIdea:
                    var initialPrompt = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Initial Prompt", initialPrompt);
                    break;
                case SaveType.FinalPrompt:
                    var finalPrompt = promptDetails.Prompt;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Final Prompt", finalPrompt);
                    break;
                case SaveType.Raw:
                    // No annotation
                    break;
                case SaveType.JustOverride:
                    var initialPrompt2 = promptDetails.TransformationSteps.First().Explanation;
                    imageInfo.Add("Producer", result.ImageGenerator.ToString());
                    imageInfo.Add("Initial Prompt", initialPrompt2);
                    break;
            }

            imageInfo = imageInfo.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                                 .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return imageInfo;
        }
    }
}
