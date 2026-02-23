# SocketJack

![SocketJack Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack.Windows/SocketJackWpfIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack.WPF)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A high-performance .NET networking library for building client-server and peer-to-peer applications. SocketJack wraps raw `System.Net.Sockets` TCP and UDP, `SslStream` TLS 1.2 encryption, and `System.Text.Json` serialization behind a unified, transport-agnostic API -- so you can focus on your application logic instead of low-level networking.

---

## Features

| Category | Highlights |
|---|---|
| **Transport** | Built on `System.Net.Sockets.Socket` and `NetworkStream`. Unified `TcpClient` / `TcpServer`, `UdpClient` / `UdpServer`, and WebSocket API with consistent connection lifecycle events. |
| **Peer-to-Peer** | Automatic peer discovery, host/client role management, relay-based NAT traversal, and metadata propagation via the `Identifier` class. |
| **Serialization** | Default `System.Text.Json` serializer with pluggable `ISerializer` interface, custom `JsonConverter` support (e.g., `Bitmap`, `byte[]`, `Type`), and type whitelist/blacklist for secure deserialization. |
| **Compression** | Pluggable `ICompression` interface with built-in `GZipStream` and `DeflateStream` implementations, configurable `CompressionLevel`. |
| **Performance** | Large configurable buffers (default 100 MB), fully async I/O, automatic message segmentation for payloads exceeding MTU, outbound chunking with configurable flush interval, and upload/download bandwidth throttling (Mbps). |
| **Security** | `SslStream` with TLS 1.2, `X509Certificate` authentication, and fine-grained control over allowed message types and connection policies. |
| **Extensibility** | Rich event system for connection, disconnection, peer updates, and data receipt. Attach arbitrary metadata to any peer or connection for dynamic routing and discovery. |

**Target frameworks:** .NET Standard 2.1 * .NET 6 * .NET 8 * .NET 9 * .NET 10

---

## Use Cases

SocketJack is well-suited for a broad range of networked applications:

- **Real-time multiplayer games** -- low-latency communication with dynamic peer discovery.
- **Distributed chat** -- P2P messaging with metadata-driven room discovery.
- **IoT device networks** -- efficient, secure communication across flexible topologies.
- **Remote control & automation** -- event-driven command/control of remote systems.
- **Custom protocols** -- build domain-specific protocols on top of TCP, UDP, or WebSocket with full control over serialization and peer management.

---

## Getting Started

1. **Install via NuGet:**

```cs
Install-Package SocketJack
```

2. **Create a Server:**

```cs
var server = new TcpServer(port: 12345);
server.StartListening();
```

3. **Connect a Client:**

```cs
var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);
```

4. **Sending and Receiving:**

```cs
client.Send(new customMessage("Hello!"));
server.OnReceived += (sender, args) => {
    var message = args.Object as string;
    // Handle message
};
```

5. **Setting up callbacks:**

```cs
// Register a callback for a custom message class
server.RegisterCallback<CustomMessage>((customMessage) =>
{
    Console.WriteLine($"Received: customMessage ({customMessage.Message})");

    // Echo back to the client
    args.From.Send(new customEchoObject("10-4"));
});
```

6. **Enabling options by default:**

*MUST be called before the instantiation of a Client or Server.*

```cs
TcpOptions.Default.UsePeerToPeer = true;
```

7. **Attach Metadata:**

*This will ONLY work on the server authority.*
*Use your own authentication to validate the clients.*

```cs
client.Identifier.SetMetaData("Room", "Lobby1");
```

---

## UDP Transport

SocketJack includes `UdpClient` and `UdpServer` classes that mirror the TCP API but use connectionless UDP datagrams. The same `NetworkOptions`, serialization, compression, peer-to-peer, and callback systems work across both transports.

### Create a UDP Server

```cs
var server = new UdpServer(port: 12345);
server.Listen();
```

### Connect a UDP Client

```cs
var client = new UdpClient();
await client.Connect("127.0.0.1", 12345);
```

### Sending and Receiving

```cs
// Client sends an object to the server
client.Send(new CustomMessage("Hello via UDP!"));

// Server receives objects with the same callback system as TCP
server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});
```

### Peer-to-Peer over UDP

```cs
// Send to a specific peer (relayed through the server)
client.Send(remotePeer, new CustomMessage("P2P over UDP"));

// Broadcast to all peers
client.SendPeerBroadcast(new CustomMessage("Hello everyone!"));
```

### Server Broadcasting

```cs
// Send to all connected UDP clients
server.SendBroadcast(new CustomMessage("Server announcement"));

// Send to a specific client by Identifier
server.Send(clientIdentifier, new CustomMessage("Direct message"));
```

### UDP-Specific Options

UDP settings live in `NetworkOptions` alongside the TCP options:

```cs
var options = new NetworkOptions();

// Maximum datagram payload size (default 65,507 bytes).
// Lower to ~1,400 for safe MTU.
options.MaxDatagramSize = 1400;

// Seconds before the server considers a silent client disconnected (default 30).
options.ClientTimeoutSeconds = 60;

// Receive buffer size in bytes (default 65,535).
options.UdpReceiveBufferSize = 131072;

// Allow the socket to send broadcast datagrams (default false).
options.EnableBroadcast = true;

// How often the server checks for timed-out clients (default 5,000 ms).
options.ClientTimeoutCheckIntervalMs = 10000;

var server = new UdpServer(options, port: 12345);
var client = new UdpClient(options);
```

### Key Differences from TCP

| | TCP | UDP |
|---|---|---|
| **Connection** | Stream-oriented, persistent | Connectionless datagrams |
| **Reliability** | Guaranteed delivery & ordering | No built-in delivery guarantee |
| **Max payload** | Unlimited (automatic segmentation) | Limited by `MaxDatagramSize` (default 65,507 bytes) |
| **TLS** | Supported via `SslStream` | Not supported |
| **Server method** | `StartListening()` | `Listen()` |
| **Client method** | `Connect()` returns on TCP handshake | `Connect()` binds locally and sets remote endpoint |

---

## HTTP Server & Client

SocketJack provides `HttpServer` and `HttpClient` classes that layer a familiar HTTP API on top of the existing TCP transport. Both classes inherit from the TCP base, so all `NetworkOptions`, serialization, compression, and callback features carry over automatically.

### Create an HTTP Server

`HttpServer` extends `TcpServer`. Pass a port (and optional name) to the constructor, then call `StartListening()`.

```cs
var server = new HttpServer(port: 8080);
server.StartListening();
```

By default a GET to `/` returns a built-in HTML page. You can replace it:

```cs
server.IndexPageHtml = "<html><body><h1>Welcome!</h1></body></html>";
```

### Route Mapping

Register handlers for specific HTTP methods and paths with `Map`. The handler receives the `NetworkConnection`, the parsed `HttpRequest`, and a `CancellationToken`, and returns a response body object.

```cs
// Return a plain string
server.Map("GET", "/hello", (connection, request, ct) =>
{
    return "Hello, World!";
});

// Return a serialized object (sent as application/json)
server.Map("POST", "/echo", (connection, request, ct) =>
{
    return new EchoResponse(request.Body);
});

// Remove a route
server.RemoveRoute("GET", "/hello");
```

For requests that don't match any route, subscribe to the `OnHttpRequest` event and set the response on the `HttpContext` directly:

```cs
server.OnHttpRequest += (connection, ref context, ct) =>
{
    context.Response.Body = "Custom response";
    context.Response.ContentType = "text/plain";
    context.StatusCode = "200 OK";
};
```

### Typed Callbacks on the Server

If the request body contains a SocketJack-serialized object, you can register strongly-typed callbacks just like with `TcpServer`:

```cs
server.RegisterCallback<MyPayload>((args) =>
{
    Console.WriteLine($"Received payload: {args.Object}");
});
```

### HTTP Client

`HttpClient` extends `TcpClient` and provides standard `GetAsync`, `PostAsync`, and `SendAsync` methods. It handles Content-Length, chunked transfer-encoding, HTTPS/TLS, and automatic redirects.

```cs
using var client = new HttpClient();

// Simple GET
HttpResponse response = await client.GetAsync("http://localhost:8080/hello");
Console.WriteLine(response.Body);

// POST with a body
byte[] body = Encoding.UTF8.GetBytes("{\"message\":\"hi\"}");
HttpResponse postResp = await client.PostAsync(
    "http://localhost:8080/echo",
    "application/json",
    body);

// Full control: method, headers, body, streaming
HttpResponse resp = await client.SendAsync(
    "PUT",
    "https://example.com/api/resource",
    new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
    body);
```

### Client Options

```cs
var client = new HttpClient();

// Request timeout (default 30 seconds)
client.Timeout = TimeSpan.FromSeconds(60);

// Maximum redirect hops (default 5)
client.MaxRedirects = 10;

// Default headers sent with every request
client.DefaultHeaders["Accept"] = "application/json";
```

### Streaming Responses

Pass a `Stream` or an `onChunk` callback to stream large responses without buffering the entire body in memory:

```cs
using var fileStream = File.Create("download.bin");
await client.GetAsync("http://example.com/largefile", responseStream: fileStream);
```

### Typed Callbacks on the Client

When the server returns a SocketJack-serialized object, the client can dispatch it to typed callbacks automatically:

```cs
client.RegisterCallback<EchoResponse>((args) =>
{
    Console.WriteLine($"Server echoed: {args.Object}");
});
```

### Key HTTP Classes

| Class | Description |
|---|---|
| `HttpServer` | Extends `TcpServer`. Parses incoming HTTP requests, resolves routes, and writes HTTP responses. |
| `HttpClient` | Extends `TcpClient`. Sends HTTP/HTTPS requests with redirect and chunked-transfer support. |
| `HttpContext` | Carries the `HttpRequest`, `HttpResponse`, status code, and content type for a single request cycle. |
| `HttpRequest` | Parsed request with `Method`, `Path`, `Headers`, `Body`, and `BodyBytes`. |
| `HttpResponse` | Response with `StatusCodeNumber`, `Headers`, `Body`/`BodyBytes`, and `ContentType`. Serializes to wire-ready bytes. |

---

## WebSocket Transport

SocketJack includes `WebSocketClient` and `WebSocketServer` classes that implement the WebSocket protocol (RFC 6455) while sharing the same serialization, compression, peer-to-peer, and callback systems as the TCP and UDP transports.

### Create a WebSocket Server

```cs
var server = new WebSocketServer(port: 9000);
server.Listen();
```

The server performs the HTTP upgrade handshake automatically. Optionally enable TLS by setting an `X509Certificate`:

```cs
server.SslCertificate = new X509Certificate2("cert.pfx", "password");
server.Options.UseSsl = true;
server.Listen();
```

### Connect a WebSocket Client

```cs
var client = new WebSocketClient();
await client.Connect("127.0.0.1", 9000);
```

Or connect with a full URI:

```cs
await client.ConnectAsync(new Uri("ws://127.0.0.1:9000/path"));
```

### Sending and Receiving

The same `Send`, `RegisterCallback`, and event patterns work identically to TCP:

```cs
// Client sends an object
client.Send(new CustomMessage("Hello via WebSocket!"));

// Server registers a typed callback
server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});

// Server sends to a specific client
server.Send(clientConnection, new CustomMessage("Reply"));

// Server broadcasts to all clients
server.SendBroadcast(new CustomMessage("Announcement"));
```

### Peer-to-Peer over WebSocket

Enable P2P and use the same peer API as TCP/UDP:

```cs
var options = new NetworkOptions();
options.UsePeerToPeer = true;

var client = new WebSocketClient(options);
await client.Connect("127.0.0.1", 9000);

// Send to a specific peer (relayed through the server)
client.Send(remotePeer, new CustomMessage("P2P over WebSocket"));

// Broadcast to all peers
client.SendBroadcast(new CustomMessage("Hello everyone!"));
```

### Events

`WebSocketServer` and `WebSocketClient` expose the same event system as the TCP classes:

```cs
// Server events
server.ClientConnected += (e) => Console.WriteLine($"Client connected: {e.Connection.Identity.ID}");
server.ClientDisconnected += (e) => Console.WriteLine($"Client disconnected: {e.Connection.Identity.ID}");
server.OnReceive += (ref e) => Console.WriteLine($"Received: {e.Obj}");

// Client events
client.OnConnected += (e) => Console.WriteLine("Connected!");
client.OnDisconnected += (e) => Console.WriteLine("Disconnected.");
client.PeerConnected += (sender, peer) => Console.WriteLine($"Peer joined: {peer.ID}");
client.PeerDisconnected += (sender, peer) => Console.WriteLine($"Peer left: {peer.ID}");
```

### Browser Client Support

`WebSocketServer` can automatically generate JavaScript class constructors for all whitelisted types and send them to browser-based WebSocket clients. This allows browser clients to construct and send SocketJack-compatible objects without manual schema definition.

### Key Differences from TcpClient

| | TCP | WebSocket |
|---|---|---|
| **Protocol** | Raw TCP stream | WebSocket frames (RFC 6455) |
| **Handshake** | TCP three-way handshake | HTTP Upgrade + WebSocket handshake |
| **Browser support** | Not natively supported | Full browser `WebSocket` API compatibility |
| **Server method** | `StartListening()` | `Listen()` |
| **Client method** | `Connect(host, port)` | `Connect(host, port)` or `ConnectAsync(uri)` |
| **TLS** | `SslStream` with `X509Certificate` | `SslStream` with `X509Certificate` |
| **Segmentation** | Automatic for large payloads | Automatic for payloads > 8 KB |

---

## WPF Network Controller -- Live Control Sharing

The `SocketJack.WPF` library lets you share any WPF `FrameworkElement` over a `TcpClient` connection. The sharer captures JPEG frames of the element at a configurable frame rate and streams them to a remote peer. The viewer displays those frames in a WPF `Image` control and automatically forwards mouse input back, so the remote user can interact with the shared element as if it were local.


### Sharing an Element

Call the `Share` extension method on any `FrameworkElement`. It returns an `IDisposable` handle you can dispose to stop sharing.

```cs
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.WPF;

// Both 'client' and 'peer' must already be connected and identified.
// 'client' is your local TcpClient.
// 'peer' is the Identifier of the remote peer who will view the element.

// Share any FrameworkElement -- a Canvas, Grid, Border, or even the entire Window.
IDisposable shareHandle = myCanvas.Share(client, peer, fps: 10);

// To stop sharing, dispose the handle.
shareHandle.Dispose();
```

Behind the scenes, `Share` captures the element as a JPEG bitmap on the UI thread each frame, sends it inside a `ControlShareFrame` message via P2P, and automatically replays any mouse input the viewer sends back onto the original element.

### Receiving a Shared Element

Call the `ViewShare` extension method on a `TcpClient`, passing the `Image` control and the peer `Identifier` of the sharer. Incoming frames are decoded and displayed automatically, and every mouse click or move on the `Image` is forwarded back to the sharer.

```cs
using System.Windows.Controls;
using SocketJack.Net;
using SocketJack.WPF;

// 'client' is your local TcpClient (already connected).
// 'sharedImage' is an Image control defined in your XAML.
// 'sharerPeer' is the Identifier of the peer sharing the element.
var viewer = client.ViewShare(sharedImage, sharerPeer);

// The Image now shows live frames from the remote element.
// Mouse clicks and moves on the Image are sent back to the sharer,
// where they are replayed on the original FrameworkElement.

// When finished, dispose the viewer to unhook all events.
viewer.Dispose();
```

### Full Example

A typical setup uses two application instances connected through a `TcpServer`. One instance shares a control and the other views it.

**XAML (both instances):**

```xml
<Image x:Name="SharedImage" Stretch="Uniform" />
```

**Sharer (Instance A):**

```cs
// After both clients have connected and identified each other:
Identifier remotePeer = client.Peers.FirstNotMe();

// Share the game canvas at 10 frames per second.
IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
```

**Viewer (Instance B):**

```cs
// After connecting to the same server:
Identifier remotePeer = client.Peers.FirstNotMe();
var viewer = client.ViewShare(SharedImage, remotePeer);

// SharedImage now mirrors GameCanvas from Instance A.
// Clicking SharedImage sends the click back to Instance A,
// where it is raised on GameCanvas as a real input event.
```

---

## Documentation

- [API Reference](https://github.com/JackOfFates/SocketJack)
- [Examples & Tutorials](https://github.com/JackOfFates/SocketJack/tree/master/Tests/TestControls)

---

## License

SocketJack is open source and licensed under the MIT License.

---

## Contributing

Contributions, bug reports, and feature requests are welcome!
See [CONTRIBUTING.md](https://github.com/JackOfFates/SocketJack/blob/master/CONTRIBUTING.md) for details.

---

**SocketJack** -- Fast, flexible, and modern networking for .NET.
