#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class GrokWebStatsigCapture
    {
        public static async Task RunAsync(
            Settings settings,
            RunOptions options,
            string settingsFilePath,
            CancellationToken cancellationToken = default)
        {
            var cookiePath = !string.IsNullOrWhiteSpace(options.GrokWebCookies)
                ? options.GrokWebCookies
                : settings.GrokWebCookiePath;
            if (string.IsNullOrWhiteSpace(cookiePath))
            {
                throw new InvalidOperationException(
                    "--grok-web-capture-statsig requires GrokWebCookiePath "
                    + "or --grok-web-cookies.");
            }

            var inputPath = Settings.ExpandPath(options.InputImagePath);
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                throw new FileNotFoundException(
                    "--grok-web-capture-statsig requires an existing --input-image. "
                    + "The image is uploaded to create the source post whose Edit control "
                    + "initiates signing.",
                    inputPath);
            }
            if (string.IsNullOrWhiteSpace(settingsFilePath)
                || !File.Exists(settingsFilePath))
            {
                throw new FileNotFoundException(
                    "The exact loaded settings file is required to persist Grok signing material.",
                    settingsFilePath);
            }

            using var httpClient = GrokWebClient.FromCookieFile(cookiePath);
            var uploaded = await httpClient.UploadImageAsync(inputPath, cancellationToken);
            var post = await httpClient.CreateImagePostAsync(
                uploaded.MediaUrl,
                cancellationToken);
            var postId = post.PostId ?? post.AssetId;
            if (string.IsNullOrWhiteSpace(postId))
            {
                throw new GrokWebException(
                    "Grok web source post creation returned no post id for statsig capture.");
            }

            await using var browser = new GrokWebBrowserClient(
                GrokWebBrowserClient.BuildOptions(
                    settings,
                    cookiePath,
                    headedOverride: options.GrokWebHeaded));
            var material = await browser.CaptureStatsigMaterialAsync(
                postId,
                cancellationToken);

            var json = await File.ReadAllTextAsync(
                settingsFilePath,
                cancellationToken);
            var root = JObject.Parse(json);
            root[nameof(Settings.GrokWebStatsigVerificationKey)] =
                material.VerificationKeyBase64;
            root[nameof(Settings.GrokWebStatsigAnimationKey)] =
                material.AnimationKey;
            await File.WriteAllTextAsync(
                settingsFilePath,
                root.ToString(Formatting.Indented) + Environment.NewLine,
                cancellationToken);

            Console.WriteLine(
                "grok-web-capture-statsig: PASS; verified current signing material "
                + $"and updated {settingsFilePath}");
        }
    }
}
