#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public class GrokWebWorkflow
    {
        public async Task<string?> RunAsync(
            AbstractPromptSource promptSource,
            Settings settings,
            MultiClientRunStats stats,
            RunOptions options)
        {
            var cookiePath = !string.IsNullOrWhiteSpace(options.GrokWebCookies)
                ? options.GrokWebCookies
                : settings.GrokWebCookiePath;

            if (string.IsNullOrWhiteSpace(cookiePath))
            {
                Console.Error.WriteLine("Grok web aborted: set GrokWebCookiePath in settings.json or pass --grok-web-cookies /path/to/cookies.txt");
                return null;
            }

            if (!File.Exists(Settings.ExpandPath(cookiePath)))
            {
                Console.Error.WriteLine($"Grok web aborted: cookie file not found: {Settings.ExpandPath(cookiePath)}");
                return null;
            }

            var mode = (options.GrokWebMode ?? "image").Trim().ToLowerInvariant();
            if (mode is "video-from-image" or "video_from_image" or "image-to-video" or "image_to_video")
            {
                mode = "video-from-image";
            }

            if ((mode == "edit" || mode == "video-from-image") && string.IsNullOrWhiteSpace(options.InputImagePath))
            {
                Console.Error.WriteLine($"Grok web mode '{mode}' requires --input-image /path/to/source.png");
                return null;
            }

            var promptValues = promptSource.Prompts
                .Select(p => p.Prompt)
                .Take(options.Limit == int.MaxValue ? int.MaxValue : options.Limit)
                .ToList();
            var prompts = mode == "video-from-image"
                ? promptValues.Select(p => p ?? string.Empty).ToList()
                : promptValues.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (mode == "video-from-image" && prompts.Count == 0)
            {
                // Image-to-video needs a source and a method; Grok's web app
                // explicitly accepts an empty motion prompt.
                prompts.Add(string.Empty);
            }

            if (prompts.Count == 0)
            {
                Console.Error.WriteLine("Grok web aborted: no prompts found. Use --prompt, --prompt-file, or PromptFiles.");
                return null;
            }

            // Browser-free signing is preferred when a complete, validated
            // deployment pair is configured. Otherwise CLI browser modes keep
            // their existing real-control transport.
            GrokWebStatsigSigner.TryCreateFromSettings(
                settings,
                out var statsigSigner,
                out _);
            // Only browser-free image edit has been live-verified. Keep video
            // on its established real-control browser path until separately
            // proven; sharing an endpoint is not evidence that every model
            // route has the same anti-bot contract.
            var needsBrowser = mode is "video" or "video-from-image"
                || (mode == "edit" && statsigSigner == null);
            await using var browserClient = needsBrowser
                ? new GrokWebBrowserClient(GrokWebBrowserClient.BuildOptions(
                    settings,
                    cookiePath,
                    headedOverride: options.GrokWebHeaded))
                : null;
            using var client = GrokWebClient.FromCookieFile(
                cookiePath,
                browserClient,
                statsigSigner);
            IImageGenerator generator;
            string sheetHeader;

            switch (mode)
            {
                case "image":
                    {
                        generator = new GrokWebImagineGenerator(
                            client,
                            maxConcurrency: 1,
                            stats,
                            pro: options.GrokWebPro,
                            aspectRatio: options.GrokWebAspectRatio,
                            enableSideBySide: options.GrokWebSideBySide,
                            settings: settings,
                            captureSessions: options.GrokWebCapture);
                        sheetHeader = options.GrokWebPro
                            ? "Grok Web Imagine Pro"
                            : "Grok Web Imagine";
                        break;
                    }
                case "video":
                    {
                        generator = new GrokWebImagineVideoGenerator(
                            client,
                            settings,
                            stats,
                            maxConcurrency: 1,
                            sourceAsset: null,
                            aspectRatio: options.GrokWebAspectRatio,
                            resolution: options.GrokWebVideoResolution,
                            durationSeconds: options.GrokWebVideoLength,
                            enableSideBySide: options.GrokWebSideBySide,
                            videoMode: options.GrokWebVideoMode);
                        sheetHeader = "Grok Web Imagine Video";
                        break;
                    }
                case "video-from-image":
                    {
                        generator = await GrokWebImagineVideoGenerator.CreateFromImageAsync(
                            client,
                            settings,
                            stats,
                            options.InputImagePath,
                            maxConcurrency: 1,
                            aspectRatio: options.GrokWebAspectRatio,
                            resolution: options.GrokWebVideoResolution,
                            durationSeconds: options.GrokWebVideoLength,
                            enableSideBySide: options.GrokWebSideBySide,
                            videoMode: options.GrokWebVideoMode);
                        sheetHeader = "Grok Web Imagine Video (from image)";
                        break;
                    }
                case "edit":
                    {
                        generator = await GrokWebImagineEditGenerator.CreateAsync(
                            client,
                            options.InputImagePath,
                            maxConcurrency: 1,
                            stats,
                            pro: options.GrokWebPro,
                            aspectRatio: options.GrokWebAspectRatio,
                            enableSideBySide: options.GrokWebSideBySide,
                            settings: settings,
                            captureSessions: options.GrokWebCapture);
                        sheetHeader = "Grok Web Imagine Edit";
                        break;
                    }
                default:
                    Console.Error.WriteLine($"Grok web aborted: unknown --grok-web-mode '{options.GrokWebMode}'. Use image, video, video-from-image, or edit.");
                    return null;
            }

            Logger.Log($"Grok web: mode={mode}, prompts={prompts.Count}, cookies={Settings.ExpandPath(cookiePath)}");
            return await GeneratorContactSheetRunner.RunOneGeneratorAsync(
                generator,
                prompts,
                new ImageManager(settings, stats),
                settings,
                stats,
                runLabel: "Grok web",
                sheetHeader: sheetHeader,
                openWhenDone: options.OpenImages);
        }
    }
}
