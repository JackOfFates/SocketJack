Imports System.Drawing
Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography.X509Certificates
Imports System.Security.Principal
Imports System.Threading
Imports System.Windows.Forms
Imports System.Windows.Interop
Imports System.Xml.Schema
Imports Microsoft.SqlServer
Imports Mono.Nat
Imports SocketJack.Compression
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P
Imports SocketJack.Net.WebSockets
Imports SocketJack.Serialization.Json

''' <summary>
''' This test simulates a simple chat application where two clients can send messages to each other through a server.
''' The server is responsible for redirecting messages between clients.
''' The clients can send messages and receive them from other clients.
''' The server should use SSL for secure communication, compression is enabled for message size reduction.
''' </summary>
Public Class ChatTest
    Implements ITest

    Private ServerPort As Integer = 7543
    Public WithEvents Server As ISocket
    Public WithEvents Client1 As ISocket
    Public WithEvents Client2 As ISocket
    Public ServerOptions As New NetworkOptions()
    Public ClientOptions As New NetworkOptions()

    Private Sub Cleanup()
        If Client1 IsNot Nothing Then
            Client1.Dispose()
            Client1 = Nothing
        End If
        If Client2 IsNot Nothing Then
            Client2.Dispose()
            Client2 = Nothing
        End If
        If Server IsNot Nothing Then
            Server.Dispose()
            Server = Nothing
        End If
    End Sub

    Private Sub Setup()
        Cleanup()
        With ServerOptions
            .Logging = True
            .LogReceiveEvents = False
            .LogSendEvents = False
            .LogToConsole = True
            .UseCompression = False
            .UpdateConsoleTitle = True
            .MaximumDownloadMbps = SBW.Value
            .MaximumUploadMbps = SBW.Value
            .Fps = rRate.Value
            .CompressionAlgorithm.CompressionLevel = CompressionLevel.SmallestSize
            '.UseSsl = True
        End With
        With ClientOptions
            .Logging = ServerOptions.Logging
            .LogReceiveEvents = ServerOptions.LogReceiveEvents
            .LogSendEvents = ServerOptions.LogSendEvents
            .LogToConsole = ServerOptions.LogToConsole
            .UseCompression = ServerOptions.UseCompression
            .UploadBufferSize = ServerOptions.UploadBufferSize
            .DownloadBufferSize = ServerOptions.DownloadBufferSize
            .MaximumBufferSize = ServerOptions.MaximumBufferSize
            .MaximumDownloadMbps = ServerOptions.MaximumDownloadMbps
            .MaximumUploadMbps = ServerOptions.MaximumUploadMbps
            .Fps = ServerOptions.Fps
            .CompressionAlgorithm.CompressionLevel = ServerOptions.CompressionAlgorithm.CompressionLevel
            '.UseSsl = True
        End With
        Dim KeyHostName As String = "MY-KEY-HOSTNAME.com" ' Replace with your actual SSL certificate hostname
        If WebSocket_Enabled.IsChecked Then
            Dim s As New WebSocketServer(ServerPort, "ChatServer") With {.Options = ServerOptions}
            Dim c1 As New WebSocketClient(ClientOptions, "ChatClient1") With {.Options = ClientOptions}
            Dim c2 As New WebSocketClient(ClientOptions, "ChatClient2") With {.Options = ClientOptions}

            Server = s
            Client1 = c1
            Client2 = c2

            ' Logging
            AddHandler s.LogOutput, AddressOf Log
            AddHandler c1.LogOutput, AddressOf Log
            AddHandler c2.LogOutput, AddressOf Log

            AddHandler c1.OnDisconnected, AddressOf Client1_OnDisconnected
            AddHandler c2.OnDisconnected, AddressOf Client2_OnDisconnected
            AddHandler c1.OnConnected, AddressOf Client1_OnConnected
            AddHandler c2.OnConnected, AddressOf Client2_OnConnected

            ' Setup remote identity
            AddHandler c1.OnIdentified, AddressOf Client1_OnIdentified
            AddHandler c2.OnIdentified, AddressOf Client2_OnIdentified

            ' Peer list updates
            AddHandler c1.OnConnected, AddressOf RefreshUserList
            AddHandler c2.OnConnected, AddressOf RefreshUserList
            AddHandler c1.OnDisconnected, AddressOf RefreshUserList
            AddHandler c2.OnDisconnected, AddressOf RefreshUserList
            AddHandler c1.PeerConnected, AddressOf RefreshUserList
            AddHandler c1.PeerDisconnected, AddressOf RefreshUserList
            AddHandler c2.PeerConnected, AddressOf RefreshUserList
            AddHandler c2.PeerDisconnected, AddressOf RefreshUserList
        ElseIf UDP_Enabled.IsChecked Then
            Dim s As New UdpServer(ServerPort, "ChatServer") With {.Options = ServerOptions}
            Dim c1 As New UdpClient(ClientOptions, "ChatClient1")
            Dim c2 As New UdpClient(ClientOptions, "ChatClient2")

            Server = s
            Client1 = c1
            Client2 = c2
            AddHandler s.StoppedListening, AddressOf Server_UdpStoppedListening

            ' Logging
            AddHandler s.LogOutput, AddressOf Log
            AddHandler c1.LogOutput, AddressOf Log
            AddHandler c2.LogOutput, AddressOf Log

            AddHandler c1.OnDisconnected, AddressOf Client1_OnDisconnected
            AddHandler c2.OnDisconnected, AddressOf Client2_OnDisconnected
            AddHandler c1.OnConnected, AddressOf Client1_OnConnected
            AddHandler c2.OnConnected, AddressOf Client2_OnConnected

            ' Setup remote identity
            AddHandler c1.OnIdentified, AddressOf Client1_OnIdentified
            AddHandler c2.OnIdentified, AddressOf Client2_OnIdentified

            ' Peer list updates
            AddHandler c1.OnConnected, AddressOf RefreshUserList
            AddHandler c2.OnConnected, AddressOf RefreshUserList
            AddHandler c1.OnDisconnected, AddressOf RefreshUserList
            AddHandler c2.OnDisconnected, AddressOf RefreshUserList
            AddHandler c1.PeerConnected, AddressOf RefreshUserList
            AddHandler c1.PeerDisconnected, AddressOf RefreshUserList
            AddHandler c2.PeerConnected, AddressOf RefreshUserList
            AddHandler c2.PeerDisconnected, AddressOf RefreshUserList
        Else
            ' Set the SSL certificate for the server. 
            ' Make sure to replace the path and password with your actual certificate details.
            ' NEVER hardcode the password in production code on the client side.
            'Server.SslCertificate = New X509Certificate2("...\Location on drive\Private Key.pfx", "MY KEY PASSWORD")
            Dim s As New TcpServer(ServerPort, "ChatServer") With {.Options = ServerOptions, .SslTargetHost = KeyHostName}
            Dim c1 As New TcpClient("ChatClient1") With {.Options = ClientOptions, .SslTargetHost = KeyHostName}
            Dim c2 As New TcpClient("ChatClient2") With {.Options = ClientOptions, .SslTargetHost = KeyHostName}
            Server = s
            Client1 = c1
            Client2 = c2
            AddHandler Server.AsTcpServer().StoppedListening, AddressOf Server_StoppedListening

            ' Logging
            AddHandler s.LogOutput, AddressOf Log
            AddHandler c1.LogOutput, AddressOf Log
            AddHandler c2.LogOutput, AddressOf Log

            AddHandler c1.OnDisconnected, AddressOf Client1_OnDisconnected
            AddHandler c2.OnDisconnected, AddressOf Client2_OnDisconnected
            AddHandler c1.OnConnected, AddressOf Client1_OnConnected
            AddHandler c2.OnConnected, AddressOf Client2_OnConnected

            ' Setup remote identity
            AddHandler c1.OnIdentified, AddressOf Client1_OnIdentified
            AddHandler c2.OnIdentified, AddressOf Client2_OnIdentified

            ' Peer list updates
            AddHandler c1.OnConnected, AddressOf RefreshUserList
            AddHandler c2.OnConnected, AddressOf RefreshUserList
            AddHandler c1.OnDisconnected, AddressOf RefreshUserList
            AddHandler c2.OnDisconnected, AddressOf RefreshUserList
            AddHandler c1.PeerConnected, AddressOf RefreshUserList
            AddHandler c1.PeerDisconnected, AddressOf RefreshUserList
            AddHandler c2.PeerConnected, AddressOf RefreshUserList
            AddHandler c2.PeerDisconnected, AddressOf RefreshUserList
        End If

        Server.RegisterCallback(Of LoginObj)(AddressOf Server_ClientLogin)

        ' Handle messages on the clients
        Client1.RegisterCallback(Of ChatMessage)(AddressOf Clients_ReceivedMessage)
        Client2.RegisterCallback(Of ChatMessage)(AddressOf Clients_ReceivedMessage)

        ' Handle images on the clients
        Client1.RegisterCallback(Of Bitmap)(AddressOf Clients_ReceivedBitmap)
        Client2.RegisterCallback(Of Bitmap)(AddressOf Clients_ReceivedBitmap)

        ' Handle images on the clients
        Client1.RegisterCallback(Of FileContainer)(AddressOf Clients_ReceivedFile)
        Client2.RegisterCallback(Of FileContainer)(AddressOf Clients_ReceivedFile)

        ' Mouse Movements
        Client1.RegisterCallback(Of pPos)(AddressOf Clients_ReceivedMouseMove1)
        Client2.RegisterCallback(Of pPos)(AddressOf Clients_ReceivedMouseMove2)


        ' Add the ChatMessage type to the whitelist since the server is just redirecting and not handling it
        ' For doing things like blocking other users or filtering messages you should handle it on the server.
        ' Set e.CancelPeerRedirect = true to stop the message from being sent to the recipient.
        Server.Options.Whitelist.Add(GetType(ChatMessage))
        Server.Options.Whitelist.Add(GetType(Bitmap))
        Server.Options.Whitelist.Add(GetType(FileContainer))
        Server.Options.Whitelist.Add(GetType(pPos))
    End Sub

    Private Sub Clients_ReceivedMouseMove1(e As ReceivedEventArgs(Of pPos))
        Dispatcher.InvokeAsync(Sub() BC1.Margin = New Thickness(e.Object.X, e.Object.Y / 2, 0, 0))
    End Sub
    Private Sub Clients_ReceivedMouseMove2(e As ReceivedEventArgs(Of pPos))
        Dispatcher.InvokeAsync(Sub() BC2.Margin = New Thickness(e.Object.X, (e.Object.Y / 2) + (Me.ActualHeight / 2), 0, 0))
    End Sub


    Public Class pPos
        Public Property X As Integer
        Public Property Y As Integer
    End Class

    Private Sub Server_StoppedListening(sender As TcpServer)
        ButtonC1.IsEnabled = False
        ButtonC2.IsEnabled = False
    End Sub

    Private Sub Server_UdpStoppedListening(sender As UdpServer)
        ButtonC1.IsEnabled = False
        ButtonC2.IsEnabled = False
    End Sub

    Private Sub Client1_OnConnected(e As ConnectedEventArgs)
        ButtonC1.Content = "Disconnect"
        ButtonC1.IsEnabled = True
    End Sub

    Private Sub Client2_OnConnected(e As ConnectedEventArgs)
        ButtonC2.Content = "Disconnect"
        ButtonC2.IsEnabled = True
    End Sub

    Private Async Sub RefreshUserList()
        Dim isWebsocket = False
        Dim isUdp = False
        Dispatcher.Invoke(Sub()
                              isWebsocket = WebSocket_Enabled.IsChecked
                              isUdp = UDP_Enabled.IsChecked
                          End Sub)
        If isWebsocket Then
            Try
                Dim Output As String = String.Empty
                For Each client In Server.As(Of WebSocketServer)().Clients.Values
                    Dim username As String = Await client.GetMetaData("Username")
                    Output &= username & Environment.NewLine
                Next

                Dispatcher.Invoke(Sub() UserList.Text = Output)
            Catch ex As Exception

            End Try
        ElseIf isUdp Then
            Try
                Dim Output As String = String.Empty
                For Each client In Server.AsUdpServer().Clients.Values
                    If client.Identity IsNot Nothing Then
                        Dim username As String = Await client.Identity.GetMetaData("Username")
                        Output &= username & Environment.NewLine
                    End If
                Next

                Dispatcher.Invoke(Sub() UserList.Text = Output)
            Catch ex As Exception

            End Try
        Else
            Try
                Dim Output As String = String.Empty
                For Each client In Server.As(Of TcpServer)().Clients.Values
                    Dim username As String = Await client.GetMetaData("Username")
                    Output &= username & Environment.NewLine
                Next

                Dispatcher.Invoke(Sub() UserList.Text = Output)
            Catch ex As Exception

            End Try
        End If
    End Sub

    Private Sub Clients_ReceivedFile(args As ReceivedEventArgs(Of FileContainer))
        Dispatcher.InvokeAsync(Async Function()
                                   Dim msgResult As MsgBoxResult = MsgBox("Do you want to save the received file '" & args.Object.FileName & "'?", MsgBoxStyle.YesNo, "File Received")
                                   If msgResult = MsgBoxResult.Yes Then
                                       Dim sfd As New SaveFileDialog()
                                       sfd.FileName = args.Object.FileName
                                       If sfd.ShowDialog() = DialogResult.OK Then
                                           File.WriteAllBytes(sfd.FileName, args.Object.Data)
                                           LogMessage(Await args.From.GetMetaData("Username"), "File '" & args.Object.FileName & "' saved to " & sfd.FileName)
                                       End If
                                   Else
                                       LogMessage(Await args.From.GetMetaData("Username"), "File '" & args.Object.FileName & "' received but not saved.")
                                   End If
                               End Function)
    End Sub

    Private Async Sub Clients_ReceivedMessage(args As ReceivedEventArgs(Of ChatMessage))
        LogMessage(Await args.From.GetMetaData("Username"), args.Object.Text)
        'LogMessage(args.Object.From, args.Object.Text)
    End Sub

    Public Function BitmapToBitmapImage(src As Bitmap) As BitmapImage
        Dim ms As MemoryStream = New MemoryStream()
        CType(src, System.Drawing.Bitmap).Save(ms, System.Drawing.Imaging.ImageFormat.Bmp)
        Dim image As BitmapImage = New BitmapImage()
        image.BeginInit()
        ms.Seek(0, SeekOrigin.Begin)
        image.StreamSource = ms
        image.EndInit()
        Return image
    End Function

    Private Sub Clients_ReceivedBitmap(args As ReceivedEventArgs(Of Bitmap))
        Dispatcher.Invoke(Async Function()
                              Dim fromUser As String = Await args.From.GetMetaData("Username")
                              Select Case fromUser
                                  Case "Client1"
                                      Img2.Source = BitmapToBitmapImage(args.Object)
                                  Case "Client2"
                                      Img1.Source = BitmapToBitmapImage(args.Object)
                              End Select
                          End Function)
    End Sub

    Private Sub Server_ClientLogin(e As ReceivedEventArgs(Of LoginObj))
        ' When the server receives a login object, we can set the tag for the connection.
        ' Usernames are just an example.
        e.Connection.SetMetaData("Username", e.Object.UserName)
    End Sub

    Private Sub Client1_OnIdentified(sender As ISocket, LocalIdentity As Identifier)
        ' When the client is identified, we can send the login object to the server.
        ' This is a dummy object for your login which lets anyone choose any username.
        ' You should replace it with your actual login logic.
        Dim isWs = False
        Dim isUdp = False
        Dispatcher.Invoke(Sub()
                              isWs = WebSocket_Enabled.IsChecked
                              isUdp = UDP_Enabled.IsChecked
                          End Sub)
        If isWs Then
            Client1.AsWsClient.Send(New LoginObj With {.UserName = "Client1"})
        ElseIf isUdp Then
            Client1.AsUdpClient.Send(New LoginObj With {.UserName = "Client1"})
        Else
            Client1.AsTcpClient.Send(New LoginObj With {.UserName = "Client1"})
        End If

        Dispatcher.Invoke(Sub()
                              ChatMessage1.IsEnabled = True
                              SendButton1.IsEnabled = True
                              SendButton1_Pic.IsEnabled = True
                              SendButton1_File.IsEnabled = True
                          End Sub)
    End Sub

    Private Sub Client2_OnIdentified(sender As ISocket, LocalIdentity As Identifier)
        ' When the client is identified, we can send the login object to the server.
        ' This is a dummy object for your login which lets anyone choose any username.
        ' You should replace it with your actual login logic.
        Dim isWs = False
        Dim isUdp = False
        Dispatcher.Invoke(Sub()
                              isWs = WebSocket_Enabled.IsChecked
                              isUdp = UDP_Enabled.IsChecked
                          End Sub)
        If isWs Then
            Client2.AsWsClient.Send(New LoginObj With {.UserName = "Client2"})
        ElseIf isUdp Then
            Client2.AsUdpClient.Send(New LoginObj With {.UserName = "Client2"})
        Else
            Client2.AsTcpClient.Send(New LoginObj With {.UserName = "Client2"})
        End If
        Dispatcher.Invoke(Sub()
                              ChatMessage2.IsEnabled = True
                              SendButton2.IsEnabled = True
                              SendButton2_Pic.IsEnabled = True
                              SendButton2_File.IsEnabled = True
                          End Sub)
    End Sub


#Region "Chat Classes"
    Public Class ChatMessage
        Public Property Text As String
    End Class

    Public Class LoginObj
        Public Property UserName As String
    End Class
#End Region

#Region "UI"

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "ChatServer"
        End Get
    End Property

    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return False
        End Get
    End Property

    Public Property Running As Boolean Implements ITest.Running
        Get
            Dim isNull = Server Is Nothing
            If isNull Then
                Return False
            Else
                Return CType(Server, Object).IsListening
            End If
        End Get
        Set(value As Boolean)
            If Server Is Nothing Then Return
            Dim isListening As Boolean = CType(Server, Object).IsListening
            If value AndAlso Not isListening Then
                ITest_StartTest()
            ElseIf Not value AndAlso isListening Then
                ITest_StopTest()
            End If
        End Set
    End Property
    Private _Running As Boolean

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            Dispatcher.Invoke(AddressOf Setup)
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            WebSocket_Enabled.IsEnabled = False
            UDP_Enabled.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            Dim listened As Boolean = CType(Server, Object).Listen()
            If listened Then
                If WebSocket_Enabled.IsChecked Then
                    Await Client1.AsWsClient.Connect("127.0.0.1", ServerPort)
                    Await Client2.AsWsClient.Connect("127.0.0.1", ServerPort)
                ElseIf UDP_Enabled.IsChecked Then
                    Await Client1.AsUdpClient.Connect("127.0.0.1", ServerPort)
                    Await Client2.AsUdpClient.Connect("127.0.0.1", ServerPort)
                Else
                    Dim s As TcpServer = Server.AsTcpServer()
                    Dim t As New Thread(Sub()
                                            While s.IsListening
                                                If Client1.Connection IsNot Nothing AndAlso Client2.Connection IsNot Nothing Then
                                                    Dispatcher.Invoke(Sub()
                                                                          Application.Current.MainWindow.Title = "SocketJack Chat Test - D: " & (Client1.Connection.BytesPerSecondReceived + Client2.Connection.BytesPerSecondReceived + s.Connection.BytesPerSecondReceived).ByteToString() & "/s | U: " & (Client1.Connection.BytesPerSecondSent + Client2.Connection.BytesPerSecondSent + s.Connection.BytesPerSecondSent).ByteToString() & "/s"
                                                                      End Sub)
                                                    Thread.Sleep(50)
                                                End If

                                            End While
                                        End Sub)
                    t.Start()
                    Await Client1.AsTcpClient.Connect("127.0.0.1", ServerPort)
                    Await Client2.AsTcpClient.Connect("127.0.0.1", ServerPort)
                End If
                ButtonC1.IsEnabled = True
                ButtonC2.IsEnabled = True
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Stop Test"
            Else
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Start Test"
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            WebSocket_Enabled.IsEnabled = True
            UDP_Enabled.IsEnabled = True
            ButtonStartStop.Content = "Stopping.."
            Server.As(Of Object)().StopListening()
            Cleanup()
            ButtonStartStop.Content = "Start Test"
            ButtonStartStop.IsEnabled = True
        End If
    End Sub

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.AppendText(If(text.EndsWith(Environment.NewLine), text, text & vbCrLf))
                                   If isAtEnd Then TextLog.ScrollToEnd()
                               End Sub)
    End Sub

    Public Sub LogMessage(from As String, text As String)
        Dim isAtEnd As Boolean = ChatLog.VerticalOffset >= (ChatLog.ExtentHeight - ChatLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   ChatLog.AppendText(String.Format("{0}: {1}{2}", {from, text, vbCrLf}))
                                   If isAtEnd Then ChatLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub ChatBox_PreviewKeyDown(sender As Object, e As Input.KeyEventArgs) Handles ChatMessage1.PreviewKeyDown, ChatMessage2.PreviewKeyDown
        Dim controldown = Keyboard.Modifiers = Input.ModifierKeys.Control
        If e.Key = Key.Enter AndAlso Not controldown Then
            e.Handled = True
            If sender Is ChatMessage1 Then
                SendButton1_Click()
            ElseIf sender Is ChatMessage2 Then
                SendButton2_Click()
            End If
        ElseIf controldown AndAlso e.Key = Key.Enter Then
            e.Handled = True
            If sender Is ChatMessage1 Then
                Dim lastIndex As Integer = ChatMessage1.CaretIndex
                ChatMessage1.Text = ChatMessage1.Text & vbCrLf
                ChatMessage1.CaretIndex = lastIndex + 1
            ElseIf sender Is ChatMessage2 Then
                Dim lastIndex As Integer = ChatMessage2.CaretIndex
                ChatMessage2.Text = ChatMessage2.Text & vbCrLf
                ChatMessage2.CaretIndex = lastIndex + 1
            End If
        End If
    End Sub

    Private Sub SendButton1_Click() Handles SendButton1.Click
        Dim msg As New ChatMessage With {.Text = ChatMessage1.Text}
        If WebSocket_Enabled.IsChecked Then
            Client1.AsWsClient.Send(GetOtherPeer(ClientNumber.Client1), msg)
        ElseIf UDP_Enabled.IsChecked Then
            Client1.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client1), msg)
        Else
            Client1.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client1), msg)
        End If
        ChatMessage1.Text = Nothing
        ChatMessage1.Focus()
    End Sub

    Private Sub SendButton2_Click() Handles SendButton2.Click
        Dim msg As New ChatMessage With {.Text = ChatMessage2.Text}
        If WebSocket_Enabled.IsChecked Then
            Client2.AsWsClient.Send(GetOtherPeer(ClientNumber.Client2), msg)
        ElseIf UDP_Enabled.IsChecked Then
            Client2.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client2), msg)
        Else
            Client2.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client2), msg)
        End If
        ChatMessage2.Text = Nothing
        ChatMessage2.Focus()
    End Sub

    Private Sub Client1_OnDisconnected(e As DisconnectedEventArgs)
        Dispatcher.Invoke(Sub()
                              ChatMessage1.IsEnabled = False
                              SendButton1.IsEnabled = False
                              SendButton1_Pic.IsEnabled = False
                              SendButton1_File.IsEnabled = False
                              ButtonC1.Content = "Connect"
                              If Running Then ButtonC1.IsEnabled = True
                          End Sub)
    End Sub

    Private Sub Client2_OnDisconnected(e As DisconnectedEventArgs)
        Dispatcher.Invoke(Sub()
                              ChatMessage2.IsEnabled = False
                              SendButton2.IsEnabled = False
                              SendButton2_Pic.IsEnabled = False
                              SendButton2_File.IsEnabled = False
                              ButtonC2.Content = "Connect"
                              If Running Then ButtonC2.IsEnabled = True
                          End Sub)
    End Sub

    Public Function GetOtherPeer(LocalClient As ClientNumber) As Identifier
        Select Case LocalClient
            Case ClientNumber.Client1
                If Client1 Is Nothing OrElse Client1.RemoteIdentity Is Nothing Then Return Nothing
                Dim peer = Client1.Peers.Where(Function(x) x.Value.ID <> Client1.RemoteIdentity.ID).FirstOrDefault()
                Return peer.Value
            Case ClientNumber.Client2
                If Client2 Is Nothing OrElse Client2.RemoteIdentity Is Nothing Then Return Nothing
                Dim peer = Client2.Peers.Where(Function(x) x.Value.ID <> Client2.RemoteIdentity.ID).FirstOrDefault()
                Return peer.Value
            Case Else
                Return Nothing
        End Select
    End Function

    Public Enum ClientNumber
        Client1 = 1
        Client2 = 2
    End Enum

    Private Sub SendButton1_Pic_Click(sender As Object, e As RoutedEventArgs) Handles SendButton1_Pic.Click
        Dim ofd As New OpenFileDialog()
        ofd.Title = "Select an image"
        ofd.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
        If ofd.ShowDialog() = DialogResult.OK Then
            Dim filePath As String = ofd.FileName
            If IO.File.Exists(filePath) Then
                Try
                    Dim bmp As New Bitmap(filePath)
                    If WebSocket_Enabled.IsChecked Then
                        Client1.AsWsClient.Send(GetOtherPeer(ClientNumber.Client1), bmp)
                    ElseIf UDP_Enabled.IsChecked Then
                        Client1.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client1), bmp)
                    Else
                        Client1.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client1), bmp)
                    End If
                Catch ex As Exception
                    Log(" ERROR: " & filePath & " is not a valid image file or is corrupt.")
                End Try
            End If
        End If
    End Sub

    Private Sub SendButton2_Pic_Click()
        Dim ofd As New OpenFileDialog()
        ofd.Title = "Select an image"
        ofd.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
        If ofd.ShowDialog() = DialogResult.OK Then
            Dim filePath As String = ofd.FileName
            If (IO.File.Exists(filePath)) Then
                Try
                    Dim bmp As New Bitmap(filePath)
                    If WebSocket_Enabled.IsChecked Then
                        Client2.AsWsClient.Send(GetOtherPeer(ClientNumber.Client2), bmp)
                    ElseIf UDP_Enabled.IsChecked Then
                        Client2.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client2), bmp)
                    Else
                        Client2.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client2), bmp)
                    End If
                Catch ex As Exception
                    Log(" ERROR: " & filePath & " is not a valid image file or is corrupt.")
                End Try
            End If
        End If
    End Sub

    Private Sub SendButton1_File_Click(sender As Object, e As RoutedEventArgs) Handles SendButton1_File.Click
        Dim ofd As New OpenFileDialog()
        ofd.Title = "Select an file"
        ofd.Filter = "Files|*.*"
        If ofd.ShowDialog() = DialogResult.OK Then
            Dim filePath As String = ofd.FileName
            If (IO.File.Exists(filePath)) Then
                Dim fi As New FileContainer(filePath)
                If WebSocket_Enabled.IsChecked Then
                    Client1.AsWsClient.Send(GetOtherPeer(ClientNumber.Client1), fi)
                ElseIf UDP_Enabled.IsChecked Then
                    Client1.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client1), fi)
                Else
                    Client1.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client1), fi)
                End If
            End If
        End If
    End Sub

    Private Sub SendButton2_File_Click()
        Dim ofd As New OpenFileDialog()
        ofd.Title = "Select a file"
        ofd.Filter = "Files|*.*"
        If ofd.ShowDialog() = DialogResult.OK Then
            Dim filePath As String = ofd.FileName
            If (IO.File.Exists(filePath)) Then
                Dim fi As New FileContainer(filePath)
                If WebSocket_Enabled.IsChecked Then
                    Client2.AsWsClient.Send(GetOtherPeer(ClientNumber.Client2), fi)
                ElseIf UDP_Enabled.IsChecked Then
                    Client2.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client2), fi)
                Else
                    Client2.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client2), fi)
                End If
            End If
        End If
    End Sub

    Private Async Sub ButtonC1_Click()
        If WebSocket_Enabled.IsChecked Then
            If Client1.Connected Then
                Client1.AsWsClient().CloseConnection()
            Else
                Await Client1.AsWsClient.Connect("127.0.0.1", ServerPort)
            End If
        ElseIf UDP_Enabled.IsChecked Then
            If Client1.Connected Then
                Client1.AsUdpClient().Disconnect()
            Else
                Await Client1.AsUdpClient.Connect("127.0.0.1", ServerPort)
            End If
        Else
            If Client1.Connected Then
                Client1.AsTcpClient().Disconnect()
            Else
                Await Client1.AsTcpClient.Connect("127.0.0.1", ServerPort)
            End If
        End If
    End Sub

    Private Async Sub ButtonC2_Click(sender As Object, e As RoutedEventArgs) Handles ButtonC2.Click
        If WebSocket_Enabled.IsChecked Then
            If Client2.Connected Then
                Client2.AsWsClient().CloseConnection()
            Else
                Await Client2.AsWsClient.Connect("127.0.0.1", ServerPort)
            End If
        ElseIf UDP_Enabled.IsChecked Then
            If Client2.Connected Then
                Client2.AsUdpClient().Disconnect()
            Else
                Await Client2.AsUdpClient.Connect("127.0.0.1", ServerPort)
            End If
        Else
            If Client2.Connected Then
                Client2.AsTcpClient().Disconnect()
            Else
                Await Client2.AsTcpClient.Connect("127.0.0.1", ServerPort)
            End If
        End If
    End Sub
    Dim sending As Boolean = False
    Dim lastTimeout As DateTime = DateTime.UtcNow

    Private Sub ChatTest_PreviewMouseMove(sender As Object, e As Input.MouseEventArgs) Handles Me.PreviewMouseMove
        If Not sending AndAlso Client1 IsNot Nothing AndAlso Client2 IsNot Nothing AndAlso Client1.Connected AndAlso Client2.Connected AndAlso
            Client1.RemoteIdentity IsNot Nothing AndAlso Client2.RemoteIdentity IsNot Nothing AndAlso
            DateTime.UtcNow >= lastTimeout.AddMilliseconds(Client1.Options.Timeout) Then
            lastTimeout = DateTime.UtcNow
            sending = True
            'Task.Run(Sub()
            Dispatcher.InvokeAsync(Sub()
                                       Dim pos As pPos = New pPos With {.X = CInt(e.GetPosition(Me).X), .Y = CInt(e.GetPosition(Me).Y)}
                                       If WebSocket_Enabled.IsChecked Then
                                           Client1.AsWsClient.Send(GetOtherPeer(ClientNumber.Client1), pos)
                                           Client2.AsWsClient.Send(GetOtherPeer(ClientNumber.Client2), pos)
                                       ElseIf UDP_Enabled.IsChecked Then
                                           Client1.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client1), pos)
                                           Client2.AsUdpClient.Send(GetOtherPeer(ClientNumber.Client2), pos)
                                       Else
                                           Client1.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client1), pos)
                                           Client2.AsTcpClient.Send(GetOtherPeer(ClientNumber.Client2), pos)
                                       End If
                                       sending = False
                                   End Sub)
            '      End Sub)
        End If
    End Sub

    Private Sub C1BW_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double)) Handles C1BW.ValueChanged
        If Not WebSocket_Enabled.IsChecked AndAlso Server IsNot Nothing AndAlso Server.Options IsNot Nothing AndAlso Client1 IsNot Nothing AndAlso Client1.Options IsNot Nothing AndAlso Client2 IsNot Nothing AndAlso Client2.Options IsNot Nothing AndAlso Client2.Connected Then
            Client1.Options.MaximumDownloadMbps = C1BW.Value
            Client1.Options.MaximumUploadMbps = C1BW.Value
            If C1BW.ToolTip Is Nothing Then
                C1BW.ToolTip = New System.Windows.Controls.ToolTip() With {.Content = C1BW.Value & " Mbps", .IsOpen = True, .StaysOpen = False}
            Else
                DirectCast(C1BW.ToolTip, Controls.ToolTip).Content = C1BW.Value & " Mbps"
                DirectCast(C1BW.ToolTip, Controls.ToolTip).IsOpen = True
            End If
        End If
    End Sub

    Private Sub C2BW_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double)) Handles C2BW.ValueChanged
        If Not WebSocket_Enabled.IsChecked AndAlso Server IsNot Nothing AndAlso Server.Options IsNot Nothing AndAlso Client1 IsNot Nothing AndAlso Client1.Options IsNot Nothing AndAlso Client2 IsNot Nothing AndAlso Client2.Options IsNot Nothing AndAlso Client2.Connected Then
            Client2.Options.MaximumDownloadMbps = C2BW.Value
            Client2.Options.MaximumUploadMbps = C2BW.Value
            If C2BW.ToolTip Is Nothing Then
                C2BW.ToolTip = New System.Windows.Controls.ToolTip() With {.Content = C2BW.Value & " Mbps", .IsOpen = True, .StaysOpen = False}
            Else
                DirectCast(C2BW.ToolTip, Controls.ToolTip).Content = C2BW.Value & " Mbps"
                DirectCast(C2BW.ToolTip, Controls.ToolTip).IsOpen = True
            End If
        End If
    End Sub

    Private Sub SBW_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double)) Handles SBW.ValueChanged
        If Not WebSocket_Enabled.IsChecked AndAlso Server IsNot Nothing AndAlso Server.Options IsNot Nothing AndAlso Client1 IsNot Nothing AndAlso Client1.Options IsNot Nothing AndAlso Client2 IsNot Nothing AndAlso Client2.Options IsNot Nothing Then
            Server.Options.MaximumDownloadMbps = SBW.Value
            Server.Options.MaximumUploadMbps = SBW.Value
            If SBW.ToolTip Is Nothing Then
                SBW.ToolTip = New System.Windows.Controls.ToolTip() With {.Content = SBW.Value & " Mbps", .IsOpen = True, .StaysOpen = False}
            Else
                DirectCast(SBW.ToolTip, Controls.ToolTip).Content = SBW.Value & " Mbps"
                DirectCast(SBW.ToolTip, Controls.ToolTip).IsOpen = True
            End If
        End If

    End Sub

    Private Sub rRate_ValueChanged()
        If Not WebSocket_Enabled.IsChecked AndAlso Server IsNot Nothing AndAlso Server.Options IsNot Nothing AndAlso Client1 IsNot Nothing AndAlso Client1.Options IsNot Nothing AndAlso Client2 IsNot Nothing AndAlso Client2.Options IsNot Nothing Then
            Server.Options.Fps = rRate.Value
            Client1.Options.Fps = rRate.Value
            Client2.Options.Fps = rRate.Value
            If rRate.ToolTip Is Nothing Then
                rRate.ToolTip = New System.Windows.Controls.ToolTip() With {.Content = rRate.Value & " FPS", .IsOpen = True, .StaysOpen = False}
            Else
                DirectCast(rRate.ToolTip, Controls.ToolTip).Content = rRate.Value & " FPS"
                DirectCast(rRate.ToolTip, Controls.ToolTip).IsOpen = True
            End If

        End If
    End Sub

    Private Sub SBW_ManipulationCompleted(sender As Object, e As ManipulationCompletedEventArgs) Handles SBW.ManipulationCompleted, C2BW.ManipulationCompleted, C1BW.ManipulationCompleted
        If sender.ToolTip IsNot Nothing Then
            DirectCast(sender.ToolTip, Controls.ToolTip).IsOpen = False
        End If

    End Sub

    Private Sub WebSocket_Enabled_Checked(sender As Object, e As RoutedEventArgs) Handles WebSocket_Enabled.Checked
        If UDP_Enabled IsNot Nothing Then UDP_Enabled.IsChecked = False
    End Sub

    Private Sub UDP_Enabled_Checked(sender As Object, e As RoutedEventArgs)
        If WebSocket_Enabled IsNot Nothing Then WebSocket_Enabled.IsChecked = False
    End Sub

    Private Sub ChatTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        AddHandler UDP_Enabled.Checked, AddressOf UDP_Enabled_Checked
    End Sub

#End Region

End Class


