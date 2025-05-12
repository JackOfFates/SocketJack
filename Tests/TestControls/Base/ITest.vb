Public Interface ITest

    ReadOnly Property TestName As String
    ReadOnly Property AutoStart As Boolean
    Property Running As Boolean
    Sub StartTest()
    Sub StopTest()

End Interface
