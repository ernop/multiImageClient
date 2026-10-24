#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Dev-only discovery harness for the grok-web "chat door" image path:
    // sending a prompt, optionally with an uploaded image, through a normal
    // app-chat conversation message (letting the assistant orchestrate image
    // generation) instead of the direct imagine-image-edit model. It sends the
    // production payload once over the production app-chat transport, saves
    // the raw response, and parses it with the production parser, so the exact
    // request/response schema can be confirmed against the live endpoint.
    // Nothing here is wired into UI/batch flows.
    public static class GrokWebChatProbe
    {
        private const string RawResponsePath = "/tmp/chat-probe-raw.txt";

        public static async Task RunAsync(
            Settings settings,
            RunOptions options,
            CancellationToken cancellationToken = default)
        {
            var cookiePath = !string.IsNullOrWhiteSpace(options.GrokWebCookies)
                ? options.GrokWebCookies
                : settings.GrokWebCookiePath;
            if (string.IsNullOrWhiteSpace(cookiePath) || !File.Exists(Settings.ExpandPath(cookiePath)))
            {
                throw new InvalidOperationException(
                    "--grok-web-chat-probe requires a valid GrokWebCookiePath or --grok-web-cookies.");
            }

            using var client = GrokWebClient.FromCookieFile(Settings.ExpandPath(cookiePath));

            string? assetId = null;
            var inputPath = Settings.ExpandPath(options.InputImagePath);
            if (!string.IsNullOrWhiteSpace(inputPath) && !File.Exists(inputPath))
            {
                throw new FileNotFoundException($"--input-image file not found: {inputPath}");
            }
            if (!string.IsNullOrWhiteSpace(inputPath))
            {
                var uploaded = await client.UploadImageAsync(inputPath, cancellationToken);
                var post = await client.CreateImagePostAsync(uploaded.MediaUrl, cancellationToken);
                assetId = post.AssetId;
                Console.WriteLine($"chat-probe: uploaded input, assetId={assetId}, postId={post.PostId}");
            }
            else
            {
                Console.WriteLine("chat-probe: no --input-image; probing text-only chat image generation.");
            }

            var message = !string.IsNullOrWhiteSpace(options.OverridePrompt)
                ? options.OverridePrompt
                : assetId != null
                    ? "Using the attached image, add a small bright yellow star in the top-right corner. Keep everything else the same. Full bright daylight."
                    : "A red apple on a white kitchen table in clear, bright, even daylight.";

            var payload = GrokWebClient.BuildImageChatPayload(message, assetId, GrokWebClient.DefaultChatModel);
            Console.WriteLine($"\n===== chat-door request (model={GrokWebClient.DefaultChatModel}, image={(assetId != null ? "yes" : "no")}) =====");
            var raw = await client.DebugPostAppChatRawAsync(payload, cancellationToken);
            await File.WriteAllTextAsync(RawResponsePath, raw, cancellationToken);
            var statusEnd = raw.IndexOf('\n');
            Console.WriteLine($"{(statusEnd < 0 ? raw : raw[..statusEnd])}; raw response -> {RawResponsePath}");
            var result = GrokWebClient.ParseAppChatBody(statusEnd < 0 ? "" : raw[(statusEnd + 1)..]);
            Console.WriteLine($"error={result.ErrorMessage ?? "(none)"}");
            Console.WriteLine($"modelMessage={Trim(result.ModelMessage ?? "(none)", 300)}");
            Console.WriteLine($"image urls ({result.GeneratedImageUrls.Count}):");
            foreach (var u in result.GeneratedImageUrls)
            {
                Console.WriteLine("  " + u);
            }

            if (result.GeneratedImageUrls.Count > 0)
            {
                var bytes = await client.DownloadBytesAsync(result.GeneratedImageUrls[0], cancellationToken);
                var outPath = "/tmp/chat-probe-result.jpg";
                await File.WriteAllBytesAsync(outPath, bytes, cancellationToken);
                Console.WriteLine($"chat-probe: downloaded {bytes.Length} bytes -> {outPath}");
            }
        }

        private static string Trim(string s, int max)
            => s.Length <= max ? s : s[..max] + $"\n…[truncated {s.Length - max} chars]";
    }
}
