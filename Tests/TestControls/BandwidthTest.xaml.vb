Imports System.Text
Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Management
Imports SocketJack.Networking
Imports SocketJack.Networking.[Shared]
Imports SocketJack.Serialization

Public Class BandwidthTest
    Implements ITest

    'Private Sub BandwidthTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
    '    Base.TestName = "Bandwidth"
    'End Sub

    Public Property ServerPort As Integer = NIC.FindOpenPort(7500, 8000)
    Public WithEvents Server As New TcpServer(ServerPort, String.Format("{0}Server", {TestName})) With {.Options = New TcpOptions With {.Logging = True}}
    Public WithEvents Client As New TcpClient(String.Format("{0}Client", {TestName})) With {.Options = New TcpOptions With {.Logging = True, .UpdateConsoleTitle = True}}

    Public ReadOnly Property TestName As String = "Bandwidth Test" Implements ITest.TestName

    Public Property Connected As Integer
        Get
            Return _Connected
        End Get
        Set(value As Integer)
            _Connected = value
            If LabelClients Is Nothing Then Return
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
            If LabelConnects Is Nothing Then Return
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
            If LabelDisconnects Is Nothing Then Return
            Dispatcher.InvokeAsync(Sub() LabelDisconnects.Text = "D/C #" & vbCrLf & value)
        End Set
    End Property
    Private _Disconnects As Integer

    Public Property Sent As Long
        Get
            Return _Sent
        End Get
        Set(value As Long)
            _Sent += value
            If LabelSent Is Nothing Then Return
            Dispatcher.InvokeAsync(Sub() LabelSent.Text = "Sent" & vbCrLf & _Sent.ByteToString(2))
        End Set
    End Property
    Private _Sent As Long

    Public Property Received As Long
        Get
            Return _Received
        End Get
        Set(value As Long)
            _Received += value
            If LabelReceived Is Nothing Then Return
            Dispatcher.InvokeAsync(Sub() LabelReceived.Text = "Received" & vbCrLf & _Received.ByteToString(2))
        End Set
    End Property
    Private _Received As Long

    Public Property ReceivedObjects As Integer
        Get
            Return _ReceivedObjects
        End Get
        Set(value As Integer)
            _ReceivedObjects += value
            If LabelReceivedObjects Is Nothing Then Return
            Dispatcher.InvokeAsync(Sub() LabelReceivedObjects.Text = "Objects" & vbCrLf & _ReceivedObjects)
        End Set
    End Property
    Private _ReceivedObjects As Integer

    Public Property TotalClients As Integer
        Get
            Return _TotalClients
        End Get
        Set(value As Integer)
            _TotalClients = value
            If LabelClients Is Nothing Then Return
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
    Private _AutoStart As Boolean = False

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

    Private _TotalClients As Integer

    Private Sub Server_ClientConnected(e As ConnectedEventArgs) Handles Server.ClientConnected
        Interlocked.Increment(Connects)
        Interlocked.Increment(TotalClients)
    End Sub

    Private Sub Server_OnDisconnected(e As DisconnectedEventArgs) Handles Server.OnDisconnected
        Interlocked.Increment(Disconnects)
        Interlocked.Decrement(TotalClients)
    End Sub

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(text & vbCrLf)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            ButtonStartStop.IsEnabled = False
            Log("Test Starting...")
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
                Sent = 0
                Received = 0
                Await Client.Connect("127.0.0.1", ServerPort)
                ButtonStartStop.IsEnabled = True
                Log("Test Started.")
                ButtonStartStop.Content = "Stop Test"
            Else
                Log("Test failed to start.")
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Start Test"
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Stopping.."
            Server.StopListening()
            ButtonStartStop.Content = "Start Test"
            ButtonStartStop.IsEnabled = True
            Log("Test Stopped.")
        End If
    End Sub

    Private Sub Server_OnError(e As ErrorEventArgs) Handles Server.OnError
        Log(e.Exception.Message & e.Exception.StackTrace)
    End Sub

    Private Sub Server_LogOutput(text As String) Handles Server.LogOutput
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(text)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub
    Dim Worker As Thread

    Private Sub WorkerThread(obj As Object)
        Do While Running
            If Client.Connected Then
                Client.Send(BandwidthObject.Create())
            End If
            'If Server.isListening Then
            '    Server.SendBroadcast(BandwidthObject.Create())
            'End If
            Thread.Sleep(1)
        Loop
    End Sub

    Private Sub Client_OnConnected(sender As Object) Handles Client.OnConnected
        Worker = New Thread(AddressOf WorkerThread)
        Worker.Start()
    End Sub

    Private Sub Tcp_OnSent(e As SentEventArgs) Handles Client.OnSent, Server.OnSent
        Sent = e.BytesSent
    End Sub

    Private Sub Tcp_OnReceive(ByRef e As ReceivedEventArgs(Of Object)) Handles Client.OnReceive, Server.OnReceive
        Received = e.BytesReceived
    End Sub

    Private Sub SliderUnitSize_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double)) Handles SliderUnitSize.ValueChanged
        BandwidthObject.UnitSize = CInt(SliderUnitSize.Value)
    End Sub

    Private Sub Client_LogOutput(text As String) Handles Client.LogOutput
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(text)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

    Dim Dragging As Boolean = False

    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.

        ' Alternative way to white-list types
        ' Server.Whitelist.AddType(GetType(BandwidthObject))
        With Server.Options
            .LogReceiveEvents = False
            .MaximumDownloadMbps = 0
        End With
        Client.Options = Server.Options
        Server.RegisterCallback(Of BandwidthObject)(AddressOf Received_BandwidthObject)
        Client.RegisterCallback(Of BandwidthObject)(AddressOf Received_BandwidthObject)
    End Sub

    Private Sub Received_BandwidthObject(e As ReceivedEventArgs(Of BandwidthObject))
        ReceivedObjects = 1
        ' Dim obj As BandwidthObject = e.obj

    End Sub

    Private Sub Slider_DragStarted()
        Dragging = True

    End Sub

    Private Sub Slider_DragCompleted()
        Dragging = False
        BandwidthObject.UnitSize = CInt(SliderUnitSize.Value)
    End Sub

    Private Sub Client_OnError(e As ErrorEventArgs) Handles Client.OnError

    End Sub

    Private Sub TextboxLog_TextChanged(sender As Object, e As TextChangedEventArgs) Handles TextboxLog.TextChanged

    End Sub

    Private Sub LabelClients_SizeChanged() Handles LabelClients.SizeChanged, LabelConnects.SizeChanged, LabelDisconnects.SizeChanged, LabelReceived.SizeChanged, LabelReceivedObjects.SizeChanged, LabelSent.SizeChanged, Me.Loaded, Me.SizeChanged
        SetUnitWidth()
    End Sub

    Private Sub SetUnitWidth()
        Dim leftPos As Double = LabelClients.ActualWidth + LabelConnects.ActualWidth + LabelDisconnects.ActualWidth + LabelReceived.ActualWidth + LabelReceivedObjects.ActualWidth + LabelSent.ActualWidth
        Dim UnitWidth As Double = gb1.ActualWidth - leftPos - 100
        If UnitWidth > 0 Then UnitGrid.Width = UnitWidth
    End Sub
End Class

<Serializable>
Public Class BandwidthObject

    Public Shared Function Create()
        Return New BandwidthObject() With {.Data = CachedData}
    End Function

    Public Shared Property UnitSize As Integer
        Get
            Return _UnitSize
        End Get
        Set(value As Integer)
            _UnitSize = value
            If Not IsSettingSize Then
                Task.Run(Sub()
                             IsSettingSize = True
                             _CachedData = RandomData()
                             IsSettingSize = False
                         End Sub)
            ElseIf Not WaitSet Then
                WaitSet = True
                Task.Run(Sub()
                             Do While IsSettingSize
                                 Task.Delay(1)
                             Loop
                             _CachedData = RandomData()
                             IsSettingSize = False
                             WaitSet = False
                         End Sub)
            End If
        End Set
    End Property
    Private Shared _UnitSize As Integer = 1000000
    Public Shared IsSettingSize As Boolean = False
    Private Shared WaitSet As Boolean = False

    Public Property Data As String
    Private Shared ReadOnly Property CachedData As String
        Get
            Return _CachedData
        End Get
    End Property
    Private Shared _CachedData As String = RandomData()

    Public Shared Function RandomData() As String
        Return RandomString(UnitSize)
    End Function

    Private Shared Function RandomString(length As Integer) As String
        Dim random As New Random()
        Dim chars As String = "0123456789abcdefghijklmnopqrstuvwxyz!@#$%"
        Dim output As New StringBuilder()

        For i = 0 To length - 1
            output.Append(chars(random.[Next](chars.Length)))
        Next

        Return output.ToString()
    End Function

End Class
