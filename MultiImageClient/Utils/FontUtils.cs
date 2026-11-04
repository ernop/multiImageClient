#nullable enable
using System;

using SkiaSharp;

namespace MultiImageClient
{
    public enum FontStyle { Regular, Bold }

    public enum HorizontalAlignment { Left, Center, Right }

    public enum VerticalAlignment { Top, Center, Bottom }

    // A font family face at a nominal size. Size is in points: TextBlockOptions.Dpi
    // converts it to pixels (Size * Dpi / 72) when text is laid out.
    public sealed class SheetFont
    {
        internal SheetFont(SKTypeface typeface, float size, FontStyle style)
        {
            Typeface = typeface;
            Size = size;
            Style = style;
        }

        public float Size { get; }
        public FontStyle Style { get; }
        internal SKTypeface Typeface { get; }
    }

    // Origin is the anchor point. Left, Center, and Right place each line's
    // start, middle, or end on Origin.X; Top, Center, and Bottom place the
    // block's top, middle, or bottom on Origin.Y. WrappingLength <= 0 means
    // lines break only at newlines.
    public sealed class TextBlockOptions
    {
        public TextBlockOptions(SheetFont font)
        {
            Font = font;
        }

        public SheetFont Font { get; }
        public float Dpi { get; set; } = 72f;
        public float LineSpacing { get; set; } = 1f;
        public float WrappingLength { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;
        public VerticalAlignment VerticalAlignment { get; set; } = VerticalAlignment.Top;
        public SKPoint Origin { get; set; }
    }

    public static class FontUtils
    {
        // Preferred sans-serif families in priority order. Windows ships
        // Segoe UI / Arial; Linux typically has Liberation Sans (metric-
        // compatible with Arial) or DejaVu Sans. The first installed family
        // wins; without any of them the system's default sans-serif face is
        // used, so text rendering always has a face.
        private static readonly string[] PreferredFamilies =
        {
            "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans",
        };

        private static readonly Lazy<SKTypeface> Regular = new(() => FindTypeface(SKFontStyle.Normal));
        private static readonly Lazy<SKTypeface> Bold = new(() => FindTypeface(SKFontStyle.Bold));

        public static string SystemFontFamily => Regular.Value.FamilyName;

        public static SheetFont CreateFont(float size, FontStyle style = FontStyle.Regular)
        {
            if (!(size > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(size), $"Font size must be positive, not {size}.");
            }
            return new SheetFont(style == FontStyle.Bold ? Bold.Value : Regular.Value, size, style);
        }

        public static TextBlockOptions CreateTextOptions(SheetFont font,
            HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment verticalAlignment = VerticalAlignment.Top,
            float lineSpacing = 1.15f)
        {
            return new TextBlockOptions(font)
            {
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = verticalAlignment,
                LineSpacing = lineSpacing,
                Dpi = UIConstants.TextDpi,
            };
        }

        private static SKTypeface FindTypeface(SKFontStyle style)
        {
            var manager = SKFontManager.Default;
            foreach (var family in PreferredFamilies)
            {
                var face = manager.MatchFamily(family, style);
                if (face != null)
                {
                    return face;
                }
            }
            return SKTypeface.FromFamilyName("sans-serif", style)
                ?? throw new InvalidOperationException("No sans-serif font face is installed.");
        }
    }
}
