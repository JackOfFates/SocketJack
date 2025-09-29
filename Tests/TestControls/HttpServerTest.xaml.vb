Imports System.Threading
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.Net.P2P


Public Class HttpServerTest
    Implements ITest

    Private ServerPort As Integer = 80
    Public WithEvents Server As HttpServer


    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Server = New HttpServer(ServerPort, "HttpServer")
        With Server.Options
            .Logging = True
            .LogReceiveEvents = True
            .LogSendEvents = True
            .UseCompression = True
            .CompressionAlgorithm.CompressionLevel = IO.Compression.CompressionLevel.SmallestSize
        End With
    End Sub

#Region "Test Classes"

    Public Class HelloObj
        Public Property Text As String = "Hello from HttpServer!"
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
        If Not Running Then
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
                'wb1.Navigate("http://localhost:" & ServerPort)
                Dim response As String = Await HttpClientExtensions.DownloadStringAsync("http://localhost:" & ServerPort)
                Log(response)
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

    Public Sub Log(text As String) Handles Server.LogOutput
        Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.AppendText(If(text.EndsWith(Environment.NewLine), text, text & vbCrLf))
                                   If isAtEnd Then TextLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub Server_OnHttpRequest(Connection As TcpConnection, ByRef context As HttpContext, cancellationToken As CancellationToken) Handles Server.OnHttpRequest
        context.Response.Body = New HelloObj()
    End Sub

#End Region

End Class