#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MultiImageClient
{
    /// Independent thread that verifies Kestrel can still complete a loopback
    /// request. It deliberately avoids the thread pool: provider starvation or
    /// an abandoned browser operation must not prevent detection. systemd's
    /// Restart=on-failure then supplies the out-of-process recovery.
    internal sealed class UiLivenessGuard : IDisposable
    {
        private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
        private const int ProbeTimeoutMicroseconds = 5_000_000;
        private const int FailuresBeforeExit = 3;

        private readonly int _port;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread _thread;
        private int _started;

        public UiLivenessGuard(int port)
        {
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            _port = port;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ui-liveness-guard",
            };
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("UI liveness guard was already started.");
            }
            _thread.Start();
        }

        private void Run()
        {
            if (_stop.Wait(StartupGrace))
            {
                return;
            }

            var consecutiveFailures = 0;
            while (!_stop.IsSet)
            {
                if (Probe())
                {
                    if (consecutiveFailures > 0)
                    {
                        Logger.Log("UI liveness guard: Kestrel loopback probe recovered.");
                    }
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    Logger.Log(
                        $"UI liveness guard: Kestrel loopback probe failed "
                        + $"({consecutiveFailures}/{FailuresBeforeExit}).");
                    if (consecutiveFailures >= FailuresBeforeExit)
                    {
                        Environment.FailFast(
                            $"Kestrel failed {FailuresBeforeExit} consecutive loopback probes; "
                            + "terminating so the service manager can restart the UI.");
                    }
                }

                if (_stop.Wait(ProbeInterval))
                {
                    return;
                }
            }
        }

        private bool Probe()
        {
            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Stream,
                    ProtocolType.Tcp)
                {
                    Blocking = false,
                    SendTimeout = ProbeTimeoutMicroseconds / 1000,
                    ReceiveTimeout = ProbeTimeoutMicroseconds / 1000,
                };

                try
                {
                    socket.Connect(new IPEndPoint(IPAddress.Loopback, _port));
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                    SocketError.WouldBlock or
                    SocketError.InProgress or
                    SocketError.AlreadyInProgress)
                {
                    // Expected for a non-blocking connect.
                }

                if (!socket.Connected)
                {
                    if (!socket.Poll(ProbeTimeoutMicroseconds, SelectMode.SelectWrite))
                    {
                        return false;
                    }
                    var connectErrorValue = socket.GetSocketOption(
                        SocketOptionLevel.Socket,
                        SocketOptionName.Error);
                    if (connectErrorValue is not int connectErrorCode)
                    {
                        return false;
                    }
                    var connectError = (SocketError)connectErrorCode;
                    if (connectError != SocketError.Success)
                    {
                        return false;
                    }
                }

                socket.Blocking = true;
                var request = Encoding.ASCII.GetBytes(
                    "GET /healthz HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                var sent = 0;
                while (sent < request.Length)
                {
                    var written = socket.Send(request, sent, request.Length - sent, SocketFlags.None);
                    if (written <= 0)
                    {
                        return false;
                    }
                    sent += written;
                }

                var response = new byte[64];
                var received = 0;
                while (received < response.Length)
                {
                    var read = socket.Receive(
                        response,
                        received,
                        response.Length - received,
                        SocketFlags.None);
                    if (read <= 0)
                    {
                        break;
                    }
                    received += read;
                    if (Array.IndexOf(response, (byte)'\n', 0, received) >= 0)
                    {
                        break;
                    }
                }
                var statusLine = Encoding.ASCII.GetString(response, 0, received);
                return statusLine.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                    || statusLine.StartsWith("HTTP/1.0 200", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (Volatile.Read(ref _started) != 0
                && Thread.CurrentThread != _thread
                && !_thread.Join(TimeSpan.FromSeconds(2)))
            {
                Logger.Log("UI liveness guard: thread did not stop within 2 seconds.");
            }
            _stop.Dispose();
        }
    }
}
