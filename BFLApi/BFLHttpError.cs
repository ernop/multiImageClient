using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace BFLAPIClient
{
    /// Turns a BFL HTTP failure into a short user-facing message. FastAPI
    /// 422 bodies echo the whole request under <c>detail[].input</c>, including
    /// base64 image_prompt bytes; those must never reach the UI error text.
    public static class BFLHttpError
    {
        public const int MaxMessageChars = 480;

        public static string Format(HttpResponseMessage response, string responseContent)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            return Format(
                response.StatusCode,
                response.ReasonPhrase,
                responseContent,
                ReadRetryAfter(response));
        }

        public static string Format(
            HttpStatusCode statusCode,
            string reasonPhrase,
            string responseContent,
            TimeSpan? retryAfter = null)
        {
            var status = (int)statusCode;
            var details = ExtractDetails(responseContent);
            var parts = new List<string>();
            switch (statusCode)
            {
                case HttpStatusCode.TooManyRequests:
                    parts.Add($"Black Forest Labs rate-limited this request (HTTP {status}).");
                    break;
                case HttpStatusCode.PaymentRequired:
                    parts.Add($"Black Forest Labs requires payment (HTTP {status}).");
                    break;
                case HttpStatusCode.UnprocessableEntity:
                    parts.Add($"BFL rejected this request (HTTP {status}).");
                    break;
                default:
                    var reason = string.IsNullOrWhiteSpace(reasonPhrase)
                        ? statusCode.ToString()
                        : reasonPhrase.Trim();
                    parts.Add($"BFL request failed (HTTP {status} {reason}).");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                parts.Add(details);
            }
            else if (statusCode == HttpStatusCode.TooManyRequests)
            {
                parts.Add("Wait and resend.");
            }

            if (statusCode == HttpStatusCode.TooManyRequests
                && retryAfter is TimeSpan wait
                && wait > TimeSpan.Zero)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                parts.Add($"Retry-After {seconds}s.");
            }

            return Truncate(string.Join(" ", parts));
        }

        public static HttpRequestException ToException(HttpResponseMessage response, string responseContent)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            return new HttpRequestException(
                Format(response, responseContent),
                null,
                response.StatusCode);
        }

        private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter;
            if (retry == null)
            {
                return null;
            }
            if (retry.Delta.HasValue)
            {
                return retry.Delta;
            }
            if (retry.Date.HasValue)
            {
                var wait = retry.Date.Value - DateTimeOffset.UtcNow;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
            return null;
        }

        private static string ExtractDetails(string responseContent)
        {
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return "";
            }

            try
            {
                var token = JToken.Parse(responseContent);
                var messages = new List<string>();
                CollectMessages(token, messages);
                if (messages.Count > 0)
                {
                    return string.Join("; ", messages);
                }
                return "";
            }
            catch (Exception)
            {
                return CompactPlainText(responseContent);
            }
        }

        private static void CollectMessages(JToken token, List<string> messages)
        {
            if (token is JObject obj)
            {
                if (obj.TryGetValue("detail", out var detail))
                {
                    CollectDetail(detail, messages);
                }
                AddIfMessage(obj.Value<string>("msg"), messages);
                AddIfMessage(obj.Value<string>("message"), messages);
                AddIfMessage(obj.Value<string>("error"), messages);
                return;
            }

            CollectDetail(token, messages);
        }

        private static void CollectDetail(JToken detail, List<string> messages)
        {
            if (detail == null || detail.Type == JTokenType.Null)
            {
                return;
            }
            if (detail.Type == JTokenType.String)
            {
                AddIfMessage(detail.Value<string>(), messages);
                return;
            }
            if (detail is JArray array)
            {
                foreach (var item in array)
                {
                    CollectDetailItem(item, messages);
                }
                return;
            }
            CollectDetailItem(detail, messages);
        }

        private static void CollectDetailItem(JToken item, List<string> messages)
        {
            if (item == null || item.Type == JTokenType.Null)
            {
                return;
            }
            if (item.Type == JTokenType.String)
            {
                AddIfMessage(item.Value<string>(), messages);
                return;
            }
            if (item is not JObject obj)
            {
                return;
            }

            var msg = obj.Value<string>("msg") ?? obj.Value<string>("message");
            msg = CleanValidationMessage(msg);
            if (string.IsNullOrWhiteSpace(msg))
            {
                return;
            }

            var field = FieldNameFromLoc(obj["loc"]);
            messages.Add(string.IsNullOrWhiteSpace(field) ? msg : $"{field}: {msg}");
        }

        private static string FieldNameFromLoc(JToken loc)
        {
            if (loc is not JArray array || array.Count == 0)
            {
                return "";
            }
            for (var i = array.Count - 1; i >= 0; i--)
            {
                if (array[i].Type != JTokenType.String)
                {
                    continue;
                }
                var name = array[i].Value<string>();
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, "body", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "query", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "path", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return name.Trim();
            }
            return "";
        }

        private static void AddIfMessage(string text, List<string> messages)
        {
            var cleaned = CleanValidationMessage(text);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return;
            }
            if (messages.Contains(cleaned))
            {
                return;
            }
            messages.Add(cleaned);
        }

        private static string CleanValidationMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() is "{" or "}")
            {
                return "";
            }
            var cleaned = text.Trim();
            const string valueErrorPrefix = "Value error, ";
            if (cleaned.StartsWith(valueErrorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[valueErrorPrefix.Length..].Trim();
            }
            if (LooksLikeEmbeddedPayload(cleaned))
            {
                return "";
            }
            return cleaned;
        }

        private static string CompactPlainText(string body)
        {
            var trimmed = body.Trim();
            if (LooksLikeEmbeddedPayload(trimmed))
            {
                return "";
            }
            if (trimmed.Length <= 240)
            {
                return trimmed;
            }
            return trimmed[..240].TrimEnd() + "…";
        }

        private static bool LooksLikeEmbeddedPayload(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.IndexOf("iVBOR", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("data:image/", StringComparison.OrdinalIgnoreCase) >= 0
                || text.Contains("\"image_prompt\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("\"input_image\"", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return text.Length > 256 && IsMostlyBase64(text);
        }

        private static bool IsMostlyBase64(string text)
        {
            var counted = 0;
            var base64 = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }
                counted++;
                if (char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=')
                {
                    base64++;
                }
            }
            return counted >= 64 && base64 * 10 >= counted * 9;
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxMessageChars)
            {
                return text;
            }
            return text[..(MaxMessageChars - 1)].TrimEnd() + "…";
        }
    }
}
