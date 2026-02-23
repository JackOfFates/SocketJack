Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Policy
Imports System.Text

Class TestWindow
    Public TestTabs As New Dictionary(Of String, TabItem)
    Private Sub ButtonMaxClients_Click(sender As Object, e As RoutedEventArgs) Handles ButtonMaxClients.Click
        StartTest(New MaxClientsTest With {.Name = "MaxClientsTestControl"})
    End Sub

    Private Sub ButtonBandwidth_Click(sender As Object, e As RoutedEventArgs) Handles ButtonBandwidth.Click
        Dim t = New BandwidthTest With {.Name = "BandwidthTestControl"}
        StartTest(t)
    End Sub

    Private Sub ButtonChat_Click(sender As Object, e As RoutedEventArgs) Handles ButtonChat.Click
        Dim t = New ChatTest With {.Name = "BandwidthTestControl"}
        StartTest(t)
    End Sub
    Private Sub ButtonHttpServer_Click(sender As Object, e As RoutedEventArgs) Handles ButtonHttpServer.Click
        Dim t = New HttpServerTest With {.Name = "HttpServerTestControl"}
        StartTest(t)
    End Sub

    Private Sub ButtonWebSockets_Click(sender As Object, e As RoutedEventArgs) Handles ButtonWebSockets.Click
        Dim t = New WebSocketTest With {.Name = "HttpServerTestControl"}
        StartTest(t)
    End Sub

    Private Sub ButtonControlShare_Click(sender As Object, e As RoutedEventArgs) Handles ButtonControlShare.Click
        Dim t = New ControlShareTest With {.Name = "ControlShareTestControl"}
        StartTest(t)
    End Sub

    Private Sub StartTest(Test As ITest)
        If Not TestTabs.ContainsKey(Test.TestName) Then

            If Height < 720 Then
                Height = 720
                Top = SystemParameters.WorkArea.Height / 2 - ActualHeight / 2
            End If

            Dim Tab As New TabItem
            Tab.Content = Test
            Tab.Header = Test.TestName
            TestTabs.Add(Test.TestName, Tab)
            Tabs.Items.Add(Tab)
            Tab.IsSelected = True
            If Test.AutoStart Then
                Test.StartTest()
            End If

        Else
            TestTabs(Test.TestName).IsSelected = True
            Test = Nothing
        End If
    End Sub

End Class
