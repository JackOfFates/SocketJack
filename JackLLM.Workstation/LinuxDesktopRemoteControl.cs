using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LmVs;

namespace JackLLM.Workstation;

internal static class LinuxDesktopRemoteControl
{
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GeometryCacheLifetime = TimeSpan.FromSeconds(2);
    private static Action<string>? _log;
    private static string _display = "";
    private static string _xauthority = "";
    private static bool _openClAvailable;
    private static DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;
    private static DesktopGeometry _geometry;
    private static DateTimeOffset _lastGeometryUtc = DateTimeOffset.MinValue;

    public static void TryRegister(Action<string> log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        _log = log;
        lock (SyncRoot)
        {
            LmVsProxyRemoteControl.ProviderName = "SocketJack.Linux.OpenCL/X11";
            LmVsProxyRemoteControl.CaptureScreen = CaptureScreen;
            LmVsProxyRemoteControl.ExecuteInput = ExecuteInput;
        }

        log("Linux desktop remote-control provider registered. Capture uses Xorg/FFmpeg with OpenCL when available; input uses xdotool.");
    }

    private static LmVsProxyScreenCaptureResult CaptureScreen(LmVsProxyScreenCaptureOptions options)
    {
        options ??= new LmVsProxyScreenCaptureOptions();
        DesktopProbe probe = ResolveDesktop();
        DesktopGeometry source = ResolveGeometry(probe);
        DesktopGeometry output = ApplyOutputBounds(source, options);
        int quality = Math.Max(1, Math.Min(100, options.Quality));

        bool tryOpenCl = probe.OpenClAvailable && string.Equals(
            Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_OPENCL") ?? "true",
            "true",
            StringComparison.OrdinalIgnoreCase);

        CaptureAttempt attempt = tryOpenCl
            ? CaptureWithFfmpeg(probe, source, output, quality, useOpenCl: true)
            : CaptureAttempt.Failed("OpenCL capture was not requested or no OpenCL platform was detected.");

        if (!attempt.Success)
            attempt = CaptureWithFfmpeg(probe, source, output, quality, useOpenCl: false);

        if (!attempt.Success || attempt.Bytes.Length == 0)
            throw new InvalidOperationException(attempt.Error.Length == 0 ? "Linux desktop capture failed." : attempt.Error);

        return new LmVsProxyScreenCaptureResult
        {
            Bytes = attempt.Bytes,
            MimeType = "image/jpeg",
            Width = output.Width,
            Height = output.Height,
            Left = source.Left,
            Top = source.Top,
            Backend = attempt.Backend
        };
    }

    private static CaptureAttempt CaptureWithFfmpeg(DesktopProbe probe, DesktopGeometry source, DesktopGeometry output, int quality, bool useOpenCl)
    {
        if (!CommandExists("ffmpeg"))
            return CaptureAttempt.Failed("ffmpeg is required for Linux desktop capture.");

        int qScale = Math.Max(2, Math.Min(31, 31 - (int)Math.Round(quality / 100.0 * 29)));
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error"
        };

        if (useOpenCl)
        {
            args.Add("-init_hw_device");
            args.Add("opencl=ocl");
            args.Add("-filter_hw_device");
            args.Add("ocl");
        }

        args.AddRange(new[]
        {
            "-f",
            "x11grab",
            "-draw_mouse",
            "1",
            "-video_size",
            source.Width.ToString(CultureInfo.InvariantCulture) + "x" + source.Height.ToString(CultureInfo.InvariantCulture),
            "-i",
            BuildDisplayInput(probe.Display, source)
        });

        string filter = BuildVideoFilter(output, useOpenCl);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.Add("-vf");
            args.Add(filter);
        }

        args.AddRange(new[]
        {
            "-frames:v",
            "1",
            "-q:v",
            qScale.ToString(CultureInfo.InvariantCulture),
            "-f",
            "mjpeg",
            "pipe:1"
        });

        ProcessResult result = RunProcess("ffmpeg", args, probe, timeoutMs: 8000, captureStdout: true);
        if (result.ExitCode != 0 || result.StdoutBytes.Length == 0)
        {
            string backend = useOpenCl ? "FFmpeg x11grab OpenCL" : "FFmpeg x11grab";
            return CaptureAttempt.Failed(backend + " failed: " + result.ErrorText);
        }

        return CaptureAttempt.Ok(
            result.StdoutBytes,
            useOpenCl ? "FFmpeg x11grab + OpenCL filter pipeline" : "FFmpeg x11grab software encode");
    }

    private static string BuildVideoFilter(DesktopGeometry output, bool useOpenCl)
    {
        if (output.SourceWidth == output.Width && output.SourceHeight == output.Height)
        {
            return useOpenCl
                ? "format=rgba,hwupload,unsharp_opencl=lx=1:ly=1:la=0:cx=1:cy=1:ca=0,hwdownload,format=rgba"
                : "";
        }

        string scale = "scale=" + output.Width.ToString(CultureInfo.InvariantCulture) + ":" + output.Height.ToString(CultureInfo.InvariantCulture);
        if (!useOpenCl)
            return scale;

        return scale + ",format=rgba,hwupload,unsharp_opencl=lx=1:ly=1:la=0:cx=1:cy=1:ca=0,hwdownload,format=rgba";
    }

    private static LmVsProxyRemoteInputResult ExecuteInput(LmVsProxyRemoteInputRequest input)
    {
        if (!CommandExists("xdotool"))
            throw new InvalidOperationException("xdotool is required for Linux desktop remote input.");

        input ??= new LmVsProxyRemoteInputRequest();
        DesktopProbe probe = ResolveDesktop();
        DesktopGeometry geometry = ResolveGeometry(probe);
        DesktopPoint start = ResolvePoint(geometry, input.HasPoint ? input.X : 0.5, input.HasPoint ? input.Y : 0.5, input.HasPoint ? input.Normalized : true);
        DesktopPoint target = input.HasTargetPoint ? ResolvePoint(geometry, input.ToX, input.ToY, input.Normalized) : start;
        string action = (input.Action ?? "move").Trim().ToLowerInvariant();

        List<string> args = action switch
        {
            "move" or "mousemove" => XdoMove(start),
            "click" or "mouseclick" => XdoClick(start, input.Button),
            "doubleclick" or "double-click" => XdoDoubleClick(start, input.Button),
            "down" or "mousedown" => XdoMouseButton(start, "mousedown", input.Button),
            "up" or "mouseup" => XdoMouseButton(start, "mouseup", input.Button),
            "drag" or "mousedrag" => XdoDrag(start, target, input.Button),
            "wheel" or "scroll" => XdoWheel(start, input.Delta),
            "text" or "type" => XdoType(start, input.Text),
            "key" or "keypress" => XdoKey(input.Key),
            _ => throw new NotSupportedException("Unsupported Linux remote input action: " + input.Action)
        };

        ProcessResult result = RunProcess("xdotool", args, probe, timeoutMs: 5000, captureStdout: false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("xdotool failed: " + result.ErrorText);

        return new LmVsProxyRemoteInputResult
        {
            Action = action,
            X = start.X,
            Y = start.Y,
            Message = "Linux Xorg remote input executed through xdotool."
        };
    }

    private static List<string> XdoMove(DesktopPoint point) =>
        ["mousemove", point.X.ToString(CultureInfo.InvariantCulture), point.Y.ToString(CultureInfo.InvariantCulture)];

    private static List<string> XdoClick(DesktopPoint point, string button)
    {
        var args = XdoMove(point);
        args.Add("click");
        args.Add(ParseButton(button));
        return args;
    }

    private static List<string> XdoDoubleClick(DesktopPoint point, string button)
    {
        var args = XdoMove(point);
        args.AddRange(["click", "--repeat", "2", "--delay", "80", ParseButton(button)]);
        return args;
    }

    private static List<string> XdoMouseButton(DesktopPoint point, string command, string button)
    {
        var args = XdoMove(point);
        args.Add(command);
        args.Add(ParseButton(button));
        return args;
    }

    private static List<string> XdoDrag(DesktopPoint start, DesktopPoint target, string button)
    {
        string parsedButton = ParseButton(button);
        return
        [
            "mousemove", start.X.ToString(CultureInfo.InvariantCulture), start.Y.ToString(CultureInfo.InvariantCulture),
            "mousedown", parsedButton,
            "mousemove", "--sync", target.X.ToString(CultureInfo.InvariantCulture), target.Y.ToString(CultureInfo.InvariantCulture),
            "mouseup", parsedButton
        ];
    }

    private static List<string> XdoWheel(DesktopPoint point, int delta)
    {
        var args = XdoMove(point);
        int clicks = Math.Max(1, Math.Min(12, Math.Abs(delta == 0 ? 120 : delta) / 120));
        string button = delta > 0 ? "4" : "5";
        for (int i = 0; i < clicks; i++)
        {
            args.Add("click");
            args.Add(button);
        }
        return args;
    }

    private static List<string> XdoType(DesktopPoint point, string text)
    {
        var args = XdoClick(point, "left");
        args.AddRange(["type", "--clearmodifiers", "--delay", "1", text ?? ""]);
        return args;
    }

    private static List<string> XdoKey(string key) =>
        ["key", "--clearmodifiers", NormalizeKey(key)];

    private static string ParseButton(string button)
    {
        string normalized = (button ?? "left").Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "left" => "1",
            "2" or "middle" or "wheel" => "2",
            "3" or "right" => "3",
            "4" or "up" or "wheelup" => "4",
            "5" or "down" or "wheeldown" => "5",
            _ => "1"
        };
    }

    private static string NormalizeKey(string key)
    {
        string normalized = (key ?? "").Trim();
        if (normalized.Length == 0)
            return "Return";

        string lower = normalized.ToLowerInvariant();
        string mapped = lower switch
        {
            "enter" => "Return",
            "escape" or "esc" => "Escape",
            "tab" => "Tab",
            "backspace" => "BackSpace",
            "delete" or "del" => "Delete",
            "space" => "space",
            "left" or "arrowleft" => "Left",
            "right" or "arrowright" => "Right",
            "up" or "arrowup" => "Up",
            "down" or "arrowdown" => "Down",
            "home" => "Home",
            "end" => "End",
            "pageup" => "Page_Up",
            "pagedown" => "Page_Down",
            _ => normalized
        };

        if (!Regex.IsMatch(mapped, "^[A-Za-z0-9_+\\-]+$"))
            throw new InvalidOperationException("Unsupported key name for Linux remote input.");
        return mapped;
    }

    private static DesktopPoint ResolvePoint(DesktopGeometry geometry, double x, double y, bool normalized)
    {
        double px = geometry.Left + (normalized ? Clamp(x, 0, 1) * geometry.Width : Clamp(x, 0, geometry.Width));
        double py = geometry.Top + (normalized ? Clamp(y, 0, 1) * geometry.Height : Clamp(y, 0, geometry.Height));
        return new DesktopPoint((int)Math.Round(px), (int)Math.Round(py));
    }

    private static DesktopGeometry ApplyOutputBounds(DesktopGeometry source, LmVsProxyScreenCaptureOptions options)
    {
        int maxWidth = Math.Max(0, options?.MaxWidth ?? 0);
        int maxHeight = Math.Max(0, options?.MaxHeight ?? 0);
        if (maxWidth <= 0 && maxHeight <= 0)
            return source with { SourceWidth = source.Width, SourceHeight = source.Height };

        double scale = 1.0;
        if (maxWidth > 0)
            scale = Math.Min(scale, maxWidth / (double)source.Width);
        if (maxHeight > 0)
            scale = Math.Min(scale, maxHeight / (double)source.Height);
        if (scale >= 1.0)
            return source with { SourceWidth = source.Width, SourceHeight = source.Height };

        return new DesktopGeometry(
            source.Left,
            source.Top,
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            source.Width,
            source.Height);
    }

    private static DesktopProbe ResolveDesktop()
    {
        lock (SyncRoot)
        {
            if ((DateTimeOffset.UtcNow - _lastProbeUtc) < ProbeCacheLifetime && !string.IsNullOrWhiteSpace(_display))
                return new DesktopProbe(_display, _xauthority, _openClAvailable);
        }

        string xauthority = FirstNonEmpty(
            Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_XAUTHORITY"),
            Environment.GetEnvironmentVariable("XAUTHORITY"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Xauthority"));

        List<string> displays = [];
        AddDisplayCandidate(displays, Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_DISPLAY"));
        AddDisplayCandidate(displays, Environment.GetEnvironmentVariable("DISPLAY"));
        if (Directory.Exists("/tmp/.X11-unix"))
        {
            foreach (string socket in Directory.GetFiles("/tmp/.X11-unix", "X*", SearchOption.TopDirectoryOnly).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                AddDisplayCandidate(displays, ":" + Path.GetFileName(socket).Substring(1));
        }
        AddDisplayCandidate(displays, ":0");

        string display = displays.FirstOrDefault(candidate => ProbeDisplay(candidate, xauthority)) ??
            throw new InvalidOperationException("No accessible Xorg display was found. Set JACKLLM_CAPTURE_DISPLAY and JACKLLM_CAPTURE_XAUTHORITY.");
        bool openCl = ProbeOpenCl();

        lock (SyncRoot)
        {
            _display = display;
            _xauthority = xauthority;
            _openClAvailable = openCl;
            _lastProbeUtc = DateTimeOffset.UtcNow;
        }

        if (!openCl)
            _log?.Invoke("OpenCL capture path unavailable: no OpenCL platform was detected. Linux capture will use FFmpeg x11grab software fallback.");

        return new DesktopProbe(display, xauthority, openCl);
    }

    private static DesktopGeometry ResolveGeometry(DesktopProbe probe)
    {
        lock (SyncRoot)
        {
            if ((DateTimeOffset.UtcNow - _lastGeometryUtc) < GeometryCacheLifetime && _geometry.Width > 0 && _geometry.Height > 0)
                return _geometry;
        }

        ProcessResult result = RunProcess("xdpyinfo", [], probe, timeoutMs: 2500, captureStdout: true);
        string text = result.StdoutText + "\n" + result.ErrorText;
        Match match = Regex.Match(text, @"dimensions:\s*(\d+)x(\d+)\s+pixels", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException("Unable to read Xorg display dimensions: " + result.ErrorText);

        var displayGeometry = new DesktopGeometry(
            0,
            0,
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        DesktopGeometry geometry = TryResolveWindowGeometry(probe) ?? displayGeometry;

        lock (SyncRoot)
        {
            _geometry = geometry;
            _lastGeometryUtc = DateTimeOffset.UtcNow;
        }

        return geometry;
    }

    private static DesktopGeometry? TryResolveWindowGeometry(DesktopProbe probe)
    {
        string title = FirstNonEmpty(Environment.GetEnvironmentVariable("JACKLLM_CAPTURE_WINDOW_TITLE"), "JackLLM Workstation");
        if (!CommandExists("xdotool"))
            return null;

        ProcessResult search = RunProcess("xdotool", ["search", "--name", title], probe, timeoutMs: 2500, captureStdout: true);
        if (search.ExitCode != 0 || string.IsNullOrWhiteSpace(search.StdoutText))
            return null;

        string? id = search.StdoutText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        ProcessResult result = RunProcess("xdotool", ["getwindowgeometry", "--shell", id], probe, timeoutMs: 2500, captureStdout: true);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdoutText))
            return null;

        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in result.StdoutText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            if (int.TryParse(line.Substring(equals + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                values[line.Substring(0, equals)] = value;
        }

        if (!values.TryGetValue("X", out int left) ||
            !values.TryGetValue("Y", out int top) ||
            !values.TryGetValue("WIDTH", out int width) ||
            !values.TryGetValue("HEIGHT", out int height) ||
            width <= 0 ||
            height <= 0)
            return null;

        return new DesktopGeometry(left, top, width, height, width, height);
    }

    private static string BuildDisplayInput(string display, DesktopGeometry source)
    {
        if (source.Left == 0 && source.Top == 0)
            return display;
        return display + "+" + source.Left.ToString(CultureInfo.InvariantCulture) + "," + source.Top.ToString(CultureInfo.InvariantCulture);
    }

    private static bool ProbeDisplay(string display, string xauthority)
    {
        if (string.IsNullOrWhiteSpace(display) || !CommandExists("xdpyinfo"))
            return false;
        return RunProcess("xdpyinfo", [], new DesktopProbe(display, xauthority, false), timeoutMs: 2500, captureStdout: false).ExitCode == 0;
    }

    private static bool ProbeOpenCl()
    {
        if (!CommandExists("ffmpeg"))
            return false;

        ProcessResult result = RunProcess(
            "ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-init_hw_device", "opencl=ocl", "-f", "lavfi", "-i", "color=s=16x16:d=0.01", "-frames:v", "1", "-f", "null", "-"],
            new DesktopProbe("", "", false),
            timeoutMs: 5000,
            captureStdout: false);
        return result.ExitCode == 0;
    }

    private static bool CommandExists(string command)
    {
        ProcessResult result = RunProcess("sh", ["-c", "command -v " + ShellQuote(command) + " >/dev/null 2>&1"], new DesktopProbe("", "", false), timeoutMs: 2000, captureStdout: false);
        return result.ExitCode == 0;
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> args, DesktopProbe probe, int timeoutMs, bool captureStdout)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        if (!string.IsNullOrWhiteSpace(probe.Display))
            process.StartInfo.Environment["DISPLAY"] = probe.Display;
        if (!string.IsNullOrWhiteSpace(probe.Xauthority))
            process.StartInfo.Environment["XAUTHORITY"] = probe.Xauthority;
        foreach (string arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var output = new MemoryStream();
        var error = new StringBuilder();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                error.AppendLine(eventArgs.Data);
        };

        process.Start();
        Task copyOutput = captureStdout
            ? process.StandardOutput.BaseStream.CopyToAsync(output)
            : process.StandardOutput.ReadToEndAsync();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(fileName + " timed out.");
        }

        copyOutput.GetAwaiter().GetResult();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.ToArray(), error.ToString().Trim());
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

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return min;
        return Math.Max(min, Math.Min(max, value));
    }

    private static string ShellQuote(string value) => "'" + (value ?? "").Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed record DesktopProbe(string Display, string Xauthority, bool OpenClAvailable);
    private readonly record struct DesktopPoint(int X, int Y);
    private readonly record struct DesktopGeometry(int Left, int Top, int Width, int Height, int SourceWidth, int SourceHeight);

    private sealed record CaptureAttempt(bool Success, byte[] Bytes, string Backend, string Error)
    {
        public static CaptureAttempt Ok(byte[] bytes, string backend) => new(true, bytes, backend, "");
        public static CaptureAttempt Failed(string error) => new(false, Array.Empty<byte>(), "", error ?? "");
    }

    private sealed record ProcessResult(int ExitCode, byte[] StdoutBytes, string ErrorText)
    {
        public string StdoutText => Encoding.UTF8.GetString(StdoutBytes);
    }
}
