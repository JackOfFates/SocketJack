
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {

    public class HttpServer : TcpServer {

        //new private event PeerConnectionRequestEventHandler PeerConnectionRequest;

        public event RequestHandler OnHttpRequest;
        public delegate void RequestHandler(TcpConnection Connection, ref HttpContext context, CancellationToken cancellationToken);

        private void Disposing() {
            OnDisposing -= Disposing;
            RemoveCallback<byte[]>();
        }

        public HttpServer(int Port, string Name = "TcpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            RegisterCallback<byte[]>(GetRequestAsync);
        }

        public HttpServer(TcpOptions Options, int Port, string Name = "TcpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            RegisterCallback<byte[]>(GetRequestAsync);
        }

        internal async void GetRequestAsync(ReceivedEventArgs<byte[]> e) {
            try {
                string inbound = System.Text.UTF8Encoding.UTF8.GetString(e.Object);

                var request = ParseHttpRequest(inbound);
                    var context = new HttpContext {
                        Request = request,
                        Connection = Connection,
                        cancellationToken = default
                    };
                    OnHttpRequest?.Invoke(Connection, ref context, default);
                    await e.Connection.Stream.WriteAsync(context.Response.ToBytes());
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
            }
        }

        /// <summary>
        /// Parses a raw HTTP request string into an HttpRequest object.
        /// </summary>
        /// <param name="rawRequest">The raw HTTP request string.</param>
        /// <returns>HttpRequest object with parsed data.</returns>
        public static HttpRequest ParseHttpRequest(string rawRequest) {
            if (string.IsNullOrWhiteSpace(rawRequest))
                throw new ArgumentException("Request is empty", nameof(rawRequest));

            var request = new HttpRequest();
            using (var reader = new StringReader(rawRequest)) {
                // Parse request line
                var requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                    throw new InvalidDataException("Invalid HTTP request line.");

                var parts = requestLine.Split(' ');
                if (parts.Length < 3)
                    throw new InvalidDataException("Invalid HTTP request line.");

                request.Method = parts[0];
                request.Path = parts[1];
                request.Version = parts[2];

                // Parse headers
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex > 0) {
                        var headerName = line.Substring(0, separatorIndex).Trim();
                        var headerValue = line.Substring(separatorIndex + 1).Trim();
                        request.Headers[headerName] = headerValue;
                    }
                }

                // Parse body (if any)
                var bodyBuilder = new StringBuilder();
                while ((line = reader.ReadLine()) != null) {
                    bodyBuilder.AppendLine(line);
                }
                request.Body = bodyBuilder.Length > 0 ? bodyBuilder.ToString() : null;
            }
            return request;
        }
    }

    public class HttpContext {
        public static string DefaultCharset = "UTF-8";
        public string StatusCode { get; set; } = "200 OK";
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
        private string _ContentType = "text/json";
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
    }

    public class HttpResponse {
        public HttpContext Context { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public object Body {
            get {
                return _Body;
            }
            set {
                if (value == null) {
                    _Body = string.Empty;
                } else if (value is string str) {
                    _Body = str;
                } else {
                    _Body = System.Text.UTF8Encoding.UTF8.GetString(Context.Connection.Parent.Options.Serializer.Serialize(value));
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
        private string _ContentType = "text/json";

        public override string ToString() {
            return "HTTP/1.1 " + Context.StatusCode + "\r\n" +
                   "Content-Type: " + Context.ContentType + "\r\n" +
                  $"Content-Length: {Encoding.UTF8.GetByteCount(_Body)}\r\n" +
                   "Connection: close\r\n" +
                   "\r\n" +
                   Body;
        }

        public byte[] ToBytes() {
            return Encoding.UTF8.GetBytes(ToString());
        }
    }
}
