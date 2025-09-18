using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Linq;

namespace MultiImageClient
{
    /// <summary>
    /// Centralized font handling utilities to avoid duplication across the application.
    /// </summary>
    public static class FontUtils
    {
        private static FontFamily? _cachedSystemFont;

        /// <summary>
        /// Gets the preferred system font family with fallback logic.
        /// Order: Segoe UI → Arial → First available system font
        /// </summary>
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

        /// <summary>
        /// Creates a font with the preferred system font family.
        /// </summary>
        public static Font CreateFont(float size, FontStyle style = FontStyle.Regular)
        {
            return GetSystemFont().CreateFont(size, style);
        }

        /// <summary>
        /// Creates standard RichTextOptions with common settings.
        /// </summary>
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
                Dpi = 72,
                FallbackFontFamilies = new[] { GetSystemFont() }
            };
        }
    }
}