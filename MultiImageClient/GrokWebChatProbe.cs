#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Dev-only discovery harness for the grok-web "chat door" image path:
    // sending an uploaded image through a normal app-chat conversation message
    // (letting the assistant orchestrate image generation) instead of the
    // direct imagine-image-edit model. It reuses the proven upload + signed
    // app-chat transport and prints the raw response so the exact working
    // request/response schema can be confirmed against the live endpoint before
    // any production route is built. Nothing here is wired into UI/batch flows.
    public static class GrokWebChatProbe
    {
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

            if (!GrokWebStatsigSigner.TryCreateFromSettings(settings, out var signer, out var problem)
                || signer == null)
            {
                throw new InvalidOperationException(
                    $"--grok-web-chat-probe requires x-statsig-id signing material: {problem}");
            }

            var message = string.IsNullOrWhiteSpace(options.OverridePrompt)
                ? "Using the attached image, add a small bright yellow star in the top-right corner. Keep everything else the same. Full bright daylight."
                : options.OverridePrompt;

            using var client = GrokWebClient.FromCookieFile(
                Settings.ExpandPath(cookiePath),
                statsigSigner: signer);

            string? assetId = null;
            var inputPath = Settings.ExpandPath(options.InputImagePath);
            if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
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

            if (assetId == null)
            {
                Console.WriteLine("chat-probe: --input-image is required to validate the chat image path.");
                return;
            }

            var asset = new GrokWebAsset { AssetId = assetId, MediaUrl = "", PostId = assetId };
            Console.WriteLine($"\n===== RunImageChatEditAsync (model={GrokWebClient.DefaultChatModel}) =====");
            var result = await client.RunImageChatEditAsync(message, asset, cancellationToken: cancellationToken);
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
