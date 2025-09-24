using SocketJack;
using SocketJack.Extensions;
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Net.WebSockets;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Net.WebSockets {
    public class WebSocketServer : IDisposable, ISocket {

        #region Internal
        private readonly int _port;
        private Socket _listenerSocket;
        private CancellationToken _ct;
        private bool _isListening = false;
        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private Guid _InternalID = Guid.NewGuid();
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        bool ISocket.Connected { get => IsListening; }

        #endregion

        #region Properties
        public Socket Socket { get { return _Socket; } set { } }
        private Socket _Socket = null;
        public Stream Stream { get { return _Stream; } set { } }
        private Stream _Stream = null;
        public IPEndPoint EndPoint { get { return (IPEndPoint)Socket?.RemoteEndPoint; } set { } }
        public bool isDisposed { get; internal set; } = false;
        public bool PeerToPeerInstance { get; internal set; }
        /// <summary>
        /// Server certificate for SSL connections.
        /// </summary>
        public X509Certificate SslCertificate { get; set; }
        public TcpConnection Connection { get; private set; }
        public string Name { get; set; } = "WebSocketServer";
        public bool IsListening => _isListening;
        public ConcurrentDictionary<Guid, TcpConnection> Clients { get; } = new ConcurrentDictionary<Guid, TcpConnection>();
        public TcpOptions Options { get; set; } = TcpOptions.DefaultOptions.Clone<TcpOptions>();
        #endregion

        #region Internal
        protected internal bool _PeerToPeerInstance = false;

        private ConcurrentDictionary<Guid, bool> _handshakeCompleted = new ConcurrentDictionary<Guid, bool>();

        private async Task HandleRawSocketClient(TcpConnection connection) {
            if (connection._Stream == null) {
                connection._Stream = new NetworkStream(connection.Socket, ownsSocket: false);
                _Stream = connection._Stream;
            }
            if (Options.UseSsl) {
                if (SslCertificate == null)
                    throw new InvalidOperationException("SSL is enabled but no certificate is set in SslCertificate property.");
                connection.SslStream = new System.Net.Security.SslStream(connection._Stream, false);
                await connection.SslStream.AuthenticateAsServerAsync(SslCertificate, false, System.Security.Authentication.SslProtocols.Tls12, false);
            }

            var buffer = new byte[Options.DownloadBufferSize];
            int totalRead = 0;
            int bytesRead = 0;
            var requestBuilder = new StringBuilder();

            while (true) {
                bytesRead = await connection.Stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead <= 0) break;
                requestBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                totalRead += bytesRead;
                if (requestBuilder.ToString().Contains("\r\n\r\n")) break;
                if (totalRead > 8192) break;
            }
            string request = requestBuilder.ToString();
            if (!request.StartsWith("GET") || !request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase)) {
                connection.Socket.Close();
                return;
            }
            string secWebSocketKey = null;
            foreach (var line in request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    secWebSocketKey = line.Substring(line.IndexOf(":") + 1).Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(secWebSocketKey)) {
                connection.Socket.Close();
                return;
            }
            string acceptKey = ComputeWebSocketAcceptKey(secWebSocketKey);
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                "\r\n";
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await connection.Stream.WriteAsync(responseBytes, 0, responseBytes.Length);

            ClientConnected?.Invoke(new ConnectedEventArgs(this, connection));
            InitializePeer(connection);
            await HandleWebSocketFrames(connection);
        }

        private static string ComputeWebSocketAcceptKey(string secWebSocketKey) {
            string concat = secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            using (SHA1 sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(concat));
                return Convert.ToBase64String(hash);
            }
        }

        private async Task<int> ReadByteAsync(Stream stream, CancellationToken token = default) {
            var buffer = new byte[1];
            int read = await stream.ReadAsync(buffer, 0, 1, token);
            return read == 1 ? buffer[0] : -1;
        }

        // Add constants for opcodes
        private const int TextFrameOpcode = 0x1;
        private const int BinaryFrameOpcode = 0x2;
        private const int CloseFrameOpcode = 0x8;
        private const int BrowserClientOpcode = 0xB; // 0xB is unused in RFC6455

        private async Task HandleWebSocketFrames(TcpConnection connection) {
            var buffer = new byte[Options.DownloadBufferSize];

            try {
                bool exitLoop = false;
                while (connection.Socket.Connected && !connection.Closed) {
                    using var cts = new CancellationTokenSource();
                    var monitorTask = Task.Run(async () => {
                        while (connection.Socket != null && connection.Socket.Connected && !connection.Closed) {
                            await Task.Delay(100);
                        }
                        cts.Cancel();
                        CloseConnection(connection, DisconnectionReason.RemoteSocketClosed);
                    });

                    int header = await ReadByteAsync(connection.Stream, cts.Token);
                    if (header == -1) break;
                    bool fin = (header & 0x80) != 0;
                    int opcode = header & 0x0F;
                    int lengthByte = await ReadByteAsync(connection.Stream, cts.Token);
                    if (lengthByte == -1) break;
                    bool mask = (lengthByte & 0x80) != 0;
                    int payloadLen = lengthByte & 0x7F;
                    if (payloadLen == 126) {
                        var ext = new byte[2];
                        await connection.Stream.ReadAsync(ext, 0, 2, cts.Token);
                        payloadLen = ext[0] << 8 | ext[1];
                    } else if (payloadLen == 127) {
                        var ext = new byte[8];
                        await connection.Stream.ReadAsync(ext, 0, 8, cts.Token);
                        payloadLen = (int)BitConverter.ToUInt64(ext, 0);
                    }
                    byte[] maskingKey = null;
                    if (mask) {
                        maskingKey = new byte[4];
                        await connection.Stream.ReadAsync(maskingKey, 0, 4, cts.Token);
                    }
                    int read = 0;
                    var payload = new byte[payloadLen];
                    while (read < payloadLen && !cts.Token.IsCancellationRequested) {
                        int r = await connection.Stream.ReadAsync(payload, read, payloadLen - read, cts.Token);
                        if (r <= 0) break;
                        read += r;
                    }
                    if (mask && maskingKey != null) {
                        for (int i = 0; i < payload.Length; i++)
                            payload[i] ^= maskingKey[i % 4];
                    }

                    switch (opcode) {
                        case CloseFrameOpcode: // Close
                            exitLoop = true;
                            break;
                        case BrowserClientOpcode:
                            SendConstructors(ref connection);
                            continue;
                        case TextFrameOpcode:
                        case BinaryFrameOpcode:
                            _handshakeCompleted[connection.ID] = true;

                            byte[] data = payload;
                            if (Options.UseCompression && opcode == BinaryFrameOpcode) {
                                data = Options.CompressionAlgorithm.Decompress(data);
                            }
                            var message = Encoding.UTF8.GetString(data);
                            var serializer = Options.Serializer;
                            var wrapper = serializer.Deserialize(Encoding.UTF8.GetBytes(message));
                            if (wrapper != null) {
                                var obj = wrapper.Unwrap(this);
                                if (obj == null) {
                                    if (Options.Logging)
                                        LogAsync($"[{Name}] Type of '{wrapper.Type}' is not allowed.");
                                    continue;
                                } else {
                                    Type objType = obj.GetType();
                                    if (Options.Logging && Options.LogReceiveEvents) {
                                        if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                                            LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)obj).CleanTypeName()), payload.Length.ByteToString() });
                                        } else {
                                            LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, objType.Name, payload.Length.ByteToString() });
                                        }
                                    }
                                    HandleReceive(connection, obj, objType, payload.Length);
                                }
                            }
                            break;
                        default:
                            // Unknown or unsupported opcode, ignore
                            break;
                    }
                    if(exitLoop || cts.IsCancellationRequested) {
                        break;
                    }
                }
            } catch (Exception ex) {
                if (connection.Socket != null && Options.Logging && connection.Socket.Connected) {
                    InvokeOnError(ex);
                    InvokeOnError(connection, ex);
                }
            }
        }

        private TcpConnection NewConnection(ref Socket handler) {
            var newConnection = new TcpConnection(this, handler);
            newConnection.StartConnectionTester();
            newConnection.ID = Guid.NewGuid();
            _handshakeCompleted[newConnection.ID] = false;
            newConnection._Identity = Identifier.Create(Guid.NewGuid(), true, handler.RemoteEndPoint.ToString());
            Clients.AddOrUpdate(newConnection.ID, newConnection);
            return newConnection;
        }

        private void SendConstructors(ref TcpConnection connection) {
            var javascript = new StringBuilder();
            // For each whitelisted type, generate a JS constructor
            foreach (var t in Options.Whitelist) {
                Type type = Wrapper.GetValueType(t);
                if (type == typeof(string) || type == typeof(Wrapper)) continue;
                if (type == null) continue;
                if (!type.IsClass || type.IsAbstract || type.IsGenericType) continue;
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                string className = $"{type.Name}";
                javascript.Append($"class {className} {{{Environment.NewLine}");
                javascript.Append("    constructor(");
                for (int i = 0; i < properties.Length; i++) {
                    javascript.Append(properties[i].Name);
                    if (i < properties.Length - 1) javascript.Append(", ");
                }
                javascript.Append($") {{{Environment.NewLine}");
                for (int i = 0; i < properties.Length; i++) {
                    javascript.Append($"        this.{properties[i].Name} = {properties[i].Name};{Environment.NewLine}");
                }
                javascript.Append($"    }}{Environment.NewLine}");
                javascript.Append($"}}{Environment.NewLine}");
                javascript.Append($"if (typeof window !== 'undefined') {{ window['{className}'] = {className}; }}{Environment.NewLine}{Environment.NewLine}");
            }
            JSContructors script = new JSContructors(javascript.ToString());
            if (connection != null) {
                Send(connection, script);
            }
        }

        public class JSContructors {
            public string Script { get; set; }
            public JSContructors(string script) {
                Script = script;
            }
        }

        #endregion

        #region Events

        public event OnStoppedListeningEventHandler OnStoppedListening;
        public delegate void OnStoppedListeningEventHandler();

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

        public event ClientIdentifiedEventHandler ClientIdentified;
        public delegate void ClientIdentifiedEventHandler(ConnectedEventArgs e);

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event OnClientDisconnectedEventHandler ClientDisconnected;
        public delegate void OnClientDisconnectedEventHandler(DisconnectedEventArgs e);

        /// <summary>
        /// Fired when a user connects to the server.
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user disconnects from the server.
        /// /// </summary>
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

        void ISocket.InvokeBytesPerSecondUpdate(TcpConnection connection) {
            InvokeBytesPerSecondUpdate(connection.BytesPerSecondReceived, connection.BytesPerSecondSent);
        }
        public void InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server) {
            Internal_PeerConnectionRequest?.Invoke(sender, ref Server);
        }
        public void InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
        }
        public void InvokePeerServerShutdown(ISocket sender, PeerServer Server) {
            PeerServerShutdown?.Invoke(sender, Server);
        }
        public void InvokePeerUpdate(ISocket sender, Identifier Peer) {
            PeerUpdate?.Invoke(sender, Peer);
        }
        public void InvokePeerConnected(ISocket sender, Identifier Peer) {
            PeerConnected?.Invoke(sender, Peer);
        }
        public void InvokePeerDisconnected(ISocket sender, Identifier Peer) {
            PeerDisconnected?.Invoke(sender, Peer);
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

        public void InvokeOnError(Exception e) {
            LogAsync(e.Message + Environment.NewLine + e.StackTrace);
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
        }
        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection == null) return;
            if (e.Connection.Identity == null) return;
            Identifier dcPeer = Identifier.Create(e.Connection.Identity.ID, false);
            dcPeer.Action = PeerAction.Dispose;
            SendBroadcast(dcPeer, e.Connection);
            if (Peers.ContainsKey(e.Connection.Identity.ID)) {
                Peers.Remove(e.Connection.Identity.ID);
            }
            if(Clients.ContainsKey(e.Connection.ID)) {
                Clients.Remove(e.Connection.ID);
            }
            if (e.Connection != Connection) ClientDisconnected?.Invoke(e);
        }
        public void CloseConnection(TcpConnection Connection, DisconnectionReason Reason) {
            try {
                if (Connection.Socket != null && Connection.Socket.Connected) {
                    // Send close frame
                    byte[] closeFrame = new byte[] { 0x88, 0x00 };
                    Connection.Stream.Write(closeFrame, 0, closeFrame.Length);
                    Connection.Socket.Shutdown(SocketShutdown.Both);
                    Connection.Socket.Close();
                }
                Connection.Stream?.Dispose();
                InvokeOnDisconnected(new DisconnectedEventArgs(this, Connection, Reason));
            } catch (Exception ex) {
                InvokeOnError(ex);
            }
        }
        public void InvokeOnDisposing() {
            OnDisposing?.Invoke();
        }

        public virtual void InvokeOnDisconnected(ISocket sender, TcpConnection Connection) {
            throw new NotImplementedException();
        }

        public virtual void InvokeOnConnected(ISocket sender, TcpConnection Connection) {
            throw new NotImplementedException();
        }


        #endregion

        #region P2P
        public PeerList Peers { get; internal set; }
        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, TcpConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }
        Identifier ISocket.RemoteIdentity { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new ConcurrentDictionary<string, PeerServer>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Servers = new ConcurrentDictionary<string, TcpConnection>();
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Clients = new ConcurrentDictionary<string, TcpConnection>();

        private void InitializePeer(TcpConnection newConnection) {
            if (Options.UsePeerToPeer) {
                var peer = newConnection._Identity;
                if (!Peers.ContainsKey(newConnection.Identity.ID)) {
                    Peers.AddOrUpdate(peer);
                } else {
                    peer = Peers[newConnection.Identity.ID];
                    newConnection._Identity = peer;
                }
                PeerConnected?.Invoke(this, peer);
                var peerArray = Peers.ToArrayWithLocal(newConnection);
                Send(newConnection, peerArray);
                SendBroadcast(peer.RemoteReady(this), newConnection);
                ClientIdentified?.Invoke(new ConnectedEventArgs(this, newConnection));
            }
        }

        Task<ISocket> ISocket.StartServer(Identifier identifier, TcpOptions options, string name) {
            throw new NotImplementedException();
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

        #region Logging

        private void WireUpConnectionEvents() {
            ClientConnected += (e) => {
                if (Options.Logging) {
                    LogAsync($"[{Name}] Client Connected    -> {e.Connection.Identity.ID.ToUpper()} ({e.Connection.Socket.RemoteEndPoint})");
                }
            };
            ClientDisconnected += (e) => {
                if (Options.Logging) {
                    LogAsync($"[{Name}] Client Disconnected -> {e.Connection.Identity.ID.ToUpper()} ({e.Connection.EndPoint.ToString()})");
                }
            };
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
                });
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
        #endregion

        #region Sending

        public void Send(TcpConnection Client, object Obj) {
            if (Client == null || Client.Closed || Client.Socket == null || !Client.Socket.Connected) return;
            if (Client.Stream == null) Client._Stream = new NetworkStream(Client.Socket, ownsSocket: false);
            Task.Run(() => {
                try {
                    if(Obj == null) return;
                    Type objType = Obj.GetType();
                    if(Options.Blacklist.Contains(objType)) {
                        if (Options.Logging) {
                            InvokeOnError(Client, new Exception($"Attempted to send blacklisted type {objType.Name}"));
                        }
                        return;
                    }
                    Client.IsSending = true;
                    var wrapper = new Wrapper(Obj, this);
                    byte[] payload = Options.Serializer.Serialize(wrapper);
                    bool useBinaryFrame = Options.UseCompression;
                    if (useBinaryFrame) {
                        payload = Options.CompressionAlgorithm.Compress(payload);
                    }
                    List<byte> frame = new List<byte>();
                    frame.Add(useBinaryFrame ? (byte)0x82 : (byte)0x81); // 0x82 = FIN+binary, 0x81 = FIN+text
                    if (payload.Length <= 125) {
                        frame.Add((byte)payload.Length);
                    } else if (payload.Length <= 8192) {
                        frame.Add(126);
                        frame.Add((byte)(payload.Length >> 8 & 0xFF));
                        frame.Add((byte)(payload.Length & 0xFF));
                    } else {
                        frame.Add(127);
                        ulong len = (ulong)payload.Length;
                        for (int i = 7; i >= 0; i--)
                            frame.Add((byte)(len >> 8 * i & 0xFF));
                    }
                    frame.AddRange(payload);

                    if (Client.Stream == null) return;
                    Client.Stream.Write(frame.ToArray(), 0, frame.Count);
                    Client.Stream.Flush();
                   
                    InvokeInternalSendEvent(Client, objType, Obj, payload.Length);
                    InvokeOnSent(new SentEventArgs(this, Client, objType, payload.Length));

                    if (Options.Logging && Options.LogSendEvents) {
                        if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                            LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)Obj).CleanTypeName()), payload.Length.ByteToString() });
                        } else {
                            LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, payload.Length.ByteToString() });
                        }
                    }

                    Client.IsSending = false;
                } catch (Exception ex) {
                    Client.IsSending = false;
                    InvokeOnError(Client, ex);
                }
            });
        }

        public void Send(Identifier recipient, object Obj) {
            if (recipient == null) return;
            var client = Clients.Values.FirstOrDefault(c => c.Identity.ID == recipient.ID);
            var redirect = new PeerRedirect(client?.ID.ToString(), recipient.ID, Obj);
            Send(client, redirect);
        }

        public void SendSegmented(TcpConnection Client, object Obj) {
            Task.Run(() => {
                byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
                Segment[] SegmentedObject = SerializedBytes.GetSegments();
                Parallel.ForEach(SegmentedObject, (s) => {
                    Send(Client, s);
                });
            });
        }

        public void SendBroadcast(object Obj) {
            lock (Clients.Values) {
                Parallel.ForEach(Clients.Values, (client) => {
                    if (client != null && !client.Closed && client.Socket.Connected) {
                        Send(client, Obj);
                    }
                });
            }
        }

        public void SendBroadcast(TcpConnection[] Clients, object Obj, TcpConnection Except) {
            lock (Clients) {
                Parallel.ForEach(Clients, (client) => {
                    if (client != null && client.Socket != null && client.Socket.Connected && client != Except) {
                        Send(client, Obj);
                    }
                });
            }
        }

        public void SendBroadcast(object Obj, TcpConnection Except) {
            lock (Clients.Values) {
                Parallel.ForEach(Clients.Values, (client) => {
                    if (client != null && client.Socket != null && client.Socket.Connected && client != Except) {
                        Send(client, Obj);
                    }
                });
            }
        }
        #endregion

        #region Receiving
        public void HandleReceive(TcpConnection connection, object obj, Type objType, int Length) {
            if (objType == typeof(PeerServer)) {
                var pServer = (PeerServer)obj;
                if (Options.UsePeerToPeer) {
                    if (pServer.Shutdown) {
                        PeerServerShutdown?.Invoke(this, pServer);
                    } else {
                        PeerConnectionRequest?.Invoke(this, pServer);
                    }
                }
            } else if(objType == typeof(PeerRedirect)) {
                var redirect = (PeerRedirect)obj;
                redirect.Sender = connection.Identity.ID.ToString();
                if (redirect.Recipient == "#ALL#") {
                    //var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);
                    //IReceivedEventArgs args = e;

                    // args.From = Peers[redirect.Sender];

                    // FIX THIS
                    Type redirectType = Type.GetType(redirect.Type);
                    var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                    var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                    var from = Peers[redirect.Sender];
                    receivedEventArgs.From = from;
                    receivedEventArgs.Initialize(this, connection, obj, Length);
                    IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                    NonGenericEventArgs.From = from;
                    InvokeOnReceive(ref NonGenericEventArgs);
                    InvokeAllCallbacks(receivedEventArgs);

                    //OnReceive?.Invoke(ref args);
                    if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                        SendBroadcast(redirect, connection);
                    }
                } else {
                    var recipientConnection = Clients.Where((p) => p.Value.Identity.ID == redirect.Recipient && p.Value.Identity.ID != redirect.Sender)?.FirstOrDefault().Value;
                    if(recipientConnection != null) {
                        //var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);
                        //IReceivedEventArgs args = e;

                        // args.From = Peers[redirect.Sender];

                        // FIX THIS
                        Type redirectType = Type.GetType(redirect.Type);
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        var from = Peers[redirect.Sender];
                        receivedEventArgs.From = from;
                        receivedEventArgs.Initialize(this, connection, obj, Length);
                        IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                        NonGenericEventArgs.From = from;
                        InvokeOnReceive(ref NonGenericEventArgs);
                        InvokeAllCallbacks(receivedEventArgs);

                        //OnReceive?.Invoke(ref args);
                        if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                            Send(recipientConnection, redirect);
                        }
                    }
                }
            } else {
                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(objType);
                var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                receivedEventArgs.Initialize(this, connection, obj, Length);
                IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                InvokeOnReceive(ref NonGenericEventArgs);
                InvokeAllCallbacks(receivedEventArgs);
            }
        }
        /// <summary>
        /// Stop the client from updating metadata keys that are restricted, such as `Username`.
        /// <para>Not case sensitive.</para>
        /// </summary>
        public List<string> RestrictedMetadataKeys = new List<string>();
        private void OnReceived_MetadataKeyValue(ReceivedEventArgs<MetadataKeyValue> e) {
            if (string.IsNullOrEmpty(e.Object.Key)) return;
            if (!RestrictedMetadataKeys.Contains(e.Object.Key.ToLower())) {
                Clients[e.Connection.ID].SetMetaData(e.Object.Key, e.Object.Value);
                Peers[e.Connection.Identity.ID].SetMetaData(this, e.Object.Key, e.Object.Value);
            }
        }
        #endregion

        public WebSocketServer(int port, string name) {
            Peers = new PeerList(this);
            Name = name;
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback<MetadataKeyValue>(OnReceived_MetadataKeyValue);
            WireUpConnectionEvents();
        }

        public WebSocketServer(int port) {
            Peers = new PeerList(this);
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback<MetadataKeyValue>(OnReceived_MetadataKeyValue);
            WireUpConnectionEvents();
        }

        private bool AcceptNextConnection = true;
        public bool Listen() {
            _ct = new CancellationToken(IsListening);
            _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _Socket = _listenerSocket;
            var connection = new TcpConnection(this, _listenerSocket);
            Connection = connection;
            _listenerSocket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _listenerSocket.Listen(Options.Backlog > 0 ? Options.Backlog : 100);
            _isListening = true;

            if (Options.Logging) {
                LogAsync($"{Name} started listening on port {_port}.");
            }
            Task.Run(() => Listener());
            return _isListening;
        }

        private void Listener() {
            while (!_ct.IsCancellationRequested || !IsListening) {
                if (AcceptNextConnection && _listenerSocket != null) {
                    AcceptNextConnection = false;
                    _listenerSocket.BeginAccept(async (ar) => {
                        AcceptNextConnection = true;
                        if (_listenerSocket == null) return;
                        try {
                            Socket handler = _listenerSocket.EndAccept(ar);
                            var newConnection = NewConnection(ref handler);
                            await HandleRawSocketClient(newConnection);
                        } catch (Exception ex) {
                            InvokeOnError(ex);
                        }
                    }, null);
                } else {
                    Thread.Sleep(1);
                }
            }
        }

        public void StopListening() {
            if (_isListening) {
                //var argServer = (ISocket)this;
                //argServer.CloseConnection(argServer.Connection, DisconnectionReason.LocalSocketClosed);
                lock (Clients.Values) {
                    foreach (var client in Clients.Values) {
                        client.Close(this, DisconnectionReason.LocalSocketClosed);
                        MethodExtensions.TryInvoke(client.Socket.Close);
                    }
                }
                Peers.Clear();
                Clients.Clear();
                Connection.Close(this, DisconnectionReason.LocalSocketClosed);
                _listenerSocket?.Close();
                _listenerSocket = null;
                _handshakeCompleted.Clear();
                _isListening = false;
                LogAsync(new[] { $"[{Name}] stopped listening." });
                OnStoppedListening?.Invoke();
            }
        }

        public void Dispose() {
            if(IsListening) {
                InvokeOnDisposing();
                var argServer = (ISocket)this;
                Globals.UnregisterServer(ref argServer);
                StopListening();
                isDisposed = true;
            }
        }
    }
}
