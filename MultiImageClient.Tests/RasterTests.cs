using System;
using System.IO;
using System.Linq;
using System.Text;

using SkiaSharp;

using Xunit;

namespace MultiImageClient.Tests
{
    public class RasterTests
    {
        private static readonly SKColor[] Palette =
        {
            SKColor.Parse("E53935"), SKColor.Parse("1E88E5"), SKColor.Parse("43A047"), SKColor.Parse("FB8C00"),
            SKColor.Parse("8E24AA"), SKColor.Parse("00ACC1"), SKColor.Parse("FDD835"), SKColor.Parse("6D4C41"),
        };

        [Fact]
        public void IdentifyReportsTheFiveAcceptedFormats()
        {
            using var image = Raster.Create(6, 4, SKColors.Teal);
            Assert.Equal(new RasterInfo(RasterFormat.Png, 6, 4), Raster.Identify(Raster.EncodePng(image)));
            Assert.Equal(new RasterInfo(RasterFormat.Jpeg, 6, 4), Raster.Identify(Raster.EncodeJpeg(image, 90)));
            using var webp = image.Encode(SKEncodedImageFormat.Webp, 90);
            Assert.Equal(new RasterInfo(RasterFormat.Webp, 6, 4), Raster.Identify(webp.ToArray()));
            Assert.Equal(new RasterInfo(RasterFormat.Gif, 1, 1), Raster.Identify(OnePixelGif));
            Assert.Equal(new RasterInfo(RasterFormat.Bmp, 2, 1), Raster.Identify(TwoPixelBmp()));
            Assert.Equal("image/webp", new RasterInfo(RasterFormat.Webp, 1, 1).MimeType);
        }

        [Fact]
        public void IdentifyRefusesOtherFormatsAndNonImages()
        {
            using var image = Raster.Create(16, 16, SKColors.Teal);
            var ico = IcoWrapping(Raster.EncodePng(image));
            var icoError = Assert.Throws<InvalidDataException>(() => Raster.Identify(ico));
            Assert.Contains("Ico", icoError.Message);
            Assert.Throws<InvalidDataException>(() => Raster.Decode(ico));

            var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            Assert.Throws<InvalidDataException>(() => Raster.Identify(tiff));
            Assert.Throws<InvalidDataException>(() => Raster.Identify(Encoding.ASCII.GetBytes("not an image at all")));
            Assert.Throws<InvalidDataException>(() => Raster.Identify(Array.Empty<byte>()));
        }

        [Fact]
        public void PixelLimitRefusesAHugeHeaderBeforeDecoding()
        {
            using var image = Raster.Create(4, 4, SKColors.Teal);
            var png = WithPngSize(Raster.EncodePng(image), 20_000, 20_000);
            var error = Assert.Throws<InvalidDataException>(() => Raster.Identify(png));
            Assert.Contains("20000x20000", error.Message);
            Assert.Throws<InvalidDataException>(() => Raster.Decode(png));
            Assert.Throws<InvalidDataException>(() => Raster.Create(20_000, 20_000, SKColors.White));
        }

        [Fact]
        public void ExifOrientationRotatesDimensionsAndPixels()
        {
            var stored = QuadrantJpeg();
            var red = SKColors.Red;
            var blue = SKColors.Blue;
            var green = new SKColor(0, 255, 0);
            var yellow = SKColors.Yellow;

            var rotatedClockwise = WithExifOrientation(stored, 6);
            Assert.Equal(new RasterInfo(RasterFormat.Jpeg, 32, 64), Raster.Identify(rotatedClockwise));
            using (var bitmap = Raster.Decode(rotatedClockwise))
            {
                Assert.Equal((32, 64), (bitmap.Width, bitmap.Height));
                Assert.Equal(green, Nearest(bitmap.GetPixel(8, 16), red, blue, green, yellow));
                Assert.Equal(red, Nearest(bitmap.GetPixel(24, 16), red, blue, green, yellow));
                Assert.Equal(yellow, Nearest(bitmap.GetPixel(8, 48), red, blue, green, yellow));
                Assert.Equal(blue, Nearest(bitmap.GetPixel(24, 48), red, blue, green, yellow));
            }

            var rotatedCounterClockwise = WithExifOrientation(stored, 8);
            using (var bitmap = Raster.Decode(rotatedCounterClockwise))
            {
                Assert.Equal((32, 64), (bitmap.Width, bitmap.Height));
                Assert.Equal(blue, Nearest(bitmap.GetPixel(8, 16), red, blue, green, yellow));
                Assert.Equal(yellow, Nearest(bitmap.GetPixel(24, 16), red, blue, green, yellow));
                Assert.Equal(red, Nearest(bitmap.GetPixel(8, 48), red, blue, green, yellow));
                Assert.Equal(green, Nearest(bitmap.GetPixel(24, 48), red, blue, green, yellow));
            }
        }

        [Fact]
        public void PngRoundTripKeepsExactPaletteColors()
        {
            using var image = Raster.Create(80, 10, SKColors.White);
            using (var canvas = new SKCanvas(image))
            {
                for (var i = 0; i < Palette.Length; i++)
                {
                    canvas.FillRect(SKRect.Create(i * 10, 0, 10, 10), Palette[i]);
                }
            }
            using var decoded = Raster.Decode(Raster.EncodePng(image));
            for (var x = 0; x < 80; x++)
            {
                for (var y = 0; y < 10; y++)
                {
                    Assert.Equal(Palette[x / 10], decoded.GetPixel(x, y));
                }
            }
        }

        [Fact]
        public void JpegNeedsFlatteningAndFlattenUsesTheBackground()
        {
            using var image = Raster.Create(8, 8, SKColors.Transparent);
            using (var canvas = new SKCanvas(image))
            {
                canvas.FillRect(SKRect.Create(0, 0, 4, 8), SKColors.Black);
            }
            Assert.True(Raster.HasTranslucentPixels(image));
            Assert.Throws<InvalidOperationException>(() => Raster.EncodeJpeg(image, 90));

            using var flat = Raster.Flatten(image, SKColors.White);
            Assert.False(Raster.HasTranslucentPixels(flat));
            using var decoded = Raster.Decode(Raster.EncodeJpeg(flat, 95));
            Assert.True(decoded.GetPixel(6, 4).Red > 240);
            Assert.True(decoded.GetPixel(1, 4).Red < 15);
        }

        [Fact]
        public void ResizeCopiesSameSizeExactlyAndShrinksToTheRequestedSize()
        {
            using var image = Raster.Create(200, 100, SKColors.White);
            image.SetPixel(10, 10, SKColors.Black);
            using (var same = Raster.Resize(image, 200, 100))
            {
                Assert.Equal(SKColors.Black, same.GetPixel(10, 10));
                Assert.Equal(SKColors.White, same.GetPixel(11, 10));
            }
            using var smaller = Raster.Resize(image, 37, 19);
            Assert.Equal((37, 19), (smaller.Width, smaller.Height));
            using var fitted = Raster.FitWithin(image, 50, 50);
            Assert.Equal((50, 25), (fitted.Width, fitted.Height));
            Assert.Same(image, Raster.FitWithin(image, 400, 400));
        }

        [Fact]
        public void DrawnTextStaysInsideItsMeasuredBounds()
        {
            using var image = Raster.Create(500, 300, SKColors.White);
            var options = new TextBlockOptions(FontUtils.CreateFont(24, FontStyle.Bold))
            {
                Origin = new SKPoint(20, 30),
                WrappingLength = 300,
                LineSpacing = 1.2f,
            };
            const string text = "Hgypj quick brown foxes jump over lazy dogs again and again";
            var bounds = TextLayout.Measure(text, options);
            using (var canvas = new SKCanvas(image))
            {
                canvas.DrawTextStandard(options, text, SKColors.Black);
            }

            var inked = 0;
            var allowed = SKRect.Inflate(bounds, 2, 2);
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    if (image.GetPixel(x, y) == SKColors.White) continue;
                    inked++;
                    Assert.True(allowed.Contains(x + 0.5f, y + 0.5f), $"ink at {x},{y} lies outside {bounds}");
                }
            }
            Assert.True(inked > 500);
            Assert.True(bounds.Width <= 300);
            var oneLine = TextLayout.Measure("Hgypj", options);
            Assert.True(bounds.Height > oneLine.Height * 2);
        }

        [Fact]
        public void WrappingBreaksAWordWiderThanTheLine()
        {
            var options = new TextBlockOptions(FontUtils.CreateFont(20)) { WrappingLength = 120 };
            var bounds = TextLayout.Measure(new string('W', 60), options);
            Assert.True(bounds.Width <= 120);
            Assert.True(bounds.Height > TextLayout.Measure("W", options).Height * 3);
            Assert.Equal(0, TextLayout.Measure("", options).Height);
        }

        [Fact]
        public void SvgRendersAtItsDeclaredSizeOnWhite()
        {
            var svg = Svg("<rect x=\"0\" y=\"0\" width=\"20\" height=\"30\" fill=\"#ff0000\"/>");
            using var bitmap = SvgRaster.Rasterize(svg, "test.svg");
            Assert.Equal((40, 30), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 15));
            Assert.Equal(SKColors.White, bitmap.GetPixel(30, 15));
        }

        [Theory]
        [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"/>")]
        [InlineData("<?xml-stylesheet href=\"http://example.invalid/x.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"/>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><script>alert(1)</script></svg>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\" onload=\"alert(1)\"/>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"4\" height=\"4\"><use xlink:href=\"file:///etc/passwd#x\"/></svg>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><image href=\"https://example.invalid/a.png\" width=\"4\" height=\"4\"/></svg>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" fill=\"url(https://example.invalid/p.svg#g)\"/></svg>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><style>@import url(https://example.invalid/s.css);</style></svg>")]
        [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><foreignObject width=\"4\" height=\"4\"/></svg>")]
        [InlineData("<html><body/></html>")]
        public void SvgValidatorRefusesUnsafeContent(string document)
        {
            Assert.Throws<InvalidDataException>(() => SvgRaster.Rasterize(Encoding.UTF8.GetBytes(document), "unsafe.svg"));
        }

        [Fact]
        public void SvgValidatorAllowsInternalReferences()
        {
            var svg = Svg(
                "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#ff0000\"/></linearGradient></defs>"
                + "<rect width=\"40\" height=\"30\" fill=\"url(#g)\"/>");
            using var bitmap = SvgRaster.Rasterize(svg, "gradient.svg");
            Assert.Equal(SKColors.Red, bitmap.GetPixel(20, 15));
        }

        private static byte[] Svg(string body)
            => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\">" + body + "</svg>");

        private static SKColor Nearest(SKColor actual, params SKColor[] candidates)
            => candidates.OrderBy(c =>
                Math.Pow(c.Red - actual.Red, 2) + Math.Pow(c.Green - actual.Green, 2) + Math.Pow(c.Blue - actual.Blue, 2)).First();

        // 64x32 JPEG: top-left red, top-right blue, bottom-left green, bottom-right yellow.
        private static byte[] QuadrantJpeg()
        {
            using var image = Raster.Create(64, 32, SKColors.White);
            using (var canvas = new SKCanvas(image))
            {
                canvas.FillRect(SKRect.Create(0, 0, 32, 16), SKColors.Red);
                canvas.FillRect(SKRect.Create(32, 0, 32, 16), SKColors.Blue);
                canvas.FillRect(SKRect.Create(0, 16, 32, 16), new SKColor(0, 255, 0));
                canvas.FillRect(SKRect.Create(32, 16, 32, 16), SKColors.Yellow);
            }
            return Raster.EncodeJpeg(image, 100);
        }

        // Inserts an APP1 Exif segment (big-endian TIFF, one Orientation entry) after SOI.
        private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
        {
            var tiff = new byte[]
            {
                0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
                0x00, 0x01,
                0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
            var length = payload.Length + 2;
            var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
            return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
        }

        private static byte[] WithPngSize(byte[] png, int width, int height)
        {
            var patched = (byte[])png.Clone();
            WriteBigEndian(patched, 16, (uint)width);
            WriteBigEndian(patched, 20, (uint)height);
            WriteBigEndian(patched, 29, Crc32(patched, 12, 17));
            return patched;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint Crc32(byte[] data, int offset, int count)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return ~crc;
        }

        private static readonly byte[] OnePixelGif =
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
        };

        // 2x1, 24-bit, bottom-up rows padded to 4 bytes.
        private static byte[] TwoPixelBmp()
        {
            var bmp = new byte[62];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(62).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(2).CopyTo(bmp, 18);
            BitConverter.GetBytes(1).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
            BitConverter.GetBytes(8).CopyTo(bmp, 34);
            bmp[54] = 0xFF;
            bmp[59] = 0xFF;
            return bmp;
        }

        private static byte[] IcoWrapping(byte[] png)
        {
            var header = new byte[22];
            BitConverter.GetBytes((short)1).CopyTo(header, 2);
            BitConverter.GetBytes((short)1).CopyTo(header, 4);
            header[6] = 16;
            header[7] = 16;
            BitConverter.GetBytes((short)1).CopyTo(header, 10);
            BitConverter.GetBytes((short)32).CopyTo(header, 12);
            BitConverter.GetBytes(png.Length).CopyTo(header, 14);
            BitConverter.GetBytes(22).CopyTo(header, 18);
            return header.Concat(png).ToArray();
        }
    }
}
