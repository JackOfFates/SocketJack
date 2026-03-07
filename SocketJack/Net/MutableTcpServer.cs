using SocketJack.Extensions;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {

    /// <summary>
    /// Defines a protocol handler that can detect and process incoming data
    /// on a <see cref="MutableTcpServer"/>.
    /// </summary>
    public interface IProtocolHandler {

        /// <summary>
        /// Display name used for logging.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Inspects the first bytes received on a connection to determine
        /// whether this handler can process the data.
        /// </summary>
        bool CanHandle(byte[] data);

        /// <summary>
        /// Processes incoming data for a connection assigned to this handler.
        /// </summary>
        void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e);

        /// <summary>
        /// Called when a connection assigned to this handler disconnects.
        /// </summary>
        void OnDisconnected(MutableTcpServer server, NetworkConnection connection);
    }

    /// <summary>
    /// A TCP server that detects the incoming byte format (e.g. HTTP request vs
    /// SocketJack-framed JSON) and routes each connection to the appropriate
    /// <see cref="IProtocolHandler"/>. This allows a single listening port to serve
    /// multiple protocols simultaneously.
    /// <para>
    /// By default a <see cref="SocketJackProtocolHandler"/> and an
    /// <see cref="HttpProtocolHandler"/> are registered. Additional handlers can be
    /// added via <see cref="RegisterProtocol"/>; they are evaluated in registration
    /// order, with built-in handlers checked last.
    /// </para>
    /// </summary>
    public class MutableTcpServer : HttpServer {

        #region Fields

        private readonly List<IProtocolHandler> _handlers = new List<IProtocolHandler>();
        private readonly ConcurrentDictionary<Guid, IProtocolHandler> _connectionHandlers = new ConcurrentDictionary<Guid, IProtocolHandler>();

        private readonly HttpProtocolHandler _httpHandler;
        private readonly SocketJackProtocolHandler _socketJackHandler;
        private readonly RtmpDelegateHandler _rtmpDelegateHandler = new RtmpDelegateHandler();

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new <see cref="MutableTcpServer"/> that listens on the given port.
        /// </summary>
        public MutableTcpServer(int Port, string Name = "MutableTcpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;

            // Defer peer initialization until a connection is identified as SocketJack.
            // Without this, NewConnection sends SocketJack-framed peer data to every
            // connection (including HTTP clients), corrupting their response stream.
            DeferPeerInitialization = true;

            // Remove the base HttpServer's receive handler — MutableTcpServer routes
            // HTTP through HttpProtocolHandler via RouteReceive instead.
            this.OnReceive -= GetRequestAsync;

            _httpHandler = new HttpProtocolHandler();
            _socketJackHandler = new SocketJackProtocolHandler();

            this.OnReceive += RouteReceive;
            this.ClientDisconnected += OnClientDisconnected_Cleanup;
        }

        /// <summary>
        /// Initializes a new <see cref="MutableTcpServer"/> with explicit options.
        /// </summary>
        public MutableTcpServer(NetworkOptions Options, int Port, string Name = "MutableTcpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;

            // Defer peer initialization until a connection is identified as SocketJack.
            DeferPeerInitialization = true;

            // Remove the base HttpServer's receive handler — MutableTcpServer routes
            // HTTP through HttpProtocolHandler via RouteReceive instead.
            this.OnReceive -= GetRequestAsync;

            _httpHandler = new HttpProtocolHandler();
            _socketJackHandler = new SocketJackProtocolHandler();

            this.OnReceive += RouteReceive;
            this.ClientDisconnected += OnClientDisconnected_Cleanup;
        }

        #endregion

        #region Protocol Registration

        /// <summary>
        /// Registers a custom protocol handler. Custom handlers are evaluated before
        /// the built-in HTTP and SocketJack handlers.
        /// </summary>
        public void RegisterProtocol(IProtocolHandler handler) {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
        }

        /// <summary>
        /// Removes a previously registered custom protocol handler.
        /// </summary>
        public bool RemoveProtocol(IProtocolHandler handler) {
            return _handlers.Remove(handler);
        }

        /// <summary>
        /// Returns the built-in <see cref="HttpProtocolHandler"/> for configuring HTTP
        /// routes, directory mappings, and other HTTP-specific settings.
        /// </summary>
        public HttpProtocolHandler Http => _httpHandler;

        /// <summary>
        /// Returns the built-in <see cref="SocketJackProtocolHandler"/> for reference.
        /// SocketJack-protocol connections are also handled through the normal
        /// <see cref="NetworkBase.OnReceive"/> event and registered callbacks.
        /// </summary>
        public SocketJackProtocolHandler SocketJack => _socketJackHandler;

        /// <summary>
        /// Raised when a connection's first data is identified as SocketJack protocol.
        /// Use this instead of <see cref="TcpServer.ClientConnected"/> when you need
        /// to send SocketJack-framed objects only to SocketJack clients (not HTTP).
        /// </summary>
        public event Action<NetworkConnection> SocketJackClientConnected;

        #endregion

        #region Core Routing

        private void RouteReceive(ref IReceivedEventArgs e) {
            if (e == null || e.Obj == null || e.Connection == null)
                return;

            var connId = e.Connection.ID;

            // Fast path: connection already assigned to a handler.
            if (_connectionHandlers.TryGetValue(connId, out var assigned)) {
                // SocketJack connections: only intercept raw byte data from the network.
                // Deserialized objects dispatched through HandleReceive flow through
                // the normal OnReceive/callback pipeline, acting like a standard TcpServer.
                if (assigned == _socketJackHandler && !(e.Obj is List<byte>))
                    return;
                assigned.ProcessReceive(this, e.Connection, ref e);
                return;
            }

            // First data on this connection — detect the protocol.
            byte[] probe = TryGetRawBytes(e.Obj);
            if (probe == null || probe.Length == 0)
                return;

            // Check custom handlers first (in registration order).
            for (int i = 0; i < _handlers.Count; i++) {
                if (_handlers[i].CanHandle(probe)) {
                    _connectionHandlers.TryAdd(connId, _handlers[i]);
                    _handlers[i].ProcessReceive(this, e.Connection, ref e);
                    return;
                }
            }

            // Check built-in HTTP handler first — HTTP requests are short-lived
            // and must be answered promptly.  SocketJack is checked second so that
            // an HTTP client connecting to the same port is never misrouted.
            if (_httpHandler.CanHandle(probe)) {
                _connectionHandlers.TryAdd(connId, _httpHandler);
                e.Connection._Protocol = TcpProtocol.Http;
                _httpHandler.ProcessReceive(this, e.Connection, ref e);
                return;
            }

            // Check built-in SocketJack handler.
            if (_socketJackHandler.CanHandle(probe)) {
                _connectionHandlers.TryAdd(connId, _socketJackHandler);
                e.Connection._Protocol = TcpProtocol.SocketJack;
                _socketJackHandler.ProcessReceive(this, e.Connection, ref e);
                try { SocketJackClientConnected?.Invoke(e.Connection); } catch { }
                return;
            }

            // Check for RTMP connections (version byte 0x03 + C1 zero field at offset 5-8).
            // Delegate to the base HttpServer's GetRequestAsync which has full RTMP
            // handshake, session management, and publish route support built in.
            if (probe.Length >= 9
                && probe[0] == 0x03
                && probe[5] == 0 && probe[6] == 0 && probe[7] == 0 && probe[8] == 0) {
                _connectionHandlers.TryAdd(connId, _rtmpDelegateHandler);
                GetRequestAsync(ref e);
                return;
            }

            // No handler matched — data flows through to other OnReceive subscribers.
        }

        private void OnClientDisconnected_Cleanup(DisconnectedEventArgs args) {
            if (args.Connection == null) return;
            if (_connectionHandlers.TryRemove(args.Connection.ID, out var handler)) {
                handler.OnDisconnected(this, args.Connection);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Overrides peer synchronization so that only connections confirmed as
        /// SocketJack receive broadcast peer updates.  Without this filter,
        /// <see cref="ConnectedClientExtensions.SendBroadcast"/> pushes
        /// SocketJack-framed bytes into every connection's send queue —
        /// including HTTP connections — corrupting their response stream.
        /// </summary>
        protected internal override void SyncPeer(NetworkConnection Client) {
            Task.Run(() => {
                Client.Send(Peers.ToArrayWithLocal(Client));
                foreach (var kvp in Clients) {
                    var conn = kvp.Value;
                    if (conn == null || conn.ID == Client.ID || conn.Closed)
                        continue;
                    if (conn.Socket == null || !conn.Socket.Connected)
                        continue;
                    // Only send to connections that have been identified as SocketJack.
                    if (_connectionHandlers.TryGetValue(conn.ID, out var handler) && handler == _socketJackHandler) {
                        Send(conn, Client.Identity);
                    }
                }
            });
        }

        /// <inheritdoc />
        public override void SendBroadcast(object Obj) {
            SendBroadcastToSocketJackOnly(Obj, null);
        }

        /// <inheritdoc />
        public override void SendBroadcast(object Obj, NetworkConnection Except) {
            SendBroadcastToSocketJackOnly(Obj, Except);
        }

        /// <inheritdoc />
        public override void SendBroadcast(NetworkConnection[] ClientArray, object Obj, NetworkConnection Except = null) {
            if (ClientArray == null) return;
            foreach (var conn in ClientArray) {
                if (conn == null || conn.Closed)
                    continue;
                if (conn.Socket == null || !conn.Socket.Connected)
                    continue;
                if (Except != null && conn.ID == Except.ID)
                    continue;
                if (_connectionHandlers.TryGetValue(conn.ID, out var handler) && handler == _socketJackHandler) {
                    Send(conn, Obj);
                }
            }
        }

        private void SendBroadcastToSocketJackOnly(object Obj, NetworkConnection Except) {
            foreach (var kvp in Clients) {
                var conn = kvp.Value;
                if (conn == null || conn.Closed)
                    continue;
                if (conn.Socket == null || !conn.Socket.Connected)
                    continue;
                if (Except != null && conn.ID == Except.ID)
                    continue;
                // Only send to connections that have been identified as SocketJack.
                if (_connectionHandlers.TryGetValue(conn.ID, out var handler) && handler == _socketJackHandler) {
                    Send(conn, Obj);
                }
            }
        }

        internal static byte[] TryGetRawBytes(object obj) {
            if (obj == null)
                return null;

            if (obj is byte[] b)
                return b;

            if (obj is List<byte> lb)
                return lb.ToArray();

            if (obj is IEnumerable<byte> eb)
                return System.Linq.Enumerable.ToArray(eb);

            if (obj is ArraySegment<byte> seg) {
                if (seg.Array == null) return null;
                var outBytes = new byte[seg.Count];
                Buffer.BlockCopy(seg.Array, seg.Offset, outBytes, 0, seg.Count);
                return outBytes;
            }

            if (obj is string s) {
                if (string.IsNullOrEmpty(s))
                    return Array.Empty<byte>();
                return Encoding.UTF8.GetBytes(s);
            }

            return null;
        }

        #endregion

        #region Dispose

        protected override void Dispose(bool disposing) {
            this.OnReceive -= RouteReceive;
            this.ClientDisconnected -= OnClientDisconnected_Cleanup;
            _connectionHandlers.Clear();
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Lightweight handler that delegates RTMP connections to the base
        /// <see cref="HttpServer.GetRequestAsync"/> method, which contains
        /// the full RTMP handshake, session, and publish-route pipeline.
        /// </summary>
        private class RtmpDelegateHandler : IProtocolHandler {
            public string Name => "RTMP";
            public bool CanHandle(byte[] data) {
                return data != null && data.Length >= 9
                    && data[0] == 0x03
                    && data[5] == 0 && data[6] == 0 && data[7] == 0 && data[8] == 0;
            }
            public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
                server.GetRequestAsync(ref e);
            }
            public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) { }
        }
    }

    #region Built-in Protocol Handlers

    /// <summary>
    /// Detects and processes SocketJack length-prefixed framed messages.
    /// When <see cref="MutableTcpServer"/> receives raw bytes that begin with a
    /// 15-byte padded length header, this handler extracts the payload, deserializes
    /// it through the SocketJack serializer, and dispatches it through the normal
    /// <see cref="NetworkBase.HandleReceive"/> pipeline.
    /// </summary>
    public class SocketJackProtocolHandler : IProtocolHandler {

        public string Name => "SocketJack";

        /// <summary>
        /// Per-connection buffer for accumulating partial SocketJack frames.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, List<byte>> _buffers = new ConcurrentDictionary<Guid, List<byte>>();

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="data"/> starts with a
        /// 15-byte NUL-padded numeric length header (the SocketJack framing format).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 15)
                return false;

            // SocketJack frames start with a 15-char length field padded with NUL bytes.
            // Verify the first 15 bytes form a valid NUL-padded decimal number.
            bool foundDigit = false;
            bool pastPadding = false;
            for (int i = 0; i < 15; i++) {
                byte c = data[i];
                if (c == 0) {
                    // NUL padding is only valid before the digits start.
                    if (pastPadding)
                        return false;
                    continue;
                }
                if (c >= (byte)'0' && c <= (byte)'9') {
                    pastPadding = true;
                    foundDigit = true;
                } else {
                    return false;
                }
            }
            return foundDigit;
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            // Detect first data on a SocketJack connection — initialize peer if enabled.
            // This runs before ProcessBuffer so the peer is registered in Peers before
            // any deserialized message handlers (e.g. MetadataKeyValue) execute.
            bool isNewConnection = !_buffers.ContainsKey(connection.ID);
            if (isNewConnection && server.Options.UsePeerToPeer) {
                server.InitializePeer(connection);
            }

            var buffer = _buffers.GetOrAdd(connection.ID, _ => new List<byte>());
            lock (buffer) {
                buffer.AddRange(incoming);
                ProcessBuffer(buffer, connection, server);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _buffers.TryRemove(connection.ID, out _);
        }

        private static void ProcessBuffer(List<byte> buffer, NetworkConnection sender, MutableTcpServer target) {
            // Each SocketJack frame: [15-byte length header][payload bytes]
            while (buffer.Count >= 15) {
                string lengthStr = Encoding.UTF8.GetString(buffer.GetRange(0, 15).ToArray()).Trim('\0', ' ', '\r', '\n');
                if (!int.TryParse(lengthStr, out int payloadLength) || payloadLength <= 0)
                    break;

                int totalFrameSize = 15 + payloadLength;
                if (buffer.Count < totalFrameSize)
                    break; // Wait for more data.

                byte[] payload = new byte[payloadLength];
                buffer.CopyTo(15, payload, 0, payloadLength);
                buffer.RemoveRange(0, totalFrameSize);

                // Deserialize and dispatch through the normal SocketJack pipeline.
                Task.Run(() => {
                    try {
                        byte[] bytes = payload;
                        if (target.Options.UseCompression) {
                            bytes = target.Options.CompressionAlgorithm.Decompress(bytes);
                        }
                        Wrapper wrapper = target.Options.Serializer.Deserialize(bytes);
                        if (wrapper == null) return;
                        var valueType = wrapper.GetValueType();
                        if (wrapper.value != null || wrapper.Type != "") {
                            object unwrapped = wrapper.Unwrap(target);
                            if (unwrapped != null) {
                                target.HandleReceive(sender, unwrapped, valueType, payloadLength);
                                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(unwrapped.GetType());
                                var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                                receivedEventArgs.Initialize(target, sender, unwrapped, payloadLength);
                                target.InvokeCallbacks(receivedEventArgs);
                            }
                        }
                    } catch (Exception ex) {
                        target.InvokeOnError(sender, ex);
                    }
                });
            }
        }
    }

    /// <summary>
    /// Detects and processes HTTP requests. Provides the same route-mapping API
    /// surface as <see cref="HttpServer"/> (Map, MapStream, MapUploadStream,
    /// MapDirectory) but runs inside a <see cref="MutableTcpServer"/>.
    /// </summary>
    public class HttpProtocolHandler : IProtocolHandler {

        public string Name => "HTTP";

        #region Routes

        /// <inheritdoc cref="HttpServer.RouteHandler"/>
        public delegate object RouteHandler(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.RouteHandler{T}"/>
        public delegate object RouteHandler<T>(NetworkConnection connection, T body, HttpRequest request, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.StreamRouteHandler"/>
        public delegate Task StreamRouteHandler(NetworkConnection connection, HttpRequest request, ChunkedStream chunkedStream, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.UploadStreamRouteHandler"/>
        public delegate void UploadStreamRouteHandler(NetworkConnection connection, HttpRequest request, UploadStream uploadStream, CancellationToken cancellationToken);

        public event HttpServer.RequestHandler OnHttpRequest;

        private readonly Dictionary<string, Dictionary<string, RouteHandler>> _routes = new Dictionary<string, Dictionary<string, RouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, StreamRouteHandler>> _streamRoutes = new Dictionary<string, Dictionary<string, StreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, UploadStreamRouteHandler>> _uploadStreamRoutes = new Dictionary<string, Dictionary<string, UploadStreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _directoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<Guid, byte> _activeStreamConnections = new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, UploadStream> _activeUploadConnections = new ConcurrentDictionary<Guid, UploadStream>();
        private readonly ConcurrentDictionary<Guid, List<byte>> _requestBuffers = new ConcurrentDictionary<Guid, List<byte>>();

        #endregion

        #region Properties

        /// <summary>
        /// HTML served at the root path (<c>/</c>) for GET requests.
        /// </summary>
        public string IndexPageHtml { get; set; } = "<html><body><h1>SocketJack MutableTcpServer</h1></body></html>";

        /// <summary>
        /// Content served at <c>/robots.txt</c>. Set to <see langword="null"/> to disable.
        /// </summary>
        public string Robots { get; set; } = "User-agent: *\nAllow: /\n";

        /// <summary>
        /// Minimum body size in bytes before the server switches to chunked transfer encoding.
        /// </summary>
        public int ChunkedThreshold { get; set; } = 8192;

        /// <summary>
        /// Size of each chunk when using chunked transfer encoding.
        /// </summary>
        public int ChunkedSize { get; set; } = 4096;

        /// <summary>
        /// When <see langword="true"/>, mapped directories that contain no
        /// <c>index.html</c> / <c>index.htm</c> will return an auto-generated
        /// HTML listing of their contents. Defaults to <see langword="false"/>.
        /// A <c>.htaccess</c> file inside the directory can override this
        /// per-directory with <c>Options +Indexes</c> or <c>Options -Indexes</c>.
        /// </summary>
        public bool AllowDirectoryListing { get; set; } = false;

        #endregion

        #region Route Mapping

        /// <inheritdoc cref="HttpServer.Map(string, string, HttpServer.RouteHandler)"/>
        public void Map(string method, string path, RouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_routes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, RouteHandler>(StringComparer.OrdinalIgnoreCase);
                _routes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <inheritdoc cref="HttpServer.Map{T}(string, string, HttpServer.RouteHandler{T})"/>
        public void Map<T>(string method, string path, RouteHandler<T> handler) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Map(method, path, (RouteHandler)((connection, request, ct) => {
                T body = default;
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    var wrapped = connection.Parent.Options.Serializer.Deserialize(request.BodyBytes);
                    if (wrapped != null) {
                        var obj = wrapped.Unwrap(connection.Parent);
                        if (obj is T typed)
                            body = typed;
                    }
                }
                return handler(connection, body, request, ct);
            }));
        }

        /// <inheritdoc cref="HttpServer.RemoveRoute"/>
        public bool RemoveRoute(string method, string path) {
            if (!_routes.TryGetValue(method, out var byPath)) return false;
            return byPath.Remove(path);
        }

        /// <inheritdoc cref="HttpServer.MapStream"/>
        public void MapStream(string method, string path, StreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_streamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, StreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _streamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <inheritdoc cref="HttpServer.MapUploadStream"/>
        public void MapUploadStream(string method, string path, UploadStreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, UploadStreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _uploadStreamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <inheritdoc cref="HttpServer.RemoveUploadStreamRoute"/>
        public bool RemoveUploadStreamRoute(string method, string path) {
            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath)) return false;
            return byPath.Remove(path);
        }

        /// <inheritdoc cref="HttpServer.MapDirectory"/>
        public void MapDirectory(string urlPrefix, string localDirectory) {
            if (string.IsNullOrWhiteSpace(urlPrefix)) throw new ArgumentException("URL prefix is required.", nameof(urlPrefix));
            if (string.IsNullOrWhiteSpace(localDirectory)) throw new ArgumentException("Local directory is required.", nameof(localDirectory));
            if (!Directory.Exists(localDirectory)) throw new DirectoryNotFoundException("Directory not found: " + localDirectory);
            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized)) normalized = "/";
            _directoryMappings[normalized] = Path.GetFullPath(localDirectory);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix and configures a <c>.htaccess</c> file
        /// using the fluent <see cref="HtAccessBuilder"/> API for directory-level security.
        /// <para>
        /// Example:
        /// <code>
        /// server.Http.MapDirectory("/secure", @"C:\data", htaccess => {
        ///     htaccess.DenyDirectoryListing()
        ///             .AllowFrom("192.168.1.0/24")
        ///             .DenyFiles("*.log", "*.bak")
        ///             .RequireBasicAuth("Admin", "admin:secret");
        /// });
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="urlPrefix">The URL path prefix (e.g., <c>"/secure"</c>).</param>
        /// <param name="localDirectory">The absolute or relative path to the local directory.</param>
        /// <param name="configure">A delegate that configures the <see cref="HtAccessBuilder"/>.</param>
        public void MapDirectory(string urlPrefix, string localDirectory, Action<HtAccessBuilder> configure) {
            MapDirectory(urlPrefix, localDirectory);
            if (configure != null) {
                var builder = new HtAccessBuilder();
                configure(builder);
                builder.WriteTo(Path.GetFullPath(localDirectory));
            }
        }

        /// <inheritdoc cref="HttpServer.RemoveDirectoryMapping"/>
        public bool RemoveDirectoryMapping(string urlPrefix) {
            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized)) normalized = "/";
            return _directoryMappings.Remove(normalized);
        }

        #endregion

        #region IProtocolHandler

        /// <summary>
        /// Returns <see langword="true"/> when the first bytes look like an HTTP request
        /// (start with a known HTTP method keyword).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 3)
                return false;

            // Check for common HTTP method first bytes.
            byte first = data[0];
            if (first != (byte)'G' && first != (byte)'P' && first != (byte)'H' &&
                first != (byte)'D' && first != (byte)'O' && first != (byte)'T' &&
                first != (byte)'C')
                return false;

            // Verify the beginning spells out a known HTTP method.
            string prefix;
            int prefixLen = data.Length > 10 ? 10 : data.Length;
            prefix = Encoding.UTF8.GetString(data, 0, prefixLen);

            return prefix.StartsWith("GET ") || prefix.StartsWith("POST ") ||
                   prefix.StartsWith("PUT ") || prefix.StartsWith("DELETE ") ||
                   prefix.StartsWith("HEAD ") || prefix.StartsWith("OPTIONS ") ||
                   prefix.StartsWith("PATCH ") || prefix.StartsWith("TRACE ") ||
                   prefix.StartsWith("CONNECT ");
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            try {
                // Active stream handler — skip HTTP parsing.
                if (_activeStreamConnections.ContainsKey(connection.ID))
                    return;

                // Active upload stream — forward raw data.
                if (_activeUploadConnections.TryGetValue(connection.ID, out var activeUpload)) {
                    var uploadBytes = MutableTcpServer.TryGetRawBytes(e.Obj);
                    if (uploadBytes != null && uploadBytes.Length > 0)
                        activeUpload.Enqueue(uploadBytes);
                    return;
                }

                byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
                if (incoming == null || incoming.Length == 0)
                    return;

                // Accumulate incoming bytes into a per-connection buffer so that
                // HTTP requests spanning multiple TCP segments are fully received
                // before parsing. Without this, large POST uploads are parsed from
                // the first segment alone and the connection is closed prematurely.
                var buffer = _requestBuffers.GetOrAdd(connection.ID, _ => new List<byte>());
                byte[] rawRequestBytes;
                lock (buffer) {
                    buffer.AddRange(incoming);

                    // Wait until complete HTTP headers have arrived (\r\n\r\n).
                    int hdrEnd = FindHeaderEnd(buffer);
                    if (hdrEnd == -1)
                        return;

                    // If Content-Length is present, wait for the full body unless
                    // an upload-stream route matches (those start processing with
                    // partial body data and receive the rest incrementally).
                    long contentLength = ExtractContentLength(buffer, hdrEnd);
                    if (contentLength > 0 && buffer.Count < hdrEnd + contentLength) {
                        if (!IsUploadStreamRoute(buffer, hdrEnd))
                            return;
                    }

                    rawRequestBytes = buffer.ToArray();
                    buffer.Clear();
                }
                _requestBuffers.TryRemove(connection.ID, out _);

                var request = HttpServer.ParseHttpRequest(rawRequestBytes);

                var context = new HttpContext {
                    Request = request,
                    Connection = connection,
                    cancellationToken = default
                };

                var ct = default(CancellationToken);

                // Streaming routes
                var streamHandler = ResolveStreamRoute(request);
                if (streamHandler != null) {
                    if (server.Options.Logging) {
                        try {
                            var ep = connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                            server.Log(request.Method + " " + request.Path + " > STREAM - " + ep);
                        } catch { }
                    }
                    _activeStreamConnections.TryAdd(connection.ID, 0);
                    var conn = connection;
                    var response = context.Response;
                    Task.Factory.StartNew(async () => {
                        var chunked = new ChunkedStream(conn.Stream, response);
                        try {
                            await streamHandler(conn, request, chunked, ct);
                        } catch (Exception ex) {
                            server.InvokeOnError(conn, ex);
                        } finally {
                            chunked.Finish();
                            _activeStreamConnections.TryRemove(conn.ID, out _);
                        }
                    }, TaskCreationOptions.LongRunning);
                    return;
                }

                // Upload-stream routes
                var uploadHandler = ResolveUploadStreamRoute(request);
                if (uploadHandler != null) {
                    if (server.Options.Logging) {
                        try {
                            var ep = connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                            server.Log(request.Method + " " + request.Path + " > UPLOAD - " + ep);
                        } catch { }
                    }
                    var upload = new UploadStream(connection);
                    _activeUploadConnections.TryAdd(connection.ID, upload);
                    if (request.BodyBytes != null && request.BodyBytes.Length > 0)
                        upload.Enqueue(request.BodyBytes);

                    // If the full body was already received in the initial request,
                    // complete the upload stream so the handler's ReadAsync loop
                    // terminates instead of blocking forever waiting for data.
                    if (IsBodyComplete(request)) {
                        upload.Complete();
                        _activeUploadConnections.TryRemove(connection.ID, out _);
                    }

                    var connId = connection.ID;
                    var conn = connection;
                    Task.Run(() => {
                        try {
                            uploadHandler(conn, request, upload, ct);
                        } catch {
                        } finally {
                            upload.Complete();
                            _activeUploadConnections.TryRemove(connId, out _);
                        }
                    });
                    return;
                }

                // Normal route resolution
                bool handledByRoute = false;
                if (_routes.Count > 0 || IndexPageHtml != null || Robots != null) {
                    var routeResult = ResolveRouteObject(server, connection, request, ct);
                    if (routeResult != null) {
                        handledByRoute = true;
                        context.Response.Body = routeResult;

                        var trimmed = routeResult.TrimStart();
                        bool isIndex = request.Method != null &&
                            (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) &&
                            (request.Path == "/" || string.IsNullOrEmpty(request.Path)) && IndexPageHtml != null;
                        if (isIndex || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                            context.Response.ContentType = "text/html";
                        else if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
                            context.Response.ContentType = "application/json";
                        else
                            context.Response.ContentType = "text/plain";
                    }
                }

                // Directory mappings
                if (!handledByRoute) {
                    var normalizedPath = NormalizePath(request.Path);
                    if (TryResolveDirectoryFile(request.Method, normalizedPath, connection, request, out var fileBytes, out var fileMime, out var htAccessResult, out var htAuthRealm, out var htExtraHeaders)) {
                        if (htAccessResult == HtAccessResult.Denied) {
                            context.StatusCodeNumber = 403;
                            context.ReasonPhrase = "Forbidden";
                            context.Response.Body = "403 Forbidden";
                            context.Response.ContentType = "text/plain";
                            handledByRoute = true;
                        } else if (htAccessResult == HtAccessResult.AuthRequired) {
                            context.StatusCodeNumber = 401;
                            context.ReasonPhrase = "Unauthorized";
                            context.Response.Body = "401 Unauthorized";
                            context.Response.ContentType = "text/plain";
                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"" + (htAuthRealm ?? "Restricted") + "\"";
                            handledByRoute = true;
                        } else {
                            handledByRoute = true;
                            context.Response.BodyBytes = fileBytes;
                            context.Response.ContentType = fileMime;
                        }
                        // Apply extra headers from .htaccess Header directives.
                        if (htExtraHeaders != null && htExtraHeaders.Count > 0) {
                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in htExtraHeaders)
                                context.Response.Headers[kv.Key] = kv.Value;
                        }
                    }
                }

                // Fire event for unhandled requests.
                if (!handledByRoute) {
                    OnHttpRequest?.Invoke(connection, ref context, ct);
                }

                // Standard response headers.
                if (context.Response != null && context.Response.Headers != null) {
                    if (!context.Response.Headers.ContainsKey("Server"))
                        context.Response.Headers["Server"] = "SocketJack";
                    if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    if (!context.Response.Headers.ContainsKey("Date"))
                        context.Response.Headers["Date"] = DateTime.UtcNow.ToString("R");
                    if (!context.Response.Headers.ContainsKey("Connection"))
                        context.Response.Headers["Connection"] = "close";
                }

                // Ensure the response always carries a valid status code.
                if (context.StatusCodeNumber <= 0)
                    context.StatusCodeNumber = 200;
                if (string.IsNullOrWhiteSpace(context.ReasonPhrase))
                    context.ReasonPhrase = "OK";

                // Disable Nagle's algorithm so the response is pushed to the
                // network immediately rather than waiting for a full TCP segment.
                try { connection.Socket.NoDelay = true; } catch { }

                // Capture the stream reference once — a background thread
                // (connection tester, receive loop) can dispose the underlying
                // socket between property accesses, turning a subsequent
                // connection.Stream call into null or an ObjectDisposedException.
                var responseStream = connection.Stream;
                if (responseStream != null && !connection.Closed && !connection.Closing) {
                    // Write response.
                    context.Response.EnsureBodyBytes();
                    int bodyLen = context.Response.BodyBytes != null ? context.Response.BodyBytes.Length : 0;

                    if (server.Options.Logging) {
                        try {
                            var ua = request.Headers != null && request.Headers.ContainsKey("User-Agent") ? request.Headers["User-Agent"] : "";
                            var ep = connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                            server.Log(request.Method + " " + request.Path + " > " + context.StatusCodeNumber + " (" + bodyLen + " bytes) UA: " + ua + " - " + ep);
                        } catch { }
                    }
                    bool isHead = request.Method != null && request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

                    try {
                        if (isHead) {
                            var (hdr, _) = context.Response.ToBytesWithHeader();
                            if (hdr != null && hdr.Length > 0)
                                responseStream.Write(hdr, 0, hdr.Length);
                            responseStream.Flush();
                        } else if (bodyLen > ChunkedThreshold) {
                            context.Response.WriteChunkedTo(responseStream, ChunkedSize);
                        } else {
                            var (hdr, body) = context.Response.ToBytesWithHeader();
                            if (hdr != null && hdr.Length > 0)
                                responseStream.Write(hdr, 0, hdr.Length);
                            if (body != null && body.Length > 0)
                                responseStream.Write(body, 0, body.Length);
                            responseStream.Flush();
                        }
                    } catch (ObjectDisposedException) {
                    } catch (IOException) { }
                }

                // Graceful HTTP close sequence:
                // 1. Set linger so the OS waits for the send buffer to drain on Close().
                // 2. Shut down the send direction first — this queues a FIN *after*
                //    any remaining response bytes, preventing an RST.
                // 3. Call Close() for resource cleanup (its internal Shutdown(Both)
                //    is harmless since Send is already shut down).
                try {
                    var socket = connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
            } catch (Exception ex) {
                server.InvokeOnError(connection, ex);
                // Ensure the client always receives a valid HTTP response,
                // even when an exception occurs during request processing.
                try {
                    var errStream = connection.Stream;
                    if (errStream != null && !connection.Closed && !connection.Closing) {
                        var errBody = Encoding.UTF8.GetBytes("500 Internal Server Error");
                        var errHeader = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 500 Internal Server Error\r\n" +
                            "Content-Type: text/plain\r\n" +
                            "Content-Length: " + errBody.Length + "\r\n" +
                            "Connection: close\r\n\r\n");
                        errStream.Write(errHeader, 0, errHeader.Length);
                        errStream.Write(errBody, 0, errBody.Length);
                        errStream.Flush();
                    }
                } catch { }
                try {
                    var socket = connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _requestBuffers.TryRemove(connection.ID, out _);
            _activeStreamConnections.TryRemove(connection.ID, out _);
            if (_activeUploadConnections.TryRemove(connection.ID, out var upload))
                upload.Complete();
        }

        #endregion

        #region Internal Resolution

        private string ResolveRouteObject(MutableTcpServer server, NetworkConnection connection, HttpRequest request, CancellationToken ct) {
            if (request == null) return null;
            string method = request.Method;
            string path = NormalizePath(request.Path);

            if (method != null && path != null) {
                if (_routes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return SerializeRouteResult(server, handler(connection, request, ct));
                }
                if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) {
                    if (_routes.TryGetValue("GET", out var getByPath)) {
                        if (getByPath.TryGetValue(path, out var getHandler))
                            return SerializeRouteResult(server, getHandler(connection, request, ct));
                    }
                }
            }

            if (method != null && (method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))) {
                if (path == "/" || string.IsNullOrEmpty(path))
                    return IndexPageHtml;
                if (path == "/robots.txt" && Robots != null)
                    return Robots;
            }

            return null;
        }

        private static string SerializeRouteResult(MutableTcpServer server, object result) {
            if (result == null) return null;
            if (result is string s) return s;
            return Encoding.UTF8.GetString(server.Options.Serializer.Serialize(result));
        }

        private StreamRouteHandler ResolveStreamRoute(HttpRequest request) {
            if (request == null || _streamRoutes.Count == 0) return null;
            string method = request.Method;
            string path = NormalizePath(request.Path);
            if (method != null && path != null) {
                if (_streamRoutes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler;
                }
            }
            return null;
        }

        private UploadStreamRouteHandler ResolveUploadStreamRoute(HttpRequest request) {
            if (request == null || _uploadStreamRoutes.Count == 0) return null;
            string method = request.Method;
            string path = NormalizePath(request.Path);
            if (method != null && path != null) {
                if (_uploadStreamRoutes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler;
                }
            }
            return null;
        }

        private bool TryResolveDirectoryFile(string method, string path, NetworkConnection connection, HttpRequest request, out byte[] fileBytes, out string contentType, out HtAccessResult accessResult, out string authRealm, out Dictionary<string, string> extraHeaders) {
            fileBytes = null;
            contentType = null;
            accessResult = HtAccessResult.Allowed;
            authRealm = null;
            extraHeaders = null;

            if (_directoryMappings.Count == 0) return false;
            if (method == null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (string.IsNullOrEmpty(path)) return false;

            // Block .htaccess from ever being served to clients.
            var fileName = path.Substring(path.LastIndexOf('/') + 1);
            if (fileName.Equals(".htaccess", StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var kv in _directoryMappings) {
                var prefix = kv.Key;
                var localDir = kv.Value;
                string relativePath = null;

                if (prefix == "/") {
                    relativePath = path;
                } else if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) {
                    relativePath = "/";
                } else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) {
                    relativePath = path.Substring(prefix.Length);
                }
                if (relativePath == null) continue;

                var localRelative = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = string.IsNullOrEmpty(localRelative) ? localDir : Path.Combine(localDir, localRelative);
                fullPath = Path.GetFullPath(fullPath);

                if (!fullPath.StartsWith(localDir, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Evaluate .htaccess access rules before serving any content.
                string clientIp = null;
                string authHeader = null;
                try { clientIp = connection?.EndPoint?.Address?.ToString(); } catch { }
                try { if (request?.Headers != null) request.Headers.TryGetValue("Authorization", out authHeader); } catch { }
                string resolvedFileName = Path.GetFileName(fullPath);
                string htDir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                accessResult = HtAccessEvaluator.Evaluate(htDir, resolvedFileName, clientIp, authHeader, out authRealm, out extraHeaders);
                if (accessResult != HtAccessResult.Allowed) {
                    // Return true so the caller knows the route matched, but with a non-Allowed result.
                    return true;
                }

                if (Directory.Exists(fullPath)) {
                    var indexHtml = Path.Combine(fullPath, "index.html");
                    var indexHtm = Path.Combine(fullPath, "index.htm");
                    if (File.Exists(indexHtml)) fullPath = indexHtml;
                    else if (File.Exists(indexHtm)) fullPath = indexHtm;
                    else {
                        // Check if directory listing is allowed.
                        if (IsDirectoryListingAllowed(fullPath)) {
                            fileBytes = Encoding.UTF8.GetBytes(GenerateDirectoryListing(fullPath, path));
                            contentType = "text/html";
                            return true;
                        }
                        continue;
                    }
                }

                if (!File.Exists(fullPath)) continue;

                // Block .htaccess at the file level as well.
                if (Path.GetFileName(fullPath).Equals(".htaccess", StringComparison.OrdinalIgnoreCase))
                    return false;

                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    fileBytes = new byte[fs.Length];
                    int totalRead = 0;
                    while (totalRead < fileBytes.Length) {
                        int read = fs.Read(fileBytes, totalRead, fileBytes.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                }
                contentType = GetMimeType(Path.GetExtension(fullPath));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Determines whether directory listing is allowed for the given local
        /// directory. Checks for a <c>.htaccess</c> file first; if it contains
        /// <c>Options +Indexes</c> listing is allowed, <c>Options -Indexes</c>
        /// denies it. Falls back to <see cref="AllowDirectoryListing"/>.
        /// </summary>
        private bool IsDirectoryListingAllowed(string localDirectory) {
            try {
                var htaccess = Path.Combine(localDirectory, ".htaccess");
                if (File.Exists(htaccess)) {
                    var lines = File.ReadAllLines(htaccess);
                    for (int i = lines.Length - 1; i >= 0; i--) {
                        var line = lines[i].Trim();
                        if (line.StartsWith("#") || line.Length == 0) continue;
                        if (line.StartsWith("Options", StringComparison.OrdinalIgnoreCase)) {
                            if (line.IndexOf("+Indexes", StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                            if (line.IndexOf("-Indexes", StringComparison.OrdinalIgnoreCase) >= 0)
                                return false;
                        }
                    }
                }
            } catch { }
            return AllowDirectoryListing;
        }

        /// <summary>
        /// Generates an HTML page listing the contents of a directory.
        /// </summary>
        private static string GenerateDirectoryListing(string localDirectory, string urlPath) {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"UTF-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>Index of " + System.Net.WebUtility.HtmlEncode(urlPath) + "</title>");
            sb.Append("<style>");
            sb.Append("*{margin:0;padding:0;box-sizing:border-box}");
            sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;background:#111;color:#e8e8e8;padding:2rem}");
            sb.Append("h1{font-weight:300;margin-bottom:1rem;color:#0078d4}");
            sb.Append("table{width:100%;border-collapse:collapse}");
            sb.Append("th,td{text-align:left;padding:.5rem .75rem;border-bottom:1px solid rgba(255,255,255,.08)}");
            sb.Append("th{color:#888;font-size:.8rem;text-transform:uppercase;letter-spacing:.05em}");
            sb.Append("a{color:#0078d4;text-decoration:none}a:hover{text-decoration:underline}");
            sb.Append("tr:hover{background:rgba(0,120,212,.06)}");
            sb.Append(".size{color:#888;font-variant-numeric:tabular-nums}");
            sb.Append(".date{color:#888}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>Index of " + System.Net.WebUtility.HtmlEncode(urlPath) + "</h1>");
            sb.Append("<table><thead><tr><th>Name</th><th>Size</th><th>Modified</th></tr></thead><tbody>");

            // Parent directory link
            if (urlPath != "/" && urlPath.Length > 1) {
                var parent = urlPath.TrimEnd('/');
                var lastSlash = parent.LastIndexOf('/');
                parent = lastSlash > 0 ? parent.Substring(0, lastSlash) : "/";
                sb.Append("<tr><td><a href=\"" + parent + "\">⬆ ..</a></td><td></td><td></td></tr>");
            }

            try {
                // Directories first
                foreach (var dir in Directory.GetDirectories(localDirectory)) {
                    var name = Path.GetFileName(dir);
                    var info = new DirectoryInfo(dir);
                    var href = urlPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) + "/";
                    sb.Append("<tr><td><a href=\"" + href + "\">📁 " + System.Net.WebUtility.HtmlEncode(name) + "/</a></td>");
                    sb.Append("<td class=\"size\">—</td>");
                    sb.Append("<td class=\"date\">" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>");
                }
                // Files (skip .htaccess)
                foreach (var file in Directory.GetFiles(localDirectory)) {
                    var name = Path.GetFileName(file);
                    if (name.Equals(".htaccess", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    var href = urlPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
                    sb.Append("<tr><td><a href=\"" + href + "\">" + System.Net.WebUtility.HtmlEncode(name) + "</a></td>");
                    sb.Append("<td class=\"size\">" + FormatFileSize(info.Length) + "</td>");
                    sb.Append("<td class=\"date\">" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>");
                }
            } catch { }

            sb.Append("</tbody></table></body></html>");
            return sb.ToString();
        }

        private static string FormatFileSize(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("N1") + " KB";
            if (bytes < 1024 * 1024 * 1024L) return (bytes / (1024.0 * 1024)).ToString("N1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";
        }

        private static int FindHeaderEnd(List<byte> buffer) {
            for (int i = 0; i <= buffer.Count - 4; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    return i + 4;
            }
            return -1;
        }

        private static long ExtractContentLength(List<byte> buffer, int hdrEnd) {
            var headerText = Encoding.UTF8.GetString(buffer.GetRange(0, hdrEnd).ToArray());
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                    if (long.TryParse(line.Substring(15).Trim(), out long cl))
                        return cl;
                }
            }
            return -1;
        }

        private bool IsUploadStreamRoute(List<byte> buffer, int hdrEnd) {
            if (_uploadStreamRoutes.Count == 0) return false;
            int lineEnd = -1;
            for (int i = 0; i < Math.Min(hdrEnd, buffer.Count) - 1; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n') {
                    lineEnd = i;
                    break;
                }
            }
            if (lineEnd <= 0) return false;
            var requestLine = Encoding.UTF8.GetString(buffer.GetRange(0, lineEnd).ToArray());
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return false;
            var method = parts[0];
            var path = NormalizePath(parts[1]);
            return method != null && path != null &&
                   _uploadStreamRoutes.TryGetValue(method, out var byPath) &&
                   byPath.ContainsKey(path);
        }

        private static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path)) return path;
            var q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            // URL-decode the path portion so percent-encoded segments
            // (e.g. %20, %2F) match the plain-text route registration.
            try { path = Uri.UnescapeDataString(path); } catch { }
            if (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the request body appears to be fully
        /// available in <see cref="HttpRequest.BodyBytes"/> (as opposed to a streaming
        /// upload that arrives over multiple reads).
        /// </summary>
        private static bool IsBodyComplete(HttpRequest request) {
            if (request.Headers != null
                && request.Headers.TryGetValue("Content-Length", out var clStr)
                && long.TryParse(clStr, out var cl)
                && cl > 0) {
                return request.BodyBytes != null && request.BodyBytes.Length >= cl;
            }
            if (request.Headers != null
                && request.Headers.TryGetValue("Transfer-Encoding", out var te)
                && te != null
                && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) {
                return false;
            }
            return true;
        }

        private static string GetMimeType(string extension) {
            if (string.IsNullOrEmpty(extension)) return "application/octet-stream";
            switch (extension.ToLowerInvariant()) {
                case ".html":
                case ".htm":  return "text/html";
                case ".css":  return "text/css";
                case ".js":   return "application/javascript";
                case ".json": return "application/json";
                case ".xml":  return "application/xml";
                case ".txt":  return "text/plain";
                case ".csv":  return "text/csv";
                case ".svg":  return "image/svg+xml";
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".ico":  return "image/x-icon";
                case ".webp": return "image/webp";
                case ".bmp":  return "image/bmp";
                case ".woff": return "font/woff";
                case ".woff2":return "font/woff2";
                case ".ttf":  return "font/ttf";
                case ".otf":  return "font/otf";
                case ".eot":  return "application/vnd.ms-fontobject";
                case ".pdf":  return "application/pdf";
                case ".zip":  return "application/zip";
                case ".mp3":  return "audio/mpeg";
                case ".mp4":  return "video/mp4";
                case ".webm": return "video/webm";
                case ".ogg":  return "audio/ogg";
                case ".wav":  return "audio/wav";
                case ".wasm": return "application/wasm";
                default:      return "application/octet-stream";
            }
        }

        #endregion
    }

    #endregion
}
