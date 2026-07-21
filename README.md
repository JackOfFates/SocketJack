# SocketJack 2026

![SocketJack Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/SocketJackIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![SocketJack.WPF](https://img.shields.io/nuget/v/SocketJack.WPF.svg?label=SocketJack.WPF)](https://www.nuget.org/packages/SocketJack.WPF)
[![GitHub Packages](https://img.shields.io/badge/GitHub%20Packages-published-24292f?logo=github)](https://github.com/JackOfFates/SocketJack/packages)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

SocketJack 2026 is a batteries-included .NET networking platform for typed object transport, TCP, UDP, WebSockets, HTTP apps, protocol multiplexing, SQL-backed management, peer-to-peer metadata, and remote WPF control.

It is built for projects that need real network behavior without rebuilding wire plumbing from scratch. Framing, segmentation, serialization, compression, routing, protocol detection, TLS, static file hosting, streaming routes, typed callbacks, peer identity, and P2P forwarding are already part of the platform.

| Start here | Link |
|---|---|
| Install the NuGet packages | [#Install](#install) |
| See the 2026 package line | [#Versions](#versions) |
| Explore networking features | [#Networking](#networking) |
| Share or control WPF windows | [#SocketJack.WPF](#socketjackwpf) |
| Learn about JackLLM Workstation | [#JackLLM Workstation](#jackllm-workstation) |
| Try JackLLM Mobile for Android or iOS | [#JackLLM Mobile](#jackllm-mobile-android-and-ios) |

<a id="versions"></a>
## #Versions

SocketJack 2026 is the current platform line. The core packages use year-based versions, while some companion tooling keeps its own package-compatible numbering.

| Surface | Version | Target / format | Notes |
|---|---:|---|---|
| [`SocketJack`](https://www.nuget.org/packages/SocketJack) | `2026.8` | `.NET Standard 2.1` | Core networking, protocol hosting, P2P, SQL/data, streaming, HTTP, and WebSockets. |
| [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) | `2026.4` | `net8.0-windows7.0`, `net10.0-windows7.0` | WPF capture, remote input, and GUI remoting. |
| `JackLLM Workstation` | `2026.0` | Windows app metadata | WPF workstation app in `JackLLM/`. |
| `JackLLM Workstation Linux` | `1:26.0.1` | Debian package version | Linux-compatible Debian version for the 2026 line. The epoch keeps upgrades ordered after earlier `2026.0.x` packages. |
| `SocketJack LlmRuntime VS Extension` | `0.1.4` | Visual Studio 2022 VSIX | Legacy VSSDK extension in `LlmRuntime.VisualStudio/`. |
| `SocketJack LlmRuntime VS 2026 Extension` | `0.2.30` | Visual Studio 2026 VSIX | Modern VisualStudio.Extensibility extension in `LlmRuntime.VisualStudio2026/`. |
| `JackLLM Mobile` | `1.0` | Android + iOS app | Shared MAUI client in `SocketJack/JackLLM.Android/` for workstation chat, PC Access, and session management. |
| `SocketJack.Unity` | `1.1.0.1` | `.NET Standard 2.1` | Legacy Unity-facing package surface. |
| `SocketJack.WebSocketServer` | `1.1.0.1` | `.NET Standard 2.1` | Legacy WebSocket server package surface. |

<a id="install"></a>
<details open>
<summary><strong>#Install</strong> - package commands and first choice</summary>

Install the core networking package:

```powershell
dotnet add package SocketJack
```

Or with Package Manager:

```powershell
Install-Package SocketJack
```

Install the WPF companion package when you want live WPF capture, remote input, or peer-controlled desktop windows:

```powershell
dotnet add package SocketJack.WPF
```

Use `SocketJack` for servers, clients, protocol hosting, HTTP/WebSocket apps, SQL/data, streaming, and peer routing. Add `SocketJack.WPF` when the network should see or control a WPF interface.

</details>

<a id="networking"></a>
<details>
<summary><strong>#Networking</strong> - TCP, UDP, WebSockets, standard protocols, and the SocketJack protocol</summary>

SocketJack gives one consistent API family for local-network tools, multiplayer games, admin panels, browser apps, agent hosts, stream relays, database management, and peer-routed software.

### #Tcp

`TcpServer` and `TcpClient` provide typed object messaging over TCP. You register callbacks by message type and let SocketJack handle framing, segmentation, connection identity, serialization, compression, and broadcast routing.

```csharp
using SocketJack.Net;

var server = new TcpServer(port: 12345);
server.RegisterCallback<ChatMessage>(args =>
{
    Console.WriteLine(args.Object.Text);
    args.Connection.Send(new ChatMessage("received"));
});
server.Listen();

var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);
client.Send(new ChatMessage("hello"));

public sealed record ChatMessage(string Text);
```

### #Udp

`UdpServer` and `UdpClient` use the same typed callback style for datagram workflows. This is useful for discovery, presence, lightweight state, games, telemetry, local-network devices, and low-overhead service coordination.

### #WebSockets

SocketJack includes browser-compatible WebSocket clients and servers. WebSocket connections can use the same serialization, compression, callbacks, peer metadata, and P2P routing as native SocketJack connections.

```csharp
using SocketJack.Net;

var server = new WebSocketServer(port: 9000);
server.RegisterCallback<ChatMessage>(args =>
{
    server.SendBroadcast(new ChatMessage(args.Object.Text));
});
server.Listen();
```

### #Standard Protocols Built In

SocketJack can host normal internet and application protocols directly:

| Protocol / surface | What it is used for |
|---|---|
| HTTP | Routes, APIs, typed request bodies, static files, directories, redirects, uploads, and stream responses. |
| WebSockets | Browser clients, dashboards, chat, realtime admin, and app clients. |
| RTMP streaming | Live media ingest and relay workflows. |
| TDS / SQL | SQL-style clients and database management surfaces. |
| FTP / SFTP helpers | File transport, session artifacts, and remote file movement. |
| TLS / auth helpers | `SslStream`, certificates, Basic auth, bearer tokens, local-host checks, and allow/deny rules. |

`MutableTcpServer` can detect and route multiple protocols on one listening port, so a single server can host HTTP, WebSocket, SocketJack protocol traffic, RTMP, SQL/TDS, and custom handlers.

### #SocketJack Protocol Built In

The native SocketJack protocol is the object-transport layer:

- Send CLR objects directly with `client.Send(new MyMessage(...))`.
- Register type-safe callbacks with `RegisterCallback<T>()`.
- Broadcast to connected clients or route to a specific peer.
- Use peer redirects for P2P-style delivery through a coordinating server.
- Carry identity and metadata with each connection.
- Let SocketJack handle wrappers, type information, chunking, compression, and reassembly.

</details>

<a id="serialization"></a>
<details>
<summary><strong>#Serialization</strong> - built-in System.Text.Json with type-safe callbacks</summary>

SocketJack uses `System.Text.Json` by default and keeps application code strongly typed.

| Feature | Benefit |
|---|---|
| Built-in JSON serializer | No serializer setup required for normal object messaging. |
| Type-safe callbacks | Receive `T` directly instead of manually decoding bytes. |
| Wrapper metadata | Type information and peer-routing metadata travel with each message. |
| Pluggable surface | Advanced users can replace the serializer through `NetworkOptions.Serializer`. |

```csharp
server.RegisterCallback<OrderSubmitted>(args =>
{
    OrderSubmitted order = args.Object;
    Console.WriteLine(order.OrderId);
});

client.Send(new OrderSubmitted("SO-1001", total: 42.50m));

public sealed record OrderSubmitted(string OrderId, decimal Total);
```

</details>

<a id="database--mvc"></a>
<details>
<summary><strong>#Database / MVC</strong> - SQL, web admin, HTTP routing, and peer metadata</summary>

SocketJack includes enough web, data, and routing infrastructure to build admin panels, dashboards, internal tools, workstation state, and agent-facing management surfaces without adding a separate web stack first.

### #SQL Database

- Built-in data-server primitives for application records, snapshots, generated context, and cached query paths.
- SQL/TDS protocol handling for SQL-style access paths.
- Good fit for local-first tools, embedded admin systems, workstation state, sessions, permissions, and hosted metadata.

### #Web SQL Admin / Management

- HTTP-hosted SQL admin and management surfaces can run directly on a SocketJack server.
- Route mapping supports JSON APIs, typed request bodies, static pages, uploads, and stream responses.
- The same host can serve an admin UI, a browser app, a database API, and native SocketJack peers.

```csharp
using SocketJack.Net;

var server = new HttpServer(port: 8080);

server.Map("GET", "/health", (connection, request, ct) => new { status = "ok" });
server.MapDirectory("/static", @"C:\wwwroot");

server.Listen();
```

### #P2P Metadata Sharing

SocketJack peers can share metadata with the network. It works like network cookies for clients and servers:

- Announce model, hardware, role, room, user, app, price, uptime, or service state.
- Update peer metadata while connections are alive.
- Route messages based on peer identity and metadata.
- Build server browsers, room lists, workstation directories, or capability registries.

</details>

<a id="socketjackwpf"></a>
<details>
<summary><strong>#SocketJack.WPF</strong> - share, control, and manipulate remote WPF windows</summary>

`SocketJack.WPF` makes WPF windows and controls remotely shareable over SocketJack.

| Capability | What it means |
|---|---|
| Live control sharing | Share any WPF `FrameworkElement` as a live image stream. |
| Remote viewing | View remote WPF content from another WPF client or a browser-backed admin surface. |
| Remote input | Send mouse, wheel, text, and keyboard input back to the shared control. |
| P2P discovery | Use peer identity and metadata so remote windows can be found, shared, controlled, and manipulated through peer flows. |
| JackLLM admin | Powers browser-based remote administration for JackLLM Workstation. |

```csharp
using SocketJack.WPF;

IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
IDisposable viewerHandle = client.ViewShare(SharedImage, sharerPeer);
```

</details>

<a id="jackllm-workstation"></a>
<details>
<summary><strong>#JackLLM Workstation</strong> - AI workstation, generation node, and Visual Studio agent bridge</summary>

JackLLM Workstation is the AI workstation built on SocketJack. It is found at [SocketJack.com](https://socketjack.com/) and is designed around network nodes run by real people, not just datacenter-hosted model providers.

With JackLLM Workstation you can:

- Use your PC as an LLM node, generation node, or custom agent host.
- Publish model, hardware, tool, pricing, uptime, and availability metadata to the SocketJack network.
- Use Visual Studio with a code agent through OpenAI-compatible and Copilot-compatible endpoints.
- Run local or remote models, including large workstation-hosted models such as a Claude Opus 4.7 Distilled 35B-parameter compatible model when a node exposes one.
- Use SocketJack.com to generate images and videos, ask AI questions, research the web, or work on a Visual Studio project through a Copilot MCP server tunnel to SocketJack.com agents.
- Manage sessions, permissions, files, tools, SQL admin, payments, diagnostics, and remote WPF control from the workstation/web console.

### Dream Mode

Dream Mode is JackLLM's resource-aware background reflection system. It processes changed owner conversations only when CPU, RAM, GPU, VRAM, disk, and foreground model activity are below the configured thresholds, then writes grounded memory proposals to an owner-specific Dream journal.

- It is disabled by default and can be managed from **Web Chat > View > Dreaming**, **Workstation > Server Management > Dreaming**, or the moon button in JackLLM Mobile.
- Conservative, Balanced, and Aggressive presets configure start/pause hysteresis; custom settings control recurrence, duration, output/source token budgets, and sessions per pass.
- **Dream Now** requests one pass even when automation is disabled. Pause affects scheduled work; Cancel Run stops only the active pass.
- Every tool requires its normal permission plus a separate Dream permission. Terminal also requires permanent Terminal Trust.
- Only grounded, explicit, non-sensitive, non-conflicting facts at 90% or higher confidence auto-save. Other facts and all conflicts remain reviewable.
- Global settings are inherited until an owner override is saved. Journals, processing watermarks, and memories remain owner-specific.
- Paired devices manage their linked owner unless a local Workstation administrator explicitly grants that device **Dream Admin**.

Pending journal reviews are never automatically pruned. **Clear Resolved** removes resolved history while preserving pending candidates and conflicts.

| Project | Version | Purpose |
|---|---:|---|
| `JackLLM/` | `2026.0` | Windows WPF JackLLM Workstation app. |
| `JackLLM.Workstation/` | `2026.0` | Cross-platform native/service-style host. |
| Linux `.deb` | `1:26.0.1` | Debian-compatible Linux package version for the 2026 release line. |
| `SocketJack.LlmCore/` | repo library | Shared AI proxy, tools, sessions, payments, and workstation logic. |
| `SocketJack.WorkstationMcp/` | repo bridge | MCP bridge surface for workstation and development tooling. |

</details>

<a id="jackllm-mobile-android"></a>
<details>
<summary><strong>#JackLLM Mobile</strong> - Android and iOS client for workstation chat and sessions</summary>

JackLLM Mobile is the shared Android and iOS companion app for JackLLM Workstation. It connects to a workstation endpoint, sends prompts from a phone or tablet, shows assistant responses with model and compute telemetry, and lets you reopen workstation conversations from a mobile session list. Both platforms compile the same shared Mobile pages, models, and services.

![JackLLM Mobile chat](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-chat.png)

![JackLLM Mobile sessions](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-sessions.png)

Project README: [SocketJack/JackLLM.Android/README.md](SocketJack/JackLLM.Android/README.md)

</details>

<a id="repository-guide"></a>
<details>
<summary><strong>#Repository Guide</strong> - where the major pieces live</summary>

| Path | Purpose |
|---|---|
| `SocketJack/` | Core `SocketJack` package, transports, HTTP/WebSocket stack, mutable protocol server, SQL/data, streaming, FTP/SFTP, and resources. |
| `SocketJack.Windows/` | `SocketJack.WPF` package and WPF capture/input integration. |
| `SocketJack.Tests/` | Core networking and protocol tests. |
| `SocketJack.WebSocketServer/` | WebSocket server package/project surface. |
| `SocketJack.Unity/` | Unity-compatible package surface. |
| `SocketJack-MagicMasterList/` | Public server-list and SocketJack.com-facing host project. |
| `JackLLM/` | Windows WPF JackLLM Workstation app. |
| `JackLLM.Workstation/` | Linux/service-style JackLLM Workstation host. |
| `SocketJack/JackLLM.Android/` | Shared Android and iOS JackLLM Mobile app. |
| `LlmRuntime.VisualStudio/` | Visual Studio 2022 extension. |
| `LlmRuntime.VisualStudio2026/` | Visual Studio 2026 extension. |
| `LlmRuntime/` | LLM runtime project and documentation. |
| `examples.md` | Longer runnable examples across SocketJack transports and utilities. |

</details>

<a id="documentation"></a>
<details>
<summary><strong>#Documentation</strong> - examples, packages, and companion docs</summary>

- [Examples](examples.md)
- [SocketJack package](https://www.nuget.org/packages/SocketJack)
- [SocketJack.WPF package](https://www.nuget.org/packages/SocketJack.WPF)
- [JackLLM Workstation README](JackLLM/README.md)
- [JackLLM Mobile README](SocketJack/JackLLM.Android/README.md)
- [LlmRuntime README](LlmRuntime/README.md)
- [GitHub repository](https://github.com/JackOfFates/SocketJack)

</details>

## License

SocketJack is open source and licensed under the [MIT License](LICENSE).

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>11,961,735 lines / 916 files</code></summary>

<br>

<strong>Scope:</strong> <code>.</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| Text | 282 | 11,578,223 |
| C# | 379 | 239,378 |
| HTML | 15 | 76,629 |
| XML | 11 | 26,824 |
| XAML | 32 | 13,313 |
| Markdown | 77 | 7,766 |
| Visual Basic | 18 | 5,364 |
| MSBuild/XML | 33 | 3,336 |
| PowerShell | 16 | 3,277 |
| JSON | 33 | 2,271 |
| C++ | 2 | 1,380 |
| Shell | 3 | 1,104 |
| Batch | 2 | 740 |
| EditorConfig | 5 | 577 |
| Solution | 1 | 547 |
| JavaScript | 2 | 441 |
| TypeScript | 2 | 430 |
| YAML | 3 | 135 |
| **Total** | **916** | **11,961,735** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
