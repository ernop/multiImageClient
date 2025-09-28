using SixLabors.ImageSharp;

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
        public static readonly Color White = Color.White;
        public static readonly Color Black = Color.Black;
        public static readonly Color SuccessGreen = Color.FromRgb(0, 120, 0);
        public static readonly Color ErrorRed = Color.FromRgb(180, 0, 0);
        public static readonly Color PlaceholderFill = Color.FromRgb(240, 240, 240);
        public static readonly Color PlaceholderBorder = Color.FromRgb(200, 200, 200);
        public static readonly Color Gold = Color.FromRgb(255, 215, 0);
    }
}
