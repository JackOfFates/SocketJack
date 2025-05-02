# SocketJack

Fast, Efficient, 100% Managed, Peer to Peer, Non Generic, Extensible TCP Client & Server with built in Serialization that can be used to transmit any type.

***Note*** `You cannot send UI classes or anything with self-referencing unless those properties are private.`


`SocketJack` automatically serializes `Public Properties` for transfer within each object you send or receive without using `generics` *safely* by `white-listing used types`.

# Security Vulerabilities can be avoided.
## *Use your OWN discretion*
## `YOU SHOULD NEVER. EVER. WHITE-LIST A TYPE THAT CAN BE EXPLOITED.`

```cs
{
    Server.RegisterCallback(typeof(MyObjectType), Received_MyObject);
}
    private void Received_BandwidthObject(ReceivedEventArgs args) {
        Console.WriteLine("Received: MyObject");
    }
```

or


```cs
    TcpClient.Whitelist.AddType(Type);
    TcpServer.Whitelist.AddType(Type);
```

# SocketJack comes with System.Text.Json support built in.
SocketJack exposes the `ISerializationProtocol` interface which can be used to add another serializer.

### You can also install `SocketJack.NewtonsoftJson` or implement your own choice of serializers.

[SocketJack.NewtonsoftJson Nuget Package](https://www.nuget.org/packages/SocketJack.NewtonsoftJson)
```bash
PM> NuGet\Install-Package  SocketJack.NewtonsoftJson
```


# Roadmap
- SSL Support
- Bandwidth limiting
- FTP-like Functions

# v1.0.0.0 Release Notes

 - Multi-CPU Support at Runtime (***No configuration required***)
 - Fully **Thread  Safe** and **Concurrent** w\ThreadManager Class to handle all Tcp Threading
 - Host Caching for **Improved Connection Performance**
 - Standardized Type, Method, And Function Naming

## Your application will remain open in the background unless you dispose Clients & Servers, or can just do this on **Application Exit** 

### 
```cs
    ThreadManager.Shutdown();
```

## Peer to peer functionality
Built for faster latency and direct communication between clients.
P2P TcpServer is ***automatically***  `port forwarded` using `Mono.NAT` UPNP.

`Connect to the main server then, anytime after the TcpClient.OnIdentified Event start a P2P server.`

```cs
private  async  void  MyTcpClient_PeerConnected(object  sender, PeerIdentification  RemotePeer) {
    // Make sure it's not your own client.
    if (MyTcpClient.RemoteIdentity != null && RemotePeer.ID != MyTcpClient.RemoteIdentity.ID) {
        TcpServer  P2P_Server = await  RemotePeer.StartServer("ExampleServer");
        P2P_Server.Logging = true;
        P2P_Server.LogReceiveEvents = true;
        P2P_Server.LogSendEvents = true;
        P2P_Server.OnReceive += P2PServer_OnReceive;
        P2P_Server.OnDisconnected += P2PServer_OnDisconnected;
        P2P_Server.OnError += P2PServer_OnError;
    }
}
```

``Then, on the other Remote Client Accept by handling the TcpClient_PeerConnectionRequest Event.``

```cs

private  async  void  MyTcpClient_PeerConnectionRequest(object  sender, P2PServer  Server) {
    // CHANGE THIS - Add UI which allows the user to accept the connection.
    TcpClient  P2P_Client = await  Server.Accept(true, "ExampleClient");
    P2P_Client.Logging = true;
    P2P_Client.LogReceiveEvents = true;
    P2P_Client.LogSendEvents = true;
    P2P_Client.OnReceive += P2PClient_OnReceive;
    P2P_Client.OnConnected += P2PClient_OnConnected;
    P2P_Client.OnDisconnected += P2PClient_OnDisconnected;
    P2P_Client.OnError += P2PClient_OnError;
}

```

Features also incude `TcpClient.Send(PeerIdentification, Object)` for easily sending indirectly to another client from a client without having to handle it on the server.
PeerRedirect can be canceled on the server inside the `TcpServer.OnReceive` via `e.CancelPeerRedirect` to `True`.
Debug/Console `Logging` added for all network events `besides`  ***Send*** and ***Receive*** via `TcpClient.Logging = True` & `TcpClient.LogToOutput = True` etc..
Send and Receive events can be `individually enabled` via `TcpClient.LogReceiveEvents` etc...
Async ***TcpClient.Connect()*** w/ timeout (3 seconds) & ***AutoReconnect*** Option.


# Examples

### C#

```cs
SocketJack.Networking.Client.TcpClient  TcpClient;
SocketJack.Networking.Server.TcpServer  TcpServer;

private  const  string  Host = "127.0.0.1";
private  const  int  Port = 7474;

// Handle the incoming object
private  void  TcpServer_OnReceive(ref  ReceivedEventArgs  e) {
    // Send The Client an Object
    TcpServer.Send(Client, MySerializableClass);

    // Send All Clients an Object
    TcpServer.Broadcast(MySerializableClass);

}

private  void  TcpClient_OnConnected(ConnectedEventArgs  e) {
    TcpClient.Send(new  AuthorizationRequestObject());
}

public  SurroundingVoid() {
    // Creating a server.
    TcpServer = new  Server.TcpServer(Port);
    TcpServer.OnReceive += TcpServer_OnReceive;

    // '9999' being the max amount of pending connections allowed.
    TcpServer.Listen(9999);


    // Creating a client.
    TcpClient = new  Client.TcpClient();
    TcpClient.OnConnected += TcpClient_OnConnected;
    TcpClient.Connect("127.0.0.1", Port);
}
```
 
## Contributing

Pull requests are welcome. For major changes, please open an issue first

to discuss what you would like to change.

## License
  
[MIT](https://choosealicense.com/licenses/mit/)
