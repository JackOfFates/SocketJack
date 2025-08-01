using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using static SocketJack.Net.TcpBase;

namespace SocketJack.Net {
    public class WebSocketServer : IDisposable, ISocket {

        #region Internal
        private readonly int _port;
        private Socket _listenerSocket;
        private CancellationTokenSource _cts;
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

        private async Task HandleRawSocketClient(TcpConnection connection, Socket clientSocket) {
            if (connection._Stream == null) {
                connection._Stream = new NetworkStream(clientSocket, ownsSocket: false);
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
                clientSocket.Close();
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
                clientSocket.Close();
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
            await HandleWebSocketFrames(connection, clientSocket, connection.Stream);
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

        private async Task HandleWebSocketFrames(TcpConnection connection, Socket socket, Stream stream) {
            var buffer = new byte[Options.DownloadBufferSize];
            try {
                while (socket.Connected) {
                    int header = await ReadByteAsync(stream);
                    if (header == -1) break;
                    bool fin = (header & 0x80) != 0;
                    int opcode = header & 0x0F;
                    int lengthByte = await ReadByteAsync(stream);
                    if (lengthByte == -1) break;
                    bool mask = (lengthByte & 0x80) != 0;
                    int payloadLen = lengthByte & 0x7F;
                    if (payloadLen == 126) {
                        var ext = new byte[2];
                        await stream.ReadAsync(ext, 0, 2);
                        payloadLen = (ext[0] << 8) | ext[1];
                    } else if (payloadLen == 127) {
                        var ext = new byte[8];
                        await stream.ReadAsync(ext, 0, 8);
                        payloadLen = (int)BitConverter.ToUInt64(ext, 0);
                    }
                    byte[] maskingKey = null;
                    if (mask) {
                        maskingKey = new byte[4];
                        await stream.ReadAsync(maskingKey, 0, 4);
                    }
                    int read = 0;
                    var payload = new byte[payloadLen];
                    while (read < payloadLen) {
                        int r = await stream.ReadAsync(payload, read, payloadLen - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    if (mask && maskingKey != null) {
                        for (int i = 0; i < payload.Length; i++)
                            payload[i] ^= maskingKey[i % 4];
                    }
                    if (opcode == 0x8) // Close
                        break;
                    // Handle text (0x1) and binary (0x2) opcodes
                    if (opcode == 0x1 || opcode == 0x2) {
                        _handshakeCompleted[connection.ID] = true;

                        byte[] data = payload;
                        if (Options.UseCompression && opcode == 0x2) {
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
                    }
                }
            } catch (Exception ex) {
                if (Options.Logging && socket.Connected) {
                    LogAsync($"[{Name}] ERROR: {ex.Message}");
                    InvokeOnError(connection, ex);
                }
            } finally {
                connection.CloseConnection();
            }
        }

        private TcpConnection NewConnection(ref Socket handler) {
            var newConnection = new TcpConnection(this, handler);
            newConnection.ID = Guid.NewGuid();
            _handshakeCompleted[newConnection.ID] = false;
            newConnection._Identity = Identifier.Create(Guid.NewGuid(), true, handler.RemoteEndPoint.ToString());
            Clients.AddOrUpdate(newConnection.ID, newConnection);
            InitializePeer(newConnection);
            SendConstructors(ref newConnection);
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
        public delegate void PeerConnectedEventHandler(object sender, Identifier Peer);

        /// <summary>
        /// Fired when a user disconnects from the server.
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(object sender, Identifier Peer);

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
        public void InvokePeerConnectionRequest(object sender, ref PeerServer Server) {
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
            PeerConnected?.Invoke(sender, Peer);
        }
        public void InvokePeerDisconnected(object sender, Identifier Peer) {
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
        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection == null) return;
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
            Connection.Close(this, Reason);
        }
        public void InvokeOnDisposing() {
            OnDisposing?.Invoke();
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
                Peers.AddOrUpdate(peer);
                PeerUpdate?.Invoke(this, peer);
                PeerConnected?.Invoke(this, peer);
                var peerArray = Peers.ToArrayWithLocal(newConnection);
                Send(newConnection, peerArray);
                SendBroadcast(Identifier.Create(peer.ID, false), newConnection);
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
                    LogAsync($"[{Name}] Client Disconnected -> {e.Connection.Identity.ID.ToUpper()} ({e.Connection.Socket.RemoteEndPoint})");
                }
            };
        }
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
        public void LogError(Exception e) {
            if (Options.Logging) {
                InvokeOnError(Connection, e);
            }
        }
        #endregion

        #region Sending

        public void Send(TcpConnection Client, object Obj) {
            if (Client == null || Client.Closed || Client.Socket == null || !Client.Socket.Connected) return;
            if (Client.Stream == null) Client._Stream = new NetworkStream(Client.Socket, ownsSocket: false);
            Task.Run(() => {
                while (!_handshakeCompleted.TryGetValue(Client.ID, out var completed) || !completed || Client.IsSending) {
                    Thread.Sleep(100);
                }
                try {
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
                    } else if (payload.Length <= 65535) {
                        frame.Add(126);
                        frame.Add((byte)((payload.Length >> 8) & 0xFF));
                        frame.Add((byte)(payload.Length & 0xFF));
                    } else {
                        frame.Add(127);
                        ulong len = (ulong)payload.Length;
                        for (int i = 7; i >= 0; i--)
                            frame.Add((byte)((len >> (8 * i)) & 0xFF));
                    }
                    frame.AddRange(payload);

                    if (Client.Stream == null) return;
                    Client.Stream.Write(frame.ToArray(), 0, frame.Count);
                    Client.Stream.Flush();
                    Type objType = Obj.GetType();
                    InvokeInternalSendEvent(Client, objType, Obj, payload.Length);
                    InvokeOnSent(new SentEventArgs(this, Client, payload.Length));

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

        public void Send(TcpConnection Client, Identifier recipient, object Obj) {
            if (recipient == null) return;
            var redirect = new PeerRedirect(Client?.ID.ToString(), recipient.ID, Obj);
            Send(Client, redirect);
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
                    var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);
                    IReceivedEventArgs args = e;

                    args.From = Peers[redirect.Sender];
                    OnReceive?.Invoke(ref args);

                    if (!e.CancelPeerRedirect) {
                        SendBroadcast(redirect, connection);
                    }
                        
                } else {
                    if (P2P_Clients.TryGetValue(redirect.Recipient, out var recipientConnection)) {
                        if (recipientConnection.Socket.Connected) {
                            var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);
                            IReceivedEventArgs args = e;
                            args.From = connection.Identity;
                            OnReceive?.Invoke(ref args);

                            if (!e.CancelPeerRedirect) {
                                Send(recipientConnection, redirect);
                            }
                        } else {
                            if (Options.Logging) LogAsync($"[{Name}] Recipient {redirect.Recipient} is not connected.");
                        }
                    } else {
                        if (Options.Logging) LogAsync($"[{Name}] Recipient {redirect.Recipient} not found.");
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
        private void OnReceived_MetadataKeyValue(ReceivedEventArgs<MetadataKeyValue> Args) {
            if (string.IsNullOrEmpty(Args.Object.Key)) return;
            if (!RestrictedMetadataKeys.Contains(Args.Object.Key.ToLower())) {
                Clients[Args.Connection.ID].SetMetaData(Args.Object.Key, Args.Object.Value);
                Peers[Args.Connection.Identity.ID].SetMetaData(this, Args.Object.Key, Args.Object.Value);
            }
        }
        #endregion

        public WebSocketServer(int port, string name) {
            Peers = new PeerList(this);
            Name = name;
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            WireUpConnectionEvents();
        }

        public WebSocketServer(int port) {
            Peers = new PeerList(this);
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            WireUpConnectionEvents();
        }

        public bool Listen() {
            _cts = new CancellationTokenSource();
            _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connection = new TcpConnection(this, _listenerSocket);
            this.Connection = connection;
            _listenerSocket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _listenerSocket.Listen(Options.Backlog > 0 ? Options.Backlog : 100);
            _isListening = true;

            if (Options.Logging) {
                LogAsync($"{Name} started listening on port {_port}.");
            }
            Task.Run(async () => {
                try {
                    while (!_cts.Token.IsCancellationRequested) {
                        var clientSocket = await _listenerSocket.AcceptAsync();
                        var newConnection = NewConnection(ref clientSocket);
                        _ = Task.Run(async () => await HandleRawSocketClient(newConnection, clientSocket));
                    }
                } catch { StopListening(); }
            });
            return _isListening;
        }

        public void StopListening() {
            if (_isListening) {
                var argServer = (ISocket)this;
                argServer.CloseConnection(argServer.Connection, DisconnectionReason.LocalSocketClosed);
                Peers.Clear();
                Clients.Clear();
                _cts?.Cancel();
                _listenerSocket?.Close();
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
