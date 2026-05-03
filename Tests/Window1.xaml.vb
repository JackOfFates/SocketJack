Public Class Window1

    Private Sub MinimizeClick(sender As Object, e As RoutedEventArgs)
        WindowState = WindowState.Minimized
    End Sub

    Private Sub MaximizeRestoreClick(sender As Object, e As RoutedEventArgs)
        If WindowState = WindowState.Maximized Then
            WindowState = WindowState.Normal
        Else
            WindowState = WindowState.Maximized
        End If
    End Sub

    Private Sub CloseClick(sender As Object, e As RoutedEventArgs)
        Close()
    End Sub

    Private Sub Window_StateChanged(sender As Object, e As EventArgs)
        If WindowState = WindowState.Maximized Then
            MaxRestoreButton.Content = ChrW(&HE923)
            WindowBorder.BorderThickness = New Thickness(0)
        Else
            MaxRestoreButton.Content = ChrW(&HE922)
            WindowBorder.BorderThickness = New Thickness(1)
        End If
    End Sub

End Class
