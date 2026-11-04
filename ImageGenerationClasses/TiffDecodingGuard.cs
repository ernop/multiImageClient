using System.IO;
using System.Threading;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;

namespace MultiImageClient
{
    // ImageSharp 3.1.12's TIFF decoder can write past its buffer on a tiled fax TIFF
    // (GHSA-v76p-62qx-wwq2). 3.2.0 fixes it but fails every Release build without a
    // Six Labors license key, so the app refuses TIFF (docs/shared-site-security.md).
    public static class TiffDecodingGuard
    {
        public static void RefuseTiff() =>
            Configuration.Default.ImageFormatsManager.SetDecoder(TiffFormat.Instance, RefusingTiffDecoder.Instance);

        private sealed class RefusingTiffDecoder : ImageDecoder
        {
            public static readonly RefusingTiffDecoder Instance = new();

            protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();

            protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();

            protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();

            private static UnknownImageFormatException Refused() => new("TIFF images are not accepted.");
        }
    }
}
