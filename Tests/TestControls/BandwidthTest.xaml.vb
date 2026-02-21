Imports System.Text
Imports System.Threading
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq
Imports SocketJack.Compression
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Serialization

Public Class BandwidthTest
    Implements ITest

    'Private Sub BandwidthTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
    '    Base.TestName = "Bandwidth"
    'End Sub

    Public ReadOnly Property BufferSize As Integer
        Get
            Return BandwidthObject.UnitSize
        End Get
    End Property ' buffer size equal to object size for high bandwidth
    Public Property ServerPort As Integer = NIC.FindOpenPort(7500, 8000).Result
    Public Property Compressor As New GZip2Compression(IO.Compression.CompressionLevel.SmallestSize)
    Public WithEvents Server As New TcpServer(ServerPort, String.Format("{0}Server", {TestName})) With {.Options =
        New NetworkOptions With {.Logging = True,
                             .UseCompression = False,
                             .MaximumDownloadMbps = 0.2,
                             .MaximumUploadMbps = 0.2,
                             .CompressionAlgorithm = Compressor}}

    Public WithEvents Client As New TcpClient(String.Format("{0}Client", {TestName})) With {.Options =
        New NetworkOptions With {.Logging = True,
                             .UpdateConsoleTitle = True,
                             .UseCompression = Server.Options.UseCompression,
                             .MaximumDownloadMbps = 0.2,
                             .MaximumUploadMbps = 0.2,
                             .CompressionAlgorithm = Compressor}}

#Region "Properties"
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
#End Region

#Region "UI"

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
            ResetValues()
            If Server.Listen() Then
                Dim connected As Boolean = Await Client.Connect("127.0.0.1", ServerPort)
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

    Private Sub SliderBwSize_ValueChanged(sender As Object, e As RoutedPropertyChangedEventArgs(Of Double)) Handles SliderBwSize.ValueChanged
        If Running Then
            Server.Options.MaximumUploadMbps = SliderBwSize.Value / 100
            Server.Options.MaximumDownloadMbps = SliderBwSize.Value / 100
            Client.Options.MaximumUploadMbps = SliderBwSize.Value / 100
            Client.Options.MaximumDownloadMbps = SliderBwSize.Value / 100
        End If
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
    Private Sub Slider_DragStarted()
        Dragging = True
    End Sub

    Private Sub Slider_DragCompleted()
        Dragging = False
        BandwidthObject.UnitSize = CInt(SliderUnitSize.Value)
    End Sub
    Private Sub LabelClients_SizeChanged() Handles LabelClients.SizeChanged, LabelConnects.SizeChanged, LabelDisconnects.SizeChanged, LabelReceived.SizeChanged, LabelReceivedObjects.SizeChanged, LabelSent.SizeChanged, Me.Loaded, Me.SizeChanged
        SetUnitWidth()
    End Sub

    Private Sub SetUnitWidth()
        Dim leftPos As Double = LabelClients.ActualWidth + LabelConnects.ActualWidth + LabelDisconnects.ActualWidth + LabelReceived.ActualWidth + LabelReceivedObjects.ActualWidth + LabelSent.ActualWidth
        Dim UnitWidth As Double = gb1.ActualWidth - leftPos - 100
        If UnitWidth > 0 Then UnitGrid.Width = UnitWidth
    End Sub
#End Region

    Private Sub Server_ClientConnected(e As ConnectedEventArgs) Handles Server.ClientConnected
        Interlocked.Increment(Connects)
        Interlocked.Increment(TotalClients)
    End Sub

    Private Sub Server_ClientDisconnected(e As DisconnectedEventArgs) Handles Server.ClientDisconnected
        Interlocked.Increment(Disconnects)
        Interlocked.Decrement(TotalClients)
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

    Dim sw As New SpinWait, WorkerCount As Integer = 16
    Private Sub WorkerThread(obj As Object)
        Do While Running
            If Client.Connected Then
                Client.Send(BandwidthObject.Cached)
            End If
            'If Server.isListening Then
            '    Server.SendBroadcast(BandwidthObject.Create())
            'End If
            sw.SpinOnce()
        Loop
    End Sub

    Private Sub Client_OnConnected(sender As Object) Handles Client.OnConnected
        For i As Integer = 0 To WorkerCount - 1
            Dim Worker = New Thread(AddressOf WorkerThread)
            Worker.Start()
        Next
    End Sub

    Private Sub Tcp_OnSent(e As SentEventArgs) Handles Client.OnSent, Server.OnSent
        AddSentBytes(e.BytesSent)
    End Sub

    Private Sub ClientServer_OnReceive(ByRef e As ReceivedEventArgs(Of Object)) Handles Client.OnReceive, Server.OnReceive
        AddReceivedBytes(e.BytesReceived)
    End Sub

    Public Sub AddReceivedBytes(bytes As Integer)
        Interlocked.Add(_ReceivedBytes, bytes)
        If LabelReceived Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelReceived.Text = "Received" & vbCrLf & _ReceivedBytes.ByteToString(2))
    End Sub
    Public Sub ResetReceivedBytes()
        _ReceivedBytes = 0
        If LabelReceived Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelReceived.Text = "Received" & vbCrLf & 0)
    End Sub
    Private _ReceivedBytes As Long

    Public Sub AddSentBytes(bytes As Integer)
        Interlocked.Add(_SentBytes, bytes)
        If LabelSent Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelSent.Text = "Sent" & vbCrLf & _SentBytes.ByteToString(2))
    End Sub
    Public Sub ResetSentBytes()
        _SentBytes = 0
        If LabelSent Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelSent.Text = "Sent" & vbCrLf & 0)
    End Sub
    Private _SentBytes As Long

    Private Sub Received_BandwidthObject(e As ReceivedEventArgs(Of BandwidthObject))
        IncrementReceivedObjects()
    End Sub

    Public Sub IncrementReceivedObjects()
        _ReceivedObjects += 1
        If LabelReceivedObjects Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelReceivedObjects.Text = "Objects" & vbCrLf & _ReceivedObjects)
    End Sub
    Public Sub ResetReceivedObjects()
        _ReceivedObjects = 0
        If LabelReceivedObjects Is Nothing Then Return
        Dispatcher.InvokeAsync(Sub() LabelReceivedObjects.Text = "Objects" & vbCrLf & 0)
    End Sub
    Private _ReceivedObjects As Integer

    Public Sub ResetValues()
        Server.Options.MaximumUploadMbps = SliderBwSize.Value / 100
        Server.Options.MaximumDownloadMbps = SliderBwSize.Value / 100
        Client.Options.MaximumUploadMbps = SliderBwSize.Value / 100
        Client.Options.MaximumDownloadMbps = SliderBwSize.Value / 100
        ResetReceivedObjects()
        ResetSentBytes()
        ResetReceivedBytes()
    End Sub

    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.

        ' Alternative way to white-list types
        ' Server.Whitelist.AddType(GetType(BandwidthObject))
        With Server.Options
            .LogReceiveEvents = False
            '.UseCompression = True
            '.MaximumDownloadMbps = 0
        End With
        Client.Options = Server.Options
        Server.RegisterCallback(Of BandwidthObject)(AddressOf Received_BandwidthObject)
        Client.RegisterCallback(Of BandwidthObject)(AddressOf Received_BandwidthObject)
    End Sub

End Class

<Serializable>
Public Class BandwidthObject

    Private Shared Function Create()
        Return New BandwidthObject() With {.Data = BuildString(UnitSize)}
    End Function

    Public Shared Property UnitSize As Integer
        Get
            Return _UnitSize
        End Get
        Set(value As Integer)
            _UnitSize = value
            If Not SizeChanging Then
                Task.Run(Sub()
                             SizeChanging = True
                             Cached = BandwidthObject.Create()
                             SizeChanging = False
                         End Sub)
            ElseIf Not WaitSet Then
                WaitSet = True
                Task.Run(Sub()
                             SizeChanging = True
                             Do While SizeChanging
                                 Task.Delay(1)
                             Loop
                             Cached = BandwidthObject.Create()
                             SizeChanging = False
                             WaitSet = False
                         End Sub)
            End If
        End Set
    End Property
    Private Shared _UnitSize As Integer = 250
    Private Shared LastUnitSize As Integer = 250
    Public Shared Cached = BandwidthObject.Create()
    Public Shared SizeChanging As Boolean = False
    Private Shared WaitSet As Boolean = False

    Public Property Data As String

    Private Shared Function BuildString(length As Integer) As String
        Dim output As New StringBuilder()

        For i = 0 To length - 1
            output.Append("0")
        Next

        Return output.ToString()
    End Function

End Class
