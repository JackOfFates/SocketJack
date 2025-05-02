Public Interface ITest

    Property TestName As String
    Property AutoStart As Boolean
    Property Running As Boolean
    Sub StartTest()
    Sub StopTest()

End Interface
