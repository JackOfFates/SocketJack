Imports System.Collections.Concurrent
Imports System.Net
Imports System.Threading
Imports SocketJack
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P
Imports SocketJack.Net.WebSockets


Public Class WebSocketTest
    Implements ITest

    Private ServerPort As Integer = 8096

#Region "Test"
    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        InitServer()
    End Sub

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "WebSockets"
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
#End Region

#Region "Server"

    Public WithEvents Server As WebSocketServer
    Public Sub InitServer()
        Server = New WebSocketServer(ServerPort, "Server")
        With Server.Options
            .Logging = True
            .LogReceiveEvents = True
            .UseCompression = True
            .CompressionAlgorithm.CompressionLevel = IO.Compression.CompressionLevel.SmallestSize
        End With
        Server.RegisterCallback(Of WelcomeMessage)(AddressOf Server_ReceivedTextObject)
    End Sub
#End Region

    Private Sub RunTask(T As Action)
        Task.Run(T).ConfigureAwait(False)
    End Sub

    Private Function RunTaskAsync(T As Action) As Task
        Return Task.Run(T)
    End Function


#Region "UI"
    Private Sub Server_ReceivedTextObject(e As ReceivedEventArgs(Of WelcomeMessage))
    End Sub
    Private Async Sub Client_ReceivedWelcomeMessage(e As ReceivedEventArgs(Of WelcomeMessage))
        Log(String.Format("[{0}] '{1}' from {2}", {e.Connection.Parent.Name, e.Object.Text, Await e.From.GetMetaData("Username")}))
    End Sub

    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
                ButtonAddClient.IsEnabled = True
                Running = True
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Stop Test"
            Else
                Log("Failed to start listening.")
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Start Test"
                Running = False
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            clientCount = 1
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Stopping.."
            Server.StopListening()
            Running = False
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

    Public Sub Log(Text As String) Handles Server.LogOutput
        Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.AppendText(If(Text.EndsWith(Environment.NewLine), Text, Text & vbCrLf))
                                   If isAtEnd Then TextLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private clientCount As Integer = 1
    Private Sub ButtonAddClient_Click(sender As Object, e As RoutedEventArgs) Handles ButtonAddClient.Click
        RunTask(
            Async Sub()
                Dim newClient As New WebSocketClient(TcpOptions.DefaultOptions, "Client" & clientCount)
                With newClient
                    .Options.Logging = Server.Options.Logging
                    .Options.LogReceiveEvents = Server.Options.LogReceiveEvents
                    .Options.UseCompression = Server.Options.UseCompression
                    .Options.CompressionAlgorithm = Server.Options.CompressionAlgorithm
                    .RegisterCallback(Of WelcomeMessage)(AddressOf Client_ReceivedWelcomeMessage)
                End With
                AddHandler newClient.LogOutput, AddressOf Log
                clientCount += 1
                AddHandler newClient.OnIdentified, AddressOf newClient_Identified
                AddHandler newClient.PeerConnected, AddressOf newClient_PeerConnected
                AddHandler newClient.OnDisconnected, AddressOf newClient_Disconnected
                Await newClient.ConnectAsync(New Uri("ws://localhost:" & ServerPort))
            End Sub)
    End Sub

    Private Sub newClient_Disconnected(e As DisconnectedEventArgs)

    End Sub

    Private Sub newClient_PeerConnected(sender As ISocket, Peer As Identifier)
        If Peer.Action <> PeerAction.LocalIdentity Then
            sender.Send(Peer, New WelcomeMessage() With {.Text = "TEST"})
        End If
    End Sub

    Private Sub newClient_Identified(sender As ISocket, Peer As Identifier)
        'sender.SendBroadcast(New WelcomeMessage() With {.Text = "TEST"})
    End Sub

    Private Sub ButtonDcLast_Click(sender As Object, e As RoutedEventArgs) Handles ButtonDcLast.Click
        If Server.Clients.Count > 0 Then
            Dim lastClient As TcpConnection = Server.Clients.Last().Value
            lastClient.CloseConnection()
        End If
        If Server.Clients.Count = 0 Then
            ButtonDcLast.IsEnabled = False
            ButtonDcAll.IsEnabled = False
        End If
    End Sub

    Private Sub ButtonDcAll_Click(sender As Object, e As RoutedEventArgs) Handles ButtonDcAll.Click
        If Server.Clients.Count > 0 Then
            Dim clientArray As TcpConnection() = Server.Clients.Values.ToArray()
            For Each client In clientArray
                client.CloseConnection()
            Next
        End If
        If Server.Clients.Count = 0 Then
            Dispatcher.Invoke(
                Sub()
                    ButtonDcLast.IsEnabled = False
                    ButtonDcAll.IsEnabled = False
                End Sub)
        End If
    End Sub

    Private Sub Server_ClientConnected(e As ConnectedEventArgs) Handles Server.ClientConnected
        e.Connection.SetMetaData("Username", "Client" & clientCount - 1)
        If Server.Clients.Count > 0 Then
            Dispatcher.Invoke(
            Sub()
                ButtonDcLast.IsEnabled = True
                ButtonDcAll.IsEnabled = True
            End Sub)
        End If
    End Sub

    Private Sub Server_ClientDisconnected(e As DisconnectedEventArgs) Handles Server.ClientDisconnected
        If Server.Clients.Count = 0 Then
            Dispatcher.Invoke(
                Sub()
                    ButtonDcLast.IsEnabled = False
                    ButtonDcAll.IsEnabled = False
                End Sub)
        End If
    End Sub

    Private Sub Server_OnStoppedListening() Handles Server.OnStoppedListening
        ButtonAddClient.IsEnabled = False
        ButtonDcAll.IsEnabled = False
        ButtonDcLast.IsEnabled = False
    End Sub


#End Region

End Class

Public Class WelcomeMessage
    Public Property Text As String = "test"
End Class