using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SocketJack;
using SocketJack.Net;

namespace SocketJack.MarketWatcher;

// SocketJack owns HTTP/TCP and protocol dispatch. System.Net.WebSockets handles
// RFC6455 framing, fragmentation and control frames over the dispatched stream.
public sealed class MarketSocket(int port, string sessionToken, MarketEngine engine, string path = "/ws") : IProtocolHandler
{
    private readonly ConcurrentDictionary<Guid, Peer> peers = new();
    public string Name => "MarketWatcherWebSocket";
    public bool HasClients => peers.Values.Any(p => p.Started);
    public bool CanHandle(byte[] data) => Encoding.ASCII.GetString(data).StartsWith("GET " + path + " ", StringComparison.Ordinal);
    public bool Authorized(string host, string origin, string cookie) =>
        (host == $"127.0.0.1:{port}" || host == $"localhost:{port}") && origin == "http://" + host &&
        cookie.Split(';').Any(c => c.Trim() == "marketSession=" + sessionToken);
    public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e)
    {
        byte[]? bytes = MutableTcpServer.TryGetRawBytes(e.Obj);
        if (bytes == null) return;
        var peer = peers.GetOrAdd(connection.ID, _ => new Peer(connection.Stream));
        lock (peer)
        {
            if (peer.Started) { if (!peer.Stream.Feed(bytes)) connection.Close(server); return; }
            peer.Header.AddRange(bytes);
            if (peer.Header.Count > 16384) { connection.Close(server); return; }
            string header = Encoding.ASCII.GetString(peer.Header.ToArray());
            int end = header.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) return;
            string[] lines = header[..end].Split("\r\n");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1)) { int colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim(); }
            string key = headers.GetValueOrDefault("Sec-WebSocket-Key", "");
            bool validKey; try { validKey = Convert.FromBase64String(key).Length == 16; } catch { validKey = false; }
            if (lines[0] != "GET " + path + " HTTP/1.1" || !validKey || headers.GetValueOrDefault("Sec-WebSocket-Version") != "13" ||
                !string.Equals(headers.GetValueOrDefault("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) ||
                !Authorized(headers.GetValueOrDefault("Host", ""), headers.GetValueOrDefault("Origin", ""), headers.GetValueOrDefault("Cookie", "")))
            {
                connection.Stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                connection.Close(server); return;
            }
            string accept;
            using (SHA1 sha1 = SHA1.Create()) accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            connection.Stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            peer.Started = true;
            peer.Stream.Feed(peer.Header.Skip(end + 4).ToArray()); peer.Header.Clear();
            _ = Run(server, connection, peer);
        }
    }
    private async Task Run(MutableTcpServer server, NetworkConnection connection, Peer peer)
    {
        using var ws = WebSocket.CreateFromStream(peer.Stream, true, null, TimeSpan.FromSeconds(20));
        using var lifetime = new CancellationTokenSource();
        var send = Task.Run(async () => {
            try { await foreach (var message in peer.Outgoing.Reader.ReadAllAsync(lifetime.Token)) await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json.Options), WebSocketMessageType.Text, true, lifetime.Token); }
            catch { lifetime.Cancel(); }
        });
        try
        {
            peer.Enqueue(await engine.Handle(connection.ID, new("snapshot", null)));
            byte[] buffer = new byte[16384];
            while (ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 16384) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                ClientMessage? request;
                try { request = JsonSerializer.Deserialize<ClientMessage>(message.ToArray(), Json.Options); }
                catch (JsonException) { peer.Enqueue(new("error", null, Error: "Invalid message.")); continue; }
                if (request == null || string.IsNullOrEmpty(request.Id) || request.Id.Length > 80) { peer.Enqueue(new("error", null, Error: "A request ID is required.")); continue; }
                peer.Enqueue(await engine.Handle(connection.ID, request));
            }
        }
        catch (Exception) { /* Never log payloads: a request may contain a secret. */ }
        finally
        {
            lifetime.Cancel(); peer.Outgoing.Writer.TryComplete();
            connection.Close(server); peers.TryRemove(connection.ID, out _);
            await engine.Disconnect(connection.ID);
            try { await send; } catch { }
        }
    }
    public void Broadcast(ServerMessage message)
    {
        foreach (var p in peers.Values.Where(p => p.Started)) p.Enqueue(message);
    }
    public void OnDisconnected(MutableTcpServer server, NetworkConnection connection)
    {
        if (peers.TryRemove(connection.ID, out var p)) { p.Stream.Complete(); p.Outgoing.Writer.TryComplete(); }
    }
    private sealed class Peer(Stream output)
    {
        public List<byte> Header { get; } = [];
        public bool Started;
        public DispatchStream Stream { get; } = new(output);
        public Channel<ServerMessage> Outgoing { get; } = Channel.CreateBounded<ServerMessage>(64);
        public void Enqueue(ServerMessage message) { if (!Outgoing.Writer.TryWrite(message)) Stream.Complete(); }
    }
    private sealed class DispatchStream(Stream output) : Stream
    {
        private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(128);
        private byte[] current = [];
        private int offset;
        public bool Feed(byte[] data) => data.Length == 0 || incoming.Writer.TryWrite(data);
        public void Complete() => incoming.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (offset == current.Length)
            {
                if (!await incoming.Reader.WaitToReadAsync(ct)) return 0;
                if (!incoming.Reader.TryRead(out var next)) continue;
                current = next; offset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => output.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => output.WriteAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
    }
}
