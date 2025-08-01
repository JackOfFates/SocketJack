Imports System.Collections.Concurrent
Imports System.Net
Imports System.Threading
Imports SocketJack
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P


Public Class WebSocketTest
    Implements ITest

    Private ServerPort As Integer = 8096


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
        Server.RegisterCallback(Of TextObject)(AddressOf LogServer_ReceivedTextObject)
    End Sub
#End Region
    Private Sub RunTask(T As Action)
        Task.Run(T).ConfigureAwait(False)
    End Sub

    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        InitServer()
    End Sub

#Region "UI"

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

    Private Sub LogServer_ReceivedTextObject(e As ReceivedEventArgs(Of TextObject))
        Log("[" & e.sender.Name & "] " & e.sender.Connection.GetMetaData("Username") & ": " & e.Object.Text)
    End Sub
    Private Sub LogClient_ReceivedTextObject(e As ReceivedEventArgs(Of TextObject))
        Log("[" & e.sender.Name & "] " & e.From.Metadata("Username") & ": " & e.Object.Text)
    End Sub
    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
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
    Private clients As New ConcurrentDictionary(Of Guid, TcpConnection)
    Private Sub ButtonAddClient_Click(sender As Object, e As RoutedEventArgs) Handles ButtonAddClient.Click
        RunTask(
            Async Sub()
                Dim newClient As New WebSocketClient(TcpOptions.DefaultOptions, "Client" & clientCount)
                With newClient
                    .Options.Logging = Server.Options.Logging
                    .Options.LogReceiveEvents = Server.Options.LogReceiveEvents
                    .Options.UseCompression = Server.Options.UseCompression
                    .Options.CompressionAlgorithm = Server.Options.CompressionAlgorithm
                    .RegisterCallback(Of TextObject)(AddressOf LogClient_ReceivedTextObject)
                End With
                AddHandler newClient.LogOutput, AddressOf Log
                clientCount += 1
                Await newClient.ConnectAsync(New Uri("ws://localhost:" & ServerPort))
                RunTask(Async Sub()
                            Await Task.Delay(500)
                            newClient.SendPeerBroadcast(New TextObject() With {.Text = "TEST"})
                        End Sub)
            End Sub)
    End Sub

    Private Sub ButtonDcLast_Click(sender As Object, e As RoutedEventArgs) Handles ButtonDcLast.Click
        If clients.Count > 0 Then
            Dim lastClient As TcpConnection = clients.Last().Value
            lastClient.CloseConnection()
            clients.Remove(lastClient.ID)
        End If
        If clients.Count = 0 Then
            ButtonDcLast.IsEnabled = False
            ButtonDcAll.IsEnabled = False
        End If
    End Sub

    Private Sub ButtonDcAll_Click(sender As Object, e As RoutedEventArgs) Handles ButtonDcAll.Click
        If clients.Count > 0 Then
            Dim clientArray As TcpConnection() = clients.Values.ToArray()
            For Each client In clientArray
                client.CloseConnection()
                clients.Remove(client.ID)
            Next
        End If
        If clients.Count = 0 Then
            Dispatcher.Invoke(
                Sub()
                    ButtonDcLast.IsEnabled = False
                    ButtonDcAll.IsEnabled = False
                End Sub)
        End If
    End Sub

    Private Sub Server_ClientConnected(e As ConnectedEventArgs) Handles Server.ClientConnected
        clients.Add(e.Connection.ID, e.Connection)
        e.Connection.SetMetaData("Username", "Client" & clientCount - 1)
        If clients.Count > 0 Then
            Dispatcher.Invoke(
            Sub()
                ButtonDcLast.IsEnabled = True
                ButtonDcAll.IsEnabled = True
            End Sub)
        End If
    End Sub

    Private Sub Server_ClientDisconnected(e As DisconnectedEventArgs) Handles Server.ClientDisconnected
        clients.Remove(e.Connection.ID)
        If clients.Count = 0 Then
            Dispatcher.Invoke(
                Sub()
                    ButtonDcLast.IsEnabled = False
                    ButtonDcAll.IsEnabled = False
                End Sub)
        End If
    End Sub


#End Region

End Class

Public Class TextObject
    Public Property Text As String = "test"
End Class