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

        public event OnReceiveEventHandler OnReceive;
        public delegate void OnReceiveEventHandler(ref IReceivedEventArgs e);

        public event OnSentEventHandler OnSent;

        public delegate void OnSentEventHandler(SentEventArgs e);
        public event OnErrorEventHandler OnError;
        public delegate void OnErrorEventHandler(ErrorEventArgs e);

        public event PeerConnectionRequestEventHandler PeerConnectionRequest;
        public delegate void PeerConnectionRequestEventHandler(object sender, PeerServer Server);

        protected internal event InternalPeerConnectionRequestEventHandler Internal_PeerConnectionRequest;
        protected internal delegate void InternalPeerConnectionRequestEventHandler(object sender, ref PeerServer Server);

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
        protected internal delegate void InternalReceiveEventEventHandler(TcpConnection Connection, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(TcpConnection Connection, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(TcpConnection Connection, int BytesSent);

        protected internal event InternalSendEventEventHandler InternalSendEvent;
        protected internal delegate void InternalSendEventEventHandler(TcpConnection Connection, Type objType, object obj, int BytesSent);

        protected internal void InvokeInternalReceivedByteCounter(TcpConnection Connection, int BytesReceived) {
            InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
        }
        protected internal void InvokeInternalSentByteCounter(TcpConnection connection, int chunkSize) {
            InternalSentByteCounter?.Invoke(connection, chunkSize);
        }
        protected internal void InvokeInternalSendEvent(TcpConnection connection, Type type, object @object, int length) {
            InternalSendEvent?.Invoke(connection, type, @object, length);
        }
        protected internal void InvokeOnSent(SentEventArgs sentEventArgs) {
            OnSent?.Invoke(sentEventArgs);
        }

        #endregion

        #region Internal

        protected internal ConcurrentDictionary<Guid, byte[]> Buffers = new ConcurrentDictionary<Guid, byte[]>();
        protected internal ConcurrentDictionary<string, PeerIdentification> Peers = new ConcurrentDictionary<string, PeerIdentification>();
        protected internal Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<IReceivedEventArgs>>>();

        protected internal void HandleReceive(TcpConnection Connection, object obj, Type objType, int Length) {
            if (ReferenceEquals(objType, typeof(PingObject)))
                return;
            switch (objType) {
                case var @case when @case == typeof(PeerIdentification): {
                        if (Options.PeerToPeerEnabled)
                            PeerUpdate?.Invoke(this, (PeerIdentification)obj);
                        break;
                    }
                case var @case when @case == typeof(PeerIdentification[]): {
                        if (Options.PeerToPeerEnabled) {
                            var PeerIDs = (PeerIdentification[])obj;
                            foreach (var item in PeerIDs) {
                                PeerUpdate?.Invoke(this, item);
                            }
                        }
                            
                        break;
                    }
                case var case1 when case1 == typeof(PeerServer): {
                        PeerServer pServer = (PeerServer)obj;
                        if (Options.PeerToPeerEnabled) {
                            if (pServer.Shutdown) {
                                PeerServerShutdown?.Invoke(this, pServer);
                            } else {
                                Internal_PeerConnectionRequest?.Invoke(this, ref pServer);
                                PeerConnectionRequest?.Invoke(this, pServer);
                            }
                        } else { Send(this.Connection, new ConnectionRefusedArgs("Peer to peer is disabled on the client or server.", pServer)); }
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
                                    var segObj = ((Wrapper)Options.Serializer.Deserialize(RebuiltSegments)).Unwrap(this);
                                    var segObjType = segObj.GetType();
                                    var e = new ReceivedEventArgs<Segment>(this, Connection, segObj, RebuiltSegments.Count());
                                    InternalReceiveEvent?.Invoke(Connection, segObjType, segObj, RebuiltSegments.Count());
                                    IReceivedEventArgs args = e;
                                    OnReceive?.Invoke(ref args);
                                    InvokeAllCallbacks(e);
                                } catch (Exception) {
                                    InvokeOnError(Connection, new Exception("Failed to deserialize segment."));
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
                        if (this.Connection.IsServer) {
                            IReceivedEventArgs args = e;
                            OnReceive?.Invoke(ref args);
                        }
                        if (!e.CancelPeerRedirect) {
                            if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
                                InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                            InternalPeerRedirect?.Invoke(redirect.Recipient, Connection.RemoteIdentity, redirect.StripIPs(), Length);
                        }
                        break;
                    }
                default: {
                        if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
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
        protected internal void InvokeBytesPerSecondUpdate(TcpConnection Connection) {
            BytesPerSecondUpdate?.Invoke(Connection.BytesPerSecondSent, Connection.BytesPerSecondReceived);
        }
        protected internal void InvokeOnError(TcpConnection Connection, Exception e, bool Pause = false) {
            LogFormat("[{0}] ERROR: {1}", new[] { Name + (Connection != null && Connection.RemoteIdentity != null ? @"\" + Connection.RemoteIdentity.ID.ToUpper() : @"\Null"), e.Message});
            if(e.StackTrace != string.Empty) LogAsync(e.StackTrace);
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            if(e.InnerException != null) InvokeOnError(Connection, e.InnerException, Pause);
        }
        protected internal void CloseConnection(TcpConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            if (!Connection.Closed) {
                Connection.Close(this);
            }
        }
        protected internal void Bind(int Port) {
            Socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        protected internal void Log(string[] lines) {
            if (Options.Logging) {
                string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (Options.LogToOutput && Debugger.IsAttached)
                    Debug.Write(Output);
                Console.Write(Output);
                LogOutput?.Invoke(Output);
            }
        }
        protected internal void Log(string text) {
            Log(new[] { text });
        }
        protected internal void LogFormat(string text, string[] args) {
            Log(new[] { string.Format(text, args) });
        }
        protected internal void LogFormat(string[] lines, string[] args) {
            Log(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
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
        protected internal bool ConnectionValid() {
            return Connection.isConnectionValid();
        }
        //protected internal void StartByteCounter() {
        //    StartByteCounter(Connection);
        //}
        //protected internal void StartByteCounter(TcpConnection Connection) {
        //    Task.Run(() => {
        //        while (Connected) {
        //            Connection._SentBytesPerSecond = Connection.SentBytesCounter;
        //            Connection._ReceivedBytesPerSecond = Connection.ReceivedBytesCounter;
        //            Connection.SentBytesCounter = 0;
        //            Connection.ReceivedBytesCounter = 0;
        //            InvokeBytesPerSecondUpdate(Connection);
        //            Thread.Sleep(1000);
        //        }
        //    });
        //}
        protected internal void Send(TcpConnection Connection, object Obj) {
            if (Connection.Socket == null)
                return;
            if (Connection.Socket.Connected)
                Connection.SendQueue.Enqueue(new SendQueueItem(Obj, Connection));
        }

        protected internal void StartReceiving() {
            Connection.StartReceiving();
        }

        protected internal void StartSending() {
            Connection.StartSending();
        }

        protected internal void StartConnectionTester() {
            Connection.StartConnectionTester();
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

        /// <summary>
        /// True if sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return Connection != null && Connection.Active;
            }
        }

        /// <summary>
        /// The base connection for identification.
        /// </summary>
        public TcpConnection Connection { get; set; }

        /// <summary>
        /// The main socket that listens to all requests. Using the TCP protocol.
        /// </summary>
        /// <remarks>Uses AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp</remarks>
        public Socket Socket { get; set; }

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
                return (IPEndPoint)Socket.LocalEndPoint;
            }
        }

        /// <summary>
        /// Current connection State.
        /// </summary>
        /// <returns><see langword="true"/> if Socket.Connected or Socket.Listening; otherwise <see langword="false"/></returns>
        public virtual bool Connected {
            get {
                return Socket != null && Socket.Connected;
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
                return Connection != null && Connection.IsReceiving;
            }
        }
        public bool isSending {
            get {
                return Connection != null && Connection.IsSending;
            }
        }

        /// <summary>
        /// Host name for SSL connections.
        /// <para>Required.</para>
        /// </summary>
        public string SslTargetHost { get; set; }

        #endregion

        #region Callbacks

        /// <summary>
        /// <para>Registers a type callback.</para>
        /// <para>Action invoked when type is received.</para>
        /// </summary>
        public void RegisterCallback(Type Type, Action<IReceivedEventArgs> Action) {
            Options.Whitelist.Add(Type);
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
            Options.Whitelist.Add(Type);
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
            Options.Whitelist.Remove(Type);
        }

        /// <summary>
        /// Removes a type callback.
        /// </summary>
        public void RemoveCallback<T>(Type Type, Action<ReceivedEventArgs<T>> Action) {
            if (TypeCallbacks.ContainsKey(Type)) {
                Action<IReceivedEventArgs> wrappedAction = e => Action((ReceivedEventArgs<T>)e);
                TypeCallbacks[Type].Remove(wrappedAction);
            }
            Options.Whitelist.Remove(Type);
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
        protected internal bool isDisposed = false;

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

        protected internal void SendSegmented(TcpConnection Client, object Obj) {
            Task.Run(() => {
                byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
                Segment[] SegmentedObject = SerializedBytes.GetSegments();
                Parallel.ForEach(SegmentedObject, (s) => {
                    var state = new SendQueueItem(s, Client);
                    Client.SendQueue.Enqueue(state);
                });
            });
        }

        //#region Synchronous Receiving
        //internal async Task<ParseResult> ReceiveClient(TcpClient Client) {
        //    if (BaseConnection.IsReceiving) return null;
        //    BaseConnection.IsReceiving = true;
        //    ParseResult result = await Receive(BaseConnection);
        //    Client.BaseConnection._DownloadBuffer = result.remainingBytes;
        //    BaseConnection.IsReceiving = false;
        //    return result;
        //}

        //private async Task<ParseResult> NoBufferReceive(ConnectedClient Connection) {

        //    if (Connection.Socket is null) {
        //        CloseConnection(Connection);
        //        return null;
        //    }

        //    try {
        //        int AllBytesAvailable = Connection.Socket.Available;
        //        var temp = new byte[AllBytesAvailable];
        //        int bytesRead = Connection.Socket.Receive(temp, 0, AllBytesAvailable, SocketFlags.None);

        //        InternalReceivedByteCounter?.Invoke(Connection, bytesRead);
        //        var result = await ParseBuffer(Connection, temp);
        //        temp = null;
        //        return result;
        //    } catch (SocketException ex) {
        //        var Reason = DisconnectionReason.Unknown;
        //        if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
        //            Reason = DisconnectionReason.RemoteSocketClosed;
        //        } else if (isDisposed) {
        //            Reason = DisconnectionReason.ObjectDisposed;
        //        } else if (!IsConnected(Connection)) {
        //            Reason = DisconnectionReason.LocalSocketClosed;
        //        } else if (!NIC.InternetAvailable()) {
        //            Reason = DisconnectionReason.InternetNotAvailable;
        //        }
        //        CloseConnection(Connection, Reason);
        //        return null;
        //    } catch (NullReferenceException) {
        //        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //        return null;
        //    } catch (ObjectDisposedException) {
        //        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //        return null;
        //    } catch (Exception ex) {
        //        InvokeOnError(Connection, ex);
        //        return null;
        //    }
        //}
        //private async Task<ParseResult> BufferedReceive(ConnectedClient Connection) {
        //    int BytesAvailable = Connection.Socket.Available;

        //    DateTime DownloadStartTime = DateTime.UtcNow;
        //    long TotalBytesRead = 0L;

        //    try {
        //        for (int offset = 0, loopTo = BytesAvailable - 1; Options.DownloadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += Options.DownloadBufferSize) {
        //            //if (Options.MaximumDownloadMbps > 0) {
        //            // Limit download bandwidth.
        //            //while (LimitDownloadBandwidth(TotalDownloadedBytes, DownloadStartTime))
        //            //    Thread.Sleep(1);
        //            //DownloadStartTime = DateTime.UtcNow;
        //            //}

        //            if (Connection.Socket is null) {
        //                CloseConnection(Connection);
        //                return null;
        //            }

        //            int OffsetSize = BytesAvailable - offset;
        //            int BufferSize = OffsetSize < Options.DownloadBufferSize ? OffsetSize : Options.DownloadBufferSize;
        //            int bytesRead = 0;

        //            try {
        //                if(Connection._DownloadBuffer == null) {
        //                    Connection._DownloadBuffer = new byte[BufferSize];
        //                    bytesRead = Connection.Socket.Receive(Connection._DownloadBuffer, 0, BufferSize, SocketFlags.None);
        //                } else {
        //                    byte[] temp = new byte[BufferSize];
        //                    Connection._DownloadBuffer.Concat(temp);
        //                    temp = null;
        //                }  
        //            } catch (ObjectDisposedException) {
        //                CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //                return null;
        //            } catch (Exception ex) {
        //                InvokeOnError(Connection, ex);
        //                return null;
        //            }
        //            if (bytesRead > 0) {
        //                TotalBytesRead += bytesRead;
        //                InternalReceivedByteCounter?.Invoke(Connection, bytesRead);
        //            }
        //        }
        //    } catch (SocketException ex) {
        //        var Reason = DisconnectionReason.Unknown;
        //        if (ex.Message.ToLower().Contains("an established connection was aborted by the software in your host machine")) {
        //            Reason = DisconnectionReason.LocalSocketClosed;
        //        } else if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
        //            Reason = DisconnectionReason.RemoteSocketClosed;
        //        } else if (isDisposed) {
        //            Reason = DisconnectionReason.ObjectDisposed;
        //        } else if (!NIC.InternetAvailable()) {
        //            Reason = DisconnectionReason.InternetNotAvailable;
        //        } else if (!IsConnected(Connection)) {
        //            Reason = DisconnectionReason.LocalSocketClosed;
        //        }
        //        CloseConnection(Connection, Reason);
        //        return null;
        //    } catch (NullReferenceException) {
        //        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //        return null;
        //    } catch (ObjectDisposedException) {
        //        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //        return null;
        //    } catch (Exception ex) {
        //        InvokeOnError(Connection, ex);
        //        return null;
        //    }

        //    ParseResult result = await ParseBuffer(Connection, Connection.DownloadBuffer);
        //    return result;

        //}
        //private async Task<ParseResult> ParseBuffer(ConnectedClient Connection, byte[] Buffer) { //(ref ReceiveState state) {
        //    var Errors = new List<Exception>();
        //    int LastIndexOf = 0;
        //    List<Task> tasks = new List<Task>();
        //    if (Buffer != null && Buffer.Length > 0) {
        //        var TerminatorIndices = Buffer.IndexOfAll(ReceiveState.Terminator);
        //        for (int i = 0, loopTo = TerminatorIndices.Count - 1; i <= loopTo; i++) {
        //            int lastIndex = i > 0 ? TerminatorIndices[i - 1] : 0;
        //            int Index = TerminatorIndices[i];
        //            if (i == TerminatorIndices.Count - 1)
        //                LastIndexOf = Index;

        //            tasks.Add(Task.Run(() => {
        //                if (Buffer.Length < Index || Buffer.Length < lastIndex)
        //                    return;
        //                int MinusTerminator = lastIndex + ReceiveState.Terminator.Length;
        //                byte[] Bytes = Buffer.Part(lastIndex == 0 ? lastIndex : MinusTerminator, Index);
        //                if (Connection.Compressed) {
        //                    //string compressed = System.Text.UTF8Encoding.UTF8.GetString(Buffer);
        //                    var decompressionResult = MethodExtensions.TryInvoke(Options.CompressionAlgorithm.Decompress, ref Bytes);
        //                    if (decompressionResult.Success) {
        //                        Bytes = decompressionResult.Result;
        //                        string decompressed = System.Text.UTF8Encoding.UTF8.GetString(Bytes);
        //                        decompressed = decompressed;
        //                    } else {
        //                        this.CloseConnection(Connection, DisconnectionReason.CompressionError);
        //                        throw decompressionResult.Exception;
        //                    }
        //                }
        //                try {
        //                    object verified = VerifyObject((ObjectWrapper)Options.Serializer.Deserialize(Bytes));
        //                    if (verified != null) {
        //                        Task.Run(() => { HandleReceive(Connection, verified, verified.GetType(), Bytes.Length); });
        //                    }
        //                } catch (Exception ex) {
        //                    OnDeserializationError?.Invoke(new ErrorEventArgs(this, BaseConnection, ex));
        //                    Errors.Add(ex);
        //                    InvokeOnError(BaseConnection, ex);
        //                }
        //            }));
        //        }
        //        await Task.WhenAll(tasks);
        //    }
        //    byte[] remainingBytes = null;
        //    if (LastIndexOf > 0)
        //        remainingBytes = Buffer.Remove(0, LastIndexOf + ReceiveState.Terminator.Length);

        //    if (remainingBytes != null) {
        //        return new ParseResult(remainingBytes, LastIndexOf, Errors);
        //    } else {
        //        return null;
        //    }
        //}
        //private object VerifyObject(ObjectWrapper wrapper) {
        //    if (wrapper.Data is null)
        //        return null;
        //    if (wrapper.Type == "") {
        //        return null;
        //    } else if (wrapper.Type == typeof(PingObj).AssemblyQualifiedName) {
        //        return null;
        //    } else if (Options.Whitelist.Contains(wrapper.Type) || TcpOptions.DefaultOptions.Whitelist.Contains(wrapper.Type)) {
        //        return wrapper.Unwrap(this);
        //    } else {
        //        Exception ex = new TypeNotAllowedException(wrapper.Type);
        //        OnDeserializationError?.Invoke(new ErrorEventArgs(this, BaseConnection, ex));
        //        InvokeOnError(BaseConnection, ex);
        //        return ex;
        //    }
        //}

        ///// <summary>
        ///// Receive data from the socket without entering a loop.
        ///// </summary>
        ///// <param name="Connection"></param>
        ///// <returns><see langword="True"/> if successful; <see langword="False"/> if failed to receive.</returns>
        ///// <remarks></remarks>
        //internal async Task<ParseResult> Receive(ConnectedClient Connection) {
        //    if (Connection == null || Connection.Socket == null)
        //        return null;
        //    try {
        //        int DataAvailable = Connection.Socket.Available;
        //        if (Options.MaximumDownloadMbps <= 0) {
        //            return await NoBufferReceive(Connection);
        //        } else if (DataAvailable > 0) {
        //            return await BufferedReceive(Connection);
        //        } else {
        //            return null;
        //        }
        //    } catch (SocketException ex) {
        //        var Reason = DisconnectionReason.Unknown;
        //        if (ex.Message.ToLower().Contains("an existing connection was forcibly closed by a remote host")) {
        //            Reason = DisconnectionReason.RemoteSocketClosed;
        //        } else if (isDisposed) {
        //            Reason = DisconnectionReason.ObjectDisposed;
        //        } else if (!NIC.InternetAvailable()) {
        //            Reason = DisconnectionReason.InternetNotAvailable;
        //        } else if (!IsConnected(Connection)) {
        //            Reason = DisconnectionReason.LocalSocketClosed;
        //        }
        //        CloseConnection(Connection, Reason);
        //        return null;
        //    } catch (ObjectDisposedException) {
        //        CloseConnection(Connection, DisconnectionReason.ObjectDisposed);
        //        return null;
        //    } catch (Exception ex) {
        //        InvokeOnError(Connection, ex);
        //        return null;
        //    }
        //}

        //private bool LimitDownloadBandwidth(long TotalDownloadedBytes, DateTime DownloadStartTime) {
        //    if (Options.MaximumDownloadBytesPerSecond <= 0)
        //        return false;
        //    var Now = DateTime.UtcNow;
        //    if (DownloadStartTime - Now >= OneSecond) {
        //        DownloadStartTime = Now;
        //        TotalDownloadedBytes = 0L;
        //    } else {
        //        var elapsedTime = Now - DownloadStartTime;
        //        if (TotalDownloadedBytes > Options.MaximumDownloadBytesPerSecond) {
        //            int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
        //            if (sleepTime > 0)
        //                return true;
        //        }
        //    }
        //    return false;
        //}
        //private bool LimitUploadBandwidth(long TotalUploadedBytes, DateTime UploadStartTime) {
        //    if (Options.MaximumUploadBytesPerSecond <= 0)
        //        return false;
        //    var Now = DateTime.UtcNow;
        //    if (UploadStartTime - Now >= OneSecond) {
        //        UploadStartTime = Now;
        //        TotalUploadedBytes = 0L;
        //    } else {
        //        var elapsedTime = Now - UploadStartTime;
        //        if (TotalUploadedBytes > Options.MaximumUploadBytesPerSecond) {
        //            int sleepTime = (int)Math.Round(1000d - elapsedTime.TotalMilliseconds);
        //            if (sleepTime > 0)
        //                return true;
        //        }
        //    }
        //    return false;
        //}
        //#endregion

        public TcpBase() {
            NIC.Initialize();
        }
        public TcpBase(ISerializer Serializer) {
            Options.Serializer = Serializer;
            NIC.Initialize();
        }
    }
}