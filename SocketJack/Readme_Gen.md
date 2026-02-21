# SocketJack

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A high-performance .NET networking library for building client-server and peer-to-peer applications. SocketJack wraps raw `System.Net.Sockets` TCP and UDP, `SslStream` TLS 1.2 encryption, and `System.Text.Json` serialization behind a unified, transport-agnostic API — so you can focus on your application logic instead of low-level networking.

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

**Target frameworks:** .NET Standard 2.1 · .NET 6 · .NET 8 · .NET 9 · .NET 10

---

## Use Cases

SocketJack is well-suited for a broad range of networked applications:

- **Real-time multiplayer games** — low-latency communication with dynamic peer discovery.
- **Distributed chat** — P2P messaging with metadata-driven room discovery.
- **IoT device networks** — efficient, secure communication across flexible topologies.
- **Remote control & automation** — event-driven command/control of remote systems.
- **Custom protocols** — build domain-specific protocols on top of TCP, UDP, or WebSocket with full control over serialization and peer management.

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

## WPFController — Live Control Sharing

The `SocketJack.WPFController` library lets you share any WPF `FrameworkElement` over a `TcpClient` connection. The sharer captures JPEG frames of the element at a configurable frame rate and streams them to a remote peer. The viewer displays those frames in a WPF `Image` control and automatically forwards mouse input back, so the remote user can interact with the shared element as if it were local.

### Sharing an Element

Call the `Share` extension method on any `FrameworkElement`. It returns an `IDisposable` handle you can dispose to stop sharing.

```cs
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.WPFController;

// Both 'client' and 'peer' must already be connected and identified.
// 'client' is your local TcpClient.
// 'peer' is the Identifier of the remote peer who will view the element.

// Share any FrameworkElement — a Canvas, Grid, Border, or even the entire Window.
IDisposable shareHandle = myCanvas.Share(client, peer, fps: 10);

// To stop sharing, dispose the handle.
shareHandle.Dispose();
```

Behind the scenes, `Share` captures the element as a JPEG bitmap on the UI thread each frame, sends it inside a `ControlShareFrame` message via P2P, and automatically replays any mouse input the viewer sends back onto the original element.

### Receiving a Shared Element

Create a `ControlShareViewer` and give it a `TcpClient` and a WPF `Image` control. Incoming frames are decoded and displayed automatically, and every mouse click or move on the `Image` is forwarded back to the sharer.

```cs
using System.Windows.Controls;
using SocketJack.Net;
using SocketJack.WPFController;

// 'client' is your local TcpClient (already connected).
// 'sharedImage' is an Image control defined in your XAML.
var viewer = new ControlShareViewer(client, sharedImage);

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
Identifier remotePeer = client.Peers.First(p => p.ID != client.RemoteIdentity.ID);

// Share the game canvas at 10 frames per second.
IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
```

**Viewer (Instance B):**

```cs
// After connecting to the same server:
var viewer = new ControlShareViewer(client, SharedImage);

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

**SocketJack** — Fast, flexible, and modern networking for .NET.
