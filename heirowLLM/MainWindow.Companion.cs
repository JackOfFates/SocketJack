using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using heirowLLM;
using Forms = System.Windows.Forms;

namespace heirowLLM;

public partial class MainWindow
{
    private const int CompanionEmergencyHotKeyId = 0x4A43;
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint VkEscape = 0x1B;
    private IntPtr _companionHotKeyWindow;
    private readonly List<CompanionControlOverlayWindow> _companionControlOverlays = new();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out CompanionWindowRect rect);

    private void InitializeIntegratedCompanion()
    {
        PreviewKeyDown += CompanionWindow_PreviewKeyDown;
        _proxy.CompanionControlStateChanged += OnCompanionControlStateChanged;
        LoadCompanionPermissions();
        CleanupLegacyCompanionInstallation();
    }

    private static string LaunchCompanionApplication(string target)
    {
        target = NormalizeCompanionLaunchTarget(target);
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("An application path or registered application name is required.");

        Process? process = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        });
        if (process == null)
            return "The application launch request was submitted.";

        Process foregroundProcess = ResolveCompanionForegroundProcess(process, target);
        IntPtr window = foregroundProcess.MainWindowHandle;
        if (window != IntPtr.Zero)
        {
            ShowWindow(window, 9);
            SetForegroundWindow(window);
        }
        return "Started process " + foregroundProcess.Id +
               (window == IntPtr.Zero ? "." : " and brought its window to the foreground.");
    }

    private static byte[] CaptureCompanionForegroundWindowJpeg(int maxWidth, int maxHeight, int quality)
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out CompanionWindowRect rect))
            return CapturePcAccessDesktopJpeg(maxWidth, maxHeight, quality);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return CapturePcAccessDesktopJpeg(maxWidth, maxHeight, quality);

        using var source = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(source))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);

        double scale = Math.Min(1d, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        using var output = scale < 1d
            ? new System.Drawing.Bitmap(source, Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)))
            : new System.Drawing.Bitmap(source);
        using var stream = new MemoryStream();
        System.Drawing.Imaging.ImageCodecInfo codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(item => item.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            (long)Math.Clamp(quality, 25, 90));
        output.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private static string NormalizeCompanionLaunchTarget(string target)
    {
        // Local models can append a reasoning block after an otherwise valid
        // executable name. Launch only the first non-empty line; never let
        // model commentary become part of a process path.
        string firstLine = (target ?? "")
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        if (firstLine.Length >= 2 &&
            ((firstLine[0] == '"' && firstLine[^1] == '"') ||
             (firstLine[0] == '\'' && firstLine[^1] == '\'')))
        {
            firstLine = firstLine[1..^1].Trim();
        }
        if (firstLine.Contains('<') || firstLine.Contains('>'))
            throw new InvalidOperationException("The application name contains invalid characters.");
        return firstLine;
    }

    private static Process ResolveCompanionForegroundProcess(Process startedProcess, string target)
    {
        try { startedProcess.WaitForInputIdle(5000); } catch { }
        string processName = Path.GetFileNameWithoutExtension(target);
        DateTime launchThreshold = DateTime.Now.AddSeconds(-10);
        DateTime deadline = DateTime.Now.AddSeconds(5);
        do
        {
            startedProcess.Refresh();
            if (!startedProcess.HasExited && startedProcess.MainWindowHandle != IntPtr.Zero)
                return startedProcess;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                Process? windowProcess = Process.GetProcessesByName(processName)
                    .Where(candidate =>
                    {
                        try { return candidate.MainWindowHandle != IntPtr.Zero && candidate.StartTime >= launchThreshold; }
                        catch { return false; }
                    })
                    .OrderByDescending(candidate =>
                    {
                        try { return candidate.StartTime; }
                        catch { return DateTime.MinValue; }
                    })
                    .FirstOrDefault();
                if (windowProcess != null)
                    return windowProcess;
            }
            System.Threading.Thread.Sleep(100);
        }
        while (DateTime.Now < deadline);
        return startedProcess;
    }

    private void ApplyCompanionInput(string json)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        JsonElement root = document.RootElement;
        string type = PcString(root, "type").Trim().ToLowerInvariant();
        if (type == "shortcut")
            ApplyCompanionShortcut(PcString(root, "value"));
        else
            ApplyPcAccessInput(json);

        if (GetCompanionCursorPosition(out CompanionPoint point))
        {
            Dispatcher.Invoke(() =>
            {
                var position = new System.Drawing.Point(point.X, point.Y);
                foreach (CompanionControlOverlayWindow overlay in _companionControlOverlays)
                    overlay.UpdateCursorPosition(position);
            });
        }
    }

    private static void ApplyCompanionShortcut(string shortcut)
    {
        string[] parts = (shortcut ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("A keyboard shortcut is required.");

        var modifiers = new List<byte>();
        byte key = 0;
        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim().ToUpperInvariant();
            byte modifier = part switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "ALT" or "MENU" => 0x12,
                "SHIFT" => 0x10,
                "WIN" or "WINDOWS" => 0x5B,
                _ => 0
            };
            if (modifier != 0)
            {
                if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
                continue;
            }
            key = CompanionVirtualKey(part);
        }

        if (key == 0 && modifiers.Count == 0)
            throw new InvalidOperationException("The keyboard shortcut was not recognized.");
        foreach (byte modifier in modifiers)
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
        if (key != 0)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 2, UIntPtr.Zero);
        }
        for (int index = modifiers.Count - 1; index >= 0; index--)
            keybd_event(modifiers[index], 0, 2, UIntPtr.Zero);
    }

    private static byte CompanionVirtualKey(string token)
    {
        if (token.Length == 1)
        {
            char character = token[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return (byte)character;
        }
        return token switch
        {
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "DELETE" or "DEL" => 0x2E,
            _ when Enum.TryParse(token, true, out Forms.Keys parsed) => (byte)parsed,
            _ => 0
        };
    }

    private void RegisterCompanionEmergencyHotKey(IntPtr window)
    {
        _companionHotKeyWindow = RegisterHotKey(window, CompanionEmergencyHotKeyId, ModControl, VkEscape)
            ? window
            : IntPtr.Zero;
    }

    private void UnregisterCompanionEmergencyHotKey()
    {
        SetCompanionControlOverlayVisible(false);
        _proxy.CompanionControlStateChanged -= OnCompanionControlStateChanged;
        if (_companionHotKeyWindow == IntPtr.Zero)
            return;
        UnregisterHotKey(_companionHotKeyWindow, CompanionEmergencyHotKeyId);
        _companionHotKeyWindow = IntPtr.Zero;
    }

    private bool HandleCompanionWindowMessage(int message, IntPtr wParam)
    {
        if (message != WmHotKey || wParam.ToInt32() != CompanionEmergencyHotKeyId)
            return false;
        EmergencyStopIntegratedCompanion("global Ctrl+Esc");
        return true;
    }

    private void OnCompanionEmergencyStopRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => EmergencyStopIntegratedCompanion("API request")));

    private void OnCompanionControlStateChanged(bool active) =>
        Dispatcher.BeginInvoke(new Action(() => SetCompanionControlOverlayVisible(active)));

    private void CompanionWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        EmergencyStopIntegratedCompanion("Ctrl+Esc");
    }

    private void CompanionEmergencyStopButton_Click(object sender, RoutedEventArgs e) =>
        EmergencyStopIntegratedCompanion("Workstation button");

    private void CompanionLoadPermissionsButton_Click(object sender, RoutedEventArgs e) =>
        LoadCompanionPermissions();

    private void LoadCompanionPermissions()
    {
        ChatClientPermissionSnapshot permissions = _proxy.GetChatClientPermissionsDiagnostics("global");
        CompanionEnabledCheckBox.IsChecked = permissions.CompanionEnabled;
        CompanionScreenViewCheckBox.IsChecked = permissions.CompanionScreenView;
        CompanionCursorControlCheckBox.IsChecked = permissions.CompanionCursorControl;
        CompanionApplicationLaunchCheckBox.IsChecked = permissions.CompanionApplicationLaunch;
        CompanionApplicationControlCheckBox.IsChecked = permissions.CompanionApplicationControl;
        CompanionTerminalCommandsCheckBox.IsChecked = permissions.CompanionTerminalCommands;
        CompanionTranscriptCheckBox.IsChecked = permissions.CompanionActivityTranscriptStorage;
        CompanionSensitiveMemoryCheckBox.IsChecked = permissions.CompanionSensitiveMemory;
        CompanionFinancialActionsCheckBox.IsChecked = permissions.CompanionFinancialActions;
    }

    private void CompanionSavePermissionsButton_Click(object sender, RoutedEventArgs e)
    {
        ChatClientPermissionSnapshot permissions = _proxy.GetChatClientPermissionsDiagnostics("global");
        permissions.CompanionEnabled = CompanionEnabledCheckBox.IsChecked == true;
        permissions.CompanionScreenView = CompanionScreenViewCheckBox.IsChecked == true;
        permissions.CompanionCursorControl = CompanionCursorControlCheckBox.IsChecked == true;
        permissions.CompanionApplicationLaunch = CompanionApplicationLaunchCheckBox.IsChecked == true;
        permissions.CompanionApplicationControl = CompanionApplicationControlCheckBox.IsChecked == true;
        permissions.CompanionTerminalCommands = CompanionTerminalCommandsCheckBox.IsChecked == true;
        permissions.CompanionActivityTranscriptStorage = CompanionTranscriptCheckBox.IsChecked == true;
        permissions.CompanionSensitiveMemory = CompanionSensitiveMemoryCheckBox.IsChecked == true;
        permissions.CompanionFinancialActions = CompanionFinancialActionsCheckBox.IsChecked == true;
        _proxy.SaveChatClientPermissionsDiagnostics(permissions);
        CompanionStatusText.Text = "Permissions saved. Companion is available in Web Chat only when the selected model supports images.";
    }

    private void EmergencyStopIntegratedCompanion(string source)
    {
        try { _proxy.EmergencyStopCompanionControl(); } catch { }
        try { StopPcAccessRtmpPublisher(); } catch { }
        SetCompanionControlOverlayVisible(false);
        CompanionStatusText.Text = "EMERGENCY STOP completed from " + source + ". Companion input and screen capture are stopped.";
    }

    private void SetCompanionControlOverlayVisible(bool visible)
    {
        if (!visible)
        {
            foreach (CompanionControlOverlayWindow overlay in _companionControlOverlays)
            {
                try { overlay.Close(); } catch { }
            }
            _companionControlOverlays.Clear();
            return;
        }

        if (_companionControlOverlays.Count > 0 || _isStopping || _shutdownCompleted)
            return;

        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            CompanionControlOverlayWindow overlay = new(screen);
            _companionControlOverlays.Add(overlay);
            overlay.Show();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCompanionCursorPosition(out CompanionPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct CompanionPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompanionWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void CleanupLegacyCompanionInstallation()
    {
        string socketJackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack");
        string marker = Path.Combine(socketJackRoot, "integrated-companion-cleanup-v1.marker");
        if (File.Exists(marker))
            return;

        Directory.CreateDirectory(socketJackRoot);
        foreach (Process process in Process.GetProcessesByName("heirowLLMCompanion"))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(3000);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        string legacyData = Path.GetFullPath(Path.Combine(socketJackRoot, "Companion"));
        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        string installedCompanion = Path.GetFullPath(Path.Combine(baseDirectory, "Companion"));
        if (legacyData.StartsWith(socketJackRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(legacyData))
            Directory.Delete(legacyData, recursive: true);
        if (installedCompanion.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(installedCompanion))
            Directory.Delete(installedCompanion, recursive: true);

        string looseExecutable = Path.Combine(baseDirectory, "heirowLLMCompanion.exe");
        if (File.Exists(looseExecutable))
            File.Delete(looseExecutable);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
}
