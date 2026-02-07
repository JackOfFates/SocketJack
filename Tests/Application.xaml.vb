Imports System.Windows.Threading
Imports SocketJack

Class Application
    Private Sub Application_Exit(sender As Object, e As ExitEventArgs) Handles Me.[Exit]
        ThreadManager.Shutdown()
    End Sub

    Private Sub Application_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
        ThreadManager.UseGlobalTickLoop = True
    End Sub

    ' Application-level events, such as Startup, Exit, and DispatcherUnhandledException
    ' can be handled in this file.

End Class
