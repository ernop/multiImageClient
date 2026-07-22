#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MultiImageClient
{
    public sealed class ProviderCallTrace
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string AttemptId { get; init; } = "";
        public string Provider { get; init; } = "";
        public string Transport { get; init; } = "";
        public string Method { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public DateTime StartedAtUtc { get; init; }
        public DateTime FinishedAtUtc { get; init; }
        public int? StatusCode { get; init; }
        public bool Success { get; init; }
        public string RequestJson { get; init; } = "null";
        public string ResponseJson { get; init; } = "null";
        public string MetadataJson { get; init; } = "null";
        public string ErrorType { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
    }

    /// Ambient provider-call tracing shared by the orchestrator and low-level
    /// provider client projects. Payloads are redacted before leaving the call
    /// site; API keys, cookies, binary/base64 bodies, and signed URL queries are
    /// never handed to the archive sink.
    public static class GenerationTrace
    {
        private static readonly AsyncLocal<string?> CurrentAttempt = new();

        public static Action<ProviderCallTrace>? ProviderCallSink { get; set; }

        public static string AttemptId => CurrentAttempt.Value ?? "";

        public static IDisposable BeginAttempt(string attemptId)
        {
            var previous = CurrentAttempt.Value;
            CurrentAttempt.Value = attemptId;
            return new Scope(() => CurrentAttempt.Value = previous);
        }

        public static void RecordProviderCall(
            string provider,
            string transport,
            string method,
            string endpoint,
            DateTime startedAtUtc,
            object? request = null,
            object? response = null,
            int? statusCode = null,
            Exception? error = null,
            object? metadata = null)
        {
            var sink = ProviderCallSink;
            var attemptId = AttemptId;
            if (sink == null || string.IsNullOrWhiteSpace(attemptId))
            {
                return;
            }

            try
            {
                var finishedAtUtc = DateTime.UtcNow;
                sink(new ProviderCallTrace
                {
                    AttemptId = attemptId,
                    Provider = provider,
                    Transport = transport,
                    Method = method,
                    Endpoint = SanitizeEndpoint(endpoint),
                    StartedAtUtc = startedAtUtc,
                    FinishedAtUtc = finishedAtUtc,
                    StatusCode = statusCode,
                    Success = error == null && (!statusCode.HasValue || statusCode.Value is >= 200 and < 400),
                    RequestJson = NormalizePayload(request),
                    ResponseJson = NormalizePayload(response),
                    MetadataJson = NormalizePayload(metadata),
                    ErrorType = error?.GetType().FullName ?? "",
                    ErrorMessage = RedactText(error?.Message ?? ""),
                });
            }
            catch
            {
                // Audit capture must never make a provider request fail.
            }
        }

        public static string NormalizePayload(object? payload)
        {
            if (payload == null)
            {
                return "null";
            }
            try
            {
                JToken token;
                if (payload is JToken jToken)
                {
                    token = jToken.DeepClone();
                }
                else if (payload is string text && TryParseJson(text, out var parsed))
                {
                    token = parsed;
                }
                else
                {
                    token = JToken.FromObject(payload);
                }
                RedactToken(token, "");
                return token.ToString(Formatting.None);
            }
            catch
            {
                return JsonConvert.SerializeObject(new
                {
                    type = payload.GetType().FullName,
                    value = RedactText(payload.ToString() ?? ""),
                });
            }
        }

        private static void RedactToken(JToken token, string propertyName)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsSensitiveName(property.Name))
                    {
                        property.Value = "[REDACTED]";
                        continue;
                    }
                    RedactToken(property.Value, property.Name);
                }
                return;
            }

            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    RedactToken(item, propertyName);
                }
                return;
            }

            if (token is not JValue value || value.Type != JTokenType.String)
            {
                return;
            }

            var text = value.Value<string>() ?? "";
            if (LooksLikeBinaryField(propertyName, text))
            {
                value.Replace(BinaryPlaceholder(text));
                return;
            }
            value.Value = RedactText(text);
        }

        private static bool IsSensitiveName(string name)
        {
            var key = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
            return key.Contains("authorization")
                || key.Contains("apikey")
                || key.Contains("cookie")
                || key.Contains("password")
                || key.Contains("secret")
                || key.Contains("accesstoken")
                || key.Contains("refreshtoken")
                || key == "token";
        }

        private static bool LooksLikeBinaryField(string name, string value)
        {
            var key = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
            return value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase)
                || value.Length > 4096
                || key.Contains("base64")
                || key is "b64json" or "bytes" or "imagebytes" or "filebytes" or "bytesbase64encoded";
        }

        private static JObject BinaryPlaceholder(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return new JObject
            {
                ["_redacted"] = "binary-or-large-value",
                ["encodedLength"] = value.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            };
        }

        private static string RedactText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                return uri.GetLeftPart(UriPartial.Path);
            }
            var redacted = Regex.Replace(
                value,
                @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
                "Bearer [REDACTED]");
            redacted = Regex.Replace(
                redacted,
                @"(?i)\b(authorization|api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|secret|password)\b\s*[:=]\s*[""']?[^,\s;""'}]+",
                "$1=[REDACTED]");
            redacted = Regex.Replace(
                redacted,
                @"https?://[^\s""'<>]+",
                match => Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', ']'), UriKind.Absolute, out var embeddedUri)
                    ? embeddedUri.GetLeftPart(UriPartial.Path)
                    : match.Value);
            return redacted.Length <= 4096
                ? redacted
                : redacted.Substring(0, 4096) + $"… [truncated; original length {redacted.Length.ToString(CultureInfo.InvariantCulture)}]";
        }

        private static string SanitizeEndpoint(string endpoint)
            => RedactText(endpoint ?? "");

        private static bool TryParseJson(string text, out JToken token)
        {
            try
            {
                token = JToken.Parse(text);
                return true;
            }
            catch
            {
                token = JValue.CreateString(text);
                return false;
            }
        }

        private sealed class Scope : IDisposable
        {
            private Action? _onDispose;

            public Scope(Action onDispose) => _onDispose = onDispose;

            public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
