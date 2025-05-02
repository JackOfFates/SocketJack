Imports System.Collections.Concurrent
Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Networking
Imports SocketJack.Networking.[Shared]

Public Class MaxClientsTest
    Implements ITest

    Public MaxClients As Integer = 10000
    Public Clients As New ConcurrentDictionary(Of Guid, TcpClient)
    Public ServerPort As Integer = NIC.FindOpenPort(7500, 8000)

    Public WithEvents Server As New TcpServer(ServerPort, String.Format("{0}Server", {TestName})) With {.Logging = False, .UpdateConsoleTitle = True}

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
            Return Server.isListening
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

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(vbCrLf & text)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

#End Region

#Region "Events"

    Private Sub Server_OnError(e As ErrorEventArgs) Handles Server.OnError
        Log(e.Exception.Message & vbCrLf & e.Exception.StackTrace)
    End Sub

    Private Sub Server_LogOutput(text As String) Handles Server.LogOutput
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(text)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub Server_ClientConnected(e As ConnectedEventArgs) Handles Server.ClientConnected
        Interlocked.Increment(Connects)
        Interlocked.Increment(TotalClients)
    End Sub

    Private Sub Server_OnDisconnected(e As DisconnectedEventArgs) Handles Server.OnDisconnected
        Interlocked.Increment(Disconnects)
        Interlocked.Decrement(TotalClients)
    End Sub

#End Region

    Private cancelTest As Boolean = False
    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            If Server.Listen() Then
                cancelTest = False
                Clients.Clear()
                Log("Test Started.")
                ButtonStartStop.Content = "Stop Test"
                Dim t As New Thread(Async Sub()
                                        For i As Integer = 0 To MaxClients
                                            If cancelTest Then Exit For
                                            Await CreateClientAndConnect(i)
                                        Next
                                    End Sub)
                t.Start()
                'Parallel.For(1, MaxClients, Async Sub(index)
                '                                Await CreateClientAndConnect(index)
                '                            End Sub)
            Else
                Log(String.Format("Server failed to listen on port {0}.", {Server.Port}))
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            cancelTest = True
            ButtonStartStop.Content = "Start Test"
            Server.StopListening()
            Log("Test Stopped.")
        End If
    End Sub

    Private Async Function CreateClientAndConnect(index As Integer) As Task(Of Boolean)
        Dim Client As New TcpClient(False, String.Format("{0}Client[{1}]", {TestName, index}))
        Clients.Add(Client.InternalID, Client)
        Return Await Client.Connect("127.0.0.1", ServerPort)
    End Function


End Class
