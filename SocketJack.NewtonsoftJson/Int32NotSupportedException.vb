Public Class Int32NotSupportedException
    Inherits Exception

    Public Overrides ReadOnly Property Message As String = "Change the Class's Property listed in Int32NotSupportedException.AffectedProperty to Int64."

    ''' <summary>
    ''' This property should be type of Int64.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property AffectedProperty As String
        Get
            Return _AffectedProperty
        End Get
    End Property
    Private _AffectedProperty As String
    Public Sub New(AffectedProperty As String)
        _AffectedProperty = AffectedProperty
    End Sub
End Class