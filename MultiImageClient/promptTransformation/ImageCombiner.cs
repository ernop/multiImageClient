#nullable enable
using IdeogramAPIClient;

using SkiaSharp;

using System.Text.Json;
using System.Text.Json.Serialization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
    // All combined images are saved to disk in dated folders. They are only popped open in the
    // default image viewer when --open-images is set (see ImageCombiner.ViewerPopupsEnabled); runs
    // are headless by default.
    public static class ImageCombiner
    {
        /// Master switch for popping generated/combined images open in the
        /// system default viewer. Defaults to false so runs are headless;
        /// set it from RunOptions.OpenImages (the --open-images flag) at
        /// startup. Every viewer launch in the app funnels through
        /// OpenImageWithDefaultApplication, so this one flag governs all of
        /// them. Per-call openWhenDone arguments still apply on top of this
        /// (a caller can opt a specific image OUT even when popups are on).
        public static bool ViewerPopupsEnabled = false;

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


        /// Loads the job's input image as a grid cell that is visually and
        /// textually distinct from generated outputs: gold frame, black
        /// "INPUT" label, and a caller-supplied role text explaining exactly
        /// what function the image served for each generator. The caller must
        /// state the role — we never guess it here.
        private static LoadedImage LoadInputImageCell(string inputImagePath, string roleText)
        {
            var image = Raster.Decode(inputImagePath);

            var sourceWidth = image.Width;
            var sourceHeight = image.Height;

            if (image.Height > 1300)
            {
                var aspectRatio = (float)image.Width / image.Height;
                var newHeight = 1024;
                var newWidth = (int)(newHeight * aspectRatio);
                Logger.Log($"Resizing input image from {image.Width}x{image.Height} to {newWidth}x{newHeight} for combining");
                image = ReplaceWithResized(image, newWidth, newHeight);
            }

            return new LoadedImage
            {
                Success = true,
                IsInput = true,
                Result = "input",
                Generator = "INPUT",
                PromptText = roleText,
                SourceResolutionLabel = $"{sourceWidth}x{sourceHeight}",
                Image = image,
                Width = image.Width,
                Height = image.Height
            };
        }

        private static IEnumerable<LoadedImage> LoadResultImages(IEnumerable<TaskProcessResult> results)
        {
            var loadedImages = new List<LoadedImage>();

            foreach (var result in results)
            {
                if (!result.IsSuccess)
                {
                    loadedImages.Add(GetPlaceholder(result));
                    continue;
                }

                try
                {
                    var savedPaths = result.GetSavedRawImagePaths();
                    if (savedPaths.Count > 0)
                    {
                        foreach (var path in savedPaths)
                        {
                            if (!File.Exists(path))
                            {
                                throw new FileNotFoundException(
                                    $"Saved raw image required for grid is missing: {path}",
                                    path);
                            }
                            loadedImages.Add(PrepareLoadedResultImage(
                                LoadResultImage(path),
                                result));
                        }
                    }
                    else
                    {
                        var foundBytes = false;
                        foreach (var imageBytes in result.GetAllImages)
                        {
                            if (imageBytes == null || imageBytes.Length == 0)
                            {
                                continue;
                            }
                            foundBytes = true;
                            loadedImages.Add(PrepareLoadedResultImage(
                                Raster.Decode(imageBytes),
                                result));
                        }
                        if (!foundBytes)
                        {
                            throw new InvalidOperationException(
                                $"Successful result from {result.ImageGenerator} has no saved path or image bytes.");
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

            return loadedImages;
        }

        private static SKBitmap LoadResultImage(string path)
        {
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return SvgRaster.Rasterize(path);
            }
            return Raster.Decode(path);
        }

        private static SKBitmap LoadRoundtripOriginal(byte[] bytes)
        {
            var image = Raster.Decode(bytes);
            if (image.Height <= 1024)
            {
                return image;
            }
            var aspectRatio = (float)image.Width / image.Height;
            var newHeight = 1024;
            var newWidth = (int)(newHeight * aspectRatio);
            Logger.Log($"Resizing original image from {image.Width}x{image.Height} to {newWidth}x{newHeight} for combining");
            return ReplaceWithResized(image, newWidth, newHeight);
        }

        // Takes ownership of image and returns its resized replacement.
        private static SKBitmap ReplaceWithResized(SKBitmap image, int width, int height)
        {
            try
            {
                return Raster.Resize(image, width, height);
            }
            finally
            {
                image.Dispose();
            }
        }

        private static LoadedImage PrepareLoadedResultImage(
            SKBitmap image,
            TaskProcessResult result)
        {
            try
            {
                var sourceWidth = image.Width;
                var sourceHeight = image.Height;
                if (image.Height > 1300)
                {
                    var aspectRatio = (float)image.Width / image.Height;
                    var newHeight = 1024;
                    var newWidth = (int)(newHeight * aspectRatio);
                    Logger.Log(
                        $"Resizing image from {image.Width}x{image.Height} "
                        + $"to {newWidth}x{newHeight} for combining");
                    image = ReplaceWithResized(image, newWidth, newHeight);
                }

                return new LoadedImage
                {
                    Success = true,
                    Result = "successX",
                    Generator = GeneratorPresentation.ContactSheetLabel(result),
                    PromptText = GetDisplayPrompt(result),
                    TimingLabel = FormatTiming(result.CreateTotalMs + result.DownloadTotalMs),
                    SourceResolutionLabel = $"{sourceWidth}x{sourceHeight}",
                    Image = image,
                    Width = image.Width,
                    Height = image.Height
                };
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private static SKBitmap RenderHorizontalLayout(IEnumerable<LoadedImage> loadedImages, SheetFont generatorFont)
        {
            int totalWidth = loadedImages.Sum(img => img.Width);
            totalWidth = Math.Max(totalWidth, PlaceholderWidth);

            int maxImageHeight = loadedImages.Select(i => i.Height)
                                             .DefaultIfEmpty(PlaceholderWidth)
                                             .Max();

            var timingFont = FontUtils.CreateFont(Math.Max(8, generatorFont.Size * 0.75f), FontStyle.Regular);
            int subtitleHeight = loadedImages
                .Select(li => MeasureCompositeLabelHeight(
                    li.Generator,
                    BuildSecondaryLabel(li),
                    generatorFont,
                    timingFont,
                    Math.Max(1, li.Width - UIConstants.Padding * 2)))
                .DefaultIfEmpty(0)
                .Max();
            int subtitleBlockHeight = Math.Max(UIConstants.Padding * 2, subtitleHeight + UIConstants.Padding * 2);
            int totalHeight = maxImageHeight + subtitleBlockHeight;

            var layoutImage = ImageUtils.CreateStandardImage(totalWidth, totalHeight, UIConstants.White);

            using (var canvas = new SKCanvas(layoutImage))
            {
                int currentX = 0;

                foreach (var li in loadedImages)
                {
                    if (li.Success && li.Image != null)
                    {
                        canvas.DrawBitmapAt(li.Image, currentX, 0);
                    }
                    else
                    {
                        var rect = SKRect.Create(currentX, 0, li.Width, maxImageHeight);
                        var errorText = li.Result;
                        canvas.DrawErrorPlaceholder(rect, errorText, generatorFont);
                    }

                    float centerX = currentX + li.Width / 2f;
                    float labelY = maxImageHeight + UIConstants.Padding;

                    var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                    DrawCompositeLabelLine(
                        canvas,
                        li.Generator,
                        BuildSecondaryLabel(li),
                        generatorFont,
                        timingFont,
                        labelColor,
                        centerX,
                        labelY,
                        Math.Max(1, li.Width - UIConstants.Padding * 2));

                    currentX += li.Width;
                }
            }

            return layoutImage;
        }



        /// Renders a generator label with a clear typographic hierarchy:
        ///
        ///   [primary font (bold)] gpt-image-2   [secondary font] medium  square  1m 4s
        ///
        /// When it fits, the first whitespace-delimited token of
        /// <paramref name="fullLabel"/> stays in the primary font and the rest
        /// plus timing/resolution sits beside it in the secondary font on one
        /// shared baseline. If that complete line would exceed the cell, the
        /// full producer label wraps above a separately wrapped secondary
        /// line. Nothing is clipped or abbreviated.
        private static void DrawCompositeLabelLine(
            SKCanvas canvas,
            string fullLabel,
            string? timingLabel,
            SheetFont primaryFont,
            SheetFont secondaryFont,
            SKColor color,
            float centerX,
            float labelY,
            float availableWidth)
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
            primaryMeasureOpts.Origin = new SKPoint(0, 0);
            var primaryBounds = string.IsNullOrEmpty(primary)
                ? SKRect.Empty
                : TextLayout.Measure(primary, primaryMeasureOpts);

            float gap = secondaryPieces.Count == 0 || string.IsNullOrEmpty(primary)
                ? 0f
                : Math.Max(8f, primaryFont.Size * 0.6f);

            float secondaryWidth = 0f;
            SKRect secondaryBounds = SKRect.Empty;
            if (!string.IsNullOrEmpty(secondary))
            {
                var secondaryMeasureOpts = FontUtils.CreateTextOptions(secondaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                secondaryMeasureOpts.Origin = new SKPoint(0, 0);
                secondaryBounds = TextLayout.Measure(secondary, secondaryMeasureOpts);
                secondaryWidth = secondaryBounds.Width;
            }

            float totalWidth = primaryBounds.Width + gap + secondaryWidth;
            if (totalWidth > availableWidth)
            {
                var wrappedPrimary = FontUtils.CreateTextOptions(
                    primaryFont,
                    HorizontalAlignment.Center,
                    VerticalAlignment.Top,
                    UIConstants.LineSpacing);
                wrappedPrimary.Origin = new SKPoint(centerX, labelY);
                wrappedPrimary.WrappingLength = availableWidth;
                canvas.DrawTextStandard(wrappedPrimary, trimmed, color);

                if (!string.IsNullOrWhiteSpace(timingLabel))
                {
                    var primaryHeight = ImageUtils.MeasureTextHeight(
                        trimmed,
                        primaryFont,
                        UIConstants.LineSpacing,
                        availableWidth);
                    var descenderReserve = (int)Math.Ceiling(
                        primaryFont.Size * LabelDescenderReserveFraction);
                    var wrappedSecondary = FontUtils.CreateTextOptions(
                        secondaryFont,
                        HorizontalAlignment.Center,
                        VerticalAlignment.Top,
                        UIConstants.LineSpacing);
                    wrappedSecondary.Origin = new SKPoint(
                        centerX,
                        labelY + primaryHeight + descenderReserve);
                    wrappedSecondary.WrappingLength = availableWidth;
                    canvas.DrawTextStandard(wrappedSecondary, timingLabel, color);
                }
                return;
            }

            float startX = centerX - totalWidth / 2f;

            // labelY is the top of the primary line; the secondary text sits
            // on the primary's baseline.
            var primaryDrawOpts = FontUtils.CreateTextOptions(primaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
            primaryDrawOpts.Origin = new SKPoint(startX, labelY);
            float baseline = labelY + TextLayout.BaselineOffset(primaryDrawOpts);
            if (!string.IsNullOrEmpty(primary))
            {
                canvas.DrawTextStandard(primaryDrawOpts, primary, color);
            }

            if (!string.IsNullOrEmpty(secondary))
            {
                var secondaryDrawOpts = FontUtils.CreateTextOptions(secondaryFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                secondaryDrawOpts.Origin = new SKPoint(
                    startX + primaryBounds.Width + gap,
                    baseline - TextLayout.BaselineOffset(secondaryDrawOpts));
                canvas.DrawTextStandard(secondaryDrawOpts, secondary, color);
            }
        }

        private static SKBitmap RenderSquareLayout(
            IEnumerable<LoadedImage> loadedImages,
            SheetFont generatorFont,
            SheetFont? promptFont = null,
            bool includeCellPrompts = false)
        {
            var images = loadedImages.ToList();
            int totalImages = images.Count;

            if (totalImages == 0)
            {
                return RenderEmptyLayout();
            }

            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(totalImages)));
            int rows = (int)Math.Ceiling(totalImages / (double)columns);

            var columnWidths = new int[columns];
            var rowHeights = new int[rows];

            var index = 0;
            foreach (var li in images)
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

            var timingFont = FontUtils.CreateFont(Math.Max(8, generatorFont.Size * 0.75f), FontStyle.Regular);
            var cellPromptFont = promptFont ?? FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);
            var rowTextHeights = new int[rows];

            for (var i = 0; i < images.Count; i++)
            {
                var row = i / columns;
                var col = i % columns;
                var textHeight = MeasureCellTextHeight(
                    images[i],
                    Math.Max(columnWidths[col], 1),
                    generatorFont,
                    timingFont,
                    cellPromptFont,
                    includeCellPrompts || images[i].IsInput);
                rowTextHeights[row] = Math.Max(rowTextHeights[row], textHeight);
            }

            int layoutHeight = rowHeights.Sum() + rowTextHeights.Sum();
            var layoutImage = ImageUtils.CreateStandardImage(layoutWidth, layoutHeight, UIConstants.White);

            using (var canvas = new SKCanvas(layoutImage))
            {
                int currentY = 0;
                int imageIndex = 0;

                for (int row = 0; row < rows; row++)
                {
                    int rowHeight = rowHeights[row];
                    int textBandY = currentY + rowHeight;

                    for (int col = 0; col < columns; col++)
                    {
                        if (imageIndex >= totalImages)
                        {
                            break;
                        }

                        var li = images[imageIndex];
                        int columnWidth = columnWidths[col];
                        int columnX = columnOffsets[col];

                        int imageX = columnX + Math.Max(0, (columnWidth - li.Width) / 2);
                        int imageY = currentY;

                        if (li.Success && li.Image != null)
                        {
                            canvas.DrawBitmapAt(li.Image, imageX, imageY);
                            if (li.IsInput)
                            {
                                // Gold frame so the input image can never be
                                // mistaken for a generated output.
                                float borderWidth = 6f;
                                float inset = borderWidth / 2f;
                                canvas.StrokeRect(
                                    SKRect.Create(imageX + inset, imageY + inset, li.Width - borderWidth, li.Height - borderWidth),
                                    UIConstants.Gold,
                                    borderWidth);
                            }
                        }
                        else
                        {
                            var rect = SKRect.Create(columnX, imageY, Math.Max(columnWidth, 1), Math.Max(rowHeight, 1));
                            var errorText = li.Result;
                            canvas.DrawErrorPlaceholder(rect, errorText, generatorFont);
                        }

                        float centerX = columnX + Math.Max(columnWidth, 1) / 2f;
                        var labelColor = li.IsInput
                            ? UIConstants.Black
                            : li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                        var labelY = textBandY + UIConstants.Padding;
                        DrawCompositeLabelLine(
                            canvas,
                            li.Generator,
                            BuildSecondaryLabel(li),
                            generatorFont,
                            timingFont,
                            labelColor,
                            centerX,
                            labelY,
                            Math.Max(1, columnWidth - UIConstants.Padding * 2));
                        // Input cells always render their role text so the
                        // image's function in this job is stated on the sheet
                        // itself, regardless of the per-cell prompt setting.
                        if ((includeCellPrompts || li.IsInput) && !string.IsNullOrWhiteSpace(li.PromptText))
                        {
                            var wrappingWidth = Math.Max(1, columnWidth - UIConstants.Padding * 2);
                            var promptY = labelY
                                + MeasureCellLabelHeight(li, columnWidth, generatorFont, timingFont)
                                + UIConstants.Padding / 2f;
                            var promptOpts = FontUtils.CreateTextOptions(cellPromptFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                            promptOpts.Origin = new SKPoint(columnX + UIConstants.Padding, promptY);
                            promptOpts.WrappingLength = wrappingWidth;
                            canvas.DrawTextStandard(promptOpts, li.PromptText, UIConstants.Black);
                        }

                        imageIndex++;
                    }

                    currentY += rowHeight + rowTextHeights[row];
                }
            }

            return layoutImage;
        }

        public static async Task<string> CreateBatchLayoutImageHorizontalAsync(IEnumerable<TaskProcessResult> results, string prompt, Settings settings)
        {
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            var loadedImages = LoadResultImages(results).ToList();
            try
            {
                using var layoutImage = RenderHorizontalLayout(loadedImages, generatorFont);
                using var promptPanel = RenderPromptPanel(layoutImage.Width, prompt, promptFont);
                int totalHeight = layoutImage.Height + promptPanel.Height;
                using var combinedImage = ImageUtils.CreateStandardImage(
                    layoutImage.Width,
                    totalHeight,
                    UIConstants.White);

                using (var canvas = new SKCanvas(combinedImage))
                {
                    canvas.DrawBitmapAt(layoutImage, 0, 0);
                    canvas.DrawBitmapAt(promptPanel, 0, layoutImage.Height);
                }

                return await SaveCombinedImageToDisk(combinedImage, prompt, settings);
            }
            finally
            {
                foreach (var li in loadedImages)
                {
                    li.Image?.Dispose();
                }
            }
        }

        // openWhenDone controls whether the combined grid image is popped
        // open in the system default viewer after it's written. Defaults to
        // false: nothing auto-opens. Callers that explicitly want a viewer
        // popup (none currently) can pass true.
        //
        // inputImagePath/inputImageRole: when the job carried an input image,
        // pass its path plus a human-readable statement of what function it
        // served (per generator). It renders as the FIRST cell of the grid,
        // gold-framed and labeled INPUT with the role text underneath, so the
        // sheet documents the complete job. Both must be provided together —
        // we refuse to show an input image without stating its function.
        public static async Task<string> CreateBatchLayoutImageSquareAsync(
            IEnumerable<TaskProcessResult> results,
            string prompt,
            Settings settings,
            bool openWhenDone = false,
            bool showPerImagePrompts = false,
            string? inputImagePath = null,
            string? inputImageRole = null)
        {
            var generatorFont = FontUtils.CreateFont(CombinedImageGeneratorFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);

            var loadedImages = LoadResultImages(results).ToList();
            try
            {
                if (!string.IsNullOrEmpty(inputImagePath))
                {
                    if (string.IsNullOrWhiteSpace(inputImageRole))
                    {
                        throw new ArgumentException(
                            "inputImageRole is required when inputImagePath is provided: the sheet must state what the input image was used for.",
                            nameof(inputImageRole));
                    }
                    loadedImages.Insert(0, LoadInputImageCell(inputImagePath, inputImageRole));
                }

                using var layoutImage = RenderSquareLayout(
                    loadedImages,
                    generatorFont,
                    promptFont,
                    showPerImagePrompts);
                using var promptPanel = RenderPromptPanel(layoutImage.Width, prompt, promptFont);
                int totalHeight = layoutImage.Height + promptPanel.Height;
                using var combinedImage = ImageUtils.CreateStandardImage(
                    layoutImage.Width,
                    totalHeight,
                    UIConstants.White);

                using (var canvas = new SKCanvas(combinedImage))
                {
                    canvas.DrawBitmapAt(layoutImage, 0, 0);
                    canvas.DrawBitmapAt(promptPanel, 0, layoutImage.Height);
                }

                var outputPath = await SaveCombinedImageToDisk(combinedImage, prompt, settings);
                if (openWhenDone)
                {
                    OpenImageWithDefaultApplication(outputPath);
                }
                return outputPath;
            }
            finally
            {
                foreach (var li in loadedImages)
                {
                    li.Image?.Dispose();
                }
            }
        }


        /// One cell of a goal-loop sheet: a rendered turn. LoadImage null =
        /// the render failed (Error is drawn in the placeholder); otherwise it
        /// returns the original bytes, which are decoded, capped, and dropped
        /// before the next cell loads. Primary is the bold first token of the
        /// label (the score, so the number is the most legible element of the
        /// band); Secondary follows it smaller.
        public sealed record LoopSheetCell(
            Func<CancellationToken, Task<byte[]>>? LoadImage,
            string Primary,
            string? Secondary,
            string? Text,
            string? Error,
            int PlaceholderWidth,
            int PlaceholderHeight);

        // Loop sheet: a header band (goal, parties, outcome) above a square
        // grid of every rendered turn in order, each with its score band and
        // the exact prompt it rendered plus the manager's assessment. Every
        // image is decoded, capped to a 1024 px long edge, drawn, and
        // disposed inside this call; nothing stays resident. The caller
        // serializes builds (one at a time per process).
        public static async Task CreateGoalLoopSheetAsync(
            IReadOnlyList<LoopSheetCell> cells,
            string headerText,
            string outputPath,
            CancellationToken ct = default)
        {
            var generatorFont = FontUtils.CreateFont(GeneratedImageLabelFontSize, FontStyle.Bold);
            var promptFont = FontUtils.CreateFont(CombinedImagePromptFontSize, FontStyle.Regular);
            var headerFont = FontUtils.CreateFont(GeneratedImageLabelFontSize, FontStyle.Regular);
            var loaded = new List<LoadedImage>(cells.Count);
            try
            {
                foreach (var cell in cells)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cell.LoadImage == null)
                    {
                        loaded.Add(new LoadedImage
                        {
                            Success = false,
                            Result = cell.Error ?? "render failed",
                            Generator = cell.Primary,
                            SourceResolutionLabel = cell.Secondary,
                            PromptText = cell.Text,
                            Width = Math.Max(PlaceholderWidth, cell.PlaceholderWidth),
                            Height = Math.Max(PlaceholderWidth, cell.PlaceholderHeight),
                        });
                        continue;
                    }
                    var bytes = await cell.LoadImage(ct);
                    var info = Raster.Identify(bytes);
                    var longEdge = Math.Max(info.Width, info.Height);
                    SKBitmap image;
                    if (longEdge > 1300)
                    {
                        var scale = 1024.0 / longEdge;
                        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
                        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
                        image = ReplaceWithResized(Raster.DecodeForBox(bytes, width, height), width, height);
                    }
                    else
                    {
                        image = Raster.Decode(bytes);
                    }
                    loaded.Add(new LoadedImage
                    {
                        Success = true,
                        Result = "ok",
                        Generator = cell.Primary,
                        SourceResolutionLabel = cell.Secondary,
                        PromptText = cell.Text,
                        Image = image,
                        Width = image.Width,
                        Height = image.Height,
                    });
                }

                using var layoutImage = RenderSquareLayout(loaded, generatorFont, promptFont, includeCellPrompts: true);
                using var header = RenderPromptPanel(layoutImage.Width, headerText, headerFont);
                using var combined = ImageUtils.CreateStandardImage(
                    layoutImage.Width, header.Height + layoutImage.Height, UIConstants.White);
                using (var canvas = new SKCanvas(combined))
                {
                    canvas.DrawBitmapAt(header, 0, 0);
                    canvas.DrawBitmapAt(layoutImage, 0, header.Height);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var tmp = outputPath + ".tmp";
                Raster.SavePng(combined, tmp);
                File.Move(tmp, outputPath, overwrite: true);
            }
            finally
            {
                foreach (var li in loaded)
                {
                    li.Image?.Dispose();
                }
            }
        }

        // this is how werender the "roundtrip workflow". In this case, the user provided an image to us.
        // we used an LLM to describe it, then we sent that description to a bunch of images.
        // the output format should be a long horizontal strip. Leftmost should be the image, labelled like "original iamge".
        // then to the right of that,  how it was described, in descriptionText taking up a big square column (sine this text might be long).
        // then further to the right, in order, just like we did in RenderHorizontalLayout, should be all the output images ortheir error placeholders.
        public async static Task<string> CreateRoundtripLayoutImageAsync(byte[] originalImageBytes, IEnumerable<TaskProcessResult> results, string descriptionText, string descriptionInstructions, string describerModelName, Settings settings)
        {
            var labelFont = FontUtils.CreateFont(36, FontStyle.Bold); // Large font for labels
            using var originalImage = LoadRoundtripOriginal(originalImageBytes);

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

            using var layoutImage = ImageUtils.CreateStandardImage(totalWidth, totalHeight, UIConstants.White);

            using (var canvas = new SKCanvas(layoutImage))
            {
                float currentX = 0;

                // 1. Draw Original Image
                canvas.DrawBitmapAt(originalImage!, (int)currentX, 0);
                var labelOptsOriginal = new TextBlockOptions(labelFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                labelOptsOriginal.Dpi = UIConstants.TextDpi; // Ensure same DPI as measurement!
                labelOptsOriginal.Origin = new SKPoint(currentX + originalImage!.Width / 2f, maxImageHeight + UIConstants.Padding);
                canvas.DrawTextStandard(labelOptsOriginal, "Original Image", SKColors.Black);
                currentX += originalImage.Width;

                // 2a. Draw describer instructions
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
                    var instructionsTextOptions = new TextBlockOptions(instructionsFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        WrappingLength = instrWrappingWidth,
                        Origin = new SKPoint(currentX + UIConstants.Padding, UIConstants.Padding),
                        Dpi = UIConstants.TextDpi
                    };
                    
                    // Draw background and text for describer instructions
                    canvas.FillRect(SKRect.Create(currentX, 0, instructionsWidth, instructionsHeight), SKColors.LightBlue);
                    canvas.DrawTextStandard(instructionsTextOptions, descriptionInstructions, SKColors.Black);
                    
                    // Draw label for describer instructions
                    var instructionsLabelOpts = new TextBlockOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    instructionsLabelOpts.Dpi = UIConstants.TextDpi;
                    instructionsLabelOpts.Origin = new SKPoint(currentX + instructionsWidth / 2f, maxImageHeight + UIConstants.Padding);
                    canvas.DrawTextStandard(instructionsLabelOpts, $"{describerModelName} Instructions", SKColors.Black);
                }
                else
                {
                    // Draw placeholder if no instructions
                    canvas.FillRect(SKRect.Create(currentX, 0, instructionsWidth, instructionsHeight), SKColors.LightGray);
                    var noInstructionsFont = FontUtils.CreateFont(18, FontStyle.Regular);
                    var noInstructionsTextOptions = new TextBlockOptions(noInstructionsFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Dpi = UIConstants.TextDpi
                    };
                    noInstructionsTextOptions.Origin = new SKPoint(currentX + instructionsWidth / 2f, instructionsHeight / 2f);
                    canvas.DrawTextStandard(noInstructionsTextOptions, "No Instructions", SKColors.Black);
                    
                    // Draw label even when no instructions
                    var instructionsLabelOpts = new TextBlockOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    instructionsLabelOpts.Dpi = UIConstants.TextDpi;
                    instructionsLabelOpts.Origin = new SKPoint(currentX + instructionsWidth / 2f, instructionsHeight + UIConstants.Padding);
                    canvas.DrawTextStandard(instructionsLabelOpts, $"{describerModelName} Instructions", SKColors.Black);
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

                // The current sizing path estimates wrapped multiline height
                // from width and newline count. Replace it when a
                // rectangle-aware measurement API is available.

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
                var descriptionTextOptions = new TextBlockOptions(descriptionFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    WrappingLength = wrappingWidth,
                    Origin = new SKPoint(currentX + UIConstants.Padding, UIConstants.Padding),
                    Dpi = UIConstants.TextDpi  // Ensure same DPI as measurement!
                };


                canvas.FillRect(SKRect.Create(currentX, 0, descriptionWidth, descriptionHeight), SKColors.LightGray); // Background for text
                canvas.DrawTextStandard(descriptionTextOptions, descriptionText, SKColors.Black);

                var labelOptsDescription = new TextBlockOptions(labelFont)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                labelOptsDescription.Dpi = UIConstants.TextDpi; // Ensure same DPI as measurement!
                labelOptsDescription.Origin = new SKPoint(currentX + descriptionWidth / 2f, maxImageHeight + UIConstants.Padding);
                canvas.DrawTextStandard(labelOptsDescription, $"{describerModelName} Description", SKColors.Black);
                currentX += descriptionWidth;



                // 3. Draw Loaded Images with their labels
                foreach (var li in loadedImages)
                {
                    // Calculate vertical offset to align bottom of image with maxImageHeight
                    var imageYOffset = maxImageHeight - li.Height;
                    
                    if (li.Image != null)
                    {
                        canvas.DrawBitmapAt(li.Image, (int)currentX, imageYOffset);
                    }
                    else
                    {
                        // Draw placeholder for error images
                        canvas.FillRect(SKRect.Create((int)currentX, imageYOffset, li.Width, li.Height), SKColors.LightGray);
                        var errorTextOptions = new TextBlockOptions(labelFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Dpi = UIConstants.TextDpi // Ensure same DPI as measurement!
                        };
                        errorTextOptions.Origin = new SKPoint(currentX + li.Width / 2f, imageYOffset + li.Height / 2f);
                        canvas.DrawTextStandard(errorTextOptions, "ERROR", SKColors.Red);
                    }

                    var labelText = li.Result ?? "";
                    
                    var labelOpts = new TextBlockOptions(labelFont)
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        WrappingLength = li.Width - 10, // Allow wrapping within image bounds
                        Dpi = UIConstants.TextDpi  // Ensure same DPI as measurement!
                    };
                    labelOpts.Origin = new SKPoint(currentX + li.Width / 2f, maxImageHeight + UIConstants.Padding);

                    var labelColor = li.Success ? UIConstants.SuccessGreen : UIConstants.ErrorRed;
                    canvas.DrawTextStandard(labelOpts, labelText, labelColor);

                    currentX += li.Width;
                }


               
            }
            var outputPath = await SaveCombinedImageToDisk(layoutImage, descriptionText, settings);

            foreach (var li in loadedImages)
            {
                li.Image?.Dispose();
            }

            return outputPath;
        }
        private static SKBitmap RenderPromptPanel(int width, string prompt, SheetFont promptFont)
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

            using (var canvas = new SKCanvas(promptPanel))
            {
                var promptOpts = FontUtils.CreateTextOptions(promptFont, HorizontalAlignment.Left, VerticalAlignment.Top, UIConstants.LineSpacing);
                promptOpts.Origin = new SKPoint(UIConstants.Padding * 2, topPadding);
                promptOpts.WrappingLength = wrappingWidth;

                canvas.DrawTextStandard(promptOpts, prompt, UIConstants.Black);
            }

            return promptPanel;
        }

        
        public static void OpenImageWithDefaultApplication(string imagePath)
        {
            // Headless by default: only pop the viewer when the user opted in
            // via --open-images. This is the single chokepoint for every
            // viewer launch in the app.
            if (!ViewerPopupsEnabled)
            {
                return;
            }

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
                Generator = GeneratorPresentation.ContactSheetLabel(result),
                PromptText = GetDisplayPrompt(result),
                TimingLabel = FormatTiming(result.CreateTotalMs + result.DownloadTotalMs),
                SourceResolutionLabel = $"{width}x{height}",
                Width = width,
                Height = height
            };
        }

        private static string? BuildSecondaryLabel(LoadedImage loadedImage)
        {
            var parts = new[]
            {
                loadedImage.TimingLabel,
                loadedImage.SourceResolutionLabel,
            }.Where(s => !string.IsNullOrWhiteSpace(s));
            var label = string.Join("  ", parts);
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }

        private static int MeasureCellTextHeight(
            LoadedImage loadedImage,
            int columnWidth,
            SheetFont generatorFont,
            SheetFont timingFont,
            SheetFont promptFont,
            bool includePrompt)
        {
            var labelHeight = MeasureCellLabelHeight(
                loadedImage,
                columnWidth,
                generatorFont,
                timingFont);
            var promptHeight = 0;
            if (includePrompt && !string.IsNullOrWhiteSpace(loadedImage.PromptText))
            {
                var wrappingWidth = Math.Max(1, columnWidth - UIConstants.Padding * 2);
                promptHeight = ImageUtils.MeasureTextHeight(loadedImage.PromptText, promptFont, UIConstants.LineSpacing, wrappingWidth)
                    + UIConstants.Padding / 2;
            }

            return Math.Max(
                UIConstants.Padding * 2,
                UIConstants.Padding + labelHeight + promptHeight + UIConstants.Padding);
        }

        private static int MeasureCellLabelHeight(
            LoadedImage loadedImage,
            int columnWidth,
            SheetFont generatorFont,
            SheetFont timingFont)
        {
            var wrappingWidth = Math.Max(1, columnWidth - UIConstants.Padding * 2);
            return MeasureCompositeLabelHeight(
                loadedImage.Generator,
                BuildSecondaryLabel(loadedImage),
                generatorFont,
                timingFont,
                wrappingWidth);
        }

        private static int MeasureCompositeLabelHeight(
            string fullLabel,
            string? secondaryLabel,
            SheetFont primaryFont,
            SheetFont secondaryFont,
            float availableWidth)
        {
            var trimmed = (fullLabel ?? string.Empty).Trim();
            var firstSpace = trimmed.IndexOf(' ');
            var primary = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
            var remainder = firstSpace < 0 ? "" : trimmed[(firstSpace + 1)..].Trim();
            var secondary = string.Join(
                "  ",
                new[] { remainder, secondaryLabel }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

            var primaryOptions = FontUtils.CreateTextOptions(
                primaryFont,
                HorizontalAlignment.Left,
                VerticalAlignment.Top,
                UIConstants.LineSpacing);
            var primaryBounds = string.IsNullOrEmpty(primary)
                ? SKRect.Empty
                : TextLayout.Measure(primary, primaryOptions);
            var secondaryOptions = FontUtils.CreateTextOptions(
                secondaryFont,
                HorizontalAlignment.Left,
                VerticalAlignment.Top,
                UIConstants.LineSpacing);
            var secondaryBounds = string.IsNullOrEmpty(secondary)
                ? SKRect.Empty
                : TextLayout.Measure(secondary, secondaryOptions);
            var gap = string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(secondary)
                ? 0f
                : Math.Max(8f, primaryFont.Size * 0.6f);
            var descenderReserve = (int)Math.Ceiling(
                primaryFont.Size * LabelDescenderReserveFraction);

            if (primaryBounds.Width + gap + secondaryBounds.Width <= availableWidth)
            {
                return (int)Math.Ceiling(Math.Max(
                    primaryBounds.Height,
                    secondaryBounds.Height)) + descenderReserve;
            }

            var primaryHeight = ImageUtils.MeasureTextHeight(
                trimmed,
                primaryFont,
                UIConstants.LineSpacing,
                availableWidth) + descenderReserve;
            if (string.IsNullOrWhiteSpace(secondaryLabel))
            {
                return primaryHeight;
            }
            var secondaryDescenderReserve = (int)Math.Ceiling(
                secondaryFont.Size * LabelDescenderReserveFraction);
            return primaryHeight
                + ImageUtils.MeasureTextHeight(
                    secondaryLabel,
                    secondaryFont,
                    UIConstants.LineSpacing,
                    availableWidth)
                + secondaryDescenderReserve;
        }

        private static string? GetDisplayPrompt(TaskProcessResult result)
        {
            return result.PromptDetails?.TransformationSteps.FirstOrDefault()?.Prompt
                ?? result.PromptDetails?.Prompt;
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

        private static SKBitmap RenderEmptyLayout()
        {
            var empty = ImageUtils.CreateStandardImage(PlaceholderWidth, PlaceholderWidth + UIConstants.Padding * 2, UIConstants.White);

            using (var canvas = new SKCanvas(empty))
            {
                var font = FontUtils.CreateFont(LabelFontSize, FontStyle.Regular);
                var opts = FontUtils.CreateTextOptions(font, HorizontalAlignment.Center, VerticalAlignment.Center, UIConstants.LineSpacing);
                opts.Origin = new SKPoint(empty.Width / 2f, empty.Height / 2f);
                canvas.DrawTextStandard(opts, "No images", UIConstants.ErrorRed);
            }

            return empty;
        }


        private static async Task<string> SaveCombinedImageToDisk(SKBitmap image, string prompt, Settings settings)
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

            await Task.Run(() => Raster.SavePng(image, outputPath));
            Logger.Log($"Combined image saved: {outputPath}");
            DlMirror.Copy(outputPath, settings.FlatImageMirrorPath);

            return outputPath;
        }

        public class LoadedImage
        {
            public bool Success { get; set; }
            /// True for the job's input image cell (gold-framed, labeled
            /// INPUT, role text always rendered) as opposed to a generated
            /// output.
            public bool IsInput { get; set; }
            public string? Result { get; set; }
            public required string Generator { get; set; }
            public string? PromptText { get; set; }
            /// Preformatted total generation time (e.g. "2m 15s"), or
            /// null if we have no usable timing for this result.
            public string? TimingLabel { get; set; }
            public string? SourceResolutionLabel { get; set; }
            public SKBitmap? Image { get; set; }
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
