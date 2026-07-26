using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using LmVs;

namespace JackLLM;

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

    private void InitializeIntegratedCompanion()
    {
        PreviewKeyDown += CompanionWindow_PreviewKeyDown;
        _proxy.CompanionControlStateChanged += OnCompanionControlStateChanged;
        LoadCompanionPermissions();
        CleanupLegacyCompanionInstallation();
    }

    private static string LaunchCompanionApplication(string target)
    {
        target = (target ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("An application path or registered application name is required.");

        Process? process = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        });
        return process == null ? "The application launch request was submitted." : "Started process " + process.Id + ".";
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

    private static void CleanupLegacyCompanionInstallation()
    {
        string socketJackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack");
        string marker = Path.Combine(socketJackRoot, "integrated-companion-cleanup-v1.marker");
        if (File.Exists(marker))
            return;

        Directory.CreateDirectory(socketJackRoot);
        foreach (Process process in Process.GetProcessesByName("JackLLMCompanion"))
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

        string looseExecutable = Path.Combine(baseDirectory, "JackLLMCompanion.exe");
        if (File.Exists(looseExecutable))
            File.Delete(looseExecutable);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
}
