
using System;
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
        public delegate void RequestHandler(TcpConnection Connection, ref HttpContext context, CancellationToken cancellationToken);

        public delegate object RouteHandler(TcpConnection connection, HttpRequest request, CancellationToken cancellationToken);

        private readonly Dictionary<string, Dictionary<string, RouteHandler>> _routes = new Dictionary<string, Dictionary<string, RouteHandler>>(StringComparer.OrdinalIgnoreCase);

        public string IndexPageHtml { get; set; } = "<html><body><h1>SocketJack HttpServer</h1></body></html>";

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

        public bool RemoveRoute(string method, string path) {
            if (!_routes.TryGetValue(method, out var byPath))
                return false;
            return byPath.Remove(path);
        }

        private object ResolveRouteObject(TcpConnection connection, HttpRequest request, CancellationToken cancellationToken) {
            if (request == null)
                return null;

            string method = request.Method;
            string path = request.Path;
            if (!string.IsNullOrEmpty(path)) {
                var q = path.IndexOf('?');
                if (q >= 0)
                    path = path.Substring(0, q);
                if (path.Length > 1 && path[path.Length - 1] == '/')
                    path = path.Substring(0, path.Length - 1);
            }

            if (method != null && path != null) {
                if (_routes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler(connection, request, cancellationToken);
                }
            }

            if (method != null && method.Equals("GET", StringComparison.OrdinalIgnoreCase)) {
                if (path == "/" || string.IsNullOrEmpty(path))
                    return IndexPageHtml;
            }

            return null;
        }

        private void Disposing() {
            OnDisposing -= Disposing;
            this.OnReceive -= GetRequestAsync;
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

        public HttpServer(TcpOptions Options, int Port, string Name = "HttpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
        }

        internal void GetRequestAsync(ref IReceivedEventArgs e) {
            try {
                if (e == null || e.Obj == null)
                    return;

                // Only handle raw HTTP requests here. If this connection is carrying
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

                var handledByRoute = false;
                if (_routes.Count > 0 || IndexPageHtml != null) {
                    var routeObj = ResolveRouteObject(e.Connection, request, ct);
                    if (routeObj != null) {
                        handledByRoute = true;
                        context.Response.Body = routeObj;

                        var isIndex = request.Method != null && request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && (request.Path == "/" || string.IsNullOrEmpty(request.Path)) && IndexPageHtml != null;

                        if (routeObj is string) {
                            if (isIndex) {
                                context.Response.ContentType = "text/html";
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

                if (context.Response != null && context.Response.Headers != null) {
                    if (!context.Response.Headers.ContainsKey("Server"))
                        context.Response.Headers["Server"] = "SocketJack";
                }

                var (hdr, body) = context.Response.ToBytesWithHeader();
                if (hdr != null && hdr.Length > 0)
                    e.Connection.Stream.Write(hdr, 0, hdr.Length);
                if (body != null && body.Length > 0)
                    e.Connection.Stream.Write(body, 0, body.Length);
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
                    _ContentType = value;
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
        public TcpConnection Connection { get; set; }
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
                    _ContentType = value;
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

        private void EnsureBodyBytes() {
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
    }
}
