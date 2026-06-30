#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class GrokWebException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }

        public GrokWebException(string message, int statusCode = 0, string responseBody = "")
            : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
        }
    }

    public sealed class GrokWebAsset
    {
        public required string AssetId { get; init; }
        public required string MediaUrl { get; init; }
        public string? PostId { get; init; }
    }

    public sealed class GrokWebImageGenerationResult
    {
        public required List<byte[]> Images { get; init; }
        public string? ModelName { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string? Mode { get; init; }
        public string? CaptureDirectory { get; init; }
        public bool UsedPreviewFallback { get; init; }
    }

    public sealed class GrokWebAppChatResult
    {
        public required List<string> GeneratedImageUrls { get; init; }
        public required List<string> GeneratedVideoUrls { get; init; }
        public string? ModelMessage { get; init; }
        public string? RequestTraceId { get; init; }
    }

    public sealed class GrokWebClient : IDisposable
    {
        public const string Origin = "https://grok.com";
        public const string ImagineListenWebSocket = "wss://grok.com/ws/imagine/listen";

        private static readonly Regex UuidRegex = new(
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly string _cookieHeader;

        public GrokWebClient(string cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                throw new ArgumentException("Cookie header is empty.", nameof(cookieHeader));
            }

            _cookieHeader = cookieHeader.Trim();
            _http = new HttpClient
            {
                BaseAddress = new Uri(Origin),
                Timeout = TimeSpan.FromMinutes(15),
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", Origin);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", Origin + "/imagine");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", _cookieHeader);
        }

        public static GrokWebClient FromCookieFile(string cookieFilePath)
            => new(GrokWebCookieLoader.LoadCookieHeader(cookieFilePath));

        public async Task<GrokWebImageGenerationResult> GenerateImageAsync(
            string prompt,
            string aspectRatio,
            bool enablePro,
            bool enableSideBySide,
            TimeSpan timeout,
            string? captureBaseFolder = null,
            CancellationToken cancellationToken = default)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Cookie", _cookieHeader);
            ws.Options.SetRequestHeader("Origin", Origin);
            ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");

            var requestId = Guid.NewGuid().ToString();
            await using var capture = !string.IsNullOrWhiteSpace(captureBaseFolder)
                ? GrokWebSessionCapture.Start(captureBaseFolder, requestId, prompt, aspectRatio, enablePro, enableSideBySide, timeout)
                : null;

            var summary = new GrokWebSessionSummary
            {
                RequestId = requestId,
                ExpectedJobs = enableSideBySide ? 4 : 1,
            };
            var exitReason = "unknown";

            await ws.ConnectAsync(new Uri(ImagineListenWebSocket), cancellationToken);

            var resetPayload = new
            {
                type = "conversation.item.create",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                item = new
                {
                    type = "message",
                    content = new object[] { new { type = "reset" } },
                },
            };
            capture?.LogOutbound(resetPayload);
            await SendJsonAsync(ws, resetPayload, cancellationToken);

            var generatePayload = new
            {
                type = "conversation.item.create",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                item = new
                {
                    type = "message",
                    content = new object[]
                    {
                        new
                        {
                            requestId,
                            text = prompt,
                            type = "input_text",
                            properties = new
                            {
                                section_count = 0,
                                is_kids_mode = false,
                                enable_nsfw = true,
                                skip_upsampler = false,
                                enable_side_by_side = enableSideBySide,
                                is_initial = false,
                                aspect_ratio = aspectRatio,
                                enable_pro = enablePro,
                            },
                        },
                    },
                },
            };
            capture?.LogOutbound(generatePayload);
            await SendJsonAsync(ws, generatePayload, cancellationToken);

            var finalImages = new List<byte[]>();
            var previewFrames = new List<byte[]>();
            string? modelName = null;
            string? mode = null;
            var width = 0;
            var height = 0;
            var completedJobs = new HashSet<string>(StringComparer.Ordinal);
            var expectedJobs = enableSideBySide ? 4 : 1;
            summary.ExpectedJobs = expectedJobs;
            var skippedPartialFrames = 0;
            var lastProgressLogAt = 0;
            DateTime? allJobsCompletedUtc = null;
            var postJobGrace = TimeSpan.FromSeconds(45);
            var usedPreviewFallback = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
            while (!timeoutCts.IsCancellationRequested)
            {
                if (ShouldFinishAfterPostJobGrace(finalImages.Count, completedJobs.Count, expectedJobs, allJobsCompletedUtc, postJobGrace))
                {
                    exitReason = "post_job_grace_elapsed";
                    break;
                }

                string? payload;
                if (allJobsCompletedUtc.HasValue && finalImages.Count < expectedJobs)
                {
                    var remaining = postJobGrace - (DateTime.UtcNow - allJobsCompletedUtc.Value);
                    if (remaining <= TimeSpan.Zero)
                    {
                        exitReason = "post_job_grace_elapsed_before_receive";
                        break;
                    }

                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                    graceCts.CancelAfter(remaining);
                    try
                    {
                        payload = await ReceiveTextAsync(ws, graceCts.Token);
                    }
                    catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
                    {
                        exitReason = "post_job_grace_receive_timeout";
                        break;
                    }
                }
                else
                {
                    payload = await ReceiveTextAsync(ws, timeoutCts.Token);
                }

                if (payload == null)
                {
                    exitReason = "websocket_closed";
                    break;
                }

                capture?.LogInbound(payload);

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                if (type == "error")
                {
                    var errCode = root.TryGetProperty("err_code", out var errCodeEl) ? errCodeEl.GetString() : null;
                    var errMsg = root.TryGetProperty("err_msg", out var errMsgEl) ? errMsgEl.GetString() : null;
                    exitReason = string.IsNullOrWhiteSpace(errCode) ? "error" : errCode;
                    var message = !string.IsNullOrWhiteSpace(errMsg) ? errMsg : "Grok web returned an error.";
                    if (!string.IsNullOrWhiteSpace(errCode))
                    {
                        message = $"{message} ({errCode})";
                    }

                    Logger.Log($"\t   Grok web error: {message}");
                    throw new GrokWebException(message);
                }

                if (type == "image" && root.TryGetProperty("blob", out var blobEl))
                {
                    var blob = blobEl.GetString();
                    if (!string.IsNullOrEmpty(blob))
                    {
                        var bytes = Convert.FromBase64String(blob);
                        if (IsFinalGrokWebImage(bytes, blob))
                        {
                            finalImages.Add(bytes);
                            Logger.Log($"\t   Grok web final frame {finalImages.Count}/{expectedJobs} ({bytes.Length / 1024} KB).");
                        }
                        else
                        {
                            previewFrames.Add(bytes);
                            skippedPartialFrames++;
                            if (skippedPartialFrames - lastProgressLogAt >= 10)
                            {
                                lastProgressLogAt = skippedPartialFrames;
                                Logger.Log($"\t   Grok web generating… {skippedPartialFrames} preview frame(s), {finalImages.Count} final(s), {completedJobs.Count} job(s) completed.");
                            }
                        }
                    }

                    if (ShouldFinishGrokWebImageStream(finalImages.Count, completedJobs.Count, expectedJobs))
                    {
                        exitReason = "enough_final_frames";
                        break;
                    }

                    continue;
                }

                if (type != "json")
                {
                    continue;
                }

                if (root.TryGetProperty("moderated", out var moderatedEl)
                    && moderatedEl.ValueKind == JsonValueKind.True)
                {
                    exitReason = "moderated";
                    throw new GrokWebException("Grok web rejected or moderated this prompt.");
                }

                if (root.TryGetProperty("model_name", out var modelEl))
                {
                    modelName = modelEl.GetString();
                }
                if (root.TryGetProperty("mode", out var modeEl))
                {
                    mode = modeEl.GetString();
                }
                if (root.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var w))
                {
                    width = w;
                }
                if (root.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var h))
                {
                    height = h;
                }

                var status = root.TryGetProperty("current_status", out var statusEl) ? statusEl.GetString() : null;
                var jobId = root.TryGetProperty("job_id", out var jobEl) ? jobEl.GetString() : null;
                if (status == "completed" && !string.IsNullOrEmpty(jobId))
                {
                    completedJobs.Add(jobId);
                    Logger.Log($"\t   Grok web job completed ({completedJobs.Count}/{expectedJobs}).");
                    if (completedJobs.Count >= expectedJobs && allJobsCompletedUtc == null)
                    {
                        allJobsCompletedUtc = DateTime.UtcNow;
                        if (finalImages.Count == 0)
                        {
                            Logger.Log($"\t   Grok web all jobs done with 0 final frames; waiting up to {postJobGrace.TotalSeconds:0}s for upscaled frames.");
                        }
                    }
                }

                if (ShouldFinishGrokWebImageStream(finalImages.Count, completedJobs.Count, expectedJobs))
                {
                    exitReason = "jobs_complete_with_finals";
                    break;
                }
            }

            if (timeoutCts.IsCancellationRequested && exitReason == "unknown")
            {
                exitReason = "timeout";
            }

            var images = SelectFinalGrokWebImages(finalImages, previewFrames, expectedJobs, out usedPreviewFallback);

            if (images.Count == 0)
            {
                var previewHint = skippedPartialFrames > 0
                    ? $" ({skippedPartialFrames} intermediate preview frame(s) captured)"
                    : "";
                summary.ErrorMessage = $"Grok web image generation returned no usable image frames{previewHint}.";
                throw new GrokWebException(summary.ErrorMessage);
            }

            if (usedPreviewFallback)
            {
                Logger.Log($"\t   Grok web using {images.Count} preview frame(s) for output (no signed finals detected).");
            }
            else if (skippedPartialFrames > 0)
            {
                Logger.Log($"\t   Grok web selected {images.Count} final frame(s); {skippedPartialFrames} preview frame(s) captured separately.");
            }

            summary.ExitReason = exitReason;
            summary.FinalFrameCount = finalImages.Count;
            summary.PreviewFrameCount = previewFrames.Count;
            summary.CompletedJobCount = completedJobs.Count;
            summary.CompletedJobIds = completedJobs.ToList();
            summary.SelectedOutputCount = images.Count;
            summary.ModelName = modelName;
            summary.Mode = mode;
            summary.Width = width;
            summary.Height = height;
            summary.UsedPreviewFallback = usedPreviewFallback;

            return new GrokWebImageGenerationResult
            {
                Images = images,
                ModelName = modelName,
                Width = width,
                Height = height,
                Mode = mode,
                CaptureDirectory = capture?.SessionDirectory,
                UsedPreviewFallback = usedPreviewFallback,
            };
            }
            catch (Exception ex)
            {
                summary.ExitReason = exitReason == "unknown" ? "exception" : exitReason;
                summary.ErrorMessage = ex.Message;
                throw;
            }
            finally
            {
                if (capture != null)
                {
                    await capture.CompleteAsync(summary);
                }
            }
        }

        private static bool ShouldFinishAfterPostJobGrace(
            int finalCount,
            int completedJobCount,
            int expectedJobs,
            DateTime? allJobsCompletedUtc,
            TimeSpan postJobGrace)
        {
            if (!allJobsCompletedUtc.HasValue || finalCount >= expectedJobs)
            {
                return false;
            }

            if (completedJobCount < expectedJobs)
            {
                return false;
            }

            if (DateTime.UtcNow - allJobsCompletedUtc.Value < postJobGrace)
            {
                return false;
            }

            return true;
        }

        private static bool ShouldFinishGrokWebImageStream(int finalCount, int completedJobCount, int expectedJobs)
        {
            if (finalCount >= expectedJobs)
            {
                return true;
            }

            // Some sessions emit all finals before every job completion event arrives.
            return completedJobCount >= expectedJobs && finalCount > 0;
        }

        public static bool ClassifyGrokWebImageFrame(byte[] bytes, string base64Blob, out string magic, out string format)
        {
            magic = bytes.Length >= 4
                ? Convert.ToHexString(bytes.AsSpan(0, 4))
                : Convert.ToHexString(bytes);
            format = GuessImageFormat(bytes);
            return IsFinalGrokWebImage(bytes, base64Blob);
        }

        private static string GuessImageFormat(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "png";
            }

            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "jpeg";
            }

            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "webp";
            }

            if (bytes.Length >= 4
                && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "gif";
            }

            return "bin";
        }

        private static bool IsFinalGrokWebImage(byte[] bytes, string base64Blob)
        {
            // Current preview frames are low-resolution PNGs. Final/upscaled frames
            // are JPEGs with EXIF APP1 metadata; the APP1 length byte sequence varies.
            if (HasJpegExifApp1Segment(bytes))
            {
                return true;
            }

            return base64Blob.Contains("Signature:", StringComparison.Ordinal)
                   || base64Blob.Contains("ASCIISignature", StringComparison.Ordinal);
        }

        private static bool HasJpegExifApp1Segment(byte[] bytes)
        {
            if (bytes.Length < 10 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            {
                return false;
            }

            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset] != 0xFF)
                {
                    return false;
                }

                var marker = bytes[offset + 1];
                if (marker == 0xDA || marker == 0xD9)
                {
                    return false;
                }

                var segmentLength = (bytes[offset + 2] << 8) | bytes[offset + 3];
                if (segmentLength < 2 || offset + 2 + segmentLength > bytes.Length)
                {
                    return false;
                }

                if (marker == 0xE1
                    && segmentLength >= 8
                    && bytes[offset + 4] == (byte)'E'
                    && bytes[offset + 5] == (byte)'x'
                    && bytes[offset + 6] == (byte)'i'
                    && bytes[offset + 7] == (byte)'f'
                    && bytes[offset + 8] == 0
                    && bytes[offset + 9] == 0)
                {
                    return true;
                }

                offset += 2 + segmentLength;
            }

            return false;
        }

        private static List<byte[]> SelectFinalGrokWebImages(
            List<byte[]> finalImages,
            List<byte[]> previewFrames,
            int expectedJobs,
            out bool usedPreviewFallback)
        {
            usedPreviewFallback = false;

            if (finalImages.Count >= expectedJobs)
            {
                return finalImages.TakeLast(expectedJobs).ToList();
            }

            if (finalImages.Count > 0)
            {
                return finalImages;
            }

            if (previewFrames.Count >= expectedJobs)
            {
                usedPreviewFallback = true;
                return previewFrames.TakeLast(expectedJobs).ToList();
            }

            if (previewFrames.Count > 0)
            {
                usedPreviewFallback = true;
                return previewFrames;
            }

            return new List<byte[]>();
        }

        public async Task<GrokWebAsset> UploadImageAsync(string localPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException("Upload source image not found.", localPath);
            }

            var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
            var fileName = Path.GetFileName(localPath);
            var contentType = GuessContentType(fileName);

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "file", fileName);

            using var response = await _http.PostAsync("/http/upload-file-v2/direct", form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GrokWebException(
                    $"Grok web upload failed ({(int)response.StatusCode}).",
                    (int)response.StatusCode,
                    body);
            }

            var assetId = TryExtractAssetId(body);
            if (string.IsNullOrEmpty(assetId))
            {
                assetId = await FindLatestUploadedAssetIdAsync(cancellationToken);
            }

            if (string.IsNullOrEmpty(assetId))
            {
                throw new GrokWebException($"Grok web upload succeeded but no asset id was found. body={Truncate(body, 500)}");
            }

            var asset = await GetAssetAsync(assetId, cancellationToken);
            return asset;
        }

        public async Task<GrokWebAsset> CreateImagePostAsync(string mediaUrl, CancellationToken cancellationToken = default)
        {
            using var response = await PostJsonAsync("/rest/media/post/create", new
            {
                mediaType = "MEDIA_POST_TYPE_IMAGE",
                mediaUrl,
            }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            var post = doc.RootElement.GetProperty("post");
            var postId = post.GetProperty("id").GetString() ?? string.Empty;
            return new GrokWebAsset
            {
                AssetId = postId,
                MediaUrl = mediaUrl,
                PostId = postId,
            };
        }

        public async Task<string> CreateVideoPostPlaceholderAsync(string prompt, CancellationToken cancellationToken = default)
        {
            using var response = await PostJsonAsync("/rest/media/post/create", new
            {
                mediaType = "MEDIA_POST_TYPE_VIDEO",
                prompt,
            }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("post").GetProperty("id").GetString()
                   ?? throw new GrokWebException("Grok web video placeholder post had no id.");
        }

        public async Task<GrokWebAppChatResult> RunVideoGenerationAsync(
            string prompt,
            string parentPostId,
            string aspectRatio,
            int videoLengthSeconds,
            string resolutionName,
            GrokWebAsset? sourceAsset,
            bool enableSideBySide,
            CancellationToken cancellationToken = default)
        {
            object payload;
            if (sourceAsset != null)
            {
                payload = new
                {
                    temporary = true,
                    modelName = "imagine-video-gen",
                    message = $"{sourceAsset.MediaUrl}  {prompt} --mode=custom",
                    fileAttachments = new[] { sourceAsset.AssetId },
                    enableSideBySide = enableSideBySide,
                    responseMetadata = new
                    {
                        experiments = Array.Empty<object>(),
                        modelConfigOverride = new
                        {
                            modelMap = new
                            {
                                videoGenModelConfig = new
                                {
                                    parentPostId = sourceAsset.PostId ?? sourceAsset.AssetId,
                                    aspectRatio,
                                    videoLength = videoLengthSeconds,
                                    isVideoEdit = false,
                                    resolutionName,
                                },
                            },
                        },
                    },
                };
            }
            else
            {
                payload = new
                {
                    temporary = true,
                    modelName = "imagine-video-gen",
                    message = $"{prompt} --mode=custom",
                    enableSideBySide = enableSideBySide,
                    responseMetadata = new
                    {
                        experiments = Array.Empty<object>(),
                        modelConfigOverride = new
                        {
                            modelMap = new
                            {
                                videoGenModelConfig = new
                                {
                                    parentPostId,
                                    aspectRatio,
                                    videoLength = videoLengthSeconds,
                                    resolutionName,
                                },
                            },
                        },
                    },
                };
            }

            return await RunAppChatAsync(payload, cancellationToken);
        }

        public async Task<GrokWebAppChatResult> RunImageEditAsync(
            string prompt,
            GrokWebAsset sourceAsset,
            bool enableSideBySide,
            CancellationToken cancellationToken = default)
        {
            var payload = new
            {
                temporary = true,
                modelName = "imagine-image-edit",
                message = prompt,
                enableImageGeneration = true,
                returnImageBytes = false,
                returnRawGrokInXaiRequest = false,
                enableImageStreaming = true,
                imageGenerationCount = enableSideBySide ? 2 : 1,
                forceConcise = false,
                enableSideBySide = enableSideBySide,
                sendFinalMetadata = true,
                isReasoning = false,
                disableTextFollowUps = true,
                responseMetadata = new
                {
                    modelConfigOverride = new
                    {
                        modelMap = new
                        {
                            imageEditModelConfig = new
                            {
                                imageReferences = new[] { sourceAsset.MediaUrl },
                                parentPostId = sourceAsset.PostId ?? sourceAsset.AssetId,
                            },
                            imageEditModel = "imagine",
                        },
                    },
                },
                disableMemory = false,
                forceSideBySide = false,
            };

            return await RunAppChatAsync(payload, cancellationToken);
        }

        public async Task<string?> PollForVideoUrlAsync(
            string postId,
            string promptHint,
            TimeSpan pollInterval,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = await FindVideoUrlInRecentPostsAsync(postId, promptHint, cancellationToken);
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            return null;
        }

        public async Task<List<string>> PollForImageUrlsAsync(
            string parentPostId,
            TimeSpan pollInterval,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var urls = await FindImageUrlsInRecentPostsAsync(parentPostId, cancellationToken);
                if (urls.Count > 0)
                {
                    return urls;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            return new List<string>();
        }

        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/imagine");
            using var response = await _http.SendAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GrokWebException(
                    $"Grok web download failed ({(int)response.StatusCode}) for {url}.",
                    (int)response.StatusCode,
                    Encoding.UTF8.GetString(bytes));
            }

            return bytes;
        }

        public void Dispose() => _http.Dispose();

        private async Task<GrokWebAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync($"/rest/assets/{assetId}", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var key = root.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;
            var mediaUrl = !string.IsNullOrEmpty(key)
                ? $"https://assets.grok.com/{key.TrimStart('/')}"
                : $"https://assets.grok.com/users/unknown/{assetId}/content";

            return new GrokWebAsset
            {
                AssetId = assetId,
                MediaUrl = mediaUrl,
            };
        }

        private async Task<string?> FindLatestUploadedAssetIdAsync(CancellationToken cancellationToken)
        {
            var url = "/rest/assets?pageSize=1&orderBy=ORDER_BY_LAST_USE_TIME&source=SOURCE_UPLOADED&includeImagineFiles=true";
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("assetId", out var idEl))
                {
                    return idEl.GetString();
                }
            }

            return null;
        }

        private async Task<GrokWebAppChatResult> RunAppChatAsync(object payload, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/rest/app-chat/conversations/new")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new GrokWebException(
                    $"Grok web app-chat failed ({(int)response.StatusCode}).",
                    (int)response.StatusCode,
                    err);
            }

            var imageUrls = new HashSet<string>(StringComparer.Ordinal);
            var videoUrls = new HashSet<string>(StringComparer.Ordinal);
            string? modelMessage = null;
            string? traceId = null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CollectMediaUrlsFromJsonLine(line, imageUrls, videoUrls, ref modelMessage, ref traceId);
            }

            return new GrokWebAppChatResult
            {
                GeneratedImageUrls = imageUrls.ToList(),
                GeneratedVideoUrls = videoUrls.ToList(),
                ModelMessage = modelMessage,
                RequestTraceId = traceId,
            };
        }

        private async Task<string?> FindVideoUrlInRecentPostsAsync(
            string postId,
            string promptHint,
            CancellationToken cancellationToken)
        {
            using var response = await PostJsonAsync("/rest/media/post/list", new
            {
                limit = 40,
                filter = new { source = "MEDIA_POST_SOURCE_LIKED", safeForWork = false },
            }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var post in posts.EnumerateArray())
            {
                var url = FindVideoUrlInPost(post, postId, promptHint);
                if (!string.IsNullOrEmpty(url))
                {
                    return url;
                }
            }

            return null;
        }

        private async Task<List<string>> FindImageUrlsInRecentPostsAsync(string parentPostId, CancellationToken cancellationToken)
        {
            using var response = await PostJsonAsync("/rest/media/post/list", new
            {
                limit = 40,
                filter = new { source = "MEDIA_POST_SOURCE_LIKED", safeForWork = false },
            }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, body);

            var urls = new HashSet<string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            foreach (var post in posts.EnumerateArray())
            {
                CollectImageUrlsFromPost(post, parentPostId, urls);
            }

            return urls.ToList();
        }

        private static string? FindVideoUrlInPost(JsonElement post, string postId, string promptHint)
        {
            var id = post.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var prompt = post.TryGetProperty("prompt", out var promptEl) ? promptEl.GetString() : null;
            var matches = string.Equals(id, postId, StringComparison.OrdinalIgnoreCase)
                          || (!string.IsNullOrWhiteSpace(promptHint)
                              && !string.IsNullOrWhiteSpace(prompt)
                              && prompt.Contains(promptHint, StringComparison.OrdinalIgnoreCase));

            if (!matches)
            {
                foreach (var child in EnumerateChildPosts(post))
                {
                    var childUrl = FindVideoUrlInPost(child, postId, promptHint);
                    if (!string.IsNullOrEmpty(childUrl))
                    {
                        return childUrl;
                    }
                }
                return null;
            }

            foreach (var candidate in EnumerateMediaUrls(post))
            {
                if (candidate.Contains("generated_video.mp4", StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            foreach (var child in EnumerateChildPosts(post))
            {
                foreach (var candidate in EnumerateMediaUrls(child))
                {
                    if (candidate.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static void CollectImageUrlsFromPost(JsonElement post, string parentPostId, HashSet<string> urls)
        {
            var id = post.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var originalPostId = post.TryGetProperty("originalPostId", out var origEl) ? origEl.GetString() : null;
            var isChild = string.Equals(originalPostId, parentPostId, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(id, parentPostId, StringComparison.OrdinalIgnoreCase);

            if (isChild)
            {
                foreach (var url in EnumerateMediaUrls(post))
                {
                    if (url.Contains("/content", StringComparison.OrdinalIgnoreCase)
                        && !url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(url);
                    }
                }
            }

            foreach (var child in EnumerateChildPosts(post))
            {
                CollectImageUrlsFromPost(child, parentPostId, urls);
            }
        }

        private static IEnumerable<JsonElement> EnumerateChildPosts(JsonElement post)
        {
            if (post.TryGetProperty("childPosts", out var childPosts) && childPosts.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in childPosts.EnumerateArray())
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<string> EnumerateMediaUrls(JsonElement element)
        {
            foreach (var prop in new[] { "mediaUrl", "hdMediaUrl", "thumbnailImageUrl" })
            {
                if (element.TryGetProperty(prop, out var urlEl))
                {
                    var url = urlEl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        yield return url;
                    }
                }
            }

            foreach (var arrayName in new[] { "images", "videos" })
            {
                if (!element.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in arr.EnumerateArray())
                {
                    foreach (var url in EnumerateMediaUrls(item))
                    {
                        yield return url;
                    }
                }
            }
        }

        private static void CollectMediaUrlsFromJsonLine(
            string line,
            HashSet<string> imageUrls,
            HashSet<string> videoUrls,
            ref string? modelMessage,
            ref string? traceId)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("response", out var response)
                    && response.TryGetProperty("modelResponse", out var modelResponse))
                {
                    if (modelResponse.TryGetProperty("message", out var msgEl))
                    {
                        modelMessage = msgEl.GetString();
                    }

                    if (modelResponse.TryGetProperty("generatedImageUrls", out var genImages)
                        && genImages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in genImages.EnumerateArray())
                        {
                            var url = item.GetString();
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                imageUrls.Add(url);
                            }
                        }
                    }

                    if (modelResponse.TryGetProperty("metadata", out var metadata)
                        && metadata.TryGetProperty("request_trace_id", out var traceEl))
                    {
                        traceId = traceEl.GetString();
                    }
                }

                foreach (Match match in Regex.Matches(line, @"https://assets\.grok\.com[^""'\s]+", RegexOptions.IgnoreCase))
                {
                    var url = match.Value;
                    if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        videoUrls.Add(url);
                    }
                    else if (url.Contains("/content", StringComparison.OrdinalIgnoreCase))
                    {
                        imageUrls.Add(url);
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed partial lines.
            }
        }

        private async Task<HttpResponseMessage> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return await _http.PostAsync(path, content, cancellationToken);
        }

        private static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            throw new GrokWebException(
                $"Grok web request failed ({(int)response.StatusCode}).",
                (int)response.StatusCode,
                body);
        }

        private static string? TryExtractAssetId(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var name in new[] { "assetId", "fileMetadataId", "id", "fileId" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var el))
                    {
                        var value = el.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // fall through to regex
            }

            var match = UuidRegex.Match(body);
            return match.Success ? match.Value : null;
        }

        private static string GuessContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/png",
            };
        }

        private static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 256];
            var builder = new StringBuilder();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return builder.ToString();
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s.Substring(0, max) + "...";
    }
}
