#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using IdeogramAPIClient;
using RecraftAPIClient;

using SkiaSharp;

namespace MultiImageClient
{
    /// Maps the intent-level (shape, detail) pair onto each backend's actual
    /// knobs. Kept as a standalone static so future generators (and tests)
    /// can reuse the same mapping.
    public static class UiShapeMapping
    {
        public static readonly string[] Shapes = { "auto", "square", "landscape", "portrait", "wide", "tall" };
        public static readonly string[] Details = { "standard", "high", "max" };

        private static readonly (string Name, int Width, int Height)[] GrokAspects =
        {
            ("1:1", 1, 1),
            ("2:3", 2, 3),
            ("3:2", 3, 2),
            ("3:4", 3, 4),
            ("4:3", 4, 3),
            ("9:16", 9, 16),
            ("16:9", 16, 9),
            ("21:9", 21, 9),
            ("5:2", 5, 2),
        };

        private static readonly (string Name, int Width, int Height)[] GoogleAspects =
        {
            ("1:1", 1, 1),
            ("2:3", 2, 3),
            ("3:2", 3, 2),
            ("3:4", 3, 4),
            ("4:3", 4, 3),
            ("4:5", 4, 5),
            ("5:4", 5, 4),
            ("9:16", 9, 16),
            ("16:9", 16, 9),
            ("21:9", 21, 9),
        };

        private static readonly (string Name, int Width, int Height)[] KreaAspects =
        {
            ("1:1", 1, 1),
            ("4:3", 4, 3),
            ("3:2", 3, 2),
            ("16:9", 16, 9),
            ("2.35:1", 235, 100),
            ("4:5", 4, 5),
            ("2:3", 2, 3),
            ("9:16", 9, 16),
        };

        private static readonly (IdeogramAPIClient.IdeogramAspectRatio Aspect, int Width, int Height)[] IdeogramV3Aspects =
        {
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_1, 1, 1),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_2_3, 2, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_2, 3, 2),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_4, 3, 4),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_4_3, 4, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_9_16, 9, 16),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_9, 16, 9),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_10_16, 10, 16),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_10, 16, 10),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_3, 1, 3),
            (IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_1, 3, 1),
        };

        private static readonly (string Name, int Width, int Height)[] IdeogramV4Resolutions =
        {
            ("2048x2048", 2048, 2048),
            ("1440x2880", 1440, 2880), ("2880x1440", 2880, 1440),
            ("1664x2496", 1664, 2496), ("2496x1664", 2496, 1664),
            ("1792x2240", 1792, 2240), ("2240x1792", 2240, 1792),
            ("1440x2560", 1440, 2560), ("2560x1440", 2560, 1440),
            ("1600x2560", 1600, 2560), ("2560x1600", 2560, 1600),
            ("1728x2304", 1728, 2304), ("2304x1728", 2304, 1728),
            ("1296x3168", 1296, 3168), ("3168x1296", 3168, 1296),
            ("1152x2944", 1152, 2944), ("2944x1152", 2944, 1152),
            ("1248x3328", 1248, 3328), ("3328x1248", 3328, 1248),
            ("1280x3072", 1280, 3072), ("3072x1280", 3072, 1280),
            ("1024x3072", 1024, 3072), ("3072x1024", 3072, 1024),
        };

        public static bool IsKnownShape(string shape)
        {
            var value = (shape ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Shapes, value) >= 0;
        }

        public static string GrokAspectForInput(int width, int height)
            => NearestAspect(width, height, GrokAspects);

        public static string GoogleAspectForInput(int width, int height)
            => NearestAspect(width, height, GoogleAspects);

        public static string KreaAspectForInput(int width, int height)
            => NearestAspect(width, height, KreaAspects);

        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3AspectForInput(int width, int height)
        {
            ValidateInputDimensions(width, height);
            var sourceRatio = (double)width / height;
            return IdeogramV3Aspects
                .OrderBy(candidate => RatioDistance(sourceRatio, (double)candidate.Width / candidate.Height))
                .First()
                .Aspect;
        }

        public static string IdeogramV4ResolutionForInput(int width, int height)
            => NearestAspect(width, height, IdeogramV4Resolutions);

        /// gpt-image-2 size string. All values are multiples of 16, within
        /// the [655360, 8294400] pixel envelope, edges < 3840 (2880x2880 is
        /// exactly the max pixel count).
        public static string Gpt2Size(
            string shape,
            string detail,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            var normalizedDetail = Norm(detail, Details);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                var targetPixels = normalizedDetail switch
                {
                    "high" => 4194304,
                    "max" => GptImage2Generator.SizeMaxPixels,
                    _ => 1048576,
                };
                var (width, height) = SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels,
                    multiple: GptImage2Generator.SizeEdgeMultiple,
                    maxPixels: GptImage2Generator.SizeMaxPixels,
                    maxEdgeExclusive: GptImage2Generator.SizeMaxEdge,
                    maxLongShortRatio: GptImage2Generator.SizeMaxAspectRatio);
                var requested = $"{width}x{height}";
                if (!GptImage2Generator.TryNormalizeSize(requested, out var normalized, out _, out var error)
                    || normalized != requested)
                {
                    throw new InvalidOperationException(
                        $"Could not map input aspect ratio to a valid gpt-image-2 size: {error ?? $"{requested} normalized to {normalized}"}");
                }
                return requested;
            }

            return (normalizedShape, normalizedDetail) switch
            {
                ("auto", _) => "auto",
                ("square", "standard") => "1024x1024",
                ("square", "high") => "2048x2048",
                ("square", "max") => "2880x2880",
                ("landscape", "standard") => "1536x1024",
                ("landscape", "high") => "2304x1536",
                ("landscape", "max") => "3216x2144",
                ("portrait", "standard") => "1024x1536",
                ("portrait", "high") => "1536x2304",
                ("portrait", "max") => "2144x3216",
                ("wide", "standard") => "1536x864",
                ("wide", "high") => "2560x1440",
                ("wide", "max") => "3824x2144",
                ("tall", "standard") => "864x1536",
                ("tall", "high") => "1440x2560",
                ("tall", "max") => "2144x3824",
                _ => "auto",
            };
        }

        /// gpt-image-1 / mini support the three canonical 1024-edge sizes.
        public static string Gpt1Size(string shape)
        {
            return Norm(shape, Shapes) switch
            {
                "landscape" or "wide" => "1536x1024",
                "portrait" or "tall" => "1024x1536",
                _ => "1024x1024",
            };
        }

        /// Aspect-ratio string for the grok generators. With no input image,
        /// empty means no preference. With input dimensions, auto maps to the
        /// closest ratio accepted by Grok.
        public static string GrokAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return GrokAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "1:1",
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "",
            };
        }

        /// Krea 2 requires an explicit aspect ratio. Text-to-image auto uses
        /// square; image jobs map auto to the nearest native Krea ratio.
        public static string KreaAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return KreaAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "1:1",
            };
        }

        /// grok-api resolution tier. Standard = 1k; both higher tiers = 2k
        /// (2k is grok-api's ceiling).
        public static string GrokResolution(string detail)
            => Norm(detail, Details) == "standard" ? "1k" : "2k";

        /// Ideogram v4 resolution string. Detail has no effect. Text-to-image
        /// auto omits the field; Remix auto chooses the closest published 2K
        /// resolution to the source image.
        public static string IdeogramV4Resolution(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return IdeogramV4ResolutionForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "2048x2048",
                "landscape" => "2496x1664",
                "portrait" => "1664x2496",
                "wide" => "2560x1440",
                "tall" => "1440x2560",
                _ => "",
            };
        }

        /// Ideogram 4.5 text-to-image size. Auto omits the field, and Ideogram
        /// picks a prompt-aware 2K size. Explicit shapes use published 2K
        /// presets. Detail has no effect: 1K costs the same as 2K. Precise
        /// Edit keeps the source size and never calls this.
        public static string IdeogramV45Size(string shape)
            => Norm(shape, Shapes) switch
            {
                "square" => "2048x2048",
                "landscape" => "2496x1664",
                "portrait" => "1664x2496",
                "wide" => "2560x1440",
                "tall" => "1440x2560",
                _ => "",
            };

        /// Ideogram 4.6 text-to-image size. Auto lets Ideogram pick a 2K or 4K
        /// size depending on detail ("max" selects 4K). Explicit shapes use
        /// verified 2K or 4K multiple-of-32 dimensions.
        public static string IdeogramV46Size(string shape, string detail)
        {
            var is4k = Norm(detail, Details) == "max";
            var normalizedShape = Norm(shape, Shapes);
            if (is4k)
            {
                return normalizedShape switch
                {
                    "square" => "4096x4096",
                    "landscape" => "4800x3200",
                    "portrait" => "3200x4800",
                    "wide" => "5120x2880",
                    "tall" => "2880x5120",
                    _ => "auto_4k",
                };
            }
            return normalizedShape switch
            {
                "square" => "2048x2048",
                "landscape" => "2496x1664",
                "portrait" => "1664x2496",
                "wide" => "2560x1440",
                "tall" => "1440x2560",
                _ => "",
            };
        }

        /// BFL FLUX 3 resolution tier from detail + attachment state. Reference
        /// edits cap output at 4 MP (~2K), so max detail sends 2k with inputs.
        public static string BflFlux3Resolution(string detail, string shape, bool hasInputImage)
        {
            var d = Norm(detail, Details);
            if (hasInputImage)
            {
                if (Norm(shape, Shapes) == "auto" && d == "high")
                {
                    return "match_input";
                }
                return d == "standard" ? "1k" : "2k";
            }
            return d switch
            {
                "standard" => "1k",
                "max" => "4k",
                _ => "2k",
            };
        }

        /// Ideogram v3 aspect enum (image-reference jobs route to v3).
        public static IdeogramAPIClient.IdeogramAspectRatio IdeogramV3Aspect(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return IdeogramV3AspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "landscape" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_3_2,
                "portrait" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_2_3,
                "wide" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_16_9,
                "tall" => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_9_16,
                _ => IdeogramAPIClient.IdeogramAspectRatio.ASPECT_1_1,
            };
        }

        /// BFL FLUX.2 width/height. Multiples of 32, total pixels kept under
        /// FLUX.2's ~4 MP output ceiling, so "max" == "high".
        public static (int Width, int Height) BflSize(
            string shape,
            string detail,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var big = Norm(detail, Details) != "standard";
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels: big ? 4000000 : 1048576,
                    multiple: 32,
                    maxPixels: 4000000,
                    maxEdgeExclusive: 2592,
                    maxLongShortRatio: 3.0);
            }
            return normalizedShape switch
            {
                "landscape" => big ? (2304, 1536) : (1248, 832),
                "portrait" => big ? (1536, 2304) : (832, 1248),
                "wide" => big ? (2560, 1440) : (1344, 768),
                "tall" => big ? (1440, 2560) : (768, 1344),
                _ => big ? (1920, 1920) : (1024, 1024),
            };
        }

        /// FLUX.1 [pro]/[dev]/1.1 endpoints cap each edge at 1440 and require
        /// multiples of 32. Their API has no separate detail tier.
        public static (int Width, int Height) BflLegacySize(
            string shape,
            int inputWidth = 0,
            int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return SizeMatchingInput(
                    inputWidth,
                    inputHeight,
                    targetPixels: 1048576,
                    multiple: 32,
                    maxPixels: 1440 * 1440,
                    maxEdgeExclusive: 1441,
                    maxLongShortRatio: 1440.0 / 256.0);
            }
            return normalizedShape switch
            {
                "landscape" => (1248, 832),
                "portrait" => (832, 1248),
                "wide" => (1344, 768),
                "tall" => (768, 1344),
                _ => (1024, 1024),
            };
        }

        /// Gemini imageConfig.aspectRatio. With no input image, auto omits the
        /// field so the model decides. With input dimensions, auto maps to the
        /// closest ratio in Gemini's supported set.
        public static string GoogleAspect(string shape, int inputWidth = 0, int inputHeight = 0)
        {
            var normalizedShape = Norm(shape, Shapes);
            if (normalizedShape == "auto" && (inputWidth != 0 || inputHeight != 0))
            {
                return GoogleAspectForInput(inputWidth, inputHeight);
            }
            return normalizedShape switch
            {
                "square" => "1:1",
                "landscape" => "3:2",
                "portrait" => "2:3",
                "wide" => "16:9",
                "tall" => "9:16",
                _ => "",
            };
        }

        /// Gemini imageConfig.imageSize ("K" must be uppercase). 1K and 2K
        /// cost the same tokens; 4K is pricier.
        public static string GoogleImageSize(string detail)
        {
            return Norm(detail, Details) switch
            {
                "high" => "2K",
                "max" => "4K",
                _ => "1K",
            };
        }

        private static string NearestAspect(
            int width,
            int height,
            IReadOnlyList<(string Name, int Width, int Height)> candidates)
        {
            ValidateInputDimensions(width, height);
            var sourceRatio = (double)width / height;
            return candidates
                .OrderBy(candidate => RatioDistance(sourceRatio, (double)candidate.Width / candidate.Height))
                .First()
                .Name;
        }

        private static double RatioDistance(double first, double second)
            => Math.Abs(Math.Log(first / second));

        private static void ValidateInputDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException(
                    $"Input image dimensions must be positive to match its aspect ratio; received {width}x{height}.");
            }
        }

        private static (int Width, int Height) SizeMatchingInput(
            int inputWidth,
            int inputHeight,
            int targetPixels,
            int multiple,
            int maxPixels,
            int maxEdgeExclusive,
            double maxLongShortRatio)
        {
            ValidateInputDimensions(inputWidth, inputHeight);
            if (targetPixels <= 0 || multiple <= 0 || maxPixels <= 0 || maxEdgeExclusive <= multiple)
            {
                throw new ArgumentOutOfRangeException(nameof(targetPixels), "Output size constraints must be positive.");
            }

            var ratio = (double)inputWidth / inputHeight;
            ratio = Math.Clamp(ratio, 1.0 / maxLongShortRatio, maxLongShortRatio);
            var rawHeight = Math.Sqrt((double)targetPixels / ratio);
            var rawWidth = rawHeight * ratio;
            var scale = Math.Min(
                1.0,
                Math.Min(
                    (double)(maxEdgeExclusive - multiple) / rawWidth,
                    (double)(maxEdgeExclusive - multiple) / rawHeight));
            scale = Math.Min(scale, Math.Sqrt((double)maxPixels / (rawWidth * rawHeight)));
            rawWidth *= scale;
            rawHeight *= scale;

            var width = Math.Max(multiple, RoundToMultiple(rawWidth, multiple));
            var height = Math.Max(multiple, RoundToMultiple(rawHeight, multiple));
            while (width >= maxEdgeExclusive
                || height >= maxEdgeExclusive
                || (long)width * height > maxPixels)
            {
                var shrink = Math.Min(
                    (double)(maxEdgeExclusive - multiple) / width,
                    Math.Min(
                        (double)(maxEdgeExclusive - multiple) / height,
                        Math.Sqrt((double)maxPixels / ((long)width * height))));
                if (shrink >= 1.0)
                {
                    shrink = 0.99;
                }
                width = Math.Max(multiple, FloorToMultiple(width * shrink, multiple));
                height = Math.Max(multiple, FloorToMultiple(height * shrink, multiple));
            }
            return (width, height);
        }

        private static int RoundToMultiple(double value, int multiple)
            => (int)Math.Round(value / multiple, MidpointRounding.AwayFromZero) * multiple;

        private static int FloorToMultiple(double value, int multiple)
            => (int)Math.Floor(value / multiple) * multiple;

        private static string Norm(string value, string[] known)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(known, v) >= 0 ? v : known[0];
        }
    }
}
