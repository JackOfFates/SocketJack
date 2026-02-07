Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P


Public Class HttpServerTest
    Implements ITest

    Private ServerPort As Integer = 8201
    Public WithEvents Server As HttpServer


    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Server = New HttpServer(ServerPort, "HttpServer")

        UpdateStartStopUi()

        With Server.Options
            .Logging = True
            .LogReceiveEvents = True
            .LogSendEvents = True
            .UseCompression = True
            .CompressionAlgorithm.CompressionLevel = IO.Compression.CompressionLevel.SmallestSize
        End With

        ' Register HelloObj as a handled callback type so the server whitelist accepts it
        Server.RegisterCallback(Of HelloObj)(Sub(e)
                                                 ' simple handler to confirm reception on server side
                                                 Log("Server constructor callback registered for HelloObj: " & e.Object?.Text)
                                             End Sub)

        ' Map an HTTP endpoint that returns a HelloObj (serialized via HttpServer options)
        Server.Map("GET", "/HelloObj", Function(conn, req, ct)
                                           Return New HelloObj()
                                       End Function)
    End Sub

    Private Sub UpdateStartStopUi(Optional busyText As String = Nothing, Optional isBusy As Boolean = False)
        If ButtonStartStop Is Nothing Then Return

        Dispatcher.InvokeAsync(Sub()
                                   If isBusy Then
                                       ButtonStartStop.IsEnabled = False
                                       If Not String.IsNullOrWhiteSpace(busyText) Then
                                           ButtonStartStop.Content = busyText
                                       End If
                                       Return
                                   End If

                                   Dim listening = False
                                   If Server IsNot Nothing Then
                                       listening = Server.IsListening
                                   End If

                                   ButtonStartStop.IsEnabled = True
                                   ButtonStartStop.Content = If(listening, "Stop Test", "Start Test")
                               End Sub)
    End Sub

#Region "Test Classes"

    Public Class HelloObj
        Public Property Text As String = "# SocketJack v1.3 " & Environment.NewLine &
                                         "" & Environment.NewLine &
                                         "
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
   await client.Connect(/""127.0.0.1/"", 12345);
```

4. **Sending and Receiving:**
```
   client.Send(/""Hello, Server!/"");
   server.OnReceived += (sender, args) => {
       var message = args.Object as string;
       // Handle message
   };
```

5. **Setting up callbacks:**
```cs
// Register a callback for a custom class
server.RegisterCallback<ChatMessage>((ReceivedEventArgs<ChatMessage> args) => {
      //args.Object is type of ChatMessage
      //args.From can be used in conjunction with MetaData to aquire useful information about the remote peer
      // e.g. Await args.From.GetMetaData(/""Username/"");
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
    firstClientOnServer.SetMetaData(/""Room/"", /""Lobby1/"");
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

**SocketJack** — Fast, flexible, and modern networking for .NET." & Environment.NewLine
    End Class

#End Region

#Region "UI"

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "HttpServer"
        End Get
    End Property

    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return True
        End Get
    End Property

    Public Property Running As Boolean Implements ITest.Running
        Get
            Return Server.IsListening
        End Get
        Set(value As Boolean)
            If Server Is Nothing Then Return
            If value AndAlso Not Server.IsListening Then
                ITest_StartTest()
            ElseIf Not value AndAlso Server.IsListening Then
                ITest_StopTest()
            End If
        End Set
    End Property
    Private _Running As Boolean


    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Running Then Return

        TextLog.Text = String.Empty
        UpdateStartStopUi("Starting..", isBusy:=True)

        Try
            If Not Server.Listen() Then
                Return
            End If

            ' Server is now listening; enable the button immediately so user can stop it.
            UpdateStartStopUi()

            Try
                wb1.Navigate(New Uri("http://localhost:" & ServerPort & "/"))
            Catch
            End Try

            ' Open browser to the test site
            Try
                Dim psi As New ProcessStartInfo("http://localhost:" & ServerPort)
                psi.UseShellExecute = True
                '//'Process.Start(psi)
            Catch ex As Exception
                Log("Failed to open browser: " & ex.Message)
            End Try

            ' Use HttpClient to request the page and register a callback to receive deserialized HelloObj
            Dim hc As New SocketJack.Net.HttpClient()
            With hc.Options
                .Logging = True
                .LogReceiveEvents = True
                .LogSendEvents = True
            End With
            AddHandler hc.LogOutput, AddressOf Log
            hc.RegisterCallback(Of HelloObj)(Sub(e) Log("Received HelloObj From Server!"))
            Log("Requesting HelloObj From Server...")
            Dim resp = Await hc.GetAsync("http://localhost:" & ServerPort & "/helloObj")
            'Log("Received HelloObj From Server!")
            'If resp IsNot Nothing Then Log(resp.Body)
        Catch ex As Exception
            Log("StartTest error: " & ex.Message)
        Finally
            UpdateStartStopUi()
        End Try
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            TextLog.Text = String.Empty
            UpdateStartStopUi("Stopping..", isBusy:=True)
            Server.StopListening()
            UpdateStartStopUi()
        End If
    End Sub

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Public Sub Log(text As String) Handles Server.LogOutput
        If text Is Nothing OrElse text = String.Empty Then Return
        Try
            Dispatcher.Invoke(Sub()
                                  Try
                                      SyncLock (TextLog)
                                          Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
                                          Try : TextLog.AppendText(If(text.IndexOf(Environment.NewLine) > 0, text, text & vbCrLf))
                                          Catch : End Try
                                          Try : If isAtEnd Then TextLog.ScrollToEnd()
                                          Catch : End Try
                                      End SyncLock
                                  Catch : End Try

                              End Sub)
        Catch : End Try
    End Sub

    Private Async Sub HttpServerTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If NIC.InterfaceDiscovered Then
            Await Forward()
        Else
            AddHandler NIC.OnInterfaceDiscovered, Async Sub() Forward()
        End If

    End Sub

    Public Async Function Forward() As Task(Of Boolean)
        Dim forwarded As Boolean = Await NIC.ForwardPort(80)
        If forwarded Then
            Log("Port 80 forwarded successfully via UPnP." & Environment.NewLine)
        Else
            Log("Port forwarding via UPnP failed or not available." & Environment.NewLine)
        End If
        Return forwarded
    End Function


#End Region

End Class