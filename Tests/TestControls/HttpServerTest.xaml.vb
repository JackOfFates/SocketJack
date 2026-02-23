Imports System.Text
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
            .UseCompression = False
            .CompressionAlgorithm.CompressionLevel = IO.Compression.CompressionLevel.SmallestSize
        End With

        ' Register HelloObj as a handled callback type so the server whitelist accepts it
        Server.RegisterCallback(Of HelloObj)(Sub(e)
                                                 Log("Server received HelloObj: " & e.Object?.Text)
                                             End Sub)

        ' Map a regular HTTP endpoint that returns a HelloObj
        Server.Map("GET", "/HelloObj", Function(conn, req, ct)
                                           Return New HelloObj()
                                       End Function)

        ' Map a streaming HTTP endpoint that sends numbered lines indefinitely
        Server.MapStream("GET", "/stream", Sub(conn, req, chunked, ct)
                                               Dim i As Integer = 1
                                               Do While conn.Socket IsNot Nothing AndAlso conn.Socket.Connected
                                                   chunked.WriteLine("Test! " & i.ToString())
                                                   i += 1
                                                   Thread.Sleep(100)
                                               Loop
                                           End Sub)

        ' Index page links to the streaming endpoint
        Server.IndexPageHtml = "<html><head><title>SocketJack HttpServer</title></head><body>" &
                               "<h1>SocketJack HttpServer</h1>" &
                               "<p><a href=""/stream"">View live stream</a></p>" &
                               "</body></html>"
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
                                   Log("The IE browser built in does not support streaming content.")
                                   ButtonStartStop.IsEnabled = True
                                   ButtonStartStop.Content = If(listening, "Stop Test", "Start Test")
                               End Sub)
    End Sub

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
            If Not Server.Listen() Then
                Return
            End If

            ' Server is now listening; enable the button immediately so user can stop it.
            UpdateStartStopUi()

            Try
                wb1.Navigate(New Uri("http://localhost:" & ServerPort & "/stream"))
            Catch
            End Try

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