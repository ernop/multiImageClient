#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MultiImageClient
{
    public sealed record UiDescribeSheetReply(int InputIndex, string Text, string Comments);
    public sealed record UiDescribeSheetEndpoint(
        string Key, string Label, string Error, string ExtraText,
        IReadOnlyList<UiDescribeSheetReply> Replies);
    public sealed record UiDescribeSheetData(
        string Prompt, int InputCount, IReadOnlyList<UiDescribeSheetEndpoint> Endpoints);

    // Reads exact persisted results, including historical model names. Rendering never calls a provider.
    public static class UiDescribeSheet
    {
        private const int Margin = 56;
        private const int Gap = 32;
        private const int Pad = 28;
        private const int MaxPixels = 32_000_000;
        private static readonly Color Ink = Color.ParseHex("142B3B");
        private static readonly Color Blue = Color.ParseHex("075787");
        private static readonly Color Red = Color.ParseHex("A22222");
        private static readonly Color Paper = Color.ParseHex("F3F7FA");

        public static UiDescribeSheetData Read(
            string prompt, int inputCount, IReadOnlyList<string> keys, IEnumerable<string> events)
        {
            if (inputCount is < 1 or > 4)
                throw new InvalidDataException("A describe sheet requires one to four recorded input images.");
            var selected = keys.Where(UiJobRunner.IsAnalysisKey).ToArray();
            if (selected.Length == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
                throw new InvalidDataException("The job has no unique describe or layout endpoints.");
            var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var extras = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var json in events)
            {
                using var doc = JsonDocument.Parse(json);
                var evt = doc.RootElement;
                var type = evt.GetProperty("type").GetString();
                if (type == "accepted" && evt.TryGetProperty("generatorExtraTexts", out var extraTexts))
                {
                    foreach (var entry in extraTexts.EnumerateObject())
                        extras.Add(entry.Name, entry.Value.GetString() ?? "");
                }
                if (type != "gen-result") continue;
                var key = evt.GetProperty("gen").GetString()!;
                if (!selected.Contains(key, StringComparer.Ordinal)) continue;
                if (!results.TryAdd(key, evt.Clone()))
                    throw new InvalidDataException($"Multiple recorded results exist for {key}.");
            }
            var endpoints = new List<UiDescribeSheetEndpoint>();
            foreach (var key in selected)
            {
                if (!results.TryGetValue(key, out var evt))
                    throw new InvalidDataException($"No completed result was recorded for {key}.");
                var label = evt.GetProperty("label").GetString();
                if (string.IsNullOrWhiteSpace(label))
                    throw new InvalidDataException($"The recorded model name is missing for {key}.");
                if (key == UiJobRunner.KeyDescribeIdeogram)
                    label = label.Replace(" (fixed instruction; the prompt is not sent)", "", StringComparison.Ordinal);
                var replies = new List<UiDescribeSheetReply>();
                var error = "";
                if (!evt.GetProperty("ok").GetBoolean())
                {
                    error = evt.GetProperty("error").GetString();
                    if (string.IsNullOrWhiteSpace(error))
                        throw new InvalidDataException($"The recorded failure has no explanation for {key}.");
                }
                else if (UiJobRunner.IsLayoutMapKey(key))
                {
                    var images = evt.GetProperty("images");
                    if (images.GetArrayLength() != inputCount || images.EnumerateArray().Any(x => string.IsNullOrWhiteSpace(x.GetString())))
                        throw new InvalidDataException("The recorded layout maps do not cover every input image.");
                }
                else
                {
                    foreach (var entry in evt.GetProperty("texts").EnumerateArray())
                    {
                        var index = entry.GetProperty("inputIndex").GetInt32();
                        var text = entry.GetProperty("text").GetString();
                        if (index < 0 || index >= inputCount || replies.Any(x => x.InputIndex == index) || string.IsNullOrWhiteSpace(text))
                            throw new InvalidDataException($"The recorded descriptions have invalid input identities for {key}.");
                        var comments = entry.TryGetProperty("comments", out var value) ? value.GetString() : "";
                        replies.Add(new(index, text, comments ?? ""));
                    }
                    if (replies.Count != inputCount)
                        throw new InvalidDataException($"The recorded descriptions do not cover every input for {key}.");
                }
                endpoints.Add(new(key, label, error, extras.GetValueOrDefault(key, ""), replies));
            }
            return new(prompt, inputCount, endpoints);
        }

        private sealed record TextPlacement(string Text, Font Font, Color Color, Rectangle Bounds);
        private sealed record ImagePlacement(string Path, Rectangle Bounds);

        public static async Task RenderAsync(
            UiDescribeSheetData data, IReadOnlyList<string> inputPaths,
            IReadOnlyDictionary<int, string> layoutPaths, string outputPath, CancellationToken cancellationToken = default)
        {
            if (inputPaths.Count != data.InputCount)
                throw new InvalidDataException("The sheet inputs do not match the recorded input count.");
            var descriptions = data.Endpoints.Where(x => !UiJobRunner.IsLayoutMapKey(x.Key)).ToArray();
            var layout = data.Endpoints.SingleOrDefault(x => UiJobRunner.IsLayoutMapKey(x.Key));
            var columns = Math.Clamp(descriptions.Length, 1, 3);
            var width = columns == 3 ? 2400 : 1800;
            var contentWidth = width - Margin * 2;
            var bodyFont = FontUtils.CreateFont(26);
            var smallFont = FontUtils.CreateFont(22);
            var labelFont = FontUtils.CreateFont(29, FontStyle.Bold);
            var titleFont = FontUtils.CreateFont(46, FontStyle.Bold);
            var texts = new List<TextPlacement>();
            var images = new List<ImagePlacement>();
            var panels = new List<Rectangle>();

            int AddText(string text, Font font, Color color, int x, int y, int wrap)
            {
                if (text.Length == 0) return y;
                if (text.Length > 100_000)
                    throw new InvalidDataException("A text section exceeds the describe sheet limit; no text was shortened.");
                var options = TextOptions(font, wrap);
                var bounds = TextMeasurer.MeasureBounds(text, options);
                var height = checked((int)Math.Ceiling(bounds.Height + font.Size * 0.5f + 8));
                texts.Add(new(text, font, color, new Rectangle(x, y, wrap, height)));
                return checked(y + height);
            }

            int AddImage(string path, int x, int y, int maxWidth, int maxHeight)
            {
                var info = Image.Identify(path);
                if (info.Width <= 0 || info.Height <= 0)
                    throw new InvalidDataException("A sheet image has invalid dimensions.");
                var scale = Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height);
                var w = Math.Max(1, (int)Math.Round(info.Width * scale));
                var h = Math.Max(1, (int)Math.Round(info.Height * scale));
                images.Add(new(path, new Rectangle(x + (maxWidth - w) / 2, y, w, h)));
                return checked(y + h);
            }

            int AddEndpointText(UiDescribeSheetEndpoint endpoint, int input, int x, int y, int wrap)
            {
                y = AddText(endpoint.Label, labelFont, Blue, x, y, wrap) + 16;
                if (endpoint.Key == UiJobRunner.KeyDescribeIdeogram)
                    y = AddText("Fixed instruction · input text is not sent", smallFont, Ink, x, y, wrap) + 14;
                else if (endpoint.ExtraText.Length > 0)
                {
                    y = AddText("Additional input text", smallFont, Blue, x, y, wrap) + 6;
                    y = AddText(endpoint.ExtraText, smallFont, Ink, x, y, wrap) + 18;
                }
                if (endpoint.Error.Length > 0)
                    return AddText("Failed\n" + endpoint.Error, bodyFont, Red, x, y, wrap);
                if (UiJobRunner.IsLayoutMapKey(endpoint.Key)) return y;
                var reply = endpoint.Replies.Single(r => r.InputIndex == input);
                y = AddText(reply.Text, bodyFont, Ink, x, y, wrap);
                if (reply.Comments.Length > 0)
                {
                    y += 20;
                    y = AddText("Model comments", smallFont, Blue, x, y, wrap) + 6;
                    y = AddText(reply.Comments, bodyFont, Ink, x, y, wrap);
                }
                return y;
            }

            var top = AddText("Describe comparison", titleFont, Ink, Margin, Margin, contentWidth) + 24;
            if (!string.IsNullOrEmpty(data.Prompt))
            {
                top = AddText("Input text", smallFont, Blue, Margin, top, contentWidth) + 8;
                top = AddText(data.Prompt, bodyFont, Ink, Margin, top, contentWidth) + Gap;
            }
            for (var input = 0; input < data.InputCount; input++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceWidth = layout == null ? contentWidth : (contentWidth - Gap) / 2;
                var sourceTop = AddText(data.InputCount == 1 ? "Input image" : $"Input image {input + 1} of {data.InputCount}",
                    labelFont, Blue, Margin, top, sourceWidth) + 16;
                var rowEnd = AddImage(inputPaths[input], Margin, sourceTop, sourceWidth, 900);
                var sourceDrawWidth = images[^1].Bounds.Width;
                if (layout != null)
                {
                    var x = Margin + sourceWidth + Gap;
                    var y = AddEndpointText(layout, input, x, top, sourceWidth);
                    if (layout.Error.Length == 0)
                    {
                        if (!layoutPaths.TryGetValue(input, out var path))
                            throw new InvalidDataException($"The layout map for input {input + 1} is unavailable.");
                        // Keep the complete rendered map, numbered legend, and summary together.
                        y = AddImage(path, x + (sourceWidth - sourceDrawWidth) / 2, y, sourceDrawWidth, int.MaxValue);
                    }
                    rowEnd = Math.Max(rowEnd, y);
                }
                var next = Enumerable.Repeat(rowEnd + Gap, columns).ToArray();
                var cardWidth = (contentWidth - Gap * (columns - 1)) / columns;
                foreach (var endpoint in descriptions)
                {
                    var column = Array.IndexOf(next, next.Min());
                    var x = Margin + column * (cardWidth + Gap);
                    var y = next[column];
                    var end = AddEndpointText(endpoint, input, x + Pad, y + Pad, cardWidth - Pad * 2) + Pad;
                    panels.Add(new Rectangle(x, y, cardWidth, end - y));
                    next[column] = end + Gap;
                }
                top = next.Max() + Gap;
            }
            var height = checked(top + Margin - Gap);
            if ((long)width * height > MaxPixels)
                throw new InvalidDataException("The complete describe sheet exceeds 32 megapixels. No image was exported or text shortened.");

            using var canvas = new Image<Rgba32>(width, height, Color.White);
            canvas.Mutate(ctx =>
            {
                foreach (var panel in panels) ctx.Fill(Paper, panel);
                foreach (var text in texts)
                {
                    var options = TextOptions(text.Font, text.Bounds.Width);
                    options.Origin = new PointF(text.Bounds.X, text.Bounds.Y);
                    ctx.DrawText(options, text.Text, text.Color);
                }
            });
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = new DecoderOptions { TargetSize = new Size(image.Bounds.Width, image.Bounds.Height), MaxFrames = 1 };
                using var source = await Image.LoadAsync<Rgba32>(options, image.Path, cancellationToken);
                source.Mutate(ctx => ctx.Resize(image.Bounds.Width, image.Bounds.Height));
                canvas.Mutate(ctx => ctx.DrawImage(source, image.Bounds.Location, 1f));
            }
            await canvas.SaveAsPngAsync(outputPath, cancellationToken);
        }

        private static RichTextOptions TextOptions(Font font, int width) => new(font)
        {
            Dpi = 72,
            WrappingLength = width,
            LineSpacing = 1.25f,
            WordBreaking = WordBreaking.BreakWord,
        };
    }
}
