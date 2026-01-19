using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace SocketJack.Net {

    /// <summary>
    /// Base class for the Tcp Client and Server.
    /// </summary>
    /// <remarks></remarks>
    public abstract class TcpBase : IDisposable, ISocket {

        #region Events

        public event OnDisposingEventHandler OnDisposing;
        public delegate void OnDisposingEventHandler();

        public event OnReceiveEventHandler OnReceive;
        public delegate void OnReceiveEventHandler(ref IReceivedEventArgs e);

        public event OnSentEventHandler OnSent;
        public delegate void OnSentEventHandler(SentEventArgs e);

        public event OnErrorEventHandler OnError;
        public delegate void OnErrorEventHandler(ErrorEventArgs e);

        public event PeerConnectionRequestEventHandler PeerConnectionRequest;
        public delegate void PeerConnectionRequestEventHandler(ISocket sender, PeerServer Server);

        protected internal event InternalPeerConnectionRequestEventHandler Internal_PeerConnectionRequest;
        protected internal delegate void InternalPeerConnectionRequestEventHandler(ISocket sender, ref PeerServer Server);

        public event PeerServerShutdownEventHandler PeerServerShutdown;
        public delegate void PeerServerShutdownEventHandler(ISocket sender, PeerServer Server);

        public event BytesPerSecondUpdateEventHandler BytesPerSecondUpdate;
        public delegate void BytesPerSecondUpdateEventHandler(int ReceivedPerSecond, int SentPerSecond);

        public event LogOutputEventHandler LogOutput;
        public delegate void LogOutputEventHandler(string text);

        protected internal event PeerRefusedConnectionEventHandler PeerRefusedConnection;
        protected internal delegate void PeerRefusedConnectionEventHandler(ISocket sender, ConnectionRefusedArgs e);

        protected internal event PeerUpdateEventHandler PeerUpdate;
        protected internal delegate void PeerUpdateEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user connects to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user disconnects from the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(ISocket sender, Identifier Peer);

        protected internal event InternalPeerRedirectEventHandler InternalPeerRedirect;
        protected internal delegate void InternalPeerRedirectEventHandler(string Recipient, string Sender, object Obj, int BytesReceived);

        protected internal event InternalReceiveEventEventHandler InternalReceiveEvent;
        protected internal delegate void InternalReceiveEventEventHandler(TcpConnection Connection, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(TcpConnection Connection, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(TcpConnection Connection, int BytesSent);

        protected internal event InternalSendEventEventHandler InternalSendEvent;
        protected internal delegate void InternalSendEventEventHandler(TcpConnection Connection, Type objType, object obj, int BytesSent);

        void ISocket.InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server) {
#if UNITY
            var s = Server;
            MainThread.Run(() => {
		        Internal_PeerConnectionRequest?.Invoke(sender, ref s);
            });
#endif
#if WINDOWS
            var s = Server;
            Application.Current.Dispatcher.Invoke(() => {
                Internal_PeerConnectionRequest?.Invoke(sender, ref s);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            Internal_PeerConnectionRequest?.Invoke(sender, ref Server);
#endif
        }
        void ISocket.InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
#if UNITY
            MainThread.Run(() => {
                BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
#endif
        }
        void ISocket.InvokePeerServerShutdown(ISocket sender, PeerServer Server) {
#if UNITY
            MainThread.Run(() => {
                PeerServerShutdown?.Invoke(sender, Server);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerServerShutdown?.Invoke(sender, Server);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            PeerServerShutdown?.Invoke(sender, Server);
#endif
        }
        void ISocket.InvokePeerUpdate(ISocket sender, Identifier Peer) {
#if UNITY
            MainThread.Run(() => {
                PeerUpdate?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerUpdate?.Invoke(sender, Peer);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            PeerUpdate?.Invoke(sender, Peer);
#endif
        }
        void ISocket.InvokePeerConnected(ISocket sender, Identifier Peer) {
#if UNITY
            MainThread.Run(() => {
                PeerConnected?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerConnected?.Invoke(sender, Peer);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            PeerConnected?.Invoke(sender, Peer);
#endif
        }
        void ISocket.InvokePeerDisconnected(ISocket sender, Identifier Peer) {
#if UNITY
            MainThread.Run(() => {
                PeerDisconnected?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerDisconnected?.Invoke(sender, Peer);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            PeerDisconnected?.Invoke(sender, Peer);
#endif
        }
        void ISocket.InvokeInternalReceivedByteCounter(TcpConnection Connection, int BytesReceived) {
#if UNITY
            MainThread.Run(() => {
                InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
#endif
        }
        void ISocket.InvokeInternalSentByteCounter(TcpConnection connection, int chunkSize) {
#if UNITY
            MainThread.Run(() => {
                InternalSentByteCounter?.Invoke(connection, chunkSize);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalSentByteCounter?.Invoke(connection, chunkSize);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            InternalSentByteCounter?.Invoke(connection, chunkSize);
#endif
        }
        void ISocket.InvokeInternalSendEvent(TcpConnection connection, Type type, object @object, int length) {
#if UNITY
            MainThread.Run(() => {
                InternalSendEvent?.Invoke(connection, type, @object, length);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalSendEvent?.Invoke(connection, type, @object, length);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            InternalSendEvent?.Invoke(connection, type, @object, length);
#endif
        }
        void ISocket.InvokeOnReceive(ref IReceivedEventArgs e) {
#if UNITY
            var args = e;
            MainThread.Run(() => {
                OnReceive?.Invoke(ref args);
            });
#endif
#if WINDOWS
            var args = e;
            Application.Current.Dispatcher.Invoke(() => {
                 OnReceive?.Invoke(ref args);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnReceive?.Invoke(ref e);
#endif
        }
        void ISocket.InvokeOnSent(SentEventArgs sentEventArgs) {
#if UNITY
            MainThread.Run(() => {
                OnSent?.Invoke(sentEventArgs);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnSent?.Invoke(sentEventArgs);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnSent?.Invoke(sentEventArgs);
#endif
        }
        void ISocket.InvokeLogOutput(string text) {
#if UNITY
            MainThread.Run(() => {
                LogOutput?.Invoke(text);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                LogOutput?.Invoke(text);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            LogOutput?.Invoke(text);
#endif
        }
        void ISocket.InvokeOnError(TcpConnection Connection, Exception e) {
#if UNITY
            MainThread.Run(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
#endif
        }
        void ISocket.CloseConnection(TcpConnection Connection, DisconnectionReason Reason) {
            Connection.Close(this, Reason);
        }
        void ISocket.InvokeOnDisposing() {
#if UNITY
            MainThread.Run(() => {
                OnDisposing?.Invoke();
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisposing?.Invoke();
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnDisposing?.Invoke();
#endif
        }
        protected internal void InvokeOnReceive(IReceivedEventArgs e) {
#if UNITY
            MainThread.Run(() => {
                OnReceive?.Invoke(ref e);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnReceive?.Invoke(ref e);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnReceive?.Invoke(ref e);
#endif
        }
        protected internal void InvokeBytesPerSecondUpdate(TcpConnection Connection) {
#if UNITY
            MainThread.Run(() => {
                BytesPerSecondUpdate?.Invoke(Connection.BytesPerSecondSent, Connection.BytesPerSecondReceived);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                BytesPerSecondUpdate?.Invoke(Connection.BytesPerSecondSent, Connection.BytesPerSecondReceived);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            BytesPerSecondUpdate?.Invoke(Connection.BytesPerSecondSent, Connection.BytesPerSecondReceived);
#endif
        }
        protected internal void InvokeOnError(TcpConnection Connection, Exception e) {
            LogFormat("[{0}] ERROR: {1}", new[] { Name + (Connection != null && Connection.Identity != null ? @"\" + Connection.Identity.ID.ToUpper() : @"\Null"), e.Message });
            if (e.StackTrace != string.Empty) LogAsync(e.StackTrace);
#if UNITY
            MainThread.Run(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
                if (e.InnerException != null) InvokeOnError(Connection, e.InnerException);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
                if (e.InnerException != null) InvokeOnError(Connection, e.InnerException);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            if (e.InnerException != null) InvokeOnError(Connection, e.InnerException);
#endif
        }
        protected internal void InvokeAllCallbacks(IReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(e.Type)) {
                var l = TypeCallbacks[e.Type];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
#if UNITY
                    int index = i; // Capture the index for the lambda
                    MainThread.Run(() => {
                        l.ElementAt(index).Invoke(e);
                    });
#endif
#if WINDOWS
            int index = i; // Capture the index for the lambda
            Application.Current.Dispatcher.Invoke(() => {
                l.ElementAt(index).Invoke(e);
            });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
                    l.ElementAt(i).Invoke(e);
#endif
                }
            }
        }
        protected internal void InvokeAllCallbacks(Type Type, object obj, IReceivedEventArgs e) {
            if (TypeCallbacks.ContainsKey(Type)) {
                var l = TypeCallbacks[Type];
                for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
#if UNITY
                    int index = i; // Capture the index for the lambda
                    MainThread.Run(() => {
                        l.ElementAt(index).Invoke(e);
                    });
#endif
#if WINDOWS
                    int index = i; // Capture the index for the lambda
                    Application.Current.Dispatcher.Invoke(() => {
                        l.ElementAt(index).Invoke(e);
                    });
#endif
#if NETSTANDARD1_0_OR_GREATER && !UNITY
                    l.ElementAt(i).Invoke(e);
#endif
                }
            }
        }

        #endregion

        #region Internal
        public PeerList Peers { get; internal set; }
        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        TcpConnection ISocket.Connection { get => Connection; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new ConcurrentDictionary<string, PeerServer>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Servers = new ConcurrentDictionary<string, TcpConnection>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Clients = new ConcurrentDictionary<string, TcpConnection>();
        protected internal ConcurrentDictionary<Guid, byte[]> Buffers = new ConcurrentDictionary<Guid, byte[]>();
        protected internal Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<IReceivedEventArgs>>>();

        void ISocket.HandleReceive(TcpConnection connection, object obj, Type objType, int Length) {
            HandleReceive(connection, obj, objType, Length);
        }
        public void HandleReceive(TcpConnection Connection, object obj, Type objType, int Length) {
            switch (objType) {
                case var @case when @case == typeof(Identifier): {
                        if (Options.UsePeerToPeer)
                            PeerUpdate?.Invoke(this, (Identifier)obj);
                        break;
                    }
                case var @case when @case == typeof(Identifier[]): {
                        if (Options.UsePeerToPeer) {
                            var PeerIDs = (Identifier[])obj;
                            foreach (var item in PeerIDs) {
                                PeerUpdate?.Invoke(this, item);
                            }
                        }

                        break;
                    }
                case var case1 when case1 == typeof(PeerServer): {
                        PeerServer pServer = (PeerServer)obj;
                        if (this.Connection.IsServer)
                            pServer.LocalClient.IP = Connection.EndPoint.ToString();
                        if (Options.UsePeerToPeer) {
                            if (pServer.Shutdown) {
                                PeerServerShutdown?.Invoke(this, pServer);
                            } else {
                                Internal_PeerConnectionRequest?.Invoke(this, ref pServer);
                                PeerConnectionRequest?.Invoke(this, pServer);
                            }
                        } else { Send(this.Connection, new ConnectionRefusedArgs("Peer to peer is disabled on this connection.", pServer)); }
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
                        if (redirect == null) return;
                        if (this.Connection.IsServer)
                            redirect.Sender = Connection.ID.ToString();
                        //var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);

                        if (this.Connection.IsServer) {
                            Type redirectType = Type.GetType(redirect.Type);
                            var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                            var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                            if (!Peers.ContainsKey(Connection.Identity.ID)) return;
                            var from = Peers[Connection.Identity.ID];
                            receivedEventArgs.From = from;
                            receivedEventArgs.Initialize(this, Connection, obj, Length);
                            IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                            NonGenericEventArgs.From = from;
                            InvokeOnReceive(NonGenericEventArgs);
                            InvokeAllCallbacks(receivedEventArgs);

                            if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                                if (Peers.ContainsKey(redirect.Recipient)) {
                                    Identifier rID = Peers[redirect.Recipient];
                                    Send(rID, redirect);
                                    //SendBroadcast(redirect, Connection);
                                }
                            }

                            //if (this.Connection.IsServer) {
                            //    IReceivedEventArgs args = e;
                            //    args.From = Connection.Identity;
                            //    OnReceive?.Invoke(ref args);
                        } else {
                            InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                            InternalPeerRedirect?.Invoke(redirect.Recipient, redirect.Sender, redirect.Value, Length);
                        }
                        //if (!e.CancelPeerRedirect) {
                        //    //if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
                        //    InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                        //    InternalPeerRedirect?.Invoke(redirect.Recipient, Connection.Identity.ID, redirect.value, Length);
                        //}
                        break;
                    }
                default: {
                        //if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
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
        public void CloseConnection(TcpConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            if (!Connection.Closed) {
                Connection.Closed = true;
                if (Connection.Stream != null) {
                    MethodExtensions.TryInvoke(Connection.Stream.Close);
                    MethodExtensions.TryInvoke(Connection.Stream.Dispose);
                }
                if (Connection.Socket != null) {
                    MethodExtensions.TryInvoke(() => { Socket.Shutdown(SocketShutdown.Both); });
                    MethodExtensions.TryInvoke(Connection.Socket.Close);
                }
            }
        }
        protected internal void Bind(int Port) {
            this.Socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        public void Log(string[] lines) {
            if (Options.Logging) {
                string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (Options.LogToConsole && Debugger.IsAttached)
                    Debug.Write(Output);
                Console.Write(Output);
                LogOutput?.Invoke(Output);
            }
        }
        public void Log(string text) {
            Log(new[] { text });
        }
        public void LogFormat(string text, string[] args) {
            Log(new[] { string.Format(text, args) });
        }
        public void LogFormat(string[] lines, string[] args) {
            Log(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
        }
        public void LogAsync(string[] lines) {
            if (Options.Logging) {
                Task.Run(() => {
                    string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                    if (Options.LogToConsole && Debugger.IsAttached)
                        Debug.Write(Output);
                    Console.Write(Output);
                    LogOutput?.Invoke(Output);
                }).ConfigureAwait(false);
            }
        }
        public void LogAsync(string text) {
            LogAsync(new[] { text });
        }
        public void LogFormatAsync(string text, string[] args) {
            LogAsync(new[] { string.Format(text, args) });
        }
        public void LogFormatAsync(string[] lines, string[] args) {
            LogAsync(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
        }
        protected internal void Send(TcpConnection Connection, object Obj) {
            if (Connection.Socket == null)
                return;
            if (Connection.Socket.Connected) {
                var wrapped = new Wrapper(Obj, Connection.Parent);
                var Bytes = Connection.Parent.Options.Serializer.Serialize(wrapped);
                byte[] ProcessedBytes = Connection.Compressed ? Options.CompressionAlgorithm.Compress(Bytes) : Bytes;
                var SerializedBytes = ProcessedBytes.Terminate();
                lock (Connection.SendQueueRaw) {
                    Connection.SendQueueRaw.AddRange(SerializedBytes);
                }
            }
            //Connection.SendQueue.Enqueue(new SendQueueItem(Obj, Connection));
        }

        void ISocket.Send(TcpConnection connection, object Obj) {
            if (!Connected) return;
            Send(Connection, Obj);
        }

        public virtual void Send(Identifier Recipient, object Obj) {
            if (Recipient == null || !Connected) return;
            Send(Connection, new PeerRedirect(Recipient.ID, Obj));
        }

        public void Send(TcpConnection connection, Identifier Recipient, object Obj) {
            if (Recipient == null || !Connected) return;
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                                      "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                      "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
            } else {
                Send(connection, new PeerRedirect(RemoteIdentity.ID, Recipient.ID, Obj));
            }
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
        public virtual Stream Stream { get; set; }
        public virtual IPEndPoint EndPoint { get; set; }

        public TcpOptions Options = TcpOptions.DefaultOptions.Clone<TcpOptions>();
        TcpOptions ISocket.Options { get => Options; set => Options = value; }

        /// <summary>
        /// Remote identifier used for peer-to-peer interactions used to determine the server-side client ID.
        /// </summary>
        /// <returns><see langword="null"/> if accessed before the server identifies the client.
        /// <para>To avoid problems please do not acccess this via OnConnected Event.</para></returns>
        public Identifier RemoteIdentity {
            get {
                if (Connection != null) {
                    return Connection.Identity;
                } else {
                    return null;
                }
            }
        }

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
        public virtual Socket Socket { get; set; }

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
        public bool PeerToPeerInstance { get; internal set; }

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
        Identifier ISocket.RemoteIdentity { get => RemoteIdentity; set => throw new NotImplementedException(); }
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        bool ISocket.Connected { get => Connected; }

        #endregion

        #region Callbacks

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
        /// Removes a type callback.
        /// </summary>
        public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            Type Type = typeof(T);
            if (TypeCallbacks.ContainsKey(Type)) {
                Action<IReceivedEventArgs> wrappedAction = e => Action((ReceivedEventArgs<T>)e);
                TypeCallbacks[Type].Remove(wrappedAction);
            }
            if (TypeCallbacks[Type].Count == 0) {
                Options.Whitelist.Remove(Type);
                TypeCallbacks.Remove(Type);
            }
        }

        /// <summary>
        /// Removes all type callbacks of T and it's respective white-list entry.
        /// </summary>
        public void RemoveCallback<T>() {
            Type Type = typeof(T);
            Options.Whitelist.Remove(Type);
            TypeCallbacks.Remove(Type);
        }

        #endregion

        #region IDisposable Implementation
        private bool disposedValue;
        protected internal bool isDisposed = false;

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    // TODO: dispose managed state (managed objects)
                    OnDisposing?.Invoke();
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

        #region ISocket
        void ISocket.SendSegmented(TcpConnection Client, object Obj) {
            SendSegmented(Client, Obj);
        }

        protected internal void SendSegmented(TcpConnection Client, object Obj) {
            Task.Run(() => {
                byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
                Segment[] SegmentedObject = SerializedBytes.GetSegments();
                Parallel.ForEach(SegmentedObject, (s) => {
                    //var state = new SendQueueItem(s, Client);

                    lock (Client.SendQueueRaw) {
                        Client.SendQueueRaw.AddRange(Options.Serializer.Serialize(s));
                    }
                });
            });
        }

        public virtual void SendBroadcast(object Obj) {
            throw new NotImplementedException();
        }

        public virtual void SendBroadcast(object Obj, TcpConnection Except) {
            throw new NotImplementedException();
        }

        public virtual void SendBroadcast(TcpConnection[] Clients, object Obj, TcpConnection Except = null) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier, TcpOptions options, string name) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier, TcpOptions options) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier) {
            throw new NotImplementedException();
        }

        void ISocket.InvokeBytesPerSecondUpdate(TcpConnection connection) {
            InvokeBytesPerSecondUpdate(connection);
        }

        public virtual void InvokeOnDisconnected(ISocket sender, TcpConnection Connection) {
            throw new NotImplementedException();
        }

        public virtual void InvokeOnConnected(ISocket sender, TcpConnection Connection) {
            throw new NotImplementedException();
        }

        #endregion

        public TcpBase() {
            Peers = new PeerList(this);
            NIC.Initialize();
        }
        public TcpBase(ISerializer Serializer) {
            Peers = new PeerList(this);
            Options.Serializer = Serializer;
            NIC.Initialize();
        }
    }
}