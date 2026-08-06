#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MultiImageClient
{
    public sealed class GrokWebStatsigMaterial
    {
        public required string VerificationKeyBase64 { get; init; }
        public required string AnimationKey { get; init; }
    }

    // Generates grok.com's x-statsig-id client-integrity header without a
    // browser once the public page verification key and animation key have
    // been extracted. Extraction is deliberately separate: stale or malformed
    // deployment inputs must fail instead of producing a speculative token.
    public sealed class GrokWebStatsigSigner
    {
        private const long CounterEpochUnixSeconds = 1682924400;
        private const string HashKeyword = "obfiowerehiring";
        private const int VerificationKeyLength = 48;
        private const int DigestPrefixLength = 16;
        private const byte Trailer = 3;

        private readonly byte[] _verificationKey;
        private readonly string _animationKey;

        public GrokWebStatsigSigner(string verificationKeyBase64, string animationKey)
        {
            if (string.IsNullOrWhiteSpace(verificationKeyBase64))
            {
                throw new ArgumentException(
                    "Grok web verification key is empty.",
                    nameof(verificationKeyBase64));
            }

            try
            {
                _verificationKey = Convert.FromBase64String(verificationKeyBase64);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(
                    "Grok web verification key is not valid base64.",
                    nameof(verificationKeyBase64),
                    ex);
            }

            if (_verificationKey.Length != VerificationKeyLength)
            {
                throw new ArgumentException(
                    $"Grok web verification key must decode to exactly {VerificationKeyLength} bytes "
                    + $"(got {_verificationKey.Length}).",
                    nameof(verificationKeyBase64));
            }

            if (string.IsNullOrWhiteSpace(animationKey)
                || animationKey.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException(
                    "Grok web animation key must be a non-empty hexadecimal string.",
                    nameof(animationKey));
            }

            _animationKey = animationKey.ToLowerInvariant();
        }

        public static bool TryCreateFromSettings(
            Settings settings,
            out GrokWebStatsigSigner? signer,
            out string? problem)
        {
            ArgumentNullException.ThrowIfNull(settings);
            signer = null;
            problem = null;

            var verificationKey = settings.GrokWebStatsigVerificationKey?.Trim() ?? "";
            var animationKey = settings.GrokWebStatsigAnimationKey?.Trim() ?? "";
            if (verificationKey.Length == 0 && animationKey.Length == 0)
            {
                problem =
                    "GrokWebStatsigVerificationKey and GrokWebStatsigAnimationKey are not configured";
                return false;
            }
            if (verificationKey.Length == 0 || animationKey.Length == 0)
            {
                problem =
                    "Grok web statsig settings are incomplete; capture and configure both values together";
                return false;
            }

            try
            {
                signer = new GrokWebStatsigSigner(verificationKey, animationKey);
                return true;
            }
            catch (ArgumentException ex)
            {
                problem = $"Grok web statsig settings are malformed: {ex.Message}";
                return false;
            }
        }

        public string Generate(string method, string path)
        {
            var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - CounterEpochUnixSeconds;
            if (counter < 0 || counter > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Current time cannot be represented by the Grok web signing counter: {counter}.");
            }

            var saltBytes = RandomNumberGenerator.GetBytes(1);
            return Generate(method, path, (uint)counter, saltBytes[0]);
        }

        public string Generate(string method, string path, uint counter, byte salt)
        {
            ValidateRequestIdentity(method, path);

            var hashInput = $"{method}!{path}!{counter}{HashKeyword}{_animationKey}";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));

            Span<byte> clear = stackalloc byte[
                VerificationKeyLength + sizeof(uint) + DigestPrefixLength + 1];
            _verificationKey.CopyTo(clear);
            BinaryPrimitives.WriteUInt32LittleEndian(
                clear.Slice(VerificationKeyLength, sizeof(uint)),
                counter);
            digest.AsSpan(0, DigestPrefixLength).CopyTo(
                clear.Slice(VerificationKeyLength + sizeof(uint), DigestPrefixLength));
            clear[^1] = Trailer;

            Span<byte> encoded = stackalloc byte[clear.Length + 1];
            encoded[0] = salt;
            for (var i = 0; i < clear.Length; i++)
            {
                encoded[i + 1] = (byte)(clear[i] ^ salt);
            }

            // Grok uses ordinary base64 (not base64url), with trailing padding
            // removed. A valid token decodes to exactly 70 bytes.
            return Convert.ToBase64String(encoded).TrimEnd('=');
        }

        // The caller supplies the 16-row animation table selected from the
        // current grok.com frontend assets. This method performs the same
        // deterministic cubic-bezier/color/rotation derivation as the site's
        // signing middleware. Asset discovery remains a separate fail-closed
        // operation because deployment bundle layouts can change.
        public static string DeriveAnimationKey(
            byte[] verificationKey,
            IReadOnlyList<IReadOnlyList<int>> frameTable,
            int rowIndexByteIndex,
            IReadOnlyList<int> frameTimeByteIndices)
        {
            if (verificationKey == null || verificationKey.Length != VerificationKeyLength)
            {
                throw new ArgumentException(
                    $"Verification key must contain exactly {VerificationKeyLength} bytes.",
                    nameof(verificationKey));
            }
            if (frameTable == null || frameTable.Count != 16)
            {
                throw new ArgumentException(
                    "Grok web animation frame table must contain exactly 16 rows.",
                    nameof(frameTable));
            }

            ValidateKeyByteIndex(rowIndexByteIndex, nameof(rowIndexByteIndex));
            if (frameTimeByteIndices == null || frameTimeByteIndices.Count == 0)
            {
                throw new ArgumentException(
                    "At least one frame-time key-byte index is required.",
                    nameof(frameTimeByteIndices));
            }

            long frameTime = 1;
            foreach (var index in frameTimeByteIndices)
            {
                ValidateKeyByteIndex(index, nameof(frameTimeByteIndices));
                checked
                {
                    frameTime *= verificationKey[index] % 16;
                }
            }

            var rowIndex = verificationKey[rowIndexByteIndex] % 16;
            var frame = frameTable[rowIndex]
                ?? throw new ArgumentException(
                    $"Grok web animation frame row {rowIndex} is missing.",
                    nameof(frameTable));
            if (frame.Count < 11)
            {
                throw new ArgumentException(
                    $"Grok web animation frame row {rowIndex} must contain at least 11 numbers "
                    + $"(got {frame.Count}).",
                    nameof(frameTable));
            }

            var targetTime = frameTime / 4096.0;
            return Animate(frame, targetTime);
        }

        public static void RunSelfTest()
        {
            const string verificationKeyBase64 =
                "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4v";
            const string expectedAnimationKey =
                "01290087ae147ae147b0d99999999999980d9999999999998087ae147ae147b00";
            const string expectedTokenSalt0 =
                "AAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicoKSorLC0uL4BuUQbDmOCd6AL6YO0zES/r6Yy3Aw";
            const string expectedTokenSalt255 =
                "///+/fz7+vn49/b19PPy8fDv7u3s6+rp6Ofm5eTj4uHg397d3Nva2djX1tXU09LR0D+vrPk1OqZ2b+u7hJ2B7vQrTdCc/A";

            var verificationKey = Convert.FromBase64String(verificationKeyBase64);
            IReadOnlyList<int> frame = new[]
            {
                12, 200, 7, 250, 33, 190, 128, 4, 9, 240, 17, 60, 220, 5,
            };
            var frameTable = Enumerable
                .Range(0, 16)
                .Select(_ => frame)
                .ToArray();
            var animationKey = DeriveAnimationKey(
                verificationKey,
                frameTable,
                rowIndexByteIndex: 2,
                frameTimeByteIndices: new[] { 12, 14, 7 });
            RequireSelfTestEqual("animation key", expectedAnimationKey, animationKey);

            var signer = new GrokWebStatsigSigner(verificationKeyBase64, animationKey);
            const string path = "/rest/app-chat/conversations/new";
            var tokenSalt0 = signer.Generate("POST", path, counter: 106000000, salt: 0);
            var tokenSalt255 = signer.Generate("POST", path, counter: 106123456, salt: 255);
            RequireSelfTestEqual("salt=0 token", expectedTokenSalt0, tokenSalt0);
            RequireSelfTestEqual("salt=255 token", expectedTokenSalt255, tokenSalt255);

            var decoded = Convert.FromBase64String(
                tokenSalt0 + new string('=', (4 - tokenSalt0.Length % 4) % 4));
            if (decoded.Length != 70)
            {
                throw new InvalidOperationException(
                    $"Grok web signer self-test produced {decoded.Length} bytes instead of 70.");
            }

            var captured = ParseCapturedMaterial(
                tokenSalt0,
                $"POST!{path}!106000000{HashKeyword}{animationKey}",
                "POST",
                path);
            RequireSelfTestEqual(
                "captured verification key",
                verificationKeyBase64,
                captured.VerificationKeyBase64);
            RequireSelfTestEqual(
                "captured animation key",
                expectedAnimationKey,
                captured.AnimationKey);
        }

        public static GrokWebStatsigMaterial ParseCapturedMaterial(
            string token,
            string digestInput,
            string method,
            string path)
        {
            ValidateRequestIdentity(method, path);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException(
                    "Captured x-statsig-id header is empty.",
                    nameof(token));
            }
            if (string.IsNullOrWhiteSpace(digestInput))
            {
                throw new ArgumentException(
                    "Captured x-statsig-id digest input is empty.",
                    nameof(digestInput));
            }

            byte[] encoded;
            try
            {
                encoded = Convert.FromBase64String(
                    token + new string('=', (4 - token.Length % 4) % 4));
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(
                    "Captured x-statsig-id header is not valid unpadded base64.",
                    nameof(token),
                    ex);
            }
            if (encoded.Length != 70)
            {
                throw new ArgumentException(
                    $"Captured x-statsig-id must decode to exactly 70 bytes (got {encoded.Length}).",
                    nameof(token));
            }

            var digestPrefix = $"{method}!{path}!";
            if (!digestInput.StartsWith(digestPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Captured digest input does not match the requested HTTP method and path.",
                    nameof(digestInput));
            }
            var keywordAt = digestInput.IndexOf(
                HashKeyword,
                digestPrefix.Length,
                StringComparison.Ordinal);
            if (keywordAt < 0)
            {
                throw new ArgumentException(
                    "Captured digest input does not contain the expected Grok signing keyword.",
                    nameof(digestInput));
            }

            var counterText = digestInput.Substring(
                digestPrefix.Length,
                keywordAt - digestPrefix.Length);
            if (!uint.TryParse(
                    counterText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var counter))
            {
                throw new ArgumentException(
                    $"Captured digest input has an invalid signing counter '{counterText}'.",
                    nameof(digestInput));
            }

            var animationKey = digestInput.Substring(keywordAt + HashKeyword.Length);
            if (animationKey.Length == 0 || animationKey.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException(
                    "Captured digest input has a missing or malformed animation key.",
                    nameof(digestInput));
            }

            var salt = encoded[0];
            var verificationKey = new byte[VerificationKeyLength];
            for (var i = 0; i < verificationKey.Length; i++)
            {
                verificationKey[i] = (byte)(encoded[i + 1] ^ salt);
            }
            var verificationKeyBase64 = Convert.ToBase64String(verificationKey);
            var signer = new GrokWebStatsigSigner(
                verificationKeyBase64,
                animationKey);
            var reproduced = signer.Generate(method, path, counter, salt);
            if (!string.Equals(token, reproduced, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Captured Grok signing inputs did not reproduce the exact x-statsig-id header.");
            }

            return new GrokWebStatsigMaterial
            {
                VerificationKeyBase64 = verificationKeyBase64,
                AnimationKey = animationKey.ToLowerInvariant(),
            };
        }

        private static void ValidateRequestIdentity(string method, string path)
        {
            if (string.IsNullOrWhiteSpace(method)
                || method.Any(c => c is < 'A' or > 'Z'))
            {
                throw new ArgumentException(
                    "Grok web signing method must be non-empty uppercase ASCII.",
                    nameof(method));
            }
            if (string.IsNullOrWhiteSpace(path)
                || path[0] != '/'
                || path.StartsWith("//", StringComparison.Ordinal)
                || path.Contains("://", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Grok web signing path must be an origin-relative path beginning with '/'.",
                    nameof(path));
            }
        }

        private static void ValidateKeyByteIndex(int index, string parameterName)
        {
            if (index < 0 || index >= VerificationKeyLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    index,
                    $"Verification-key byte index must be in [0, {VerificationKeyLength - 1}].");
            }
        }

        private static string Animate(IReadOnlyList<int> frame, double targetTime)
        {
            var fromColor = new[] { (double)frame[0], frame[1], frame[2], 1.0 };
            var toColor = new[] { (double)frame[3], frame[4], frame[5], 1.0 };
            var toRotation = Solve(frame[6], 60.0, 360.0, roundDown: true);

            var curve = new double[4];
            for (var i = 0; i < curve.Length; i++)
            {
                curve[i] = Solve(
                    frame[i + 7],
                    i % 2 == 0 ? 0.0 : -1.0,
                    1.0,
                    roundDown: false);
            }

            var value = CubicBezierValue(curve, targetTime);
            var color = Interpolate(fromColor, toColor, value);
            var rotation = Interpolate(
                new[] { 0.0 },
                new[] { toRotation },
                value)[0];
            var radians = rotation * Math.PI / 180.0;
            var matrix = new[]
            {
                Math.Cos(radians),
                -Math.Sin(radians),
                Math.Sin(radians),
                Math.Cos(radians),
            };

            var result = new StringBuilder();
            for (var i = 0; i < color.Length - 1; i++)
            {
                var channel = Math.Max(0.0, color[i]);
                result.Append(
                    ((long)Math.Round(channel, MidpointRounding.ToEven))
                    .ToString("x", CultureInfo.InvariantCulture));
            }

            foreach (var component in matrix)
            {
                var rounded = Math.Abs(Math.Round(component, 2, MidpointRounding.ToEven));
                var hex = FloatToHex(rounded);
                result.Append(hex.StartsWith(".", StringComparison.Ordinal) ? $"0{hex}" : hex);
            }
            result.Append("00");

            return result
                .ToString()
                .Replace(".", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .ToLowerInvariant();
        }

        private static double Solve(
            double value,
            double minimum,
            double maximum,
            bool roundDown)
        {
            var result = value * (maximum - minimum) / 255.0 + minimum;
            return roundDown
                ? Math.Floor(result)
                : Math.Round(result, 2, MidpointRounding.ToEven);
        }

        private static double CubicBezierValue(double[] curve, double time)
        {
            if (time <= 0.0)
            {
                var startGradient = 0.0;
                if (curve[0] > 0.0)
                {
                    startGradient = curve[1] / curve[0];
                }
                else if (curve[1] == 0.0 && curve[2] > 0.0)
                {
                    startGradient = curve[3] / curve[2];
                }
                return startGradient * time;
            }

            if (time >= 1.0)
            {
                var endGradient = 0.0;
                if (curve[2] < 1.0)
                {
                    endGradient = (curve[3] - 1.0) / (curve[2] - 1.0);
                }
                else if (curve[2] == 1.0 && curve[0] < 1.0)
                {
                    endGradient = (curve[1] - 1.0) / (curve[0] - 1.0);
                }
                return 1.0 + endGradient * (time - 1.0);
            }

            var start = 0.0;
            var end = 1.0;
            var midpoint = 0.0;
            while (start < end)
            {
                midpoint = (start + end) / 2.0;
                var estimate = CubicBezierPoint(curve[0], curve[2], midpoint);
                if (Math.Abs(time - estimate) < 0.00001)
                {
                    return CubicBezierPoint(curve[1], curve[3], midpoint);
                }
                if (estimate < time)
                {
                    start = midpoint;
                }
                else
                {
                    end = midpoint;
                }
            }
            return CubicBezierPoint(curve[1], curve[3], midpoint);
        }

        private static double CubicBezierPoint(double a, double b, double position)
            => 3.0 * a * (1.0 - position) * (1.0 - position) * position
                + 3.0 * b * (1.0 - position) * position * position
                + position * position * position;

        private static double[] Interpolate(
            IReadOnlyList<double> from,
            IReadOnlyList<double> to,
            double fraction)
        {
            if (from.Count != to.Count)
            {
                throw new ArgumentException("Interpolation arrays must have equal lengths.");
            }

            var result = new double[from.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = from[i] * (1.0 - fraction) + to[i] * fraction;
            }
            return result;
        }

        private static string FloatToHex(double value)
        {
            var result = new StringBuilder();
            var quotient = (long)value;
            var fraction = value - quotient;
            while (quotient > 0)
            {
                quotient = (long)(value / 16.0);
                var remainder = (int)(value - quotient * 16.0);
                result.Insert(0, remainder > 9
                    ? (char)(remainder + 55)
                    : (char)('0' + remainder));
                value = quotient;
            }

            if (fraction == 0.0)
            {
                return result.Length == 0 ? "0" : result.ToString();
            }

            result.Append('.');
            for (var i = 0; fraction > 0.0; i++)
            {
                if (i >= 64)
                {
                    throw new InvalidOperationException(
                        "Grok web animation fractional hexadecimal conversion did not terminate.");
                }

                fraction *= 16.0;
                var integer = (int)fraction;
                fraction -= integer;
                result.Append(integer > 9
                    ? (char)(integer + 55)
                    : (char)('0' + integer));
            }
            return result.ToString();
        }

        private static void RequireSelfTestEqual(
            string name,
            string expected,
            string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Grok web signer self-test failed for {name}. "
                    + $"Expected '{expected}', got '{actual}'.");
            }
        }
    }
}
