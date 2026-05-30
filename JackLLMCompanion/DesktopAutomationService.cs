using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace JackLLMCompanion;

public sealed class DesktopAutomationService
{
    public DesktopScreenCapture CaptureScreen() => CaptureScreen(new DesktopCaptureOptions());

    public DesktopScreenCapture CaptureScreen(DesktopCaptureOptions options)
    {
        options ??= new DesktopCaptureOptions();
        Rectangle bounds = Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        using var stream = new MemoryStream();
        string format = (options.Format ?? "png").Trim().ToLowerInvariant();
        if (format is "jpg" or "jpeg")
        {
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Max(35L, Math.Min(95L, options.Quality)));
            ImageCodecInfo? jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
            if (jpegEncoder != null)
                bitmap.Save(stream, jpegEncoder, encoderParameters);
            else
                bitmap.Save(stream, ImageFormat.Jpeg);
            format = "jpeg";
        }
        else
        {
            bitmap.Save(stream, ImageFormat.Png);
            format = "png";
        }

        DesktopCursorSnapshot cursor = CaptureCursor(bounds);
        return new DesktopScreenCapture
        {
            Bytes = stream.ToArray(),
            MimeType = format == "jpeg" ? "image/jpeg" : "image/png",
            Encoding = format,
            Quality = format == "jpeg" ? (int)Math.Max(35L, Math.Min(95L, options.Quality)) : 100,
            Width = bitmap.Width,
            Height = bitmap.Height,
            Left = bounds.Left,
            Top = bounds.Top,
            Cursor = cursor
        };
    }

    public DesktopInputResult ExecuteInput(DesktopInputRequest request)
    {
        request ??= new DesktopInputRequest();
        string action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (action.Length == 0)
            action = "move";

        if (request.HasPoint || action is "move" or "click" or "doubleclick" or "rightclick" or "drag")
        {
            Point point = ResolvePoint(request.X, request.Y, request.Normalized);
            SetCursorPos(point.X, point.Y);
        }

        switch (action)
        {
            case "move":
                return Result("move", request, "Pointer moved.");
            case "click":
                Click(request.Button);
                return Result("click", request, "Mouse click sent.");
            case "doubleclick":
                Click(request.Button);
                Thread.Sleep(80);
                Click(request.Button);
                return Result("doubleclick", request, "Double-click sent.");
            case "rightclick":
                Click("right");
                return Result("rightclick", request, "Right-click sent.");
            case "drag":
                Drag(request);
                return Result("drag", request, "Drag sent.");
            case "wheel":
                mouse_event(MouseEventWheel, 0, 0, request.Delta == 0 ? 120 : request.Delta, UIntPtr.Zero);
                return Result("wheel", request, "Mouse wheel sent.");
            case "type":
                SendUnicodeText(request.Text ?? "");
                return Result("type", request, "Text sent.");
            case "key":
                SendKey(request.Key ?? "");
                return Result("key", request, "Key sent.");
            default:
                return new DesktopInputResult { Ok = false, Action = action, Message = "Unsupported input action." };
        }
    }

    public CompanionEnvironmentSnapshot CaptureEnvironmentSnapshot(bool includeRecentFiles)
    {
        ForegroundWindowInfo foreground = GetForegroundWindowInfo();
        return new CompanionEnvironmentSnapshot
        {
            Application = foreground.ProcessName,
            Window = foreground.Title,
            Url = InferUrlCue(foreground.ProcessName, foreground.Title),
            Person = InferPersonCue(foreground.ProcessName, foreground.Title),
            Files = includeRecentFiles ? GetRecentUserFiles() : new List<string>()
        };
    }

    private static ForegroundWindowInfo GetForegroundWindowInfo()
    {
        IntPtr handle = GetForegroundWindow();
        string title = "";
        if (handle != IntPtr.Zero)
        {
            var sb = new StringBuilder(512);
            GetWindowText(handle, sb, sb.Capacity);
            title = sb.ToString();
        }

        string processName = "";
        try
        {
            GetWindowThreadProcessId(handle, out uint processId);
            if (processId != 0)
                processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
        }

        return new ForegroundWindowInfo(processName, title);
    }

    private static List<string> GetRecentUserFiles()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        DateTime cutoff = DateTime.Now.AddMinutes(-20);
        var files = new List<FileInfo>();
        foreach (string root in roots)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(info => info.Exists && info.LastWriteTime >= cutoff));
            }
            catch
            {
            }
        }

        return files.OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(20)
            .Select(info => info.FullName)
            .ToList();
    }

    private static string InferUrlCue(string processName, string title)
    {
        string process = (processName ?? "").ToLowerInvariant();
        if (process is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi")
            return "Browser window: " + (title ?? "");
        return "";
    }

    private static string InferPersonCue(string processName, string title)
    {
        string process = (processName ?? "").ToLowerInvariant();
        if (!(process.Contains("discord") || process.Contains("slack") || process.Contains("teams") ||
              process.Contains("telegram") || process.Contains("whatsapp") || process.Contains("outlook")))
            return "";

        string cleaned = (title ?? "").Trim();
        foreach (string separator in new[] { " | ", " - ", " — ", " – " })
        {
            int index = cleaned.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                cleaned = cleaned[..index].Trim();
                break;
            }
        }
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static Point ResolvePoint(double x, double y, bool normalized)
    {
        Rectangle bounds = Forms.SystemInformation.VirtualScreen;
        if (normalized)
        {
            int px = bounds.Left + (int)Math.Round(Math.Max(0, Math.Min(1, x)) * Math.Max(0, bounds.Width - 1));
            int py = bounds.Top + (int)Math.Round(Math.Max(0, Math.Min(1, y)) * Math.Max(0, bounds.Height - 1));
            return new Point(px, py);
        }

        return new Point((int)Math.Round(x), (int)Math.Round(y));
    }

    private static DesktopCursorSnapshot CaptureCursor(Rectangle bounds)
    {
        if (!GetCursorPos(out POINT point))
            return new DesktopCursorSnapshot();

        double normalizedX = bounds.Width <= 1 ? 0 : (point.X - bounds.Left) / (double)(bounds.Width - 1);
        double normalizedY = bounds.Height <= 1 ? 0 : (point.Y - bounds.Top) / (double)(bounds.Height - 1);
        return new DesktopCursorSnapshot
        {
            X = point.X,
            Y = point.Y,
            NormalizedX = Math.Max(0, Math.Min(1, normalizedX)),
            NormalizedY = Math.Max(0, Math.Min(1, normalizedY)),
            Visible = bounds.Contains(point.X, point.Y)
        };
    }

    private static void Click(string? button)
    {
        string normalized = (button ?? "left").Trim().ToLowerInvariant();
        uint down = normalized == "right" ? MouseEventRightDown : MouseEventLeftDown;
        uint up = normalized == "right" ? MouseEventRightUp : MouseEventLeftUp;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    private static void Drag(DesktopInputRequest request)
    {
        Point start = ResolvePoint(request.X, request.Y, request.Normalized);
        Point end = ResolvePoint(request.EndX, request.EndY, request.Normalized);
        string normalized = (request.Button ?? "left").Trim().ToLowerInvariant();
        uint down = normalized == "right" ? MouseEventRightDown : MouseEventLeftDown;
        uint up = normalized == "right" ? MouseEventRightUp : MouseEventLeftUp;
        SetCursorPos(start.X, start.Y);
        Thread.Sleep(60);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        for (int i = 1; i <= 12; i++)
        {
            int x = start.X + (int)Math.Round((end.X - start.X) * (i / 12.0));
            int y = start.Y + (int)Math.Round((end.Y - start.Y) * (i / 12.0));
            SetCursorPos(x, y);
            Thread.Sleep(16);
        }
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    private static void SendUnicodeText(string text)
    {
        foreach (char ch in text ?? "")
        {
            SendKeyboardInput(0, ch, KeyEventUnicode);
            SendKeyboardInput(0, ch, KeyEventUnicode | KeyEventKeyUp);
        }
    }

    private static void SendKey(string key)
    {
        ushort vk = KeyToVirtualKey(key);
        if (vk == 0)
            return;
        SendKeyboardInput(vk, '\0', 0);
        SendKeyboardInput(vk, '\0', KeyEventKeyUp);
    }

    private static ushort KeyToVirtualKey(string key)
    {
        key = (key ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "space" => 0x20,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ when key.Length == 1 => (ushort)char.ToUpperInvariant(key[0]),
            _ => 0
        };
    }

    private static void SendKeyboardInput(ushort virtualKey, char scan, uint flags)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scan,
                    dwFlags = flags,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static DesktopInputResult Result(string action, DesktopInputRequest request, string message)
    {
        return new DesktopInputResult
        {
            Ok = true,
            Action = action,
            X = request.X,
            Y = request.Y,
            Message = message
        };
    }

    private sealed record ForegroundWindowInfo(string ProcessName, string Title);

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventWheel = 0x0800;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public char wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}

public sealed class DesktopCaptureOptions
{
    public string Format { get; set; } = "png";
    public long Quality { get; set; } = 72;
    public bool Adaptive { get; set; }
}

public sealed class DesktopScreenCapture
{
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public string MimeType { get; set; } = "image/png";
    public string Encoding { get; set; } = "png";
    public int Quality { get; set; } = 100;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public DesktopCursorSnapshot Cursor { get; set; } = new();
}

public sealed class DesktopCursorSnapshot
{
    public int X { get; set; }
    public int Y { get; set; }
    public double NormalizedX { get; set; }
    public double NormalizedY { get; set; }
    public bool Visible { get; set; }
}

public sealed class DesktopInputRequest
{
    public string Action { get; set; } = "move";
    public string Button { get; set; } = "left";
    public double X { get; set; }
    public double Y { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public bool HasPoint { get; set; }
    public bool HasEndPoint { get; set; }
    public bool Normalized { get; set; }
    public int Delta { get; set; }
    public string Text { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class DesktopInputResult
{
    public bool Ok { get; set; }
    public string Action { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public string Message { get; set; } = "";
}

public sealed class CompanionEnvironmentSnapshot
{
    public string Application { get; set; } = "";
    public string Window { get; set; } = "";
    public string Url { get; set; } = "";
    public string Person { get; set; } = "";
    public List<string> Files { get; set; } = new();
}
