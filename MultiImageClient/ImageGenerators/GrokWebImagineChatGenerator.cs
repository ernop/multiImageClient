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
    /// Generates or edits an image through the consumer grok.com "chat door":
    /// the prompt is sent as a normal app-chat conversation message and a chat
    /// model (grok-3) expands it and calls grok.com's image tool. With a source
    /// image the upload rides the message and the model orchestrates
    /// imagine-image-edit; without one the model generates from the text
    /// alone. This differs from GrokWebImagineEditGenerator, which posts
    /// modelName=imagine-image-edit directly. Both ride the same POST
    /// /rest/app-chat/conversations/new transport (see
    /// GrokWebClient.RunImageChatAsync).
    public class GrokWebImagineChatGenerator : IImageGenerator
    {
        private readonly GrokWebClient _client;
        private readonly SemaphoreSlim _semaphore;
        private readonly MultiClientRunStats _stats;
        private readonly GrokWebAsset? _sourceAsset;
        private readonly string _chatModel;
        // Label only: the chat door sends no aspect ratio. Edits show the
        // source image's nearest ratio; text-only generation leaves it blank
        // because the chat model chooses the shape.
        private readonly string _aspectRatio;

        public ImageGeneratorApiType ApiType => ImageGeneratorApiType.GrokWebImagineChat;

        public GrokWebImagineChatGenerator(
            GrokWebClient client,
            GrokWebAsset? sourceAsset,
            int maxConcurrency,
            MultiClientRunStats stats,
            string chatModel = GrokWebClient.DefaultChatModel,
            string aspectRatio = "")
        {
            _client = client;
            _sourceAsset = sourceAsset;
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _stats = stats;
            _chatModel = string.IsNullOrWhiteSpace(chatModel) ? GrokWebClient.DefaultChatModel : chatModel;
            _aspectRatio = aspectRatio ?? "";
        }

        public static GrokWebImagineChatGenerator CreateForText(
            GrokWebClient client,
            int maxConcurrency,
            MultiClientRunStats stats,
            string chatModel = GrokWebClient.DefaultChatModel)
            => new GrokWebImagineChatGenerator(client, null, maxConcurrency, stats, chatModel);

        public static async Task<GrokWebImagineChatGenerator> CreateAsync(
            GrokWebClient client,
            string inputImagePath,
            int maxConcurrency,
            MultiClientRunStats stats,
            string chatModel = GrokWebClient.DefaultChatModel)
        {
            var uploaded = await client.UploadImageAsync(inputImagePath);
            var asset = new GrokWebAsset
            {
                AssetId = uploaded.AssetId,
                MediaUrl = uploaded.MediaUrl,
                PostId = uploaded.AssetId,
            };
            return new GrokWebImagineChatGenerator(
                client, asset, maxConcurrency, stats,
                chatModel: chatModel, aspectRatio: DeriveAspectRatio(inputImagePath));
        }

        public string GetFilenamePart(PromptDetails pd)
            => _aspectRatio.Length == 0
                ? $"grokweb-chat_{_chatModel}"
                : $"grokweb-chat_{_chatModel}_{_aspectRatio.Replace(':', 'x')}";

        public List<string> GetRightParts()
        {
            var parts = new List<string>
            {
                "Grok Web Chat Image",
                _sourceAsset == null ? $"{_chatModel} + image tool" : $"{_chatModel} + imagine-image-edit",
                "grok.com app-chat (chat door)",
            };
            if (_aspectRatio.Length > 0)
            {
                parts.Add($"AR {_aspectRatio}");
            }
            return parts;
        }

        public string GetGeneratorSpecPart()
            => _aspectRatio.Length == 0
                ? $"Grok Web Chat Image ({_chatModel})"
                : $"Grok Web Chat Image ({_chatModel})  {_aspectRatio}";

        public decimal GetCost() => 0m;

        public async Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails)
        {
            await _semaphore.WaitAsync();
            var sw = Stopwatch.StartNew();
            try
            {
                _stats.GrokImageGenerationRequestCount++;
                var prompt = promptDetails.Prompt ?? string.Empty;
                Logger.Log($"\t-> Grok Web Chat Image model={_chatModel} asset={_sourceAsset?.AssetId ?? "none (text only)"}: {prompt}");

                var chat = await _client.RunImageChatAsync(prompt, _sourceAsset, _chatModel);
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
