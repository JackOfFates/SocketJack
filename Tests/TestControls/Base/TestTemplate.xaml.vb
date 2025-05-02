Imports System.Collections.Concurrent
Imports System.Threading
Imports System.Windows.Threading
Imports SocketJack.Networking
Imports SocketJack.Networking.[Shared]
Imports SocketJack.Serialization
Imports SocketJack.Extensions

Public Class TestTemplate
    Implements ITest

    Public Event TestStarted()
    Public Event TestStopped()

    Public Overridable Property TestName As String = "TestTemplate" Implements ITest.TestName

    Public Property AutoStart As Boolean Implements ITest.AutoStart
        Get
            Return _AutoStart
        End Get
        Set(value As Boolean)
            _AutoStart = value
        End Set
    End Property
    Private _AutoStart As Boolean = True

    Public Overridable Property Running As Boolean Implements ITest.Running

    Public Sub Log(text As String)
        Dim isAtEnd As Boolean = TextboxLog.VerticalOffset >= (TextboxLog.ExtentHeight - TextboxLog.ViewportHeight) * 0.9
        Dispatcher.InvokeAsync(Sub()
                                   TextboxLog.AppendText(vbCrLf & text)
                                   If isAtEnd Then TextboxLog.ScrollToEnd()
                               End Sub)
    End Sub

    Private Sub ButtonStartStop_Click(sender As Object, e As RoutedEventArgs) Handles ButtonStartStop.Click
        If Running Then
            ITest_StopTest()
        Else
            ITest_StartTest()
        End If
    End Sub

    Private Sub ITest_StartTest() Implements ITest.StartTest
        If Not Running Then
            Log("Test Started.")
            ButtonStartStop.Content = "Stop Test"
            RaiseEvent TestStarted()
        End If
    End Sub

    Private Sub ITest_StopTest() Implements ITest.StopTest
        If Running Then
            RaiseEvent TestStopped()
            ButtonStartStop.Content = "Start Test"
            Log("Test Stopped.")
        End If
    End Sub

End Class
