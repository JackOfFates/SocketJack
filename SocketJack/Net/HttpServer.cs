
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Serialization;

namespace SocketJack.Net {

    public class HttpServer : TcpServer {

        //new private event PeerConnectionRequestEventHandler PeerConnectionRequest;

        public event RequestHandler OnHttpRequest;
        public delegate void RequestHandler(NetworkConnection Connection, ref HttpContext context, CancellationToken cancellationToken);

        public delegate object RouteHandler(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Typed route handler delegate. The request body is automatically deserialized to <typeparamref name="T"/>
        /// and passed as the <paramref name="body"/> parameter.
        /// </summary>
        public delegate object RouteHandler<T>(NetworkConnection connection, T body, HttpRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for streaming routes. The handler receives a <see cref="ChunkedStream"/>
        /// that sends each <see cref="ChunkedStream.WriteLine"/> as an HTTP chunk, keeping
        /// the connection open until the handler returns or calls <see cref="ChunkedStream.Finish"/>.
        /// </summary>
        public delegate Task StreamRouteHandler(NetworkConnection connection, HttpRequest request, ChunkedStream chunkedStream, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for upload-streaming routes. The handler receives an <see cref="UploadStream"/>
        /// from which it can read incoming data chunks (e.g., OBS video) as they arrive on the
        /// connection. The handler runs on a background task and should return when it is done
        /// consuming data or when <see cref="UploadStream.ReadAsync"/> returns <see langword="null"/>.
        /// </summary>
        public delegate void UploadStreamRouteHandler(NetworkConnection connection, HttpRequest request, UploadStream uploadStream, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for RTMP publish routes. The handler receives an <see cref="UploadStream"/>
        /// that yields media chunks as OBS (or another RTMP encoder) publishes. Each chunk is
        /// prefixed with a single byte indicating the RTMP message type (8 = audio, 9 = video,
        /// 18 = metadata) followed by the raw payload.
        /// </summary>
        public delegate Task RtmpPublishHandler(NetworkConnection connection, string app, string streamKey, UploadStream uploadStream, CancellationToken cancellationToken);

        private readonly Dictionary<string, Dictionary<string, RouteHandler>> _routes = new Dictionary<string, Dictionary<string, RouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, StreamRouteHandler>> _streamRoutes = new Dictionary<string, Dictionary<string, StreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, UploadStreamRouteHandler>> _uploadStreamRoutes = new Dictionary<string, Dictionary<string, UploadStreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tracks connections that currently have an active <see cref="MapStream"/> handler running.
        /// Subsequent data on these connections is raw payload (e.g., OBS video) and must not be
        /// parsed as HTTP. The data still flows through <see cref="NetworkBase.OnReceive"/> for
        /// other subscribers to capture.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, byte> _activeStreamConnections = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Tracks connections that have an active <see cref="MapUploadStream"/> handler running.
        /// Subsequent raw data on these connections is forwarded to the corresponding
        /// <see cref="UploadStream"/> instead of being parsed as HTTP.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, UploadStream> _activeUploadConnections = new ConcurrentDictionary<Guid, UploadStream>();

        private readonly Dictionary<string, RtmpPublishHandler> _rtmpPublishRoutes = new Dictionary<string, RtmpPublishHandler>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tracks connections running the RTMP protocol (e.g., OBS publishing via <c>rtmp://</c>).
        /// Subsequent data on these connections is fed into the <see cref="RtmpSession"/> state
        /// machine instead of being parsed as HTTP.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, RtmpSession> _activeRtmpConnections = new ConcurrentDictionary<Guid, RtmpSession>();

        public string IndexPageHtml { get; set; } = "<html><body><h1>SocketJack HttpServer</h1></body></html>";

        /// <summary>
        /// Gets or sets the content served at <c>/robots.txt</c>. Set to <see langword="null"/> to
        /// disable the built-in robots.txt route. Defaults to a permissive policy.
        /// </summary>
        public string Robots { get; set; } = "User-agent: *\nAllow: /\n";

        public void Map(string method, string path, RouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_routes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, RouteHandler>(StringComparer.OrdinalIgnoreCase);
                _routes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <summary>
        /// Maps a route whose handler receives a deserialized body of type <typeparamref name="T"/>.
        /// The request body is deserialized using the configured serializer before the handler is invoked.
        /// </summary>
        public void Map<T>(string method, string path, RouteHandler<T> handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Map(method, path, (connection, request, cancellationToken) => {
                T body = default;
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    var wrapped = Options.Serializer.Deserialize(request.BodyBytes);
                    if (wrapped != null) {
                        var obj = wrapped.Unwrap(this as ISocket);
                        if (obj is T typed)
                            body = typed;
                    }
                }
                return handler(connection, body, request, cancellationToken);
            });
        }

        public bool RemoveRoute(string method, string path) {
            if (!_routes.TryGetValue(method, out var byPath))
                return false;
            return byPath.Remove(path);
        }

        /// <summary>
        /// Maps a streaming route. The handler receives a <see cref="ChunkedStream"/> it can
        /// write to line-by-line. The connection stays open until the handler returns.
        /// </summary>
        public void MapStream(string method, string path, StreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_streamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, StreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _streamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <summary>
        /// Maps an upload-streaming route. When an incoming HTTP request matches, the handler
        /// runs on a background task and receives an <see cref="UploadStream"/> that yields
        /// data chunks as they arrive (e.g., continuous OBS MPEG-TS video over HTTP POST).
        /// </summary>
        public void MapUploadStream(string method, string path, UploadStreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, UploadStreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _uploadStreamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        public bool RemoveUploadStreamRoute(string method, string path) {
            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath))
                return false;
            return byPath.Remove(path);
        }

        /// <summary>
        /// Maps an RTMP publish route. When an OBS (or compatible) encoder connects via RTMP
        /// and publishes to the given <paramref name="app"/> name, the <paramref name="handler"/>
        /// runs on a background task and receives an <see cref="UploadStream"/> with media data.
        /// Use <c>"*"</c> as a wildcard to match any app name.
        /// </summary>
        public void MapRtmpPublish(string app, RtmpPublishHandler handler) {
            if (string.IsNullOrWhiteSpace(app))
                throw new ArgumentException("App name is required.", nameof(app));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            _rtmpPublishRoutes[app] = handler;
        }

        public bool RemoveRtmpPublishRoute(string app) {
            return _rtmpPublishRoutes.Remove(app);
        }

        private object ResolveRouteObject(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken) {
            if (request == null)
                return null;

            string method = request.Method;
            string path = NormalizePath(request.Path);

            if (method != null && path != null) {
                if (_routes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler(connection, request, cancellationToken);
                }
                // HEAD should fall back to GET-mapped routes
                if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) {
                    if (_routes.TryGetValue("GET", out var getByPath)) {
                        if (getByPath.TryGetValue(path, out var getHandler))
                            return getHandler(connection, request, cancellationToken);
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

        private StreamRouteHandler ResolveStreamRoute(HttpRequest request) {
            if (request == null || _streamRoutes.Count == 0)
                return null;

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
            if (request == null || _uploadStreamRoutes.Count == 0)
                return null;

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

        private RtmpSession CreateRtmpSession(NetworkConnection conn) {
            var session = new RtmpSession(conn);
            _activeRtmpConnections.TryAdd(conn.ID, session);

            session.OnPublishStart = (app, streamKey) => {
                RtmpPublishHandler handler = null;
                if (!_rtmpPublishRoutes.TryGetValue(app, out handler))
                    _rtmpPublishRoutes.TryGetValue("*", out handler);

                if (handler != null) {
                    var upload = new UploadStream(conn);
                    session.AttachUploadStream(upload);

                    var connId = conn.ID;
                    Task.Run(async () => {
                        try {
                            handler(conn, app, streamKey, upload, default).GetAwaiter().GetResult();
                        } catch {
                        } finally {
                            upload.Complete();
                        }
                    });
                }
            };

            session.OnPublishStop = () => {
                _activeRtmpConnections.TryRemove(conn.ID, out _);
            };

            return session;
        }

        private static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path))
                return path;
            var q = path.IndexOf('?');
            if (q >= 0)
                path = path.Substring(0, q);
            if (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        /// <summary>
        /// Minimum body size in bytes before the server switches to chunked transfer encoding.
        /// Bodies smaller than this are sent with Content-Length in a single write.
        /// Default is 8 KB.
        /// </summary>
        public int ChunkedThreshold { get; set; } = 8192;

        /// <summary>
        /// Size of each chunk when using chunked transfer encoding. Default is 4 KB.
        /// </summary>
        public int ChunkedSize { get; set; } = 4096;

        private void Disposing() {
            OnDisposing -= Disposing;
            this.OnReceive -= GetRequestAsync;
            foreach (var kv in _activeUploadConnections) {
                kv.Value.Complete();
            }
            _activeUploadConnections.Clear();
            foreach (var kv in _activeRtmpConnections) {
                kv.Value.Stop();
            }
            _activeRtmpConnections.Clear();
        }

        public HttpServer(int Port, string Name = "HttpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
        }

        // Local callback registry mirroring TcpBase behavior
        new private Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<IReceivedEventArgs>>>();
        private Dictionary<Delegate, Action<IReceivedEventArgs>> CallbackMap = new Dictionary<Delegate, Action<IReceivedEventArgs>>();

        new public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            Options.Whitelist.Add(Type);
            Action<IReceivedEventArgs> wrapped = e => Action((ReceivedEventArgs<T>)e);
            lock (TypeCallbacks) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    TypeCallbacks[Type].Add(wrapped);
                } else {
                    TypeCallbacks[Type] = new List<Action<IReceivedEventArgs>>() { wrapped };
                }
                CallbackMap[Action] = wrapped;
            }
            if (!Options.Whitelist.Contains(Type)) Options.Whitelist.Add(Type);
        }

        new public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            lock (TypeCallbacks) {
                if (CallbackMap.TryGetValue(Action, out var wrapped)) {
                    if (TypeCallbacks.ContainsKey(Type)) {
                        TypeCallbacks[Type].Remove(wrapped);
                        if (TypeCallbacks[Type].Count == 0) {
                            TypeCallbacks.Remove(Type);
                            if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                        }
                    }
                    CallbackMap.Remove(Action);
                }
            }
        }

        new public void RemoveCallback<T>() {
            var Type = typeof(T);
            lock (TypeCallbacks) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    var wrappers = TypeCallbacks[Type];
                    var keysToRemove = new List<Delegate>();
                    foreach (var kv in CallbackMap) {
                        if (wrappers.Contains(kv.Value)) keysToRemove.Add(kv.Key);
                    }
                    foreach (var k in keysToRemove) CallbackMap.Remove(k);
                    TypeCallbacks.Remove(Type);
                    if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                }
            }
        }

        private void InvokeCallbacks(IReceivedEventArgs e) {
            if (e == null || e.Obj == null) return;
            var t = e.Obj.GetType();
            if (TypeCallbacks.ContainsKey(t)) {
                var list = TypeCallbacks[t];
                for (int i = 0; i < list.Count; i++) {
                    try { list[i].Invoke(e); } catch { }
                }
            }
        }

        public HttpServer(NetworkOptions Options, int Port, string Name = "HttpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
        }

        internal void GetRequestAsync(ref IReceivedEventArgs e) {
            try {
                if (e == null || e.Obj == null)
                    return;

                // If this connection has an active streaming handler (e.g., OBS uploading video),
                // skip HTTP parsing. The raw data is still available to other OnReceive subscribers.
                if (e.Connection != null && _activeStreamConnections.ContainsKey(e.Connection.ID))
                    return;

                // If this connection has an active upload-stream handler, forward raw data to it.
                if (e.Connection != null && _activeUploadConnections.TryGetValue(e.Connection.ID, out var activeUpload)) {
                    var uploadBytes = TryGetRequestBytes(e.Obj);
                    if (uploadBytes != null && uploadBytes.Length > 0) {
                        activeUpload.Enqueue(uploadBytes);
                    }
                    return;
                }

                // If this connection has an active RTMP session, forward data to it.
                if (e.Connection != null && _activeRtmpConnections.TryGetValue(e.Connection.ID, out var rtmpSession)) {
                    var rtmpBytes = TryGetRequestBytes(e.Obj);
                    if (rtmpBytes != null && rtmpBytes.Length > 0)
                        rtmpSession.ProcessData(rtmpBytes);
                    return;
                }

                // Detect new RTMP connections (version byte 0x03 + C1 zero field at offset 5-8)
                if (_rtmpPublishRoutes.Count > 0 && e.Connection != null) {
                    var probeBytes = TryGetRequestBytes(e.Obj);
                    if (probeBytes != null && probeBytes.Length >= 9
                        && probeBytes[0] == 0x03
                        && probeBytes[5] == 0 && probeBytes[6] == 0 && probeBytes[7] == 0 && probeBytes[8] == 0) {
                        var session = CreateRtmpSession(e.Connection);
                        session.ProcessData(probeBytes);
                        return;
                    }
                }

                // Only handle raw HTTP requests here.
                // SocketJack-serialized payloads (e.g., JSON-wrapped objects), ignore them.
                if (e.Obj is string s) {
                    if (string.IsNullOrWhiteSpace(s))
                        return;
                    var prefix = s.Length > 16 ? s.Substring(0, 16) : s;
                    if (!(prefix.StartsWith("GET ") || prefix.StartsWith("POST ") || prefix.StartsWith("PUT ") || prefix.StartsWith("DELETE ") || prefix.StartsWith("HEAD ") || prefix.StartsWith("OPTIONS ") || prefix.StartsWith("PATCH ") || prefix.StartsWith("TRACE "))) {
                        return;
                    }
                } else if (e.Obj is byte[] || e.Obj is List<byte> || e.Obj is IEnumerable<byte> || e.Obj is ArraySegment<byte>) {
                    var bytes = TryGetRequestBytes(e.Obj);
                    if (bytes == null || bytes.Length < 5)
                        return;
                    // Fast check for common methods in ASCII
                    if (!(bytes[0] == (byte)'G' || bytes[0] == (byte)'P' || bytes[0] == (byte)'H' || bytes[0] == (byte)'D' || bytes[0] == (byte)'O' || bytes[0] == (byte)'T'))
                        return;

                    // Additional guard: require an HTTP header terminator near the start.
                    // This filters out SocketJack-framed payloads that may start with arbitrary bytes.
                    var scanLimit = bytes.Length;
                    if (scanLimit > 4096)
                        scanLimit = 4096;
                    var foundTerminator = false;
                    for (int i = 0; i < scanLimit - 3; i++) {
                        if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n' && bytes[i + 2] == (byte)'\r' && bytes[i + 3] == (byte)'\n') {
                            foundTerminator = true;
                            break;
                        }
                    }
                    if (!foundTerminator)
                        return;
                } else {
                    // Not a raw HTTP payload.
                    return;
                }

                var rawRequestBytes = TryGetRequestBytes(e.Obj);
                if (rawRequestBytes == null || rawRequestBytes.Length == 0)
                    return;

                var request = ParseHttpRequest(rawRequestBytes);

                // If any callbacks registered for deserialized types, attempt to deserialize body and invoke
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    try {
                        var wrapped = Options.Serializer.Deserialize(request.BodyBytes);
                        if (wrapped != null) {
                            var obj = wrapped.Unwrap(this as ISocket);
                            if (obj != null) {
                                var objType = obj.GetType();
                                if (TypeCallbacks.ContainsKey(objType)) {
                                    var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(objType);
                                    var args = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                                    args.Initialize(this as ISocket, e.Connection, obj, request.BodyBytes.Length);
                                    InvokeCallbacks(args);
                                }
                            }
                        }
                    } catch {
                        // ignore deserialize errors
                    }
                }

                var context = new HttpContext {
                    Request = request,
                    // Use the connection that produced the request (from event args)
                    Connection = e.Connection,
                    cancellationToken = default
                };

                var ct = default(CancellationToken);

                // Check for streaming routes first — these keep the connection open
                var streamHandler = ResolveStreamRoute(request);
                if (streamHandler != null) {
                    _activeStreamConnections.TryAdd(e.Connection.ID, 0);
                    var conn = e.Connection;
                    var response = context.Response;
                    Task.Factory.StartNew(async () => {
                        var chunked = new ChunkedStream(conn.Stream, response);
                        try {
                            await streamHandler(conn, request, chunked, ct);
                        } catch (Exception ex) {
                            InvokeOnError(conn, ex);
                        } finally {
                            chunked.Finish();
                            _activeStreamConnections.TryRemove(conn.ID, out _);
                        }
                    }, TaskCreationOptions.LongRunning);
                    return;
                }

                // Check for upload-stream routes (e.g., OBS video ingest over HTTP POST)
                var uploadHandler = ResolveUploadStreamRoute(request);
                if (uploadHandler != null) {
                    var upload = new UploadStream(e.Connection);
                    _activeUploadConnections.TryAdd(e.Connection.ID, upload);

                    // Queue initial body data if present in the first read
                    if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                        upload.Enqueue(request.BodyBytes);
                    }

                    var connId = e.Connection.ID;
                    var conn = e.Connection;
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

                var handledByRoute = false;
                if (_routes.Count > 0 || IndexPageHtml != null || Robots != null) {
                    var routeObj = ResolveRouteObject(e.Connection, request, ct);
                    if (routeObj != null) {
                        handledByRoute = true;
                        context.Response.Body = routeObj;

                        var isIndex = request.Method != null && (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) && (request.Path == "/" || string.IsNullOrEmpty(request.Path)) && IndexPageHtml != null;

                        if (routeObj is string strBody) {
                            var trimmed = strBody.TrimStart();
                            if (isIndex || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) {
                                context.Response.ContentType = "text/html";
                            } else if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')) {
                                context.Response.ContentType = "application/json";
                            } else {
                                context.Response.ContentType = "text/plain";
                            }
                        } else {
                            // SocketJack returns wrapped object payloads as binary;
                            // mark them as json so our HttpClient will attempt to deserialize.
                            context.Response.ContentType = "application/json";
                        }
                    }
                }

                if (!handledByRoute) {
                    OnHttpRequest?.Invoke(e.Connection, ref context, ct);
                }

                // Return 404 for unmatched routes that were not handled by OnHttpRequest
                //if (!handledByRoute && (context.Response.Body == null || (context.Response.Body is string sb && sb.Length == 0))) {
                //    context.Response.StatusCodeNumber = 404;
                //    context.Response.ReasonPhrase = "Not Found";
                //}

                if (context.Response != null && context.Response.Headers != null) {
                    if (!context.Response.Headers.ContainsKey("Server"))
                        context.Response.Headers["Server"] = "SocketJack";
                    if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    if (!context.Response.Headers.ContainsKey("Date"))
                        context.Response.Headers["Date"] = DateTime.UtcNow.ToString("R");
                }

                if (Options.Logging) {
                    var ua = request.Headers.ContainsKey("User-Agent") ? request.Headers["User-Agent"] : "";
                    Log(request.Method + " " + request.Path + " > " + context.Response.StatusCodeNumber + " (" + (context.Response.BodyBytes != null ? context.Response.BodyBytes.Length : 0) + " bytes) UA: " + ua + " - " + request.Context?.Connection.Socket.RemoteEndPoint.ToString());
                }

                context.Response.EnsureBodyBytes();
                var bodyLen = context.Response.BodyBytes != null ? context.Response.BodyBytes.Length : 0;
                bool isHead = request.Method != null && request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

                if (isHead) {
                    // HEAD responses include Content-Length but no body
                    var (hdr, _) = context.Response.ToBytesWithHeader();
                    if (hdr != null && hdr.Length > 0)
                        e.Connection.Stream.Write(hdr, 0, hdr.Length);
                    e.Connection.Stream.Flush();
                } else if (bodyLen > ChunkedThreshold) {
                    // Large response — stream in chunks so browsers can render progressively
                    context.Response.WriteChunkedTo(e.Connection.Stream, ChunkedSize);
                } else {
                    var (hdr, body) = context.Response.ToBytesWithHeader();
                    if (hdr != null && hdr.Length > 0)
                        e.Connection.Stream.Write(hdr, 0, hdr.Length);
                    if (body != null && body.Length > 0)
                        e.Connection.Stream.Write(body, 0, body.Length);
                    e.Connection.Stream.Flush();
                }

                // Honour Connection: close — shut down the TCP connection
                try { CloseConnection(e.Connection, DisconnectionReason.LocalSocketClosed); } catch { }
            } catch (Exception ex) {
                InvokeOnError(e.Connection, ex);
            }
        }

        private static byte[] TryGetRequestBytes(object obj) {
            if (obj == null)
                return null;

            if (obj is byte[] b)
                return b;

            if (obj is List<byte> lb)
                return lb.ToArray();

            if (obj is IEnumerable<byte> eb)
                return eb.ToArray();

            if (obj is ArraySegment<byte> seg) {
                if (seg.Array == null)
                    return null;
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

        /// <summary>
        /// Parses a raw HTTP request string into an HttpRequest object.
        /// </summary>
        /// <param name="rawRequest">The raw HTTP request string.</param>
        /// <returns>HttpRequest object with parsed data.</returns>
        public static HttpRequest ParseHttpRequest(byte[] rawRequestBytes) {
            if (rawRequestBytes == null || rawRequestBytes.Length == 0)
                throw new ArgumentException("Request is empty", nameof(rawRequestBytes));

            // Find header/body separator (CRLFCRLF)
            var separator = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
            int hdrEnd = -1;
            for (int i = 0; i < rawRequestBytes.Length - 3; i++) {
                if (rawRequestBytes[i] == separator[0] && rawRequestBytes[i + 1] == separator[1] && rawRequestBytes[i + 2] == separator[2] && rawRequestBytes[i + 3] == separator[3]) {
                    hdrEnd = i + 4;
                    break;
                }
            }

            if (hdrEnd == -1)
                hdrEnd = rawRequestBytes.Length; // assume all headers if no body

            var headerBytes = new byte[hdrEnd];
            Array.Copy(rawRequestBytes, 0, headerBytes, 0, hdrEnd);
            string headerText = Encoding.UTF8.GetString(headerBytes);

            using (var reader = new StringReader(headerText)) {
                var request = new HttpRequest();

                var requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                    throw new InvalidDataException("Invalid HTTP request line.");

                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    throw new InvalidDataException("Invalid HTTP request line.");

                request.Method = parts[0];
                request.Path = parts[1];
                request.Version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex > 0) {
                        var headerName = line.Substring(0, separatorIndex).Trim();
                        var headerValue = line.Substring(separatorIndex + 1).Trim();
                        request.Headers[headerName] = headerValue;
                    }
                }

                // Determine body
                int bodyStart = hdrEnd;
                byte[] body = null;

                if (request.Headers.TryGetValue("Content-Length", out var clVal) && long.TryParse(clVal, out var cl) && cl > 0) {
                    var remaining = rawRequestBytes.Length - bodyStart;
                    var toCopy = (int)Math.Min(cl, remaining);
                    body = new byte[toCopy];
                    Array.Copy(rawRequestBytes, bodyStart, body, 0, toCopy);
                } else if (request.Headers.TryGetValue("Transfer-Encoding", out var te) && te?.ToLowerInvariant().Contains("chunked") == true) {
                    // parse chunked body
                    using (var ms = new MemoryStream()) {
                        int pos = bodyStart;
                        while (pos < rawRequestBytes.Length) {
                            // read chunk size line
                            int lineEnd = -1;
                            for (int i = pos; i < rawRequestBytes.Length - 1; i++) {
                                if (rawRequestBytes[i] == (byte)'\r' && rawRequestBytes[i + 1] == (byte)'\n') {
                                    lineEnd = i;
                                    break;
                                }
                            }
                            if (lineEnd == -1) break;
                            var sizeLine = Encoding.UTF8.GetString(rawRequestBytes, pos, lineEnd - pos);
                            pos = lineEnd + 2;
                            if (!int.TryParse(sizeLine.Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunkSize)) break;
                            if (chunkSize == 0) {
                                // consume trailing CRLF
                                break;
                            }
                            if (pos + chunkSize > rawRequestBytes.Length) chunkSize = rawRequestBytes.Length - pos;
                            ms.Write(rawRequestBytes, pos, chunkSize);
                            pos += chunkSize;
                            // consume CRLF
                            if (pos + 1 < rawRequestBytes.Length && rawRequestBytes[pos] == (byte)'\r' && rawRequestBytes[pos + 1] == (byte)'\n') pos += 2;
                        }
                        body = ms.ToArray();
                    }
                } else if (bodyStart < rawRequestBytes.Length) {
                    // No Content-Length or Transfer-Encoding; capture any remaining bytes
                    // as the body. This covers streaming uploads (e.g., OBS MPEG-TS over POST)
                    // where only partial data is available in the initial network read.
                    var remaining = rawRequestBytes.Length - bodyStart;
                    body = new byte[remaining];
                    Array.Copy(rawRequestBytes, bodyStart, body, 0, remaining);
                }

                request.SetBody(body);
                return request;
            }
        }
    }

    public class HttpContext {
        public static string DefaultCharset = "UTF-8";
        // Numeric status and reason phrase
        public int StatusCodeNumber { get; set; } = 200;
        public string ReasonPhrase { get; set; } = "OK";
        public string StatusCode {
            get { return StatusCodeNumber.ToString() + " " + ReasonPhrase; }
            set {
                if (string.IsNullOrEmpty(value)) {
                    StatusCodeNumber = 200;
                    ReasonPhrase = "OK";
                } else {
                    var parts = value.Split(' ', 2);
                    if (parts.Length > 0 && int.TryParse(parts[0], out var n)) {
                        StatusCodeNumber = n;
                        ReasonPhrase = parts.Length > 1 ? parts[1] : string.Empty;
                    } else {
                        StatusCodeNumber = 200;
                        ReasonPhrase = value;
                    }
                }
            }
        }
        public string ContentType {
            get {
                return _ContentType + "; charset=" + DefaultCharset;
            }
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    _ContentType = "text/json";
                } else {
                    var sc = value.IndexOf(';');
                    _ContentType = sc >= 0 ? value.Substring(0, sc).Trim() : value;
                }
            }
        }
        private string _ContentType = "application/json";
        // Default to standard application/json
        // Note: kept charset appended by ContentType getter
        // This makes HttpServer responses default to JSON where not overridden.
        
        public HttpRequest Request { get; set; }
        public HttpResponse Response { 
            get { 
                if(_Response == null) _Response = CreateResponse();
                return _Response;
            } 
        }
        private HttpResponse _Response;
        public NetworkConnection Connection { get; set; }
        public CancellationToken cancellationToken { get; set; }

        public HttpResponse CreateResponse() {
            return new HttpResponse() {
                Context = this,
                Path = Request.Path,
                Version = Request.Version,
                Headers = new Dictionary<string, string>(),
                ContentType = ContentType
            };
        }
    }

    public class HttpRequest {
        public HttpContext Context { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ContentLength {
            get {
                return _ContentLength;
            }
        }
        private long _ContentLength = 0L;
        public string Body { get; set; }
        public byte[] BodyBytes { get; internal set; }

        internal void SetBody(byte[] bodyBytes) {
            if (bodyBytes == null || bodyBytes.Length == 0) {
                Body = null;
                BodyBytes = null;
                _ContentLength = 0;
            } else {
                BodyBytes = bodyBytes;
                Body = Encoding.UTF8.GetString(bodyBytes);
                _ContentLength = bodyBytes.LongLength;
            }
        }
    }

    public class HttpResponse {
        public HttpContext Context { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public int StatusCodeNumber {
            get => Context?.StatusCodeNumber ?? 200;
            set { if (Context != null) Context.StatusCodeNumber = value; }
        }
        public string ReasonPhrase {
            get => Context?.ReasonPhrase ?? "OK";
            set { if (Context != null) Context.ReasonPhrase = value; }
        }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public object Body {
            get {
                return _Body;
            }
            set {
                if (value == null) {
                    _Body = string.Empty;
                    _BodyBytes = Array.Empty<byte>();
                } else if (value is string str) {
                    _Body = str;
                    _BodyBytes = null;
                } else {
                    var wrapped = new Wrapper(value, Context.Connection.Parent);
                    _BodyBytes = Context.Connection.Parent.Options.Serializer.Serialize(wrapped);
                    _Body = string.Empty;
                }
            }
        }
        private string _Body = string.Empty;
        public string ContentType {
            get {
                return _ContentType + "; charset=" + HttpContext.DefaultCharset;
            }
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    _ContentType = "text/json";
                } else {
                    var sc = value.IndexOf(';');
                    _ContentType = sc >= 0 ? value.Substring(0, sc).Trim() : value;
                }
            }
        }
        private string _ContentType = "application/json";

        private byte[] _BodyBytes = null;

        // Raw body bytes for clients that need the original payload.
        public byte[] BodyBytes {
            get {
                EnsureBodyBytes();
                return _BodyBytes;
            }
            set {
                _BodyBytes = value;
            }
        }

        internal void EnsureBodyBytes() {
            if (_BodyBytes == null) {
                if (_Body == null)
                    _BodyBytes = Array.Empty<byte>();
                else
                    _BodyBytes = Encoding.UTF8.GetBytes(_Body);
            }
        }

        public override string ToString() {
            EnsureBodyBytes();
            var sb = new StringBuilder();
            sb.Append((Version ?? "HTTP/1.1") + " " + Context.StatusCode + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                sb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                sb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            sb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            // Only append textual body for debugging output.
            // For binary SocketJack wrapped payloads, _Body is intentionally empty.
            if (!string.IsNullOrEmpty(_Body))
                sb.Append(_Body);

            return sb.ToString();
        }

        public byte[] ToBytes() {
            EnsureBodyBytes();
            // Build header bytes and append raw body bytes (binary-safe)
            var headerSb = new StringBuilder();
            headerSb.Append((Version ?? "HTTP/1.1") + " " + Context.StatusCode + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            headerSb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            var outBytes = new byte[headerBytes.Length + _BodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, outBytes, 0, headerBytes.Length);
            if (_BodyBytes.Length > 0)
                Buffer.BlockCopy(_BodyBytes, 0, outBytes, headerBytes.Length, _BodyBytes.Length);
            return outBytes;
        }

        // New: return header bytes only + raw body bytes separately (binary-safe)
        public (byte[] header, byte[] body) ToBytesWithHeader() {
            EnsureBodyBytes();
            var headerSb = new StringBuilder();
            headerSb.Append((Version ?? "HTTP/1.1") + " " + Context.StatusCode + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            headerSb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            return (headerBytes, _BodyBytes);
        }

        /// <summary>
        /// Writes the response to <paramref name="stream"/> using HTTP/1.1 chunked transfer encoding.
        /// Each line of text is sent as its own chunk so the client can render progressively.
        /// For binary (non-text) bodies, falls back to fixed-size chunks.
        /// </summary>
        public void WriteChunkedTo(Stream stream, int chunkSize) {
            EnsureBodyBytes();
            if (chunkSize <= 0) chunkSize = 4096;

            var headerSb = new StringBuilder();
            headerSb.Append((Version ?? "HTTP/1.1") + " " + Context.StatusCode + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            headerSb.Append("Transfer-Encoding: chunked\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();

            var crlf = new byte[] { (byte)'\r', (byte)'\n' };

            // Determine if body is text so we can stream line-by-line
            bool isText = !string.IsNullOrEmpty(_Body);

            if (isText) {
                // Stream line-by-line for visible progressive rendering
                using (var reader = new StringReader(_Body)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        // Re-append the newline that ReadLine consumed
                        var lineBytes = Encoding.UTF8.GetBytes(line + "\n");

                        var sizeHex = Encoding.UTF8.GetBytes(lineBytes.Length.ToString("X") + "\r\n");
                        stream.Write(sizeHex, 0, sizeHex.Length);
                        stream.Write(lineBytes, 0, lineBytes.Length);
                        stream.Write(crlf, 0, 2);
                        stream.Flush();
                    }
                }
            } else {
                // Binary body — fall back to fixed-size chunks
                int offset = 0;
                while (offset < _BodyBytes.Length) {
                    int remaining = _BodyBytes.Length - offset;
                    int size = Math.Min(chunkSize, remaining);

                    var sizeHex = Encoding.UTF8.GetBytes(size.ToString("X") + "\r\n");
                    stream.Write(sizeHex, 0, sizeHex.Length);
                    stream.Write(_BodyBytes, offset, size);
                    stream.Write(crlf, 0, 2);
                    stream.Flush();

                    offset += size;
                }
            }

            // Terminating chunk: 0\r\n\r\n
            var terminator = Encoding.UTF8.GetBytes("0\r\n\r\n");
            stream.Write(terminator, 0, terminator.Length);
            stream.Flush();
        }
    }

    /// <summary>
    /// Wraps a network stream with HTTP/1.1 chunked transfer encoding.
    /// Write lines or raw data; each write is sent as an individual chunk and flushed
    /// immediately so the client can render progressively.
    /// Call <see cref="Finish"/> (or let the server call it) to send the terminating chunk.
    /// </summary>
    public class ChunkedStream {
        private readonly Stream _stream;
        private bool _headerSent;
        private bool _finished;
        private readonly HttpResponse _response;
        private static readonly byte[] Crlf = new byte[] { (byte)'\r', (byte)'\n' };

        internal ChunkedStream(Stream stream, HttpResponse response) {
            _stream = stream;
            _response = response;
        }

        /// <summary>
        /// Gets or sets the Content-Type header for the chunked response.
        /// Must be set before the first call to <see cref="Write(string)"/>,
        /// <see cref="WriteLine"/>, or <see cref="Write(byte[], int, int)"/>.
        /// </summary>
        public string ContentType {
            get {
                if (_response.Headers.TryGetValue("Content-Type", out var ct))
                    return ct;
                return _response.ContentType;
            }
            set {
                _response.Headers["Content-Type"] = value;
            }
        }

        private void EnsureHeader() {
            if (_headerSent) return;
            _headerSent = true;

            var sb = new StringBuilder();
            sb.Append((_response.Version ?? "HTTP/1.1") + " " + _response.Context.StatusCode + "\r\n");
            if (!_response.Headers.ContainsKey("Content-Type"))
                sb.Append("Content-Type: " + (_response.ContentType ?? _response.Context.ContentType) + "\r\n");
            foreach (var h in _response.Headers) {
                sb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            if (!_response.Headers.ContainsKey("Server"))
                sb.Append("Server: SocketJack\r\n");
            sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: keep-alive\r\n");
            sb.Append("Cache-Control: no-cache, no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (!_response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            _stream.Write(headerBytes, 0, headerBytes.Length);
            _stream.Flush();
        }

        /// <summary>
        /// Sends a single line of text (with trailing newline) as one HTTP chunk.
        /// </summary>
        public void WriteLine(string text) {
            if (_finished) return;
            EnsureHeader();
            var data = Encoding.UTF8.GetBytes(text + "\n");
            WriteChunk(data);
        }

        /// <summary>
        /// Sends text as one HTTP chunk without appending a newline.
        /// </summary>
        public void Write(string text) {
            if (_finished) return;
            EnsureHeader();
            var data = Encoding.UTF8.GetBytes(text);
            WriteChunk(data);
        }

        /// <summary>
        /// Sends arbitrary bytes as one HTTP chunk.
        /// </summary>
        public void Write(byte[] data, int offset, int count) {
            if (_finished) return;
            EnsureHeader();
            var sizeHex = Encoding.UTF8.GetBytes(count.ToString("X") + "\r\n");
            _stream.Write(sizeHex, 0, sizeHex.Length);
            _stream.Write(data, offset, count);
            _stream.Write(Crlf, 0, 2);
            _stream.Flush();
        }

        private void WriteChunk(byte[] data) {
            var sizeHex = Encoding.UTF8.GetBytes(data.Length.ToString("X") + "\r\n");
            _stream.Write(sizeHex, 0, sizeHex.Length);
            _stream.Write(data, 0, data.Length);
            _stream.Write(Crlf, 0, 2);
            _stream.Flush();
        }

        /// <summary>
        /// Sends the terminating zero-length chunk. Called automatically when the
        /// stream route handler returns.
        /// </summary>
        public void Finish() {
            if (_finished) return;
            _finished = true;
            EnsureHeader();
            var terminator = Encoding.UTF8.GetBytes("0\r\n\r\n");
            try {
                _stream.Write(terminator, 0, terminator.Length);
                _stream.Flush();
            } catch {
                // stream may already be closed
            }
        }
    }

    /// <summary>
    /// Provides a stream-like interface for reading incoming data chunks on an HTTP
    /// upload-streaming connection (e.g., OBS sending continuous MPEG-TS video via POST).
    /// Data is enqueued by the server as it arrives and can be consumed asynchronously
    /// by the <see cref="HttpServer.UploadStreamRouteHandler"/>.
    /// </summary>
    public class UploadStream : IDisposable {
        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly NetworkConnection _connection;
        private volatile bool _completed;

        internal UploadStream(NetworkConnection connection) {
            _connection = connection;
        }

        /// <summary>
        /// Enqueues a chunk of raw data received from the network.
        /// </summary>
        internal void Enqueue(byte[] data) {
            if (_completed) return;
            _queue.Enqueue(data);
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Marks the stream as complete. No more data will arrive.
        /// </summary>
        internal void Complete() {
            _completed = true;
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// <see langword="true"/> when the stream is complete and all queued data has been consumed.
        /// </summary>
        public bool IsCompleted => _completed && _queue.IsEmpty;

        /// <summary>
        /// The <see cref="NetworkConnection"/> that this upload stream is associated with.
        /// </summary>
        public NetworkConnection Connection => _connection;

        /// <summary>
        /// Asynchronously reads the next chunk of data.
        /// Returns <see langword="null"/> when the stream is complete or the connection is closed.
        /// </summary>
        public async Task<byte[]> ReadAsync(CancellationToken ct = default) {
            while (true) {
                if (_queue.TryDequeue(out var data)) return data;
                if (_completed || _connection.Closed) return null;
                try {
                    // Use timeout to periodically check connection state
                    await _signal.WaitAsync(1000, ct);
                } catch (OperationCanceledException) {
                    return null;
                }
            }
        }

        /// <summary>
        /// Tries to read the next chunk of data synchronously.
        /// Returns <see langword="false"/> when no data is available within the timeout
        /// or the stream is complete.
        /// </summary>
        public bool TryRead(out byte[] data, int timeoutMs = 100) {
            data = null;
            if (_queue.TryDequeue(out data)) return true;
            if (_completed || _connection.Closed) return false;
            if (_signal.Wait(timeoutMs)) {
                return _queue.TryDequeue(out data);
            }
            return false;
        }

        public void Dispose() {
            Complete();
            _signal.Dispose();
        }
    }
}
