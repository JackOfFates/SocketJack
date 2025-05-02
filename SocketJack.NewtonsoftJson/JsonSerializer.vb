Imports System.Security.Cryptography
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports SocketJack.Serialization

''' <summary>
''' The JSON Serializer Protocol.
''' </summary>
Public Class JsonSerializer
    Implements ISerializationProtocol

    Public Shared Property JsonSettings As New JsonSerializerSettings()

    Public Sub New()

    End Sub

    Public Function Serialize(obj As Object) As Byte() Implements ISerializationProtocol.Serialize
        Dim JsonString As String = JsonConvert.SerializeObject(obj, JsonSettings)
        Return UTF8Encoding.UTF8.GetBytes(JsonString)
    End Function

    Public Function Deserialize(Data As Byte()) As Object Implements ISerializationProtocol.Deserialize
        Return JsonConvert.DeserializeObject(Of ObjectWrapper)(UTF8Encoding.UTF8.GetString(Data), JsonSettings)
    End Function

    Public Function GetPropertyValue(e As PropertyValueArgs) As Object Implements ISerializationProtocol.GetPropertyValue
        Try
            Dim v As JValue = DirectCast(e.Value, JObject)(e.Name)
            If v IsNot Nothing Then Return v.Value Else Return Nothing
        Catch
            Dim jobj As JObject = DirectCast(e.Value, JObject)(e.Name)
            Dim PropertyType As String = e.Reference.Info.PropertyType.AssemblyQualifiedName
            Dim InitializedProperty = jobj.ToObject(Type.GetType(PropertyType))
            Return InitializedProperty
        End Try
    End Function

End Class