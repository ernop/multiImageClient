using SkiaSharp;

namespace MultiImageClient
{
    public static class UIConstants
    {
        // Shared across multiple files
        public const int Padding = 10;
        public const float LineSpacing = 1.15f;
        public const int MinFontSize = 8;
        public const int TextDpi = 150;
        public const int LabelHeight = 20; // Default height for labels under images

        // Colors
        public static readonly SKColor White = SKColors.White;
        public static readonly SKColor Black = SKColors.Black;
        public static readonly SKColor SuccessGreen = new SKColor(0, 120, 0);
        public static readonly SKColor ErrorRed = new SKColor(180, 0, 0);
        public static readonly SKColor PlaceholderFill = new SKColor(240, 240, 240);
        public static readonly SKColor PlaceholderBorder = new SKColor(200, 200, 200);
        public static readonly SKColor Gold = new SKColor(255, 215, 0);
        // We do NOT use gray for text. See AGENTS.md > Visual & Typography Policy.
        // Secondary labels must get their visual hierarchy from smaller font size and/or
        // by reusing the existing semantic color (SuccessGreen, ErrorRed, Black, Gold).
    }
}
