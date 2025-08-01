
# SocketJack

 

Fast, Efficient, 100% Managed, Built-in Peer to Peer, Generic, Extensible TCP Client & Server that transmits .NET objects with Type Saftey.

 

`SocketJack` automatically serializes `Public Properties` utilizing a type `white-list` for security.

 

## SocketJack comes with System.Text.Json support built in.

SocketJack exposes the `ISerializer` interface which can be used to add another serializer.

 

## The `SocketJack.NewtonsoftJson` nuget package has been deprecated. 
*It WILL NOT be updated and does `NOT` work with 1.0.2+.*

 

# v1.1.0.0 Release Notes

***Thoroughly tested all features, stable.***


### In Progress [See GitHub Repo](https://github.com/JackOfFates/SocketJack)
- Javascript WebSocketClient implementation

### Added

- WebSocketClient
- WebSocketServer
- Metadata system similar to Cookies in HTTP servers
- HTTP Server
- PeerIdentification.Send will now find it's reference TcpClient Automatically
- SSL Support
- Compression
- Chat Test
- Generic Callbacks
- Blacklist
- Upload buffer

 

### Fixed

- Download Buffering speed
- Thread blocking
- Segments were disabled by comments
- PeerServer Accepting
- Compression Options
- DefaultOptions Initialization
- PeerRedirect Type Checking
- PeerRedirect Vulnerability
- Non-Generic OnReceive not being called
- Buffering
- Race conditions
- Re-introduced segments for stability

 

### Updated

- Tests UI
- Tests compatibility w/ compression & SSL
- Multi-CPU Support at Runtime
- Host Caching for **Improved Connection Performance**
- Standardized Type, Method, And Function Naming
- Fully **Thread Safe** and **Concurrent** w\ThreadManager Class to handle all Tcp Threading

### Removed
- RemoteIdentity.Tag ( *Replaced with MetaData* )


# Getting Started
*For a more comprehensive guide on feature-sets of SocketJack please check the Tests project on the [GitHub Repo](https://github.com/JackOfFates/SocketJack)*


### Default Options

`TcpClient` & `TcpServer` will inherit properties set to `SocketJack.Management.DefaultOptions` by default.

 
### Your application will remain open in the background unless you dispose Clients & Servers, or can just do this on **Application Exit**

 

```cs

ThreadManager.Shutdown();

```

### Building an application with SocketJack

```cs

public class MyApp {

    SocketJack.Networking.Client.TcpClient TcpClient = new Client.TcpClient();
    SocketJack.Networking.Server.TcpServer TcpServer = new Server.TcpServer(Port);

    private const string Host = "127.0.0.1";
    private const int Port = 7474;

    // Handle incoming objects
    private void TcpServer_OnReceive(ref ReceivedEventArgs e) {
        // Avoid switch cases or if statements when you have a lot of objects to handle.
    }

 

    private void TcpClient_OnConnected(ConnectedEventArgs e) {
        // Send the server an authorization request
        TcpClient.Send(new AuthorizationRequest());

    }

 

    private void Server_AuthorizationRequest(ReceivedEventArgs<AuthorizationRequest> args) {
        // Handle the auth request on the server
        // Note: This is just used as an example.
        // Please refer to the Tests Project> Chat Test on github for a working example.

    }

 

    private void Client_AuthorizationRequest(ReceivedEventArgs<AuthorizationRequest> args) {
       // Handle the auth request on the client
       // Note: This is just used as an example.
    }

 

    public async void Start_MyApp() {
        // Start the server.
        TcpServer.RegisterCallback<AuthorizationRequest>(Server_AuthorizationRequest);
        TcpServer.OnReceive += TcpServer_OnReceive;
        TcpServer.Listen();

        // Start the client.
        TcpClient.RegisterCallback<AuthorizationRequest>(Client_AuthorizationRequest);
        TcpClient.OnConnected += TcpClient_OnConnected;
        // Connect function timeout is 3 seconds by default.
        await TcpClient.Connect("127.0.0.1", Port);
    }

}

```
 

### Logging

```cs
    TcpClient.Logging = true;
    TcpClient.LogToOutput = true;
    TcpClient.LogReceiveEvents = true;
    TcpClient.LogSendEvents = true;

```

### Peer to peer functionality

Built for faster latency and direct communication between clients.


P2P TcpServer is ***automatically*** `port forwarded` using `Mono.NAT` UPNP.
 

Send another client on the server an object `WITHOUT` extra code or exposing their IP address.


PeerRedirect can be canceled on the server inside the `TcpServer.OnReceive` by setting `e.CancelPeerRedirect` to `True`.


`Connect to the main server then, anytime after the TcpClient.OnIdentified Event start a P2P server.`

 

```cs
    private async void MyTcpClient_PeerConnected(object sender, PeerIdentification RemotePeer) {

    // Make sure it's not your own client.
    if (MyTcpClient.RemoteIdentity != null && RemotePeer.ID != MyTcpClient.RemoteIdentity.ID) {
        TcpOptions Options = new TcpOptions();
        Options.Logging = true;
        Options.LogReceiveEvents = true;
        Options.LogSendEvents = true;

        TcpServer P2P_Server = await RemotePeer.StartServer(Options, "ExampleServer");
        P2P_Server.OnReceive += P2PServer_OnReceive;
        P2P_Server.OnDisconnected += P2PServer_OnDisconnected;
        P2P_Server.OnError += P2PServer_OnError;
    }
}

```

``Then, on the other Remote Client Accept by handling the TcpClient_PeerConnectionRequest Event.``

```cs
    private async void MyTcpClient_PeerConnectionRequest(object sender, P2PServer Server) {
        // CHANGE THIS - Add UI which allows the user to accept the connection.
        TcpOptions Options = new TcpOptions();
        Options.Logging = true;
        Options.LogReceiveEvents = true;
        Options.LogSendEvents = true;

        TcpClient P2P_Client = await Server.Accept(options, "ExampleClient");
        P2P_Client.OnReceive += P2PClient_OnReceive;
        P2P_Client.OnConnected += P2PClient_OnConnected;
        P2P_Client.OnDisconnected += P2PClient_OnDisconnected;
        P2P_Client.OnError += P2PClient_OnError;
    }
```

 

```cs

TcpClient.Send(PeerIdentification, Object)

```

 

## Contributing

 

Pull requests are welcome. For major changes, please open an issue first

 

to discuss what you would like to change.

 

## License

[MIT](https://choosealicense.com/licenses/mit/)
