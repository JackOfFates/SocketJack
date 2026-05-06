using System;

namespace LmVs
{
    public class LmVsProxyScreenCaptureOptions
    {
        public string Target { get; set; } = "admin";
        public int Quality { get; set; } = 82;
        public int MaxWidth { get; set; } = 0;
        public int MaxHeight { get; set; } = 0;
    }

    public class LmVsProxyScreenCaptureResult
    {
        public byte[] Bytes { get; set; }
        public string MimeType { get; set; } = "image/jpeg";
        public int Width { get; set; }
        public int Height { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public string Backend { get; set; } = "";
    }

    public class LmVsProxyRemoteInputRequest
    {
        public string Action { get; set; } = "move";
        public string Button { get; set; } = "left";
        public double X { get; set; }
        public double Y { get; set; }
        public double ToX { get; set; }
        public double ToY { get; set; }
        public bool HasPoint { get; set; }
        public bool HasTargetPoint { get; set; }
        public bool Normalized { get; set; }
        public int DurationMs { get; set; } = 180;
        public int Steps { get; set; } = 24;
        public int Delta { get; set; }
        public string Text { get; set; } = "";
        public string Key { get; set; } = "";
    }

    public class LmVsProxyRemoteInputResult
    {
        public string Action { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public string Message { get; set; } = "";
    }

    public static class LmVsProxyRemoteControl
    {
        public static Func<LmVsProxyScreenCaptureOptions, LmVsProxyScreenCaptureResult> CaptureScreen { get; set; }
        public static Func<LmVsProxyRemoteInputRequest, LmVsProxyRemoteInputResult> ExecuteInput { get; set; }
        public static string ProviderName { get; set; } = "";
        public static bool HasScreenCaptureProvider => CaptureScreen != null;
        public static bool HasInputProvider => ExecuteInput != null;
    }

    /// <summary>
    /// Event arguments for OutputLog events from LmVsProxy.
    /// </summary>
    public class OutputLogEventArgs : EventArgs
    {
        public string Message { get; set; }

        public OutputLogEventArgs(string message)
        {
            Message = message;
        }
    }
}
