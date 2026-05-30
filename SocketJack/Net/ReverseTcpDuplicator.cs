using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetTcpClient = System.Net.Sockets.TcpClient;

namespace SocketJack.Net {
    public sealed class ReverseTcpRelayLogEventArgs : EventArgs {
        public ReverseTcpRelayLogEventArgs(string instanceId, string message) {
            InstanceId = instanceId ?? "";
            Message = message ?? "";
            TimestampUtc = DateTimeOffset.UtcNow;
        }

        public string InstanceId { get; }
        public string Message { get; }
        public DateTimeOffset TimestampUtc { get; }
    }

    public sealed class ReverseTcpRelayServer : IDisposable {
        private const int MaxWaitingPublicClients = 32;
        private const int AgentSignalSendTimeoutMs = 1000;
        private const int AgentReadyHandshakeTimeoutMs = 6000;
        private static readonly byte[] AgentStartSignal = new byte[] { 1 };
        private const byte AgentReadySignal = 2;
        private readonly object _sync = new object();
        private readonly ConcurrentQueue<NetTcpClient> _waitingAgents = new ConcurrentQueue<NetTcpClient>();
        private readonly ConcurrentQueue<NetTcpClient> _waitingPublicClients = new ConcurrentQueue<NetTcpClient>();
        private TcpListener _publicListener;
        private TcpListener _agentListener;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private int _activeSessions;
        private int _waitingAgentCount;
        private int _waitingPublicCount;

        public ReverseTcpRelayServer(string id, string name, int publicPort, int agentPort) {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            Name = string.IsNullOrWhiteSpace(name) ? "Shell Relay" : name.Trim();
            PublicPort = publicPort;
            AgentPort = agentPort;
        }

        public event EventHandler<ReverseTcpRelayLogEventArgs> Log;

        public string Id { get; }
        public string Name { get; }
        public int PublicPort { get; }
        public int AgentPort { get; }
        public bool Enabled { get; set; } = true;
        public bool IsRunning { get; private set; }
        public int ActiveSessions { get { return Volatile.Read(ref _activeSessions); } }
        public int WaitingAgents { get { return Volatile.Read(ref _waitingAgentCount); } }
        public int WaitingPublicClients { get { return Volatile.Read(ref _waitingPublicCount); } }

        public void Start() {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ReverseTcpRelayServer));

            lock (_sync) {
                if (IsRunning)
                    return;

                _cts = new CancellationTokenSource();
                _publicListener = new TcpListener(IPAddress.Any, PublicPort);
                _agentListener = new TcpListener(IPAddress.Any, AgentPort);
                _publicListener.Start(512);
                _agentListener.Start(512);
                IsRunning = true;
                LogMessage("Listening: public " + PublicPort + ", agent " + AgentPort + ".");
                _ = AcceptPublicLoopAsync(_cts.Token);
                _ = AcceptAgentLoopAsync(_cts.Token);
            }
        }

        public void Stop() {
            lock (_sync) {
                if (!IsRunning)
                    return;

                IsRunning = false;
                try { _cts?.Cancel(); } catch { }
                try { _publicListener?.Stop(); } catch { }
                try { _agentListener?.Stop(); } catch { }
                Drain(_waitingAgents);
                Drain(_waitingPublicClients);
                Volatile.Write(ref _waitingAgentCount, 0);
                Volatile.Write(ref _waitingPublicCount, 0);
                LogMessage("Stopped.");
            }
        }

        public void Dispose() {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        public void PruneDisconnectedClients() {
            PruneDisconnectedClients(_waitingAgents, ref _waitingAgentCount);
            PruneDisconnectedClients(_waitingPublicClients, ref _waitingPublicCount);
            TrimWaitingPublicClientsToLimit(MaxWaitingPublicClients);
        }

        private async Task AcceptPublicLoopAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                NetTcpClient client = null;
                try {
                    client = await _publicListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (!Enabled) {
                        LogMessage("Rejected public client while disabled.");
                        client.Dispose();
                        continue;
                    }

                    ConfigureStreamingSocket(client, GetRelayLoad());
                    PruneDisconnectedClients();
                    if (WaitingAgents <= 0) {
                        LogMessage("Rejected public client because no reverse agent was waiting.");
                        client.Dispose();
                        continue;
                    }

                    int dropped = TrimWaitingPublicClientsToLimit(MaxWaitingPublicClients - 1);
                    if (dropped > 0)
                        LogMessage("Dropped " + dropped + " older waiting public client(s) before accepting a new one.");
                    _waitingPublicClients.Enqueue(client);
                    Interlocked.Increment(ref _waitingPublicCount);
                    LogMessage("Public client waiting.");
                    TryPair();
                } catch (ObjectDisposedException) {
                    break;
                } catch (Exception ex) {
                    try { client?.Dispose(); } catch { }
                    if (!cancellationToken.IsCancellationRequested)
                        LogMessage("Public accept failed: " + ex.Message);
                }
            }
        }

        private async Task AcceptAgentLoopAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                NetTcpClient agent = null;
                try {
                    agent = await _agentListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (!Enabled) {
                        LogMessage("Rejected agent while disabled.");
                        agent.Dispose();
                        continue;
                    }

                    ConfigureStreamingSocket(agent, GetRelayLoad());
                    _waitingAgents.Enqueue(agent);
                    Interlocked.Increment(ref _waitingAgentCount);
                    LogMessage("Agent connected and waiting.");
                    TryPair();
                } catch (ObjectDisposedException) {
                    break;
                } catch (Exception ex) {
                    try { agent?.Dispose(); } catch { }
                    if (!cancellationToken.IsCancellationRequested)
                        LogMessage("Agent accept failed: " + ex.Message);
                }
            }
        }

        private void TryPair() {
            PruneDisconnectedClients();
            while (Enabled) {
                if (!_waitingPublicClients.TryDequeue(out NetTcpClient publicClient))
                    return;

                Interlocked.Decrement(ref _waitingPublicCount);
                if (!IsSocketConnected(publicClient)) {
                    try { publicClient.Dispose(); } catch { }
                    continue;
                }

                bool paired = false;
                while (Enabled && _waitingAgents.TryDequeue(out NetTcpClient agent)) {
                    Interlocked.Decrement(ref _waitingAgentCount);
                    if (!IsSocketConnected(agent)) {
                        try { agent.Dispose(); } catch { }
                        continue;
                    }

                    if (!TrySignalAgent(agent)) {
                        LogMessage("Dropped stale reverse agent before pairing a public client.");
                        try { agent.Dispose(); } catch { }
                        continue;
                    }

                    NetTcpClient pairedPublicClient = publicClient;
                    NetTcpClient pairedAgent = agent;
                    paired = true;
                    _ = Task.Run(() => RunSessionAsync(pairedPublicClient, pairedAgent));
                    break;
                }

                if (!paired) {
                    LogMessage("Rejected public client because no usable reverse agent was waiting.");
                    try { publicClient.Dispose(); } catch { }
                    int dropped = TrimWaitingPublicClientsToLimit(0);
                    if (dropped > 0)
                        LogMessage("Dropped " + dropped + " waiting public client(s) after reverse agents were exhausted.");
                    return;
                }
            }
        }

        private async Task RunSessionAsync(NetTcpClient publicClient, NetTcpClient agent) {
            Interlocked.Increment(ref _activeSessions);
            try {
                LogMessage("Paired public client with reverse agent.");
                await BridgeAsync(publicClient, agent, GetRelayLoad).ConfigureAwait(false);
            } catch (Exception ex) {
                LogMessage("Session failed: " + ex.Message);
            } finally {
                try { publicClient.Dispose(); } catch { }
                try { agent.Dispose(); } catch { }
                Interlocked.Decrement(ref _activeSessions);
                LogMessage("Session closed.");
            }
        }

        private static bool TrySignalAgent(NetTcpClient agent) {
            int originalSendTimeout = 0;
            int originalReceiveTimeout = 0;
            bool shouldRestoreSendTimeout = false;
            bool shouldRestoreReceiveTimeout = false;
            try {
                originalSendTimeout = agent.SendTimeout;
                agent.SendTimeout = AgentSignalSendTimeoutMs;
                shouldRestoreSendTimeout = true;
            } catch {
            }
            try {
                originalReceiveTimeout = agent.ReceiveTimeout;
                agent.ReceiveTimeout = AgentReadyHandshakeTimeoutMs;
                shouldRestoreReceiveTimeout = true;
            } catch {
            }

            try {
                NetworkStream agentStream = agent.GetStream();
                agentStream.Write(AgentStartSignal, 0, AgentStartSignal.Length);
                agentStream.Flush();
                int ready = agentStream.ReadByte();
                return ready == AgentReadySignal;
            } catch {
                return false;
            } finally {
                if (shouldRestoreSendTimeout) {
                    try { agent.SendTimeout = originalSendTimeout; } catch { }
                }
                if (shouldRestoreReceiveTimeout) {
                    try { agent.ReceiveTimeout = originalReceiveTimeout; } catch { }
                }
            }
        }

        private int GetRelayLoad() {
            return ActiveSessions + WaitingAgents + WaitingPublicClients + 1;
        }

        private static async Task BridgeAsync(NetTcpClient left, NetTcpClient right, Func<int> getRelayLoad) {
            using NetworkStream leftStream = left.GetStream();
            using NetworkStream rightStream = right.GetStream();
            Task a = CopyAsync(leftStream, rightStream, getRelayLoad);
            Task b = CopyAsync(rightStream, leftStream, getRelayLoad);
            await Task.WhenAny(a, b).ConfigureAwait(false);
        }

        private static bool IsSocketConnected(NetTcpClient client) {
            try {
                Socket socket = client.Client;
                return socket != null &&
                       socket.Connected &&
                       !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            } catch {
                return false;
            }
        }

        private static async Task CopyAsync(NetworkStream from, NetworkStream to, Func<int> getRelayLoad) {
            byte[] buffer = TcpForwardingBufferProfile.RentCopyBuffer(getRelayLoad());
            try {
                while (true) {
                    int desiredSize = TcpForwardingBufferProfile.GetCopyBufferSize(getRelayLoad());
                    if (buffer.Length < desiredSize) {
                        TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
                        buffer = TcpForwardingBufferProfile.RentCopyBuffer(getRelayLoad());
                    }

                    int read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    await to.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                }
            } catch {
            } finally {
                TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
            }
        }

        private static void ConfigureStreamingSocket(NetTcpClient client, int activeOrQueuedSessions) {
            TcpForwardingBufferProfile.ConfigureStreamingSocket(client, activeOrQueuedSessions);
        }

        private static void Drain(ConcurrentQueue<NetTcpClient> queue) {
            while (queue.TryDequeue(out NetTcpClient client)) {
                try { client.Dispose(); } catch { }
            }
        }

        private static void PruneDisconnectedClients(ConcurrentQueue<NetTcpClient> queue, ref int queuedCount) {
            int count = Volatile.Read(ref queuedCount);
            for (int i = 0; i < count; i++) {
                if (!queue.TryDequeue(out NetTcpClient client))
                    return;

                if (IsSocketConnected(client)) {
                    queue.Enqueue(client);
                } else {
                    try { client.Dispose(); } catch { }
                    Interlocked.Decrement(ref queuedCount);
                }
            }
        }

        private int TrimWaitingPublicClientsToLimit(int maxCount) {
            int dropped = 0;
            while (Volatile.Read(ref _waitingPublicCount) > maxCount) {
                if (!_waitingPublicClients.TryDequeue(out NetTcpClient client))
                    break;

                Interlocked.Decrement(ref _waitingPublicCount);
                dropped++;
                try { client.Dispose(); } catch { }
            }

            return dropped;
        }

        private void LogMessage(string message) {
            Log?.Invoke(this, new ReverseTcpRelayLogEventArgs(Id, "[" + Name + "] " + message));
        }
    }

    public sealed class ReverseTcpRelayClient : IDisposable {
        private const byte AgentReadySignal = 2;
        private static readonly TimeSpan WaitingAgentRefreshInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan WaitingAgentRefreshSlotJitter = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan MasterConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LocalConnectTimeout = TimeSpan.FromSeconds(5);
        private readonly int _connectionPoolSize;
        private CancellationTokenSource _cts;
        private bool _disposed;

        public ReverseTcpRelayClient(string instanceId, string name, string masterHost, int agentPort, string localHost, int localPort, int connectionPoolSize = 4) {
            InstanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId.Trim();
            Name = string.IsNullOrWhiteSpace(name) ? "Shell Relay" : name.Trim();
            MasterHost = string.IsNullOrWhiteSpace(masterHost) ? "127.0.0.1" : masterHost.Trim();
            AgentPort = agentPort;
            LocalHost = string.IsNullOrWhiteSpace(localHost) ? "127.0.0.1" : localHost.Trim();
            LocalPort = localPort;
            _connectionPoolSize = Math.Max(1, Math.Min(32, connectionPoolSize));
        }

        public event EventHandler<ReverseTcpRelayLogEventArgs> Log;

        public string InstanceId { get; }
        public string Name { get; }
        public string MasterHost { get; }
        public int AgentPort { get; }
        public string LocalHost { get; }
        public int LocalPort { get; }
        public bool IsRunning { get; private set; }

        public void Start() {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ReverseTcpRelayClient));
            if (IsRunning)
                return;

            _cts = new CancellationTokenSource();
            IsRunning = true;
            LogMessage("Starting reverse agents to " + MasterHost + ":" + AgentPort + " -> " + LocalHost + ":" + LocalPort + ".");
            for (int i = 0; i < _connectionPoolSize; i++)
                _ = RunAgentLoopAsync(i + 1, _cts.Token);
        }

        public void Stop() {
            if (!IsRunning)
                return;

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            LogMessage("Stopped.");
        }

        public void Dispose() {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private async Task RunAgentLoopAsync(int slot, CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                bool delayBeforeReconnect = true;
                try {
                    using NetTcpClient master = new NetTcpClient();
                    ConfigureStreamingSocket(master, GetRelayLoad());
                    await ConnectWithTimeoutAsync(master, MasterHost, AgentPort, MasterConnectTimeout, cancellationToken).ConfigureAwait(false);
                    LogMessage("Agent " + slot + " connected to master.");
                    NetworkStream masterStream = master.GetStream();
                    int signal = await ReadSignalAsync(masterStream, GetWaitingAgentRefreshInterval(slot), cancellationToken).ConfigureAwait(false);
                    if (signal == 0) {
                        LogMessage("Agent " + slot + " refreshing idle master connection.");
                        delayBeforeReconnect = false;
                        continue;
                    }

                    if (signal != 1)
                        continue;

                    using NetTcpClient local = new NetTcpClient();
                    ConfigureStreamingSocket(local, GetRelayLoad());
                    await ConnectWithTimeoutAsync(local, LocalHost, LocalPort, LocalConnectTimeout, cancellationToken).ConfigureAwait(false);
                    LogMessage("Agent " + slot + " paired with local " + LocalHost + ":" + LocalPort + ".");
                    await masterStream.WriteAsync(new byte[] { AgentReadySignal }, 0, 1, cancellationToken).ConfigureAwait(false);
                    await masterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await BridgeAsync(master, local, GetRelayLoad).ConfigureAwait(false);
                    LogMessage("Agent " + slot + " session closed.");
                    delayBeforeReconnect = false;
                } catch (ObjectDisposedException) {
                    break;
                } catch (Exception ex) {
                    if (!cancellationToken.IsCancellationRequested)
                        LogMessage("Agent " + slot + " reconnecting: " + ex.Message);
                }

                if (delayBeforeReconnect && !cancellationToken.IsCancellationRequested) {
                    try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); } catch { }
                }
            }
        }

        private static TimeSpan GetWaitingAgentRefreshInterval(int slot) {
            int safeSlot = Math.Max(1, Math.Min(32, slot));
            return WaitingAgentRefreshInterval + TimeSpan.FromMilliseconds(WaitingAgentRefreshSlotJitter.TotalMilliseconds * (safeSlot - 1));
        }

        private static async Task<int> ReadSignalAsync(NetworkStream stream, TimeSpan idleRefreshInterval, CancellationToken cancellationToken) {
            byte[] buffer = new byte[1];
            while (!cancellationToken.IsCancellationRequested) {
                Task<int> readTask = stream.ReadAsync(buffer, 0, 1, cancellationToken);
                Task completed = await Task.WhenAny(readTask, Task.Delay(idleRefreshInterval, cancellationToken)).ConfigureAwait(false);
                if (completed != readTask)
                    return 0;

                int read = await readTask.ConfigureAwait(false);
                if (read <= 0)
                    return -1;
                return buffer[0];
            }

            return -1;
        }

        private static async Task ConnectWithTimeoutAsync(NetTcpClient client, string host, int port, TimeSpan timeout, CancellationToken cancellationToken) {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task connectTask = client.ConnectAsync(host, port);
            Task timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed != connectTask) {
                try { client.Close(); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out connecting to " + host + ":" + port + " after " + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
            }

            timeoutCts.Cancel();
            await connectTask.ConfigureAwait(false);
        }

        private int GetRelayLoad() {
            return _connectionPoolSize;
        }

        private static async Task BridgeAsync(NetTcpClient left, NetTcpClient right, Func<int> getRelayLoad) {
            using NetworkStream leftStream = left.GetStream();
            using NetworkStream rightStream = right.GetStream();
            Task a = CopyAsync(leftStream, rightStream, getRelayLoad);
            Task b = CopyAsync(rightStream, leftStream, getRelayLoad);
            await Task.WhenAny(a, b).ConfigureAwait(false);
        }

        private static async Task CopyAsync(NetworkStream from, NetworkStream to, Func<int> getRelayLoad) {
            byte[] buffer = TcpForwardingBufferProfile.RentCopyBuffer(getRelayLoad());
            try {
                while (true) {
                    int desiredSize = TcpForwardingBufferProfile.GetCopyBufferSize(getRelayLoad());
                    if (buffer.Length < desiredSize) {
                        TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
                        buffer = TcpForwardingBufferProfile.RentCopyBuffer(getRelayLoad());
                    }

                    int read = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    await to.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                }
            } catch {
            } finally {
                TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
            }
        }

        private static void ConfigureStreamingSocket(NetTcpClient client, int activeOrQueuedSessions) {
            TcpForwardingBufferProfile.ConfigureStreamingSocket(client, activeOrQueuedSessions);
        }

        private void LogMessage(string message) {
            Log?.Invoke(this, new ReverseTcpRelayLogEventArgs(InstanceId, "[" + Name + "] " + message));
        }
    }
}
