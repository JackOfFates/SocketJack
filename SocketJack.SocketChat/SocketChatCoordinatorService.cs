using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.SocketChat {
    public sealed class SocketChatCoordinatorService {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        private readonly Uri _endpoint;
        public SocketChatCoordinatorService(string coordinatorUrl) { _endpoint = new Uri(new Uri(coordinatorUrl.TrimEnd('/') + "/"), "socketchat/directory"); }
        public SocketChatMasterList Read() {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string json = client.GetStringAsync(_endpoint).GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<SocketChatMasterList>(json, JsonOptions) ?? new SocketChatMasterList();
        }
        public SocketChatMasterList Update(Action<SocketChatMasterList> mutation) {
            SocketChatMasterList list = Read(); mutation(list); list.Revision++; list.UpdatedUtc = DateTimeOffset.UtcNow;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var content = new StringContent(JsonSerializer.Serialize(list, JsonOptions), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = client.PostAsync(_endpoint, content).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<SocketChatMasterList>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), JsonOptions) ?? list;
        }
    }

    public sealed class SocketChatCoordinatorServer : IDisposable {
        private readonly TcpListener _listener;
        private readonly string _statePath;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        public int Port { get; }
        public SocketChatCoordinatorServer(int port, string statePath) { Port = port; _statePath = statePath; _listener = new TcpListener(IPAddress.Loopback, port); }
        public void Start() { Directory.CreateDirectory(Path.GetDirectoryName(_statePath)); _listener.Start(); _ = Task.Run(AcceptLoopAsync); }
        private async Task AcceptLoopAsync() { while (!_stop.IsCancellationRequested) { try { TcpClient client = await _listener.AcceptTcpClientAsync(); _ = Task.Run(() => HandleAsync(client)); } catch when (_stop.IsCancellationRequested) { } catch { } } }
        private async Task HandleAsync(TcpClient client) {
            using (client) using (NetworkStream stream = client.GetStream()) using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true)) {
                string request = await reader.ReadLineAsync() ?? ""; string line; int length = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);
                string body = ""; if (length > 0) { char[] chars = new char[length]; int read = 0; while (read < length) { int n = await reader.ReadAsync(chars, read, length - read); if (n == 0) break; read += n; } body = new string(chars, 0, read); }
                if (!request.Contains(" /socketchat/directory ", StringComparison.Ordinal)) { await ReplyAsync(stream, 404, "{\"error\":\"not found\"}"); return; }
                string json;
                lock (_gate) {
                    SocketChatMasterList current = Load();
                    if (request.StartsWith("POST ", StringComparison.Ordinal)) { var incoming = JsonSerializer.Deserialize<SocketChatMasterList>(body); if (incoming != null && incoming.Revision >= current.Revision) { current = incoming; Save(current); } }
                    json = JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true });
                }
                await ReplyAsync(stream, 200, json);
            }
        }
        private SocketChatMasterList Load() { try { return File.Exists(_statePath) ? JsonSerializer.Deserialize<SocketChatMasterList>(File.ReadAllText(_statePath)) ?? new SocketChatMasterList() : new SocketChatMasterList(); } catch { return new SocketChatMasterList(); } }
        private void Save(SocketChatMasterList value) { File.WriteAllText(_statePath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); }
        private static async Task ReplyAsync(Stream stream, int status, string body) { byte[] bytes = Encoding.UTF8.GetBytes(body); byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"); await stream.WriteAsync(head, 0, head.Length); await stream.WriteAsync(bytes, 0, bytes.Length); }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
    }
}
