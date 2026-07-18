using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SocketJack.Net;
using Forms = System.Windows.Forms;

namespace JackLLM;

public partial class MainWindow
{
    private static readonly object PcAccessRtmpGate = new();
    private static Process? _pcAccessRtmpProcess;
    private static string _pcAccessRtmpEncoder = "";
    private const int PcAccessVideoBitrateKbps = 750;

    private static byte[] CapturePcAccessDesktopJpeg(int maxWidth, int maxHeight, int quality)
    {
        Forms.Screen screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.First();
        Rectangle bounds = screen.Bounds;
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(source)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        double scale = Math.Min(1d, Math.Min(maxWidth / (double)bounds.Width, maxHeight / (double)bounds.Height));
        using var output = scale < 1d ? new Bitmap(source, Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale))) : new Bitmap(source);
        using var stream = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(25, Math.Min(90, quality)));
        output.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private static void ApplyPcAccessInput(string json)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        JsonElement root = document.RootElement;
        string type = PcString(root, "type").ToLowerInvariant();
        if (type is "move" or "click" or "down" or "up")
        {
            Rectangle bounds = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.First()).Bounds;
            int x = root.TryGetProperty("normalizedX", out JsonElement normalizedX) && normalizedX.TryGetDouble(out double nx)
                ? bounds.Left + (int)Math.Round(Math.Clamp(nx, 0d, 1d) * Math.Max(0, bounds.Width - 1))
                : PcInt(root, "x");
            int y = root.TryGetProperty("normalizedY", out JsonElement normalizedY) && normalizedY.TryGetDouble(out double ny)
                ? bounds.Top + (int)Math.Round(Math.Clamp(ny, 0d, 1d) * Math.Max(0, bounds.Height - 1))
                : PcInt(root, "y");
            string button = PcString(root, "button").ToLowerInvariant();
            uint downFlag = button == "right" ? 0x0008u : 0x0002u;
            uint upFlag = button == "right" ? 0x0010u : 0x0004u;
            SetCursorPos(x, y);
            if (type is "click" or "down") mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
            if (type is "click" or "up") mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
        }
        else if (type == "scroll") mouse_event(0x0800, 0, 0, unchecked((uint)PcInt(root, "delta")), UIntPtr.Zero);
        else if (type == "key")
        {
            byte key = (byte)Math.Max(0, Math.Min(255, PcInt(root, "keyCode")));
            bool down = !root.TryGetProperty("down", out JsonElement downElement) || downElement.ValueKind != JsonValueKind.False;
            keybd_event(key, 0, down ? 0u : 2u, UIntPtr.Zero);
        }
        else if (type == "text")
        {
            string text = PcString(root, "text");
            if (!string.IsNullOrEmpty(text)) Forms.SendKeys.SendWait(text.Replace("{", "{{}").Replace("}", "{}}"));
        }
        else if (type == "clipboard")
        {
            string text = PcString(root, "text");
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text ?? ""));
        }
    }

    private static string PcString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) ? value.ToString() : "";
    private static int PcInt(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static PcAccessRtmpStartResult StartPcAccessRtmpPublisher(PcAccessRtmpStartRequest request)
    {
        StopPcAccessRtmpPublisher();
        string ffmpeg = FindPcAccessFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            return new PcAccessRtmpStartResult { Error = "FFmpeg was not found. Install it in Documents\\FFMPEG or configure FFMPEG_PATH." };
        string tailscaleIp = FindTailscaleIpv4();
        if (string.IsNullOrWhiteSpace(tailscaleIp))
            return new PcAccessRtmpStartResult { Error = "Tailscale is not connected on this Workstation." };

        string encoders = ReadFfmpegEncoders(ffmpeg);
        string[] preferred = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
        var failures = new List<string>();
        foreach (string encoder in preferred.Where(name => encoders.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            string url = $"rtmp://{tailscaleIp}:{request.Port}/jackllm/{request.StreamKey}";
            var arguments = new StringBuilder();
            arguments.Append("-hide_banner -loglevel warning -fflags nobuffer -flags low_delay -thread_queue_size 2 -f gdigrab -draw_mouse 0 ");
            arguments.Append("-framerate ").Append(request.FramesPerSecond).Append(" -i desktop ");
            arguments.Append("-vf \"").Append("scale=").Append(request.Width).Append(':').Append(request.Height)
                .Append(":force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos\" -an -pix_fmt yuv420p ");
            arguments.Append("-c:v ").Append(encoder).Append(' ');
            if (encoder == "libx264") arguments.Append("-preset ultrafast -tune zerolatency -profile:v baseline -level:v 3.1 ");
            else if (encoder == "h264_nvenc") arguments.Append("-preset p1 -tune ull -profile:v baseline -level:v 3.1 -rc cbr -zerolatency 1 -aud 1 ");
            else arguments.Append("-preset veryfast ");
            int keyFrameInterval = Math.Max(5, request.FramesPerSecond / 2);
            arguments.Append("-b:v ").Append(PcAccessVideoBitrateKbps).Append("k -maxrate 850k -bufsize 250k ")
                .Append("-g ").Append(keyFrameInterval).Append(" -keyint_min ").Append(keyFrameInterval)
                .Append(" -bf 0 -flush_packets 1 -muxdelay 0 -muxpreload 0 -flvflags no_duration_filesize -f flv -listen 1 \"").Append(url).Append('"');

            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, arguments.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                },
                EnableRaisingEvents = true
            };
            var error = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && error.Length < 4096) error.AppendLine(e.Data); };
            try
            {
                if (!process.Start()) { process.Dispose(); continue; }
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                if (process.WaitForExit(700))
                {
                    failures.Add(encoder + ": " + error.ToString().Trim());
                    process.Dispose();
                    continue;
                }
                lock (PcAccessRtmpGate)
                {
                    _pcAccessRtmpProcess = process;
                    _pcAccessRtmpEncoder = encoder;
                }
                return new PcAccessRtmpStartResult { Ok = true, Url = url, Encoder = encoder, BitrateKbps = PcAccessVideoBitrateKbps };
            }
            catch (Exception ex)
            {
                failures.Add(encoder + ": " + ex.Message);
                try { process.Dispose(); } catch { }
            }
        }
        return new PcAccessRtmpStartResult { Error = failures.Count == 0 ? "This FFmpeg build has no supported H.264 encoder." : string.Join(" | ", failures) };
    }

    private static void StopPcAccessRtmpPublisher()
    {
        Process? process;
        lock (PcAccessRtmpGate)
        {
            process = _pcAccessRtmpProcess;
            _pcAccessRtmpProcess = null;
            _pcAccessRtmpEncoder = "";
        }
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try { process.Dispose(); } catch { }
    }

    private static PcAccessDesktopState GetPcAccessDesktopState()
    {
        Forms.Screen screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.First();
        Rectangle bounds = screen.Bounds;
        System.Drawing.Point cursor = Forms.Control.MousePosition;
        bool running;
        string encoder;
        lock (PcAccessRtmpGate)
        {
            running = _pcAccessRtmpProcess != null && !_pcAccessRtmpProcess.HasExited;
            encoder = running ? _pcAccessRtmpEncoder : "";
        }
        string tailscaleAddress = FindTailscaleIpv4();
        return new PcAccessDesktopState
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            CursorX = cursor.X,
            CursorY = cursor.Y,
            CursorVisible = IsWindowsCursorVisible(),
            FfmpegAvailable = !string.IsNullOrWhiteSpace(FindPcAccessFfmpeg()),
            TailscaleAvailable = !string.IsNullOrWhiteSpace(tailscaleAddress),
            TailscaleAddress = tailscaleAddress,
            Encoder = encoder
        };
    }

    private static string FindPcAccessFfmpeg()
    {
        string[] candidates =
        {
            Environment.GetEnvironmentVariable("JACKONNX_FFMPEG") ?? "",
            Environment.GetEnvironmentVariable("SOCKETJACK_FFMPEG") ?? "",
            Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "",
            Environment.GetEnvironmentVariable("FFMPEG") ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FFMPEG", "ffmpeg.exe"),
            @"C:\FFMPEG\ffmpeg.exe"
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? "";
    }

    private static string FindTailscaleIpv4()
    {
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] bytes = address.Address.GetAddressBytes();
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return address.Address.ToString();
            }
        }
        return "";
    }

    private static string ReadFfmpegEncoders(string ffmpeg)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return "";
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
        catch { return ""; }
    }

    private static bool IsWindowsCursorVisible()
    {
        var info = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.flags & 0x00000001) != 0;
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public System.Drawing.Point screenPosition;
    }
}
