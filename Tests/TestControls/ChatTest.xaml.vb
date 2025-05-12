Imports Microsoft.SqlServer
Imports SocketJack.Management
Imports SocketJack.Networking
Imports SocketJack.Networking.Shared

Public Class ChatTest
    Implements ITest

#Region "Chat Classes"
    Public Class ChatMessage
        Public Property Text As String
        Public Property From As String
    End Class

    Public Class LoginObj
        Public Property UserName As String
    End Class
#End Region

#Region "UI"

    Public ReadOnly Property TestName As String Implements ITest.TestName
        Get
            Return "ChatServer"
        End Get
    End Property

    Public ReadOnly Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return False
        End Get
    End Property

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

    Private Async Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            ButtonStartStop.IsEnabled = False
            ButtonStartStop.Content = "Starting.."
            If Server.Listen() Then
                Await Client1.Connect("127.0.0.1", ServerPort)
                Await Client2.Connect("127.0.0.1", ServerPort)
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

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextLog.VerticalOffset >= (TextLog.ExtentHeight - TextLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextLog.AppendText(text & vbCrLf)
                                   If isAtEnd Then TextLog.ScrollToEnd()
                               End Sub)
    End Sub

    Public Sub LogMessage(from As String, text As String)
        Dim isAtEnd As Boolean = ChatLog.VerticalOffset >= (ChatLog.ExtentHeight - ChatLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   ChatLog.AppendText(String.Format("{0}: {1}{2}", {from, text, vbCrLf}))
                                   If isAtEnd Then ChatLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub ChatBox_PreviewKeyDown(sender As Object, e As Input.KeyEventArgs) Handles ChatMessage1.PreviewKeyDown, ChatMessage2.PreviewKeyDown
        Dim controldown = Keyboard.Modifiers = Input.ModifierKeys.Control
        If e.Key = Key.Enter AndAlso Not controldown Then
            e.Handled = True
            If sender Is ChatMessage1 Then
                SendButton1_Click()
            ElseIf sender Is ChatMessage2 Then
                SendButton2_Click()
            End If
        ElseIf controldown AndAlso e.Key = Key.Enter Then
            e.Handled = True
            If sender = ChatMessage1 Then
                Dim lastIndex As Integer = ChatMessage1.CaretIndex
                ChatMessage1.Text = ChatMessage1.Text & vbCrLf
                ChatMessage1.CaretIndex = lastIndex + 1
            ElseIf sender = ChatMessage2 Then
                Dim lastIndex As Integer = ChatMessage2.CaretIndex
                ChatMessage2.Text = ChatMessage2.Text & vbCrLf
                ChatMessage2.CaretIndex = lastIndex + 1
            End If
        End If
    End Sub

    Private Sub SendButton1_Click() Handles SendButton1.Click
        Dim OtherPeer As PeerIdentification = Client1.Peers.Where(Function(x) x.Value.ID <> Client1.RemoteIdentity.ID).FirstOrDefault().Value
        Dim msg As New ChatMessage With {.Text = ChatMessage1.Text, .From = "Client1"}
        Client1.Send(OtherPeer, msg)
        ChatMessage1.Text = Nothing
        ChatMessage1.Focus()
    End Sub

    Private Sub SendButton2_Click() Handles SendButton2.Click
        Dim OtherPeer As PeerIdentification = Client2.Peers.Where(Function(x) x.Value.ID <> Client2.RemoteIdentity.ID).FirstOrDefault().Value
        Dim msg As New ChatMessage With {.Text = ChatMessage2.Text, .From = "Client2"}
        Client2.Send(OtherPeer, msg)
        ChatMessage2.Text = Nothing
        ChatMessage2.Focus()
    End Sub

    Private Sub Client1_OnConnected(sender As Object) Handles Client1.OnConnected
        ChatMessage1.IsEnabled = True
        SendButton1.IsEnabled = True
    End Sub

    Private Sub Client2_OnConnected(sender As Object) Handles Client2.OnConnected
        ChatMessage2.IsEnabled = True
        SendButton2.IsEnabled = True
    End Sub

#End Region

    Public Sub New()

        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        Server.RegisterCallback(Of LoginObj)(AddressOf Server_ClientLogin)
        Client1.RegisterCallback(Of ChatMessage)(AddressOf Client1_ReceivedMessage)
        Client2.RegisterCallback(Of ChatMessage)(AddressOf Client2_ReceivedMessage)
    End Sub

    Private ServerPort As Integer = 7543
    Public WithEvents Server As New TcpServer(ServerPort, "ChatServer") With {.Logging = True, .LogReceiveEvents = True}
    Public WithEvents Client1 As New TcpClient(False, "ChatClient1") With {.Logging = True, .LogReceiveEvents = True}
    Public WithEvents Client2 As New TcpClient(False, "ChatClient2") With {.Logging = True, .LogReceiveEvents = True}


    Private Sub Client1_ReceivedMessage(args As ReceivedEventArgs(Of ChatMessage))
        'LogMessage(args.From.Tag, args.Object.Text)
        LogMessage(args.Object.From, args.Object.Text)
    End Sub

    Private Sub Client2_ReceivedMessage(args As ReceivedEventArgs(Of ChatMessage))
        'LogMessage(args.From.Tag, args.Object.Text)
        LogMessage(args.Object.From, args.Object.Text)
    End Sub

    Private Sub Server_ClientLogin(e As ReceivedEventArgs(Of LoginObj))
        e.Client.RemoteIdentity.Tag = e.Object.UserName
    End Sub

    Private Sub Client1_OnIdentified(ByRef LocalIdentity As PeerIdentification) Handles Client1.OnIdentified
        'LocalIdentity.Tag = "Client1"
        Client1.Send(New LoginObj With {.UserName = "Client1"})
    End Sub

    Private Sub Client2_OnIdentified(ByRef LocalIdentity As PeerIdentification) Handles Client2.OnIdentified
        'LocalIdentity.Tag = "Client2"
        Client2.Send(New LoginObj With {.UserName = "Client2"})
    End Sub

    Private Sub Server_LogOutput(text As String) Handles Server.LogOutput
        Log(text)
    End Sub
End Class
