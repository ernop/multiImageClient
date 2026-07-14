#nullable enable
using Microsoft.Playwright;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// Opt-in diagnostics for meta-web browser sessions, the sibling of
    /// GrokWebSessionCapture: one directory per generation attempt under
    /// saves/<day>/meta-web-capture/ holding request.json, events.jsonl, and
    /// failure screenshots. Never writes cookies, tokens, headers, or profile
    /// contents — only page URLs, media URLs, timings, and state transitions.
    public sealed class MetaWebSessionCapture : IDisposable
    {
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly StreamWriter _eventLog;
        private readonly object _writeLock = new();
        private bool _completed;

        public string SessionDirectory { get; }

        private MetaWebSessionCapture(string sessionDirectory)
        {
            SessionDirectory = sessionDirectory;
            _eventLog = new StreamWriter(
                Path.Combine(sessionDirectory, "events.jsonl"),
                append: false,
                Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }

        public static MetaWebSessionCapture? Start(string imageDownloadBaseFolder, string prompt, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(imageDownloadBaseFolder))
            {
                return null;
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd-dddd");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var shortId = Guid.NewGuid().ToString("N")[..8];
            var sessionDir = Path.Combine(
                imageDownloadBaseFolder,
                today,
                "meta-web-capture",
                $"{stamp}_{shortId}");

            Directory.CreateDirectory(sessionDir);

            var session = new MetaWebSessionCapture(sessionDir);
            File.WriteAllText(
                Path.Combine(sessionDir, "request.json"),
                JsonSerializer.Serialize(new
                {
                    capturedAtUtc = session._startedUtc,
                    timeoutSeconds = timeout.TotalSeconds,
                    promptLength = prompt.Length,
                    prompt,
                }, JsonOptions));

            session.Event("start", new { sessionDirectory = sessionDir });
            return session;
        }

        public void Event(string name, object? details = null)
        {
            var fields = new Dictionary<string, object?>
            {
                ["event"] = name,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["elapsedMs"] = (DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                ["details"] = details,
            };

            var line = JsonSerializer.Serialize(fields, JsonLineOptions);
            lock (_writeLock)
            {
                _eventLog.WriteLine(line);
            }
        }

        public async Task ScreenshotAsync(IPage page, string label)
        {
            var file = Path.Combine(SessionDirectory, $"screenshot-{label}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });
            Event("screenshot", new { file, pageUrl = page.Url });
        }

        public void Complete(string exitReason, int imageCount)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            Event("complete", new { exitReason, imageCount });
            Logger.Log($"\t   Meta web capture saved: {SessionDirectory}");
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Complete("disposed_before_complete", 0);
            }

            _eventLog.Dispose();
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
}
