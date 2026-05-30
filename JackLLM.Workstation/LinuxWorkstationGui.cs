using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace JackLLM.Workstation;

internal sealed class LinuxWorkstationGui : IDisposable
{
    private const long ExposureMask = 1L << 15;
    private const long StructureNotifyMask = 1L << 17;

    private readonly string _chatUrl;
    private readonly string _runtimeName;
    private readonly string _runtimeUrl;
    private readonly int _proxyPort;
    private readonly int _chatPort;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private volatile bool _running = true;

    private IntPtr _display;
    private IntPtr _window;
    private IntPtr _gc;
    private int _windowWidth = 1280;
    private int _windowHeight = 900;

    private LinuxWorkstationGui(
        string chatUrl,
        string runtimeName,
        string runtimeUrl,
        int proxyPort,
        int chatPort,
        Action<string> log)
    {
        _chatUrl = chatUrl;
        _runtimeName = runtimeName;
        _runtimeUrl = runtimeUrl;
        _proxyPort = proxyPort;
        _chatPort = chatPort;
        _log = log;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "JackLLM Linux Workstation GUI"
        };
    }

    public static LinuxWorkstationGui? TryStart(
        string chatUrl,
        string runtimeName,
        string runtimeUrl,
        int proxyPort,
        int chatPort,
        Action<string> log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        if (IsDisabled())
        {
            log("Linux Workstation GUI disabled by JACKLLM_LINUX_GUI.");
            return null;
        }

        var gui = new LinuxWorkstationGui(chatUrl, runtimeName, runtimeUrl, proxyPort, chatPort, log);
        gui._thread.Start();
        return gui;
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        try
        {
            //string displayName = ResolveDisplay();
            //if (string.IsNullOrWhiteSpace(displayName))
            //{
            //    _log("Linux Workstation GUI was not started because no Xorg display was available.");
            //    return;
            //}

            //_display = XOpenDisplay(displayName);
            //if (_display == IntPtr.Zero)
            //{
                //_log("Linux Workstation GUI could not open X display " + displayName + ".");
             //   return;
            //}

            //int screen = XDefaultScreen(_display);
            //IntPtr root = XRootWindow(_display, screen);
            //int displayWidth = Math.Max(900, XDisplayWidth(_display, screen));
            //int displayHeight = Math.Max(620, XDisplayHeight(_display, screen));
            //_windowWidth = Math.Max(980, Math.Min(1280, displayWidth - 120));
            //_windowHeight = Math.Max(720, Math.Min(920, displayHeight - 90));
            //int windowX = Math.Max(20, (displayWidth - _windowWidth) / 2);
            //int windowY = Math.Max(24, (displayHeight - _windowHeight) / 2);
            //_window = XCreateSimpleWindow(_display, root, windowX, windowY, (uint)_windowWidth, (uint)_windowHeight, 1, 0x2e4659, 0x080b10);
            //if (_window == IntPtr.Zero)
            //{
            //    _log("JackLLM MCP could not create an X11 window.");
            //    return;
            //}

            //XStoreName(_display, _window, "JackLLM Workstation");
            //XSelectInput(_display, _window, ExposureMask | StructureNotifyMask);
            //XMapRaised(_display, _window);
            //_gc = XCreateGC(_display, _window, IntPtr.Zero, IntPtr.Zero);
            //_log("Linux Workstation GUI started on display " + displayName + ".");

            DateTimeOffset lastRaise = DateTimeOffset.MinValue;
            while (_running)
            {
                while (XPending(_display) > 0)
                    XNextEvent(_display, out _);

                if ((DateTimeOffset.UtcNow - lastRaise) > TimeSpan.FromSeconds(5)) {
                    XRaiseWindow(_display, _window);
                    lastRaise = DateTimeOffset.UtcNow;
                }

                //Draw();
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            _log("JackLLM MCP stopped: " + ex.Message);
        }
        finally
        {
            if (_display != IntPtr.Zero && _gc != IntPtr.Zero)
                XFreeGC(_display, _gc);
            if (_display != IntPtr.Zero && _window != IntPtr.Zero)
                XDestroyWindow(_display, _window);
            if (_display != IntPtr.Zero)
                XCloseDisplay(_display);
        }
    }

    private void Draw()
    {
        if (_display == IntPtr.Zero || _window == IntPtr.Zero || _gc == IntPtr.Zero)
            return;

        Fill(0, 0, _windowWidth, _windowHeight, 0x080b10);
        DrawWindowChrome();
        DrawTabStrip();
        DrawDiagnosticsBody();
        XFlush(_display);
    }

    private void DrawWindowChrome()
    {
        Fill(0, 0, _windowWidth, 44, 0x111827);
        Line(0, 43, _windowWidth, 43, 0x2e4659);
        Fill(16, 17, 11, 11, 0x58d6c7);
        Text(38, 29, "JackLLM MCP", 0xf3f8ff);
        DrawChromeButton(_windowWidth - 122, 8, "_");
        DrawChromeButton(_windowWidth - 82, 8, "[]");
        DrawChromeButton(_windowWidth - 42, 8, "X", 0x6d2636);
    }

    private void DrawChromeButton(int x, int y, string label, ulong background = 0x182333)
    {
        Fill(x, y, 30, 26, background);
        Line(x, y, x + 30, y, 0x3b5366);
        Line(x, y + 26, x + 30, y + 26, 0x3b5366);
        Line(x, y, x, y + 26, 0x3b5366);
        Line(x + 30, y, x + 30, y + 26, 0x3b5366);
        Text(x + 10, y + 17, label, 0xf3f8ff);
    }

    private void DrawTabStrip()
    {
        int y = 44;
        Fill(0, y, _windowWidth, 42, 0x0e151f);
        DrawTab(18, y + 9, 106, "Diagnostics", true);
        DrawTab(130, y + 9, 76, "Models", false);
        DrawTab(212, y + 9, 152, "Server Management", false);
        DrawTab(370, y + 9, 92, "Sessions", false);
        DrawTab(468, y + 9, 62, "Chat", false);
        Line(0, 85, _windowWidth, 85, 0x2e4659);
    }

    private void DrawTab(int x, int y, int width, string label, bool active)
    {
        Fill(x, y, width, 28, active ? 0x172331UL : 0x101820UL);
        Line(x, y, x + width, y, active ? 0x58d6c7UL : 0x2e4659UL);
        Line(x, y + 28, x + width, y + 28, 0x2e4659);
        Line(x, y, x, y + 28, 0x2e4659);
        Line(x + width, y, x + width, y + 28, 0x2e4659);
        Text(x + 12, y + 18, label, active ? 0xf3f8ffUL : 0x9eb1c5UL);
    }

    private void DrawDiagnosticsBody()
    {
        int margin = 14;
        int top = 100;
        int bottomStatusHeight = 34;
        int contentHeight = _windowHeight - top - bottomStatusHeight - margin;
        Fill(margin, top, _windowWidth - margin * 2, contentHeight, 0x0c121a);
        Line(margin, top, _windowWidth - margin, top, 0x2e4659);
        Line(margin, top + contentHeight, _windowWidth - margin, top + contentHeight, 0x2e4659);
        Line(margin, top, margin, top + contentHeight, 0x2e4659);
        Line(_windowWidth - margin, top, _windowWidth - margin, top + contentHeight, 0x2e4659);

        Text(30, 128, "JackLLM MCP Diagnostics", 0xf3f8ff);
        Text(30, 151, "LM Studio <-> App <-> VS Copilot metrics and debug console", 0x9eb1c5);
        DrawButton(_windowWidth - 400, 116, 106, "Open Chat UI", 0x172331);
        DrawButton(_windowWidth - 286, 116, 128, "Open Companion", 0x172331);
        DrawButton(_windowWidth - 150, 116, 104, "Start Proxy", 0x1f7a68);

        int cardY = 178;
        int cardW = (_windowWidth - 72) / 4;
        DrawMetricCard(30, cardY, cardW, "Proxy", "Running", "11434", 0x68eba2);
        DrawMetricCard(42 + cardW, cardY, cardW, "Web Chat", "Online", "11436", 0x58d6c7);
        DrawMetricCard(54 + cardW * 2, cardY, cardW, "Runtime", _runtimeName, "11435", 0xffbd66);
        DrawMetricCard(66 + cardW * 3, cardY, cardW, "MCP", "Connected", "11573", 0x68eba2);

        int leftW = Math.Max(430, (_windowWidth - 70) / 2);
        int rightX = 42 + leftW;
        int panelY = 310;
        int panelH = Math.Max(250, _windowHeight - panelY - 72);
        DrawPanel(30, panelY, leftW, panelH, "Application");
        DrawCheckRow(52, panelY + 42, true, "Start with Windows");
        DrawSettingRow(52, panelY + 76, "Server name", "z840");
        DrawSettingRow(52, panelY + 110, "Runtime provider", _runtimeName);
        DrawSettingRow(52, panelY + 144, "Chat endpoint", _chatUrl);
        DrawButton(52, panelY + 188, 118, "Open Chat UI", 0x172331);
        DrawButton(182, panelY + 188, 128, "Server Status", 0x172331);
        DrawButton(322, panelY + 188, 104, "Diagnostics", 0x172331);

        DrawPanel(rightX, panelY, _windowWidth - rightX - 30, panelH, "Services");
        DrawServiceRow(rightX + 22, panelY + 44, "Workstation", "Running", 0x68eba2);
        DrawServiceRow(rightX + 22, panelY + 82, "LlmRuntime", "Ready", 0x58d6c7);
        DrawServiceRow(rightX + 22, panelY + 120, "HTTP", "Listening", 0x68eba2);
        DrawServiceRow(rightX + 22, panelY + 158, "VS Copilot", "Proxy ready", 0xffbd66);
        DrawServiceRow(rightX + 22, panelY + 196, "Remote Capture", "OpenCL/X11", 0x68eba2);

        int logY = panelY + panelH - 96;
        Fill(rightX + 18, logY, _windowWidth - rightX - 66, 66, 0x080d13);
        Text(rightX + 32, logY + 24, DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  Workstation UI online", 0x9eb1c5);
        Text(rightX + 32, logY + 46, "Remote input routed through xdotool on Xorg", 0x9eb1c5);

        Fill(0, _windowHeight - bottomStatusHeight, _windowWidth, bottomStatusHeight, 0x101820);
        Line(0, _windowHeight - bottomStatusHeight, _windowWidth, _windowHeight - bottomStatusHeight, 0x2e4659);
        Text(18, _windowHeight - 13, "Ready | Web " + _chatUrl + " | Proxy http://127.0.0.1:" + _proxyPort.ToString(CultureInfo.InvariantCulture) + " | Linux GUI window", 0x9eb1c5);
    }

    private void DrawMetricCard(int x, int y, int width, string title, string value, string subtext, ulong accent)
    {
        Fill(x, y, width, 92, 0x101820);
        StrokeRect(x, y, width, 92, 0x2e4659);
        Fill(x, y, 5, 92, accent);
        Text(x + 18, y + 25, title, 0x9eb1c5);
        Text(x + 18, y + 52, value, accent);
        Text(x + 18, y + 76, subtext, 0xf3f8ff);
    }

    private void DrawPanel(int x, int y, int width, int height, string title)
    {
        Fill(x, y, width, height, 0x101820);
        StrokeRect(x, y, width, height, 0x2e4659);
        Fill(x, y, width, 34, 0x151f2a);
        Line(x, y + 34, x + width, y + 34, 0x2e4659);
        Text(x + 16, y + 22, title, 0xf3f8ff);
    }

    private void DrawCheckRow(int x, int y, bool isChecked, string label)
    {
        Fill(x, y - 12, 16, 16, 0x0b121a);
        StrokeRect(x, y - 12, 16, 16, 0x40586b);
        if (isChecked)
        {
            Line(x + 3, y - 4, x + 7, y + 1, 0x58d6c7);
            Line(x + 7, y + 1, x + 14, y - 10, 0x58d6c7);
        }
        Text(x + 28, y + 1, label, 0xf3f8ff);
    }

    private void DrawSettingRow(int x, int y, string label, string value)
    {
        Text(x, y, label, 0x9eb1c5);
        Fill(x + 150, y - 18, 260, 26, 0x0b121a);
        StrokeRect(x + 150, y - 18, 260, 26, 0x354c5e);
        Text(x + 162, y, TrimForCard(value), 0xf3f8ff);
    }

    private void DrawServiceRow(int x, int y, string service, string status, ulong accent)
    {
        Text(x, y, service, 0xf3f8ff);
        DrawPill(x + 150, y - 17, status, accent);
    }

    private void DrawPill(int x, int y, string label, ulong accent)
    {
        int width = Math.Max(74, Math.Min(180, label.Length * 8 + 22));
        Fill(x, y, width, 24, 0x0b121a);
        StrokeRect(x, y, width, 24, accent);
        Text(x + 10, y + 16, label, accent);
    }

    private void DrawButton(int x, int y, int width, string label, ulong background)
    {
        Fill(x, y, width, 30, background);
        StrokeRect(x, y, width, 30, 0x415b70);
        Text(x + 12, y + 20, label, 0xf3f8ff);
    }

    private void StrokeRect(int x, int y, int width, int height, ulong color)
    {
        Line(x, y, x + width, y, color);
        Line(x, y + height, x + width, y + height, color);
        Line(x, y, x, y + height, color);
        Line(x + width, y, x + width, y + height, color);
    }

    private static string TrimForCard(string value)
    {
        value ??= "";
        return value.Length <= 72 ? value : value.Substring(0, 69) + "...";
    }

    private void Fill(int x, int y, int width, int height, ulong color)
    {
        XSetForeground(_display, _gc, color);
        XFillRectangle(_display, _window, _gc, x, y, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    private void Line(int x1, int y1, int x2, int y2, ulong color)
    {
        XSetForeground(_display, _gc, color);
        XDrawLine(_display, _window, _gc, x1, y1, x2, y2);
    }

    private void Text(int x, int y, string value, ulong color)
    {
        value ??= "";
        XSetForeground(_display, _gc, color);
        XDrawString(_display, _window, _gc, x, y, value, Encoding.ASCII.GetByteCount(value));
    }

    private static bool IsDisabled()
    {
        string value = Environment.GetEnvironmentVariable("JACKLLM_LINUX_GUI") ?? "true";
        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDisplay()
    {
        string xauthority = FirstNonEmpty(
            Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_XAUTHORITY"),
            Environment.GetEnvironmentVariable("XAUTHORITY"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Xauthority"));
        if (!string.IsNullOrWhiteSpace(xauthority))
            Environment.SetEnvironmentVariable("XAUTHORITY", xauthority);

        List<string> displays = [];
        AddDisplayCandidate(displays, Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_DISPLAY"));
        AddDisplayCandidate(displays, Environment.GetEnvironmentVariable("DISPLAY"));
        if (Directory.Exists("/tmp/.X11-unix"))
        {
            foreach (string socket in Directory.GetFiles("/tmp/.X11-unix", "X*", SearchOption.TopDirectoryOnly).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                AddDisplayCandidate(displays, ":" + Path.GetFileName(socket).Substring(1));
        }
        AddDisplayCandidate(displays, ":0");

        foreach (string display in displays)
        {
            IntPtr handle = XOpenDisplay(display);
            if (handle == IntPtr.Zero)
                continue;
            XCloseDisplay(handle);
            Environment.SetEnvironmentVariable("DISPLAY", display);
            return display;
        }

        return "";
    }

    private static void AddDisplayCandidate(List<string> displays, string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return;
        string normalized = display.Trim();
        int dot = normalized.IndexOf('.');
        if (dot > 0)
            normalized = normalized.Substring(0, dot);
        if (!displays.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            displays.Add(normalized);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr XOpenDisplay(string displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XRootWindow(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(IntPtr display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateSimpleWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth, ulong border, ulong background);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern int XStoreName(IntPtr display, IntPtr window, string windowName);

    [DllImport("libX11.so.6")]
    private static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);

    [DllImport("libX11.so.6")]
    private static extern int XMapRaised(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XCreateGC(IntPtr display, IntPtr drawable, IntPtr valuemask, IntPtr values);

    [DllImport("libX11.so.6")]
    private static extern int XFreeGC(IntPtr display, IntPtr gc);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport("libX11.so.6")]
    private static extern int XPending(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XNextEvent(IntPtr display, out XEvent eventReturn);

    [DllImport("libX11.so.6")]
    private static extern int XSetForeground(IntPtr display, IntPtr gc, ulong foreground);

    [DllImport("libX11.so.6")]
    private static extern int XFillRectangle(IntPtr display, IntPtr drawable, IntPtr gc, int x, int y, uint width, uint height);

    [DllImport("libX11.so.6")]
    private static extern int XDrawLine(IntPtr display, IntPtr drawable, IntPtr gc, int x1, int y1, int x2, int y2);

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)]
    private static extern int XDrawString(IntPtr display, IntPtr drawable, IntPtr gc, int x, int y, string text, int length);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [StructLayout(LayoutKind.Sequential)]
    private struct XEvent
    {
        private readonly long _type;
        private readonly long _a;
        private readonly long _b;
        private readonly long _c;
        private readonly long _d;
        private readonly long _e;
        private readonly long _f;
        private readonly long _g;
        private readonly long _h;
        private readonly long _i;
        private readonly long _j;
        private readonly long _k;
        private readonly long _l;
        private readonly long _m;
        private readonly long _n;
        private readonly long _o;
        private readonly long _p;
        private readonly long _q;
        private readonly long _r;
        private readonly long _s;
        private readonly long _t;
        private readonly long _u;
        private readonly long _v;
        private readonly long _w;
        private readonly long _x;
    }
}
