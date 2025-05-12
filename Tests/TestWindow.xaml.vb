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

    Private Sub StartTest(Test As ITest)
        If Not TestTabs.ContainsKey(Test.TestName) Then
            Me.Height = 450
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
