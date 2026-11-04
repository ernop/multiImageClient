using SkiaSharp;

using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiImageClient
{
    public static class ImageUtils
    {
        public static int MeasureTextHeight(string text, SheetFont font, float lineSpacing = 1.15f, float wrappingLength = 0)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var textOptions = FontUtils.CreateTextOptions(font, lineSpacing: lineSpacing);
            if (wrappingLength > 0)
                textOptions.WrappingLength = wrappingLength;

            var bounds = TextLayout.Measure(text, textOptions);
            return (int)Math.Ceiling(bounds.Height);
        }

        public static void DrawTextStandard(this SKCanvas canvas,
            TextBlockOptions options,
            string text,
            SKColor color)
        {
            TextLayout.Draw(canvas, options, text ?? "", color);
        }

        public static SKBitmap CreateStandardImage(int width, int height, SKColor? backgroundColor = null)
        {
            return Raster.Create(width, height, backgroundColor ?? SKColors.Transparent);
        }

        public static int MeasureMaxTextHeight(IEnumerable<string> texts, SheetFont font, float lineSpacing = UIConstants.LineSpacing)
        {
            int maxHeight = 0;

            foreach (var text in texts.Where(t => !string.IsNullOrEmpty(t)))
            {
                var height = MeasureTextHeight(text, font, lineSpacing);
                maxHeight = Math.Max(maxHeight, height);
            }

            return maxHeight;
        }

        public static void FillRect(this SKCanvas canvas, SKRect rect, SKColor color)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, paint);
        }

        // The stroke is centered on the rectangle's edges.
        public static void StrokeRect(this SKCanvas canvas, SKRect rect, SKColor color, float thickness)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = thickness };
            canvas.DrawRect(rect, paint);
        }

        // Draws at 1:1 scale, blending over what is already on the canvas.
        public static void DrawBitmapAt(this SKCanvas canvas, SKBitmap bitmap, float x, float y)
        {
            canvas.DrawBitmap(bitmap, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        public static void DrawErrorPlaceholder(this SKCanvas canvas, SKRect bounds, string errorText, SheetFont font)
        {
            // Draw placeholder background and border
            canvas.FillRect(bounds, UIConstants.PlaceholderFill);
            canvas.StrokeRect(bounds, UIConstants.PlaceholderBorder, 2f);

            // Auto-size font to fit the available space (start with smaller size for error text)
            var maxFontSize = Math.Min((int)font.Size, 14); // Cap at 14pt for error text
            var availableWidth = bounds.Width - (UIConstants.Padding * 2);
            var autoSizedFont = AutoSizeFont(errorText, (int)availableWidth, maxFontSize, UIConstants.MinFontSize);

            // Draw error text with proper wrapping and top alignment
            var errorTextOptions = FontUtils.CreateTextOptions(autoSizedFont,
                HorizontalAlignment.Center, VerticalAlignment.Top);

            // Set wrapping length to prevent text overflow
            errorTextOptions.WrappingLength = availableWidth;

            // Position text at top with padding
            errorTextOptions.Origin = new SKPoint(bounds.Left + bounds.Width / 2f, bounds.Top + UIConstants.Padding);

            canvas.DrawTextStandard(errorTextOptions, errorText, UIConstants.ErrorRed);
        }

        public static SheetFont AutoSizeFont(string text, int maxWidth, int startingSize, int minSize = UIConstants.MinFontSize, FontStyle style = FontStyle.Regular)
        {
            var fontSize = startingSize;
            var font = FontUtils.CreateFont(fontSize, style);
            var textOptions = FontUtils.CreateTextOptions(font, HorizontalAlignment.Left);
            var textBounds = TextLayout.Measure(text, textOptions);

            while (textBounds.Width > maxWidth - (UIConstants.Padding * 2) && fontSize > minSize)
            {
                fontSize--;
                font = FontUtils.CreateFont(fontSize, style);
                textOptions = FontUtils.CreateTextOptions(font, HorizontalAlignment.Left);
                textBounds = TextLayout.Measure(text, textOptions);
            }

            return font;
        }

        public static void DrawTextWithBackground(this SKCanvas canvas, SKRect backgroundBounds,
            string text, SheetFont font, SKColor textColor, SKColor backgroundColor,
            HorizontalAlignment alignment = HorizontalAlignment.Center, float lineSpacing = 1.15f)
        {
            // Draw background
            canvas.FillRect(backgroundBounds, backgroundColor);

            // Draw text with proper wrapping and top alignment
            var textOptions = FontUtils.CreateTextOptions(font, alignment, VerticalAlignment.Top, lineSpacing);

            // Set wrapping length to prevent text overflow
            var availableWidth = backgroundBounds.Width - (UIConstants.Padding * 2);
            textOptions.WrappingLength = availableWidth;

            textOptions.Origin = alignment switch
            {
                HorizontalAlignment.Left => new SKPoint(backgroundBounds.Left + UIConstants.Padding, backgroundBounds.Top + UIConstants.Padding),
                HorizontalAlignment.Right => new SKPoint(backgroundBounds.Right - UIConstants.Padding, backgroundBounds.Top + UIConstants.Padding),
                _ => new SKPoint(backgroundBounds.Left + backgroundBounds.Width / 2f, backgroundBounds.Top + UIConstants.Padding)
            };

            canvas.DrawTextStandard(textOptions, text, textColor);
        }
    }
}
