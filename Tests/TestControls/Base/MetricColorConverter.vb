Imports System.Globalization
Imports System.Windows.Data
Imports System.Windows.Media

''' <summary>
''' Converts numeric metric values to gradient-colored SolidColorBrush.
''' Use ConverterParameter: Time, Cpu, Memory, Cycles, IO, Rating, Result.
''' </summary>
Public Class MetricColorConverter
    Implements IValueConverter

    Private Shared ReadOnly DefaultBrush As New SolidColorBrush(Color.FromRgb(&HEA, &HEA, &HEA))

    Public Function Convert(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.Convert
        If value Is Nothing Then Return DefaultBrush
        Dim param = TryCast(parameter, String)
        If param Is Nothing Then Return DefaultBrush

        Select Case param
            Case "Time"
                ' 0ms = green, 1000ms = red
                Return Lerp(ToDouble(value), 0, 1000,
                            Color.FromRgb(&H0, &HCC, &H0),
                            Color.FromRgb(&HFF, &H44, &H44))
            Case "Cpu"
                ' 0% = green, 100% = red
                Return Lerp(ToDouble(value), 0, 100,
                            Color.FromRgb(&H0, &HCC, &H0),
                            Color.FromRgb(&HFF, &H44, &H44))
            Case "Memory"
                ' 0 = green, 50MB = yellow, 500MB = orange, 1000MB = red
                Dim mb = Math.Abs(ToDouble(value)) / (1024.0 * 1024.0)
                Return LerpStops(mb,
                    New Double() {0, 50, 500, 1000},
                    New Color() {Color.FromRgb(&H9C, &HFF, &H82),
                                 Color.FromRgb(&HFF, &HFF, &H55),
                                 Color.FromRgb(&HFF, &HA5, &H0),
                                 Color.FromRgb(&HFF, &H44, &H44)})
            Case "Cycles"
                ' 0 = green, 1 million = red
                Return Lerp(ToDouble(value), 0, 1000000,
                            Color.FromRgb(&H0, &HCC, &H0),
                            Color.FromRgb(&HFF, &H44, &H44))
            Case "IO"
                ' 0-16KB = green, 512KB = yellow, 50MB = red
                Dim kb = ToDouble(value) / 1024.0
                Return LerpStops(kb,
                    New Double() {0, 16, 512, 50 * 1024},
                    New Color() {Color.FromRgb(&H9C, &HFF, &H82),
                                 Color.FromRgb(&H9C, &HFF, &H82),
                                 Color.FromRgb(&HFF, &HFF, &H55),
                                 Color.FromRgb(&HFF, &H44, &H44)})
            Case "Rating"
                ' 0 = red, 10 = green (inverted)
                Return Lerp(ToDouble(value), 0, 10,
                            Color.FromRgb(&HFF, &H44, &H44),
                            Color.FromRgb(&H0, &HCC, &H0))
            Case "Result"
                ' PASS = green, FAIL = red
                Dim s = TryCast(value, String)
                If s = "PASS" Then Return New SolidColorBrush(Color.FromRgb(&H9C, &HFF, &H82))
                If s = "FAIL" Then Return New SolidColorBrush(Color.FromRgb(&HFF, &H9A, &H9A))
                Return DefaultBrush
            Case Else
                Return DefaultBrush
        End Select
    End Function

    Public Function ConvertBack(value As Object, targetType As Type, parameter As Object, culture As CultureInfo) As Object Implements IValueConverter.ConvertBack
        Throw New NotSupportedException()
    End Function

    Private Shared Function ToDouble(value As Object) As Double
        Try
            Return System.Convert.ToDouble(value)
        Catch
            Return 0
        End Try
    End Function

    Private Shared Function Lerp(value As Double, min As Double, max As Double, fromColor As Color, toColor As Color) As SolidColorBrush
        Dim t = Math.Max(0, Math.Min(1, If(max > min, (value - min) / (max - min), 0)))
        Return New SolidColorBrush(Color.FromRgb(
            CByte(CDbl(fromColor.R) + (CDbl(toColor.R) - CDbl(fromColor.R)) * t),
            CByte(CDbl(fromColor.G) + (CDbl(toColor.G) - CDbl(fromColor.G)) * t),
            CByte(CDbl(fromColor.B) + (CDbl(toColor.B) - CDbl(fromColor.B)) * t)))
    End Function

    Private Shared Function LerpStops(value As Double, stops() As Double, colors() As Color) As SolidColorBrush
        If value <= stops(0) Then Return New SolidColorBrush(colors(0))
        For i = 1 To stops.Length - 1
            If value <= stops(i) Then
                Dim t = (value - stops(i - 1)) / (stops(i) - stops(i - 1))
                Return New SolidColorBrush(Color.FromRgb(
                    CByte(CInt(colors(i - 1).R) + (CInt(colors(i).R) - CInt(colors(i - 1).R)) * t),
                    CByte(CInt(colors(i - 1).G) + (CInt(colors(i).G) - CInt(colors(i - 1).G)) * t),
                    CByte(CInt(colors(i - 1).B) + (CInt(colors(i).B) - CInt(colors(i - 1).B)) * t)))
            End If
        Next
        Return New SolidColorBrush(colors(colors.Length - 1))
    End Function
End Class
