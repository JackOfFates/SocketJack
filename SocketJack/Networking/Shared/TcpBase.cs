using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        public delegate void OnReceiveEventHandler(ref ReceivedEventArgs e);

        public event OnSentEventHandler OnSent;

        public delegate void OnSentEventHandler(SentEventArgs e);
        public event OnErrorEventHandler OnError;
        public delegate void OnErrorEventHandler(ErrorEventArgs e);

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
        protected internal delegate void InternalReceiveEventEventHandler(ConnectedClient ConnectedSocket, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(ConnectedClient ConnectedSocket, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(ConnectedClient ConnectedSocket, int BytesSent);

        protected internal event InternalSendEventEventHandler InternalSendEvent;
        protected internal delegate void InternalSendEventEventHandler(ConnectedClient ConnectedSocket, Type objType, object obj, int BytesSent);
        #endregion

        #region Internal
        internal Thread ByteCounterThread;

        protected internal ConcurrentDictionary<Guid, byte[]> Buffers = new ConcurrentDictionary<Guid, byte[]>();
        protected internal ConcurrentDictionary<string, PeerIdentification> Peers = new ConcurrentDictionary<string, PeerIdentification>();
        protected internal ConcurrentDictionary<Guid, ConnectedClient> _ConnectedClients = new ConcurrentDictionary<Guid, ConnectedClient>();
        protected internal Dictionary<Type, List<Action<ReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<ReceivedEventArgs>>>();
        protected internal long SentBytesCounter = 0L;
        protected internal long ReceivedBytesCounter = 0L;
        protected internal bool isDisposed = false;
        private static TimeSpan OneSecond = TimeSpan.FromSeconds(1L);

        protected internal bool isReceiving() {
            return BaseConnection != null && BaseConnection.IsReceiving;
        }

        protected internal bool isSending() {
            return BaseConnection != null && BaseConnection.IsSending;
        }

        private void HandleReceive(ConnectedClient Client, object obj, Type objType, int Length) {
            if (ReferenceEquals(objType, typeof(PingObj)))
                return;
            switch (objType) {
                case var @case when @case == typeof(PeerIdentification): {
                        if (PeerToPeerEnabled)
                            PeerUpdate?.Invoke(this, (PeerIdentification)obj);
                        break;
                    }
                case var case1 when case1 == typeof(PeerServer): {
                        PeerServer pServer = (PeerServer)obj;
                        if (PeerToPeerEnabled) {
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
                                var segObj = ((ObjectWrapper)Serializer.Deserialize(RebuiltSegments)).Unwrap(this);
                                var segObjType = segObj.GetType();
                                var e = new ReceivedEventArgs(this, Client, segObj, RebuiltSegments.Count());
                                InternalReceiveEvent?.Invoke(Client, segObjType, segObj, RebuiltSegments.Count());
                                OnReceive?.Invoke(ref e);
                                InvokeAllCallbacks(e);
                            }
                        } else {
                            Segment.Cache.Add(s.SID, new List<Segment>() { s });
                        }

                        break;
                    }
                case var case4 when case4 == typeof(PeerRedirect): {
                        var e = new ReceivedEventArgs(this, Client, obj, Length);
                        OnReceive?.Invoke(ref e);
                        InvokeAllCallbacks(e);
                        if (!e.CancelPeerRedirect) {
                            PeerRedirect Wrapper = (PeerRedirect)obj;
                            InternalReceiveEvent?.Invoke(Client, objType, obj, Length);
                            InternalPeerRedirect?.Invoke(Wrapper.Recipient, Client.RemoteIdentity, Wrapper, Length);
                        }
                        break;
                    }
                default: {
                        if (LogReceiveEvents)
                            InternalReceiveEvent?.Invoke(Client, objType, obj, Length);
                        var e = new ReceivedEventArgs(this, Client, obj, Length);
                        OnReceive?.Invoke(ref e);
                        InvokeAllCallbacks(e);
                        break;
                    }
            }
        }
        private void InvokeAllCallbacks(ReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(e.type)) {
                var l = TypeCallbacks[e.type];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
                    l.ElementAt(i).Invoke(e);
                }
            }
        }
        private void InvokeAllCallbacks(Type t, ReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(t)) {
                var l = TypeCallbacks[t];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
                    l.ElementAt(i).Invoke(e);
                }
            }
        }
        protected internal void InvokeOnReceive(ReceivedEventArgs e) {
            OnReceive?.Invoke(ref e);
        }
        protected internal void InvokeBytesPerSecondUpdate() {
            BytesPerSecondUpdate?.Invoke(BytesPerSecondSent, BytesPerSecondReceived);
        }
        protected internal void InvokeOnError(ConnectedClient Client, Exception e, bool Pause = false) {
            LogFormatAsync("[{0}] {1}{2}{3}", new[] { Name + (Client != null && Client.RemoteIdentity != null ? @"\" + Client.RemoteIdentity.ID.ToUpper() : "Null"), e.Message, Environment.NewLine, e.StackTrace });
            LogAsync(e.StackTrace);
            OnError?.Invoke(new ErrorEventArgs(this, Client, e));
        }
        protected internal void CloseConnection(ConnectedClient Client, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            if (!Client.Closed) {
                OnDisconnected?.Invoke(new DisconnectedEventArgs(this, Client));
                Client.CloseClient(this);
            }
        }
        protected internal void Bind(int Port) {
            BaseSocket.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        protected internal void LogAsync(string[] lines) {
            if (Logging) {
                Task.Run(() => {
                    string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                    if (LogToOutput && Debugger.IsAttached)
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

        /// <summary>
        /// Types that are allowed to be deserialized.
        /// </summary>
        public WhitelistedTypes Whitelist { get; set; } = DefaultOptions.Whitelist;

        /// <summary>
        /// Serialization protocol for serialization and deserialization.
        /// </summary>
        public ISerializer Serializer {
            get {
                return _Serializer;
            }
            set {
                _Serializer = value;
            }
        } 
        private ISerializer _Serializer = DefaultOptions.DefaultSerializer;

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

        /// <summary>
        /// Output events like OnConnected, OnDisconnected, OnConnectionFailed, OnClientTimedOut, and more to Console and Debug Output Window.
        /// Send and Receive events only logged when LogSendEvents or LogReceiveEvents are set to True.
        /// </summary>
        /// <returns></returns>
        public bool Logging { get; set; } = false;

        /// <summary>
        /// Log sent events to console.
        /// </summary>
        /// <returns></returns>
        public bool LogSendEvents { get; set; } = DefaultOptions.LogSendEvents;

        /// <summary>
        /// <para>Log received events to console.</para>
        /// </summary>
        /// <returns></returns>
        public bool LogReceiveEvents { get; set; } = DefaultOptions.LogReceiveEvents;

        /// <summary>
        /// Update the title of the console window with traffic statistics.
        /// </summary>
        /// <returns></returns>
        public bool UpdateConsoleTitle { get; set; } = false;

        /// <summary>
        /// <para>Turns on or off Peer to Peer functionality.</para>
        /// <para>Required to be set before TcpClient.Connect or TcpServer.StartListening.</para>
        /// </summary>
        /// <returns></returns>
        public bool PeerToPeerEnabled { get; set; } = DefaultOptions.PeerToPeerEnabled;
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
            } catch (SocketException ex) {
                return false;
            } catch (ObjectDisposedException ex) {
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

        /// <summary>
        /// Log to Debug Output Window.
        /// </summary>
        /// <returns></returns>
        public bool LogToOutput { get; set; } = false;

        /// <summary>
        /// Receive buffer size.
        /// <para>Configurable from DefaultOptions.</para>
        /// </summary>
        /// <remarks>Default is 8192 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// <summary>
        public int DownloadBufferSize { get; set; } = DefaultOptions.DownloadBufferSize;

        /// <summary>
        /// Maximum buffer size per connection.
        /// <para>Configurable from DefaultOptions.</para>
        /// </summary>
        /// <remarks>Default is 100MB.</remarks>
        /// <value>Long</value>
        /// <remarks></remarks>
        public int MaximumBufferSize { get; set; } = DefaultOptions.MaximumBufferSize;

        /// <summary>
        /// Maximum receiving bandwidth.
        /// <para>Configurable from DefaultOptions.</para>
        /// </summary>
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 for unlimited.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        public int MaxReceiveMbps {
            get {
                return _MaxReceiveMbps;
            }
            set {
                _MaxReceiveMbps = value;
                MaximumDownloadBytesPerSecond = MaxReceiveMbps * 1024 * 1024 / 8;
            }
        }
        private int _MaxReceiveMbps = DefaultOptions._MaximumDownloadMbps;
        private int MaximumDownloadBytesPerSecond = DefaultOptions.MaximumDownloadBytesPerSecond;

        /// <summary>
        /// Maximum Upload bandwidth.
        /// <para>Configurable from DefaultOptions.</para>
        /// </summary>
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 for unlimited.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        public static int MaximumUploadMbps {
            get {
                return _MaximumUploadMbps;
            }
            set {
                _MaximumUploadMbps = value;
                MaximumUploadBytesPerSecond = MaximumUploadMbps * 1024 * 1024 / 8;
            }
        }
        protected internal static int _MaximumUploadMbps = DefaultOptions._MaximumUploadMbps;
        protected internal static int MaximumUploadBytesPerSecond = DefaultOptions.MaximumUploadBytesPerSecond;

        /// <summary>
        /// Upload buffer size.
        /// </summary>
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// <summary>
        public static int UploadBufferSize { get; set; } = 65536;

        #endregion

        #region Callbacks

        /// <summary>
        /// <para>Registers a type callback.</para>
        /// <para>Action invoked when type is received.</para>
        /// </summary>
        public void RegisterCallback(Type Type, Action<ReceivedEventArgs> Action) {
            Whitelist.AddType(Type);
            if (TypeCallbacks.ContainsKey(Type)) {
                TypeCallbacks[Type].Add(Action);
            } else {
                var l = new List<Action<ReceivedEventArgs>>() { Action };
                TypeCallbacks.Add(Type, l);
            }
        }

        /// <summary>
        /// <para>Removes a type callback.</para>
        /// </summary>
        public void RemoveCallback(Type Type, Action<ReceivedEventArgs> Action) {
            if (TypeCallbacks.ContainsKey(Type))
                TypeCallbacks[Type].Remove(Action);
            Whitelist.RemoveType(Type);
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
        protected internal void SendSegmented(ConnectedClient Client, object Obj) {
            if (Client.Socket.Connected) {
                Task.Run(() => {
                    byte[] Bytes = Serializer.Serialize(ObjectWrapper.Wrap(Obj, this));
                    SendSegmented(Client, Bytes);
                });
            }
        }
        protected internal void SendSegmented(ConnectedClient Client, byte[] SerializedBytes) {
            if (Client.Socket.Connected) {
                Task.Run(() => {
                    Segment[] SegmentedObject = SerializedBytes.GetSegments();

                    for (int i = 0, loopTo = SegmentedObject.Count() - 1; i <= loopTo; i++) {
                        var s = SegmentedObject[i];
                        var state = new SendState(s, Client);
                        Client.SendQueue.Enqueue(state);
                    }

                }).ConfigureAwait(false);
            }
        }
        protected internal bool ProcessSendState(SendState State) {
            if (isDisposed)
                return false;
            if (BaseConnection == null || BaseConnection.IsSending)
                return false;
            bool sentSuccessful = false;
            try {
                ObjectWrapper wrapped = (ObjectWrapper)ObjectWrapper.Wrap(State.Object, this);
                byte[] SerializedBytes = Serializer.Serialize(wrapped);

                var Type = State.Object.GetType();
                //if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                // Object smaller than MTU.
                bool isSegment = ReferenceEquals(Type, typeof(Segment));
                bool isPingObj = ReferenceEquals(Type, typeof(PingObj));
                byte[] TerminatedBytes = PrepareSend(SerializedBytes);
                int BytesToSend = TerminatedBytes.Length;
                ConnectedClient Client = State.Client;

                try {
                    BaseConnection.IsSending = true;
                    for (int offset = 0, loopTo = BytesToSend - 1; UploadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += UploadBufferSize) {
                        if (Client.Closed) {
                            SerializedBytes = null;
                            TerminatedBytes = null;
                            wrapped = null;
                            return false;
                        }
                        //while (LimitUploadBandwidth(ref Client))
                        //    Thread.Sleep(1);
                        int OffsetSize = BytesToSend - offset;
                        int BufferSize = OffsetSize < UploadBufferSize ? OffsetSize : UploadBufferSize;
                        byte[] buffer = new byte[BufferSize];
                        Array.Copy(TerminatedBytes, offset, buffer, 0, BufferSize);
                        int BytesSent = State.Client.Socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
                        InternalSentByteCounter?.Invoke(State.Client, BytesSent);
                    }
                    BaseConnection.IsSending = false;
                    sentSuccessful = true;

                    if (!isSegment) {
                        InternalSendEvent?.Invoke(State.Client, Type, TerminatedBytes, TerminatedBytes.Length);
                        OnSent?.Invoke(new SentEventArgs(this, State.Client, TerminatedBytes.Length));
                    }
                } catch (ObjectDisposedException ex) {
                    CloseConnection(State.Client, DisconnectionReason.ObjectDisposed);
                    BaseConnection.IsSending = false;
                } catch (SocketException ex) {
                    BaseConnection.IsSending = false;

                    var Reason = DisconnectionReason.Unknown;
                    if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
                        Reason = DisconnectionReason.RemoteSocketClosed;
                    } else if (isDisposed) {
                        Reason = DisconnectionReason.ObjectDisposed;
                    } else if (!IsConnected(State.Client)) {
                        Reason = DisconnectionReason.LocalSocketClosed;
                    } else if (!NIC.InternetAvailable()) {
                        Reason = DisconnectionReason.InternetNotAvailable;
                    }
                    SerializedBytes = null;
                    TerminatedBytes = null;
                    CloseConnection(State.Client, Reason);
                }
                //} else if (SerializedBytes.Length > NIC.MTU) {
                //    // Object larger than MTU, Segment.
                //    SendSegmented(State.Client, SerializedBytes);
                //    if (!ReferenceEquals(Type, typeof(Segment)))
                //        InternalSendEvent?.Invoke(State.Client, Type, SerializedBytes, SerializedBytes.Length);
                //}
            } catch (Exception ex) {
                BaseConnection.IsSending = false;
                InvokeOnError(State.Client, ex);
            }
            return sentSuccessful;
        }
        #endregion

        #region Synchronous Receiving

        internal bool ReceiveSyncNoLoop(ref TcpClient Client) {
            var BaseConnection = Client.BaseConnection;
            return ReceiveSyncNoLoop(ref BaseConnection);
        }

        /// <summary>
        /// Receive data from the socket without entering a loop.
        /// </summary>
        /// <param name="Connection"></param>
        /// <returns><see langword="True"/> if successful; <see langword="False"/> if failed to receive.</returns>
        /// <remarks></remarks>
        internal bool ReceiveSyncNoLoop(ref ConnectedClient Connection) {
            if (Connection == null || Connection.Socket == null) 
                return false;
            
            try {
                // Dim CurrentBufferSize As Long = If(BufferSizes.ContainsKey(Client.ID), BufferSizes(Client.ID), 0)
                int BytesAvailable = Connection.Socket.Available;
                if (BytesAvailable > DownloadBufferSize * 32) {
                    /// No buffer required to keep up with bytes available.
                    var state = new ReceiveState() { Client = Connection };
                    try {
                        int BufferSize = BytesAvailable;
                        byte[] temp = new byte[BufferSize];
                        if (Connection.Closed) {
                            temp = null;
                            state.Buffer = null;
                            Connection.IsReceiving = false;
                            return false;
                        }
                        int bytesRead = Connection.Socket.Receive(temp, 0, BufferSize, SocketFlags.None);
                        if (state.Buffer == null) {
                            state.Buffer = (byte[])temp.Clone();
                        } else {
                            state.Buffer = state.Buffer.Concat(temp);
                        }

                        temp = null;
                        state.BytesRead += bytesRead;
                        Connection.TotalDownloadedBytes += bytesRead;

                        InternalReceivedByteCounter?.Invoke(Connection, bytesRead);

                        var result = ProcessReceiveState(ref state);
                        foreach (var ProcessedObject in result.Objects)
                            HandleReceive(Connection, ProcessedObject.Obj, ProcessedObject.Obj.GetType(), (int)ProcessedObject.Length);
                        Connection._DownloadBuffer = (byte[])state.Buffer.Clone();
                        state.Buffer = null;
                        Connection.IsReceiving = false;
                        return true;
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
                        Connection.IsReceiving = false;
                        return false;
                    } catch (ObjectDisposedException ex) {
                        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                        Connection.IsReceiving = false;
                        return false;
                    } catch (Exception ex) {
                        InvokeOnError(Connection, ex);
                        Connection.IsReceiving = false;
                        return false;
                    }
                } else if (BytesAvailable > 0) {
                    /// Bytes available small enough to buffer.
                    Connection.IsReceiving = true;
                    // Dim BufferSize As Integer = BytesAvailable 'If(CurrentBufferSize + BytesAvailable <= MaximumBufferSize, BytesAvailable, Math.Max(MaximumBufferSize - BytesAvailable, 0))
                    var state = new ReceiveState() { Client = Connection };

                    // , .Buffer = If(Client.Buffer Is Nothing OrElse Client.Buffer.Length = 0, New Byte(ReceiveBufferSize - 1) {}, Client.Buffer.Combine(New Byte(BytesAvailable - 1) {}))
                    // If BufferSize = 0 Then
                    // If Not Client.IsProcessingBuffer Then
                    // Task.Run(Sub() HandleBuffer(state, True))
                    // End If
                    // Return False
                    // End If

                    if (Connection.DownloadBuffer != null) {
                        state.Buffer = (byte[])Connection.DownloadBuffer.Clone();
                        Connection._DownloadBuffer = null;
                    }

                    for (int offset = 0, loopTo = BytesAvailable - 1; DownloadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += DownloadBufferSize) {
                        //while (LimitDownloadBandwidth(ref Client))
                        //    Thread.Sleep(1);
                        try {
                            int OffsetSize = BytesAvailable - offset;
                            int BufferSize = OffsetSize < DownloadBufferSize ? OffsetSize : DownloadBufferSize;
                            byte[] temp = new byte[BufferSize];
                            if (Connection.Closed) {
                                temp = null;
                                state.Buffer = null;
                                Connection.IsReceiving = false;
                                return false;
                            }
                            int bytesRead = Connection.Socket.Receive(temp, 0, BufferSize, SocketFlags.None);
                            if(state.Buffer == null) {
                                state.Buffer = (byte[])temp.Clone();
                            } else {
                                state.Buffer = state.Buffer.Concat(temp);
                            }

                            temp = null;
                            state.BytesRead += bytesRead;
                            Connection.TotalDownloadedBytes += bytesRead;

                            InternalReceivedByteCounter?.Invoke(Connection, bytesRead);
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
                            Connection.IsReceiving = false;
                            return false;
                        } catch (ObjectDisposedException ex) {
                            CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
                            Connection.IsReceiving = false;
                            return false;
                        } catch (Exception ex) {
                            InvokeOnError(Connection, ex);
                            Connection.IsReceiving = false;
                            return false;
                        }
                    }
                    var result = ProcessReceiveState(ref state);
                    foreach (var ProcessedObject in result.Objects)
                        HandleReceive(Connection, ProcessedObject.Obj, ProcessedObject.Obj.GetType(), (int)ProcessedObject.Length);
                    Connection._DownloadBuffer = (byte[])state.Buffer.Clone();
                    state.Buffer = null;
                    Connection.IsReceiving = false;
                    return true;
                } else {
                    Connection.IsReceiving = false;
                    return false;
                }
            } catch (SocketException ex) {
                CloseConnection(Connection);
                Connection.IsReceiving = false;
                return false;
            } catch (ObjectDisposedException ex) {
                Connection.IsReceiving = false;
                return false;
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
                Connection.IsReceiving = false;
                return false;
            }
        }

        private bool LimitDownloadBandwidth(ref ConnectedClient Client) {
            if (MaximumDownloadBytesPerSecond <= 0)
                return false;
            var Now = DateTime.UtcNow;
            if (Client.DownloadStartTime - Now >= OneSecond) {
                Client.DownloadStartTime = Now;
                Client.TotalDownloadedBytes = 0L;
            } else {
                var elapsedTime = Now - Client.DownloadStartTime;
                if (Client.TotalDownloadedBytes > MaximumDownloadBytesPerSecond) {
                    int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
                    if (sleepTime > 0)
                        return true;
                }
            }
            return false;
        }
        private bool LimitUploadBandwidth(ref ConnectedClient Client) {
            if (MaximumUploadBytesPerSecond <= 0)
                return false;
            var Now = DateTime.UtcNow;
            if (Client.UploadStartTime - Now >= OneSecond) {
                Client.UploadStartTime = Now;
                Client.TotalUploadedBytes = 0L;
            } else {
                var elapsedTime = Now - Client.UploadStartTime;
                if (Client.TotalUploadedBytes > MaximumUploadBytesPerSecond) {
                    int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
                    if (sleepTime > 0)
                        return true;
                }
            }
            return false;
        }
        private ReceiveStateResult ProcessReceiveState(ref ReceiveState state) { // , TerminatorIndices As List(Of Integer)
            var Objects = new List<DeserializedObject>();
            var Errors = new List<Exception>();
            int StartIndex = 0;
            if (state.Buffer != null && state.Buffer.Length > 0) {
                var TerminatorIndices = state.Buffer.IndexOfAll(ReceiveState.Terminator);
                if (TerminatorIndices.Count > 0) {
                    foreach (var Index in TerminatorIndices) {
                        if (state.Buffer.Length < Index | state.Buffer.Length < StartIndex)
                            break;
                        int MinusTerminator = StartIndex + ReceiveState.Terminator.Length;
                        byte[] Bytes = state.Buffer.Part(StartIndex == 0 ? StartIndex : MinusTerminator, Index);
                        StartIndex = Index;
                        try {
                            ObjectWrapper wrapper = (ObjectWrapper)Serializer.Deserialize(Bytes);
                            if (Whitelist.Contains(wrapper.Type)) {
                                var obj = wrapper.Unwrap(this);
                                Objects.Add(new DeserializedObject(obj, Bytes.Length));
                            } else {
                                throw new TypeNotAllowedException(wrapper.Type);
                            }
                        } catch (Exception ex) {
                            Errors.Add(ex);
                        }
                    }
                }
            }
            if (StartIndex > 0)
                state.Buffer = state.Buffer.Remove(0, StartIndex + ReceiveState.Terminator.Length);
            return new ReceiveStateResult(state, Objects, StartIndex, Errors);
        }

        #endregion

        public TcpBase() {
            NIC.Initialize();
        }
        public TcpBase(ISerializer Protocol) {
            this.Serializer = Protocol;
            NIC.Initialize();
        }
    }
}