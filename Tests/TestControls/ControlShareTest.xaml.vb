Imports System.IO.Compression
Imports System.Windows.Threading
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
    Public Server As ISocket
    Public Client1 As ISocket
    Public Client2 As ISocket

    Private ShareHandle As IDisposable
    Private Viewer As ControlShareViewer
    Private WithEvents LatencyTimer As DispatcherTimer

    Private Sub Cleanup()
        If Client1 IsNot Nothing Then
            Client1.Dispose()
            Client1 = Nothing
        End If
        If Client2 IsNot Nothing Then
            Client2.Dispose()
            Client2 = Nothing
        End If
        If Server IsNot Nothing Then
            Server.Dispose()
            Server = Nothing
        End If
    End Sub

    Private Sub Setup()
        Cleanup()
        Dim opts As New NetworkOptions With {
            .Logging = True,
            .LogReceiveEvents = False,
            .LogSendEvents = False,
            .LogToConsole = True,
            .UseCompression = False,
            .Fps = CInt(SliderFps.Value)
        }

        If UDP_Enabled.IsChecked Then
            Dim s As New UdpServer(ServerPort, "ShareServer") With {.Options = opts}
            Dim c1 As New UdpClient("ShareClient1") With {.Options = opts}
            Dim c2 As New UdpClient("ShareClient2") With {.Options = opts}
            Server = s
            Client1 = c1
            Client2 = c2

            ' Logging
            AddHandler s.LogOutput, AddressOf Log
            AddHandler c1.LogOutput, AddressOf Log
            AddHandler c2.LogOutput, AddressOf Log

            AddHandler c1.OnConnected, AddressOf Client1_OnConnected
            AddHandler c2.OnConnected, AddressOf Client2_OnConnected
            AddHandler c1.OnDisconnected, AddressOf Client_OnDisconnected
            AddHandler c2.OnDisconnected, AddressOf Client_OnDisconnected

            AddHandler c1.OnIdentified, AddressOf Client1_OnIdentified
            AddHandler c1.PeerConnected, AddressOf Client1_PeerConnected
            AddHandler c2.OnIdentified, AddressOf Client2_OnIdentified
            AddHandler c2.PeerConnected, AddressOf Client2_PeerConnected
        Else
            Dim s As New TcpServer(ServerPort, "ShareServer") With {.Options = opts}
            Dim c1 As New TcpClient("ShareClient1") With {.Options = opts}
            Dim c2 As New TcpClient("ShareClient2") With {.Options = opts}
            Server = s
            Client1 = c1
            Client2 = c2

            ' Logging
            AddHandler s.LogOutput, AddressOf Log
            AddHandler c1.LogOutput, AddressOf Log
            AddHandler c2.LogOutput, AddressOf Log

            AddHandler c1.OnConnected, AddressOf Client1_OnConnected
            AddHandler c2.OnConnected, AddressOf Client2_OnConnected
            AddHandler c1.OnDisconnected, AddressOf Client_OnDisconnected
            AddHandler c2.OnDisconnected, AddressOf Client_OnDisconnected

            AddHandler c1.OnIdentified, AddressOf Client1_OnIdentified
            AddHandler c1.PeerConnected, AddressOf Client1_PeerConnected
            AddHandler c2.OnIdentified, AddressOf Client2_OnIdentified
            AddHandler c2.PeerConnected, AddressOf Client2_PeerConnected
        End If

        ' Whitelist the control-share types on the server so it redirects them
        Server.Options.Whitelist.Add(GetType(ControlShareFrame))
        Server.Options.Whitelist.Add(GetType(RemoteAction))

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
                              If Client2.AsUdpClient() IsNot Nothing Then
                                  Viewer = Client2.AsUdpClient().ViewShare(ViewerImage, client1Peer)
                              Else
                                  Viewer = Client2.AsTcpClient().ViewShare(ViewerImage, client1Peer)
                              End If
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
                              If Client1.AsUdpClient() IsNot Nothing Then
                                  ShareHandle = SharedGroupBox.Share(Client1.AsUdpClient(), client2Peer, fps)
                              Else
                                  ShareHandle = SharedGroupBox.Share(Client1.AsTcpClient(), client2Peer, fps)
                              End If
                          End Sub)
    End Sub

#Region "Latency"

    Private Sub StartLatencyTimer()
        StopLatencyTimer()
        LatencyTimer = New DispatcherTimer With {
            .Interval = TimeSpan.FromSeconds(2)
        }
        AddHandler LatencyTimer.Tick, AddressOf LatencyTimer_Tick
        LatencyTimer.Start()
    End Sub

    Private Sub StopLatencyTimer()
        If LatencyTimer IsNot Nothing Then
            LatencyTimer.Stop()
            RemoveHandler LatencyTimer.Tick, AddressOf LatencyTimer_Tick
            LatencyTimer = Nothing
        End If
    End Sub

    Private Sub LatencyTimer_Tick(sender As Object, e As EventArgs)
        Dim c1 As Long = 0
        Dim c2 As Long = 0
        If Client1 IsNot Nothing AndAlso Client1.Connected Then c1 = CType(Client1, Object).LatencyMs
        If Client2 IsNot Nothing AndAlso Client2.Connected Then c2 = CType(Client2, Object).LatencyMs
        TextClient1Latency.Text = c1 & " ms"
        TextClient2Latency.Text = c2 & " ms"
    End Sub

#End Region

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
            Return CType(Server, Object).IsListening
        End Get
        Set(value As Boolean)
            If Server Is Nothing Then Return
            Dim isListening As Boolean = CType(Server, Object).IsListening
            If value AndAlso Not isListening Then
                ITest_StartTest()
            ElseIf Not value AndAlso isListening Then
                ITest_StopTest()
            End If
        End Set
    End Property

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            Dispatcher.Invoke(AddressOf Setup)
            TextLog.Text = String.Empty
            ButtonStartStop.IsEnabled = False
            UDP_Enabled.IsEnabled = False
            ButtonStartStop.Content = "Starting.."

            Dim listened As Boolean = CType(Server, Object).Listen()
            If listened Then
                If UDP_Enabled.IsChecked Then
                    Await Client1.AsUdpClient.Connect("127.0.0.1", ServerPort)
                    Await Client2.AsUdpClient.Connect("127.0.0.1", ServerPort)
                Else
                    Await Client1.AsTcpClient.Connect("127.0.0.1", ServerPort)
                    Await Client2.AsTcpClient.Connect("127.0.0.1", ServerPort)
                End If
                StartLatencyTimer()
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

            StopLatencyTimer()

            If ShareHandle IsNot Nothing Then
                ShareHandle.Dispose()
                ShareHandle = Nothing
            End If

            If Viewer IsNot Nothing Then
                Viewer.Dispose()
                Viewer = Nothing
            End If

            CType(Server, Object).StopListening()
            Cleanup()
            ViewerImage.Source = Nothing
            TextClient1Latency.Text = "-- ms"
            TextClient2Latency.Text = "-- ms"
            ButtonStartStop.Content = "Start Test"
            ButtonStartStop.IsEnabled = True
            UDP_Enabled.IsEnabled = True
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
