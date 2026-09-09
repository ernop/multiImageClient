#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MultiImageClient
{
    // The request gate and sleep decision share a lock. A request cannot start
    // new background work after the monitor commits to shutting down.
    public sealed class UiIdleLifetime
    {
        private readonly object _gate = new();
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _clock;
        private long _lastActivity;
        private int _requests;
        private bool _sleeping;

        public UiIdleLifetime(TimeSpan timeout, TimeProvider? clock = null)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            _timeout = timeout;
            _clock = clock ?? TimeProvider.System;
            _lastActivity = _clock.GetTimestamp();
        }

        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_sleeping) return false;
                _requests++;
                return true;
            }
        }

        public void Exit(bool activity)
        {
            lock (_gate)
            {
                if (_requests <= 0) throw new InvalidOperationException("Unbalanced UI request lifetime.");
                _requests--;
                if (activity) _lastActivity = _clock.GetTimestamp();
            }
        }

        public bool TrySleep(Func<bool> hasWork)
        {
            lock (_gate)
            {
                if (_sleeping || _requests != 0) return false;
                if (hasWork())
                {
                    _lastActivity = _clock.GetTimestamp();
                    return false;
                }
                if (_clock.GetElapsedTime(_lastActivity) < _timeout) return false;
                _sleeping = true;
                return true;
            }
        }

        public async Task MonitorAsync(Func<bool> hasWork, Action stop, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token);
                    if (!TrySleep(hasWork)) continue;
                    Logger.Log("UI idle timeout: no requests or active work; sleeping until the next socket activation.");
                    stop();
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public static bool UseSystemdSocket(int port, bool required)
        {
            var pid = Environment.GetEnvironmentVariable("LISTEN_PID");
            var count = Environment.GetEnvironmentVariable("LISTEN_FDS");
            if (pid == null && count == null && !required) return false;
            if (!OperatingSystem.IsLinux() || pid != Environment.ProcessId.ToString() || count != "1")
                throw new InvalidOperationException("UI socket activation requires exactly one descriptor from systemd for this process.");
            using var socket = new Socket(new SafeSocketHandle((IntPtr)3, ownsHandle: false));
            if (socket.LocalEndPoint is not IPEndPoint endpoint || !endpoint.Address.Equals(IPAddress.Loopback)
                || endpoint.Port != port || socket.SocketType != SocketType.Stream)
                throw new InvalidOperationException("The inherited UI socket must match the configured IPv4 loopback port.");
            // Do not pass activation metadata to child processes.
            Environment.SetEnvironmentVariable("LISTEN_PID", null);
            Environment.SetEnvironmentVariable("LISTEN_FDS", null);
            Environment.SetEnvironmentVariable("LISTEN_FDNAMES", null);
            return true;
        }
    }
}
