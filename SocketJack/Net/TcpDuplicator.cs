using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    internal class TcpDuplicator : IDisposable {

        // This class is basically a reverse VPN.
        // I created it to serve as a way to connect Visual Studio Copilot to the SocketJack server, which is running on a different machine. It works by creating a TCP listener on the local machine, and then forwarding all incoming connections to a remote server.

        #region Fields

        private readonly string _remoteHost;
        private readonly int _remotePort;
        private readonly int _localPort;
        private System.Net.Sockets.TcpListener _listener;
        private readonly List<ForwardingSession> _sessions = new List<ForwardingSession>();
        private bool _isRunning = false;
        private bool _isDisposed = false;
        private CancellationTokenSource _cts;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new TcpDuplicator that listens locally and forwards to a remote server.
        /// </summary>
        /// <param name="remoteHost">The remote host to forward connections to.</param>
        /// <param name="remotePort">The remote port to forward connections to.</param>
        /// <param name="localPort">The local port to listen on.</param>
        public TcpDuplicator(string remoteHost, int remotePort, int localPort) {
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            _localPort = localPort;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts listening on the local port and forwarding connections to the remote server.
        /// </summary>
        /// <returns>True if successfully started; false otherwise.</returns>
        public bool Start()
        {
            if (_isDisposed)
                throw new ObjectDisposedException("TcpDuplicator is disposed.");
            if (_isRunning)
                return false;

            try
            {
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, _localPort);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _isRunning = true;
                _ = AcceptLoopAsync(_cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start TcpDuplicator on port {_localPort}.", ex);
            }
        }

        /// <summary>
        /// Stops listening and closes all forwarding sessions.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();

            lock (_sessions)
            {
                foreach (var session in _sessions)
                {
                    session.Close();
                }
                _sessions.Clear();
            }
        }

        #endregion

        #region Private Methods

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient localClient = await _listener.AcceptTcpClientAsync();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            System.Net.Sockets.TcpClient remoteClient = new System.Net.Sockets.TcpClient();
                            await remoteClient.ConnectAsync(_remoteHost, _remotePort);

                            var session = new ForwardingSession(localClient, remoteClient, OnSessionClosed);
                            lock (_sessions)
                            {
                                _sessions.Add(session);
                            }
                            session.Start();
                        }
                        catch
                        {
                            localClient.Dispose();
                        }
                    }, cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected when listener is stopped
            }
            catch (Exception)
            {
                // Log or handle other exceptions
            }
        }

        private void OnSessionClosed(ForwardingSession session)
        {
            lock (_sessions)
            {
                _sessions.Remove(session);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            Stop();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion

        #region ForwardingSession

        /// <summary>
        /// Manages bidirectional forwarding between a local client and remote server.
        /// </summary>
        private class ForwardingSession
        {
            private readonly System.Net.Sockets.TcpClient _localClient;
            private readonly System.Net.Sockets.TcpClient _remoteClient;
            private readonly Action<ForwardingSession> _onClose;
            private bool _isClosed = false;

            public ForwardingSession(System.Net.Sockets.TcpClient localClient, System.Net.Sockets.TcpClient remoteClient, Action<ForwardingSession> onClose)
            {
                _localClient = localClient;
                _remoteClient = remoteClient;
                _onClose = onClose;
            }

            public void Start()
            {
                Task.Run(() => ForwardAsync(_localClient.GetStream(), _remoteClient.GetStream()));
                Task.Run(() => ForwardAsync(_remoteClient.GetStream(), _localClient.GetStream()));
            }

            public void Close()
            {
                if (_isClosed)
                    return;
                _isClosed = true;
                try { _localClient.Dispose(); } catch { }
                try { _remoteClient.Dispose(); } catch { }
                _onClose?.Invoke(this);
            }

            private async Task ForwardAsync(System.Net.Sockets.NetworkStream from, System.Net.Sockets.NetworkStream to)
            {
                var buffer = new byte[65536];
                try
                {
                    while (!_isClosed)
                    {
                        int bytesRead = await from.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                            break;
                        await to.WriteAsync(buffer, 0, bytesRead);
                        await to.FlushAsync();
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    Close();
                }
            }
        }

        #endregion
    }
}
