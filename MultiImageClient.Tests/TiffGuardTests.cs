using System.IO;

using MultiImageClient;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;

using Xunit;

namespace MultiImageClientTests
{
    public class TiffGuardTests
    {
        private static MemoryStream Encoded(System.Action<Image<Rgba32>, Stream> save)
        {
            using var image = new Image<Rgba32>(4, 3);
            var stream = new MemoryStream();
            save(image, stream);
            stream.Position = 0;
            return stream;
        }

        [Fact]
        public void RefusedTiffNeitherDecodesNorIdentifies()
        {
            TiffGuard.RefuseTiff();
            using var tiff = Encoded((image, stream) => image.Save(stream, new TiffEncoder()));

            Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>(tiff));
            tiff.Position = 0;
            Assert.Throws<UnknownImageFormatException>(() => Image.Identify(tiff));
        }

        [Fact]
        public void RefusedTiffIsNotWritten()
        {
            TiffGuard.RefuseTiff();
            using var image = new Image<Rgba32>(4, 3);
            using var stream = new MemoryStream();

            Assert.Throws<UnknownImageFormatException>(() => image.SaveAsTiff(stream));
            Assert.Equal(0, stream.Length);
        }

        [Fact]
        public void OtherFormatsStillDecode()
        {
            TiffGuard.RefuseTiff();
            using var png = Encoded((image, stream) => image.SaveAsPng(stream));
            using var jpeg = Encoded((image, stream) => image.SaveAsJpeg(stream));

            using var fromPng = Image.Load<Rgba32>(png);
            using var fromJpeg = Image.Load<Rgba32>(jpeg);
            Assert.Equal(4, fromPng.Width);
            Assert.Equal(3, fromJpeg.Height);
        }
    }
}
