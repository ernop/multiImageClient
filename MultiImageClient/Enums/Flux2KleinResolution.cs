#nullable enable
using System;
using System.Linq;

namespace MultiImageClient
{
    /// Output resolutions for local ComfyUI image generators.
    /// Member names encode the pixel dimensions as _WIDTHxHEIGHT so the
    /// dimensions can be parsed straight from the name (see the extension
    /// methods below), matching the RecraftImageSize convention.
    ///
    /// All edges are multiples of 16 (ComfyUI's latent requirement) and the
    /// set stays in the usual local-model sweet spot of roughly 1-2 megapixels;
    /// larger than ~2 MP gives diminishing returns on 4B-6B models.
    public enum Flux2KleinResolution
    {
        // 1:1 square, ~1 MP. Default.
        _1024x1024 = 1,

        // 3:2 / 2:3 landscape and portrait, ~1.5 MP.
        _1536x1024 = 2,
        _1024x1536 = 3,

        // 4:3 / 3:4 landscape and portrait, ~1 MP.
        _1152x896 = 4,
        _896x1152 = 5,

        // 16:9 / 9:16 wide and tall, ~1 MP.
        _1344x768 = 6,
        _768x1344 = 7,

        // 1:1 square at ~2 MP for maximum detail.
        _1408x1408 = 8,
    }

    public static class Flux2KleinResolutionExtensions
    {
        /// Parses the (width, height) pixel dimensions out of the enum member
        /// name, e.g. _1536x1024 -> (1536, 1024).
        public static (int Width, int Height) GetDimensions(this Flux2KleinResolution resolution)
        {
            var parts = resolution.ToString().TrimStart('_').Split('x');
            return (int.Parse(parts[0]), int.Parse(parts[1]));
        }

        /// The "WIDTHxHEIGHT" string used for logs, filenames, and metadata.
        public static string ToSizeString(this Flux2KleinResolution resolution) =>
            resolution.ToString().TrimStart('_');

        /// Parses a user-supplied size like "1536x1024" (leading underscore
        /// optional) into the matching enum value. Case-insensitive.
        public static bool TryParseSize(string? text, out Flux2KleinResolution resolution)
        {
            resolution = Flux2KleinResolution._1024x1024;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = "_" + text.Trim().TrimStart('_');
            return Enum.TryParse(normalized, ignoreCase: true, out resolution)
                && Enum.IsDefined(resolution);
        }

        /// Comma-separated list of the supported sizes, for help/error text.
        public static string ValidSizesCsv() =>
            string.Join(", ", Enum.GetValues<Flux2KleinResolution>().Select(r => r.ToSizeString()));
    }
}
