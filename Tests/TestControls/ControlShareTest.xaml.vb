Imports System.IO.Compression
Imports SocketJack.Extensions
Imports SocketJack.Net
Imports SocketJack.WPF.Controller

''' <summary>
''' This test demonstrates SocketJack.WPF's FrameworkElement.Share functionality.
''' Client1 shares a GroupBox containing various controls as a JPEG stream.
''' Client2 receives the stream and displays it in an Image control via ControlShareViewer.
''' </summary>
Public Class ControlShareTest
    Implements ITest

    Private ServerPort As Integer = NIC.FindOpenPort(7600, 8000).Result
    Public WithEvents Server As TcpServer
    Public WithEvents Client1 As TcpClient
    Public WithEvents Client2 As TcpClient

    Private ShareHandle As IDisposable
    Private Viewer As ControlShareViewer

    Private Sub Setup()
        Dim opts As New NetworkOptions With {
            .Logging = True,
            .LogReceiveEvents = False,
            .LogSendEvents = False,
            .LogToConsole = True,
            .UseCompression = False,
            .Fps = CInt(SliderFps.Value)
        }

        Server = New TcpServer(ServerPort, "ShareServer") With {.Options = opts}
        Client1 = New TcpClient("ShareClient1") With {.Options = opts}
        Client2 = New TcpClient("ShareClient2") With {.Options = opts}

        ' Logging
        AddHandler Server.LogOutput, AddressOf Log
        AddHandler Client1.LogOutput, AddressOf Log
        AddHandler Client2.LogOutput, AddressOf Log

        AddHandler Client1.OnConnected, AddressOf Client1_OnConnected
        AddHandler Client2.OnConnected, AddressOf Client2_OnConnected
        AddHandler Client1.OnDisconnected, AddressOf Client_OnDisconnected
        AddHandler Client2.OnDisconnected, AddressOf Client_OnDisconnected

        ' Start sharing once both clients are identified and peers are known
        AddHandler Client1.OnIdentified, AddressOf Client1_OnIdentified
        AddHandler Client1.PeerConnected, AddressOf Client1_PeerConnected

        ' Start viewing once Client2 discovers Client1's peer identity
        AddHandler Client2.OnIdentified, AddressOf Client2_OnIdentified
        AddHandler Client2.PeerConnected, AddressOf Client2_PeerConnected

        ' Whitelist the control-share types on the server so it redirects them
        Server.Options.Whitelist.Add(GetType(ControlShareFrame))
        Server.Options.Whitelist.Add(GetType(ControlShareRemoteAction))

        ' Wire up event logging on the shared controls
        AddHandler SharedGroupBox.PreviewMouseMove, Sub(s, e)
                                                        Dim el = TryCast(e.OriginalSource, FrameworkElement)
                                                        Dim name = If(el IsNot Nothing, If(el.Name, el.GetType().Name), "?")
                                                        Dim pt = e.GetPosition(SharedGroupBox)
                                                        Log($"[Event] MouseMove on {name} at ({pt.X:F0},{pt.Y:F0})")
                                                    End Sub
        AddHandler SharedGroupBox.PreviewMouseLeftButtonDown, Sub(s, e)
                                                                  Dim el = TryCast(e.OriginalSource, FrameworkElement)
                                                                  Dim name = If(el IsNot Nothing, If(el.Name, el.GetType().Name), "?")
                                                                  Dim pt = e.GetPosition(SharedGroupBox)
                                                                  Log($"[Event] MouseLeftButtonDown on {name} at ({pt.X:F0},{pt.Y:F0})")
                                                              End Sub
        AddHandler SharedGroupBox.PreviewMouseLeftButtonUp, Sub(s, e)
                                                                Dim el = TryCast(e.OriginalSource, FrameworkElement)
                                                                Dim name = If(el IsNot Nothing, If(el.Name, el.GetType().Name), "?")
                                                                Dim pt = e.GetPosition(SharedGroupBox)
                                                                Log($"[Event] MouseLeftButtonUp on {name} at ({pt.X:F0},{pt.Y:F0})")
                                                            End Sub
        AddHandler SharedGroupBox.PreviewMouseRightButtonDown, Sub(s, e)
                                                                   Dim el = TryCast(e.OriginalSource, FrameworkElement)
                                                                   Dim name = If(el IsNot Nothing, If(el.Name, el.GetType().Name), "?")
                                                                   Dim pt = e.GetPosition(SharedGroupBox)
                                                                   Log($"[Event] MouseRightButtonDown on {name} at ({pt.X:F0},{pt.Y:F0})")
                                                               End Sub
        AddHandler SharedGroupBox.PreviewMouseRightButtonUp, Sub(s, e)
                                                                 Dim el = TryCast(e.OriginalSource, FrameworkElement)
                                                                 Dim name = If(el IsNot Nothing, If(el.Name, el.GetType().Name), "?")
                                                                 Dim pt = e.GetPosition(SharedGroupBox)
                                                                 Log($"[Event] MouseRightButtonUp on {name} at ({pt.X:F0},{pt.Y:F0})")
                                                             End Sub
        AddHandler SharedGroupBox.MouseEnter, Sub(s, e)
                                                  Log("[Event] MouseEnter on SharedGroupBox")
                                              End Sub
        AddHandler SharedGroupBox.MouseLeave, Sub(s, e)
                                                  Log("[Event] MouseLeave on SharedGroupBox")
                                              End Sub
    End Sub

    Private Sub Client1_OnConnected(e As ConnectedEventArgs)
        Log("Client1 Connected")
    End Sub

    Private Sub Client2_OnConnected(e As ConnectedEventArgs)
        Log("Client2 Connected")
    End Sub

    Private Sub Client_OnDisconnected(e As DisconnectedEventArgs)
        Log("Client Disconnected")
    End Sub

    Private Sub Client1_OnIdentified(sender As ISocket, LocalIdentity As P2P.Identifier)
        Log("Client1 Identified: " & LocalIdentity.ID)
        TryStartSharing()
    End Sub

    Private Sub Client1_PeerConnected(sender As ISocket, e As P2P.Identifier)
        Log("Client1 sees peer: " & e.ID)
        TryStartSharing()
    End Sub

    Private Sub Client2_OnIdentified(sender As ISocket, LocalIdentity As P2P.Identifier)
        Log("Client2 Identified: " & LocalIdentity.ID)
        TryStartViewing()
    End Sub

    Private Sub Client2_PeerConnected(sender As ISocket, e As P2P.Identifier)
        Log("Client2 sees peer: " & e.ID)
        TryStartViewing()
    End Sub

    Private Sub TryStartViewing()
        If Viewer IsNot Nothing Then Return
        If Client2 Is Nothing OrElse Not Client2.Connected Then Return
        If Client2.RemoteIdentity Is Nothing Then Return

        ' Find Client1's peer identity from Client2's peer list
        Dim client1Peer = Client2.Peers.FirstNotMe()
        If client1Peer Is Nothing Then Return

        Log("Starting Viewer: receiving frames from peer " & client1Peer.ID)
        Dispatcher.Invoke(Sub()
                              Viewer = Client2.ViewShare(ViewerImage, client1Peer)
                          End Sub)
    End Sub

    Private Sub TryStartSharing()
        If ShareHandle IsNot Nothing Then Return
        If Client1 Is Nothing OrElse Not Client1.Connected Then Return
        If Client1.RemoteIdentity Is Nothing Then Return

        ' Find Client2's peer identity from Client1's peer list
        Dim client2Peer = Client1.Peers.FirstNotMe()
        If client2Peer Is Nothing Then Return

        Dim fps As Integer = 10
        Dispatcher.Invoke(Sub() fps = CInt(SliderFps.Value))

        Log("Starting Share: sending frames to peer " & client2Peer.ID)
        Dispatcher.Invoke(Sub()
                              ShareHandle = SharedGroupBox.Share(Client1, client2Peer, fps)


                          End Sub)
    End Sub

#Region "UI"

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "Control Share"
        End Get
    End Property

    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return False
        End Get
    End Property

    Public Property Running As Boolean Implements ITest.Running
        Get
            If Server Is Nothing Then Return False
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

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            Dispatcher.Invoke(AddressOf Setup)
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."

            If Server.Listen() Then
                Await Client1.Connect("127.0.0.1", ServerPort)
                Await Client2.Connect("127.0.0.1", ServerPort)
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Stop Test"
                Log("Test Started.")
            Else
                Log("Failed to start server.")
                ButtonStartStop.IsEnabled = True
                ButtonStartStop.Content = "Start Test"
            End If
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Stopping.."

            If ShareHandle IsNot Nothing Then
                ShareHandle.Dispose()
                ShareHandle = Nothing
            End If

            If Viewer IsNot Nothing Then
                Viewer.Dispose()
                Viewer = Nothing
            End If

            Server.StopListening()
            ViewerImage.Source = Nothing
            ButtonStartStop.Content = "Start Test"
            ButtonStartStop.IsEnabled = True
            Log("Test Stopped.")
        End If
    End Sub

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.AppendText(If(text.EndsWith(Environment.NewLine), text, text & vbCrLf))
                                   If isAtEnd Then TextLog.ScrollToEnd()
                               End Sub)
    End Sub

#End Region

End Class
