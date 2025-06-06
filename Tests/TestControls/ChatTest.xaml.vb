﻿Imports System.IO.Compression
Imports System.Security.Cryptography.X509Certificates
Imports Microsoft.SqlServer
Imports SocketJack.Compression
Imports SocketJack.Management
Imports SocketJack.Networking
Imports SocketJack.Networking.P2P
Imports SocketJack.Networking.Shared

''' <summary>
''' This test simulates a simple chat application where two clients can send messages to each other through a server.
''' The server is responsible for redirecting messages between clients.
''' The clients can send messages and receive them from other clients.
''' The server should use SSL for secure communication, compression is enabled for message size reduction.
''' </summary>
Public Class ChatTest
    Implements ITest

    Private ServerPort As Integer = 7543
    Public WithEvents Server As TcpServer
    Public WithEvents Client1 As TcpClient
    Public WithEvents Client2 As TcpClient
    Public ServerOptions As New TcpOptions()
    Public ClientOptions As New TcpOptions()

    Private Sub SetupTcpOptions()
        With ServerOptions
            .Logging = True
            .LogReceiveEvents = True
            .LogSendEvents = True
            .UseCompression = True
            .CompressionAlgorithm.CompressionLevel = CompressionLevel.SmallestSize
            '.UseSsl = True
        End With
        With ClientOptions
            .Logging = True
            .LogReceiveEvents = True
            .LogSendEvents = True
            .UseCompression = True
            .CompressionAlgorithm.CompressionLevel = CompressionLevel.SmallestSize
            '.UseSsl = True
        End With
    End Sub

    Private Sub ChatTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        SetupTcpOptions()
        Dim KeyHostName As String = "MY-KEY-HOSTNAME.com" ' Replace with your actual SSL certificate hostname
        Server = New TcpServer(ServerPort, "ChatServer") With {.Options = ServerOptions, .SslTargetHost = KeyHostName}

        ' Add the ChatMessage type to the whitelist since the server is just redirecting and not handling it
        ' For doing things like blocking other users or filtering messages you should handle it on the server.
        ' Set e.CancelPeerRedirect = true to stop the message from being sent to the recipient.
        Server.Options.Whitelist.Add(GetType(ChatMessage))
        Server.RegisterCallback(Of LoginObj)(AddressOf Server_ClientLogin)

        ' Set the SSL certificate for the server. 
        ' Make sure to replace the path and password with your actual certificate details.
        ' NEVER hardcode the password in production code on the client side.
        'Server.SslCertificate = New X509Certificate2("...\Location on drive\Private Key.pfx", "MY KEY PASSWORD")

        Client1 = New TcpClient("ChatClient1") With {.Options = ClientOptions, .SslTargetHost = KeyHostName}
        Client2 = New TcpClient("ChatClient2") With {.Options = ClientOptions, .SslTargetHost = KeyHostName}

        ' Handle messages on the clients
        Client1.RegisterCallback(Of ChatMessage)(AddressOf Clients_ReceivedMessage)
        Client2.RegisterCallback(Of ChatMessage)(AddressOf Clients_ReceivedMessage)
    End Sub

    Private Sub Clients_ReceivedMessage(args As ReceivedEventArgs(Of ChatMessage))
        'LogMessage(args.From.Tag, args.Object.Text)
        LogMessage(args.Object.From, args.Object.Text)
    End Sub

    Private Sub Server_ClientLogin(e As ReceivedEventArgs(Of LoginObj))
        ' When the server receives a login object, we can set the tag for the connection.
        ' Usernames are just an example, you can use any identifier you want.
        e.Connection.SetTag(e.Object.UserName)
    End Sub

    Private Sub Client1_OnIdentified(ByRef LocalIdentity As PeerIdentification) Handles Client1.OnIdentified
        ' When the client is identified, we can send the login object to the server.
        ' This is a dummy object for your login, you can replace it with your actual login logic.
        Client1.Send(New LoginObj With {.UserName = "Client1"})
    End Sub

    Private Sub Client2_OnIdentified(ByRef LocalIdentity As PeerIdentification) Handles Client2.OnIdentified
        ' When the client is identified, we can send the login object to the server.
        ' This is a dummy object for your login, you can replace it with your actual login logic.
        Client2.Send(New LoginObj With {.UserName = "Client2"})
    End Sub


#Region "Chat Classes"
    Public Class ChatMessage
        Public Property Text As String
        Public Property From As String
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
            Return Server.isListening
        End Get
        Set(value As Boolean)
            If Server Is Nothing Then Return
            If value AndAlso Not Server.isListening Then
                ITest_StartTest()
            ElseIf Not value AndAlso Server.isListening Then
                ITest_StopTest()
            End If
        End Set
    End Property
    Private _Running As Boolean

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
                Await Client1.Connect("127.0.0.1", ServerPort)
                Await Client2.Connect("127.0.0.1", ServerPort)
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
            ButtonStartStop.Content = "Stopping.."
            Server.StopListening()
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

    Public Sub Log(text As String) Handles Server.LogOutput, Client1.LogOutput, Client2.LogOutput
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
        Dim OtherPeer As PeerIdentification = Client1.Peers.Where(Function(x) x.Value.ID <> Client1.RemoteIdentity.ID).FirstOrDefault().Value
        Dim msg As New ChatMessage With {.Text = ChatMessage1.Text, .From = "Client1"}
        Client1.Send(OtherPeer, msg)
        ChatMessage1.Text = Nothing
        ChatMessage1.Focus()
    End Sub

    Private Sub SendButton2_Click() Handles SendButton2.Click
        Dim OtherPeer As PeerIdentification = Client2.Peers.Where(Function(x) x.Value.ID <> Client2.RemoteIdentity.ID).FirstOrDefault().Value
        Dim msg As New ChatMessage With {.Text = ChatMessage2.Text, .From = "Client2"}
        Client2.Send(OtherPeer, msg)
        ChatMessage2.Text = Nothing
        ChatMessage2.Focus()
    End Sub

    Private Sub Client1_OnConnected(sender As Object) Handles Client1.OnConnected
        ChatMessage1.IsEnabled = True
        SendButton1.IsEnabled = True
    End Sub

    Private Sub Client2_OnConnected(sender As Object) Handles Client2.OnConnected
        ChatMessage2.IsEnabled = True
        SendButton2.IsEnabled = True
    End Sub

#End Region

End Class


