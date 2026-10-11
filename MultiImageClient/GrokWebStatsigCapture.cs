#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Captures one signing pair from the real grok.com Edit control. The
    // capture checks the browser-free page-key derivation against Chromium's
    // own result; --grok-web-statsig-pin additionally pins the pair.
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
            if (options.GrokWebStatsigPin
                && (string.IsNullOrWhiteSpace(settingsFilePath) || !File.Exists(settingsFilePath)))
            {
                throw new FileNotFoundException(
                    "The exact loaded settings file is required to pin Grok signing material.",
                    settingsFilePath);
            }

            var cookieHeader = GrokWebCookieLoader.LoadCookieHeader(cookiePath);
            using var httpClient = new GrokWebClient(cookieHeader);
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
            var capturedAtUtc = DateTimeOffset.UtcNow;
            var captured = new GrokWebStatsigSigner(
                material.VerificationKeyBase64,
                material.AnimationKey);
            Console.WriteLine(
                "grok-web-capture-statsig: the captured header reproduces exactly "
                + $"(key {captured.KeyFingerprint}).");

            var derivationPassed = false;
            try
            {
                using var pageSource = GrokWebStatsigSource.ForPage();
                var derived = await pageSource.DeriveAnimationKeyAsync(
                    material.VerificationKeyBase64,
                    cookieHeader,
                    cancellationToken);
                derivationPassed = string.Equals(derived, material.AnimationKey, StringComparison.Ordinal);
                Console.WriteLine(derivationPassed
                    ? "grok-web-capture-statsig: PASS; page-key derivation reproduces the browser's animation key."
                    : "grok-web-capture-statsig: FAIL; page-key derivation differs from the browser's "
                        + "animation key, so grok.com will reject page keys for this deployment.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"grok-web-capture-statsig: FAIL; page-key derivation failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (options.GrokWebStatsigPin)
            {
                var json = await File.ReadAllTextAsync(settingsFilePath, cancellationToken);
                var root = JObject.Parse(json);
                root[nameof(Settings.GrokWebStatsigVerificationKey)] = material.VerificationKeyBase64;
                root[nameof(Settings.GrokWebStatsigAnimationKey)] = material.AnimationKey;
                root[nameof(Settings.GrokWebStatsigCapturedAtUtc)] =
                    capturedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                await File.WriteAllTextAsync(
                    settingsFilePath,
                    root.ToString(Formatting.Indented) + Environment.NewLine,
                    cancellationToken);
                Console.WriteLine(
                    $"grok-web-capture-statsig: pinned key {captured.KeyFingerprint} in {settingsFilePath}. "
                    + "Restart to use it. It stops working after about 7 days; clear the three "
                    + "GrokWebStatsig settings to return to page keys.");
            }
            else
            {
                Console.WriteLine(
                    "grok-web-capture-statsig: settings unchanged. Add --grok-web-statsig-pin to pin this pair.");
            }

            if (!derivationPassed)
            {
                Environment.ExitCode = 1;
            }
        }
    }
}
