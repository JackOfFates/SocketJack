
# SocketJack

**SocketJack** is a high-performance, flexible networking library for .NET, designed to simplify the creation of robust client-server and peer-to-peer (P2P) applications. It provides a modern, extensible API for TCP and WebSocket communication, advanced serialization, and seamless P2P networking with metadata-driven peer discovery.

---

## Features

- **Unified TCP & WebSocket Support**  
  Easily create clients and servers using TCP or WebSockets with a consistent API.

- **Peer-to-Peer (P2P) Networking**  
  Built-in P2P support allows direct peer connections, peer discovery, and metadata sharing.  
  - Peers are identified, discovered, and described using The Identifier class w/metadata.
  - Host and client roles are managed automatically.
  - Peer redirection and relay are supported for NAT traversal scenarios AND typical centralized configurations.

- **Advanced Serialization**  
  - Serializer interface (`ISerializer`) with a fast, efficient JSON implementation.
  - Custom converters for complex types (e.g., `Bitmap`).
  - Type whitelisting/blacklisting for secure deserialization.

- **Efficient Data Handling**  
  - Large buffer support (default 1MB) for high-throughput scenarios.
  - Asynchronous, non-blocking I/O for scalability.
  - Optimized for .NET Standard 2.1, .NET 6, .NET 8, and .NET 9.

- **Metadata-Driven Peer Management**  
  - Attach arbitrary metadata to peers and connections.
  - Query and filter peers by metadata for dynamic discovery and routing.

- **Extensible Event System**  
  - Events for connection, disconnection, peer updates, and data receipt.
  - Customizable handling for peer redirection and connection requests.

- **Security & Control**  
  - SSL/TLS support for secure connections.
  - Fine-grained control over allowed types and connection policies.

---

## Peer-to-Peer Implementations

SocketJack's P2P system is designed for flexibility and ease of use:

- **Peer Discovery:**  
  Use metadata to find peers with specific attributes (e.g., game lobbies, chat rooms).

- **Direct Peer Connections:**  
  Peers can connect directly, bypassing the server when possible for low-latency communication.

- **Peer Redirection:**  
  If a direct connection is not possible, messages can be relayed through other peers or the server.

- **Host/Client Role Management:**  
  The library automatically manages which peer acts as host or client, simplifying connection logic.

- **Metadata Propagation:**  
  Metadata changes are propagated to all connected peers, enabling dynamic network topologies.

---

## Efficiency

- **High Throughput:**  
  Large default buffer sizes and efficient serialization minimize overhead.

- **Asynchronous Operations:**  
  All networking is fully async, allowing thousands of concurrent connections.

- **Minimal Allocations:**  
  Serialization and deserialization are optimized to reduce memory usage and GC pressure.

- **Selective Serialization:**  
  Only allowed types are serialized/deserialized, improving security and performance.

---

## Possible Usages

SocketJack is ideal for a wide range of networking scenarios, including:

- **Real-Time Multiplayer Games**  
  Fast, low-latency communication between players with dynamic peer discovery.

- **Distributed Chat Applications**  
  Peer-to-peer chat with metadata-driven room discovery and direct messaging.

- **IoT Device Networks**  
  Efficient, secure communication between devices with flexible topology.

- **Remote Control & Automation**  
  Secure, event-driven control of remote systems with custom data types.

- **Custom Protocols & Services**  
  Build your own protocols on top of TCP/WebSocket with full control over serialization and peer management.

---

## Getting Started

1. **Install via NuGet:**
```
   Install-Package SocketJack
```

2. **Create a Server:**
```
   var server = new TcpServer(port: 12345);
   server.StartListening();
```

3. **Connect a Client:**
```
   var client = new TcpClient();
   await client.Connect("127.0.0.1", 12345);
```

4. **Sending and Receiving:**
```
   client.Send("Hello, Server!");
   server.OnReceived += (sender, args) => {
       var message = args.Object as string;
       // Handle message
   };
```

5. **Setting up callbacks:**
```cs
// Register a callback for a custom class
server.RegisterCallback<ChatMessage>((ReceivedEventArgs args) => {
      //args.Object is type of ChatMessage
      //args.From can be used in conjunction with MetaData to aquire useful information about the remote peer
      // e.g. Await args.From.GetMetaData("Username");
 });
```

6. **Enabling options by default:**
 *MUST be called before the instantiation of a Client or Server.*
```
   TcpOptions.Default.UsePeerToPeer = false; //true by default
```

7. **Attach Metadata:**
*This will ONLY work on the server authority.*
*Can be used to authenticate clients.*
```
    TcpConnection firstClientOnServer = server.Clients.FirstOrDefault().Value;
    firstClientOnServer.SetMetaData("Room", "Lobby1");
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
