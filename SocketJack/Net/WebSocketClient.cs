using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SocketJack.Net.TcpBase;

namespace SocketJack.Net {

    public class WebSocketClient : IDisposable, ISocket {
        private readonly ClientWebSocketWrapper _clientWrapper = new ClientWebSocketWrapper();
        private ClientWebSocket _client => _clientWrapper.InnerWebSocket;

        #region Properties

        /// <summary>
        /// Exposes the underlying Socket of the ClientWebSocket (if available).
        /// </summary>
        public Socket UnderlyingSocket => _clientWrapper.GetUnderlyingSocket();
        public string Name {
            get {
                return PeerToPeerInstance ? string.Format(@"{0}\P2P", new[] { _Name }) : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name = "WebSocketClient";
        public PeerList Peers { get; internal set; }
        
        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new ConcurrentDictionary<string, PeerServer>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Servers = new ConcurrentDictionary<string, TcpConnection>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Clients = new ConcurrentDictionary<string, TcpConnection>();
        public TcpOptions Options { get; set; } = TcpOptions.DefaultOptions.Clone<TcpOptions>();
        public TcpConnection Connection { get; set; }
        public Identifier RemoteIdentity { get; internal set; }

        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private Guid _InternalID = Guid.NewGuid();
        public bool isDisposed { get; internal set; } = false;
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        public bool Connected { get => _client.State == WebSocketState.Open; }

        private long _totalBytesReceived = 0;
        public long TotalBytesReceived => _totalBytesReceived;

        #endregion

        #region Events

        public event OnIdentifiedEventHandler OnIdentified;
        public delegate void OnIdentifiedEventHandler(Identifier Peer);

        public event OnDisposingEventHandler OnDisposing;
        public delegate void OnDisposingEventHandler();

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
        protected internal delegate void PeerUpdateEventHandler(object sender, Identifier Peer);


        /// <summary>
        /// Fired when a user's metadata changes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerMetaDataChanged;
        public delegate void PeerMetaDataChangedEventHandler(object sender, Identifier Peer);

        /// <summary>
        /// Fired when a user connects to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(object sender, Identifier Peer);

        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(object sender, Identifier Peer);

        /// <summary>
        /// Fired when connected to the remote server.
        /// </summary>
        /// <param name="sender"></param>
        public event OnConnectedEventHandler OnConnected;
        public delegate void OnConnectedEventHandler(ConnectedEventArgs e);

        /// <summary>
        /// Fired when disconnected from the remote server.
        /// </summary>
        public event OnDisconnectedEventHandler OnDisconnected;
        public delegate void OnDisconnectedEventHandler(DisconnectedEventArgs e);

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

        public void InvokePeerConnectionRequest(object sender, ref PeerServer Server) {
            if (Options.Logging) {
                LogFormat(@"[{0}\{1}] Requested Connection -> {2}:{3}", new[] { Name, Server.LocalClient.ID.ToUpper(), Server.Host, Server.Port.ToString() });
            }
            Internal_PeerConnectionRequest?.Invoke(sender, ref Server);
        }
        public void InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
        }
        public void InvokePeerServerShutdown(object sender, PeerServer Server) {
            PeerServerShutdown?.Invoke(sender, Server);
        }
        public void InvokePeerUpdate(object sender, Identifier Peer) {
            PeerUpdate?.Invoke(sender, Peer);
        }
        public void InvokePeerConnected(object sender, Identifier Peer) {
            if (Options.Logging) {
                LogFormat(@"[{0}\{1}] Connected To Server.", new[] { Name, Peer.ID.ToUpper() });
            }
            PeerConnected?.Invoke(sender, Peer);
        }
        public void InvokePeerDisconnected(object sender, Identifier Peer) {
            if(Options.Logging) {
                LogFormat(@"[{0}\{1}] Disconnected From Server.", new[] { Name, Peer.ID.ToUpper() });
            }
            PeerDisconnected?.Invoke(sender, Peer);
        }
        protected internal bool InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != null && !e.Connection.Closed) {
                e.Connection.Closed = true;
                OnDisconnected?.Invoke(e);
                return true;
            }
            return false;
        }
        protected internal void InvokeOnConnected(ConnectedEventArgs e) {
            if (e.Connection != null) OnConnected?.Invoke(e);
        }
        public void InvokeInternalReceivedByteCounter(TcpConnection Connection, int BytesReceived) {
            InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
        }
        public void InvokeInternalSentByteCounter(TcpConnection connection, int chunkSize) {
            InternalSentByteCounter?.Invoke(connection, chunkSize);
        }
        public void InvokeInternalSendEvent(TcpConnection connection, Type type, object @object, int length) {
            InternalSendEvent?.Invoke(connection, type, @object, length);
        }
        public void InvokeOnReceive(ref IReceivedEventArgs e) {
            OnReceive?.Invoke(ref e);
        }
        public void InvokeOnSent(SentEventArgs sentEventArgs) {
            OnSent?.Invoke(sentEventArgs);
        }
        public void InvokeLogOutput(string text) {
            LogOutput?.Invoke(text);
        }
        public void InvokeOnError(TcpConnection Connection, Exception e) {
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
        }
        void InvokeOnIdentified(ref Identifier Peer) {
            OnIdentified?.Invoke(Peer);
        }
        public void CloseConnection() {
            CloseConnection(Connection, DisconnectionReason.LocalSocketClosed);
        }
        public void CloseConnection(TcpConnection Connection, DisconnectionReason Reason) {
            if (_client.State == WebSocketState.Open || _client.State == WebSocketState.CloseReceived) {
                WebSocketCloseStatus status = GetWebSocketCloseStatus(Reason);
                string description = GetWebSocketCloseDescription(Reason);
                try {
                    _client.CloseAsync(status, description, CancellationToken.None).Wait();
                } catch (Exception ex) {
                    LogError(ex);
                }
            }

        }

        private static string GetWebSocketCloseDescription(DisconnectionReason reason) {
            return reason switch {
                DisconnectionReason.RemoteSocketClosed => "Remote socket closed",
                DisconnectionReason.LocalSocketClosed => "Local socket closed",
                _ => "Unknown reason",
            };
        }

        private static WebSocketCloseStatus GetWebSocketCloseStatus(DisconnectionReason reason) {
            return reason switch {
                DisconnectionReason.RemoteSocketClosed => WebSocketCloseStatus.EndpointUnavailable,
                DisconnectionReason.LocalSocketClosed => WebSocketCloseStatus.NormalClosure,
                _ => WebSocketCloseStatus.InternalServerError,
            };
        }

        private static DisconnectionReason GetDisconnectionReason(WebSocketCloseStatus reason) {
            switch (reason) {
                case WebSocketCloseStatus.EndpointUnavailable:
                    return DisconnectionReason.RemoteSocketClosed;
                case WebSocketCloseStatus.NormalClosure:
                    return DisconnectionReason.LocalSocketClosed;
                default:
                    return DisconnectionReason.Unknown;
            }
        }

        public void InvokeOnDisposing() {
            OnDisposing?.Invoke();
        }
        void ISocket.InvokeBytesPerSecondUpdate(TcpConnection connection) {
            InvokeBytesPerSecondUpdate(connection.BytesPerSecondReceived, connection.BytesPerSecondSent);
        }
        #endregion

        #region Logging
        public void Log(string[] lines) {
            if (Options.Logging) {
                string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (Options.LogToOutput && Debugger.IsAttached)
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
                    if (Options.LogToOutput && Debugger.IsAttached)
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
        public void LogError(Exception e) {
            if (Options.Logging) {
               InvokeOnError(Connection, e);
            }
        }
        #endregion

        #region P2P
        public bool PeerToPeerInstance { get; internal set; } = false;
        Identifier ISocket.RemoteIdentity { get => RemoteIdentity; set => RemoteIdentity = value; }

        public async Task<ISocket> StartServer(string ID, TcpOptions Options, string name = "WebSocketP2PServer", int Port = 0) {
            if (!Options.UsePeerToPeer) {
                LogError(new InvalidOperationException("P2P is not enabled."));
                return default;
            }
            // For WebSocket, you may need to implement signaling to exchange connection info
            var ServerInfo = await PeerServer.NewServer(Identifier.Create(ID), Identifier.Create(RemoteIdentity.ID), Options.Serializer, Port);
            var newServer = new WebSocketServer( (int)ServerInfo.Port, Name) { Options = Options, _PeerToPeerInstance = true };
            if (P2P_Servers.ContainsKey(ID))
                P2P_Servers[ID].CloseConnection();
            P2P_ServerInformation.AddOrUpdate(ID, ServerInfo);
            if (newServer.Listen()) {
                string RemoteIP = RemoteIdentity.IP;
                LogFormat(new[] { "[{0}] Started P2P Server.", "    EndPoint      = {1}:{2}" }, new[] { newServer.Name + @"\P2P}", RemoteIP, ServerInfo.Port.ToString() });
                P2P_Servers.AddOrUpdate(ID, newServer.Connection);
                Send(ServerInfo);
                return newServer;
            } else {
                InvokeOnError(Connection, new P2PException("Failed to start P2P Server." + Environment.NewLine + "Port may be in use." + Environment.NewLine + "Check if the port is already in use."));
                return null;
            }
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public async Task<ISocket> StartServer(Identifier RemotePeer, TcpOptions Options, string Name = "TcpServer") {
            return await StartServer(RemotePeer.ID, Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public async Task<ISocket> StartServer(Identifier RemotePeer, string Name = "TcpServer") {
            return await StartServer(RemotePeer, this.Options, Name);
        }

        public async Task<WebSocketClient> AcceptPeerConnection(PeerServer server, string name = "WebSocketP2PClient") {
            if (!Options.UsePeerToPeer) {
                LogError(new InvalidOperationException("P2P is not enabled."));
                return default;
            }
            var v = await server.Accept(name);
            if (v is WebSocketClient wsClient)
                return wsClient;
            else
                return default;
        }


        private void HandlePeer(Identifier peer, TcpConnection connection) {
            if (Options.UsePeerToPeer) {
                PeerUpdate?.Invoke(this, peer);
                switch (peer.Action) {
                    case PeerAction.LocalIdentity: {
                            RemoteIdentity = peer;
                            Connection._Identity = peer;
                            Connection.ID = Guid.Parse(peer.ID);
                            LogFormat("[{0}] Local Identity = {1}", new[] { Name, peer.ID.ToUpper() });
                            InvokeOnIdentified(ref Connection._Identity);
                            break;
                        }
                    case PeerAction.RemoteIdentity: {
                            PeerConnected?.Invoke(this, peer);
                            Peers.AddOrUpdate(peer);
                            break;
                        }
                    case PeerAction.Dispose: {
                            PeerDisconnected?.Invoke(this, peer);
                            Peers.Remove(peer.ID);
                            P2P_Clients.TryRemove(peer.ID, out _);
                            P2P_ServerInformation.TryRemove(peer.ID, out _);
                            break;
                        }
                    case PeerAction.MetadataUpdate: {
                            PeerMetaDataChanged?.Invoke(this, peer);
                            Peers.UpdateMetaData(peer);
                            break;
                        }
                }
            }
        }
        #endregion

        #region Sending
        public void SendBroadcast(object Obj) {
            Parallel.ForEach(Peers.Values, (peer) => { Send(Connection, peer, Obj); });
        }

        public void SendBroadcast(TcpConnection[] Clients, object Obj, TcpConnection Except) {
            throw new NotImplementedException("SendBroadcast with TcpConnection[] is not implemented for WebSocketClient.");
        }

        public void SendBroadcast(object Obj, TcpConnection Except) {
            throw new NotImplementedException("SendBroadcast with TcpConnection is not implemented for WebSocketClient.");
        }

        public void SendSegmented(TcpConnection Client, object Obj) {
            Task.Run(() => {
                byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
                Segment[] SegmentedObject = SerializedBytes.GetSegments();
                Parallel.ForEach(SegmentedObject, (s) => {
                    SendAsync(s);
                });
            });
        }

        public void Send(object obj) {
            Task.Run(() => SendAsync(obj, default));
        }

        public void Send(TcpConnection connection, object obj) {
            Task.Run(() => SendAsync(obj, default));
        }

        public void Send(Identifier recipient, object obj) {
            Task.Run(() => {
                var redirect = new PeerRedirect(recipient.ID, obj);
                SendAsync(redirect, default);
            });
        }

        public void SendPeerBroadcast( object obj) {
            Task.Run(() => {
                var redirect = new PeerRedirect("#ALL#", obj);
                SendAsync(redirect, default);
            });
        }

        public void Send(TcpConnection connection,Identifier recipient, object obj) {
            var redirect = new PeerRedirect(connection?.ID.ToString(), recipient.ID, obj);
            Task.Run(() => SendAsync(redirect, default));
        }

        protected internal async Task SendAsync(object obj, CancellationToken cancellationToken = default) {
            if(_client.State != WebSocketState.Open) {
                await Task.Run(()=> {
                    do {
                        Thread.Sleep(100);
                    } while (_client.State != WebSocketState.Open && _client.State != WebSocketState.Connecting);
                    SendAsync_Internal(obj, cancellationToken);
                });
            } else {
                SendAsync_Internal(obj, cancellationToken);
            }
        }
        private async void SendAsync_Internal(object obj, CancellationToken cancellationToken ) {
            byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(obj, this));
            WebSocketMessageType messageType = WebSocketMessageType.Text;
            if (Options.UseCompression) {
                SerializedBytes = Options.CompressionAlgorithm.Compress(SerializedBytes);
                messageType = WebSocketMessageType.Binary;
            }
            await _client.SendAsync(new ArraySegment<byte>(SerializedBytes), messageType, true, cancellationToken);
            if (Options.Logging && Options.LogSendEvents) {
                Type objType = obj.GetType();
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)obj).CleanTypeName()), SerializedBytes.Length.ByteToString() });
                } else {
                    LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, SerializedBytes.Length.ByteToString() });
                }
            }
        }

        #endregion

        public class WscObject {
            public object Object;
            public int Length;
            public WscObject(object Object, int Length) {
                this.Object = Object;
                this.Length = Length;
            }
        }

        #region Receiving
        public async Task<WscObject> ReceiveAsync(CancellationToken cancellationToken = default) {
            try {
                var SerializedBytes = new byte[Options.DownloadBufferSize];
                var result = await _client.ReceiveAsync(new ArraySegment<byte>(SerializedBytes), cancellationToken);
                _totalBytesReceived += result.Count;
                Array.Resize(ref SerializedBytes, result.Count);
                if (Options.UseCompression) {
                    SerializedBytes = Options.CompressionAlgorithm.Decompress(SerializedBytes);
                }
                Wrapper wrapper = Options.Serializer.Deserialize(SerializedBytes);
                return new WscObject(wrapper.Unwrap(this), result.Count);
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
                return null;
            }
        }

        public void HandleReceive(TcpConnection connection, object obj, Type objType, int Length) {
            if (objType == typeof(Identifier)) {
                if (Options.UsePeerToPeer)
                    HandlePeer((Identifier)obj, connection);
            } else if (objType == typeof(Identifier[])) {
                if (Options.UsePeerToPeer) {
                    var peerIDs = (Identifier[])obj;
                    foreach (var peer in peerIDs)
                        HandlePeer(peer, connection);
                }
            } else if (objType == typeof(PeerServer)) {
                if (Options.UsePeerToPeer) {
                    var pServer = (PeerServer)obj;
                    if (pServer.Shutdown) {
                        PeerServerShutdown?.Invoke(this, pServer);
                    } else {
                        PeerConnectionRequest?.Invoke(this, pServer);
                    }
                }
            } else if (objType == typeof(PeerRedirect)) {
                if (Options.UsePeerToPeer) {
                    var redirect = (PeerRedirect)obj;
                    var From = Peers.Values.FirstOrDefault(p => p.ID == redirect.Recipient);
                    if(From != null) {
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirect.value.GetType());
                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        receivedEventArgs.From = From;

                        receivedEventArgs.Initialize(this, Connection, redirect.value, Length);
                        IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, redirect.value, Length);
                        NonGenericEventArgs.From = From;
                        InvokeOnReceive(ref NonGenericEventArgs);
                        InvokeAllCallbacks(receivedEventArgs);
                    }
                }
            } else {
                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(obj.GetType());
                var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                receivedEventArgs.Initialize(this, Connection, obj, Length);
                IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                InvokeOnReceive(ref NonGenericEventArgs);
                InvokeAllCallbacks(receivedEventArgs);
            }
        }
        #endregion

        #region Callbacks
        private Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new();
        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> action) {
            Type type = typeof(T);
            if (!TypeCallbacks.ContainsKey(type))
                TypeCallbacks[type] = new List<Action<IReceivedEventArgs>>();
            TypeCallbacks[type].Add(e => action((ReceivedEventArgs<T>)e));
        }
        public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> action) {
            Type type = typeof(T);
            if (TypeCallbacks.ContainsKey(type)) {
                TypeCallbacks[type].Remove(e => action((ReceivedEventArgs<T>)e));
                if (TypeCallbacks[type].Count == 0)
                    TypeCallbacks.Remove(type);
            }
        }
        public void RemoveCallback<T>() {
            Type type = typeof(T);
            if (TypeCallbacks.ContainsKey(type))
                TypeCallbacks.Remove(type);
        }
        protected internal void InvokeAllCallbacks(IReceivedEventArgs e) {
            if (TypeCallbacks.TryGetValue(e.Type, out var list)) {
                foreach (var cb in list)
                    cb(e);
            }
        }
        #endregion

        public WebSocketClient() {
            Peers = new PeerList(this);
            var argsClient = (ISocket)this;
            Globals.RegisterServer(ref argsClient);
            PeerConnected += (sender, peer) => {
                LogFormatAsync("[{0}] Peer connected: {1}", new[] { Name, peer.ID.ToUpper() });
            };
            PeerDisconnected += (sender, peer) => {
                LogFormatAsync("[{0}] Peer disconnected: {1}", new[] { Name, peer.ID.ToUpper() });
            };
        }

        public WebSocketClient(TcpOptions Options, string Name = "WebSocketClient") {
            this.Name = Name;
            Peers = new PeerList(this); 
            var argsClient = (ISocket)this;
            Globals.RegisterServer(ref argsClient);
            PeerConnected += (sender, peer) => {
                LogFormatAsync("[{0}] Peer connected: {1}", new[] { Name, peer.ID.ToUpper() });
            };
            PeerDisconnected += (sender, peer) => {
                LogFormatAsync("[{0}] Peer disconnected: {1}", new[] { Name, peer.ID.ToUpper() });
            };
        }

        public async Task<bool> ConnectAsync(Uri uri, CancellationToken cancellationToken = default) {
            bool connected = false;
            try {
                if (Options.Logging) {
                    LogFormatAsync("[{0}] Connecting to -> {1}", new[] { Name, uri.ToString() });
                }
                await _client.ConnectAsync(uri, cancellationToken);
                connected = _client.State == WebSocketState.Open;
                if (!connected) {
                    LogFormatAsync("[{0}] Failed to connect to -> {1}", new[] { Name, uri.ToString() });
                    return false;
                }
                Connection = new TcpConnection(this, UnderlyingSocket);
                // SSL support for client
                if (Options.UseSsl) {
                    var stream = new NetworkStream(UnderlyingSocket, ownsSocket: false);
                    var sslStream = new System.Net.Security.SslStream(stream, false);
                    await sslStream.AuthenticateAsClientAsync(uri.Host);
                    Connection.SslStream = sslStream;
                }
                InvokeOnConnected(new ConnectedEventArgs(this, Connection));
                if (Options.Logging) {
                    LogFormatAsync("[{0}] Connected to  -> {1}", new[] { Name, uri.ToString() });
                }
                Task.Run(async () => {
                    while (_client.State == WebSocketState.Open) {
                        try {
                            var receivedObject = await ReceiveAsync(cancellationToken);
                            if (receivedObject != null) {
                                Type objType = receivedObject.Object.GetType();
                                if (Options.Logging && Options.LogReceiveEvents) {
                                    if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                                        LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)receivedObject.Object).CleanTypeName()), receivedObject.Length.ByteToString() });
                                    } else {
                                        LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, objType.Name, receivedObject.Length.ByteToString() });
                                    }
                                }
                                HandleReceive(Connection, receivedObject.Object, objType, receivedObject.Length);
                            }
                        } catch (Exception ex) {
                            LogError(ex);
                            break;
                        }
                    }
                    var status = _client.CloseStatus;
                    DisconnectionReason reason = DisconnectionReason.Unknown;
                    if (status != null) {
                        reason = GetDisconnectionReason(status.Value);
                    } else {
                        reason = DisconnectionReason.RemoteSocketClosed;
                    }
                    if (InvokeOnDisconnected(new DisconnectedEventArgs(this, Connection, reason))) {
                        if (Options.Logging) {
                            LogFormatAsync("[{0}] Disconnected  -> {1}", new[] { Name, uri.ToString() });
                        }
                    }
                });
                return true;
            } catch (Exception ex) {
                LogError(ex);
                return false;
            }
        }

        public void Dispose() {
            if(Connected) {
                var argClient = (ISocket)this;
                Globals.UnregisterClient(ref argClient);
                InvokeOnDisposing();
                CloseConnection();
                isDisposed = true;
            }
        }
    }
}
