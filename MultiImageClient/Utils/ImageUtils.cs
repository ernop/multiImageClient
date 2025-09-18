using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiImageClient
{
    public static class ImageUtils
    {
        public static readonly GraphicsOptions StandardGraphicsOptions = new GraphicsOptions
        {
            Antialias = true,
            AntialiasSubpixelDepth = 32
        };

        public static void ApplyStandardGraphicsOptions(this IImageProcessingContext ctx)
        {
            ctx.SetGraphicsOptions(StandardGraphicsOptions);
        }

        public static int MeasureTextHeight(string text, Font font, float lineSpacing = 1.15f, float wrappingLength = 0)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var textOptions = FontUtils.CreateTextOptions(font, lineSpacing: lineSpacing);
            if (wrappingLength > 0)
                textOptions.WrappingLength = wrappingLength;

            var bounds = TextMeasurer.MeasureBounds(text, textOptions);
            return (int)Math.Ceiling(bounds.Height);
        }

        public static void DrawTextStandard(this IImageProcessingContext ctx, 
            RichTextOptions options, 
            string text, 
            Color color)
        {
            ctx.DrawText(options, text, color);
        }


        public static Image<Rgba32> CreateStandardImage(int width, int height, Color? backgroundColor = null)
        {
            var image = new Image<Rgba32>(width, height);
            
            if (backgroundColor.HasValue)
            {
                image.Mutate(ctx =>
                {
                    ctx.ApplyStandardGraphicsOptions();
                    ctx.Fill(backgroundColor.Value);
                });
            }

            return image;
        }

        public static int MeasureMaxTextHeight(IEnumerable<string> texts, Font font, float lineSpacing = UIConstants.LineSpacing)
        {
            int maxHeight = 0;

            foreach (var text in texts.Where(t => !string.IsNullOrEmpty(t)))
            {
                var height = MeasureTextHeight(text, font, lineSpacing);
                maxHeight = Math.Max(maxHeight, height);
            }

            return maxHeight;
        }

        public static void DrawErrorPlaceholder(this IImageProcessingContext ctx, RectangleF bounds, string errorText, Font font)
        {
            // Draw placeholder background and border
            ctx.Fill(UIConstants.PlaceholderFill, bounds);
            ctx.Draw(UIConstants.PlaceholderBorder, 2f, bounds);

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
            errorTextOptions.Origin = new PointF(bounds.X + bounds.Width / 2f, bounds.Y + UIConstants.Padding);
            
            ctx.DrawTextStandard(errorTextOptions, errorText, UIConstants.ErrorRed);
        }

        public static Font AutoSizeFont(string text, int maxWidth, int startingSize, int minSize = UIConstants.MinFontSize, FontStyle style = FontStyle.Regular)
        {
            var fontSize = startingSize;
            var font = FontUtils.CreateFont(fontSize, style);
            var textOptions = FontUtils.CreateTextOptions(font, HorizontalAlignment.Left);
            var textBounds = TextMeasurer.MeasureBounds(text, textOptions);

            while (textBounds.Width > maxWidth - (UIConstants.Padding * 2) && fontSize > minSize)
            {
                fontSize--;
                font = FontUtils.CreateFont(fontSize, style);
                textOptions = FontUtils.CreateTextOptions(font, HorizontalAlignment.Left);
                textBounds = TextMeasurer.MeasureBounds(text, textOptions);
            }

            return font;
        }

        public static void DrawTextWithBackground(this IImageProcessingContext ctx, RectangleF backgroundBounds, 
            string text, Font font, Color textColor, Color backgroundColor, 
            HorizontalAlignment alignment = HorizontalAlignment.Center, float lineSpacing = 1.15f)
        {
            // Draw background
            ctx.Fill(backgroundColor, backgroundBounds);

            // Draw text with proper wrapping and top alignment
            var textOptions = FontUtils.CreateTextOptions(font, alignment, VerticalAlignment.Top, lineSpacing);
            
            // Set wrapping length to prevent text overflow
            var availableWidth = backgroundBounds.Width - (UIConstants.Padding * 2);
            textOptions.WrappingLength = availableWidth;
            
            textOptions.Origin = alignment switch
            {
                HorizontalAlignment.Left => new PointF(backgroundBounds.X + UIConstants.Padding, backgroundBounds.Y + UIConstants.Padding),
                HorizontalAlignment.Right => new PointF(backgroundBounds.X + backgroundBounds.Width - UIConstants.Padding, backgroundBounds.Y + UIConstants.Padding),
                _ => new PointF(backgroundBounds.X + backgroundBounds.Width / 2f, backgroundBounds.Y + UIConstants.Padding)
            };

            ctx.DrawTextStandard(textOptions, text, textColor);
        }
    }
}
