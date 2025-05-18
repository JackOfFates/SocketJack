using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking.P2P;
using SocketJack.Serialization;

namespace SocketJack.Networking.Shared {

    /// <summary>
    /// Base class for the Tcp Client and Server.
    /// </summary>
    /// <remarks></remarks>
    public abstract class TcpBase : IDisposable {

        #region Events
        public event OnDisconnectedEventHandler OnDisconnected;
        public delegate void OnDisconnectedEventHandler(DisconnectedEventArgs e);

        public event OnReceiveEventHandler OnReceive;
        public delegate void OnReceiveEventHandler(ref IReceivedEventArgs e);

        public event OnSentEventHandler OnSent;

        public delegate void OnSentEventHandler(SentEventArgs e);
        public event OnErrorEventHandler OnError;
        public delegate void OnErrorEventHandler(ErrorEventArgs e);

        public event OnSerializationErrorEventHandler OnSerializationError;
        public delegate void OnSerializationErrorEventHandler(ErrorEventArgs e);

        public event OnDeserializationErrorEventHandler OnDeserializationError;
        public delegate void OnDeserializationErrorEventHandler(ErrorEventArgs e);

        public event PeerConnectionRequestEventHandler PeerConnectionRequest;
        public delegate void PeerConnectionRequestEventHandler(object sender, PeerServer Server);

        public event PeerServerShutdownEventHandler PeerServerShutdown;
        public delegate void PeerServerShutdownEventHandler(object sender, PeerServer Server);

        public event BytesPerSecondUpdateEventHandler BytesPerSecondUpdate;
        public delegate void BytesPerSecondUpdateEventHandler(int ReceivedPerSecond, int SentPerSecond);

        public event LogOutputEventHandler LogOutput;
        public delegate void LogOutputEventHandler(string text);

        protected internal event PeerRefusedConnectionEventHandler PeerRefusedConnection;
        protected internal delegate void PeerRefusedConnectionEventHandler(object sender, ConnectionRefusedArgs e);

        protected internal event PeerUpdateEventHandler PeerUpdate;
        protected internal delegate void PeerUpdateEventHandler(object sender, PeerIdentification RemotePeer);

        protected internal event InternalPeerRedirectEventHandler InternalPeerRedirect;
        protected internal delegate void InternalPeerRedirectEventHandler(PeerIdentification Recipient, PeerIdentification Sender, object Obj, int BytesReceived);

        protected internal event InternalReceiveEventEventHandler InternalReceiveEvent;
        protected internal delegate void InternalReceiveEventEventHandler(ConnectedClient Connection, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(ConnectedClient Connection, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(ConnectedClient Connection, int BytesSent);

        protected internal event InternalSendEventEventHandler InternalSendEvent;
        protected internal delegate void InternalSendEventEventHandler(ConnectedClient Connection, Type objType, object obj, int BytesSent);


        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if(e.Client != BaseConnection) OnDisconnected?.Invoke(e);
        }
        #endregion

        #region Internal

        protected internal ConcurrentDictionary<Guid, byte[]> Buffers = new ConcurrentDictionary<Guid, byte[]>();
        protected internal ConcurrentDictionary<string, PeerIdentification> Peers = new ConcurrentDictionary<string, PeerIdentification>();
        protected internal ConcurrentDictionary<Guid, ConnectedClient> _ConnectedClients = new ConcurrentDictionary<Guid, ConnectedClient>();
        protected internal Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<IReceivedEventArgs>>>();
        protected internal long SentBytesCounter = 0L;
        protected internal long ReceivedBytesCounter = 0L;
        protected internal bool isDisposed = false;
        private static TimeSpan OneSecond = TimeSpan.FromSeconds(1L);

        private void HandleReceive(ConnectedClient Connection, object obj, Type objType, int Length) {
            if (ReferenceEquals(objType, typeof(PingObj)))
                return;
            switch (objType) {
                case var @case when @case == typeof(PeerIdentification): {
                        if (Options.PeerToPeerEnabled)
                            PeerUpdate?.Invoke(this, (PeerIdentification)obj);
                        break;
                    }
                case var case1 when case1 == typeof(PeerServer): {
                        PeerServer pServer = (PeerServer)obj;
                        if (Options.PeerToPeerEnabled) {
                            if (pServer.Shutdown) {
                                PeerServerShutdown?.Invoke(this, pServer);
                            } else {
                                PeerConnectionRequest?.Invoke(this, pServer);
                            }
                        } else { Send(BaseConnection, new ConnectionRefusedArgs("Peer to peer is disabled on the client or server.", pServer)); }
                        break;
                    }
                case var case2 when case2 == typeof(ConnectionRefusedArgs): {
                        ConnectionRefusedArgs refusedArgs = (ConnectionRefusedArgs)obj;
                        PeerRefusedConnection?.Invoke(this, refusedArgs);
                        break;
                    }
                case var case3 when case3 == typeof(Segment): {
                        Segment s = (Segment)obj;
                        if (Segment.Cache.ContainsKey(s.SID)) {
                            Segment.Cache[s.SID].Add(s);
                            if (Segment.SegmentComplete(s)) {
                                byte[] RebuiltSegments = Segment.Rebuild(s);
                                try {
                                    var segObj = ((ObjectWrapper)Options.Serializer.Deserialize(RebuiltSegments)).Unwrap(this);
                                    var segObjType = segObj.GetType();
                                    var e = new ReceivedEventArgs<Segment>(this, Connection, segObj, RebuiltSegments.Count());
                                    InternalReceiveEvent?.Invoke(Connection, segObjType, segObj, RebuiltSegments.Count());
                                    IReceivedEventArgs args = e;
                                    OnReceive?.Invoke(ref args);
                                    InvokeAllCallbacks(e);
                                } catch (Exception) {
                                    OnDeserializationError?.Invoke(new ErrorEventArgs(this, Connection, new Exception("Failed to deserialize segment.")));
                                }
                            }
                        } else {
                            Segment.Cache.Add(s.SID, new List<Segment>() { s });
                        }

                        break;
                    }
                case var case4 when case4 == typeof(PeerRedirect): {
                        PeerRedirect redirect = (PeerRedirect)obj;
                        var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);
                        if (BaseConnection.IsServer) {
                            IReceivedEventArgs args = e;
                            OnReceive?.Invoke(ref args);
                        }
                        if (!e.CancelPeerRedirect) {
                            if (Options.LogReceiveEvents)
                                InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                            InternalPeerRedirect?.Invoke(redirect.Recipient, Connection.RemoteIdentity, redirect.StripIPs(), Length);
                        }
                        break;
                    }
                default: {
                        if (Options.LogReceiveEvents)
                            InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(obj.GetType());

                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        receivedEventArgs.Initialize(this, Connection, obj, Length);
                        IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                        OnReceive?.Invoke(ref NonGenericEventArgs);
                        InvokeAllCallbacks(receivedEventArgs);
                        break;
                    }
            }
        }
        protected internal void InvokeAllCallbacks(IReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(e.Type)) {
                var l = TypeCallbacks[e.Type];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
                    l.ElementAt(i).Invoke(e);
                }
            }
        }

        protected internal void InvokeAllCallbacks(Type Type, object obj, IReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(Type)) {
                var l = TypeCallbacks[Type];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
                    l.ElementAt(i).Invoke(e);
                }
            }
        }

        protected internal void InvokeOnReceive(IReceivedEventArgs e) {
            OnReceive?.Invoke(ref e);
        }
        protected internal void InvokeBytesPerSecondUpdate() {
            BytesPerSecondUpdate?.Invoke(BytesPerSecondSent, BytesPerSecondReceived);
        }
        protected internal void InvokeOnError(ConnectedClient Client, Exception e, bool Pause = false) {
            LogFormatAsync("[{0}] {1}{2}{3}", new[] { Name + (Client != null && Client.RemoteIdentity != null ? @"\" + Client.RemoteIdentity.ID.ToUpper() : @"\Null"), e.Message, Environment.NewLine, e.StackTrace });
            if(e.StackTrace != string.Empty) LogAsync(e.StackTrace);
            OnError?.Invoke(new ErrorEventArgs(this, Client, e));
        }
        protected internal void CloseConnection(ConnectedClient Client, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            if (!Client.Closed) {
                Client.CloseClient(this);
            }
        }
        protected internal void Bind(int Port) {
            BaseSocket.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        protected internal void LogAsync(string[] lines) {
            if (Options.Logging) {
                Task.Run(() => {
                    string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                    if (Options.LogToOutput && Debugger.IsAttached)
                        Debug.Write(Output);
                    Console.Write(Output);
                    LogOutput?.Invoke(Output);
                }).ConfigureAwait(false);
            }
        }
        protected internal void LogAsync(string text) {
            LogAsync(new[] { text });
        }
        protected internal void LogFormatAsync(string text, string[] args) {
            LogAsync(new[] { string.Format(text, args) });
        }
        protected internal void LogFormatAsync(string[] lines, string[] args) {
            LogAsync(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
        }

        #endregion

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public virtual string Name { get; set; } = "TcpBase";

        public TcpOptions Options = TcpOptions.DefaultOptions.Clone<TcpOptions>();

        /// <summary>
        /// Not to be confused with RemoteIdentity, InternalID is used for internally identifying the client.
        /// </summary>
        /// <returns></returns>
        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private Guid _InternalID = Guid.NewGuid();

        public int BytesPerSecondSent {
            get {
                return _SentBytesPerSecond;
            }
        }
        protected internal int _SentBytesPerSecond = 0;
        public int BytesPerSecondReceived {
            get {
                return _ReceivedBytesPerSecond;
            }
        }
        protected internal int _ReceivedBytesPerSecond = 0;

        /// <summary>
        /// True if sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return BaseConnection != null && BaseConnection.IsReceiving || BaseConnection.IsSending;
            }
        }

        /// <summary>
        /// The base connection for identification.
        /// </summary>
        public ConnectedClient BaseConnection { get; set; }

        /// <summary>
        /// The main socket that listens to all requests. Using the TCP protocol.
        /// </summary>
        /// <remarks>Uses AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp</remarks>
        public Socket BaseSocket { get; set; }

        /// <summary>
        /// The bound port.
        /// </summary>
        /// <value>Integer</value>
        /// <returns></returns>
        /// <remarks></remarks>
        public virtual int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }
        protected internal int _Port;

        /// <summary>
        /// Returns the bound IPEndPoint.
        /// </summary>
        /// <value>IPEndPoint</value>
        /// <returns></returns>
        /// <remarks></remarks>
        public IPEndPoint LocalIPEndPoint {
            get {
                return (IPEndPoint)BaseSocket.LocalEndPoint;
            }
        }

        /// <summary>
        /// Current connection State.
        /// </summary>
        /// <returns><see langword="true"/> if Socket.Connected or Socket.Listening; otherwise <see langword="false"/></returns>
        public virtual bool Connected {
            get {
                return BaseSocket != null && BaseSocket.Connected;
            }
        }

        /// <summary>
        /// Polls a socket to see if it is connected.
        /// </summary>
        /// <param name="socket"></param>
        /// <returns><see langword="false"/> if sending or receiving to avoid false positives; otherwise <see langword="true"/> if poll successful.</returns>
        public bool IsConnected(ConnectedClient Client) {
            try {
                if(Client == null || Client.Socket == null) {
                    return false;
                } else if (!Client.Socket.Connected || Client.Closed) {
                    return false;
                } else if (!Active && Client.Socket.Poll(1000, SelectMode.SelectRead) && Client.Socket.Available == 0) {
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

        /// <summary>
        /// True if is Peer to Peer Client or Server instance.
        /// </summary>
        /// <returns></returns>
        public bool PeerToPeerInstance {
            get {
                return _PeerToPeerInstance;
            }
        }
        protected internal bool _PeerToPeerInstance;

        public bool isReceiving { 
            get {
                return BaseConnection != null && BaseConnection.IsReceiving;
            }
        }
        public bool isSending {
            get {
                return BaseConnection != null && BaseConnection.IsSending;
            }
        }

        #endregion

        #region Callbacks

        /// <summary>
        /// <para>Registers a type callback.</para>
        /// <para>Action invoked when type is received.</para>
        /// </summary>
        public void RegisterCallback(Type Type, Action<IReceivedEventArgs> Action) {
            Options.Whitelist.AddType(Type);
            if (TypeCallbacks.ContainsKey(Type)) {
                TypeCallbacks[Type].Add(Action);
            } else {
                var l = new List<Action<IReceivedEventArgs>>() { Action };
                TypeCallbacks.Add(Type, l);
            }
        }

        /// <summary>
        /// <para>Registers a type callback using generic types.</para>
        /// <para>Action of type invoked when type is received.</para>
        /// </summary>
        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            Type Type = typeof(T);
            Options.Whitelist.AddType(Type);
            if (TypeCallbacks.ContainsKey(Type)) {
                TypeCallbacks[Type].Add(e => Action((ReceivedEventArgs<T>)e));
            } else {
                var l = new List<Action<IReceivedEventArgs>>() {
                    e => Action((ReceivedEventArgs<T>)e)
                };
                TypeCallbacks.Add(Type, l);
            }
        }

        /// <summary>
        /// <para>Removes a type callback.</para>
        /// </summary>
        public void RemoveCallback(Type Type, Action<IReceivedEventArgs> Action) {
            if (TypeCallbacks.ContainsKey(Type))
                TypeCallbacks[Type].Remove(Action);
            Options.Whitelist.RemoveType(Type);
        }

        /// <summary>
        /// Removes a type callback.
        /// </summary>
        public void RemoveCallback<T>(Type Type, Action<ReceivedEventArgs<T>> Action) {
            if (TypeCallbacks.ContainsKey(Type)) {
                Action<IReceivedEventArgs> wrappedAction = e => Action((ReceivedEventArgs<T>)e);
                TypeCallbacks[Type].Remove(wrappedAction);
            }
            Options.Whitelist.RemoveType(Type);
        }

        #endregion

        #region Peer to Peer

        protected internal void AddPeer(PeerIdentification RemotePeer) {
            var peersInternal = Peers;
            peersInternal.Add(RemotePeer.ID, RemotePeer);
            Peers = peersInternal;
        }

        protected internal void RemovePeer(PeerIdentification RemotePeer) {
            var peersInternal = Peers;
            peersInternal.Remove( RemotePeer.ID);
            Peers = peersInternal;
        }

        protected internal bool ContainsPeer(PeerIdentification p) {
            if (p is null)
                return false;
            return Peers.ContainsKey(p.ID);
        }

        protected internal void UpdatePeerTag(PeerIdentification p) {
            if(!ContainsPeer(p)) {
                AddPeer(p);
            }
            Peers[p.ID]._Tag = p.Tag;
        }

        #endregion

        #region IDisposable Implementation
        private bool disposedValue;

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    // TODO: dispose managed state (managed objects)
                    Buffers.Clear();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        public void Dispose() {
            // Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
            isDisposed = true;
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

        #region Synchronous Sending
        private static byte[] PrepareSend(byte[] Data) {
            return ByteExtensions.Concat(new[] { Data, ReceiveState.Terminator });
        }
        protected internal void Send(ConnectedClient Client, object Obj) {
            if (Client.Socket == null)
                return;
            if (Client.Socket.Connected)
                Client.SendQueue.Enqueue(new SendState(Obj, Client));
        }

        //protected internal void SendSegmented(ConnectedClient Client, object Obj) {
        //    Task.Run(() => {
        //        byte[] SerializedBytes = Options.Serializer.Serialize(ObjectWrapper.Wrap(Obj, this));
        //        Segment[] SegmentedObject = SerializedBytes.GetSegments();
        //        Parallel.ForEach(SegmentedObject, (s) => {
        //            var state = new SendState(s, Client);
        //            Client.SendQueue.Enqueue(state);
        //        });
        //    });
        //}

        protected internal bool ProcessSendState(ref SendState State) {
            if (isDisposed || BaseConnection == null || BaseConnection.IsSending || BaseConnection.Closed || State.Complete) return false;
            State.Complete = true;
            BaseConnection.IsSending = true;
            bool sentSuccessful = false;
            try {
                ObjectWrapper wrapped = (ObjectWrapper)ObjectWrapper.Wrap(State.Object, this);
                MethodExtensions.TryFuncResult<byte[]> result = MethodExtensions.TryInvoke(() => { return Options.Serializer.Serialize(wrapped); });
                if (result.Success) {
                    byte[] SerializedBytes = Options.Serializer.Serialize(wrapped);
                    var Type = State.Object.GetType();
                    //if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                    // Object smaller than MTU.
                    bool isSegment = ReferenceEquals(Type, typeof(Segment));
                    byte[] TerminatedBytes = State.Connection.Compressed ? Options.CompressionAlgorithm.Compress(PrepareSend(SerializedBytes)) : PrepareSend(SerializedBytes);
                    int totalBytes = TerminatedBytes.Length;
                    ConnectedClient Client = State.Connection;
                    long TotalUploadedBytes = 0L;
                    DateTime UploadStartTime = DateTime.UtcNow;
                    byte[] SentBytes = new byte[totalBytes];

                    try {
                        int offset = 0;

                        while (offset < totalBytes) {
                            if (Client.Closed) {
                                SerializedBytes = null;
                                TerminatedBytes = null;
                                wrapped = null;
                                return false;
                            }
                            if (Options.MaximumUploadMbps > 0) {
                                // Limit upload bandwidth.
                                while (LimitUploadBandwidth(TotalUploadedBytes, UploadStartTime))
                                    Thread.Sleep(1);
                                UploadStartTime = DateTime.UtcNow;
                            }
                            int chunkSize = Math.Min(Options.UploadBufferSize, totalBytes - offset);
                            State.Connection.Socket.Send(TerminatedBytes, offset, chunkSize, SocketFlags.None);
                            InternalSentByteCounter?.Invoke(State.Connection, chunkSize);
                            offset += chunkSize;
                        }
                        sentSuccessful = true;

                        if (!isSegment) {
                            InternalSendEvent?.Invoke(State.Connection, Type, TerminatedBytes, TerminatedBytes.Length);
                            OnSent?.Invoke(new SentEventArgs(this, State.Connection, TerminatedBytes.Length));
                        }
                    } catch (ObjectDisposedException) {
                        CloseConnection(State.Connection, DisconnectionReason.ObjectDisposed);
                    } catch (SocketException ex) {
                        var Reason = DisconnectionReason.Unknown;
                        if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
                            Reason = DisconnectionReason.RemoteSocketClosed;
                        } else if (isDisposed) {
                            Reason = DisconnectionReason.ObjectDisposed;
                        } else if (!IsConnected(State.Connection)) {
                            Reason = DisconnectionReason.LocalSocketClosed;
                        } else if (!NIC.InternetAvailable()) {
                            Reason = DisconnectionReason.InternetNotAvailable;
                        }
                        SerializedBytes = null;
                        TerminatedBytes = null;
                        CloseConnection(State.Connection, Reason);
                    } catch (Exception ex) {
                        InvokeOnError(State.Connection, ex);
                    }
                    //} else if (SerializedBytes.Length > NIC.MTU) {
                    //    // Object larger than MTU, Segment.
                    //    SendSegmented(State.Client, SerializedBytes);
                    //    if (!ReferenceEquals(Type, typeof(Segment)))
                    //        InternalSendEvent?.Invoke(State.Client, Type, SerializedBytes, SerializedBytes.Length);
                    //}
                } else {
                    OnSerializationError?.Invoke(new ErrorEventArgs(this, State.Connection, result.Exception));
                    InvokeOnError(State.Connection, result.Exception);
                }
            } catch (Exception ex) {
                InvokeOnError(State.Connection, ex);
            }
            BaseConnection.IsSending = false;
            return sentSuccessful;
        }
        #endregion

        #region Synchronous Receiving

        internal async Task<ReceiveResult> ReceiveClient(TcpClient Client) {
            BaseConnection.IsReceiving = true;
            ReceiveResult result = await Receive(BaseConnection);
            BaseConnection._DownloadBuffer = result.RemainingBytes;
            BaseConnection.IsReceiving = false;
            return result;
        }

        private async Task<ReceiveResult> NoBufferReceive(ConnectedClient Connection) {
            // Do not buffer to keep up with available bytes since Maximum Mbps is unlimited.
            var state = new ReceiveState() { Client = Connection };

            if (Connection.DownloadBuffer != null)
                state.Buffer = (byte[])Connection.DownloadBuffer.Clone();

            try {
                int BufferSize = Connection.Socket.Available;
                byte[] temp = new byte[BufferSize];
                int bytesRead = Connection.Socket.Receive(temp, 0, BufferSize, SocketFlags.None);
                state.Buffer = state.Buffer == null ? state.Buffer = (byte[])temp.Clone() : state.Buffer.Concat(temp);
                var decompressionResult = MethodExtensions.TryInvoke<byte[]>(() => { if (Connection.Compressed) temp = Options.CompressionAlgorithm.Decompress(temp); });
                if(decompressionResult.Success) {
                    temp = decompressionResult.Result;
                } else {
                    throw decompressionResult.Exception;
                }
                temp = null;
                state.BytesRead += bytesRead;

                InternalReceivedByteCounter?.Invoke(Connection, bytesRead);
                var result = await ProcessReceiveBuffer(state.Buffer);
                foreach (var ProcessedObject in result.Objects) {
                    if (ProcessedObject is null)
                        continue;
                    HandleReceive(Connection, ProcessedObject.Obj, ProcessedObject.Obj.GetType(), (int)ProcessedObject.Length);
                }
                state.Buffer = null;
                return new ReceiveResult(result.remainingBytes);
            } catch (SocketException ex) {
                var Reason = DisconnectionReason.Unknown;
                if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
                    Reason = DisconnectionReason.RemoteSocketClosed;
                } else if (isDisposed) {
                    Reason = DisconnectionReason.ObjectDisposed;
                } else if (!IsConnected(state.Client)) {
                    Reason = DisconnectionReason.LocalSocketClosed;
                } else if (!NIC.InternetAvailable()) {
                    Reason = DisconnectionReason.InternetNotAvailable;
                }
                CloseConnection(state.Client, Reason);
                state.Buffer = null;
                return ReceiveResult.NotAvailable;
            } catch (ObjectDisposedException) {
                CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                return ReceiveResult.NotAvailable;
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
                return ReceiveResult.NotAvailable;
            }
        }

        private async Task<ReceiveResult> BufferedReceive(ConnectedClient Connection) {
            var state = new ReceiveState() { Client = Connection };
            if (Connection.DownloadBuffer != null)
                state.Buffer = (byte[])Connection.DownloadBuffer.Clone();

            DateTime DownloadStartTime = DateTime.UtcNow;
            long TotalDownloadedBytes = 0L;
            int BytesAvailable = Connection.Socket.Available;
            try {
                for (int offset = 0, loopTo = BytesAvailable - 1; Options.DownloadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += Options.DownloadBufferSize) {
                    if (Options.MaximumDownloadMbps > 0) {
                        // Limit download bandwidth.
                        while (LimitDownloadBandwidth(TotalDownloadedBytes, DownloadStartTime))
                            Thread.Sleep(1);
                        DownloadStartTime = DateTime.UtcNow;
                    }
                    if (Connection.Socket is null) {
                        CloseConnection(state.Client);
                        state.Buffer = null;
                        return ReceiveResult.NotAvailable;
                    }

                    int OffsetSize = BytesAvailable - offset;
                    int BufferSize = OffsetSize < Options.DownloadBufferSize ? OffsetSize : Options.DownloadBufferSize;
                    byte[] temp = new byte[BufferSize];
                    int bytesRead = 0;
        
                    try {
                        bytesRead = Connection.Socket.Receive(temp, 0, BufferSize, SocketFlags.None);

                        if (Connection.Compressed) {
                            var decompressionResult = MethodExtensions.TryInvoke(Options.CompressionAlgorithm.Decompress, ref temp);
                            if (decompressionResult.Success) {
                                temp = decompressionResult.Result;
                            } else {
                                temp = null;
                                state.Buffer = null;
                                throw decompressionResult.Exception;
                            }
                        }

                        if (state.Buffer == null) {
                            state.Buffer = (byte[])temp.Clone();
                        } else {
                            state.Buffer = state.Buffer.Concat(temp);
                        }
                    } catch (ObjectDisposedException) {
                        temp = null;
                        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                        return ReceiveResult.NotAvailable;
                    }

                    state.BytesRead += bytesRead;
                    TotalDownloadedBytes += bytesRead;

                    InternalReceivedByteCounter?.Invoke(Connection, bytesRead);
                }
            } catch (SocketException ex) {
                var Reason = DisconnectionReason.Unknown;
                if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
                    Reason = DisconnectionReason.RemoteSocketClosed;
                } else if (isDisposed) {
                    Reason = DisconnectionReason.ObjectDisposed;
                } else if (!NIC.InternetAvailable()) {
                    Reason = DisconnectionReason.InternetNotAvailable;
                } else if (!IsConnected(state.Client)) {
                    Reason = DisconnectionReason.LocalSocketClosed;
                }
                CloseConnection(state.Client, Reason);
                state.Buffer = null;
                return ReceiveResult.NotAvailable;
            } catch (ObjectDisposedException) {
                CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                return ReceiveResult.NotAvailable;
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
                return ReceiveResult.NotAvailable;
            }

            var result = await ProcessReceiveBuffer(state.Buffer);
            foreach (var ProcessedObject in result.Objects) {
                if (ProcessedObject is null)
                    continue;
                HandleReceive(Connection, ProcessedObject.Obj, ProcessedObject.Obj.GetType(), (int)ProcessedObject.Length);
            }
            state.Buffer = null;
            return new ReceiveResult(result.remainingBytes);
        }

        /// <summary>
        /// Receive data from the socket without entering a loop.
        /// </summary>
        /// <param name="Connection"></param>
        /// <returns><see langword="True"/> if successful; <see langword="False"/> if failed to receive.</returns>
        /// <remarks></remarks>
        internal async Task<ReceiveResult> Receive(ConnectedClient Connection) {
            if (Connection == null || Connection.Socket == null)
                return new ReceiveResult();
            try {
                int DataAvailable = Connection.Socket.Available;
                if (Options.MaximumDownloadMbps <= 0) {
                    return await NoBufferReceive(Connection);
                } else if (DataAvailable > 0) {
                    return await BufferedReceive(Connection);
                } else {
                    return ReceiveResult.NotAvailable;
                }
            } catch (SocketException ex) {
                var Reason = DisconnectionReason.Unknown;
                if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
                    Reason = DisconnectionReason.RemoteSocketClosed;
                } else if (isDisposed) {
                    Reason = DisconnectionReason.ObjectDisposed;
                } else if (!NIC.InternetAvailable()) {
                    Reason = DisconnectionReason.InternetNotAvailable;
                } else if (!IsConnected(Connection)) {
                    Reason = DisconnectionReason.LocalSocketClosed;
                }
                CloseConnection(Connection, Reason);
                return ReceiveResult.NotAvailable;
            } catch (ObjectDisposedException) {
                CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                return ReceiveResult.NotAvailable;
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
                return ReceiveResult.NotAvailable;
            }
        }

        private bool LimitDownloadBandwidth(long TotalDownloadedBytes, DateTime DownloadStartTime) {
            if (Options.MaximumDownloadBytesPerSecond <= 0)
                return false;
            var Now = DateTime.UtcNow;
            if (DownloadStartTime - Now >= OneSecond) {
                DownloadStartTime = Now;
                TotalDownloadedBytes = 0L;
            } else {
                var elapsedTime = Now - DownloadStartTime;
                if (TotalDownloadedBytes > Options.MaximumDownloadBytesPerSecond) {
                    int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
                    if (sleepTime > 0)
                        return true;
                }
            }
            return false;
        }
        private bool LimitUploadBandwidth(long TotalUploadedBytes, DateTime UploadStartTime) {
            if (Options.MaximumUploadBytesPerSecond <= 0)
                return false;
            var Now = DateTime.UtcNow;
            if (UploadStartTime - Now >= OneSecond) {
                UploadStartTime = Now;
                TotalUploadedBytes = 0L;
            } else {
                var elapsedTime = Now - UploadStartTime;
                if (TotalUploadedBytes > Options.MaximumUploadBytesPerSecond) {
                    int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
                    if (sleepTime > 0)
                        return true;
                }
            }
            return false;
        }
        private async Task<ReceiveStateResult> ProcessReceiveBuffer(byte[] Buffer) { //(ref ReceiveState state) {
            var Objects = new List<DeserializedObject>();
            var Errors = new List<Exception>();
            int LastIndexOf = 0;
            List<Task> tasks = new List<Task>();
            if (Buffer != null && Buffer.Length > 0) {
                var TerminatorIndices = Buffer.IndexOfAll(ReceiveState.Terminator);
                if (TerminatorIndices.Count > 0) {
                    for (int i = 0, loopTo = TerminatorIndices.Count - 1; i <= loopTo; i++) {
                        int lastIndex = i > 0 ? TerminatorIndices[i - 1] : 0;
                        int Index = TerminatorIndices[i];
                        if(i == TerminatorIndices.Count - 1) 
                            LastIndexOf = Index;
                        
                        tasks.Add(Task.Run(() => {
                            if (Buffer.Length < Index || Buffer.Length < lastIndex)
                                return;
                            int MinusTerminator = lastIndex + ReceiveState.Terminator.Length;
                            byte[] Bytes = Buffer.Part(lastIndex == 0 ? lastIndex : MinusTerminator, Index);
                            try {
                                object verified = VerifyObject((ObjectWrapper)Options.Serializer.Deserialize(Bytes));
                                if(verified != null) {
                                    DeserializedObject obj = new DeserializedObject(verified, Bytes.Length);
                                    Objects.Add(obj);
                                }
                            } catch (Exception ex) {
                                OnDeserializationError?.Invoke(new ErrorEventArgs(this, BaseConnection, ex));
                                Objects.Add(new DeserializedObject(ex, 0));
                                Errors.Add(ex);
                                InvokeOnError(BaseConnection, ex);
                            }
                        }));
                    }
                    await Task.WhenAll(tasks);
                }
            }
            byte[] remainingBytes = null;
            if (LastIndexOf > 0)
                remainingBytes = Buffer.Remove(0, LastIndexOf + ReceiveState.Terminator.Length);
            return new ReceiveStateResult(remainingBytes, Objects, LastIndexOf, Errors);
        }
        private object VerifyObject(ObjectWrapper wrapper) {
            if (wrapper.Data is null)
                return null;
            if (wrapper.Type == "") {
                return null;
            } else if (wrapper.Type == typeof(PingObj).AssemblyQualifiedName) {
                return null;
            } else if (Options.Whitelist.Contains(wrapper.Type) || TcpOptions.DefaultOptions.Whitelist.Contains(wrapper.Type)) {
                return wrapper.Unwrap(this);
            } else {
                Exception ex = new TypeNotAllowedException(wrapper.Type);
                OnDeserializationError?.Invoke(new ErrorEventArgs(this, BaseConnection, ex));
                InvokeOnError(BaseConnection, ex);
                return ex;
            }
        }

        #endregion

        public TcpBase() {
            NIC.Initialize();
        }
        public TcpBase(ISerializer Serializer) {
            Options.Serializer = Serializer;
            NIC.Initialize();
        }
    }
}