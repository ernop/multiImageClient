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
    }

    public sealed class GrokWebAppChatResult
    {
        public required List<string> GeneratedImageUrls { get; init; }
        public required List<string> GeneratedVideoUrls { get; init; }
        public string? ModelMessage { get; init; }
        public string? ErrorMessage { get; init; }
        public string? RequestTraceId { get; init; }
    }

    public sealed class GrokWebVideoPollResult
    {
        public string? VideoUrl { get; init; }
        public string? ErrorMessage { get; init; }
        public bool IsTerminal => !string.IsNullOrWhiteSpace(VideoUrl)
                                  || !string.IsNullOrWhiteSpace(ErrorMessage);
    }

    public sealed class GrokWebClient : IDisposable
    {
        public const string Origin = "https://grok.com";
        public const string ImagineListenWebSocket = "wss://grok.com/ws/imagine/listen";

        /// Hard prompt-length cap of the consumer imagine WebSocket, verified
        /// empirically 2026-07-30: an 8192-char prompt generated normally while
        /// 8193 chars was rejected instantly (pre-generation, free) with
        /// "Prompt is too long (invalid_parameter)". Note this differs from the
        /// official api.x.ai limit of 4096 — that does not apply here.
        public const int MaxPromptChars = 8192;
        private static readonly TimeSpan FirstImageEventTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ImageEventInactivityTimeout = TimeSpan.FromSeconds(60);

        private readonly HttpClient _http;
        private readonly string _cookieHeader;
        private readonly GrokWebBrowserClient? _appChatBrowser;

        public GrokWebClient(string cookieHeader, GrokWebBrowserClient? appChatBrowser = null)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                throw new ArgumentException("Cookie header is empty.", nameof(cookieHeader));
            }

            _cookieHeader = cookieHeader.Trim();
            _appChatBrowser = appChatBrowser;
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

        public static GrokWebClient FromCookieFile(
            string cookieFilePath,
            GrokWebBrowserClient? appChatBrowser = null)
            => new(GrokWebCookieLoader.LoadCookieHeader(cookieFilePath), appChatBrowser);

        public async Task<GrokWebImageGenerationResult> GenerateImageAsync(
            string prompt,
            string aspectRatio,
            bool enablePro,
            bool enableSideBySide,
            TimeSpan timeout,
            string? captureBaseFolder = null,
            string? imageReferenceUrl = null,
            CancellationToken cancellationToken = default)
        {
            // Explicit user-required product behavior (2026-07-30): prompts over
            // the verified transport cap are truncated here, at the send-to-grok
            // stage, instead of letting the WebSocket reject the whole job. This
            // is a declared pre-call input transformation (the UI warns before
            // submit), not a failure fallback. Never splits a surrogate pair.
            if (prompt.Length > MaxPromptChars)
            {
                var originalLength = prompt.Length;
                var cut = MaxPromptChars;
                if (char.IsHighSurrogate(prompt[cut - 1]))
                {
                    cut--;
                }
                prompt = prompt[..cut];
                Logger.Log($"\t   Grok web prompt truncated from {originalLength} to {prompt.Length} chars (transport limit {MaxPromptChars}).");
            }

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
            var startedAtUtc = DateTime.UtcNow;
            object traceRequest = new
            {
                requestId,
                prompt,
                properties = new
                {
                    aspectRatio,
                    enablePro,
                    enableSideBySide,
                    imageReferenceUrl,
                },
                timeoutSeconds = timeout.TotalSeconds,
            };
            var finalImages = new List<byte[]>();
            var previewFrames = new List<byte[]>();
            string? modelName = null;
            string? mode = null;
            var width = 0;
            var height = 0;
            var completedJobs = new HashSet<string>(StringComparer.Ordinal);
            var expectedJobs = enableSideBySide ? 4 : 1;
            var skippedPartialFrames = 0;
            var lastProgressLogAt = 0;
            DateTime? allJobsCompletedUtc = null;
            var postJobGrace = TimeSpan.FromSeconds(45);
            var receivedAnyPayload = false;

            try
            {
                try
                {
                    await ws.ConnectAsync(new Uri(ImagineListenWebSocket), cancellationToken);
                }
                catch (WebSocketException ex) when (LooksLikeCloudflareEdgeFailure(ex.Message))
                {
                    // Cloudflare 521/522/525 mean the edge never completed the
                    // WebSocket upgrade to 101 — usually transient origin/edge
                    // trouble or a datacenter IP getting a bad CF response, not
                    // a cookie/prompt bug in this client.
                    throw new InvalidOperationException(
                        "grok-web could not open wss://grok.com/ws/imagine/listen: "
                        + "Cloudflare returned an edge error instead of HTTP 101 Switching Protocols "
                        + $"({ex.Message}). Retry; if it keeps failing from this host, use grok-api "
                        + "or run grok-web from a residential network.",
                        ex);
                }

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

                // Optional imageReferenceUrl remains for experimentation only.
                // Production edits must use RunImageEditAsync (browser app-chat);
                // putting a URL in properties.image_uri here is accepted by the
                // WS but the consumer transport ignores the source image.
                var properties = new Dictionary<string, object>
                {
                    ["section_count"] = 0,
                    ["is_kids_mode"] = false,
                    ["enable_nsfw"] = true,
                    ["skip_upsampler"] = false,
                    ["enable_side_by_side"] = enableSideBySide,
                    ["is_initial"] = false,
                    ["enable_pro"] = enablePro,
                };
                // The consumer transport has no working prompt-aware auto enum.
                // Omitting the field selects its native default (observed as 2:3).
                if (!string.Equals(aspectRatio, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    properties["aspect_ratio"] = aspectRatio;
                }
                if (!string.IsNullOrWhiteSpace(imageReferenceUrl))
                {
                    properties["image_uri"] = imageReferenceUrl;
                }

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
                            properties,
                        },
                        },
                    },
                };
                traceRequest = new
                {
                    reset = resetPayload,
                    generate = generatePayload,
                    timeoutSeconds = timeout.TotalSeconds,
                };
                capture?.LogOutbound(generatePayload);
                await SendJsonAsync(ws, generatePayload, cancellationToken);

                summary.ExpectedJobs = expectedJobs;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                while (!timeoutCts.IsCancellationRequested)
                {
                    if (ShouldFinishAfterPostJobGrace(finalImages.Count, completedJobs.Count, expectedJobs, allJobsCompletedUtc, postJobGrace))
                    {
                        exitReason = "post_job_grace_elapsed";
                        break;
                    }

                    var waitingForPostJobGrace = allJobsCompletedUtc.HasValue && finalImages.Count < expectedJobs;
                    TimeSpan receiveWindow;
                    if (waitingForPostJobGrace)
                    {
                        var remaining = postJobGrace - (DateTime.UtcNow - allJobsCompletedUtc.GetValueOrDefault());
                        if (remaining <= TimeSpan.Zero)
                        {
                            exitReason = "post_job_grace_elapsed_before_receive";
                            break;
                        }

                        receiveWindow = remaining;
                    }
                    else
                    {
                        receiveWindow = receivedAnyPayload
                            ? ImageEventInactivityTimeout
                            : FirstImageEventTimeout;
                    }

                    string? payload;
                    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                    receiveCts.CancelAfter(receiveWindow);
                    try
                    {
                        payload = await ReceiveTextAsync(ws, receiveCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (timeoutCts.IsCancellationRequested)
                        {
                            exitReason = "timeout";
                            if (finalImages.Count > 0 || previewFrames.Count > 0)
                            {
                                Logger.Log($"\t   Grok web overall timeout reached; returning {finalImages.Count} final and {previewFrames.Count} preview frame(s).");
                                break;
                            }

                            summary.ErrorMessage = $"Grok web image generation timed out after {timeout.TotalMinutes:0.#} minutes with no image frames.";
                            throw new GrokWebException(summary.ErrorMessage);
                        }

                        if (waitingForPostJobGrace)
                        {
                            exitReason = "post_job_grace_receive_timeout";
                            break;
                        }

                        exitReason = receivedAnyPayload ? "event_inactivity_timeout" : "first_event_timeout";
                        if (finalImages.Count > 0 || previewFrames.Count > 0)
                        {
                            Logger.Log($"\t   Grok web stopped sending events for {receiveWindow.TotalSeconds:0}s; returning {finalImages.Count} final and {previewFrames.Count} preview frame(s).");
                            break;
                        }

                        var eventDescription = receivedAnyPayload ? "stopped sending events" : "returned no events";
                        summary.ErrorMessage =
                            $"Grok web {eventDescription} for {receiveWindow.TotalSeconds:0} seconds before returning an image. "
                            + "The provider likely rejected or stalled the request.";
                        throw new GrokWebException(summary.ErrorMessage);
                    }
                    catch (WebSocketException ex) when (finalImages.Count > 0 || previewFrames.Count > 0)
                    {
                        exitReason = "websocket_closed_with_frames";
                        Logger.Log($"\t   Grok web WebSocket closed after receiving frames ({ex.Message}); returning {finalImages.Count} final and {previewFrames.Count} preview frame(s).");
                        break;
                    }

                    if (payload == null)
                    {
                        exitReason = "websocket_closed";
                        break;
                    }

                    receivedAnyPayload = true;
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

                var images = SelectFinalGrokWebImages(finalImages, expectedJobs);

                if (images.Count == 0)
                {
                    var previewHint = skippedPartialFrames > 0
                        ? $" ({skippedPartialFrames} intermediate preview frame(s) rejected as non-final)"
                        : "";
                    summary.ErrorMessage = $"Grok web image generation returned no usable image frames{previewHint}.";
                    throw new GrokWebException(summary.ErrorMessage);
                }

                if (skippedPartialFrames > 0)
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

                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "websocket",
                    "GENERATE",
                    ImagineListenWebSocket,
                    startedAtUtc,
                    request: traceRequest,
                    response: new
                    {
                        exitReason,
                        modelName,
                        mode,
                        width,
                        height,
                        expectedJobs,
                        completedJobCount = completedJobs.Count,
                        completedJobIds = completedJobs,
                        finalFrameCount = finalImages.Count,
                        previewFrameCount = previewFrames.Count,
                        selectedImages = images.Select((bytes, index) => new
                        {
                            index,
                            byteLength = bytes.LongLength,
                            format = GuessImageFormat(bytes),
                        }).ToList(),
                    },
                    metadata: new { requestId, operation = "image-generation" });
                return new GrokWebImageGenerationResult
                {
                    Images = images,
                    ModelName = modelName,
                    Width = width,
                    Height = height,
                    Mode = mode,
                    CaptureDirectory = capture?.SessionDirectory,
                };
            }
            catch (Exception ex)
            {
                summary.ExitReason = exitReason == "unknown" ? "exception" : exitReason;
                summary.ErrorMessage = ex.Message;
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "websocket",
                    "GENERATE",
                    ImagineListenWebSocket,
                    startedAtUtc,
                    request: traceRequest,
                    response: new
                    {
                        exitReason = summary.ExitReason,
                        modelName,
                        mode,
                        width,
                        height,
                        expectedJobs,
                        completedJobCount = completedJobs.Count,
                        completedJobIds = completedJobs,
                        finalFrameCount = finalImages.Count,
                        previewFrameCount = previewFrames.Count,
                    },
                    error: ex,
                    metadata: new { requestId, operation = "image-generation" });
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
            int expectedJobs)
        {
            if (finalImages.Count >= expectedJobs)
            {
                return finalImages.TakeLast(expectedJobs).ToList();
            }

            if (finalImages.Count > 0)
            {
                return finalImages;
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
            var contentType = DetectImageContentType(bytes)
                ?? throw new GrokWebException(
                    $"Grok web upload source is not a supported PNG, JPEG, WEBP, or GIF image: {fileName}");

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "file", fileName);

            const string path = "/http/upload-file-v2/direct";
            var startedAtUtc = DateTime.UtcNow;
            var traceRequest = new
            {
                file = new
                {
                    fileName,
                    contentType,
                    byteLength = bytes.LongLength,
                },
            };
            HttpResponseMessage? response = null;
            string? body = null;
            try
            {
                response = await _http.PostAsync(path, form, cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: traceRequest,
                    response: body,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { operation = "upload-image" });
                response?.Dispose();
                throw;
            }

            using (response)
            {
                var assetId = response.IsSuccessStatusCode
                    ? TryExtractAssetId(body)
                    : null;
                var logicalError = response.IsSuccessStatusCode
                    ? ReadProviderErrorFromBody(body)
                    : null;
                GrokWebException? providerError = null;
                if (!response.IsSuccessStatusCode)
                {
                    providerError = new GrokWebException(
                        $"Grok web upload failed ({(int)response.StatusCode}).",
                        (int)response.StatusCode,
                        body);
                }
                else if (!string.IsNullOrWhiteSpace(logicalError))
                {
                    providerError = new GrokWebException(
                        $"Grok web upload returned an error: {logicalError}",
                        (int)response.StatusCode,
                        body);
                }
                else if (string.IsNullOrWhiteSpace(assetId))
                {
                    providerError = new GrokWebException(
                        "Grok web upload returned HTTP 200 but no asset id for this upload.",
                        (int)response.StatusCode,
                        body);
                }
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: traceRequest,
                    response: body,
                    statusCode: (int)response.StatusCode,
                    error: providerError,
                    metadata: new { operation = "upload-image" });
                if (providerError != null)
                {
                    throw providerError;
                }
                // grok.com's upload response shape drifts; keep the raw body in the
                // log so id-extraction failures are diagnosable after the fact.
                Logger.Log($"\t(grok-web) upload-file-v2 response: {Truncate(body, 800)}");

                var asset = await GetAssetAsync(assetId!, cancellationToken);
                return asset;
            }
        }

        public async Task<GrokWebAsset> CreateImagePostAsync(string mediaUrl, CancellationToken cancellationToken = default)
        {
            var payload = new
            {
                mediaType = "MEDIA_POST_TYPE_IMAGE",
                mediaUrl,
            };
            var call = await PostJsonForTextAsync(
                "/rest/media/post/create",
                payload,
                cancellationToken,
                detectLogicalError: true);
            using var response = call.Response;
            var body = call.Body;
            EnsureSuccess(response, body, detectLogicalError: true);

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
            var payload = new
            {
                mediaType = "MEDIA_POST_TYPE_VIDEO",
                prompt,
            };
            var call = await PostJsonForTextAsync(
                "/rest/media/post/create",
                payload,
                cancellationToken,
                detectLogicalError: true);
            using var response = call.Response;
            var body = call.Body;
            EnsureSuccess(response, body, detectLogicalError: true);

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
            string videoMode = "normal",
            CancellationToken cancellationToken = default)
        {
            videoMode = NormalizeVideoMode(videoMode);
            object payload;
            if (sourceAsset != null)
            {
                var motionPrompt = (prompt ?? string.Empty).Trim();
                var message = motionPrompt.Length == 0
                    ? $"{sourceAsset.MediaUrl}  --mode={videoMode}"
                    : $"{sourceAsset.MediaUrl}  {motionPrompt} --mode={videoMode}";
                payload = new
                {
                    temporary = true,
                    modelName = "imagine-video-gen",
                    message,
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
                                    resolutionName,
                                },
                            },
                        },
                    },
                };
            }
            else
            {
                var videoPrompt = (prompt ?? string.Empty).Trim();
                payload = new
                {
                    modelName = "imagine-video-gen",
                    message = $"{videoPrompt} --mode={videoMode}".Trim(),
                    mediaGenInput = new
                    {
                        textToVideo = new
                        {
                            prompt = videoPrompt,
                            aspectRatio,
                            duration = videoLengthSeconds,
                            resolutionName,
                        },
                    },
                    sendFinalMetadata = true,
                    enableSideBySide = enableSideBySide,
                    responseMetadata = new
                    {
                        experiments = Array.Empty<object>(),
                        modelConfigOverride = new
                        {
                            modelMap = new { },
                        },
                    },
                };
            }

            return await RunAppChatAsync(
                payload,
                parentPostId,
                GrokWebAppChatTrigger.Video,
                cancellationToken);
        }

        // Live browser protocol (verified 2026-07-31): imagine-image-edit via
        // app-chat with mediaGenInput.imageToImage.inputAssets = [assetId].
        // The older properties.image_uri WebSocket path and the legacy
        // imageReferences/parentPostId app-chat shape are both wrong for the
        // current grok.com UI — they either ignore the source or 403.
        public async Task<GrokWebAppChatResult> RunImageEditAsync(
            string prompt,
            GrokWebAsset sourceAsset,
            CancellationToken cancellationToken = default)
        {
            if (_appChatBrowser == null)
            {
                throw new GrokWebException(
                    "Grok web image edit requires the Playwright browser transport "
                    + "(same integrity-signed app-chat path as video). "
                    + "Run with --playwright-install once if Chromium is missing.");
            }
            if (string.IsNullOrWhiteSpace(sourceAsset.AssetId))
            {
                throw new GrokWebException(
                    "Grok web image edit requires a source asset id from upload.");
            }

            var parentPostId = sourceAsset.PostId ?? sourceAsset.AssetId;
            if (string.IsNullOrWhiteSpace(parentPostId))
            {
                throw new GrokWebException(
                    "Grok web image edit requires a source post id.");
            }

            var message = prompt ?? string.Empty;
            var payload = new
            {
                modelName = "imagine-image-edit",
                message,
                enableImageStreaming = true,
                sendFinalMetadata = true,
                responseMetadata = new
                {
                    modelConfigOverride = new
                    {
                        modelMap = new
                        {
                            imageEditModel = "imagine",
                        },
                    },
                },
                mediaGenInput = new
                {
                    imageToImage = new
                    {
                        prompt = message,
                        inputAssets = new[] { sourceAsset.AssetId },
                    },
                },
                kind = "CONVERSATION_KIND_IMAGINE",
            };

            return await RunAppChatAsync(
                payload,
                parentPostId,
                GrokWebAppChatTrigger.ImageEdit,
                cancellationToken);
        }

        public static string NormalizeVideoMode(string? mode)
        {
            var normalized = (mode ?? "").Trim().ToLowerInvariant() switch
            {
                "normal" => "normal",
                "fun" or "funny" => "fun",
                "custom" => "custom",
                "spicy" or "extremely-spicy-or-crazy" => "extremely-spicy-or-crazy",
                _ => "",
            };
            if (normalized.Length == 0)
            {
                throw new ArgumentException(
                    "Video mode must be normal, fun, custom, or spicy.",
                    nameof(mode));
            }
            return normalized;
        }

        public async Task<GrokWebVideoPollResult> PollForVideoResultAsync(
            string postId,
            TimeSpan pollInterval,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await FindVideoResultInRecentPostsAsync(
                    postId,
                    cancellationToken);
                if (result.IsTerminal)
                {
                    return result;
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            return new GrokWebVideoPollResult
            {
                ErrorMessage =
                    $"Grok accepted the video request but did not publish a video for source post {postId} "
                    + $"within {timeout.TotalSeconds:0} seconds. The provider likely moderated, rejected, "
                    + "or stalled the image-to-video job without returning a terminal error.",
            };
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

        public async Task<byte[]> DownloadBytesAsync(
            string url,
            CancellationToken cancellationToken = default,
            bool expectVideo = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/imagine");
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            byte[]? bytes = null;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
                bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    request: new { mediaUrl = url, expectVideo },
                    response: bytes == null ? null : BinaryMetadata(response, bytes),
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { operation = "download-media" });
                response?.Dispose();
                throw;
            }

            using (response)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType;
                var errorBody = response.IsSuccessStatusCode || !CanTraceAsText(contentType)
                    ? null
                    : Encoding.UTF8.GetString(bytes);
                GrokWebException? providerError = null;
                if (!response.IsSuccessStatusCode)
                {
                    providerError = new GrokWebException(
                        $"Grok web download failed ({(int)response.StatusCode}) for {url}.",
                        (int)response.StatusCode,
                        errorBody ?? "[binary response]");
                }
                else if (expectVideo && !IsMp4(bytes))
                {
                    var bodyHint = CanTraceAsText(contentType)
                        ? Truncate(Encoding.UTF8.GetString(bytes), 500)
                        : "[non-MP4 binary response]";
                    providerError = new GrokWebException(
                        $"Grok web video download returned HTTP 200 but was not an MP4 "
                        + $"(content-type {contentType ?? "missing"}, {bytes.Length} bytes).",
                        (int)response.StatusCode,
                        bodyHint);
                }
                else if (!expectVideo && DetectImageContentType(bytes) == null)
                {
                    var bodyHint = CanTraceAsText(contentType)
                        ? Truncate(Encoding.UTF8.GetString(bytes), 500)
                        : "[non-image binary response]";
                    providerError = new GrokWebException(
                        $"Grok web image download returned HTTP 200 but was not PNG/JPEG/WEBP/GIF "
                        + $"(content-type {contentType ?? "missing"}, {bytes.Length} bytes).",
                        (int)response.StatusCode,
                        bodyHint);
                }
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "GET",
                    url,
                    startedAtUtc,
                    request: new { mediaUrl = url, expectVideo },
                    response: new
                    {
                        binary = BinaryMetadata(response, bytes),
                        errorBody,
                    },
                    statusCode: (int)response.StatusCode,
                    error: providerError,
                    metadata: new { operation = "download-media" });
                if (providerError != null)
                {
                    throw providerError;
                }

                return bytes;
            }
        }

        public void Dispose() => _http.Dispose();

        private async Task<GrokWebAsset> GetAssetAsync(string assetId, CancellationToken cancellationToken)
        {
            var path = $"/rest/assets/{assetId}";
            var call = await GetTextWithTraceAsync(
                path,
                new { assetId },
                cancellationToken,
                detectLogicalError: true);
            using var response = call.Response;
            var body = call.Body;
            EnsureSuccess(response, body, detectLogicalError: true);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var key = root.TryGetProperty("key", out var keyEl)
                && keyEl.ValueKind == JsonValueKind.String
                    ? keyEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new GrokWebException(
                    $"Grok web asset lookup returned no media key for asset {assetId}.",
                    (int)response.StatusCode,
                    body);
            }
            var mediaUrl = $"https://assets.grok.com/{key.TrimStart('/')}";

            return new GrokWebAsset
            {
                AssetId = assetId,
                MediaUrl = mediaUrl,
            };
        }

        private async Task<GrokWebAppChatResult> RunAppChatAsync(
            object payload,
            string? triggerPostId,
            GrokWebAppChatTrigger trigger,
            CancellationToken cancellationToken)
        {
            if (_appChatBrowser != null)
            {
                return await RunAppChatInBrowserAsync(payload, triggerPostId, trigger, cancellationToken);
            }
            if (trigger != GrokWebAppChatTrigger.None)
            {
                throw new GrokWebException(
                    "Grok web app-chat requires the Playwright browser transport for "
                    + $"{trigger} (integrity-signed request). Standalone HTTP returns 403.");
            }

            const string path = "/rest/app-chat/conversations/new";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/rest/app-chat/conversations/new")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            // /rest/app-chat is behind stricter anti-bot rules than the other
            // REST endpoints (upload, media/post) — without the browser's
            // fetch-metadata headers it 403s with "Request rejected by
            // anti-bot rules" (observed 2026-07-12).
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"136\", \"Google Chrome\";v=\"136\", \"Not.A/Brand\";v=\"99\"");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            try
            {
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http-stream",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: payload,
                    error: ex,
                    metadata: new { operation = "video-generation" });
                throw;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string? err = null;
                    try
                    {
                        err = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        GenerationTrace.RecordProviderCall(
                            "grok-web",
                            "http-stream",
                            "POST",
                            AbsoluteEndpoint(path),
                            startedAtUtc,
                            request: payload,
                            statusCode: (int)response.StatusCode,
                            error: ex,
                            metadata: new { operation = "video-generation" });
                        throw;
                    }

                    var error = new GrokWebException(
                        $"Grok web app-chat failed ({(int)response.StatusCode}).",
                        (int)response.StatusCode,
                        err);
                    GenerationTrace.RecordProviderCall(
                        "grok-web",
                        "http-stream",
                        "POST",
                        AbsoluteEndpoint(path),
                        startedAtUtc,
                        request: payload,
                        response: err,
                        statusCode: (int)response.StatusCode,
                        error: error,
                        metadata: new { operation = "video-generation" });
                    throw error;
                }

                var imageUrls = new HashSet<string>(StringComparer.Ordinal);
                var videoUrls = new HashSet<string>(StringComparer.Ordinal);
                string? modelMessage = null;
                string? errorMessage = null;
                string? traceId = null;

                try
                {
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

                        CollectMediaUrlsFromJsonLine(
                            line,
                            imageUrls,
                            videoUrls,
                            ref modelMessage,
                            ref errorMessage,
                            ref traceId);
                    }
                }
                catch (Exception ex)
                {
                    GenerationTrace.RecordProviderCall(
                        "grok-web",
                        "http-stream",
                        "POST",
                        AbsoluteEndpoint(path),
                        startedAtUtc,
                        request: payload,
                        response: new
                        {
                            generatedImageUrls = imageUrls,
                            generatedVideoUrls = videoUrls,
                            modelMessage,
                            errorMessage,
                            requestTraceId = traceId,
                        },
                        statusCode: (int)response.StatusCode,
                        error: ex,
                        metadata: new { operation = "video-generation" });
                    throw;
                }

                var result = new GrokWebAppChatResult
                {
                    GeneratedImageUrls = imageUrls.ToList(),
                    GeneratedVideoUrls = videoUrls.ToList(),
                    ModelMessage = modelMessage,
                    ErrorMessage = errorMessage,
                    RequestTraceId = traceId,
                };
                var logicalError = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? null
                    : new GrokWebException($"Grok web video generation failed: {result.ErrorMessage}");
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http-stream",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: payload,
                    response: new
                    {
                        generatedImageUrls = result.GeneratedImageUrls,
                        generatedVideoUrls = result.GeneratedVideoUrls,
                        result.ModelMessage,
                        result.ErrorMessage,
                        result.RequestTraceId,
                    },
                    statusCode: (int)response.StatusCode,
                    error: logicalError,
                    metadata: new { operation = "video-generation" });
                return result;
            }
        }

        private async Task<GrokWebAppChatResult> RunAppChatInBrowserAsync(
            object payload,
            string? triggerPostId,
            GrokWebAppChatTrigger trigger,
            CancellationToken cancellationToken)
        {
            const string path = "/rest/app-chat/conversations/new";
            var startedAtUtc = DateTime.UtcNow;
            var operation = trigger == GrokWebAppChatTrigger.ImageEdit
                ? "image-edit"
                : "video-generation";
            GrokWebBrowserResponse? response = null;
            try
            {
                response = await _appChatBrowser!.PostAppChatAsync(
                    payload,
                    triggerPostId,
                    trigger,
                    cancellationToken);
                if (response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    throw new GrokWebException(
                        $"Grok web browser app-chat failed ({response.StatusCode}).",
                        response.StatusCode,
                        response.Body);
                }

                var result = ParseAppChatBody(response.Body);
                var logicalError = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? null
                    : new GrokWebException($"Grok web {operation} failed: {result.ErrorMessage}");
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "playwright-fetch",
                    "POST",
                    response.Url,
                    startedAtUtc,
                    request: payload,
                    response: new
                    {
                        generatedImageUrls = result.GeneratedImageUrls,
                        generatedVideoUrls = result.GeneratedVideoUrls,
                        result.ModelMessage,
                        result.ErrorMessage,
                        result.RequestTraceId,
                    },
                    statusCode: response.StatusCode,
                    error: logicalError,
                    metadata: new { operation });
                return result;
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "playwright-fetch",
                    "POST",
                    response?.Url ?? AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: payload,
                    response: response == null ? null : new
                    {
                        response.StatusCode,
                        responseBody = Truncate(response.Body, 2000),
                    },
                    statusCode: response?.StatusCode,
                    error: ex,
                    metadata: new { operation });
                throw;
            }
        }

        private static GrokWebAppChatResult ParseAppChatBody(string body)
        {
            var imageUrls = new HashSet<string>(StringComparer.Ordinal);
            var videoUrls = new HashSet<string>(StringComparer.Ordinal);
            string? modelMessage = null;
            string? errorMessage = null;
            string? traceId = null;
            using var reader = new StringReader(body ?? string.Empty);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    CollectMediaUrlsFromJsonLine(
                        line,
                        imageUrls,
                        videoUrls,
                        ref modelMessage,
                        ref errorMessage,
                        ref traceId);
                }
            }

            return new GrokWebAppChatResult
            {
                GeneratedImageUrls = imageUrls.ToList(),
                GeneratedVideoUrls = videoUrls.ToList(),
                ModelMessage = modelMessage,
                ErrorMessage = errorMessage,
                RequestTraceId = traceId,
            };
        }

        private async Task<GrokWebVideoPollResult> FindVideoResultInRecentPostsAsync(
            string postId,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                limit = 40,
                filter = new { source = "MEDIA_POST_SOURCE_LIKED", safeForWork = false },
            };
            var call = await PostJsonForTextAsync("/rest/media/post/list", payload, cancellationToken);
            using var response = call.Response;
            var body = call.Body;
            EnsureSuccess(response, body);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array)
            {
                return new GrokWebVideoPollResult();
            }

            foreach (var post in posts.EnumerateArray())
            {
                var result = FindVideoResultInPost(post, postId);
                if (result.IsTerminal)
                {
                    return result;
                }
            }

            return new GrokWebVideoPollResult();
        }

        private async Task<List<string>> FindImageUrlsInRecentPostsAsync(string parentPostId, CancellationToken cancellationToken)
        {
            var payload = new
            {
                limit = 40,
                filter = new { source = "MEDIA_POST_SOURCE_LIKED", safeForWork = false },
            };
            var call = await PostJsonForTextAsync("/rest/media/post/list", payload, cancellationToken);
            using var response = call.Response;
            var body = call.Body;
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

        private static GrokWebVideoPollResult FindVideoResultInPost(
            JsonElement post,
            string postId)
        {
            var id = post.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var originalPostId = post.TryGetProperty("originalPostId", out var originalEl)
                ? originalEl.GetString()
                : null;
            var matches = string.Equals(id, postId, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(originalPostId, postId, StringComparison.OrdinalIgnoreCase);

            if (!matches)
            {
                foreach (var child in EnumerateChildPosts(post))
                {
                    var childResult = FindVideoResultInPost(child, postId);
                    if (childResult.IsTerminal)
                    {
                        return childResult;
                    }
                }
                return new GrokWebVideoPollResult();
            }

            var error = ReadProviderError(post);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return new GrokWebVideoPollResult { ErrorMessage = error };
            }

            foreach (var candidate in EnumerateMediaUrls(post))
            {
                if (candidate.Contains("generated_video.mp4", StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    return new GrokWebVideoPollResult { VideoUrl = candidate };
                }
            }

            foreach (var child in EnumerateChildPosts(post))
            {
                foreach (var candidate in EnumerateMediaUrls(child))
                {
                    if (candidate.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        return new GrokWebVideoPollResult { VideoUrl = candidate };
                    }
                }
            }

            return new GrokWebVideoPollResult();
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
            ref string? errorMessage,
            ref string? traceId)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                errorMessage ??= ReadProviderError(root);
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("response", out var response)
                    && response.ValueKind == JsonValueKind.Object)
                {
                    if (response.TryGetProperty("streamingVideoGenerationResponse", out var streamingVideo)
                        && streamingVideo.ValueKind == JsonValueKind.Object
                        && streamingVideo.TryGetProperty("videoUrl", out var videoUrlEl))
                    {
                        var videoUrl = NormalizeAssetUrl(videoUrlEl.GetString());
                        if (!string.IsNullOrWhiteSpace(videoUrl))
                        {
                            videoUrls.Add(videoUrl);
                        }
                    }

                    // Image edit streams relative paths under
                    // streamingImageGenerationResponse.imageUrl. Only accept
                    // progress=100 finals that are not moderated and not
                    // intermediate -part- previews.
                    if (response.TryGetProperty("streamingImageGenerationResponse", out var streamingImage)
                        && streamingImage.ValueKind == JsonValueKind.Object)
                    {
                        var moderated = streamingImage.TryGetProperty("moderated", out var modEl)
                            && modEl.ValueKind == JsonValueKind.True;
                        var progress = streamingImage.TryGetProperty("progress", out var progEl)
                            && progEl.TryGetInt32(out var prog)
                                ? prog
                                : -1;
                        if (!moderated
                            && progress >= 100
                            && streamingImage.TryGetProperty("imageUrl", out var imageUrlEl))
                        {
                            var imageUrl = NormalizeAssetUrl(imageUrlEl.GetString());
                            if (!string.IsNullOrWhiteSpace(imageUrl)
                                && !imageUrl.Contains("-part-", StringComparison.OrdinalIgnoreCase))
                            {
                                imageUrls.Add(imageUrl);
                            }
                        }
                    }

                    if (response.TryGetProperty("modelResponse", out var modelResponse)
                        && modelResponse.ValueKind == JsonValueKind.Object)
                    {
                        errorMessage ??= ReadProviderError(modelResponse);
                        if (modelResponse.TryGetProperty("message", out var msgEl))
                        {
                            modelMessage = msgEl.GetString();
                        }

                        if (modelResponse.TryGetProperty("generatedImageUrls", out var genImages)
                            && genImages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in genImages.EnumerateArray())
                            {
                                var url = NormalizeAssetUrl(item.GetString());
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
                }

                // Absolute asset URLs also appear in request metadata as
                // imageReferences (often users/_/… placeholders for the SOURCE).
                // Never harvest those — only generated outputs under /generated/,
                // plus explicit streamingImageGenerationResponse /
                // generatedImageUrls parsing above.
                foreach (Match match in Regex.Matches(line, @"https://assets\.grok\.com[^""'\s]+", RegexOptions.IgnoreCase))
                {
                    var url = match.Value;
                    if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        videoUrls.Add(url);
                    }
                    else if (url.Contains("/generated/", StringComparison.OrdinalIgnoreCase)
                        && !url.Contains("-part-", StringComparison.OrdinalIgnoreCase)
                        && !url.Contains("/users/_/", StringComparison.OrdinalIgnoreCase))
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

        private static string? ReadProviderError(JsonElement element)
            => ReadProviderError(element, 0);

        private static string? ReadProviderErrorFromBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                return ReadProviderError(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadProviderError(JsonElement element, int depth)
        {
            if (depth > 12)
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nestedError = ReadProviderError(item, depth + 1);
                    if (!string.IsNullOrWhiteSpace(nestedError))
                    {
                        return nestedError;
                    }
                }
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name.ToLowerInvariant();
                var value = property.Value;
                if (name is "error" or "errormessage" or "error_message"
                    or "errors" or "streamerrors" or "stream_errors"
                    or "failure" or "failuremessage" or "failure_message"
                    or "failurereason" or "failure_reason"
                    or "blockreason" or "block_reason"
                    or "moderationreason" or "moderation_reason"
                    or "statusreason" or "status_reason")
                {
                    var message = ReadErrorValue(value);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        return $"Grok returned terminal error flag '{property.Name}'.";
                    }
                }
                if ((name is "moderated" or "ismoderated"
                    or "blocked" or "isblocked"
                    or "rejected" or "isrejected")
                    && value.ValueKind is JsonValueKind.True)
                {
                    return $"Grok marked the video job as {name}.";
                }
                if ((name is "status" or "state"
                    or "moderationstatus" or "moderation_status")
                    && value.ValueKind == JsonValueKind.String)
                {
                    var status = value.GetString()?.Trim();
                    if (status is not null
                        && status.ToLowerInvariant() is (
                            "failed" or "error" or "rejected" or "blocked"
                            or "moderated" or "cancelled" or "canceled"))
                    {
                        return $"Grok video job entered terminal state '{status}'.";
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nestedError = ReadProviderError(property.Value, depth + 1);
                if (!string.IsNullOrWhiteSpace(nestedError))
                {
                    return nestedError;
                }
            }

            return null;
        }

        private static string? ReadErrorValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    var message = ReadErrorValue(item);
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "detail", "reason", "code" })
                {
                    if (value.TryGetProperty(name, out var nested))
                    {
                        var message = ReadErrorValue(nested);
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            return message;
                        }
                    }
                }
            }
            return null;
        }

        private static string? NormalizeAssetUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out _)
                ? url
                : $"https://assets.grok.com/{url.TrimStart('/')}";
        }

        private async Task<(HttpResponseMessage Response, string Body)> PostJsonForTextAsync(
            string path,
            object payload,
            CancellationToken cancellationToken,
            bool detectLogicalError = false)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? body = null;
            try
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                response = await _http.PostAsync(path, content, cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                GrokWebException? error = null;
                if (!response.IsSuccessStatusCode)
                {
                    error = new GrokWebException(
                        $"Grok web request failed ({(int)response.StatusCode}).",
                        (int)response.StatusCode,
                        body);
                }
                else if (detectLogicalError
                    && ReadProviderErrorFromBody(body) is { } logicalError)
                {
                    error = new GrokWebException(
                        $"Grok web request returned an error: {logicalError}",
                        (int)response.StatusCode,
                        body);
                }
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: payload,
                    response: body,
                    statusCode: (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = path });
                return (response, body);
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "POST",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: payload,
                    response: body,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { operation = path });
                response?.Dispose();
                throw;
            }
        }

        private async Task<(HttpResponseMessage Response, string Body)> GetTextWithTraceAsync(
            string path,
            object request,
            CancellationToken cancellationToken,
            bool detectLogicalError = false)
        {
            var startedAtUtc = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            string? body = null;
            try
            {
                response = await _http.GetAsync(path, cancellationToken);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                GrokWebException? error = null;
                if (!response.IsSuccessStatusCode)
                {
                    error = new GrokWebException(
                        $"Grok web request failed ({(int)response.StatusCode}).",
                        (int)response.StatusCode,
                        body);
                }
                else if (detectLogicalError
                    && ReadProviderErrorFromBody(body) is { } logicalError)
                {
                    error = new GrokWebException(
                        $"Grok web request returned an error: {logicalError}",
                        (int)response.StatusCode,
                        body);
                }
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "GET",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: request,
                    response: body,
                    statusCode: (int)response.StatusCode,
                    error: error,
                    metadata: new { operation = path.Split('?', 2)[0] });
                return (response, body);
            }
            catch (Exception ex)
            {
                GenerationTrace.RecordProviderCall(
                    "grok-web",
                    "http",
                    "GET",
                    AbsoluteEndpoint(path),
                    startedAtUtc,
                    request: request,
                    response: body,
                    statusCode: response == null ? null : (int)response.StatusCode,
                    error: ex,
                    metadata: new { operation = path.Split('?', 2)[0] });
                response?.Dispose();
                throw;
            }
        }

        private static string AbsoluteEndpoint(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                && absolute.Scheme is "http" or "https")
            {
                return absolute.ToString();
            }
            return new Uri(new Uri(Origin), path).ToString();
        }

        private static void EnsureSuccess(
            HttpResponseMessage response,
            string body,
            bool detectLogicalError = false)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new GrokWebException(
                    $"Grok web request failed ({(int)response.StatusCode}).",
                    (int)response.StatusCode,
                    body);
            }

            if (detectLogicalError
                && ReadProviderErrorFromBody(body) is { } logicalError)
            {
                throw new GrokWebException(
                    $"Grok web request returned an error: {logicalError}",
                    (int)response.StatusCode,
                    body);
            }
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

                // Current (2026-07) upload-file-v2 shape nests the asset id:
                // { "uploadId": ..., "fileMetadata": { "fileMetadataId": ... } }.
                // uploadId is NOT an asset id — /rest/assets/{uploadId} 404s —
                // so the nested lookup must run before top-level alternatives.
                if (doc.RootElement.TryGetProperty("fileMetadata", out var meta)
                    && meta.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "fileMetadataId", "id" })
                    {
                        if (meta.TryGetProperty(name, out var nested))
                        {
                            var value = nested.ValueKind == JsonValueKind.String
                                ? nested.GetString()
                                : null;
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                return value;
                            }
                        }
                    }
                }

                foreach (var name in new[] { "assetId", "fileMetadataId", "id", "fileId" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var el))
                    {
                        var value = el.ValueKind == JsonValueKind.String
                            ? el.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            return null;
        }

        private static string? DetectImageContentType(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == (byte)'P'
                && bytes[2] == (byte)'N' && bytes[3] == (byte)'G'
                && bytes[4] == 0x0D && bytes[5] == 0x0A
                && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                return "image/png";
            }
            if (bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "image/webp";
            }
            if (bytes.Length >= 6
                && bytes[0] == (byte)'G' && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F' && bytes[3] == (byte)'8'
                && bytes[4] is (byte)'7' or (byte)'9'
                && bytes[5] == (byte)'a')
            {
                return "image/gif";
            }
            return null;
        }

        private static bool IsMp4(byte[] bytes)
            => bytes.Length >= 12
               && bytes[4] == (byte)'f' && bytes[5] == (byte)'t'
               && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';

        private static object BinaryMetadata(HttpResponseMessage? response, byte[] bytes)
        {
            return new
            {
                contentType = response?.Content.Headers.ContentType?.MediaType,
                byteLength = bytes.LongLength,
                format = GuessMediaFormat(bytes),
            };
        }

        private static bool CanTraceAsText(string? mediaType)
            => string.IsNullOrWhiteSpace(mediaType)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

        private static string GuessMediaFormat(byte[] bytes)
        {
            var imageFormat = GuessImageFormat(bytes);
            if (imageFormat != "bin")
            {
                return imageFormat;
            }
            if (bytes.Length >= 12
                && bytes[4] == (byte)'f' && bytes[5] == (byte)'t'
                && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
            {
                return "mp4";
            }
            return "unknown";
        }

        private static bool LooksLikeCloudflareEdgeFailure(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            // ClientWebSocket: "The server returned status code '521' when status code '101' was expected."
            foreach (var code in new[] { "521", "522", "523", "524", "525", "526", "530" })
            {
                if (message.Contains($"'{code}'", StringComparison.Ordinal)
                    || message.Contains($"\"{code}\"", StringComparison.Ordinal)
                    || message.Contains($" {code} ", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
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
