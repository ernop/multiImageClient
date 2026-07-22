#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public sealed class GrokWebSessionCapture : IAsyncDisposable
    {
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly StreamWriter _eventLog;
        private readonly object _writeLock = new();
        private int _messageSeq;
        private int _frameSeq;
        private int _outboundSeq;
        private bool _completed;

        public string SessionDirectory { get; }
        public string RequestId { get; }

        private GrokWebSessionCapture(string sessionDirectory, string requestId)
        {
            SessionDirectory = sessionDirectory;
            RequestId = requestId;
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "payloads"));
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "frames"));

            _eventLog = new StreamWriter(
                Path.Combine(sessionDirectory, "events.jsonl"),
                append: false,
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }

        public static GrokWebSessionCapture Start(
            string imageDownloadBaseFolder,
            string requestId,
            string prompt,
            string aspectRatio,
            bool enablePro,
            bool enableSideBySide,
            TimeSpan timeout)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var shortId = requestId.Length > 8 ? requestId[..8] : requestId;
            var sessionDir = Path.Combine(
                imageDownloadBaseFolder,
                today,
                "grok-web-capture",
                $"{stamp}_{shortId}");

            Directory.CreateDirectory(sessionDir);

            var session = new GrokWebSessionCapture(sessionDir, requestId);
            var requestDoc = new
            {
                capturedAtUtc = session._startedUtc,
                requestId,
                aspectRatio,
                enablePro,
                enableSideBySide,
                timeoutSeconds = timeout.TotalSeconds,
                promptLength = prompt.Length,
                prompt,
                websocket = GrokWebClient.ImagineListenWebSocket,
            };
            File.WriteAllText(
                Path.Combine(sessionDir, "request.json"),
                JsonSerializer.Serialize(requestDoc, JsonOptions));

            session.WriteEvent(new Dictionary<string, object?>
            {
                ["direction"] = "session",
                ["event"] = "start",
                ["sessionDirectory"] = sessionDir,
            });

            return session;
        }

        public void LogOutbound(object payload)
        {
            var seq = Interlocked.Increment(ref _outboundSeq);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var fileName = $"out-{seq:D5}.json";
            var path = Path.Combine(SessionDirectory, "payloads", fileName);
            File.WriteAllText(path, json, Encoding.UTF8);

            WriteEvent(new Dictionary<string, object?>
            {
                ["direction"] = "outbound",
                ["payloadFile"] = Path.Combine("payloads", fileName),
                ["byteLength"] = Encoding.UTF8.GetByteCount(json),
            });
        }

        public void LogInbound(string rawPayload)
        {
            var seq = Interlocked.Increment(ref _messageSeq);
            var payloadFile = $"in-{seq:D5}.json";
            var payloadPath = Path.Combine(SessionDirectory, "payloads", payloadFile);
            File.WriteAllText(payloadPath, rawPayload, Encoding.UTF8);

            var evt = new Dictionary<string, object?>
            {
                ["direction"] = "inbound",
                ["payloadFile"] = Path.Combine("payloads", payloadFile),
                ["byteLength"] = Encoding.UTF8.GetByteCount(rawPayload),
            };

            try
            {
                using var doc = JsonDocument.Parse(rawPayload);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                evt["wsType"] = type;

                if (type == "json")
                {
                    if (root.TryGetProperty("current_status", out var statusEl))
                    {
                        evt["currentStatus"] = statusEl.GetString();
                    }
                    if (root.TryGetProperty("job_id", out var jobEl))
                    {
                        evt["jobId"] = jobEl.GetString();
                    }
                    if (root.TryGetProperty("order", out var orderEl) && orderEl.TryGetInt32(out var order))
                    {
                        evt["order"] = order;
                    }
                    if (root.TryGetProperty("moderated", out var modEl))
                    {
                        evt["moderated"] = modEl.ValueKind == JsonValueKind.True;
                    }
                    if (root.TryGetProperty("model_name", out var modelEl))
                    {
                        evt["modelName"] = modelEl.GetString();
                    }
                    if (root.TryGetProperty("mode", out var modeEl))
                    {
                        evt["mode"] = modeEl.GetString();
                    }
                    if (root.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var w))
                    {
                        evt["width"] = w;
                    }
                    if (root.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var h))
                    {
                        evt["height"] = h;
                    }
                    if (root.TryGetProperty("percentage_complete", out var pctEl)
                        && pctEl.TryGetDouble(out var pct))
                    {
                        evt["percentageComplete"] = pct;
                    }
                }
                else if (type == "image" && root.TryGetProperty("blob", out var blobEl))
                {
                    var blob = blobEl.GetString();
                    if (!string.IsNullOrEmpty(blob))
                    {
                        var bytes = Convert.FromBase64String(blob);
                        var isFinal = GrokWebClient.ClassifyGrokWebImageFrame(bytes, blob, out var magic, out var format);
                        var frameSeq = Interlocked.Increment(ref _frameSeq);
                        var ext = format switch
                        {
                            "png" => "png",
                            "jpeg" => "jpg",
                            "webp" => "webp",
                            "gif" => "gif",
                            _ => "bin",
                        };
                        var kind = isFinal ? "final" : "preview";
                        var frameFile = $"frame-{frameSeq:D5}-{kind}.{ext}";
                        var framePath = Path.Combine(SessionDirectory, "frames", frameFile);
                        File.WriteAllBytes(framePath, bytes);

                        evt["frameFile"] = Path.Combine("frames", frameFile);
                        evt["frameKind"] = kind;
                        evt["frameFormat"] = format;
                        evt["frameMagic"] = magic;
                        evt["frameBytes"] = bytes.Length;
                        evt["base64Prefix"] = blob.Length <= 24 ? blob : blob[..24];
                    }
                }
            }
            catch (Exception ex)
            {
                evt["parseError"] = ex.Message;
            }

            WriteEvent(evt);
        }

        public async Task CompleteAsync(GrokWebSessionSummary summary)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            summary.StartedUtc = _startedUtc;
            summary.FinishedUtc = DateTime.UtcNow;
            summary.RequestId = RequestId;
            summary.SessionDirectory = SessionDirectory;
            summary.InboundMessageCount = _messageSeq;
            summary.OutboundMessageCount = _outboundSeq;
            summary.FrameCount = _frameSeq;

            WriteEvent(new Dictionary<string, object?>
            {
                ["direction"] = "session",
                ["event"] = "complete",
                ["exitReason"] = summary.ExitReason,
                ["finalFrameCount"] = summary.FinalFrameCount,
                ["previewFrameCount"] = summary.PreviewFrameCount,
                ["completedJobCount"] = summary.CompletedJobCount,
            });

            var summaryPath = Path.Combine(SessionDirectory, "summary.json");
            await File.WriteAllTextAsync(
                summaryPath,
                JsonSerializer.Serialize(summary, JsonOptions));

            Logger.Log($"\t   Grok web capture saved: {SessionDirectory}");
        }

        public async ValueTask DisposeAsync()
        {
            _eventLog.Dispose();
            if (!_completed)
            {
                await CompleteAsync(new GrokWebSessionSummary { ExitReason = "disposed_before_complete" });
            }
        }

        private void WriteEvent(Dictionary<string, object?> fields)
        {
            fields["utc"] = DateTime.UtcNow.ToString("o");
            fields["elapsedMs"] = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;

            var line = JsonSerializer.Serialize(fields, JsonLineOptions);
            lock (_writeLock)
            {
                _eventLog.WriteLine(line);
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static readonly JsonSerializerOptions JsonLineOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public sealed class GrokWebSessionSummary
    {
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public string RequestId { get; set; } = "";
        public string SessionDirectory { get; set; } = "";
        public string ExitReason { get; set; } = "";
        public int InboundMessageCount { get; set; }
        public int OutboundMessageCount { get; set; }
        public int FrameCount { get; set; }
        public int FinalFrameCount { get; set; }
        public int PreviewFrameCount { get; set; }
        public int CompletedJobCount { get; set; }
        public int ExpectedJobs { get; set; }
        public int SelectedOutputCount { get; set; }
        public List<string> CompletedJobIds { get; set; } = new();
        public List<string> SelectedFrameFiles { get; set; } = new();
        public string? ModelName { get; set; }
        public string? Mode { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
