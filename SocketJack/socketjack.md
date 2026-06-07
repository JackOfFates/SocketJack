# SocketJack C# Library Summary

## Purpose

SocketJack is a managed .NET networking library packaged as `SocketJack` and targeting `netstandard2.1`. Its core purpose is to provide reusable client/server building blocks for real-time communication systems without forcing each application to hand-roll socket loops, protocol routing, serialization, compression, or transport security.

## Main Components

- `SocketJack.Net` contains the main networking surface, including TCP, UDP, HTTP, WebSocket, forwarding, peer-to-peer, torrent, and connection abstractions.
- `TcpServer`, `TcpClient`, `UdpServer`, and `UdpClient` cover low-level stream and datagram communication.
- `HttpServer` and `HttpClient` add HTTP routing, document responses, dispatch metadata, and lightweight web service hosting.
- `Net/WebSockets` provides WebSocket client and server support for bidirectional real-time channels.
- `Net/P2P` includes peer lists, redirects, ping/metadata helpers, and NAT traversal-oriented coordination.
- `Net/Database` contains lightweight database/table abstractions, snapshots, TDS protocol handling, SQL session/admin helpers, and DbContext generation utilities.
- `Net/AgentBuilder` provides workflow models and an execution engine for graph-like agent/action flows.
- `Serialization`, `Compression`, and `Sandbox` provide supporting infrastructure for structured payloads, GZip/Deflate compression, and in-memory sandbox state.

## Capabilities

SocketJack combines protocol hosting, connection management, JSON serialization, TLS via `SslStream`, compression, bandwidth/throttling-oriented utilities, file/HTML helpers, and service-style endpoints. It also includes higher-level helpers for prompt intellisense, reflection, terminal/git service integration, and workflow execution, which makes it useful beyond a simple socket wrapper.

## Likely Uses

The library is suited for multiplayer/game networking, local tools, remote-control software, IoT-style device communication, embedded web dashboards, peer-to-peer apps, AI/tooling servers, and other real-time systems that need a mix of raw sockets, HTTP, WebSockets, structured data, and optional service orchestration.
