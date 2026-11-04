#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

using SkiaSharp;

using Svg.Skia;

namespace MultiImageClient
{
    // Rasterizes provider SVG output (Recraft vector models) with Svg.Skia
    // (MIT). Every file is checked before rendering: it must be one
    // self-contained SVG document. Document type declarations, scripts,
    // foreign content, event handlers, processing instructions, and any
    // reference outside the file are refused, so rendering never reads
    // another file or contacts a network address. The library's own external
    // resolution is switched off as well.
    public static class SvgRaster
    {
        private const long MaxSvgBytes = 64L * 1024 * 1024;
        private const int MaxEdge = 16384;
        private const string SvgNamespace = "http://www.w3.org/2000/svg";

        private static readonly string[] RefusedElements =
        {
            "script", "foreignObject", "image", "feImage", "iframe", "object", "embed",
            "audio", "video", "handler", "listener",
        };

        private static readonly Regex ExternalUrl = new(
            @"url\(\s*(?!['""]?\s*#)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        static SvgRaster()
        {
            Svg.SvgDocument.DisableDtdProcessing = true;
            Svg.SvgDocument.ResolveExternalXmlEntites = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.None;
            Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.None;
        }

        public static (int Width, int Height) IntrinsicSize(string path)
        {
            using var svg = Load(File.ReadAllBytes(path), path, out var bounds);
            return SizeOf(bounds, path);
        }

        // Draws the SVG at its declared size onto opaque white, so cards and
        // contact sheets show vector results without transparent areas.
        public static SKBitmap Rasterize(string path) => Rasterize(File.ReadAllBytes(path), path);

        public static SKBitmap Rasterize(byte[] bytes, string sourceLabel)
        {
            using var svg = Load(bytes, sourceLabel, out var bounds);
            var (width, height) = SizeOf(bounds, sourceLabel);
            var bitmap = Raster.Create(width, height, SKColors.White);
            try
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(svg.Picture);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static SKSvg Load(byte[] bytes, string sourceLabel, out SKRect bounds)
        {
            Validate(bytes, sourceLabel);
            var svg = new SKSvg();
            try
            {
                svg.Settings.EnableJavaScript = false;
                svg.Settings.EnableExternalJavaScript = false;
                using var stream = new MemoryStream(bytes, writable: false);
                var picture = svg.Load(stream)
                    ?? throw new InvalidDataException($"The SVG '{sourceLabel}' produced no drawing.");
                bounds = picture.CullRect;
                return svg;
            }
            catch
            {
                svg.Dispose();
                throw;
            }
        }

        private static (int Width, int Height) SizeOf(SKRect bounds, string sourceLabel)
        {
            var width = (int)Math.Ceiling(bounds.Width);
            var height = (int)Math.Ceiling(bounds.Height);
            if (width < 1 || height < 1 || width > MaxEdge || height > MaxEdge)
            {
                throw new InvalidDataException(
                    $"The SVG '{sourceLabel}' declares {bounds.Width}x{bounds.Height}; each side must be 1 to {MaxEdge} pixels.");
            }
            return (width, height);
        }

        private static void Validate(byte[] bytes, string sourceLabel)
        {
            if (bytes.LongLength == 0 || bytes.LongLength > MaxSvgBytes)
            {
                throw new InvalidDataException(
                    $"The SVG '{sourceLabel}' is {bytes.LongLength:N0} bytes; the limit is {MaxSvgBytes:N0}.");
            }
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            var sawRoot = false;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = XmlReader.Create(stream, settings);
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.ProcessingInstruction:
                            throw Refuse(sourceLabel, $"processing instruction <?{reader.Name}?>");
                        case XmlNodeType.Element:
                            if (!sawRoot)
                            {
                                if (reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)
                                {
                                    throw Refuse(sourceLabel, $"root element <{reader.Name}> instead of an SVG <svg> element");
                                }
                                sawRoot = true;
                            }
                            foreach (var refused in RefusedElements)
                            {
                                if (string.Equals(reader.LocalName, refused, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw Refuse(sourceLabel, $"<{reader.Name}> element");
                                }
                            }
                            CheckAttributes(reader, sourceLabel);
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                            if (reader.Value.Contains("@import", StringComparison.OrdinalIgnoreCase)
                                || ExternalUrl.IsMatch(reader.Value))
                            {
                                throw Refuse(sourceLabel, "style text that references another file");
                            }
                            break;
                    }
                }
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The SVG '{sourceLabel}' is not acceptable XML: {ex.Message}", ex);
            }
            if (!sawRoot)
            {
                throw Refuse(sourceLabel, "a document without an <svg> element");
            }
        }

        private static void CheckAttributes(XmlReader reader, string sourceLabel)
        {
            if (!reader.MoveToFirstAttribute()) return;
            do
            {
                var name = reader.LocalName;
                var value = reader.Value;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && reader.NamespaceURI.Length == 0)
                {
                    throw Refuse(sourceLabel, $"event handler attribute {reader.Name}");
                }
                if (name == "href" && !value.TrimStart().StartsWith('#'))
                {
                    throw Refuse(sourceLabel, $"{reader.Name} pointing outside the file");
                }
                if (value.Contains("@import", StringComparison.OrdinalIgnoreCase) || ExternalUrl.IsMatch(value))
                {
                    throw Refuse(sourceLabel, $"attribute {reader.Name} that references another file");
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        private static InvalidDataException Refuse(string sourceLabel, string what)
            => new($"The SVG '{sourceLabel}' was refused: it contains {what}. Only self-contained SVG drawings are rendered.");
    }
}
