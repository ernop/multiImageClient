
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using System.Linq;

namespace MultiImageClient
{
    public static class FontUtils
    {
        private static FontFamily? _cachedSystemFont;

        // Preferred sans-serif families in priority order. Windows ships
        // Segoe UI / Arial; Linux typically has Liberation Sans (metric-
        // compatible with Arial) or DejaVu Sans. Whichever is found first
        // wins, and we fall back to the first available family so text
        // rendering never throws FontFamilyNotFoundException.
        private static readonly string[] PreferredFamilies =
        {
            "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans",
        };

        // Gets the preferred system font family with fallback logic.
        public static FontFamily GetSystemFont()
        {
            if (_cachedSystemFont != null)
                return _cachedSystemFont.Value!;

            foreach (var family in PreferredFamilies)
            {
                if (SystemFonts.TryGet(family, out var found))
                {
                    _cachedSystemFont = found;
                    return found;
                }
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