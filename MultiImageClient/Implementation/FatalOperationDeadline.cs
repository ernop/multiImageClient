#nullable enable
using System;
using System.Threading;

namespace MultiImageClient
{
    /// Last-resort guard for native/browser calls that may ignore .NET
    /// cancellation. Expiry terminates the process so its service manager can
    /// kill the complete child-process tree and start from a known state.
    internal sealed class FatalOperationDeadline : IDisposable
    {
        private readonly ManualResetEventSlim _completed = new(false);
        private readonly Thread _thread;

        public FatalOperationDeadline(TimeSpan timeout, string operation)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            if (string.IsNullOrWhiteSpace(operation))
            {
                throw new ArgumentException("Operation description is required.", nameof(operation));
            }

            _thread = new Thread(() =>
            {
                if (_completed.Wait(timeout))
                {
                    return;
                }

                var message =
                    $"{operation} exceeded its hard {timeout.TotalSeconds:0}-second deadline; "
                    + "terminating so the service manager can kill the browser process tree and restart.";
                Environment.FailFast(message);
            })
            {
                IsBackground = true,
                Name = "fatal-operation-deadline",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _completed.Set();
            if (Thread.CurrentThread != _thread
                && !_thread.Join(TimeSpan.FromSeconds(2)))
            {
                Logger.Log("Fatal operation deadline thread did not stop within 2 seconds.");
                return;
            }
            _completed.Dispose();
        }
    }
}
