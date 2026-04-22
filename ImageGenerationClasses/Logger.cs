using System;
using System.IO;

namespace MultiImageClient
{
    /// Writes every line to both the console and a configured log file.
    ///
    /// Call <see cref="Initialize"/> once at startup with
    /// <c>Settings.LogFilePath</c> before any <see cref="Log"/> call. The
    /// stream auto-flushes so crashes (e.g. cancelled HTTP requests,
    /// Ctrl+C) still leave a readable trail on disk. A
    /// <c>ProcessExit</c> hook also flushes/closes defensively.
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static StreamWriter _logWriter;
        private static string _logFilePath;
        private static bool _warnedAboutUninitialized;

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
                _logWriter.WriteLine($"{Timestamp()} --- Logger initialized, logging to {logFilePath}");
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
            }
        }

        private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}
