using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiImageClient
{
    public sealed class BufferedLogLine
    {
        public long Sequence { get; init; }
        public required string Line { get; init; }
    }

    /// Writes every line to both the console and a configured log file.
    ///
    /// Call <see cref="Initialize"/> once at startup with
    /// <c>Settings.LogFilePath</c> before any <see cref="Log"/> call. The
    /// stream auto-flushes so crashes (e.g. cancelled HTTP requests,
    /// Ctrl+C) still leave a readable trail on disk. A
    /// <c>ProcessExit</c> hook also flushes/closes defensively.
    public static class Logger
    {
        private const int MaxBufferedLines = 2000;
        private static readonly object _lock = new object();
        private static readonly List<BufferedLogLine> _recentLines = new List<BufferedLogLine>();
        private static StreamWriter _logWriter;
        private static string _logFilePath;
        private static bool _warnedAboutUninitialized;
        private static long _nextSequence;

        public static void Initialize(string logFilePath)
        {
            if (string.IsNullOrWhiteSpace(logFilePath))
            {
                throw new ArgumentException("logFilePath must be non-empty", nameof(logFilePath));
            }

            lock (_lock)
            {
                _logWriter?.Dispose();

                var dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                _logFilePath = logFilePath;
                _logWriter = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
                var line = $"{Timestamp()} --- Logger initialized, logging to {logFilePath}";
                _logWriter.WriteLine(line);
                BufferLine(line);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                lock (_lock)
                {
                    _logWriter?.Flush();
                    _logWriter?.Dispose();
                    _logWriter = null;
                }
            };
        }

        public static void Log(string message)
        {
            var line = $"{Timestamp()} {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                if (_logWriter == null)
                {
                    if (!_warnedAboutUninitialized)
                    {
                        _warnedAboutUninitialized = true;
                        Console.Error.WriteLine(
                            "[Logger] Log() called before Initialize(); file logging disabled for this line and any prior lines.");
                    }
                    return;
                }
                _logWriter.WriteLine(line);
                BufferLine(line);
            }
        }

        public static List<BufferedLogLine> ReadBuffered(long afterSequence)
        {
            lock (_lock)
            {
                return _recentLines
                    .Where(line => line.Sequence > afterSequence)
                    .ToList();
            }
        }

        private static void BufferLine(string line)
        {
            _recentLines.Add(new BufferedLogLine
            {
                Sequence = ++_nextSequence,
                Line = line,
            });
            if (_recentLines.Count > MaxBufferedLines)
            {
                _recentLines.RemoveRange(0, _recentLines.Count - MaxBufferedLines);
            }
        }

        private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}
