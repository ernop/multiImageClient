#nullable enable
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

using SkiaSharp;

namespace MultiImageClient
{
    public enum RasterFormat { Png, Jpeg, Webp, Gif, Bmp }

    // Width and Height are the displayed size: an EXIF rotation of 90 or 270
    // degrees swaps the stored dimensions, exactly as browsers show the file.
    public sealed record RasterInfo(RasterFormat Format, int Width, int Height)
    {
        public string MimeType => Format switch
        {
            RasterFormat.Png => "image/png",
            RasterFormat.Jpeg => "image/jpeg",
            RasterFormat.Webp => "image/webp",
            RasterFormat.Gif => "image/gif",
            RasterFormat.Bmp => "image/bmp",
            _ => throw new ArgumentOutOfRangeException(nameof(Format), Format, null),
        };
    }

    // Every raster identify, decode, resize, and encode in the app goes through
    // this class (SkiaSharp, MIT). Only the five formats in RasterFormat are
    // decoded; Skia's other codecs (ICO, WBMP, HEIF, AVIF, DNG) are refused
    // before any pixel work. Decoded bitmaps keep the source color space, so a
    // re-encoded wide-gamut photo still displays with its own colors.
    public static class Raster
    {
        // 512 MiB at 4 bytes per pixel. Checking it before decoding stops a
        // small file that expands into a huge image.
        public const long MaxPixels = 512L * 1024 * 1024 / 4;

        public static RasterInfo Identify(byte[] bytes)
        {
            using var codec = OpenCodec(bytes);
            return Describe(codec);
        }

        public static RasterInfo Identify(string path)
        {
            using var codec = OpenCodec(path);
            return Describe(codec);
        }

        public static RasterInfo Identify(Stream stream) => Identify(ReadAll(stream));

        public static SKBitmap Decode(byte[] bytes)
        {
            using var codec = OpenCodec(bytes);
            return DecodeOriented(codec, 0, 0);
        }

        public static SKBitmap Decode(string path)
        {
            using var codec = OpenCodec(path);
            return DecodeOriented(codec, 0, 0);
        }

        public static SKBitmap Decode(Stream stream) => Decode(ReadAll(stream));

        // Decodes at a reduced size when the codec supports it (JPEG, WebP),
        // never below twice the requested box, so the final high-quality
        // resize still has full detail. The result is at least as large as
        // the box or the full image, whichever is smaller; callers resize it.
        public static SKBitmap DecodeForBox(string path, int boxWidth, int boxHeight)
        {
            using var codec = OpenCodec(path);
            return DecodeOriented(codec, boxWidth, boxHeight);
        }

        public static SKBitmap DecodeForBox(byte[] bytes, int boxWidth, int boxHeight)
        {
            using var codec = OpenCodec(bytes);
            return DecodeOriented(codec, boxWidth, boxHeight);
        }

        // High-quality resize to an exact size. Enlarging uses Skia's Mitchell
        // cubic filter. Shrinking uses Lanczos3 widened by the shrink factor,
        // so every source pixel contributes and fine detail does not alias.
        public static SKBitmap Resize(SKBitmap source, int width, int height)
        {
            if (width < 1 || height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"Resize target {width}x{height} must be positive.");
            }
            RequirePixelBudget(width, height);
            if (source.Width == width && source.Height == height)
            {
                return source.Copy() ?? throw new InvalidOperationException("Copying the image failed.");
            }
            if (width >= source.Width && height >= source.Height)
            {
                return source.Resize(source.Info.WithSize(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
                    ?? throw new InvalidOperationException($"Resizing the image to {width}x{height} failed.");
            }
            return Shrink(source, width, height);
        }

        private readonly record struct Taps(int Start, float[] Weights);

        // Two separable passes over premultiplied 8-bit pixels: each output
        // row first sums its source rows, then each output pixel sums columns.
        private static SKBitmap Shrink(SKBitmap source, int width, int height)
        {
            if (source.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)
                || source.AlphaType == SKAlphaType.Unpremul)
            {
                throw new NotSupportedException($"Resizing does not handle {source.ColorType} {source.AlphaType} pixels.");
            }
            var result = new SKBitmap(source.Info.WithSize(width, height));
            if (result.GetPixels() == IntPtr.Zero)
            {
                result.Dispose();
                throw new OutOfMemoryException($"Could not allocate a {width}x{height} image.");
            }
            var rowTaps = LanczosTaps(source.Height, height);
            var columnTaps = LanczosTaps(source.Width, width);
            ReadOnlySpan<byte> pixels = source.GetPixelSpan();
            var output = result.GetPixelSpan();
            var rowBytes = source.Width * 4;
            var rowSums = new float[rowBytes];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(rowSums);
                var (start, weights) = rowTaps[y];
                for (var k = 0; k < weights.Length; k++)
                {
                    AccumulateRow(pixels.Slice((start + k) * source.RowBytes, rowBytes), rowSums, weights[k]);
                }
                ReadOnlySpan<Vector4> sumPixels = MemoryMarshal.Cast<float, Vector4>(rowSums);
                var target = output.Slice(y * result.RowBytes, width * 4);
                for (var x = 0; x < width; x++)
                {
                    var (first, columnWeights) = columnTaps[x];
                    var sum = Vector4.Zero;
                    for (var k = 0; k < columnWeights.Length; k++)
                    {
                        sum += sumPixels[first + k] * columnWeights[k];
                    }
                    // Lanczos lobes can overshoot; premultiplied color never exceeds alpha.
                    var alpha = Math.Clamp(sum.W, 0f, 255f);
                    var p = x * 4;
                    target[p] = (byte)(Math.Clamp(sum.X, 0f, alpha) + 0.5f);
                    target[p + 1] = (byte)(Math.Clamp(sum.Y, 0f, alpha) + 0.5f);
                    target[p + 2] = (byte)(Math.Clamp(sum.Z, 0f, alpha) + 0.5f);
                    target[p + 3] = (byte)(alpha + 0.5f);
                }
            }
            result.NotifyPixelsChanged();
            return result;
        }

        private static void AccumulateRow(ReadOnlySpan<byte> row, Span<float> sums, float weight)
        {
            var i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var scale = new Vector<float>(weight);
                var lanes = Vector<float>.Count;
                for (; i + Vector<byte>.Count <= row.Length; i += Vector<byte>.Count)
                {
                    Vector.Widen(new Vector<byte>(row.Slice(i)), out Vector<ushort> low, out Vector<ushort> high);
                    Vector.Widen(low, out Vector<uint> a, out Vector<uint> b);
                    Vector.Widen(high, out Vector<uint> c, out Vector<uint> d);
                    AddScaled(sums.Slice(i, lanes), a, scale);
                    AddScaled(sums.Slice(i + lanes, lanes), b, scale);
                    AddScaled(sums.Slice(i + 2 * lanes, lanes), c, scale);
                    AddScaled(sums.Slice(i + 3 * lanes, lanes), d, scale);
                }
            }
            for (; i < row.Length; i++)
            {
                sums[i] += row[i] * weight;
            }
        }

        private static void AddScaled(Span<float> slot, Vector<uint> values, Vector<float> scale)
            => (new Vector<float>(slot) + Vector.ConvertToSingle(Vector.AsVectorInt32(values)) * scale).CopyTo(slot);

        private static Taps[] LanczosTaps(int sourceSize, int targetSize)
        {
            var scale = (double)sourceSize / targetSize;
            var stretch = Math.Max(1.0, scale);
            var radius = 3.0 * stretch;
            var taps = new Taps[targetSize];
            for (var i = 0; i < targetSize; i++)
            {
                var center = (i + 0.5) * scale;
                var start = Math.Max(0, (int)Math.Floor(center - radius));
                var end = Math.Min(sourceSize - 1, (int)Math.Ceiling(center + radius));
                var weights = new double[end - start + 1];
                var total = 0.0;
                for (var s = start; s <= end; s++)
                {
                    total += weights[s - start] = Lanczos3((s + 0.5 - center) / stretch);
                }
                taps[i] = new Taps(start, Array.ConvertAll(weights, w => (float)(w / total)));
            }
            return taps;
        }

        private static double Lanczos3(double x)
        {
            x = Math.Abs(x);
            if (x >= 3) return 0;
            if (x < 1e-9) return 1;
            var px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }

        // Scales to fit inside the box, keeping the aspect ratio. Returns the
        // source itself (not a copy) when it already fits.
        public static SKBitmap FitWithin(SKBitmap source, int maxWidth, int maxHeight)
        {
            var scale = Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height);
            if (scale >= 1.0) return source;
            return Resize(
                source,
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale)));
        }

        public static SKBitmap Create(int width, int height, SKColor background)
        {
            if (width < 1 || height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"Image size {width}x{height} must be positive.");
            }
            RequirePixelBudget(width, height);
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            if (bitmap.GetPixels() == IntPtr.Zero)
            {
                bitmap.Dispose();
                throw new OutOfMemoryException($"Could not allocate a {width}x{height} image.");
            }
            bitmap.Erase(background);
            return bitmap;
        }

        // JPEG has no alpha channel: composite onto an opaque background first
        // so transparent areas do not turn black.
        public static SKBitmap Flatten(SKBitmap source, SKColor background)
        {
            var flat = Create(source.Width, source.Height, background);
            using var canvas = new SKCanvas(flat);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            return flat;
        }

        public static bool HasTranslucentPixels(SKBitmap bitmap)
        {
            if (bitmap.AlphaType == SKAlphaType.Opaque) return false;
            if (bitmap.BytesPerPixel != 4)
            {
                throw new NotSupportedException($"Alpha scan expects 4-byte pixels, not {bitmap.ColorType}.");
            }
            var alphaOffset = bitmap.ColorType switch
            {
                SKColorType.Bgra8888 or SKColorType.Rgba8888 => 3,
                _ => throw new NotSupportedException($"Alpha scan does not handle {bitmap.ColorType}."),
            };
            var pixels = bitmap.GetPixelSpan();
            var rowBytes = bitmap.RowBytes;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = pixels.Slice(y * rowBytes, bitmap.Width * 4);
                for (var i = alphaOffset; i < row.Length; i += 4)
                {
                    if (row[i] != 255) return true;
                }
            }
            return false;
        }

        public static byte[] EncodePng(SKBitmap bitmap)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException($"PNG encoding of a {bitmap.Width}x{bitmap.Height} image failed.");
            return data.ToArray();
        }

        public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
        {
            if (bitmap.AlphaType != SKAlphaType.Opaque && HasTranslucentPixels(bitmap))
            {
                throw new InvalidOperationException("JPEG encoding needs an opaque image; flatten it first.");
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidOperationException($"JPEG encoding of a {bitmap.Width}x{bitmap.Height} image failed.");
            return data.ToArray();
        }

        // Streams the PNG straight to the file, with no full-size buffer.
        public static void SavePng(SKBitmap bitmap, string path)
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var stream = new SKManagedWStream(file))
            {
                if (bitmap.Encode(stream, SKEncodedImageFormat.Png, 100)) return;
            }
            File.Delete(path);
            throw new InvalidOperationException($"PNG encoding of a {bitmap.Width}x{bitmap.Height} image failed for {path}.");
        }

        private static SKCodec OpenCodec(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidDataException("The image has no bytes.");
            }
            var data = SKData.CreateCopy(bytes);
            var codec = SKCodec.Create(data);
            if (codec == null)
            {
                data.Dispose();
                throw new InvalidDataException("The bytes are not a readable PNG, JPEG, WebP, GIF, or BMP image.");
            }
            data.Dispose();
            return codec;
        }

        private static SKCodec OpenCodec(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Image file not found: {path}", path);
            }
            return SKCodec.Create(path, out var result)
                ?? throw new InvalidDataException(
                    $"'{path}' is not a readable PNG, JPEG, WebP, GIF, or BMP image ({result}).");
        }

        private static RasterInfo Describe(SKCodec codec)
        {
            var format = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Png => RasterFormat.Png,
                SKEncodedImageFormat.Jpeg => RasterFormat.Jpeg,
                SKEncodedImageFormat.Webp => RasterFormat.Webp,
                SKEncodedImageFormat.Gif => RasterFormat.Gif,
                SKEncodedImageFormat.Bmp => RasterFormat.Bmp,
                var other => throw new InvalidDataException(
                    $"{other} images are not accepted; use PNG, JPEG, WebP, GIF, or BMP."),
            };
            var (width, height) = OrientedSize(codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
            if (width < 1 || height < 1)
            {
                throw new InvalidDataException($"The image reports invalid dimensions {width}x{height}.");
            }
            RequirePixelBudget(width, height);
            return new RasterInfo(format, width, height);
        }

        private static SKBitmap DecodeOriented(SKCodec codec, int boxWidth, int boxHeight)
        {
            Describe(codec);
            var origin = codec.EncodedOrigin;
            var stored = codec.Info;
            var size = new SKSizeI(stored.Width, stored.Height);
            if (boxWidth > 0 && boxHeight > 0)
            {
                var (shownWidth, shownHeight) = OrientedSize(stored.Width, stored.Height, origin);
                var scale = Math.Min(1.0, 2.0 * Math.Min((double)boxWidth / shownWidth, (double)boxHeight / shownHeight));
                if (scale < 1.0)
                {
                    var scaled = codec.GetScaledDimensions((float)scale);
                    if (scaled.Width >= 1 && scaled.Height >= 1
                        && scaled.Width <= stored.Width && scaled.Height <= stored.Height)
                    {
                        size = scaled;
                    }
                }
            }
            var info = new SKImageInfo(
                size.Width,
                size.Height,
                SKImageInfo.PlatformColorType,
                stored.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul,
                stored.ColorSpace);
            var bitmap = new SKBitmap(info);
            try
            {
                if (bitmap.GetPixels() == IntPtr.Zero)
                {
                    throw new OutOfMemoryException($"Could not allocate a {size.Width}x{size.Height} image.");
                }
                var result = codec.GetPixels(info, bitmap.GetPixels());
                if (result != SKCodecResult.Success)
                {
                    throw new InvalidDataException($"The {codec.EncodedFormat} image could not be decoded completely ({result}).");
                }
                return ApplyOrigin(bitmap, origin);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static (int Width, int Height) OrientedSize(int width, int height, SKEncodedOrigin origin)
            => SwapsAxes(origin) ? (height, width) : (width, height);

        private static bool SwapsAxes(SKEncodedOrigin origin)
            => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // Takes ownership of source. The matrices map stored pixel coordinates
        // to displayed coordinates for the eight EXIF orientations.
        private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
        {
            if (origin == SKEncodedOrigin.TopLeft) return source;
            float w = source.Width;
            float h = source.Height;
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
                _ => throw new InvalidDataException($"Unknown EXIF orientation {origin}."),
            };
            var (width, height) = OrientedSize(source.Width, source.Height, origin);
            try
            {
                var turned = new SKBitmap(source.Info.WithSize(width, height));
                using (var canvas = new SKCanvas(turned))
                {
                    canvas.SetMatrix(matrix);
                    canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
                }
                return turned;
            }
            finally
            {
                source.Dispose();
            }
        }

        private static void RequirePixelBudget(int width, int height)
        {
            if ((long)width * height > MaxPixels)
            {
                throw new InvalidDataException(
                    $"A {width}x{height} image exceeds the {MaxPixels:N0}-pixel limit; it was not decoded.");
            }
        }

        private static byte[] ReadAll(Stream stream)
        {
            if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment)
                && segment.Offset == 0 && segment.Count == segment.Array!.Length && memory.Position == 0)
            {
                return segment.Array;
            }
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
