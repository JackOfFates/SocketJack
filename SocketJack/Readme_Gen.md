# SocketJack

![SocketJack Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/SocketJackIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A .NET networking library that lets you send and receive any object over TCP or UDP with a single method call. Powered by `System.Text.Json` serialization, SocketJack handles framing, segmentation, and deserialization automatically — just call `Send(myObject)` on one end and register a typed callback on the other. No manual byte wrangling, no protocol boilerplate. Built on `System.Net.Sockets` with optional `SslStream` TLS 1.2 encryption, peer-to-peer relay, and a unified API across TCP, UDP, HTTP, and WebSocket transports.

**Target frameworks:** .NET Standard 2.1 · .NET 6 · .NET 8 · .NET 9 · .NET 10

---

## Features

| Category | Highlights |
|---|---|
| **Transport** | Built on `System.Net.Sockets.Socket` and `NetworkStream`. Unified `TcpClient` / `TcpServer`, `UdpClient` / `UdpServer`, and WebSocket API with consistent connection lifecycle events. |
| **Serialization** | Default `System.Text.Json` serializer with pluggable `ISerializer` interface, custom `JsonConverter` support (e.g., `Bitmap`, `byte[]`, `Type`), and type whitelist/blacklist for secure deserialization. |
| **Peer-to-Peer** | Automatic peer discovery, host/client role management, relay-based NAT traversal, and metadata propagation via the `Identifier` class. |
| **Compression** | Pluggable `ICompression` interface with built-in `GZipStream` and `DeflateStream` implementations, configurable `CompressionLevel`. |
| **Performance** | Large configurable buffers (default 100 MB), fully async I/O, automatic message segmentation, outbound chunking with configurable flush interval, and upload/download bandwidth throttling (Mbps). |
| **Security** | `SslStream` with TLS 1.2, `X509Certificate` authentication, and fine-grained control over allowed message types and connection policies. |
| **Extensibility** | Rich event system for connection, disconnection, peer updates, and data receipt. Attach arbitrary metadata to any peer or connection for dynamic routing and discovery. |

---

## Supported Transports

### TCP

The core transport. `TcpClient` and `TcpServer` provide reliable, ordered, stream-oriented communication with automatic message segmentation for arbitrarily large payloads. TLS is supported via `SslStream` and `X509Certificate`.

### UDP

`UdpClient` and `UdpServer` mirror the TCP API but use connectionless datagrams. The same `NetworkOptions`, serialization, compression, peer-to-peer, and callback systems work identically. Payloads are limited by `MaxDatagramSize` (default 65,507 bytes).

| | TCP | UDP |
|---|---|---|
| **Connection** | Stream-oriented, persistent | Connectionless datagrams |
| **Reliability** | Guaranteed delivery & ordering | No built-in delivery guarantee |
| **Max payload** | Unlimited (automatic segmentation) | Limited by `MaxDatagramSize` |
| **TLS** | Supported via `SslStream` | Not supported |

### HTTP

`HttpServer` and `HttpClient` layer a familiar HTTP API on top of the TCP transport. Route mapping, typed callbacks, chunked transfer-encoding, HTTPS/TLS, and automatic redirects are all built in.

| Class | Description |
|---|---|
| `HttpServer` | Extends `TcpServer`. Parses HTTP requests, resolves routes, and writes responses. |
| `HttpClient` | Extends `TcpClient`. Sends HTTP/HTTPS requests with redirect and chunked-transfer support. |
| `BroadcastServer` | Attaches to an `HttpServer` to relay live video from OBS (RTMP or HTTP upload) to browser and VLC viewers via FLV. |
| `HttpContext` | Carries the `HttpRequest`, `HttpResponse`, status code, and content type for a single request cycle. |
| `HttpRequest` | Parsed request: `Method`, `Path`, `Headers`, `Body`, `BodyBytes`. |
| `HttpResponse` | Response: `StatusCodeNumber`, `Headers`, `Body`/`BodyBytes`, `ContentType`. |

### WebSocket

`WebSocketClient` and `WebSocketServer` implement RFC 6455 while sharing the same serialization, compression, P2P, and callback systems. The server handles the HTTP upgrade handshake automatically and can generate JavaScript class constructors for browser clients.

| | TCP | WebSocket |
|---|---|---|
| **Protocol** | Raw TCP stream | WebSocket frames (RFC 6455) |
| **Handshake** | TCP three-way handshake | HTTP Upgrade + WebSocket handshake |
| **Browser support** | Not natively supported | Full browser `WebSocket` API compatibility |
| **Client connect** | `Connect(host, port)` | `Connect(host, port)` or `ConnectAsync(uri)` |

### WPF Live Control Sharing

> **Requires the [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) NuGet package.**

The `SocketJack.WPF` library lets you share any WPF `FrameworkElement` over a `TcpClient` connection. The sharer captures JPEG frames at a configurable frame rate and streams them to a remote peer. The viewer displays those frames in an `Image` control and automatically forwards mouse input back, so the remote user can interact with the shared element as if it were local.

---

## Use Cases

- **Real-time multiplayer games** — low-latency communication with dynamic peer discovery.
- **Distributed chat** — P2P messaging with metadata-driven room discovery.
- **IoT device networks** — efficient, secure communication across flexible topologies.
- **Remote control & automation** — event-driven command/control of remote systems.
- **Custom protocols** — build domain-specific protocols on top of any transport with full control over serialization and peer management.

---

## Getting Started

Install via NuGet:

```
Install-Package SocketJack
```

---

## Examples

### TCP — Server & Client

```cs
// Create and start a server
var server = new TcpServer(port: 12345);
server.Listen();

// Connect a client
var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);

// Send any serializable object
client.Send(new CustomMessage("Hello!"));

// Handle it with a typed callback
server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");

    // Echo back to the sender
    args.Connection.Send(new CustomMessage("10-4"));
});
```

### TCP — Default Options

Must be set before creating any Client or Server instance.

```cs
NetworkOptions.DefaultOptions.UsePeerToPeer = true;
```

### TCP — Attach Metadata (Server-Side)

```cs
// Inside a server callback or ClientConnected handler:
connection.SetMetaData("room", "Lobby1");
```

### UDP — Server & Client

```cs
var server = new UdpServer(port: 12345);
server.Listen();

var client = new UdpClient();
await client.Connect("127.0.0.1", 12345);

// Same Send / RegisterCallback pattern as TCP
client.Send(new CustomMessage("Hello via UDP!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});
```

### UDP — Peer-to-Peer & Broadcasting

```cs
// Send to a specific peer (relayed through the server)
client.Send(remotePeer, new CustomMessage("P2P over UDP"));

// Broadcast to all peers
client.SendPeerBroadcast(new CustomMessage("Hello everyone!"));

// Server broadcasts to all connected clients
server.SendBroadcast(new CustomMessage("Server announcement"));

// Server sends to a specific client by Identifier
server.Send(clientIdentifier, new CustomMessage("Direct message"));
```

### UDP — Options

```cs
var options = new NetworkOptions();
options.MaxDatagramSize      = 1400;     // Safe MTU (default 65,507)
options.ClientTimeoutSeconds = 60;       // Default 30
options.UdpReceiveBufferSize = 131072;   // Default 65,535
options.EnableBroadcast      = true;     // Default false
options.ClientTimeoutCheckIntervalMs = 10000; // Default 5,000

var server = new UdpServer(options, port: 12345);
var client = new UdpClient(options);
```

### HTTP — Server

```cs
var server = new HttpServer(port: 8080);
server.Listen();

// Custom index page
server.IndexPageHtml = "<html><body><h1>Welcome!</h1></body></html>";

// Route mapping
server.Map("GET", "/hello", (connection, request, ct) =>
{
    return "Hello, World!";
});

server.Map("POST", "/echo", (connection, request, ct) =>
{
    return new EchoResponse(request.Body);
});

server.RemoveRoute("GET", "/hello");

// Fallback for unmatched routes
server.OnHttpRequest += (connection, ref context, ct) =>
{
    context.Response.Body = "Custom response";
    context.Response.ContentType = "text/plain";
    context.StatusCode = "200 OK";
};
```

### HTTP — Client

```cs
using var client = new HttpClient();

// GET
HttpResponse response = await client.GetAsync("http://localhost:8080/hello");
Console.WriteLine(response.Body);

// POST
byte[] body = Encoding.UTF8.GetBytes("{\"message\":\"hi\"}");
HttpResponse postResp = await client.PostAsync(
    "http://localhost:8080/echo",
    "application/json",
    body);

// Full control
HttpResponse resp = await client.SendAsync(
    "PUT",
    "https://example.com/api/resource",
    new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
    body);

// Streaming
using var fileStream = File.Create("download.bin");
await client.GetAsync("http://example.com/largefile", responseStream: fileStream);
```

### HTTP — Client Options

```cs
var client = new HttpClient();
client.Timeout    = TimeSpan.FromSeconds(60); // Default 30
client.MaxRedirects = 10;                     // Default 5
client.DefaultHeaders["Accept"] = "application/json";
```

### HTTP — Live Streaming with BroadcastServer

`BroadcastServer` turns any `HttpServer` into a live video relay. Point OBS (or any RTMP encoder) at the server and viewers can watch in a browser or VLC — no additional dependencies required.

![OBS streaming to SocketJack HttpServer via BroadcastServer](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/httpStream.PNG)

```cs
using SocketJack.Net;

// Create the HTTP server
var server = new HttpServer(port: 8080);

// Attach BroadcastServer and register the default streaming routes:
//   GET  /stream       — HTML player page (mpegts.js)
//   GET  /stream/data  — raw FLV relay for the player / VLC
//   PUT  /Upload       — OBS Custom Output (HTTP)
//   POST /Upload       — OBS Custom Output (HTTP)
//   RTMP rtmp://host:port/live  — OBS RTMP publish
var broadcast = new BroadcastServer(server);
broadcast.Register();

// Start listening
server.Listen();

// The stream key is auto-generated. In OBS set:
//   Server:     rtmp://localhost:8080/live
//   Stream Key: <broadcast.StreamKey>
Console.WriteLine("Stream Key: " + broadcast.StreamKey);

// Viewers open http://localhost:8080/stream in a browser,
// or play http://localhost:8080/stream/data directly in VLC.

// Optional: poll stats once per second
while (true) {
    var stats = broadcast.UpdateStats();
    if (stats.Active) {
        Console.WriteLine(
            stats.BitrateKbps.ToString("N0") + " kbps | " +
            stats.VideoFrames + " video | " +
            stats.AudioFrames + " audio | " +
            BroadcastServer.FormatBytes(stats.TotalBytes));
    }
    Thread.Sleep(1000);
}
```

### WebSocket — Server & Client

```cs
var server = new WebSocketServer(port: 9000);
server.Listen();

// Optional TLS
server.SslCertificate = new X509Certificate2("cert.pfx", "password");
server.Options.UseSsl = true;
server.Listen();

// Connect a client
var client = new WebSocketClient();
await client.Connect("127.0.0.1", 9000);

// Or with a full URI
await client.ConnectAsync(new Uri("ws://127.0.0.1:9000/path"));
```

### WebSocket — Send, Receive & Broadcast

```cs
client.Send(new CustomMessage("Hello via WebSocket!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});

server.Send(clientConnection, new CustomMessage("Reply"));
server.SendBroadcast(new CustomMessage("Announcement"));
```

### WebSocket — Peer-to-Peer

```cs
var options = new NetworkOptions();
options.UsePeerToPeer = true;

var client = new WebSocketClient(options);
await client.Connect("127.0.0.1", 9000);

client.Send(remotePeer, new CustomMessage("P2P over WebSocket"));
client.SendBroadcast(new CustomMessage("Hello everyone!"));
```

### WebSocket — Events

```cs
// Server
server.ClientConnected  += (e) => Console.WriteLine($"Client connected: {e.Connection.Identity.ID}");
server.ClientDisconnected += (e) => Console.WriteLine($"Client disconnected: {e.Connection.Identity.ID}");
server.OnReceive += (ref e) => Console.WriteLine($"Received: {e.Obj}");

// Client
client.OnConnected    += (e) => Console.WriteLine("Connected!");
client.OnDisconnected += (e) => Console.WriteLine("Disconnected.");
client.PeerConnected    += (sender, peer) => Console.WriteLine($"Peer joined: {peer.ID}");
client.PeerDisconnected += (sender, peer) => Console.WriteLine($"Peer left: {peer.ID}");
```

### WPF — Sharing a Control

> **These examples require the [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) NuGet package, not `SocketJack`.**

```cs
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.WPF;

// Share any FrameworkElement (Canvas, Grid, Border, Window, etc.)
IDisposable shareHandle = myCanvas.Share(client, peer, fps: 10);

// Stop sharing
shareHandle.Dispose();
```

### WPF — Viewing a Shared Control

```cs
using System.Windows.Controls;
using SocketJack.Net;
using SocketJack.WPF;

var viewer = client.ViewShare(sharedImage, sharerPeer);

// Dispose when finished
viewer.Dispose();
```

### WPF — Full Example

**XAML (both instances):**

```xml
<Image x:Name="SharedImage" Stretch="Uniform" />
```

**Sharer (Instance A):**

```cs
Identifier remotePeer = client.Peers.FirstNotMe();
IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
```

**Viewer (Instance B):**

```cs
Identifier remotePeer = client.Peers.FirstNotMe();
var viewer = client.ViewShare(SharedImage, remotePeer);
```

---

## Documentation

- [API Reference](https://github.com/JackOfFates/SocketJack)
- [Examples & Tutorials](https://github.com/JackOfFates/SocketJack/tree/master/Tests/TestControls)

## License

SocketJack is open source and licensed under the [MIT License](LICENSE).

## Contributing

Contributions, bug reports, and feature requests are welcome! See [CONTRIBUTING.md](https://github.com/JackOfFates/SocketJack/blob/master/CONTRIBUTING.md) for details.

---

**SocketJack** — Fast, flexible, and modern networking for .NET.
