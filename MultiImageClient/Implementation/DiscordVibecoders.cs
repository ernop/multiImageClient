#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class DiscordVibecoders
    {
        public const long MaxAttachmentBytes = 10L * 1024 * 1024;

        public static bool IsConfigured(Settings settings)
        {
            return TryNormalizeWebhookUrl(settings.DiscordVibecodersWebhookUrl, out _);
        }

        public static bool TryNormalizeWebhookUrl(string? raw, out string url)
        {
            url = "";
            if (string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || (!string.Equals(uri.Host, "discord.com", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Host, "discordapp.com", StringComparison.OrdinalIgnoreCase))
                || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Length < "/api/webhooks/x/y".Length)
            {
                return false;
            }
            url = uri.AbsoluteUri;
            return true;
        }

        public static bool TryNormalizePublicBaseUrl(string? raw, out string url)
        {
            url = "";
            if (string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(raw.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || uri.AbsolutePath.Length < 2)
            {
                return false;
            }
            url = uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        public static string FileExtension(string contentType)
        {
            return contentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/svg+xml" => ".svg",
                "video/mp4" => ".mp4",
                "video/webm" => ".webm",
                _ => "",
            };
        }
    }

    public sealed class DiscordVibecodersClient
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        private readonly string _webhookUrl;
        private readonly HttpClient _http;

        public DiscordVibecodersClient(Settings settings, HttpClient? httpClient = null)
        {
            if (!DiscordVibecoders.TryNormalizeWebhookUrl(settings.DiscordVibecodersWebhookUrl, out var url))
            {
                throw new InvalidOperationException("Discord vibecoders webhook is not configured.");
            }
            _webhookUrl = url;
            _http = httpClient ?? Http;
        }

        public async Task SendAsync(
            string senderName,
            Stream media,
            string contentType,
            string fileName,
            CancellationToken cancellationToken)
        {
            if (media == Stream.Null || !media.CanRead || !media.CanSeek
                || media.Length - media.Position <= 0 || media.Length - media.Position > DiscordVibecoders.MaxAttachmentBytes)
            {
                throw new InvalidOperationException("Discord requires an original attachment of at most 10 MiB.");
            }

            var username = string.IsNullOrWhiteSpace(senderName) ? "miic" : senderName.Trim();
            if (username.Length > 80)
            {
                username = username[..80];
            }

            using var form = new MultipartFormDataContent();
            var payload = new
            {
                username,
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
            form.Add(
                new StringContent(
                    JsonSerializer.Serialize(
                        payload,
                        new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        }),
                    Encoding.UTF8),
                "payload_json");

            if (media != Stream.Null)
            {
                var file = new StreamContent(media);
                file.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
                form.Add(file, "files[0]", fileName);
            }

            using var response = await _http.PostAsync(_webhookUrl, form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Discord rejected the upload ({(int)response.StatusCode}).");
            }
        }
    }
}
