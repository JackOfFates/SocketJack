using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace SocketJack.Net {
    public class NetworkConnection : IDisposable {

        #region Properties

        /// <summary>
        /// The socket associated to the connection.
        /// </summary>
        public Socket Socket {
            get {
                return _Socket;
            }
        }
        private Socket _Socket = null;

        /// <summary>
        /// The stream associated to the connection.
        /// </summary>
        public Stream Stream {
            get {
                return SSL ? (Stream)SslStream : _Stream;
            }
        }
        public NetworkStream _Stream = null;
        public SslStream SslStream { get; set; }

        public static readonly byte[] Terminator = new[] { (byte)192, (byte)128 };
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        private static readonly Type ByteArrayType = typeof(byte[]);
        private static readonly char[] LengthTrimChars = { '\0', ' ', '\r', '\n' };

        /// <summary>
        /// Whether or not data is compressed.
        /// </summary>
        public bool Compressed {
            get {
                return Parent.Options.UseCompression;
            }
        }

        /// <summary>
        /// Whether or not data is encrypted.
        /// </summary>
        public bool SSL {
            get {
                return Parent.Options.UseSsl;
            }
        }

        /// <summary>
        /// The remote endpoint of the connection.
        /// </summary>
        public IPEndPoint EndPoint { get; set; }

        /// <summary>
        /// Unique identifier for this connection.
        /// </summary>
        public Guid ID { get; set; }

        /// <summary>
        /// Parent of this connection.
        /// </summary>
        public ISocket Parent {
            get {
                return _Parent;
            }
        }
        private ISocket _Parent;

        public bool IsWebSocket {
            get {
                return _isWebSocket;
            }
        }

        /// <summary>
        /// True if connection sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return IsReceiving || IsSending;
            }
        }

        /// <summary>
        /// Bytes per second sent on this connection (rolling average over the last 10 seconds).
        /// </summary>
        public int BytesPerSecondSent {
            get {
                return GetRollingBpsAverage(isSent: true);
            }
        }
        protected internal int _SentBytesPerSecond = 0;
        protected internal int SentBytesCounter = 0;

        /// <summary>
        /// Bytes per second received on this connection (rolling average over the last 10 seconds).
        /// </summary>
        public int BytesPerSecondReceived {
            get {
                return GetRollingBpsAverage(isSent: false);
            }
        }
        protected internal int _ReceivedBytesPerSecond = 0;
        protected internal int ReceivedBytesCounter = 0;

        private const int BpsRollingWindowSeconds = 10;
        private readonly object _bpsSamplesLock = new object();
        private readonly Queue<(DateTime time, int sent, int recv)> _bpsSamples = new Queue<(DateTime, int, int)>();

        protected internal void RecordBpsSample() {
            lock (_bpsSamplesLock) {
                _bpsSamples.Enqueue((DateTime.UtcNow, _SentBytesPerSecond, _ReceivedBytesPerSecond));
                PruneBpsSamples();
            }
        }

        private void PruneBpsSamples() {
            var cutoff = DateTime.UtcNow.AddSeconds(-BpsRollingWindowSeconds);
            while (_bpsSamples.Count > 0 && _bpsSamples.Peek().time < cutoff)
                _bpsSamples.Dequeue();
        }

        private int GetRollingBpsAverage(bool isSent) {
            lock (_bpsSamplesLock) {
                PruneBpsSamples();
                if (_bpsSamples.Count == 0)
                    return 0;
                long sum = 0;
                foreach (var s in _bpsSamples)
                    sum += isSent ? s.sent : s.recv;
                return (int)(sum / _bpsSamples.Count);
            }
        }

        public long TotalBytesSent {
            get {
                return _TotalBytesSent;
            }
        }
        protected internal long _TotalBytesSent = 0;
        protected internal long _TotalBytesSent_l = 0;

        public long TotalBytesReceived {
            get {
                return _TotalBytesReceived;
            }
        }
        protected internal long _TotalBytesReceived = 0;
        protected internal long _TotalBytesReceived_l = 0;
        DateTime LastSendFrame = DateTime.UtcNow;
        DateTime LastReceiveFrame = DateTime.UtcNow;

        /// <summary>
        /// Remote Peer identifier for peer to peer interactions used to determine the Server's Client GUID.
        /// </summary>
        /// <returns>NULL if accessed before the Server identifies the Client.
        /// To avoid problems please do not acccess this via OnConnected Event.</returns>
        public Identifier Identity {
            get {
                return _Identity;
            }
        }
        public Identifier _Identity;

        /// <summary>
        /// True if the connection is receiving data.
        /// </summary>
        public bool IsReceiving {
            get {
                return _IsReceiving == 1;
            }
            set {
                Interlocked.Exchange(ref _IsReceiving, value ? 1 : 0);
            }
        }
        private int _IsReceiving = 0;

        /// <summary>
        /// True if the connection is sending data.
        /// </summary>
        public bool IsSending {
            get {
                return _IsSending == 1;
            }
            set {
                Interlocked.Exchange(ref _IsSending, value ? 1 : 0);
            }
        }
        private int _IsSending = 0;

        /// <summary>
        /// Buffer used to receive data.
        /// </summary>
        public List<byte> DownloadBuffer {
            get {
                return _DownloadBuffer;
            }
        }
        protected internal List<byte> _DownloadBuffer = new();

        /// <summary>
        /// Buffer used to send data.
        /// </summary>
        public List<byte> UploadBuffer {
            get {
                return _UploadBuffer;
            }
        }
        protected internal List<byte> _UploadBuffer = new();

        /// <summary>
        /// True if the connection is closed.
        /// </summary>
        public bool Closed {
            get {
                return _Closed;
            }
            set {
                _Closed = value;
                if (Closed) {
                    Interlocked.Exchange(ref _TotalBytesReceived, 0);
                    Interlocked.Exchange(ref _TotalBytesSent, 0);
                }
            }
        }

        /// <summary>
        /// True if the connection is closing.
        /// </summary>
        public bool Closing {
            get {
                return _Closing;
            }
        }
        #endregion

        #region Internal
        private volatile bool _Closed = false;
        public volatile bool _Closing = false;
        private readonly object _closeLock = new();
        private readonly string _parentTypeName;
        private readonly bool _isWebSocket;
        private readonly byte[] _lengthHeaderBuffer = new byte[15];
        private volatile bool _connectionCounted = false;
        private static int _activeConnectionCount = 0;
        protected internal SemaphoreSlim _SendSignal = new(0);
        protected internal long _nextChunkFlushAt = 0;

        protected internal ConcurrentQueue<SendQueueItem> SendQueue = new();
        protected internal List<byte> SendQueueRaw = new();

        /// <summary>
        /// Adjusts <see cref="ThreadPool"/> minimum threads so worker/IO capacity
        /// scales with the number of active connections.  Without this, the default
        /// pool ramp-up (~1 thread per 500 ms) causes server-side deserialization
        /// and callback dispatch to fall behind under heavy bot load.
        /// </summary>
        private static void AdjustThreadPool(int connectionDelta) {
            int connections = Interlocked.Add(ref _activeConnectionCount, connectionDelta);
            if (connections < 0) {
                Interlocked.Exchange(ref _activeConnectionCount, 0);
                connections = 0;
            }
            int processorCount = Environment.ProcessorCount;
            int required = processorCount + (connections * 2);
            ThreadPool.GetMinThreads(out int currentWorker, out int currentIo);
            if (currentWorker < required || currentIo < required) {
                ThreadPool.SetMinThreads(
                    Math.Max(currentWorker, required),
                    Math.Max(currentIo, required));
            }
        }

        /// <summary>
        /// <see langword="True"/> if created by a TcpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// Sends the remote client their remote Identity and IP.
        /// </summary>
        internal void SendLocalIdentity() {
            Send(Identifier.Create(ID, true, EndPoint.Address.ToString()));
        }

        /// <summary>
        /// Polls a socket to see if it is connected.
        /// </summary>
        /// <param name="socket"></param>
        /// <returns><see langword="True"/> if poll successful.</returns>
        public bool Poll() {
            try {
                if (Socket == null) {
                    return false;
                } else if (!Socket.Connected || Closed || Closing) {
                    return false;
                } else if (!Active && Socket.Poll(10000, SelectMode.SelectRead) && Socket.Available == 0) {
                    return false;
                } else {
                    return true;
                }
            } catch (SocketException) {
                return false;
            } catch (ObjectDisposedException) {
                return false;
            }
        }

        public void CloseConnection() {
            Close(Parent);
        }

        public void Close(ISocket sender, DisconnectionReason Reason = DisconnectionReason.LocalSocketClosed) {
            lock (_closeLock) {
                if (!Closed && !Closing) {
                    _Closing = true;
                    if (_connectionCounted) {
                        _connectionCounted = false;
                        AdjustThreadPool(-1);
                    }
                    var e = new DisconnectedEventArgs(sender, this, Reason);
                    if (Socket != null && Socket.Connected) {
                        MethodExtensions.TryInvoke(() => Socket.Shutdown(SocketShutdown.Both));
                        MethodExtensions.TryInvoke(() => Socket.Close());
                    }
                    InvokeDisconnected(sender, e);
                    SendQueue.Clear();
                    lock (SendQueueRaw) {
                        SendQueueRaw.Clear();
                    }
                    _UploadBuffer.Clear();
                    _DownloadBuffer.Clear();
                    sender.EndPoint = null;
                    UnsubscribePeerUpdate();
                    try { _SendSignal.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        public void StartConnectionTester() {
            Task.Factory.StartNew(async () => {
                if (_parentTypeName == "WebSocketClient")
                    return;

                int failedPollCount = 0;
                const int maxFailedPolls = 3;

                while (!isDisposed && !Closed && !Closing && Socket.Connected) {
                    if (!Poll()) {
                        failedPollCount++;
                        if (failedPollCount >= maxFailedPolls) {
                            if (!Closed) {
                                Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                            }
                            break;
                        }
                    } else {
                        failedPollCount = 0;
                    }
                    await Task.Delay(1000);
                }
                if (!Closed && !Closing) {
                    Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                }
            }, TaskCreationOptions.LongRunning);
        }

        #endregion

        #region Peer To Peer

        private void SetPeerID(ISocket sender, Identifier RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                        _Identity = RemotePeer;
                        TcpClient Client = (TcpClient)Parent;
                        if (Client != null) {
                            Client.LogFormat("[{0}] Local Identity = {1}", new[] { Client.Name, RemotePeer.ID.ToUpper() });
                            Client.InvokeOnIdentified(sender, _Identity);
                        }
                        break;
                    }
            }
        }
        private void ResetPeerID(DisconnectedEventArgs args) {
            _Identity = null;
            UnsubscribePeerUpdate();
        }
        private void SubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && !Client.isPeerUpdateSubscribed) {
                Client.isPeerUpdateSubscribed = true;
                Client.OnDisconnected += ResetPeerID;
                Client.PeerUpdate += SetPeerID;
            }
        }
        private void UnsubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && Client.isPeerUpdateSubscribed) {
                Client.OnDisconnected -= ResetPeerID;
                Client.PeerUpdate -= SetPeerID;
                Client.isPeerUpdateSubscribed = false;
            }
        }

        #endregion

        #region Events

        public event ClientDisconnectedEventHandler OnDisconnected;
        public delegate void ClientDisconnectedEventHandler(NetworkConnection sender, DisconnectedEventArgs e);

        protected internal void InvokeDisconnected(ISocket sender, DisconnectedEventArgs e) {
            if (e.Connection.Closed) return;
            var senderTypeName = sender.GetType().Name;
            if (!(senderTypeName == "WebSocketClient") && !(senderTypeName == "WebSocketClient") && !(sender is TcpServer) && !(senderTypeName == "WebSocketServer")) {
                ((ISocket)sender).InvokeOnDisconnected(sender, e.Connection);
                e.Connection.Closed = true;
            }
#if UNITY
            MainThread.Run(() => {
		        OnDisconnected?.Invoke(this, e);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisconnected?.Invoke(this, e);
            });
#endif
#if !UNITY && !WINDOWS
            OnDisconnected?.Invoke(this, e);
#endif
        }

        #endregion

        #region IDisposable

        private bool isDisposed = false;
        public void Dispose() {
            if (!isDisposed) {
                isDisposed = true;
                UnsubscribePeerUpdate();
                Close(Parent);
                _SendSignal.Dispose();
                GC.SuppressFinalize(this);
            } else {
                throw new ObjectDisposedException(ID.ToString().ToUpper());
            }

        }

        #endregion

        #region SSL

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="serverCertificate">The server certificate (required for server-side).</param>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(X509Certificate serverCertificate, string targetHost) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (serverCertificate == null)
                throw new ArgumentNullException(nameof(serverCertificate), "Server certificate required for SSL server mode.");

            SslStream = new SslStream(_Stream, true);
            SslStream.AuthenticateAsServer(serverCertificate, false, SslProtocols.Tls12 | SslProtocols.Tls12, false);

        }

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(string targetHost) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (string.IsNullOrEmpty(targetHost))
                throw new ArgumentNullException(nameof(targetHost), "Target host required for SSL client mode.");

            SslStream = new SslStream(_Stream, true, null);
            SslStream.AuthenticateAsClient(targetHost, null, SslProtocols.Tls12 | SslProtocols.Tls12, false);
        }
        #endregion

        #region Receive

        protected internal void StartReceiving() {
            if (!_connectionCounted) {
                _connectionCounted = true;
                AdjustThreadPool(1);
            }
            Task.Factory.StartNew(async () => {
                var options = Parent.Options;
                if (options.Fps > 0) {
                    DateTime Time = DateTime.UtcNow;
                    while (!isDisposed && !Closed && Socket.Connected) {
                        var now = DateTime.UtcNow;
                        var nextFrame = Time.AddMilliseconds(options.Timeout);
                        if (now >= nextFrame) {
                            Time = now;
                            await Receive();
                        } else {
                            int waitMs = (int)(nextFrame - now).TotalMilliseconds;
                            if (waitMs > 0) await Task.Delay(waitMs);
                        }
                    }
                } else {
                    while (!isDisposed && !Closed && Socket.Connected) {
                        if (Interlocked.CompareExchange(ref _IsReceiving, 1, 0) == 0) {
                            try {
                                await ReceiveData();
                            } catch (Exception ex) {
                                var Reason = ex.Interpret();
                                if (Reason.ShouldLogReason())
                                    Parent.InvokeOnError(this, ex);
                                Parent.CloseConnection(this, Reason);
                            } finally {
                                IsReceiving = false;
                            }
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        internal async Task Receive() {
            while (Socket != null && Stream != null && _Stream != null && !Closed && _Stream.DataAvailable) {
                if (Interlocked.CompareExchange(ref _IsReceiving, 1, 0) == 0) {
                    try {
                        await ReceiveData();
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                        return;
                    } finally {
                        IsReceiving = false;
                    }
                }
            }
        }

        private async Task ReceiveData() {
                var options = Parent.Options;
                var stream = Stream;
                // If terminated streams are disabled treat this as a raw TCP stream (e.g. HTTP)
                if (!options.UseTerminatedStreams) {
                    var bufSize = options.DownloadBufferSize > 0 ? options.DownloadBufferSize : 8192;
                    var buffer = ArrayPool<byte>.Shared.Rent(bufSize);
                    try {
                        var temp = new List<byte>();
                        int bytesRead = 0;
                        // Read available data into buffer
                        do {
                            try {
                                bytesRead = await stream.ReadAsync(buffer, 0, bufSize);
                            } catch (Exception ex) {
                                var Reason = ex.Interpret();
                                if (Reason.ShouldLogReason()) Parent.InvokeOnError(this, ex);
                                break;
                            }
                            if (bytesRead <= 0) {
                                // Stream ended — remote side closed the connection.
                                // Close locally to prevent the outer loop from spinning.
                                if (temp.Count > 0) {
                                    await ParseBuffer(temp, this, Parent);
                                }
                                ArrayPool<byte>.Shared.Return(buffer);
                                IsReceiving = false;
                                Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                                return;
                            }
                            Interlocked.Add(ref _TotalBytesReceived, bytesRead);
                            Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                            temp.AddRange(new ArraySegment<byte>(buffer, 0, bytesRead));
                        } while (_Stream.DataAvailable);

                        if (temp.Count > 0) {
                            await ParseBuffer(temp, this, Parent);
                        }
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason()) Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    } finally {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    IsReceiving = false;
                    return;
                }

                // Read message length header (15 bytes) then read body in buffered chunks
                int lengthRead;
                try {
                    lengthRead = await stream.ReadAsync(_lengthHeaderBuffer, 0, 15);
                    Interlocked.Add(ref _TotalBytesReceived, lengthRead);
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                    IsReceiving = false;
                    return;
                }

                if (lengthRead <= 0) {
                    // Stream ended — remote side closed the connection.
                    Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                    IsReceiving = false;
                    return;
                }

                string lengthStr = Encoding.UTF8.GetString(_lengthHeaderBuffer, 0, lengthRead).Trim(LengthTrimChars);
                if (!int.TryParse(lengthStr, out int Length)) {
                    Parent.InvokeOnError(this, new FormatException("Failed to parse message length."));
                    Parent.CloseConnection(this, DisconnectionReason.Unknown);
                    IsReceiving = false;
                    return;
                }

                byte[] Body = new byte[Length];
                int totalRead = 0;
                try {
                    while (totalRead < Length) {
                        if (Socket is null || stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                            if (!Closed) Parent.CloseConnection(this);
                            IsReceiving = false;
                            return;
                        }

                        long diff = ((TotalBytesReceived - _TotalBytesReceived_l)) - options.MaximumDownloadBytesPerSecond;
                        if (diff > 0) {
                            var now = DateTime.UtcNow;
                            var timeDiff = now - LastReceiveFrame;
                            _ReceivedBytesPerSecond = (int)(TotalBytesReceived - _TotalBytesReceived_l);
                            _TotalBytesReceived_l = TotalBytesReceived;
                            LastReceiveFrame = now;
                            if (timeDiff > OneSecond) {
                                await Task.Delay((int)Math.Min(1000, diff * 1000L / Math.Max(1, options.MaximumDownloadBytesPerSecond)));
                            }
                        }

                        int chunkSize = options.isDownloadBuffered ? options.DownloadBufferSize : (Length - totalRead);
                        chunkSize = Math.Min(chunkSize, Length - totalRead);

                        int bytesRead = await stream.ReadAsync(Body, totalRead, chunkSize);
                        if (bytesRead <= 0) break;
                        totalRead += bytesRead;
                        Interlocked.Add(ref _TotalBytesReceived, bytesRead);
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                    }
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                    IsReceiving = false;
                    return;
                }

                if (totalRead == Length) {
                    ParseBuffer(Body, this, Parent);
                } else {
                    Parent.InvokeOnError(this, new IOException("Unexpected end of stream while reading message body."));
                    Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                }

                IsReceiving = false;
        }

        private async Task ReceiveData_old(bool DataAvailable) {
            DateTime DownloadStartTime = DateTime.UtcNow;
            long TotalBytesRead = 0L;

            // SSL does not support Socket.Available, so we need to read the data until no more bytes received.
            if (SSL | Parent.Options.isDownloadBuffered) {
                int bytesRead = 0;
                do {
                    // Not Yet Implemented
                    //if (Parent.Options.MaximumDownloadMbps > 0) {
                    // Limit download bandwidth.
                    //while (LimitDownloadBandwidth(TotalDownloadedBytes, DownloadStartTime))
                    //    Thread.Sleep(1);
                    //DownloadStartTime = DateTime.UtcNow;
                    //}

                    if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                        if (!Closed) Parent.CloseConnection(this);
                        return;
                    }

                    try {
                        byte[] temp = new byte[Parent.Options.DownloadBufferSize];
                        bytesRead = await Stream.ReadAsync(temp, 0, Parent.Options.DownloadBufferSize);
                        if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);
                        if (bytesRead > 0) _DownloadBuffer.AddRange(temp);
                        temp = null;
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    }

                    if (bytesRead > 0) {
                        TotalBytesRead += bytesRead;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    }
                } while (bytesRead > 0);
            } else {
                try {
                    if (Socket is null)
                        return;
                    int AvailableBytes = Socket.Available;
                    var temp = new byte[AvailableBytes];
                    if (Stream is null) return;
                    int bytesRead = await Stream.ReadAsync(temp, 0, AvailableBytes);
                    if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);

                    if (bytesRead > 0) {
                        if (_DownloadBuffer == null)
                            _DownloadBuffer = new List<byte>(temp);
                        else
                            _DownloadBuffer.AddRange(temp);
                        temp = null;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    } else {
                        _DownloadBuffer.Clear();
                    }
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                }
            }
            IsReceiving = false;
        }

        private static void DeserializeAndDispatch(byte[] Bytes, int ByteLength, NetworkConnection Sender, ISocket Target) {
            if (Target.Options.UseCompression) {
                var decompressionResult = MethodExtensions.TryInvoke(Target.Options.CompressionAlgorithm.Decompress, ref Bytes);
                if (decompressionResult.Success) {
                    Bytes = decompressionResult.Result;
                } else {
                    Target.CloseConnection(Sender, DisconnectionReason.CompressionError);
                    Target.InvokeOnError(Sender, decompressionResult.Exception);
                    return;
                }
            }
            Wrapper wrapper = Target.Options.Serializer.Deserialize(Bytes);
            if (wrapper == null) {
                Target.InvokeOnError(Sender, new P2PException("Deserialized object returned null."));
                return;
            }
            var valueType = wrapper.GetValueType();
            if (wrapper.value != null || wrapper.Type != "") { //wrapper.Type != typeof(PingObject).AssemblyQualifiedName
                if (valueType == typeof(PeerRedirect)) {
                    Byte[] redirectBytes = null;
                    object val = wrapper.value;
                    Type type = wrapper.value.GetType();
                    if (type == typeof(string)) {
                        redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes((string)val);
                    } else if (type == typeof(JsonElement)) {
                        string json = ((JsonElement)val).GetRawText();
                        redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes(json);
                    }
                    PeerRedirect redirect = Target.Options.Serializer.DeserializeRedirect(Target, redirectBytes);
                    Target.HandleReceive(Sender, redirect, valueType, ByteLength);
                } else {
                    object unwrapped = null;
                    try {
                        unwrapped = wrapper.Unwrap(Target);
                    } catch (Exception ex) {
                        Target.InvokeOnError(Sender, ex);
                    }
                    if (unwrapped != null)
                        Target.HandleReceive(Sender, unwrapped, valueType, ByteLength);
                }
            }
        }

        private static void ParseBuffer(byte[] Bytes, NetworkConnection Sender, ISocket Target) {
            Task.Run(() => DeserializeAndDispatch(Bytes, Bytes.Length, Sender, Target));
        }

        private static async Task<List<byte>> ParseBuffer(List<byte> Buffer, NetworkConnection Sender, ISocket Target) {
            if (Buffer == null || Buffer.Count == 0) return Buffer;

            if (Target.Options.UseTerminatedStreams) {
                var TerminatorIndices = Buffer.IndexOfAll(Terminator);

                if (TerminatorIndices.Count > 0) {
                    int lastTerminatorIndex = TerminatorIndices[TerminatorIndices.Count - 1];

                    // Pre-extract all message segments as independent byte arrays
                    // so parallel tasks operate on isolated data instead of the shared List<byte>
                    var segments = new byte[TerminatorIndices.Count][];
                    for (int i = 0; i < TerminatorIndices.Count; i++) {
                        int prevEnd = i > 0 ? TerminatorIndices[i - 1] + Terminator.Length : 0;
                        segments[i] = Buffer.Part(prevEnd, TerminatorIndices[i]);
                    }

                    // Process all segments in parallel
                    var tasks = new Task[segments.Length];
                    for (int i = 0; i < segments.Length; i++) {
                        var segment = segments[i];
                        tasks[i] = Task.Run(() => DeserializeAndDispatch(segment, segment.Length, Sender, Target));
                    }
                    await Task.WhenAll(tasks);

                    // Clean up consumed bytes from buffer
                    int consumedEnd = lastTerminatorIndex + Terminator.Length;
                    if (consumedEnd >= Buffer.Count) {
                        Buffer.Clear();
                    } else {
                        Buffer.RemoveRange(0, consumedEnd);
                    }
                }
                return Buffer;
            } else {
                var tempBuffer = new List<byte>(Buffer);
                Buffer.Clear();
                Task.Run(() => {
                    Target.HandleReceive(Sender, tempBuffer, ByteArrayType, tempBuffer.Count);
                });

                return Buffer;
            }
        }

        #endregion

        #region Send

        protected internal void StartSending() {
            Task.Factory.StartNew(async () => {
                var options = Parent.Options;
                DateTime Time = DateTime.UtcNow;
                while (!isDisposed && !Closed && Socket.Connected) {
                    if (options.Chunking) {
                        var intervalMs = options.ChunkingIntervalMs;
                        if (intervalMs < 100) intervalMs = 100;
                        await Task.Delay(intervalMs);
                        await ProcessQueue();
                    } else if (options.Fps > 0) {
                        var now = DateTime.UtcNow;
                        var nextFrame = Time.AddMilliseconds(options.Timeout);
                        if (now >= nextFrame) {
                            Time = now;
                            await ProcessQueue();
                        } else {
                            int waitMs = (int)(nextFrame - now).TotalMilliseconds;
                            if (waitMs > 0) await Task.Delay(waitMs);
                        }
                    } else {
                        await ProcessQueue();
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        protected async internal Task ProcessQueue() {
            if (Socket == null || Stream == null || !Socket.Connected || Closed) {
                await Task.Delay(50);
                return;
            }

            if (Interlocked.CompareExchange(ref _IsSending, 1, 0) != 0) {
                await Task.Delay(1);
                return;
            }

            byte[] chunk = null;
            lock (SendQueueRaw) {
                if (SendQueueRaw.Count > 0) {
                    chunk = SendQueueRaw.ToArray();
                    SendQueueRaw.Clear();
                }
            }

            var options = Parent.Options;
            if (chunk == null || chunk.Length == 0) {
                IsSending = false;
                if (!options.Chunking)
                    await _SendSignal.WaitAsync(100);
                return;
            }

#if UNITY
                        MainThread.Run(() => {
                           SendSerializedBytes(chunk);
                           IsSending = false;
                        });
#endif
#if WINDOWS
            try {
                SendSerializedBytes(chunk);
            } finally {
                IsSending = false;
            }
#endif
#if !UNITY && !WINDOWS
            SendSerializedBytes(chunk);
            IsSending = false;
#endif
        }

        //        protected async internal Task ProcessQueue() {
        //            if (Socket != null && Stream != null && (Socket.Connected) && !Closed && !IsSending && SendQueue.Count > 0) {
        //                IsSending = true;
        //                var Items = new List<SendQueueItem>();
        //                while (SendQueue.Count > 0) {
        //                    SendQueueItem Item;
        //                    SendQueue.TryDequeue(out Item);
        //                    if (Item != null)
        //                        Items.Add(Item);
        //                }
        //                if (Items.Count == 0) {
        //                    IsSending = false;
        //                    return;
        //                }
        //                for (int i = 0; i < Items.Count; i++) {
        //                    var item = Items[i];
        //                    if (Socket == null || !Socket.Connected || Closed)
        //                        break;
        //                    if (item != null) {
        //                        bool okay = false;
        //#if UNITY
        //                        MainThread.Run(() => {
        //                            okay = TrySendQueueItem(item).Result;
        //                        });
        //#endif
        //#if WINDOWS
        //                       okay = await Application.Current.Dispatcher.InvokeAsync(async () => { return await TrySendQueueItem(item); }).Result;
        //#endif
        //#if !UNITY && !WINDOWS
        //            okay = await TrySendQueueItem(item);
        //#endif
        //                        if (okay) {
        //                            item.Complete = true;
        //                        }
        //                    }
        //                }
        //                IsSending = false;
        //            } else {
        //                await Task.Delay(1);
        //            }
        //        }

        protected internal bool SendSerializedBytes(byte[] SerializedBytes) {
            //return await Task.Run(async () => {
            //});
            var stream = Stream;
            if (isDisposed || stream == null || Closed) return false;
            var options = Parent.Options;
            bool useSsl = SSL;
            int mtu = NIC.MTU;
            bool sentSuccessful = false;
            try {
                if (mtu == -1 || SerializedBytes.Length < mtu && SerializedBytes.Length < 65535) {
                    // Object smaller than MTU.
                    byte[] ProcessedBytes = SerializedBytes;
                    int totalBytes = ProcessedBytes.Length;

                    // If upload buffering is disabled or UploadBufferSize is invalid, write whole buffer at once
                    if (!options.isUploadBuffered || options.UploadBufferSize <= 0) {
                        if (useSsl) {
                            SslStream.Write(ProcessedBytes, 0, totalBytes);
                        } else {
                            stream.Write(ProcessedBytes, 0, totalBytes);
                        }
                        Parent.InvokeInternalSentByteCounter(this, totalBytes);
                    } else {
                        int chunkUnit = Math.Max(1, options.UploadBufferSize);

                        for (int offset = 0; offset < totalBytes; offset += chunkUnit) {
                            if (Socket is null || stream is null || !Socket.Connected || Closed || (useSsl && SslStream is null)) {
                                SerializedBytes = null;
                                ProcessedBytes = null;
                                if (!Closed) Parent.CloseConnection(this);
                                return false;
                            }

                            long diff = ((TotalBytesSent - _TotalBytesSent_l)) - options.MaximumUploadBytesPerSecond;
                            if (diff > 0) {
                                var now = DateTime.UtcNow;
                                var timeDiff = now - LastSendFrame;
                                _SentBytesPerSecond = (int)(TotalBytesSent - _TotalBytesSent_l);
                                _TotalBytesSent_l = TotalBytesSent;
                                LastSendFrame = now;
                                if (timeDiff > OneSecond) {
                                    Thread.Sleep((int)(options.MaximumUploadBytesPerSecond / (diff * 10)));
                                }

                            }

                            int chunkSize = Math.Min(chunkUnit, totalBytes - offset);
                            if (useSsl) {
                                SslStream.Write(ProcessedBytes, offset, chunkSize);
                            } else {
                                stream.Write(ProcessedBytes, offset, chunkSize);
                            }

                            Interlocked.Add(ref _TotalBytesSent, chunkSize);
                            Parent.InvokeInternalSentByteCounter(this, chunkSize);
                        }
                    }
                    sentSuccessful = true;
                    //if (!Globals.IgnoreLoggedTypes.Contains(type)) {
                    Parent.InvokeInternalSendEvent(this, ByteArrayType, "[CHUNK]", ProcessedBytes.Length);
                    Parent.InvokeOnSent(new SentEventArgs(this.Parent, this, ByteArrayType, ProcessedBytes.Length));
                //}

            } else if (SerializedBytes.Length > mtu) {
               // Object larger than MTU, we have to Segment the object.
               Parent.SendSegmented(this, SerializedBytes);
            }
        } catch (Exception ex) {
                if (Closed) return false;
                var Reason = ex.Interpret();
                if (Reason.ShouldLogReason()) {
                    Parent.InvokeOnError(this, ex);
                }
                Parent.CloseConnection(this, Reason);
            }
            return sentSuccessful;
        }
        protected internal async Task<bool> TrySendQueueItem(SendQueueItem Item) {
            if (isDisposed || Stream == null || Closed || Item.Complete) return false;
            bool sentSuccessful = false;
            try {
                byte[] SerializedBytes = default;
                Wrapper wrapped = default;
                Type type = default;
                lock (Item.Object) {
                    type = Item.Object.GetType();
                    wrapped = new Wrapper(Item.Object, Parent);
                    SerializedBytes = Parent.Options.Serializer.Serialize(wrapped);
                }
                // if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                //    Object smaller than MTU.
                byte[] ProcessedBytes = Item.Connection.Compressed ? Parent.Options.CompressionAlgorithm.Compress(SerializedBytes).Terminate() : SerializedBytes.Terminate();
                int totalBytes = ProcessedBytes.Length;
                NetworkConnection Client = Item.Connection;
                byte[] SentBytes = new byte[totalBytes];

                if (Parent.Options.isUploadBuffered) {
                    for (int offset = 0, loopTo = ProcessedBytes.Length - 1; Parent.Options.UploadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += Parent.Options.UploadBufferSize) {
                        if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                            SerializedBytes = null;
                            ProcessedBytes = null;
                            wrapped = null;
                            if (!Closed) Parent.CloseConnection(this);
                            return false;
                        }
                        int chunkSize = Math.Min(Parent.Options.UploadBufferSize, totalBytes - offset);
                        if (SSL) {
                            await SslStream.WriteAsync(ProcessedBytes, offset, chunkSize);
                        } else {
                            await Stream.WriteAsync(ProcessedBytes, offset, chunkSize);
                        }
                        Parent.InvokeInternalSentByteCounter(Item.Connection, chunkSize);
                    }
                } else {
                    if (SSL) {
                        await SslStream.WriteAsync(ProcessedBytes, 0, ProcessedBytes.Length);
                    } else {
                        await Stream.WriteAsync(ProcessedBytes, 0, ProcessedBytes.Length);
                    }
                    Parent.InvokeInternalSentByteCounter(Item.Connection, ProcessedBytes.Length);
                }
                sentSuccessful = true;
                //if (!Globals.IgnoreLoggedTypes.Contains(type)) {
                Parent.InvokeInternalSendEvent(Item.Connection, type, Item.Object, ProcessedBytes.Length);
                Parent.InvokeOnSent(new SentEventArgs(this.Parent, Item.Connection, type, ProcessedBytes.Length));
                //}

                //} else if (SerializedBytes.Length > NIC.MTU) {
                //    // Object larger than MTU, we have to Segment the object.
                //    Item.Connection.Parent.SendSegmented(Item.Connection, SerializedBytes);
                // }
            } catch (Exception ex) {
                if (Closed) return false;
                var Reason = ex.Interpret();
                if (Reason.ShouldLogReason()) {
                    Parent.InvokeOnError(this, ex);
                }
                Parent.CloseConnection(this, Reason);
            }
            return sentSuccessful;
        }
        public void Send(object Obj) {
            if (Parent is TcpServer Server) {
                if (Server is null) return;
                Server.Send(this, Obj);
            } else if (Parent is UdpServer udpServer) {
                foreach (var kvp in udpServer.Clients) {
                    if (kvp.Value.ID == this.ID) {
                        udpServer.SendTo(kvp.Value, Obj);
                        break;
                    }
                }
            } else if (Parent is TcpClient Client) {
                Client.Send(Obj);
            } else if (Parent is UdpClient udpClient) {
                udpClient.Send(Obj);
            }
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Recipient"></param>
        /// <param name="Obj"></param>
        public void Send(Identifier Recipient, object Obj) {
            if (!IsServer) {
                if (Parent is TcpClient Client) {
                    if (ID == default) {
                        Client.InvokeOnError(this, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                                          "     ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                          "     Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                    } else {
                        Client.Send(Recipient, Obj);
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Set metadata for the connection serialized with Json.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> to retain private metadata only on server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <paramref name="value">Metadata Value.</paramref>
        /// <paramref name="Private"><see langword="true"/> to keep the metadata only on server; <see langword="false"/> shares with all peers.</paramref>
        /// <paramref name="Restricted"><see langword="true"/> to restrict the client from updating the metadata value.</paramref>
        /// </summary>
        public void SetMetaData(string key, string value, bool Private = false, bool Restricted = false) {
            if (Parent == null) return;
            if (string.IsNullOrEmpty(key)) return;
            if (Parent is TcpServer server) {
                if (Restricted && !server.RestrictedMetadataKeys.Contains(key.ToLower()))
                    server.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(server, key.ToLower(), value, Private);
                //Identity.SetMetaData(server, key.ToLower(), value, Private);
            } else if (Parent is UdpServer udpServer) {
                if (Restricted && !udpServer.RestrictedMetadataKeys.Contains(key.ToLower()))
                    udpServer.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(udpServer, key.ToLower(), value, Private);
            } else if (_parentTypeName == "WebSocketServer") {
                // Use reflection to locate the type and members
                var wsServerType = Parent.GetType();
                var restrictedKeysProp = wsServerType.GetProperty("RestrictedMetadataKeys");

                if (restrictedKeysProp != null) {
                    var restrictedKeys = restrictedKeysProp.GetValue(Parent) as List<string>;
                    if (restrictedKeys != null && Restricted) {
                        var containsMethod = restrictedKeys.GetType().GetMethod("Contains");
                        var addMethod = restrictedKeys.GetType().GetMethod("Add");
                        bool contains = false;
                        if (containsMethod != null)
                            contains = (bool)containsMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                        if (!contains && addMethod != null)
                            addMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                    }
                }
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(Parent, key.ToLower(), value, Private);
                //Identity.SetMetaData(Parent, key.ToLower(), value, Private);
            } else if (Parent is TcpClient client) {
                client.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            } else if (Parent is UdpClient udpClient) {
                udpClient.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            } else if (_parentTypeName == "WebSocketClient") {
                // Use reflection to resolve and invoke Send on Parent
                var sendMethod = Parent.GetType().GetMethod("Send", new[] { typeof(object) });
                if (sendMethod != null) {
                    sendMethod.Invoke(Parent, new object[] { new MetadataKeyValue() { Key = key.ToLower(), Value = value } });
                } else {
                    Parent.InvokeOnError(this, new MissingMethodException("Send method not found on WebSocketClient."));
                }
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection.
        /// <para>Can only be called from server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <returns>Value as string.</returns>
        /// </summary>
        public async Task<string> GetMetaData(string key, bool Private = false, bool WaitForValueIfNull = true) {
            if (Identity == null && !WaitForValueIfNull) {
                return default;
            } else if (Identity == null && WaitForValueIfNull) {
                while (Identity == null) {
                    await Task.Delay(50);
                }
            }
            return await Identity.GetMetaData(key, Private, WaitForValueIfNull);
        }

        public NetworkConnection(ISocket Parent, Socket Socket) {
            _Parent = Parent;
            _parentTypeName = Parent.GetType().Name;
            _isWebSocket = _parentTypeName == "WebSocketClient" || _parentTypeName == "WebSocketServer";
            if (Parent is TcpClient) {
                SubscribePeerUpdate();
                IsServer = false;
            } else if (Parent is UdpClient) {
                // UdpClient manages its own PeerUpdate subscriptions.
                IsServer = false;
            } else {
                IsServer = true;
            }
            if (Socket != null) {
                _Socket = Socket;
                EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
            }
        }
    }
}