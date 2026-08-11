#nullable enable
using SixLabors.ImageSharp;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Edits/generates an image through the consumer grok.com "chat door":
    /// the uploaded source is sent as a normal app-chat conversation message
    /// and a chat model (grok-3) reads it, expands the instruction into a
    /// detailed edit prompt, and orchestrates imagine-image-edit. This differs
    /// from GrokWebImagineEditGenerator, which posts modelName=imagine-image-edit
    /// directly. Both ride the same signed POST /rest/app-chat/conversations/new
    /// transport; the request/response schema for this path was validated live
    /// 2026-08-10 (see GrokWebClient.RunImageChatEditAsync).
    public class GrokWebImagineChatEditGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly GrokWebAsset _sourceAsset;
        private readonly string _chatModel;
        private readonly string _aspectRatio;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineChat;

        public GrokWebImagineChatEditGenerator(
            GrokWebClient client,
            GrokWebAsset sourceAsset,
            int maxConcurrency,
            MultiClientRunStats stats,
            string chatModel = GrokWebClient.DefaultChatModel,
            string aspectRatio = "1:1")
        {
            _client = client;
            _sourceAsset = sourceAsset;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _chatModel = string.IsNullOrWhiteSpace(chatModel) ? GrokWebClient.DefaultChatModel : chatModel;
            _aspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "1:1" : aspectRatio;
        }

        public static async Task<GrokWebImagineChatEditGenerator> CreateAsync(
            GrokWebClient client,
            string inputImagePath,
            int maxConcurrency,
            MultiClientRunStats stats,
            string chatModel = GrokWebClient.DefaultChatModel,
            string aspectRatio = "")
        {
            var uploaded = await client.UploadImageAsync(inputImagePath);
            var asset = new GrokWebAsset
            {
                AssetId = uploaded.AssetId,
                MediaUrl = uploaded.MediaUrl,
                PostId = uploaded.AssetId,
            };
            var resolvedAspect = string.IsNullOrWhiteSpace(aspectRatio)
                ? DeriveAspectRatio(inputImagePath)
                : aspectRatio;
            return new GrokWebImagineChatEditGenerator(
                client, asset, maxConcurrency, stats,
                chatModel: chatModel, aspectRatio: resolvedAspect);
        }

        public string GetFilenamePart(PromptDetails pd)
        {
            var ar = _aspectRatio.Replace(':', 'x');
            return $"grokweb-chat_{_chatModel}_{ar}";
        }

        public List<string> GetRightParts()
        {
            return new List<string>
            {
                "Grok Web Chat Image",
                $"{_chatModel} + imagine-image-edit",
                "grok.com app-chat (chat door)",
                $"AR {_aspectRatio}",
            };
        }

        public string GetGeneratorSpecPart()
            => $"Grok Web Chat Image ({_chatModel})  {_aspectRatio}";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                Logger.Log($"\t-> Grok Web Chat Image model={_chatModel} asset={_sourceAsset.AssetId}: {prompt}");

                var chat = await _client.RunImageChatEditAsync(prompt, _sourceAsset, _chatModel);
                if (!string.IsNullOrWhiteSpace(chat.ErrorMessage))
                {
                    _stats.GrokImageGenerationErrorCount++;
                    return Fail(
                        $"Grok web chat image failed: {chat.ErrorMessage}",
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }

                var urls = chat.GeneratedImageUrls;
                if (urls.Count == 0)
                {
                    // The chat model can answer with text and no image (e.g.
                    // a clarifying question or a refusal). That is a hard
                    // failure for an image endpoint, not an empty success.
                    _stats.GrokImageGenerationErrorCount++;
                    var hint = string.IsNullOrWhiteSpace(chat.ModelMessage) ? "no image returned" : chat.ModelMessage;
                    return Fail(
                        $"Grok web chat image completed without an image ({Truncate(hint, 300)}).",
                        promptDetails,
                        generator,
                        sw.ElapsedMilliseconds);
                }

                var images = new List<CreatedBase64Image>();
                foreach (var url in urls)
                {
                    var bytes = await _client.DownloadBytesAsync(url);
                    images.Add(new CreatedBase64Image
                    {
                        bytesBase64 = Convert.ToBase64String(bytes),
                        newPrompt = prompt,
                    });
                }

                sw.Stop();
                _stats.GrokImageGenerationSuccessCount++;
                Logger.Log($"\t<- Grok Web Chat Image OK in {sw.ElapsedMilliseconds} ms; {images.Count} image(s)");

                return new TaskProcessResult
                {
                    IsSuccess = true,
                    Base64ImageDatas = images,
                    ContentType = GuessContentType(urls[0], images[0].bytesBase64),
                    PromptDetails = promptDetails,
                    ImageGenerator = ApiType,
                    ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                    CreateTotalMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _stats.GrokImageGenerationErrorCount++;
                var detail = ex is GrokWebException gwe && !string.IsNullOrEmpty(gwe.ResponseBody)
                    ? $"{ex.Message} body={Truncate(gwe.ResponseBody, 400)}"
                    : ex.Message;
                Logger.Log($"\t<- Grok Web Chat Image FAIL: {detail}");
                return Fail(detail, promptDetails, generator, sw.ElapsedMilliseconds);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        internal static string DeriveAspectRatio(string imagePath)
        {
            var info = Image.Identify(imagePath);
            if (info == null || info.Width <= 0 || info.Height <= 0)
            {
                throw new InvalidDataException(
                    $"Could not read source image dimensions from '{imagePath}'.");
            }
            return UiShapeMapping.GrokAspectForInput(info.Width, info.Height);
        }

        private static string GuessContentType(string url, string b64)
        {
            var lower = url.ToLowerInvariant();
            if (lower.Contains(".webp")) return "image/webp";
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            try
            {
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length >= 8
                    && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                {
                    return "image/png";
                }
            }
            catch (FormatException)
            {
                // Fall through to jpeg default for grok asset URLs.
            }
            return "image/jpeg";
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        private TaskProcessResult Fail(string message, PromptDetails pd, IImageGenerator generator, long elapsedMs)
        {
            return new TaskProcessResult
            {
                IsSuccess = false,
                ErrorMessage = message,
                PromptDetails = pd,
                ImageGenerator = ApiType,
                ImageGeneratorDescription = generator.GetGeneratorSpecPart(),
                CreateTotalMs = elapsedMs,
            };
        }
    }
}
