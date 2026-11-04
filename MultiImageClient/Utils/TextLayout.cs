#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace MultiImageClient
{
    // Lays out, measures, and draws plain text blocks for contact sheets,
    // labels, and maps. Each run of text is shaped with HarfBuzz (kerning,
    // ligatures, joined scripts). Characters the primary face lacks (CJK,
    // emoji, other scripts) come from the system face that has them, so text
    // never silently draws as empty boxes when a face exists for it.
    //
    // Measured bounds are line boxes: lines * line height, where line height
    // is the face's ascent + descent + gap times LineSpacing. The block's top
    // is the first line's ascent line.
    public static class TextLayout
    {
        // Fallback lookups are cached per (face, style, code point); the cap
        // bounds memory for long-running servers that see many scripts.
        private const int MaxFallbackEntries = 4096;

        private static readonly ConcurrentDictionary<(string Family, int Weight, int CodePoint), SKTypeface?> Fallbacks = new();

        // One shaper per installed face. Shaping is read-only on the shared
        // HarfBuzz font, so concurrent use is safe. Bounded by the number of
        // installed font faces.
        private static readonly ConcurrentDictionary<SKTypeface, SKShaper> Shapers = new();

        public static SKRect Measure(string text, TextBlockOptions options)
        {
            using var block = Block.Create(text, options);
            return block.Bounds;
        }

        // Distance from a block's top to its first baseline, in pixels.
        public static float BaselineOffset(TextBlockOptions options)
        {
            using var block = Block.Create(string.Empty, options);
            return block.Ascent;
        }

        public static void Draw(SKCanvas canvas, TextBlockOptions options, string text, SKColor color)
        {
            using var block = Block.Create(text, options);
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
            block.Draw(canvas, paint);
        }

        private static SKShaper ShaperFor(SKTypeface face) => Shapers.GetOrAdd(face, f => new SKShaper(f));

        private static SKTypeface? FallbackFor(SKTypeface primary, int codePoint)
        {
            var key = (primary.FamilyName, primary.FontStyle.Weight, codePoint);
            if (Fallbacks.TryGetValue(key, out var cached)) return cached;
            if (Fallbacks.Count >= MaxFallbackEntries) Fallbacks.Clear();
            var found = SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, null, codePoint);
            Fallbacks[key] = found;
            return found;
        }

        private readonly record struct Run(SKTypeface Face, string Text, float Width);

        private sealed class Line
        {
            public Line(List<Run> runs, float width)
            {
                Runs = runs;
                Width = width;
            }

            public List<Run> Runs { get; }
            public float Width { get; }
        }

        private sealed class Block : IDisposable
        {
            private readonly TextBlockOptions _options;
            private readonly float _pixelSize;
            private readonly Dictionary<SKTypeface, SKFont> _fonts = new();
            private readonly Dictionary<string, float> _wordWidths = new(StringComparer.Ordinal);
            private readonly List<Line> _lines = new();
            private readonly SKFont _primary;
            private readonly float _ascent;
            private readonly float _lineHeight;
            private readonly float _height;
            private float _width;

            private Block(TextBlockOptions options)
            {
                _options = options;
                _pixelSize = options.Font.Size * options.Dpi / 72f;
                if (!(_pixelSize > 0))
                {
                    throw new ArgumentOutOfRangeException(nameof(options), $"Text size {_pixelSize}px must be positive.");
                }
                _primary = FontFor(options.Font.Typeface);
                var metrics = _primary.Metrics;
                _ascent = -metrics.Ascent;
                _lineHeight = _primary.Spacing * options.LineSpacing;
                _height = _ascent + metrics.Descent;
            }

            public static Block Create(string text, TextBlockOptions options)
            {
                var block = new Block(options);
                try
                {
                    block.LayOut(text ?? string.Empty);
                    return block;
                }
                catch
                {
                    block.Dispose();
                    throw;
                }
            }

            public float Ascent => _ascent;

            public float Height => _lines.Count == 0 ? 0 : (_lines.Count - 1) * _lineHeight + _height;

            public SKRect Bounds
            {
                get
                {
                    if (_lines.Count == 0) return SKRect.Create(_options.Origin.X, _options.Origin.Y, 0, 0);
                    var left = _options.HorizontalAlignment switch
                    {
                        HorizontalAlignment.Center => _options.Origin.X - _width / 2,
                        HorizontalAlignment.Right => _options.Origin.X - _width,
                        _ => _options.Origin.X,
                    };
                    return SKRect.Create(left, Top, _width, Height);
                }
            }

            private float Top => _options.VerticalAlignment switch
            {
                VerticalAlignment.Center => _options.Origin.Y - Height / 2,
                VerticalAlignment.Bottom => _options.Origin.Y - Height,
                _ => _options.Origin.Y,
            };

            public void Draw(SKCanvas canvas, SKPaint paint)
            {
                var baseline = Top + _ascent;
                foreach (var line in _lines)
                {
                    var x = _options.HorizontalAlignment switch
                    {
                        HorizontalAlignment.Center => _options.Origin.X - line.Width / 2,
                        HorizontalAlignment.Right => _options.Origin.X - line.Width,
                        _ => _options.Origin.X,
                    };
                    foreach (var run in line.Runs)
                    {
                        canvas.DrawShapedText(ShaperFor(run.Face), run.Text, x, baseline, SKTextAlign.Left, FontFor(run.Face), paint);
                        x += run.Width;
                    }
                    baseline += _lineHeight;
                }
            }

            public void Dispose()
            {
                foreach (var font in _fonts.Values) font.Dispose();
                _fonts.Clear();
            }

            private void LayOut(string text)
            {
                if (text.Length == 0) return;
                var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
                foreach (var paragraph in normalized.Split('\n'))
                {
                    foreach (var lineText in Wrap(paragraph))
                    {
                        AddLine(lineText);
                    }
                }
            }

            private void AddLine(string lineText)
            {
                var runs = SplitRuns(lineText);
                var width = 0f;
                foreach (var run in runs) width += run.Width;
                _lines.Add(new Line(runs, width));
                _width = Math.Max(_width, width);
            }

            // Greedy word wrap. Spaces between words on one line are kept;
            // the spaces where a line breaks are dropped. A word wider than
            // the line breaks between characters.
            private List<string> Wrap(string paragraph)
            {
                var max = _options.WrappingLength;
                if (max <= 0 || paragraph.Length == 0 || WidthOf(paragraph) <= max)
                {
                    return new List<string> { paragraph };
                }

                var lines = new List<string>();
                var line = new StringBuilder();
                var lineWidth = 0f;
                var hasWord = false;
                var index = 0;
                var pending = TakeSpaces(paragraph, ref index);
                while (index < paragraph.Length)
                {
                    var word = TakeWord(paragraph, ref index);
                    var wordWidth = WidthOf(word);
                    var pendingWidth = pending.Length > 0 ? WidthOf(pending) : 0f;
                    if (lineWidth + pendingWidth + wordWidth <= max)
                    {
                        line.Append(pending).Append(word);
                        lineWidth += pendingWidth + wordWidth;
                    }
                    else
                    {
                        if (hasWord) lines.Add(line.ToString());
                        line.Clear();
                        if (wordWidth <= max)
                        {
                            line.Append(word);
                            lineWidth = wordWidth;
                        }
                        else
                        {
                            var pieces = BreakWord(word, max);
                            for (var i = 0; i < pieces.Count - 1; i++) lines.Add(pieces[i]);
                            line.Append(pieces[^1]);
                            lineWidth = WidthOf(pieces[^1]);
                        }
                    }
                    hasWord = true;
                    pending = TakeSpaces(paragraph, ref index);
                }
                lines.Add(line.ToString());
                return lines;
            }

            private List<string> BreakWord(string word, float max)
            {
                var pieces = new List<string>();
                var piece = new StringBuilder();
                var elements = StringInfo.GetTextElementEnumerator(word);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement();
                    if (piece.Length > 0 && WidthOf(piece + element) > max)
                    {
                        pieces.Add(piece.ToString());
                        piece.Clear();
                    }
                    piece.Append(element);
                }
                pieces.Add(piece.ToString());
                return pieces;
            }

            private static string TakeSpaces(string text, ref int index)
            {
                var start = index;
                while (index < text.Length && text[index] == ' ') index++;
                return text.Substring(start, index - start);
            }

            private static string TakeWord(string text, ref int index)
            {
                var start = index;
                while (index < text.Length && text[index] != ' ') index++;
                return text.Substring(start, index - start);
            }

            private float WidthOf(string text)
            {
                if (_wordWidths.TryGetValue(text, out var cached)) return cached;
                var width = 0f;
                foreach (var run in SplitRuns(text)) width += run.Width;
                if (text.Length <= 64) _wordWidths[text] = width;
                return width;
            }

            private List<Run> SplitRuns(string text)
            {
                var runs = new List<Run>();
                if (text.Length == 0) return runs;
                var primary = _options.Font.Typeface;
                var builder = new StringBuilder();
                SKTypeface? current = null;
                var elements = StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    var element = elements.GetTextElement();
                    var face = primary;
                    if (element[0] != ' ' && !_primary.ContainsGlyphs(element))
                    {
                        var codePoint = Rune.DecodeFromUtf16(element, out var rune, out _) == System.Buffers.OperationStatus.Done
                            ? rune.Value
                            : Rune.ReplacementChar.Value;
                        face = FallbackFor(primary, codePoint) ?? primary;
                    }
                    if (current != null && !ReferenceEquals(face, current))
                    {
                        runs.Add(Shape(current, builder.ToString()));
                        builder.Clear();
                    }
                    current = face;
                    builder.Append(element);
                }
                runs.Add(Shape(current!, builder.ToString()));
                return runs;
            }

            private Run Shape(SKTypeface face, string text)
            {
                var result = ShaperFor(face).Shape(text, FontFor(face));
                return new Run(face, text, result.Width);
            }

            private SKFont FontFor(SKTypeface face)
            {
                if (_fonts.TryGetValue(face, out var font)) return font;
                font = new SKFont(face, _pixelSize)
                {
                    Edging = SKFontEdging.Antialias,
                    Subpixel = true,
                    LinearMetrics = true,
                    Hinting = SKFontHinting.None,
                };
                _fonts[face] = font;
                return font;
            }
        }
    }
}
