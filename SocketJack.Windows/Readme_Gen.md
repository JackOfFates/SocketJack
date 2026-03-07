# SocketJack.WPF

![SocketJack WPF Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack.Windows/SocketJackWpfIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.WPF.svg)](https://www.nuget.org/packages/SocketJack.WPF)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

WPF extension for [SocketJack](https://www.nuget.org/packages/SocketJack) that adds live control sharing over the network. Share any `FrameworkElement` — a `Canvas`, `Grid`, `Border`, or entire `Window` — as a JPEG stream to a remote peer, and let the viewer interact with it as if it were local. Mouse input is automatically forwarded back and replayed on the original element.

Built on top of the full SocketJack networking stack: `System.Text.Json` serialization, TCP/UDP/WebSocket transports, peer-to-peer relay, compression, and TLS encryption.

**Target frameworks:** .NET 8 · .NET 9 · .NET 10

---

## How It Works

1. **Sharer** calls `element.Share(client, peer, fps)` — captures the element as a JPEG bitmap each frame and streams `ControlShareFrame` messages via P2P.
2. **Viewer** calls `client.ViewShare(image, peer)` — decodes incoming frames into a WPF `Image` and forwards mouse events back to the sharer.
3. Mouse clicks and moves on the viewer are replayed on the original element as real input events.

---

## Getting Started

```
Install-Package SocketJack.WPF
```

---

## Examples

### Sharing a Control

```cs
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.WPF;

// Both 'client' and 'peer' must already be connected and identified.
IDisposable shareHandle = myCanvas.Share(client, peer, fps: 10);

// Stop sharing
shareHandle.Dispose();
```

### Viewing a Shared Control

```cs
using System.Windows.Controls;
using SocketJack.Net;
using SocketJack.WPF;

var viewer = client.ViewShare(sharedImage, sharerPeer);

// Dispose when finished
viewer.Dispose();
```

### Full Example

A typical setup uses two application instances connected through a `TcpServer`. One shares a control, the other views it.

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

## HTTP Live Streaming with BroadcastServer

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

---

## SocketJack Core Features

This package includes the full [SocketJack](https://www.nuget.org/packages/SocketJack) library. All core networking features are available:

| Category | Highlights |
|---|---|
| **Transport** | Unified `TcpClient` / `TcpServer`, `UdpClient` / `UdpServer`, and WebSocket API. |
| **Protocol Multiplexing** | `MutableTcpServer` auto-detects HTTP, SocketJack, and RTMP on a single port. |
| **HTTP** | Route mapping (`Map`, `Map<T>`, `MapStream`, `MapUploadStream`), static file serving via `MapDirectory`, `.htaccess` security, RTMP ingest. |
| **Serialization** | `System.Text.Json` with pluggable `ISerializer`, type whitelist/blacklist. |
| **Peer-to-Peer** | Automatic discovery, relay-based NAT traversal, metadata propagation. |
| **Compression** | `GZipStream` / `DeflateStream` with configurable `CompressionLevel`. |
| **Performance** | Async I/O, automatic segmentation, bandwidth throttling. |
| **Security** | `SslStream` TLS 1.2, `X509Certificate` authentication, `.htaccess` access control. |

### TCP — Quick Start

```cs
var server = new TcpServer(port: 12345);
server.Listen();

var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);

client.Send(new CustomMessage("Hello!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
    args.Connection.Send(new CustomMessage("10-4"));
});
```

### UDP — Quick Start

```cs
var server = new UdpServer(port: 12345);
server.Listen();

var client = new UdpClient();
await client.Connect("127.0.0.1", 12345);

client.Send(new CustomMessage("Hello via UDP!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});
```

### Default Options

Must be set before creating any Client or Server instance.

```cs
NetworkOptions.DefaultOptions.UsePeerToPeer = true;
```

### MutableTcpServer — Multi-Protocol Quick Start

`MutableTcpServer` auto-detects HTTP, SocketJack, and RTMP per-connection on a single port:

```cs
var server = new MutableTcpServer(port: 9000);

// HTTP routes via the Http property
server.Http.Map("GET", "/api/status", (connection, request, ct) =>
{
    return "{ \"status\": \"ok\" }";
});

// Serve static files
server.Http.MapDirectory("/www", @"C:\wwwroot");

// SocketJack callbacks work as usual
server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});

server.Listen();
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

**SocketJack.WPF** — Live control sharing for WPF, powered by SocketJack.
