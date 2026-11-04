using System.IO;
using System.Threading;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;

namespace MultiImageClient
{
    // ImageSharp 3.1.12's TIFF codec has five advisories, among them heap writes past
    // buffers (docs/shared-site-security.md). 3.2.0 fixes them but fails every Release
    // build without a Six Labors license key, so the app neither reads nor writes TIFF.
    public static class TiffGuard
    {
        public static void RefuseTiff()
        {
            var formats = Configuration.Default.ImageFormatsManager;
            formats.SetDecoder(TiffFormat.Instance, RefusingTiffDecoder.Instance);
            formats.SetEncoder(TiffFormat.Instance, RefusingTiffEncoder.Instance);
        }

        private static UnknownImageFormatException Refused() => new("TIFF images are not accepted.");

        private sealed class RefusingTiffDecoder : ImageDecoder
        {
            public static readonly RefusingTiffDecoder Instance = new();

            protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();

            protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();

            protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();
        }

        private sealed class RefusingTiffEncoder : ImageEncoder
        {
            public static readonly RefusingTiffEncoder Instance = new();

            protected override void Encode<TPixel>(Image<TPixel> image, Stream stream, CancellationToken cancellationToken) =>
                throw Refused();
        }
    }
}
