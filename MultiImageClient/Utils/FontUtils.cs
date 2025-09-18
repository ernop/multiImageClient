using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Linq;

namespace MultiImageClient
{
    public static class FontUtils
    {
        private static FontFamily? _cachedSystemFont;

        // Gets the preferred system font family with fallback logic.
        // Order: Segoe UI → Arial → First available system font
        public static FontFamily GetSystemFont()
        {
            if (_cachedSystemFont != null)
                return _cachedSystemFont.Value!;

            if (SystemFonts.TryGet("Segoe UI", out var segoeFontFamily))
            {
                _cachedSystemFont = segoeFontFamily;
                return segoeFontFamily;
            }

            if (SystemFonts.TryGet("Arial", out var arialFontFamily))
            {
                _cachedSystemFont = arialFontFamily;
                return arialFontFamily;
            }

            var fallbackFont = SystemFonts.Families.First();
            _cachedSystemFont = fallbackFont;
            return fallbackFont;
        }

        public static Font CreateFont(float size, FontStyle style = FontStyle.Regular)
        {
            return GetSystemFont().CreateFont(size, style);
        }

        public static RichTextOptions CreateTextOptions(Font font, 
            HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment verticalAlignment = VerticalAlignment.Top,
            float lineSpacing = 1.15f)
        {
            return new RichTextOptions(font)
            {
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = verticalAlignment,
                LineSpacing = lineSpacing,
                Dpi = UIConstants.TextDpi,
                FallbackFontFamilies = new[] { GetSystemFont() }
            };
        }
    }
}