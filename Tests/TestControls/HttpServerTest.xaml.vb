Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Windows.Threading
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P


Public Class HttpServerTest
    Implements ITest

    Private ServerPort As Integer = 11434
    Public WithEvents Server As New LmVsProxy("localhost", 11435, 11434)
    'Public WithEvents Server As HttpServer
    Private _broadcast As BroadcastServer
    Private _statsTimer As DispatcherTimer
    Private _remoteLmStudioProxy As TcpDuplicator
    Private Const LocalLmStudioProxyPort As Integer = 11435


    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Server = New LmVsProxy("localhost", 11435, 11434)
        Server.PromptTimeout = TimeSpan.FromMinutes(30)
        AddHandler Server.OutputLog, AddressOf OnServerLog
        NIC.ForwardPort(ServerPort).ConfigureAwait(True)
        UpdateStartStopUi()

        'With Server.Options
        '    .Logging = True
        '    .LogReceiveEvents = False
        '    .LogSendEvents = False
        '    .UseCompression = False
        '    .CompressionAlgorithm.CompressionLevel = IO.Compression.CompressionLevel.SmallestSize
        'End With

        ' Register HelloObj as a handled callback type so the server whitelist accepts it
        'Server.RegisterCallback(Of HelloObj)(Sub(e)
        '                                         Log("Server received HelloObj: " & e.Object?.Text)
        '                                     End Sub)

        ' Map a regular HTTP endpoint that returns a HelloObj
        'Server.Map("GET", "/HelloObj", Function(conn, req, ct)
        '                                   Return New HelloObj()
        '                               End Function)

        '' Set up BroadcastServer for streaming/upload/RTMP routes
        '_broadcast = New BroadcastServer(Server)
        'AddHandler _broadcast.LogOutput, AddressOf Log
        '_broadcast.Register()

        ' Index page links to the streaming endpoint and shows upload info
        'Server.IndexPageHtml = "<!DOCTYPE html>" &
        '                       "<html><head>" &
        '                       "<meta charset=""UTF-8"">" &
        '                       "<title>SocketJack HttpServer</title>" &
        '                       "<meta property=""og:title"" content=""SocketJack HttpServer"">" &
        '                       "<meta property=""og:description"" content=""Live streaming server powered by SocketJack."">" &
        '                       "<meta property=""og:type"" content=""website"">" &
        '                       "</head><body>" &
        '                       "<h1>SocketJack HttpServer</h1>" &
        '                       "<p><a href=""/stream"">Watch live stream (browser)</a></p>" &
        '                       "<p>Direct MPEG-TS: <code><a href=""/stream/data"">/stream/data</a></code> (for VLC)</p>" &
        '                       "<p>OBS Upload endpoint: PUT or POST to <code>/Upload</code></p>" &
        '                       "<p>OBS RTMP endpoint: <code>rtmp://localhost:" & ServerPort & "/live</code></p>" &
        '                       "<p>Stream Key: <code>" & _broadcast.StreamKey & "</code></p>" &
        '                       "</body></html>"

        '' Allow Facebook crawler to scrape the server for link previews
        'Server.Robots = "User-agent: *" & vbLf &
        '                "Allow: /" & vbLf &
        '                vbLf &
        '                "User-agent: facebookexternalhit" & vbLf &
        '               "Allow: /" & vbLf

        ' Stats refresh timer
        '_statsTimer = New DispatcherTimer()
        '_statsTimer.Interval = TimeSpan.FromSeconds(1)
        'AddHandler _statsTimer.Tick, AddressOf StatsTimerTick
        '_statsTimer.Start()
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
                                   'Log("The IE browser built in does not support streaming content.")
                                   ButtonStartStop.IsEnabled = True
                                   ButtonStartStop.Content = If(listening, "Stop Test", "Start Test")
                               End Sub)
    End Sub

    'Private Sub StatsTimerTick(sender As Object, e As EventArgs)
    '    Dim stats = _broadcast.UpdateStats()

    '    If Not stats.Active Then
    '        If RtmpStatsPanel.Visibility = Visibility.Visible Then
    '            LabelRtmpStatus.Text = "Idle"
    '            RtmpStatusIndicator.Fill = New SolidColorBrush(Colors.Gray)
    '            LabelBitrate.Text = "0 kbps"
    '            LabelVideoFrames.Text = "0"
    '            LabelAudioFrames.Text = "0"
    '            LabelDroppedFrames.Text = "0"
    '            LabelTotalBytes.Text = "0 B"
    '        End If
    '        Return
    '    End If

    '    RtmpStatsPanel.Visibility = Visibility.Visible

    '    ' Update UI
    '    LabelRtmpStatus.Text = "RTMP Live (" & stats.App & "/" & stats.StreamKey & ")"
    '    RtmpStatusIndicator.Fill = New SolidColorBrush(Colors.LimeGreen)
    '    LabelBitrate.Text = stats.BitrateKbps.ToString("N0") & " kbps"
    '    LabelVideoFrames.Text = stats.VideoFrames.ToString("N0")
    '    LabelAudioFrames.Text = stats.AudioFrames.ToString("N0")
    '    LabelDroppedFrames.Text = stats.DroppedFrames.ToString("N0")
    '    LabelTotalBytes.Text = BroadcastServer.FormatBytes(stats.TotalBytes)
    'End Sub

#Region "Test Classes"

    Public Class HelloObj
        Public Property Text As String = "Hello from SocketJack HttpServer!"
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
            StartRemoteLmStudioProxyIfEnabled()

            If Not Server.Start() Then
                StopRemoteLmStudioProxy()
                Return
            End If

            ' Server is now listening; enable the button immediately so user can stop it.
            UpdateStartStopUi()

            Try
                If Not Server.ChatServer.IsListening Then
                    Server.ChatServer.Listen()
                End If

                Log("VS proxy endpoint: http://localhost:" & ServerPort & "/v1/chat/completions")
                Log("Chat UI available at: " & Server.ChatServerUrl)
                Log("LM Studio prompt timeout: " & Server.PromptTimeout.TotalMinutes.ToString("0") & " minutes")
                Process.Start(Server.ChatServerUrl)
            Catch ex As Exception
                Log("Chat UI start error: " & ex.Message)
            End Try

        Catch ex As Exception
            StopRemoteLmStudioProxy()
            Log("StartTest error: " & ex.Message)
        Finally
            UpdateStartStopUi()
        End Try
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Server IsNot Nothing AndAlso (Server.IsListening OrElse (Server.IsChatServerCreated AndAlso Server.ChatServer.IsListening)) Then
            UpdateStartStopUi("Stopping..", isBusy:=True)
            Server.Stop()
            StopRemoteLmStudioProxy()
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

    Private Sub CheckRemoteLmStudio_CheckedChanged(sender As Object, e As RoutedEventArgs) Handles CheckRemoteLmStudio.Checked, CheckRemoteLmStudio.Unchecked
        If TextRemoteServer IsNot Nothing Then
            TextRemoteServer.IsEnabled = CheckRemoteLmStudio.IsChecked.GetValueOrDefault(False) AndAlso Not Running
        End If
    End Sub

    Private Sub StartRemoteLmStudioProxyIfEnabled()
        If CheckRemoteLmStudio Is Nothing OrElse Not CheckRemoteLmStudio.IsChecked.GetValueOrDefault(False) Then Return

        Dim remoteHost As String = Nothing
        Dim remotePort As Integer = LocalLmStudioProxyPort
        If Not TryParseRemoteServer(TextRemoteServer.Text, remoteHost, remotePort) Then
            Throw New InvalidOperationException("Remote Server must be a host, host:port, or http(s) URL.")
        End If

        StopRemoteLmStudioProxy()
        _remoteLmStudioProxy = New TcpDuplicator(remoteHost, remotePort, LocalLmStudioProxyPort)
        _remoteLmStudioProxy.Start()
        TextRemoteServer.IsEnabled = False
        Log("Remote LM Studio localhost proxy enabled: 127.0.0.1:" & LocalLmStudioProxyPort & " -> " & remoteHost & ":" & remotePort)
    End Sub

    Private Sub StopRemoteLmStudioProxy()
        If _remoteLmStudioProxy IsNot Nothing Then
            Try
                If _remoteLmStudioProxy.IsRunning Then
                    Log("Stopping remote LM Studio localhost proxy on 127.0.0.1:" & LocalLmStudioProxyPort)
                End If
                _remoteLmStudioProxy.Dispose()
            Catch ex As Exception
                Log("Remote LM Studio proxy stop error: " & ex.Message)
            Finally
                _remoteLmStudioProxy = Nothing
            End Try
        End If

        If TextRemoteServer IsNot Nothing AndAlso CheckRemoteLmStudio IsNot Nothing Then
            TextRemoteServer.IsEnabled = CheckRemoteLmStudio.IsChecked.GetValueOrDefault(False) AndAlso Not Running
        End If
    End Sub

    Private Function TryParseRemoteServer(input As String, ByRef host As String, ByRef port As Integer) As Boolean
        host = Nothing
        port = LocalLmStudioProxyPort

        Dim value = If(input, String.Empty).Trim()
        If String.IsNullOrWhiteSpace(value) Then Return False

        Dim uri As Uri = Nothing
        If Uri.TryCreate(value, UriKind.Absolute, uri) AndAlso Not String.IsNullOrWhiteSpace(uri.Host) Then
            host = uri.Host
            If uri.Port > 0 Then port = uri.Port
            Return True
        End If

        If value.StartsWith("[") Then
            Dim close = value.IndexOf("]"c)
            If close > 1 Then
                host = value.Substring(1, close - 1)
                If value.Length > close + 2 AndAlso value(close + 1) = ":"c Then
                    If Not Integer.TryParse(value.Substring(close + 2), port) Then Return False
                End If
                Return Not String.IsNullOrWhiteSpace(host)
            End If
        End If

        Dim lastColon = value.LastIndexOf(":"c)
        If lastColon > 0 AndAlso value.IndexOf(":"c) = lastColon Then
            host = value.Substring(0, lastColon).Trim()
            If Not Integer.TryParse(value.Substring(lastColon + 1).Trim(), port) Then Return False
        Else
            host = value
        End If

        Return Not String.IsNullOrWhiteSpace(host) AndAlso port > 0 AndAlso port <= 65535
    End Function

    'Private Sub ButtonRecord_Click(sender As Object, e As RoutedEventArgs) Handles ButtonRecord.Click
    '    _broadcast.Recording = Not _broadcast.Recording
    '    If _broadcast.Recording Then
    '        ButtonRecord.Content = "Stop Recording"
    '        Log("Recording started.")
    '    Else
    '        ButtonRecord.Content = "Record"
    '        Dim count As Integer = _broadcast.GetRecordedBytes().Length
    '        Log("Recording stopped. " & count & " chunk(s) captured.")
    '    End If
    'End Sub

    'Private Sub ButtonSave_Click(sender As Object, e As RoutedEventArgs) Handles ButtonSave.Click
    '    Dim chunks As Byte()() = _broadcast.GetRecordedBytes()

    '    If chunks.Length = 0 Then
    '        Log("No recorded bytes to save.")
    '        Return
    '    End If

    '    Dim dlg As New Microsoft.Win32.SaveFileDialog()
    '    dlg.Title = "Save Recorded Bytes"
    '    dlg.Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
    '    dlg.DefaultExt = ".txt"
    '    dlg.FileName = "RecordedBytes.txt"

    '    If dlg.ShowDialog() = True Then
    '        Using writer As New StreamWriter(dlg.FileName, False, Encoding.UTF8)
    '            For i As Integer = 0 To chunks.Length - 1
    '                For offset As Integer = 0 To chunks(i).Length - 1 Step 16
    '                    Dim remaining As Integer = Math.Min(16, chunks(i).Length - offset)
    '                    Dim hex As New StringBuilder()
    '                    Dim ascii As New StringBuilder()
    '                    For j As Integer = 0 To remaining - 1
    '                        Dim b As Byte = chunks(i)(offset + j)
    '                        hex.Append(b.ToString("X2"))
    '                        hex.Append(" ")
    '                        If b >= 32 AndAlso b < 127 Then
    '                            ascii.Append(Chr(b))
    '                        Else
    '                            ascii.Append(".")
    '                        End If
    '                    Next
    '                    writer.WriteLine(offset.ToString("X8") & "  " & hex.ToString().PadRight(48) & " " & ascii.ToString())
    '                Next
    '            Next
    '        End Using
    '        Log("Recorded bytes saved to: " & dlg.FileName)
    '    End If
    'End Sub

    Public Sub Log(text As String) 'Handles Server.LogOutput
        If text Is Nothing OrElse text = String.Empty Then Return
        Try
            Dispatcher.Invoke(Sub()
                                  Try
                                      SyncLock (TextLog)
                                          Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
                                          ' Sanitize chars >= U+0100 that crash SyntaxBox AhoCorasickSearch (256-char limit)
                                          Dim chars = text.ToCharArray()
                                          For i = 0 To chars.Length - 1
                                              If AscW(chars(i)) >= 256 Then chars(i) = "?"c
                                          Next
                                          Dim safe = New String(chars)
                                          If Not safe.EndsWith(vbCrLf) AndAlso Not safe.EndsWith(vbLf) AndAlso Not safe.EndsWith(vbCr) Then
                                              safe &= vbCrLf
                                          End If
                                          Try : TextLog.AppendText(safe)
                                          Catch : End Try
                                          Try : If isAtEnd Then TextLog.ScrollToEnd()
                                          Catch : End Try
                                      End SyncLock
                                  Catch : End Try

                              End Sub)
        Catch : End Try
    End Sub

    Private Sub OnServerLog(sender As Object, e As SocketJack.Net.OutputLogEventArgs)
        Log(e.Message)
    End Sub

    Private Async Sub HttpServerTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If NIC.InterfaceDiscovered Then
            Await Forward()
        Else
            AddHandler NIC.OnInterfaceDiscovered, Async Sub() Forward()
        End If
    End Sub

    Public Async Function Forward() As Task(Of Boolean)
        Dim forwarded As Boolean = Await NIC.ForwardPorts(New Integer() {80, 11434, 11435, 11436})
        If forwarded Then
            Log("Ports 80, 11434, 11435, & 11436 forwarded successfully via UPnP." & Environment.NewLine)
        Else
            Log("Port forwarding via UPnP failed or not available." & Environment.NewLine)
        End If
        Return forwarded
    End Function


#End Region

End Class
