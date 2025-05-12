Imports System.Windows.Threading
Imports SocketJack.Management

Class Application
    Private Sub Application_Exit(sender As Object, e As ExitEventArgs) Handles Me.[Exit]
        ThreadManager.Shutdown()
    End Sub

    ' Application-level events, such as Startup, Exit, and DispatcherUnhandledException
    ' can be handled in this file.

End Class
