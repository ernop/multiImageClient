using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using System;

namespace MultiImageClient
{
    public static class ImageUtils
    {
        public static readonly GraphicsOptions StandardGraphicsOptions = new GraphicsOptions
        {
            Antialias = true,
            AntialiasSubpixelDepth = 16
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
    }
}
