using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {

    public sealed class PersonaPlexBridgeEndpoint {
        public bool Enabled { get; set; } = true;
        public Uri UpstreamUri { get; set; }
        public int ConnectTimeoutMs { get; set; } = 5000;
    }

    public class PersonaPlexWebSocketBridgeProtocolHandler : IProtocolHandler {
        public string Name => "PersonaPlexWebSocketBridge";

        private const string WebSocketMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int OpcodeContinuation = 0x0;
        private const int OpcodeText = 0x1;
        private const int OpcodeBinary = 0x2;
        private const int OpcodeClose = 0x8;
        private const int OpcodePing = 0x9;
        private const int OpcodePong = 0xA;
        private const int PersonaPlexErrorKind = 0x05;
        private const int MaxPendingBytes = 4 * 1024 * 1024;

        private readonly string _path;
        private readonly Func<HttpRequest, PersonaPlexBridgeEndpoint> _resolveEndpoint;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<Guid, State> _states = new ConcurrentDictionary<Guid, State>();

        public PersonaPlexWebSocketBridgeProtocolHandler(
            Func<HttpRequest, PersonaPlexBridgeEndpoint> resolveEndpoint,
            Action<string> log = null,
            string path = "/api/personaplex/ws") {
            _resolveEndpoint = resolveEndpoint ?? throw new ArgumentNullException(nameof(resolveEndpoint));
            _log = log;
            _path = NormalizeWebSocketPath(path);
        }

        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 20)
                return false;
            if (data[0] != (byte)'G' || data[1] != (byte)'E' || data[2] != (byte)'T' || data[3] != (byte)' ')
                return false;

            string text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096));
            if (text.IndexOf("Upgrade:", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string path = GetRequestPath(text);
            return string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            State state = _states.GetOrAdd(connection.ID, _ => new State());
            lock (state) {
                state.Buffer.AddRange(incoming);
                if (!state.HandshakeComplete)
                    TryCompleteHandshake(server, connection, state);
                else
                    ProcessFrames(server, connection, state);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            if (connection == null)
                return;
            if (_states.TryRemove(connection.ID, out State state)) {
                try { state.Cancellation.Cancel(); } catch { }
                try { state.Upstream?.Abort(); } catch { }
                try { state.Upstream?.Dispose(); } catch { }
                try { state.Cancellation.Dispose(); } catch { }
            }
        }

        private void TryCompleteHandshake(MutableTcpServer server, NetworkConnection connection, State state) {
            int hdrEnd = FindHeaderEnd(state.Buffer);
            if (hdrEnd < 0)
                return;

            string request = Encoding.UTF8.GetString(state.Buffer.ToArray(), 0, hdrEnd);
            HttpRequest parsedRequest;
            try {
                parsedRequest = HttpServer.ParseHttpRequest(Encoding.UTF8.GetBytes(request));
                var securityDecision = server.EvaluateEndpointSecurityHttpRequest(connection, parsedRequest);
                if (!securityDecision.Allowed) {
                    SendHttpResponse(connection, HttpServer.EndpointSecurityStatusText(securityDecision), securityDecision.Message);
                    TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);
                if (!server.IsHostAllowed(parsedRequest)) {
                    SendHttpResponse(connection, "421 Misdirected Request", "421 Misdirected Request");
                    TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                    return;
                }
            } catch {
                TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                return;
            }

            PersonaPlexBridgeEndpoint endpoint;
            try {
                endpoint = _resolveEndpoint(parsedRequest);
            } catch (Exception ex) {
                SendHttpResponse(connection, "500 Internal Server Error", "PersonaPlex bridge target failed: " + ex.Message);
                TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                return;
            }

            if (endpoint == null || !endpoint.Enabled || endpoint.UpstreamUri == null) {
                SendHttpResponse(connection, "503 Service Unavailable", "PersonaPlex bridge is not configured.");
                TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                return;
            }

            string secKey = "";
            foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    secKey = line.Substring(line.IndexOf(':') + 1).Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(secKey)) {
                TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                return;
            }

            string acceptKey;
            using (SHA1 sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(secKey + WebSocketMagicGuid));
                acceptKey = Convert.ToBase64String(hash);
            }

            byte[] responseBytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                "\r\n");
            try {
                Stream stream = connection.Stream;
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
            } catch (Exception ex) {
                server.InvokeOnError(connection, ex);
                return;
            }

            state.HandshakeComplete = true;
            state.Endpoint = endpoint;
            state.Buffer.RemoveRange(0, hdrEnd);
            state.ConnectTask = Task.Run(() => ConnectAndPumpAsync(server, connection, state));

            if (state.Buffer.Count > 0)
                ProcessFrames(server, connection, state);
        }

        private async Task ConnectAndPumpAsync(MutableTcpServer server, NetworkConnection connection, State state) {
            ClientWebSocket upstream = new ClientWebSocket();
            try {
                using (CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(state.Cancellation.Token)) {
                    int timeout = Math.Max(1000, state.Endpoint.ConnectTimeoutMs);
                    connectCts.CancelAfter(timeout);
                    await upstream.ConnectAsync(state.Endpoint.UpstreamUri, connectCts.Token).ConfigureAwait(false);
                }

                List<PendingFrame> pending;
                lock (state) {
                    state.Upstream = upstream;
                    state.UpstreamReady = true;
                    pending = state.PendingFrames.ToList();
                    state.PendingFrames.Clear();
                    state.PendingBytes = 0;
                }

                _log?.Invoke("[PersonaPlex] Bridge connected to " + state.Endpoint.UpstreamUri);
                foreach (PendingFrame frame in pending)
                    await SendToUpstreamAsync(state, frame.Payload, frame.MessageType).ConfigureAwait(false);

                await PumpUpstreamToBrowserAsync(server, connection, state).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                if (!state.Cancellation.IsCancellationRequested)
                    SendPersonaPlexError(connection, state, "PersonaPlex bridge timed out connecting to the upstream server.");
            } catch (Exception ex) {
                SendPersonaPlexError(connection, state, "PersonaPlex bridge failed: " + ex.Message);
                _log?.Invoke("[PersonaPlex] Bridge failed: " + ex.Message);
            } finally {
                lock (state) {
                    state.UpstreamReady = false;
                    state.PendingFrames.Clear();
                    state.PendingBytes = 0;
                }
                try { upstream.Dispose(); } catch { }
                SendCloseFrame(connection, state);
                TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
            }
        }

        private static async Task PumpUpstreamToBrowserAsync(MutableTcpServer server, NetworkConnection connection, State state) {
            byte[] buffer = new byte[32 * 1024];
            while (!state.Cancellation.IsCancellationRequested &&
                   state.Upstream != null &&
                   state.Upstream.State == WebSocketState.Open) {
                using (MemoryStream message = new MemoryStream()) {
                    WebSocketReceiveResult result;
                    do {
                        result = await state.Upstream.ReceiveAsync(new ArraySegment<byte>(buffer), state.Cancellation.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) {
                            SendCloseFrame(connection, state);
                            return;
                        }
                        if (result.Count > 0)
                            message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    byte[] payload = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                        SendFrame(connection, state, payload, OpcodeText);
                    else if (result.MessageType == WebSocketMessageType.Binary)
                        SendFrame(connection, state, payload, OpcodeBinary);
                }
            }
        }

        private void ProcessFrames(MutableTcpServer server, NetworkConnection connection, State state) {
            while (state.Buffer.Count >= 2) {
                if (!TryReadFrame(state, out int opcode, out byte[] payload))
                    return;

                var securityDecision = server.RecordEndpointSecurityWebSocketFrame(connection, payload.Length);
                if (!securityDecision.Allowed) {
                    SendCloseFrame(connection, state);
                    TryClose(server, connection, DisconnectionReason.LocalSocketClosed);
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);

                switch (opcode) {
                    case OpcodeText:
                        ForwardOrQueueBrowserFrame(connection, state, payload, WebSocketMessageType.Text);
                        break;
                    case OpcodeBinary:
                    case OpcodeContinuation:
                        ForwardOrQueueBrowserFrame(connection, state, payload, WebSocketMessageType.Binary);
                        break;
                    case OpcodeClose:
                        SendCloseFrame(connection, state);
                        TryClose(server, connection, DisconnectionReason.RemoteSocketClosed);
                        return;
                    case OpcodePing:
                        SendFrame(connection, state, payload, OpcodePong);
                        break;
                    case OpcodePong:
                        break;
                }
            }
        }

        private static bool TryReadFrame(State state, out int opcode, out byte[] payload) {
            opcode = 0;
            payload = Array.Empty<byte>();
            int offset = 0;
            byte b0 = state.Buffer[offset++];
            byte b1 = state.Buffer[offset++];
            opcode = b0 & 0x0F;
            bool masked = (b1 & 0x80) != 0;
            long payloadLen = b1 & 0x7F;

            if (payloadLen == 126) {
                if (state.Buffer.Count < offset + 2) return false;
                payloadLen = (state.Buffer[offset] << 8) | state.Buffer[offset + 1];
                offset += 2;
            } else if (payloadLen == 127) {
                if (state.Buffer.Count < offset + 8) return false;
                payloadLen = 0;
                for (int i = 0; i < 8; i++)
                    payloadLen = (payloadLen << 8) | state.Buffer[offset + i];
                offset += 8;
            }

            if (payloadLen > int.MaxValue)
                return false;

            byte[] maskKey = null;
            if (masked) {
                if (state.Buffer.Count < offset + 4) return false;
                maskKey = new byte[4];
                for (int i = 0; i < 4; i++)
                    maskKey[i] = state.Buffer[offset + i];
                offset += 4;
            }

            if (state.Buffer.Count < offset + payloadLen)
                return false;

            int count = (int)payloadLen;
            payload = new byte[count];
            state.Buffer.CopyTo(offset, payload, 0, count);
            state.Buffer.RemoveRange(0, offset + count);
            if (masked && maskKey != null) {
                for (int i = 0; i < count; i++)
                    payload[i] ^= maskKey[i % 4];
            }
            return true;
        }

        private void ForwardOrQueueBrowserFrame(NetworkConnection connection, State state, byte[] payload, WebSocketMessageType type) {
            ClientWebSocket upstream;
            lock (state) {
                upstream = state.Upstream;
                if (!state.UpstreamReady || upstream == null || upstream.State != WebSocketState.Open) {
                    if (state.PendingBytes + payload.Length > MaxPendingBytes) {
                        SendPersonaPlexError(connection, state, "PersonaPlex bridge is still connecting and its send buffer is full.");
                        return;
                    }
                    state.PendingFrames.Add(new PendingFrame(payload, type));
                    state.PendingBytes += payload.Length;
                    return;
                }
            }

            try {
                SendToUpstreamAsync(state, payload, type).GetAwaiter().GetResult();
            } catch (Exception ex) {
                SendPersonaPlexError(connection, state, "PersonaPlex bridge send failed: " + ex.Message);
            }
        }

        private static async Task SendToUpstreamAsync(State state, byte[] payload, WebSocketMessageType type) {
            ClientWebSocket upstream = state.Upstream;
            if (upstream == null || upstream.State != WebSocketState.Open)
                return;
            lock (state.UpstreamSendLock) {
                upstream.SendAsync(new ArraySegment<byte>(payload ?? Array.Empty<byte>()), type, true, state.Cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static void SendPersonaPlexError(NetworkConnection connection, State state, string message) {
            byte[] text = Encoding.UTF8.GetBytes(message ?? "PersonaPlex bridge error.");
            byte[] payload = new byte[text.Length + 1];
            payload[0] = PersonaPlexErrorKind;
            Buffer.BlockCopy(text, 0, payload, 1, text.Length);
            SendFrame(connection, state, payload, OpcodeBinary);
        }

        private static void SendFrame(NetworkConnection connection, State state, byte[] payload, int opcode) {
            try {
                byte[] frame = BuildFrame(payload ?? Array.Empty<byte>(), opcode);
                Stream stream = connection.Stream;
                if (stream == null)
                    return;
                lock (state.ClientSendLock) {
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            } catch { }
        }

        private static byte[] BuildFrame(byte[] payload, int opcode) {
            payload ??= Array.Empty<byte>();
            if ((opcode == OpcodeClose || opcode == OpcodePing || opcode == OpcodePong) && payload.Length > 125)
                payload = payload.Take(125).ToArray();

            int headerLength = 2;
            if (payload.Length > ushort.MaxValue)
                headerLength += 8;
            else if (payload.Length >= 126)
                headerLength += 2;

            byte[] frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | (opcode & 0x0F));
            int offset = 2;
            if (payload.Length > ushort.MaxValue) {
                frame[1] = 127;
                ulong len = (ulong)payload.Length;
                for (int i = 7; i >= 0; i--) {
                    frame[offset + i] = (byte)(len & 0xFF);
                    len >>= 8;
                }
                offset += 8;
            } else if (payload.Length >= 126) {
                frame[1] = 126;
                frame[offset++] = (byte)((payload.Length >> 8) & 0xFF);
                frame[offset++] = (byte)(payload.Length & 0xFF);
            } else {
                frame[1] = (byte)payload.Length;
            }

            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, frame, offset, payload.Length);
            return frame;
        }

        private static void SendCloseFrame(NetworkConnection connection, State state) {
            SendFrame(connection, state, Array.Empty<byte>(), OpcodeClose);
        }

        private static void SendHttpResponse(NetworkConnection connection, string status, string body) {
            try {
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
                byte[] headerBytes = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 " + status + "\r\n" +
                    "Content-Type: text/plain; charset=utf-8\r\n" +
                    "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "Connection: close\r\n\r\n");
                Stream stream = connection.Stream;
                stream.Write(headerBytes, 0, headerBytes.Length);
                if (bodyBytes.Length > 0)
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            } catch { }
        }

        private static void TryClose(MutableTcpServer server, NetworkConnection connection, DisconnectionReason reason) {
            try { connection.Close(server, reason); } catch { }
        }

        private static int FindHeaderEnd(List<byte> buffer) {
            for (int i = 0; i <= buffer.Count - 4; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    return i + 4;
            }
            return -1;
        }

        private static string NormalizeWebSocketPath(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return "/";
            path = path.Trim();
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        private static string GetRequestPath(string requestText) {
            string firstLine = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2)
                return "";
            string target = parts[1];
            int query = target.IndexOf('?');
            return query >= 0 ? target.Substring(0, query) : target;
        }

        private sealed class State {
            public readonly List<byte> Buffer = new List<byte>();
            public readonly List<PendingFrame> PendingFrames = new List<PendingFrame>();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly object ClientSendLock = new object();
            public readonly object UpstreamSendLock = new object();
            public bool HandshakeComplete;
            public bool UpstreamReady;
            public int PendingBytes;
            public ClientWebSocket Upstream;
            public PersonaPlexBridgeEndpoint Endpoint;
            public Task ConnectTask;
        }

        private sealed class PendingFrame {
            public PendingFrame(byte[] payload, WebSocketMessageType messageType) {
                Payload = payload ?? Array.Empty<byte>();
                MessageType = messageType;
            }

            public byte[] Payload { get; }
            public WebSocketMessageType MessageType { get; }
        }
    }
}
