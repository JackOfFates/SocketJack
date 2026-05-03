Public Class MagnetLinkDialog

    Public Property MagnetUri As String = ""

    Private Sub OkButton_Click(sender As Object, e As RoutedEventArgs) Handles OkButton.Click
        MagnetUri = MagnetTextBox.Text.Trim()
        If String.IsNullOrWhiteSpace(MagnetUri) Then
            Return
        End If
        DialogResult = True
        Close()
    End Sub

End Class
