Imports Microsoft.Web.WebView2.Core

''' <summary>
''' Opens RuTracker login page in an embedded WebView2 (Chromium) browser so the user can
''' solve the CAPTCHA and log in manually. After login the session cookies
''' are extracted and returned to the caller.
''' </summary>
Public Class RuTrackerLoginWindow

    Private Const LoginUrl As String = "https://rutracker.org/forum/login.php"

    ''' <summary>
    ''' Cookies extracted after the user logs in, in "name=value; name=value" format.
    ''' </summary>
    Public Property SessionCookies As System.Net.CookieCollection

    Public Sub New()
        InitializeComponent()
        AddHandler DoneButton.Click, AddressOf DoneButton_Click
        AddHandler CancelButton.Click, AddressOf CancelButton_Click
        AddHandler Loaded, AddressOf Window_Loaded
    End Sub

    Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs)
        Try
            Await LoginBrowser.EnsureCoreWebView2Async(Nothing)
            LoginBrowser.CoreWebView2.Navigate(LoginUrl)
        Catch ex As Exception
            ' If WebView2 runtime is not installed, show a message and close
            MessageBox.Show("WebView2 runtime is required but was not found." & Environment.NewLine & ex.Message,
                            "RuTracker Login", MessageBoxButton.OK, MessageBoxImage.Error)
            DialogResult = False
            Close()
        End Try
    End Sub

    Private Async Sub DoneButton_Click(sender As Object, e As RoutedEventArgs)
        Try
            ' Use the current browser URL so cookies scoped to /forum/ are included.
            Dim currentUri As String = Nothing
            If LoginBrowser.Source IsNot Nothing Then
                currentUri = LoginBrowser.Source.ToString()
            End If
            If String.IsNullOrEmpty(currentUri) Then
                currentUri = LoginUrl
            End If
            SessionCookies = Await GetWebView2CookiesAsync(New Uri(currentUri))
        Catch
            SessionCookies = Nothing
        End Try
        DialogResult = True
        Close()
    End Sub

    Private Sub CancelButton_Click(sender As Object, e As RoutedEventArgs)
        DialogResult = False
        Close()
    End Sub

    ''' <summary>
    ''' Extracts cookies from the WebView2 (Chromium) cookie manager.
    ''' </summary>
    Private Async Function GetWebView2CookiesAsync(uri As Uri) As Task(Of System.Net.CookieCollection)
        Dim collection As New System.Net.CookieCollection()

        If LoginBrowser.CoreWebView2 Is Nothing Then
            Return collection
        End If

        Dim cookieManager = LoginBrowser.CoreWebView2.CookieManager
        Dim cookies = Await cookieManager.GetCookiesAsync(uri.ToString())

        If cookies IsNot Nothing Then
            For Each cookie As CoreWebView2Cookie In cookies
                ' Use path "/" so CookieContainer.GetCookies matches at the domain root.
                collection.Add(New System.Net.Cookie(cookie.Name, cookie.Value, "/", cookie.Domain.TrimStart("."c)))
            Next
        End If

        Return collection
    End Function

End Class
