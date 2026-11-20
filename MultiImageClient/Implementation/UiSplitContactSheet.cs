#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SkiaSharp;

namespace MultiImageClient
{
    public sealed record UiSplitContactSheetCell(
        string GeneratorKey,
        int PartIndex,
        bool Ok,
        string Label,
        string Error,
        int ElapsedMs,
        string RecordedSize,
        int ImageCount);

    public sealed record UiSplitContactSheetPart(
        int Index,
        int Count,
        string JobId,
        string Title,
        string Prompt,
        IReadOnlyList<UiSplitContactSheetCell> Cells);

    public sealed record UiSplitContactSheetData(
        string GroupId,
        string ExchangeId,
        string ModelLabel,
        string Reason,
        string SourcePrompt,
        string StyleNote,
        int InputCount,
        string InputRoleText,
        IReadOnlyList<string> GeneratorKeys,
        IReadOnlyDictionary<string, string> GeneratorLabels,
        IReadOnlyDictionary<string, string> GeneratorExtraTexts,
        IReadOnlyList<UiSplitContactSheetPart> Parts);

    // Builds one combined contact sheet PNG for an entire multi-page split
    // series (all generators x all pages, or one selected generator's pages),
    // with or without full prompt text. Uses a single-canvas two-pass renderer
    // inside PixelWorkGate and reduced-size codec decoding so memory stays
    // bounded on the shared host.
    public static class UiSplitContactSheet
    {
        private const int Margin = 44;
        private const int Gap = 20;
        private const int SubGap = 8;
        private const int CardPad = 20;
        private const int MaxPixels = 32_000_000;
        private const int MaxSectionChars = 100_000;
        private const int MinCellEdge = 256;
        private const int InputMaxEdge = 260;

        private static readonly SKColor Ink = SKColors.Black;
        private static readonly SKColor Accent = SKColor.Parse("075787");
        private static readonly SKColor ErrorRed = SKColor.Parse("A22222");
        private static readonly SKColor BandFill = SKColor.Parse("E8EEFA");
        private static readonly SKColor PanelFill = SKColor.Parse("F3F7FA");
        private static readonly SKColor ErrorFill = SKColor.Parse("FDECEA");
        private static readonly SKColor BorderColor = SKColor.Parse("B8C8D8");
        private static readonly SKColor DividerColor = SKColor.Parse("7A8FA6");

        public static UiSplitContactSheetData Read(
            IReadOnlyList<UiJob> parts,
            Func<UiJob, (List<string> Events, bool Done)> readEvents,
            Func<IReadOnlyList<string>, string> inputRoleTextBuilder,
            string sourcePrompt,
            string styleNote,
            string? filterGeneratorKey = null)
        {
            if (parts == null || parts.Count < 2)
                throw new InvalidDataException("A series contact sheet requires at least two split parts.");

            var firstSplit = parts[0].Split
                ?? throw new InvalidDataException("The first job has no split metadata.");
            var expectedCount = firstSplit.Count;
            if (expectedCount < 2 || parts.Count != expectedCount)
                throw new InvalidDataException($"The split series has {parts.Count} recorded part(s), but its plan expects {expectedCount}.");

            var orderedParts = parts.OrderBy(p => p.Split?.Index ?? 0).ToList();
            for (var i = 0; i < expectedCount; i++)
            {
                var split = orderedParts[i].Split
                    ?? throw new InvalidDataException($"Part {i + 1} is missing split metadata.");
                if (!string.Equals(split.GroupId, firstSplit.GroupId, StringComparison.Ordinal)
                    || !string.Equals(split.ExchangeId, firstSplit.ExchangeId, StringComparison.Ordinal)
                    || split.Count != expectedCount
                    || split.Index != i + 1)
                {
                    throw new InvalidDataException("The split series parts do not form a complete contiguous sequence.");
                }
            }

            var allImageKeys = orderedParts[0].GeneratorKeys
                .Where(k => !UiJobRunner.IsAnalysisKey(k)
                    && !string.Equals(k, UiJobRunner.KeyGrokWebVideo, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (allImageKeys.Length == 0 || allImageKeys.Distinct(StringComparer.Ordinal).Count() != allImageKeys.Length)
                throw new InvalidDataException("The split series has no unique image generator targets.");

            foreach (var part in orderedParts.Skip(1))
            {
                var partImageKeys = part.GeneratorKeys
                    .Where(k => !UiJobRunner.IsAnalysisKey(k)
                        && !string.Equals(k, UiJobRunner.KeyGrokWebVideo, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!partImageKeys.SequenceEqual(allImageKeys, StringComparer.Ordinal))
                    throw new InvalidDataException("The split series parts do not share the same image generator set.");
            }

            string[] selectedKeys;
            if (!string.IsNullOrWhiteSpace(filterGeneratorKey))
            {
                var trimmed = filterGeneratorKey.Trim();
                if (!allImageKeys.Contains(trimmed, StringComparer.Ordinal))
                    throw new ArgumentException($"Generator '{trimmed}' is not part of this split series.", nameof(filterGeneratorKey));
                selectedKeys = new[] { trimmed };
            }
            else
            {
                selectedKeys = allImageKeys;
            }

            var generatorLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            var generatorExtraTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            var partModels = new List<UiSplitContactSheetPart>(expectedCount);

            foreach (var job in orderedParts)
            {
                var split = job.Split!;
                var (events, done) = readEvents(job);
                if (!done)
                    throw new InvalidOperationException("Wait for all parts of this series to finish before making its contact sheet.");

                var resultsByGen = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var json in events)
                {
                    using var doc = JsonDocument.Parse(json);
                    var evt = doc.RootElement;
                    if (!evt.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    if (type == "accepted" && evt.TryGetProperty("generatorExtraTexts", out var extraTexts))
                    {
                        foreach (var entry in extraTexts.EnumerateObject())
                        {
                            if (!selectedKeys.Contains(entry.Name, StringComparer.Ordinal)) continue;
                            var value = entry.Value.GetString() ?? "";
                            if (value.Length > 0 && !generatorExtraTexts.ContainsKey(entry.Name))
                                generatorExtraTexts[entry.Name] = value;
                        }
                    }
                    if (type != "gen-result") continue;
                    var gen = evt.GetProperty("gen").GetString() ?? "";
                    if (!selectedKeys.Contains(gen, StringComparer.Ordinal)) continue;
                    if (!resultsByGen.TryAdd(gen, evt.Clone()))
                        throw new InvalidDataException($"Part {split.Index} recorded multiple results for {gen}.");
                }

                var cells = new List<UiSplitContactSheetCell>(selectedKeys.Length);
                foreach (var gen in selectedKeys)
                {
                    if (!resultsByGen.TryGetValue(gen, out var evt))
                        throw new InvalidDataException($"Part {split.Index} has no completed result for {gen}.");
                    var label = evt.TryGetProperty("label", out var labelEl) ? (labelEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(label))
                        label = GeneratorPresentation.UiContactSheetLabel(gen);
                    if (!generatorLabels.ContainsKey(gen))
                        generatorLabels[gen] = label;

                    var ok = evt.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                    var ms = evt.TryGetProperty("ms", out var msEl) && msEl.TryGetInt32(out var msVal) ? msVal : 0;
                    var size = evt.TryGetProperty("size", out var sizeEl) ? (sizeEl.GetString() ?? "") : "";
                    if (!ok)
                    {
                        var error = evt.TryGetProperty("error", out var errEl) ? (errEl.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(error))
                            throw new InvalidDataException($"Part {split.Index} failed for {gen} without an error message.");
                        cells.Add(new UiSplitContactSheetCell(gen, split.Index, false, label, error, ms, size, 0));
                        continue;
                    }

                    if (!evt.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException($"Part {split.Index} succeeded for {gen} without recorded image entries.");
                    var imageCount = 0;
                    foreach (var item in imagesEl.EnumerateArray())
                    {
                        if (string.IsNullOrWhiteSpace(item.GetString()))
                            throw new InvalidDataException($"Part {split.Index} has a blank image URL for {gen}.");
                        imageCount++;
                    }
                    if (imageCount == 0)
                        throw new InvalidDataException($"Part {split.Index} succeeded for {gen} with zero images.");

                    cells.Add(new UiSplitContactSheetCell(gen, split.Index, true, label, "", ms, size, imageCount));
                }

                partModels.Add(new UiSplitContactSheetPart(
                    split.Index,
                    split.Count,
                    job.Id,
                    split.Title,
                    job.Prompt,
                    cells));
            }

            var inputCount = orderedParts[0].InputImageCount;
            var inputRoleText = inputCount > 0 ? inputRoleTextBuilder(selectedKeys) : "";
            return new UiSplitContactSheetData(
                firstSplit.GroupId,
                firstSplit.ExchangeId,
                PromptSplit.ModelLabel(firstSplit.Model),
                firstSplit.Reason,
                sourcePrompt ?? "",
                styleNote ?? "",
                inputCount,
                inputRoleText,
                selectedKeys,
                generatorLabels,
                generatorExtraTexts,
                partModels);
        }

        private sealed record PanelPlacement(SKRectI Bounds, SKColor Fill, SKColor? Border, float BorderWidth = 1.5f);
        private sealed record LinePlacement(SKPoint Start, SKPoint End, SKColor Color, float Thickness);
        private sealed record TextPlacement(string Text, SheetFont Font, SKColor Color, SKRectI Bounds, HorizontalAlignment Align = HorizontalAlignment.Left, SKRectI? Clip = null);
        private sealed record ImagePlacement(string Path, SKRectI Bounds, bool GoldFrame);
        private sealed record ImageMeta(string Path, int Width, int Height, bool IsSvg);
        private sealed record LayoutPlan(
            int Width,
            int Height,
            List<PanelPlacement> Panels,
            List<LinePlacement> Lines,
            List<TextPlacement> Texts,
            List<ImagePlacement> Images);

        public static Task RenderAsync(
            UiSplitContactSheetData data,
            IReadOnlyList<string> inputPaths,
            IReadOnlyDictionary<(int PartIndex, string GeneratorKey), IReadOnlyList<string>> cellImagePaths,
            bool includeText,
            string outputPath,
            CancellationToken cancellationToken = default)
            => PixelWorkGate.RunAsync(() => Task.Run(
                () => Render(data, inputPaths, cellImagePaths, includeText, outputPath, cancellationToken),
                cancellationToken));

        private static void Render(
            UiSplitContactSheetData data,
            IReadOnlyList<string> inputPaths,
            IReadOnlyDictionary<(int PartIndex, string GeneratorKey), IReadOnlyList<string>> cellImagePaths,
            bool includeText,
            string outputPath,
            CancellationToken cancellationToken)
        {
            if (inputPaths.Count != data.InputCount)
                throw new InvalidDataException("The series sheet inputs do not match the recorded input count.");

            var inputMetas = new List<ImageMeta>(inputPaths.Count);
            foreach (var path in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputMetas.Add(InspectImage(path));
            }

            var cellMetas = new Dictionary<(int PartIndex, string GeneratorKey), List<ImageMeta>>();
            foreach (var part in data.Parts)
            {
                foreach (var cell in part.Cells)
                {
                    if (!cell.Ok) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!cellImagePaths.TryGetValue((part.Index, cell.GeneratorKey), out var paths)
                        || paths.Count != cell.ImageCount)
                    {
                        throw new InvalidDataException(
                            $"Part {part.Index} ({cell.GeneratorKey}) is missing recorded image files.");
                    }
                    var list = new List<ImageMeta>(paths.Count);
                    foreach (var p in paths) list.Add(InspectImage(p));
                    cellMetas[(part.Index, cell.GeneratorKey)] = list;
                }
            }

            var partCount = data.Parts.Count;
            var genCount = data.GeneratorKeys.Count;
            var totalCells = partCount * genCount;
            var cellEdge = totalCells <= 4 ? 1024
                : totalCells <= 9 ? 896
                : totalCells <= 16 ? 768
                : totalCells <= 25 ? 640
                : 512;

            LayoutPlan? chosen = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = BuildLayoutPlan(data, inputMetas, cellMetas, includeText, cellEdge);
                if ((long)candidate.Width * candidate.Height <= MaxPixels)
                {
                    chosen = candidate;
                    break;
                }
                if (cellEdge <= MinCellEdge)
                {
                    throw new InvalidDataException(
                        "The complete series contact sheet exceeds 32 megapixels. Try the no-text contact sheet or a per-model sheet.");
                }
                var ratio = Math.Sqrt((double)MaxPixels / ((long)candidate.Width * candidate.Height));
                var nextEdge = Math.Min(cellEdge - 64, (int)Math.Floor(cellEdge * Math.Min(0.85, ratio)));
                cellEdge = Math.Max(MinCellEdge, nextEdge);
            }

            using var sheet = Raster.Create(chosen.Width, chosen.Height, SKColors.White);
            using (var canvas = new SKCanvas(sheet))
            {
                foreach (var panel in chosen.Panels)
                {
                    var rect = new SKRect(panel.Bounds.Left, panel.Bounds.Top, panel.Bounds.Right, panel.Bounds.Bottom);
                    canvas.FillRect(rect, panel.Fill);
                    if (panel.Border.HasValue)
                    {
                        var inset = panel.BorderWidth / 2f;
                        canvas.StrokeRect(
                            SKRect.Create(
                                rect.Left + inset,
                                rect.Top + inset,
                                Math.Max(1f, rect.Width - panel.BorderWidth),
                                Math.Max(1f, rect.Height - panel.BorderWidth)),
                            panel.Border.Value,
                            panel.BorderWidth);
                    }
                }

                foreach (var line in chosen.Lines)
                {
                    using var paint = new SKPaint
                    {
                        Color = line.Color,
                        StrokeWidth = line.Thickness,
                        IsAntialias = false,
                        Style = SKPaintStyle.Stroke,
                    };
                    canvas.DrawLine(line.Start, line.End, paint);
                }

                foreach (var text in chosen.Texts)
                {
                    var options = TextOptions(text.Font, text.Bounds.Width, text.Align);
                    options.Origin = text.Align == HorizontalAlignment.Center
                        ? new SKPoint(text.Bounds.Left + text.Bounds.Width / 2f, text.Bounds.Top)
                        : new SKPoint(text.Bounds.Left, text.Bounds.Top);
                    if (text.Clip.HasValue)
                    {
                        var c = text.Clip.Value;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(c.Left, c.Top, c.Right, c.Bottom));
                        canvas.DrawTextStandard(options, text.Text, text.Color);
                        canvas.Restore();
                    }
                    else
                    {
                        canvas.DrawTextStandard(options, text.Text, text.Color);
                    }
                }

                foreach (var img in chosen.Images)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var decoded = DecodeForTarget(img.Path, img.Bounds.Width, img.Bounds.Height);
                    using var resized = Raster.Resize(decoded, img.Bounds.Width, img.Bounds.Height);
                    canvas.DrawBitmapAt(resized, img.Bounds.Left, img.Bounds.Top);
                    if (img.GoldFrame)
                    {
                        const float borderWidth = 6f;
                        const float inset = borderWidth / 2f;
                        canvas.StrokeRect(
                            SKRect.Create(
                                img.Bounds.Left + inset,
                                img.Bounds.Top + inset,
                                Math.Max(1f, img.Bounds.Width - borderWidth),
                                Math.Max(1f, img.Bounds.Height - borderWidth)),
                            UIConstants.Gold,
                            borderWidth);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Raster.SavePng(sheet, outputPath);
        }

        private static LayoutPlan BuildLayoutPlan(
            UiSplitContactSheetData data,
            IReadOnlyList<ImageMeta> inputMetas,
            IReadOnlyDictionary<(int PartIndex, string GeneratorKey), List<ImageMeta>> cellMetas,
            bool includeText,
            int cellEdge)
        {
            var partCount = data.Parts.Count;
            var genCount = data.GeneratorKeys.Count;
            var singleGenerator = genCount == 1;
            var columns = singleGenerator
                ? (partCount <= 4 ? partCount : (int)Math.Ceiling(partCount / 2.0))
                : partCount;

            var contentWidth = Math.Max(1200, columns * cellEdge + (columns - 1) * Gap);
            var cellWidth = (contentWidth - (columns - 1) * Gap) / columns;
            var width = contentWidth + Margin * 2;

            var titleFont = FontUtils.CreateFont(36, FontStyle.Bold);
            var sectionFont = FontUtils.CreateFont(28, FontStyle.Bold);
            var headingFont = FontUtils.CreateFont(24, FontStyle.Bold);
            var captionFont = FontUtils.CreateFont(18, FontStyle.Bold);
            var bodyFont = FontUtils.CreateFont(22, FontStyle.Regular);
            var smallFont = FontUtils.CreateFont(18, FontStyle.Regular);

            var panels = new List<PanelPlacement>();
            var lines = new List<LinePlacement>();
            var texts = new List<TextPlacement>();
            var images = new List<ImagePlacement>();

            int MeasureText(string text, SheetFont font, int wrap)
            {
                if (text.Length == 0) return 0;
                if (text.Length > MaxSectionChars)
                    throw new InvalidDataException("A text section exceeds the series contact sheet limit; no text was shortened.");
                var opts = TextOptions(font, wrap);
                var bounds = TextLayout.Measure(text, opts);
                return checked((int)Math.Ceiling(bounds.Height + font.Size * 0.45f + 6));
            }

            int AddText(string text, SheetFont font, SKColor color, int x, int y, int wrap, HorizontalAlignment align = HorizontalAlignment.Left)
            {
                if (text.Length == 0) return y;
                var h = MeasureText(text, font, wrap);
                texts.Add(new TextPlacement(text, font, color, SKRectI.Create(x, y, wrap, h), align));
                return checked(y + h);
            }

            var y = Margin;
            var title = singleGenerator
                ? $"{partCount}-page series · {data.GeneratorLabels[data.GeneratorKeys[0]]} · split by {data.ModelLabel}"
                : $"{partCount}-page series · {genCount} models · split by {data.ModelLabel}";
            y = AddText(title, titleFont, Ink, Margin, y, contentWidth) + 12;

            // Optional gold-framed input strip right below the title.
            if (inputMetas.Count > 0)
            {
                var stripTop = y;
                var stripInnerY = stripTop + CardPad;
                var stripInnerX = Margin + CardPad;
                var stripInnerW = contentWidth - CardPad * 2;
                var labelBottom = AddText(
                    inputMetas.Count == 1 ? "INPUT IMAGE" : $"INPUT IMAGES ({inputMetas.Count})",
                    headingFont,
                    Ink,
                    stripInnerX,
                    stripInnerY,
                    stripInnerW) + 8;

                var thumbX = stripInnerX;
                var thumbRowBottom = labelBottom;
                foreach (var meta in inputMetas)
                {
                    var scale = Math.Min((double)InputMaxEdge / meta.Width, (double)InputMaxEdge / meta.Height);
                    var w = Math.Max(1, (int)Math.Round(meta.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(meta.Height * scale));
                    if (thumbX + w > stripInnerX + stripInnerW && thumbX > stripInnerX)
                    {
                        thumbX = stripInnerX;
                        labelBottom = thumbRowBottom + 12;
                    }
                    images.Add(new ImagePlacement(meta.Path, SKRectI.Create(thumbX, labelBottom, w, h), GoldFrame: true));
                    thumbX += w + 14;
                    thumbRowBottom = Math.Max(thumbRowBottom, labelBottom + h);
                }

                var roleBottom = thumbRowBottom + 10;
                if (!string.IsNullOrWhiteSpace(data.InputRoleText))
                {
                    roleBottom = AddText(data.InputRoleText, smallFont, Ink, stripInnerX, roleBottom, stripInnerW);
                }
                var stripHeight = roleBottom + CardPad - stripTop;
                panels.Add(new PanelPlacement(SKRectI.Create(Margin, stripTop, contentWidth, stripHeight), PanelFill, UIConstants.Gold, 3f));
                y = stripTop + stripHeight + Gap;
            }

            if (!singleGenerator)
            {
                // Column headers for pages 1..P so all generator rows align cleanly beneath them.
                var headerHeights = new int[partCount];
                for (var c = 0; c < partCount; c++)
                {
                    var part = data.Parts[c];
                    var colHeading = string.IsNullOrWhiteSpace(part.Title)
                        ? $"Page {part.Index}"
                        : $"Page {part.Index} · {part.Title}";
                    headerHeights[c] = MeasureText(colHeading, headingFont, Math.Max(1, cellWidth - 16));
                }
                var headerRowH = headerHeights.Max() + 16;
                for (var c = 0; c < partCount; c++)
                {
                    var colX = Margin + c * (cellWidth + Gap);
                    var part = data.Parts[c];
                    var colHeading = string.IsNullOrWhiteSpace(part.Title)
                        ? $"Page {part.Index}"
                        : $"Page {part.Index} · {part.Title}";
                    panels.Add(new PanelPlacement(SKRectI.Create(colX, y, cellWidth, headerRowH), PanelFill, BorderColor, 1.5f));
                    AddText(colHeading, headingFont, Accent, colX + 8, y + 8, Math.Max(1, cellWidth - 16), HorizontalAlignment.Center);
                }
                y += headerRowH + 14;

                foreach (var genKey in data.GeneratorKeys)
                {
                    var genLabel = data.GeneratorLabels[genKey];
                    var bandTextH = MeasureText(genLabel, sectionFont, contentWidth - 28);
                    var bandH = bandTextH + 16;
                    panels.Add(new PanelPlacement(SKRectI.Create(Margin, y, contentWidth, bandH), BandFill, BorderColor, 1.5f));
                    AddText(genLabel, sectionFont, Ink, Margin + 14, y + 8, contentWidth - 28);
                    y += bandH + 12;

                    var rowCells = data.Parts
                        .Select(p => p.Cells.Single(c => string.Equals(c.GeneratorKey, genKey, StringComparison.Ordinal)))
                        .ToList();
                    y = LayoutImageRow(
                        rowCells,
                        data.Parts,
                        cellMetas,
                        includePartTitleInCaption: false,
                        cellWidth,
                        cellEdge,
                        Margin,
                        y,
                        captionFont,
                        smallFont,
                        panels,
                        texts,
                        images,
                        MeasureText,
                        AddText) + Gap;
                }
            }
            else
            {
                var genKey = data.GeneratorKeys[0];
                var totalRows = (int)Math.Ceiling(partCount / (double)columns);
                for (var r = 0; r < totalRows; r++)
                {
                    var sliceParts = data.Parts.Skip(r * columns).Take(columns).ToList();
                    var rowCells = sliceParts
                        .Select(p => p.Cells.Single(c => string.Equals(c.GeneratorKey, genKey, StringComparison.Ordinal)))
                        .ToList();
                    y = LayoutImageRow(
                        rowCells,
                        sliceParts,
                        cellMetas,
                        includePartTitleInCaption: true,
                        cellWidth,
                        cellEdge,
                        Margin,
                        y,
                        captionFont,
                        smallFont,
                        panels,
                        texts,
                        images,
                        MeasureText,
                        AddText) + Gap;
                }
            }

            if (includeText)
            {
                y += 6;
                lines.Add(new LinePlacement(
                    new SKPoint(Margin, y),
                    new SKPoint(Margin + contentWidth, y),
                    DividerColor,
                    3f));
                y += 22;

                y = AddText($"Full text — {partCount} pages", sectionFont, Ink, Margin, y, contentWidth) + 16;

                var textCols = Math.Clamp(Math.Min(partCount, Math.Max(1, contentWidth / 760)), 1, 3);
                var cardW = (contentWidth - (textCols - 1) * Gap) / textCols;
                var cardInnerW = Math.Max(1, cardW - CardPad * 2);
                var textRows = (int)Math.Ceiling(partCount / (double)textCols);

                for (var row = 0; row < textRows; row++)
                {
                    var rowParts = data.Parts.Skip(row * textCols).Take(textCols).ToList();
                    var rowCardHeights = new int[rowParts.Count];
                    for (var c = 0; c < rowParts.Count; c++)
                    {
                        var part = rowParts[c];
                        var head = string.IsNullOrWhiteSpace(part.Title)
                            ? $"Page {part.Index} of {part.Count}"
                            : $"Page {part.Index} of {part.Count} · {part.Title}";
                        var h = CardPad
                            + MeasureText(head, headingFont, cardInnerW)
                            + 10
                            + MeasureText(part.Prompt, bodyFont, cardInnerW)
                            + CardPad;
                        rowCardHeights[c] = h;
                    }
                    var rowHeight = rowCardHeights.Max();
                    for (var c = 0; c < rowParts.Count; c++)
                    {
                        var part = rowParts[c];
                        var cardX = Margin + c * (cardW + Gap);
                        var head = string.IsNullOrWhiteSpace(part.Title)
                            ? $"Page {part.Index} of {part.Count}"
                            : $"Page {part.Index} of {part.Count} · {part.Title}";
                        panels.Add(new PanelPlacement(SKRectI.Create(cardX, y, cardW, rowHeight), PanelFill, BorderColor, 1.5f));
                        var textY = AddText(head, headingFont, Accent, cardX + CardPad, y + CardPad, cardInnerW) + 10;
                        AddText(part.Prompt, bodyFont, Ink, cardX + CardPad, textY, cardInnerW);
                    }
                    y += rowHeight + Gap;
                }

                var hasSourceSection = !string.IsNullOrWhiteSpace(data.SourcePrompt)
                    || !string.IsNullOrWhiteSpace(data.Reason)
                    || !string.IsNullOrWhiteSpace(data.StyleNote)
                    || data.GeneratorExtraTexts.Count > 0;
                if (hasSourceSection)
                {
                    var boxTop = y;
                    var innerX = Margin + CardPad;
                    var innerW = Math.Max(1, contentWidth - CardPad * 2);
                    var curY = boxTop + CardPad;

                    if (!string.IsNullOrWhiteSpace(data.SourcePrompt))
                    {
                        curY = AddText("Original source prompt", headingFont, Accent, innerX, curY, innerW) + 8;
                        curY = AddText(data.SourcePrompt, bodyFont, Ink, innerX, curY, innerW) + 16;
                    }
                    if (!string.IsNullOrWhiteSpace(data.Reason))
                    {
                        curY = AddText($"Split reason ({data.ModelLabel})", headingFont, Accent, innerX, curY, innerW) + 8;
                        curY = AddText(data.Reason, bodyFont, Ink, innerX, curY, innerW) + 16;
                    }
                    if (!string.IsNullOrWhiteSpace(data.StyleNote))
                    {
                        curY = AddText("Style note", headingFont, Accent, innerX, curY, innerW) + 8;
                        curY = AddText(data.StyleNote, bodyFont, Ink, innerX, curY, innerW) + 16;
                    }
                    if (data.GeneratorExtraTexts.Count > 0)
                    {
                        curY = AddText("Endpoint extra text", headingFont, Accent, innerX, curY, innerW) + 8;
                        foreach (var genKey in data.GeneratorKeys)
                        {
                            if (!data.GeneratorExtraTexts.TryGetValue(genKey, out var extra) || string.IsNullOrWhiteSpace(extra))
                                continue;
                            var label = data.GeneratorLabels[genKey];
                            curY = AddText($"{label}: {extra}", smallFont, Ink, innerX, curY, innerW) + 8;
                        }
                    }

                    var boxH = curY + CardPad - boxTop;
                    panels.Add(new PanelPlacement(SKRectI.Create(Margin, boxTop, contentWidth, boxH), PanelFill, BorderColor, 1.5f));
                    y = boxTop + boxH + Gap;
                }
            }

            var totalHeight = checked(y + Margin - Gap);
            return new LayoutPlan(width, totalHeight, panels, lines, texts, images);
        }

        private static int LayoutImageRow(
            IReadOnlyList<UiSplitContactSheetCell> rowCells,
            IReadOnlyList<UiSplitContactSheetPart> rowParts,
            IReadOnlyDictionary<(int PartIndex, string GeneratorKey), List<ImageMeta>> cellMetas,
            bool includePartTitleInCaption,
            int cellWidth,
            int cellMaxHeight,
            int leftMargin,
            int rowTop,
            SheetFont captionFont,
            SheetFont smallFont,
            List<PanelPlacement> panels,
            List<TextPlacement> texts,
            List<ImagePlacement> images,
            Func<string, SheetFont, int, int> measureText,
            Func<string, SheetFont, SKColor, int, int, int, HorizontalAlignment, int> addText)
        {
            var cellImageHeights = new int[rowCells.Count];
            var cellLayouts = new List<List<(ImageMeta Meta, int RelX, int RelY, int W, int H)>>(rowCells.Count);

            for (var c = 0; c < rowCells.Count; c++)
            {
                var cell = rowCells[c];
                if (!cell.Ok)
                {
                    cellLayouts.Add(new List<(ImageMeta, int, int, int, int)>());
                    cellImageHeights[c] = Math.Min(cellWidth, Math.Max(220, cellMaxHeight / 2));
                    continue;
                }

                var metas = cellMetas[(cell.PartIndex, cell.GeneratorKey)];
                if (metas.Count == 1)
                {
                    var m = metas[0];
                    var scale = Math.Min((double)cellWidth / m.Width, (double)cellMaxHeight / m.Height);
                    var w = Math.Max(1, (int)Math.Round(m.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(m.Height * scale));
                    var relX = (cellWidth - w) / 2;
                    cellLayouts.Add(new List<(ImageMeta, int, int, int, int)> { (m, relX, 0, w, h) });
                    cellImageHeights[c] = h;
                }
                else
                {
                    // Multi-image cell (e.g. grok-web side-by-side 2-4 images): neat sub-grid inside cellWidth.
                    var subCols = metas.Count <= 2 ? metas.Count : 2;
                    var subRows = (int)Math.Ceiling(metas.Count / (double)subCols);
                    var subBoxW = Math.Max(1, (cellWidth - (subCols - 1) * SubGap) / subCols);
                    var subBoxMaxH = Math.Max(1, (cellMaxHeight - (subRows - 1) * SubGap) / subRows);
                    var placed = new List<(ImageMeta, int, int, int, int)>(metas.Count);
                    var subY = 0;
                    for (var sr = 0; sr < subRows; sr++)
                    {
                        var rowSlice = metas.Skip(sr * subCols).Take(subCols).ToList();
                        var rowSized = new List<(ImageMeta Meta, int W, int H)>(rowSlice.Count);
                        foreach (var m in rowSlice)
                        {
                            var scale = Math.Min((double)subBoxW / m.Width, (double)subBoxMaxH / m.Height);
                            var w = Math.Max(1, (int)Math.Round(m.Width * scale));
                            var h = Math.Max(1, (int)Math.Round(m.Height * scale));
                            rowSized.Add((m, w, h));
                        }
                        var subRowH = rowSized.Max(x => x.H);
                        for (var sc = 0; sc < rowSized.Count; sc++)
                        {
                            var (m, w, h) = rowSized[sc];
                            var relX = sc * (subBoxW + SubGap) + (subBoxW - w) / 2;
                            var relY = subY + (subRowH - h) / 2;
                            placed.Add((m, relX, relY, w, h));
                        }
                        subY += subRowH + SubGap;
                    }
                    cellLayouts.Add(placed);
                    cellImageHeights[c] = Math.Max(1, subY - SubGap);
                }
            }

            var rowImageH = cellImageHeights.Max();
            var captionHeights = new int[rowCells.Count];
            var captions = new string[rowCells.Count];

            for (var c = 0; c < rowCells.Count; c++)
            {
                var cell = rowCells[c];
                var part = rowParts[c];
                captions[c] = BuildCellCaption(cell, part, cellMetas, includePartTitleInCaption);
                captionHeights[c] = measureText(captions[c], captionFont, Math.Max(1, cellWidth - 12));
            }
            var rowCaptionH = captionHeights.Max() + 10;

            for (var c = 0; c < rowCells.Count; c++)
            {
                var cell = rowCells[c];
                var colX = leftMargin + c * (cellWidth + Gap);
                if (!cell.Ok)
                {
                    var errRect = SKRectI.Create(colX, rowTop, cellWidth, rowImageH);
                    panels.Add(new PanelPlacement(errRect, ErrorFill, ErrorRed, 2f));
                    var errPad = 14;
                    var clip = SKRectI.Create(colX + 4, rowTop + 4, Math.Max(1, cellWidth - 8), Math.Max(1, rowImageH - 8));
                    var headH = measureText("FAILED", captionFont, Math.Max(1, cellWidth - errPad * 2));
                    texts.Add(new TextPlacement("FAILED", captionFont, ErrorRed, SKRectI.Create(colX + errPad, rowTop + errPad, Math.Max(1, cellWidth - errPad * 2), headH), HorizontalAlignment.Center, clip));
                    var errY = rowTop + errPad + headH + 8;
                    var errorText = cell.Error.Length > 600 ? cell.Error[..600] + "…" : cell.Error;
                    var errH = measureText(errorText, smallFont, Math.Max(1, cellWidth - errPad * 2));
                    texts.Add(new TextPlacement(errorText, smallFont, Ink, SKRectI.Create(colX + errPad, errY, Math.Max(1, cellWidth - errPad * 2), errH), HorizontalAlignment.Left, clip));
                }
                else
                {
                    var verticalOffset = (rowImageH - cellImageHeights[c]) / 2;
                    foreach (var (meta, relX, relY, w, h) in cellLayouts[c])
                    {
                        images.Add(new ImagePlacement(
                            meta.Path,
                            SKRectI.Create(colX + relX, rowTop + verticalOffset + relY, w, h),
                            GoldFrame: false));
                    }
                }

                var capColor = cell.Ok ? Ink : ErrorRed;
                addText(
                    captions[c],
                    captionFont,
                    capColor,
                    colX + 6,
                    rowTop + rowImageH + 6,
                    Math.Max(1, cellWidth - 12),
                    HorizontalAlignment.Center);
            }

            return checked(rowTop + rowImageH + rowCaptionH);
        }

        private static string BuildCellCaption(
            UiSplitContactSheetCell cell,
            UiSplitContactSheetPart part,
            IReadOnlyDictionary<(int PartIndex, string GeneratorKey), List<ImageMeta>> cellMetas,
            bool includePartTitle)
        {
            var pieces = new List<string>();
            if (includePartTitle && !string.IsNullOrWhiteSpace(part.Title))
                pieces.Add($"Page {part.Index} · {part.Title}");
            else
                pieces.Add($"Page {part.Index}");

            if (cell.Ok)
            {
                if (cellMetas.TryGetValue((cell.PartIndex, cell.GeneratorKey), out var metas) && metas.Count > 0)
                {
                    if (metas.Count == 1)
                        pieces.Add($"{metas[0].Width}×{metas[0].Height}");
                    else
                        pieces.Add($"{metas.Count} imgs ({metas[0].Width}×{metas[0].Height})");
                }
                else if (!string.IsNullOrWhiteSpace(cell.RecordedSize))
                {
                    pieces.Add(cell.RecordedSize);
                }
            }
            else
            {
                pieces.Add("failed");
            }

            if (cell.ElapsedMs > 0)
            {
                var secs = Math.Max(1, (int)Math.Round(cell.ElapsedMs / 1000.0));
                pieces.Add(secs >= 60 ? $"{secs / 60}m {secs % 60}s" : $"{secs}s");
            }
            return string.Join(" · ", pieces);
        }

        private static ImageMeta InspectImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidDataException("An image file needed for the series contact sheet is missing.");
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var (w, h) = SvgRaster.IntrinsicSize(path);
                return new ImageMeta(path, w, h, IsSvg: true);
            }
            var info = Raster.Identify(path);
            if (info.Width <= 0 || info.Height <= 0)
                throw new InvalidDataException("A series sheet image has invalid dimensions.");
            return new ImageMeta(path, info.Width, info.Height, IsSvg: false);
        }

        private static SKBitmap DecodeForTarget(string path, int targetWidth, int targetHeight)
        {
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                return SvgRaster.Rasterize(path);
            }
            return Raster.DecodeForBox(path, targetWidth, targetHeight);
        }

        private static TextBlockOptions TextOptions(SheetFont font, int width, HorizontalAlignment align = HorizontalAlignment.Left) => new(font)
        {
            Dpi = 72,
            WrappingLength = Math.Max(1, width),
            LineSpacing = 1.25f,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }
}
