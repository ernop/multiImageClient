using System;
using System.Collections.Generic;
using System.Linq;

using SkiaSharp;

namespace MultiImageClient
{
    /// One validated layout-map region in Gemini's 0-1000 detection
    /// coordinate convention ([ymin, xmin, ymax, xmax], y down, x right).
    public sealed record UiLayoutMapRegion(string Label, int YMin, int XMin, int YMax, int XMax);

    /// Renders parsed layout-map regions as the simple flat-color section map
    /// the feature promises: the map area keeps the source image's aspect
    /// ratio, each region is one solid palette color with a numbered label,
    /// and a white legend band below carries the numbered color key plus the
    /// model's one-sentence summary. Everything the viewer needs is baked
    /// into the PNG, so the result rides the normal image pipeline unchanged.
    public static class UiLayoutMapRenderer
    {
        // Eight visually distinct fills, assigned to regions in reply order.
        // Text drawn on top of them is black or white by luminance (never
        // gray, per the visual policy).
        private static readonly SKColor[] Palette =
        {
            SKColor.Parse("E53935"), // red
            SKColor.Parse("1E88E5"), // blue
            SKColor.Parse("43A047"), // green
            SKColor.Parse("FB8C00"), // orange
            SKColor.Parse("8E24AA"), // purple
            SKColor.Parse("00ACC1"), // cyan
            SKColor.Parse("FDD835"), // yellow
            SKColor.Parse("6D4C41"), // brown
        };

        public static int MaxRegions => Palette.Length;

        private const int MapLongEdge = 1024;
        private const int Pad = 12;
        private const float LegendFontSize = 20f;
        private const int LegendSwatch = 20;
        private const int LegendRowGap = 8;

        public static SKBitmap Render(
            int sourceWidth,
            int sourceHeight,
            IReadOnlyList<UiLayoutMapRegion> regions,
            string summary)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceWidth),
                    $"Source dimensions must be positive; received {sourceWidth}x{sourceHeight}.");
            }
            if (regions == null || regions.Count == 0 || regions.Count > MaxRegions)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(regions),
                    $"Layout maps render 1 to {MaxRegions} regions; received {regions?.Count ?? 0}.");
            }
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new ArgumentException("Layout maps require the model's summary sentence.", nameof(summary));
            }

            // Map area: source aspect at a fixed long edge, so the map always
            // reads as "your image, abstracted" regardless of source size.
            var scale = (double)MapLongEdge / Math.Max(sourceWidth, sourceHeight);
            var mapWidth = Math.Max(64, (int)Math.Round(sourceWidth * scale));
            var mapHeight = Math.Max(64, (int)Math.Round(sourceHeight * scale));

            var legendFont = FontUtils.CreateFont(LegendFontSize, FontStyle.Regular);
            var legendRowHeight = Math.Max(
                LegendSwatch,
                ImageUtils.MeasureTextHeight("Ag", legendFont)) + LegendRowGap;
            var legendTextWidth = mapWidth - Pad * 2 - LegendSwatch - Pad;
            var legendRows = regions
                .Select((r, i) => $"{i + 1}. {r.Label}")
                .ToList();
            var legendRowHeights = legendRows
                .Select(text => Math.Max(
                    LegendSwatch,
                    ImageUtils.MeasureTextHeight(text, legendFont, wrappingLength: legendTextWidth)) + LegendRowGap)
                .ToList();
            var summaryHeight = ImageUtils.MeasureTextHeight(
                summary, legendFont, wrappingLength: mapWidth - Pad * 2);
            // ~25% descender padding per the typography policy.
            var descender = (int)Math.Ceiling(LegendFontSize * 0.25f);
            var legendHeight = Pad
                + legendRowHeights.Sum()
                + summaryHeight + descender
                + Pad;

            var image = ImageUtils.CreateStandardImage(mapWidth, mapHeight + legendHeight, SKColors.White);
            using (var canvas = new SKCanvas(image))
            {
                // Paint larger regions first so smaller (typically foreground)
                // sections stay visible on top; the numbering keeps the
                // model's reply order either way.
                var paintOrder = regions
                    .Select((region, index) => (Region: region, Index: index))
                    .OrderByDescending(entry =>
                        (long)(entry.Region.YMax - entry.Region.YMin)
                        * (entry.Region.XMax - entry.Region.XMin))
                    .ThenBy(entry => entry.Index)
                    .ToList();

                // Two passes: all fills and borders first, then all labels, so
                // a large background region's label is never buried under the
                // smaller boxes painted over it (the label may extend across a
                // neighboring box; the number + legend keep it unambiguous).
                foreach (var (region, index) in paintOrder)
                {
                    var bounds = RegionBounds(region, mapWidth, mapHeight);
                    canvas.FillRect(bounds, Palette[index]);
                    canvas.StrokeRect(bounds, SKColors.Black, 3f);
                }

                var placedLabels = new List<SKRect>();
                foreach (var (region, index) in paintOrder)
                {
                    var fill = Palette[index];
                    var bounds = RegionBounds(region, mapWidth, mapHeight);
                    var textColor = LuminanceOf(fill) > 150 ? SKColors.Black : SKColors.White;
                    var inBoxText = $"{index + 1} · {region.Label}";
                    var startingSize = (int)Math.Clamp(Math.Min(bounds.Width, bounds.Height) / 4f, 16f, 34f);
                    var font = ImageUtils.AutoSizeFont(inBoxText, (int)bounds.Width, startingSize);
                    var measured = TextLayout.Measure(
                        inBoxText, FontUtils.CreateTextOptions(font));
                    // A label whose autosized minimum still overflows the box
                    // falls back to the bare number; the legend carries the
                    // full text either way (full-name rule: nothing truncated).
                    var text = measured.Width <= bounds.Width - 8f ? inBoxText : $"{index + 1}";
                    if (text != inBoxText)
                    {
                        measured = TextLayout.Measure(
                            text, FontUtils.CreateTextOptions(font));
                    }
                    // Overlapping regions are normal (foreground boxes sit on
                    // background ones), so a centered label can land under or
                    // across another box's label. Try the center plus four
                    // inset corners and keep the placement least covered by
                    // OTHER region boxes and already-placed labels.
                    var otherBounds = paintOrder
                        .Where(entry => entry.Index != index)
                        .Select(entry => RegionBounds(entry.Region, mapWidth, mapHeight))
                        .ToList();
                    var textW = measured.Width;
                    var textH = measured.Height;
                    var inset = 10f;
                    var candidates = new[]
                    {
                        new SKPoint(
                            bounds.Left + (bounds.Width - textW) / 2f,
                            bounds.Top + (bounds.Height - textH) / 2f),
                        new SKPoint(bounds.Left + inset, bounds.Top + inset),
                        new SKPoint(bounds.Right - textW - inset, bounds.Top + inset),
                        new SKPoint(bounds.Left + inset, bounds.Bottom - textH - inset),
                        new SKPoint(bounds.Right - textW - inset, bounds.Bottom - textH - inset),
                    };
                    var best = candidates[0];
                    var bestCost = float.MaxValue;
                    foreach (var candidate in candidates)
                    {
                        var rect = SKRect.Create(candidate.X, candidate.Y, textW, textH);
                        var cost = otherBounds.Sum(other => IntersectionArea(rect, other))
                            + placedLabels.Sum(other => IntersectionArea(rect, other) * 4f);
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = candidate;
                        }
                    }
                    placedLabels.Add(SKRect.Create(best.X, best.Y, textW, textH));
                    var options = FontUtils.CreateTextOptions(font);
                    options.Origin = best;
                    canvas.DrawTextStandard(options, text, textColor);
                }

                // Legend band on white below the map: numbered swatch rows in
                // reply order, then the model's summary sentence.
                var rowY = (float)(mapHeight + Pad);
                for (var i = 0; i < regions.Count; i++)
                {
                    var swatch = SKRect.Create(Pad, rowY, LegendSwatch, LegendSwatch);
                    canvas.FillRect(swatch, Palette[i]);
                    canvas.StrokeRect(swatch, SKColors.Black, 1.5f);
                    var rowOptions = FontUtils.CreateTextOptions(legendFont);
                    rowOptions.Origin = new SKPoint(Pad + LegendSwatch + Pad, rowY);
                    rowOptions.WrappingLength = legendTextWidth;
                    canvas.DrawTextStandard(rowOptions, legendRows[i], SKColors.Black);
                    rowY += legendRowHeights[i];
                }
                var summaryOptions = FontUtils.CreateTextOptions(legendFont);
                summaryOptions.Origin = new SKPoint(Pad, rowY);
                summaryOptions.WrappingLength = mapWidth - Pad * 2;
                canvas.DrawTextStandard(summaryOptions, summary, SKColors.Black);
            }
            return image;
        }

        private static float IntersectionArea(SKRect a, SKRect b)
        {
            var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            return w > 0 && h > 0 ? w * h : 0f;
        }

        private static SKRect RegionBounds(UiLayoutMapRegion region, int mapWidth, int mapHeight)
        {
            var x = region.XMin / 1000f * mapWidth;
            var y = region.YMin / 1000f * mapHeight;
            var w = Math.Max(2f, (region.XMax - region.XMin) / 1000f * mapWidth);
            var h = Math.Max(2f, (region.YMax - region.YMin) / 1000f * mapHeight);
            return SKRect.Create(x, y, w, h);
        }

        private static double LuminanceOf(SKColor color)
        {
            return 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;
        }
    }
}
