
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Net.Sockets;
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

        private readonly Dictionary<string, string> _directoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _fileMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-connection buffer for accumulating partial HTTP requests
        /// that span multiple TCP segments.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, List<byte>> _requestBuffers = new ConcurrentDictionary<Guid, List<byte>>();

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

        /// <summary>
        /// When <see langword="true"/>, mapped directories that contain no
        /// <c>index.html</c> / <c>index.htm</c> will return an auto-generated
        /// HTML listing of their contents. Defaults to <see langword="false"/>.
        /// A <c>.htaccess</c> file inside the directory can override this
        /// per-directory with <c>Options +Indexes</c> or <c>Options -Indexes</c>.
        /// </summary>
        public bool AllowDirectoryListing { get; set; } = false;

        /// <summary>
        /// Gets or sets the <c>Cache-Control</c> header value added to every HTTP response.
        /// When <see langword="null"/> (the default), no <c>Cache-Control</c> header is emitted.
        /// Example values: <c>"public, max-age=3600"</c>, <c>"no-cache"</c>,
        /// <c>"private, max-age=86400"</c>.
        /// For static files served via <see cref="MapDirectory"/>, an <c>ETag</c> and
        /// <c>Last-Modified</c> header are also sent, and <c>If-None-Match</c> /
        /// <c>If-Modified-Since</c> conditional requests are honoured with <c>304 Not Modified</c>.
        /// </summary>
        public string CacheControl { get; set; } = null;

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

        /// <summary>
        /// Maps a local directory to a URL prefix so that files inside the directory are
        /// served when a GET (or HEAD) request matches. For example,
        /// <c>MapDirectory("/static", @"C:\wwwroot")</c> serves <c>C:\wwwroot\style.css</c>
        /// when the client requests <c>/static/style.css</c>.
        /// If <paramref name="urlPrefix"/> is <c>"/"</c>, the directory is served at the root.
        /// </summary>
        /// <param name="urlPrefix">The URL path prefix (e.g., <c>"/static"</c>).</param>
        /// <param name="localDirectory">The absolute or relative path to the local directory.</param>
        public void MapDirectory(string urlPrefix, string localDirectory) {
            if (string.IsNullOrWhiteSpace(urlPrefix))
                throw new ArgumentException("URL prefix is required.", nameof(urlPrefix));
            if (string.IsNullOrWhiteSpace(localDirectory))
                throw new ArgumentException("Local directory is required.", nameof(localDirectory));
            if (!Directory.Exists(localDirectory))
                throw new DirectoryNotFoundException("Directory not found: " + localDirectory);

            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";
            _directoryMappings[normalized] = Path.GetFullPath(localDirectory);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix and configures a <c>.htaccess</c> file
        /// using the fluent <see cref="HtAccessBuilder"/> API for directory-level security.
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

        /// <summary>
        /// Removes a previously mapped directory route.
        /// </summary>
        public bool RemoveDirectoryMapping(string urlPrefix) {
            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";
            return _directoryMappings.Remove(normalized);
        }

        /// <summary>
        /// Maps a URL path to a specific local file. When a GET (or HEAD) request
        /// matches the <paramref name="urlPath"/>, the file at <paramref name="localFilePath"/>
        /// is served with the appropriate MIME type. For example,
        /// <c>MapFile("/js/widget.js", @"C:\Pages\widget.js")</c> serves the file
        /// when the client requests <c>/js/widget.js</c>.
        /// </summary>
        /// <param name="urlPath">The URL path to match (e.g., <c>"/js/widget.js"</c>).</param>
        /// <param name="localFilePath">The absolute or relative path to the local file.</param>
        public void MapFile(string urlPath, string localFilePath) {
            if (string.IsNullOrWhiteSpace(urlPath))
                throw new ArgumentException("URL path is required.", nameof(urlPath));
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw new ArgumentException("Local file path is required.", nameof(localFilePath));
            _fileMappings[NormalizePath(urlPath)] = Path.GetFullPath(localFilePath);
        }

        /// <summary>
        /// Removes a previously mapped file route.
        /// </summary>
        public bool RemoveFileMapping(string urlPath) {
            return _fileMappings.Remove(NormalizePath(urlPath));
        }

        private bool TryResolveDirectoryFile(string method, string path, string clientIp, string authHeader, out byte[] fileBytes, out string contentType, out HtAccessResult accessResult, out string authRealm, out Dictionary<string, string> extraHeaders, out DateTime? lastModifiedUtc) {
            fileBytes = null;
            contentType = null;
            accessResult = HtAccessResult.Allowed;
            authRealm = null;
            extraHeaders = null;
            lastModifiedUtc = null;

            if (_directoryMappings.Count == 0)
                return false;

            if (method == null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.IsNullOrEmpty(path))
                return false;

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
                    // Exact match on prefix with no trailing path — try index files
                    relativePath = "/";
                } else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) {
                    relativePath = path.Substring(prefix.Length);
                }

                if (relativePath == null)
                    continue;

                // Convert URL slashes to OS path separators and strip leading slash
                var localRelative = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = string.IsNullOrEmpty(localRelative)
                    ? localDir
                    : Path.Combine(localDir, localRelative);
                fullPath = Path.GetFullPath(fullPath);

                // Security: ensure the resolved path is still within the mapped directory
                if (!fullPath.StartsWith(localDir, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Evaluate .htaccess access rules before serving any content.
                string resolvedFileName = Path.GetFileName(fullPath);
                string htDir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                accessResult = HtAccessEvaluator.Evaluate(htDir, resolvedFileName, clientIp, authHeader, out authRealm, out extraHeaders);
                if (accessResult != HtAccessResult.Allowed)
                    return true;

                // If path points to a directory, try common index files
                if (Directory.Exists(fullPath)) {
                    var indexHtml = Path.Combine(fullPath, "index.html");
                    var indexHtm = Path.Combine(fullPath, "index.htm");
                    if (File.Exists(indexHtml))
                        fullPath = indexHtml;
                    else if (File.Exists(indexHtm))
                        fullPath = indexHtm;
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

                if (!File.Exists(fullPath))
                    continue;

                // Block .htaccess at the file level as well.
                if (Path.GetFileName(fullPath).Equals(".htaccess", StringComparison.OrdinalIgnoreCase))
                    return false;

                fileBytes = File.ReadAllBytes(fullPath);
                contentType = GetMimeType(Path.GetExtension(fullPath));
                try { lastModifiedUtc = File.GetLastWriteTimeUtc(fullPath); } catch { }
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

        private bool TryResolveFileMapping(string method, string path, out byte[] fileBytes, out string contentType, out DateTime? lastModifiedUtc) {
            fileBytes = null;
            contentType = null;
            lastModifiedUtc = null;

            if (_fileMappings.Count == 0)
                return false;

            if (method == null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.IsNullOrEmpty(path))
                return false;

            var normalized = NormalizePath(path);
            if (_fileMappings.TryGetValue(normalized, out var filePath)) {
                if (File.Exists(filePath)) {
                    fileBytes = File.ReadAllBytes(filePath);
                    contentType = GetMimeType(Path.GetExtension(filePath));
                    try { lastModifiedUtc = File.GetLastWriteTimeUtc(filePath); } catch { }
                    return true;
                }
            }
            return false;
        }

        private static string GetMimeType(string extension) {
            if (string.IsNullOrEmpty(extension))
                return "application/octet-stream";
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
                default:       return "application/octet-stream";
            }
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
            // URL-decode the path portion so percent-encoded segments
            // (e.g. %20, %2B) match the plain-text route registration.
            try { path = Uri.UnescapeDataString(path); } catch { }
            if (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);
            return path;
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
            // Chunked transfer encoding = streaming, not complete.
            if (request.Headers != null
                && request.Headers.TryGetValue("Transfer-Encoding", out var te)
                && te != null
                && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) {
                return false;
            }
            // No Content-Length and not chunked — body is whatever was received.
            return true;
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
            _requestBuffers.Clear();
            foreach (var kv in _activeUploadConnections) {
                kv.Value.Complete();
            }
            _activeUploadConnections.Clear();
            foreach (var kv in _activeRtmpConnections) {
                kv.Value.Stop();
            }
            _activeRtmpConnections.Clear();
        }

        /// <summary>
        /// Cleans up per-connection HTTP state (buffers, active streams, uploads).
        /// Called on client disconnection.
        /// </summary>
        internal void CleanupHttpConnection(Guid connId) {
            _requestBuffers.TryRemove(connId, out _);
            _activeStreamConnections.TryRemove(connId, out _);
            if (_activeUploadConnections.TryRemove(connId, out var upload))
                upload.Complete();
        }

        private void OnHttpClientDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != null)
                CleanupHttpConnection(e.Connection.ID);
        }

        public HttpServer(int Port, string Name = "HttpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
            this.ClientDisconnected += OnHttpClientDisconnected;
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

        internal void InvokeCallbacks(IReceivedEventArgs e) {
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
            this.ClientDisconnected += OnHttpClientDisconnected;
        }

        internal void GetRequestAsync(ref IReceivedEventArgs e) {
            try {
                if (e == null || e.Obj == null || e.Connection == null)
                    return;

                // If this connection has an active streaming handler (e.g., OBS uploading video),
                // skip HTTP parsing. The raw data is still available to other OnReceive subscribers.
                if (_activeStreamConnections.ContainsKey(e.Connection.ID))
                    return;

                // If this connection has an active upload-stream handler, forward raw data to it.
                if (_activeUploadConnections.TryGetValue(e.Connection.ID, out var activeUpload)) {
                    var uploadBytes = TryGetRequestBytes(e.Obj);
                    if (uploadBytes != null && uploadBytes.Length > 0) {
                        activeUpload.Enqueue(uploadBytes);
                    }
                    return;
                }

                // If this connection has an active RTMP session, forward data to it.
                if (_activeRtmpConnections.TryGetValue(e.Connection.ID, out var rtmpSession)) {
                    var rtmpBytes = TryGetRequestBytes(e.Obj);
                    if (rtmpBytes != null && rtmpBytes.Length > 0)
                        rtmpSession.ProcessData(rtmpBytes);
                    return;
                }

                // Check if we're already buffering an HTTP request for this connection.
                // If so, skip protocol detection and HTTP validation — just accumulate data.
                bool isBuffering = _requestBuffers.ContainsKey(e.Connection.ID);

                if (!isBuffering) {
                    // Detect new RTMP connections (version byte 0x03 + C1 zero field at offset 5-8)
                    if (_rtmpPublishRoutes.Count > 0) {
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
                }

                // Accumulate incoming bytes into a per-connection buffer so that
                // HTTP requests spanning multiple TCP segments are fully received
                // before parsing. Without this, large POST uploads are parsed from
                // the first segment alone and the connection is closed prematurely.
                var incoming = TryGetRequestBytes(e.Obj);
                if (incoming == null || incoming.Length == 0)
                    return;

                var buffer = _requestBuffers.GetOrAdd(e.Connection.ID, _ => new List<byte>());
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
                _requestBuffers.TryRemove(e.Connection.ID, out _);

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
                    // When a regular Map route also exists for the same method+path,
                    // prefer it if the request body is already fully available (i.e.,
                    // a normal form POST rather than a streaming upload).
                    bool preferRegularRoute = false;
                    if (_routes.Count > 0) {
                        var np = NormalizePath(request.Path);
                        if (request.Method != null && np != null
                            && _routes.TryGetValue(request.Method, out var routeByPath)
                            && routeByPath.ContainsKey(np)) {
                            preferRegularRoute = IsBodyComplete(request);
                        }
                    }

                    if (!preferRegularRoute) {
                        var upload = new UploadStream(e.Connection);
                        _activeUploadConnections.TryAdd(e.Connection.ID, upload);

                        // Queue initial body data if present in the first read
                        if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                            upload.Enqueue(request.BodyBytes);
                        }

                        // If the full body was already received in the initial request,
                        // complete the upload stream so the handler's ReadAsync loop
                        // terminates instead of blocking forever waiting for data.
                        if (IsBodyComplete(request)) {
                            upload.Complete();
                            _activeUploadConnections.TryRemove(e.Connection.ID, out _);
                        }

                        var connId = e.Connection.ID;
                        var conn = e.Connection;
                        var self = this;
                        Task.Run(() => {
                            try {
                                uploadHandler(conn, request, upload, ct);
                            } catch {
                            } finally {
                                upload.Complete();
                                _activeUploadConnections.TryRemove(connId, out _);

                                // Send a 200 OK response so the HTTP client doesn't hang
                                // waiting for a response that never comes.
                                try {
                                    var responseStream = conn.Stream;
                                    if (responseStream != null && !conn.Closed && !conn.Closing) {
                                        var okBody = Encoding.UTF8.GetBytes("OK");
                                        var okHeader = Encoding.UTF8.GetBytes(
                                            "HTTP/1.1 200 OK\r\n" +
                                            "Content-Type: text/plain\r\n" +
                                            "Content-Length: " + okBody.Length + "\r\n" +
                                            "Connection: close\r\n\r\n");
                                        responseStream.Write(okHeader, 0, okHeader.Length);
                                        responseStream.Write(okBody, 0, okBody.Length);
                                        responseStream.Flush();
                                    }
                                } catch { }

                                // Graceful connection close
                                try { self.CloseConnection(conn, DisconnectionReason.LocalSocketClosed); } catch { }
                            }
                        });
                        return;
                    }
                }

                var handledByRoute = false;
                if (_routes.Count > 0 || IndexPageHtml != null || Robots != null) {
                    var routeObj = ResolveRouteObject(e.Connection, request, ct);
                    if (routeObj != null) {
                        // FileResponse allows route handlers to return binary data with an explicit content type.
                        if (routeObj is FileResponse fileResp) {
                            handledByRoute = true;
                            context.Response.BodyBytes = fileResp.Data;
                            context.Response.ContentType = fileResp.ContentType ?? "application/octet-stream";
                        } else {
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
                }

                // Check file mappings (URL-to-file routes)
                if (!handledByRoute) {
                    var fileNormalizedPath = NormalizePath(request.Path);
                    if (TryResolveFileMapping(request.Method, fileNormalizedPath, out var fileMapBytes, out var fileMapMime, out var fileMapLastMod)) {
                        handledByRoute = true;
                        context.Response.BodyBytes = fileMapBytes;
                        context.Response.ContentType = fileMapMime;
                    }
                }

                // Check mapped directories for static file serving
                if (!handledByRoute) {
                    var normalizedPath = NormalizePath(request.Path);
                    string clientIp = null;
                    string authHeader = null;
                    try { clientIp = e.Connection?.EndPoint?.Address?.ToString(); } catch { }
                    try { if (request?.Headers != null) request.Headers.TryGetValue("Authorization", out authHeader); } catch { }
                    if (TryResolveDirectoryFile(request.Method, normalizedPath, clientIp, authHeader, out var fileBytes, out var fileMime, out var htAccessResult, out var htAuthRealm, out var htExtraHeaders, out var fileLastModified)) {
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

                            // Compute ETag and Last-Modified for cache validation
                            string etag = null;
                            if (fileBytes != null && fileBytes.Length > 0) {
                                using (var sha = System.Security.Cryptography.SHA256.Create()) {
                                    var hash = sha.ComputeHash(fileBytes);
                                    etag = "\"" + BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant() + "\"";
                                }
                            }

                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                            if (etag != null)
                                context.Response.Headers["ETag"] = etag;
                            if (fileLastModified.HasValue)
                                context.Response.Headers["Last-Modified"] = fileLastModified.Value.ToString("R");

                            // Handle conditional requests (304 Not Modified)
                            bool notModified = false;
                            if (etag != null && request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch)) {
                                notModified = ifNoneMatch.Trim() == etag || ifNoneMatch.Trim() == "*";
                            }
                            if (!notModified && fileLastModified.HasValue && request.Headers.TryGetValue("If-Modified-Since", out var ifModSince)) {
                                if (DateTime.TryParse(ifModSince, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var sinceDate)) {
                                    // Truncate to second precision for comparison (HTTP dates have no sub-second)
                                    var fileMod = new DateTime(fileLastModified.Value.Ticks - (fileLastModified.Value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
                                    notModified = fileMod <= sinceDate;
                                }
                            }

                            if (notModified) {
                                context.StatusCodeNumber = 304;
                                context.ReasonPhrase = "Not Modified";
                                context.Response.BodyBytes = Array.Empty<byte>();
                                context.Response.ContentType = fileMime;
                            } else {
                                context.Response.BodyBytes = fileBytes;
                                context.Response.ContentType = fileMime;
                            }
                        }
                        if (htExtraHeaders != null && htExtraHeaders.Count > 0) {
                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in htExtraHeaders)
                                context.Response.Headers[kv.Key] = kv.Value;
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
                    if (!context.Response.Headers.ContainsKey("Connection"))
                        context.Response.Headers["Connection"] = "close";
                    if (CacheControl != null && !context.Response.Headers.ContainsKey("Cache-Control"))
                        context.Response.Headers["Cache-Control"] = CacheControl;
                }

                // Disable Nagle's algorithm so the response is pushed to the
                // network immediately rather than waiting for a full TCP segment.
                try { e.Connection.Socket.NoDelay = true; } catch { }

                // Capture the stream reference once — a background thread
                // can dispose the socket between property accesses.
                var responseStream = e.Connection.Stream;
                if (responseStream != null && !e.Connection.Closed && !e.Connection.Closing) {
                    context.Response.EnsureBodyBytes();
                    int bodyLen = context.Response.BodyBytes != null ? context.Response.BodyBytes.Length : 0;

                    if (Options.Logging) {
                        try {
                            var ua = request.Headers != null && request.Headers.ContainsKey("User-Agent") ? request.Headers["User-Agent"] : "";
                            var ep = e.Connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                            Log(request.Method + " " + request.Path + " > " + context.StatusCodeNumber + " (" + bodyLen + " bytes) UA: " + ua + " - " + ep);
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
                // 3. Call Close() for resource cleanup.
                try {
                    var socket = e.Connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { CloseConnection(e.Connection, DisconnectionReason.LocalSocketClosed); } catch { }
            } catch (Exception ex) {
                InvokeOnError(e.Connection, ex);
                // Ensure the client always receives a valid HTTP response,
                // even when an exception occurs during request processing.
                try {
                    var errStream = e.Connection.Stream;
                    if (errStream != null && !e.Connection.Closed && !e.Connection.Closing) {
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
                    var socket = e.Connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { CloseConnection(e.Connection, DisconnectionReason.LocalSocketClosed); } catch { }
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

                // Separate query string from path so route matching works with
                // URL-encoded parameters and handlers can access them via
                // request.QueryString / request.QueryParameters.
                var qIdx = request.Path.IndexOf('?');
                if (qIdx >= 0) {
                    request.QueryString = request.Path.Substring(qIdx + 1);
                    request.Path = request.Path.Substring(0, qIdx);
                    ParseQueryParameters(request);
                }

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

        private static void ParseQueryParameters(HttpRequest request) {
            if (string.IsNullOrEmpty(request.QueryString)) return;
            foreach (var pair in request.QueryString.Split('&')) {
                if (string.IsNullOrEmpty(pair)) continue;
                var eqIdx = pair.IndexOf('=');
                string key, value;
                if (eqIdx >= 0) {
                    key = Uri.UnescapeDataString(pair.Substring(0, eqIdx).Replace('+', ' '));
                    value = Uri.UnescapeDataString(pair.Substring(eqIdx + 1).Replace('+', ' '));
                } else {
                    key = Uri.UnescapeDataString(pair.Replace('+', ' '));
                    value = "";
                }
                request.QueryParameters[key] = value;
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
        /// <summary>
        /// The raw query string from the request URL (everything after the <c>?</c>),
        /// or <see langword="null"/> if no query string was present.
        /// </summary>
        public string QueryString { get; set; }
        /// <summary>
        /// URL-decoded key/value pairs parsed from the query string.
        /// Both <c>%XX</c> and <c>+</c> (as space) encodings are handled.
        /// </summary>
        public Dictionary<string, string> QueryParameters { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>
        /// Builds the full HTTP status line (e.g. <c>HTTP/1.1 200 OK</c>),
        /// always falling back to <c>HTTP/1.1 200 OK</c> when any component is
        /// missing or empty.
        /// </summary>
        internal string StatusLine {
            get {
                var version = string.IsNullOrWhiteSpace(Version) ? "HTTP/1.1" : Version;
                string statusCode;
                if (Context != null) {
                    statusCode = Context.StatusCode;
                    if (string.IsNullOrWhiteSpace(statusCode))
                        statusCode = "200 OK";
                } else {
                    statusCode = "200 OK";
                }
                return version + " " + statusCode;
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
            sb.Append(StatusLine + "\r\n");
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
            headerSb.Append(StatusLine + "\r\n");
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
            headerSb.Append(StatusLine + "\r\n");
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
            headerSb.Append(StatusLine + "\r\n");
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
    /// Represents a binary file response from a route handler.
    /// Return an instance of this from a <see cref="HttpServer.RouteHandler"/> to send
    /// raw bytes (e.g., an image or PDF) with an explicit content type instead of the
    /// default JSON serialization behaviour.
    /// </summary>
    public class FileResponse {
        /// <summary>The raw response body bytes.</summary>
        public byte[] Data { get; set; }
        /// <summary>The MIME content type (e.g., <c>"image/png"</c>).</summary>
        public string ContentType { get; set; }

        public FileResponse() { }
        public FileResponse(byte[] data, string contentType) {
            Data = data;
            ContentType = contentType;
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
            sb.Append(_response.StatusLine + "\r\n");
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
