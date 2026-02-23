Imports System.Collections.Concurrent
Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Net

Public Class MaxClientsTest
    Implements ITest

    Public MaxClients As Integer = 10000
    Public Clients As New ConcurrentDictionary(Of Guid, ISocket)
    Public ServerPort As Integer = NIC.FindOpenPort(7500, 8000).Result

    Public ServerOptions As New NetworkOptions With {.Logging = False, .UsePeerToPeer = False, .UpdateConsoleTitle = True}
    Public ClientOptions As New NetworkOptions With {.Logging = False, .UsePeerToPeer = False, .UpdateConsoleTitle = False}
    Public Server As ISocket
    Private _UseUdp As Boolean

    Private Sub Cleanup()
        For Each kvp In Clients
            kvp.Value.Dispose()
        Next
        Clients.Clear()
        If Server IsNot Nothing Then
            Server.Dispose()
            Server = Nothing
        End If
    End Sub

    Private Sub Setup()
        Cleanup()
        _UseUdp = UDP_Enabled.IsChecked
        If _UseUdp Then
            Dim s As New UdpServer(ServerPort, String.Format("{0}Server", {TestName})) With {.Options = ServerOptions}
            Server = s
            AddHandler s.ClientConnected, AddressOf Server_ClientConnected
            AddHandler s.ClientDisconnected, AddressOf Server_ClientDisconnected
            AddHandler s.OnError, AddressOf Server_OnError
            AddHandler s.LogOutput, AddressOf Server_LogOutput
        Else
            Dim s As New TcpServer(ServerPort, String.Format("{0}Server", {TestName})) With {.Options = ServerOptions}
            Server = s
            AddHandler s.ClientConnected, AddressOf Server_ClientConnected
            AddHandler s.ClientDisconnected, AddressOf Server_ClientDisconnected
            AddHandler s.OnError, AddressOf Server_OnError
            AddHandler s.LogOutput, AddressOf Server_LogOutput
        End If
    End Sub

#Region "Properties"
    Public Property TestName As String = "MaxConnections" Implements ITest.TestName

    Public Property Connected As Integer
        Get
            Return _Connected
        End Get
        Set(value As Integer)
            _Connected = value
            Dispatcher.InvokeAsync(Sub() LabelClients.Text = "Connected" & vbCrLf & value)
        End Set
    End Property
    Private _Connected As Integer

    Public Property Connects As Integer
        Get
            Return _Connects
        End Get
        Set(value As Integer)
            _Connects = value
            Dispatcher.InvokeAsync(Sub() LabelConnects.Text = "Connect #" & vbCrLf & value)
        End Set
    End Property
    Private _Connects As Integer

    Public Property Disconnects As Integer
        Get
            Return _Disconnects
        End Get
        Set(value As Integer)
            _Disconnects = value
            Dispatcher.InvokeAsync(Sub() LabelDisconnects.Text = "D/C #" & vbCrLf & value)
        End Set
    End Property
    Private _Disconnects As Integer

    Public Property TotalClients As Integer
        Get
            Return _TotalClients
        End Get
        Set(value As Integer)
            _TotalClients = value
            Dispatcher.InvokeAsync(Sub() LabelClients.Text = "Clients" & vbCrLf & value)
        End Set
    End Property

    Public Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return _AutoStart
        End Get
        Set(value As Boolean)
            _AutoStart = value
        End Set
    End Property
    Private _AutoStart As Boolean = True

    Public Property Running As Boolean Implements ITest.Running
        Get
            If Server Is Nothing Then Return False
            Return CType(Server, Object).IsListening
        End Get
        Set(value As Boolean)
            If value Then
                ITest_StartTest()
            Else
                ITest_StopTest()
            End If
        End Set
    End Property
    Private _TotalClients As Integer
#End Region

#Region "UI"

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Public Sub Log(text As String, Optional AppendNewLine As Boolean = False)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.Invoke(Sub()
                              TextboxLog.AppendText(If(TextboxLog.Text = String.Empty, String.Empty, vbCrLf) & text & If(AppendNewLine, Environment.NewLine, String.Empty))
                              If isAtEnd Then TextboxLog.ScrollToEnd()
                          End Sub)
    End Sub
    Public Sub LogAsync(text As String, Optional AppendNewLine As Boolean = False)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(If(TextboxLog.Text = String.Empty, String.Empty, vbCrLf) & text & If(AppendNewLine, Environment.NewLine, String.Empty))
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

#End Region

#Region "Events"

    Private Sub Server_OnError(e As ErrorEventArgs)
        LogAsync(e.Exception.Message & vbCrLf & e.Exception.StackTrace)
    End Sub

    Private Sub Server_LogOutput(text As String)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.Invoke(Sub()
                              TextboxLog.AppendText(text)
                              If isAtEnd Then TextboxLog.ScrollToEnd()
                          End Sub)
    End Sub

    Private Sub Server_ClientConnected(e As ConnectedEventArgs)
        Interlocked.Increment(Connects)
        Interlocked.Increment(TotalClients)
    End Sub

    Private Sub Server_ClientDisconnected(e As DisconnectedEventArgs)
        Interlocked.Increment(Disconnects)
        Interlocked.Decrement(TotalClients)
    End Sub

#End Region

    Private cancelTest As Boolean = False
    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            Dispatcher.Invoke(AddressOf Setup)
            UDP_Enabled.IsEnabled = False
            Dim listened As Boolean = CType(Server, Object).Listen()
            If listened Then
                cancelTest = False
                Clients.Clear()
                Log("Starting test.", True)
                ButtonStartStop.Content = "Stop Test"
                Task.Run(Sub()
                             Parallel.For(1, MaxClients, Sub(index)
                                                             If cancelTest Then Return
                                                             CreateClientAndConnect(index).ConfigureAwait(False)
                                                         End Sub)
                         End Sub)
            Else
                Log(String.Format("Server failed to listen on port {0}.", {CType(Server, Object).Port}))
                UDP_Enabled.IsEnabled = True
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            cancelTest = True
            ButtonStartStop.Content = "Start Test"
            CType(Server, Object).StopListening()
            Cleanup()
            UDP_Enabled.IsEnabled = True
            Log("Stopping test.", True)
        End If
    End Sub

    Private Async Function CreateClientAndConnect(index As Integer) As Task(Of Boolean)
        If _UseUdp Then
            Dim Client As New UdpClient(ClientOptions, String.Format("{0}Client[{1}]", {TestName, index}))
            Clients.TryAdd(Client.InternalID, Client)
            Return Await Client.Connect("127.0.0.1", ServerPort)
        Else
            Dim Client As New TcpClient(ClientOptions, String.Format("{0}Client[{1}]", {TestName, index}))
            Clients.TryAdd(Client.InternalID, Client)
            Return Await Client.Connect("127.0.0.1", ServerPort)
        End If
    End Function


End Class
