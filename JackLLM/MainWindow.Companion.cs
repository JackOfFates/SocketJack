using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LmVs;

namespace JackLLM;

public partial class MainWindow
{
    private CancellationTokenSource? _companionCancellation;
    private readonly HttpClient _companionHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string CompanionFinancialPhrase = "I UNDERSTAND THIS CAN CAUSE FINANCIAL LOSS";
    private const int CompanionEmergencyHotKeyId = 0x4A43;
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint VkEscape = 0x1B;
    private IntPtr _companionHotKeyWindow;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void InitializeIntegratedCompanion()
    {
        PreviewKeyDown += CompanionWindow_PreviewKeyDown;
        CompanionModelTextBox.Text = "";
        LoadCompanionPermissions();
        CleanupLegacyCompanionInstallation();
    }

    private void RegisterCompanionEmergencyHotKey(IntPtr window)
    {
        _companionHotKeyWindow = RegisterHotKey(window, CompanionEmergencyHotKeyId, ModControl, VkEscape)
            ? window
            : IntPtr.Zero;
    }

    private void UnregisterCompanionEmergencyHotKey()
    {
        if (_companionHotKeyWindow == IntPtr.Zero) return;
        UnregisterHotKey(_companionHotKeyWindow, CompanionEmergencyHotKeyId);
        _companionHotKeyWindow = IntPtr.Zero;
    }

    private bool HandleCompanionWindowMessage(int message, IntPtr wParam)
    {
        if (message != WmHotKey || wParam.ToInt32() != CompanionEmergencyHotKeyId) return false;
        EmergencyStopIntegratedCompanion("global Ctrl+Esc");
        return true;
    }

    private static string LaunchCompanionApplication(string target)
    {
        target = (target ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("An application path or registered application name is required.");
        Process? process = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        });
        return process == null ? "The application launch request was submitted." : "Started process " + process.Id + ".";
    }

    private void OnCompanionEmergencyStopRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => EmergencyStopIntegratedCompanion("API request")));
    }

    private void CompanionWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            EmergencyStopIntegratedCompanion("Ctrl+Esc");
        }
    }

    private void CompanionEmergencyStopButton_Click(object sender, RoutedEventArgs e) =>
        EmergencyStopIntegratedCompanion("Workstation button");

    private void CompanionStopButton_Click(object sender, RoutedEventArgs e)
    {
        _companionCancellation?.Cancel();
        CompanionStatusText.Text = "Companion stopped.";
    }

    private async void CompanionStartButton_Click(object sender, RoutedEventArgs e)
    {
        string goal = CompanionGoalTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(goal))
        {
            CompanionStatusText.Text = "Enter a Companion goal first.";
            return;
        }

        _companionCancellation?.Cancel();
        _companionCancellation?.Dispose();
        _companionCancellation = new CancellationTokenSource();
        try
        {
            await RunIntegratedCompanionAsync(goal, CompanionModelTextBox.Text.Trim(), _companionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            CompanionStatusText.Text = "Companion stopped.";
        }
        catch (Exception ex)
        {
            CompanionStatusText.Text = "Companion failed: " + SocketJack.Net.LmVsProxy.RedactCompanionSensitiveText(ex.Message);
        }
    }

    private async Task RunIntegratedCompanionAsync(string goal, string model, CancellationToken cancellationToken)
    {
        ChatClientPermissionSnapshot permissions = _proxy.GetChatClientPermissionsDiagnostics("global");
        if (!permissions.CompanionEnabled) throw new InvalidOperationException("Enable Companion mode in its permission panel first.");
        model = string.IsNullOrWhiteSpace(model) ? "auto" : model;
        await PostCompanionJsonAsync("/api/companion/task", new { action = "start", goal }, cancellationToken);

        for (int step = 1; step <= 20; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompanionStatusText.Text = "Companion step " + step + ": observing and planning...";

            string screenData = "";
            if (permissions.CompanionScreenView)
            {
                byte[] jpeg = CapturePcAccessDesktopJpeg(1280, 720, 65);
                screenData = Convert.ToBase64String(jpeg);
                ShowCompanionPreview(jpeg);
            }

            string permissionSummary = JsonSerializer.Serialize(new
            {
                permissions.CompanionScreenView,
                permissions.CompanionCursorControl,
                permissions.CompanionApplicationLaunch,
                permissions.CompanionApplicationControl,
                permissions.CompanionTerminalCommands,
                permissions.CompanionFinancialActions
            });
            string instruction =
                "You are Companion mode inside JackLLM Workstation. Perform one small desktop action toward the goal. " +
                "Return JSON only with type and value, plus normalizedX/normalizedY when needed. " +
                "Allowed types: observe, move, click, text, key, scroll, launch, terminal, done. " +
                "Never claim an action ran before its tool result. Never handle passwords or credentials. " +
                "Financial actions require a separate local human confirmation and must be described honestly. " +
                "Permissions: " + permissionSummary + "\nGoal: " + goal;

            object userContent = string.IsNullOrWhiteSpace(screenData)
                ? instruction
                : new object[]
                {
                    new { type = "text", text = instruction },
                    new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + screenData } }
                };
            string response = await PostModelAsync(model, userContent, cancellationToken);
            string json = ExtractCompanionJson(response);
            using JsonDocument actionDocument = JsonDocument.Parse(json);
            JsonElement root = actionDocument.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.ToString().Trim().ToLowerInvariant() : "";
            if (type == "done")
            {
                CompanionStatusText.Text = "Companion completed the task.";
                await PostCompanionJsonAsync("/api/companion/task", new { action = "stop" }, cancellationToken);
                return;
            }

            string actionBody = root.GetRawText();
            HttpResponseMessage actionResponse = await PostCompanionRawAsync("/api/companion/action", actionBody, cancellationToken);
            if ((int)actionResponse.StatusCode == 428)
            {
                string value = root.TryGetProperty("value", out JsonElement valueElement) ? valueElement.ToString() : "";
                string ownerKey = await GetCompanionOwnerKeyAsync(cancellationToken);
                string phrase = PromptForCompanionPhrase(
                    "EXTREME FINANCIAL RISK",
                    "This action can cause charges, recurring billing, lost accounts, irreversible transfers, paid trials, or severe financial destruction.\n\nAction: " + type + " — " + value,
                    CompanionFinancialPhrase);
                if (!string.Equals(phrase, CompanionFinancialPhrase, StringComparison.Ordinal))
                    throw new OperationCanceledException("Financial action was not confirmed.");
                string confirmationJson = await PostCompanionJsonAsync("/api/companion/confirm",
                    new { ownerKey, kind = "financial", action = type + "\n" + value, phrase }, cancellationToken);
                using JsonDocument confirmation = JsonDocument.Parse(confirmationJson);
                string token = confirmation.RootElement.GetProperty("confirmationToken").GetString() ?? "";
                var retry = new System.Collections.Generic.Dictionary<string, object?>();
                foreach (JsonProperty property in root.EnumerateObject()) retry[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
                retry["confirmationToken"] = token;
                actionResponse = await PostCompanionRawAsync("/api/companion/action", JsonSerializer.Serialize(retry), cancellationToken);
            }
            string result = await actionResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!actionResponse.IsSuccessStatusCode) throw new InvalidOperationException(result);
            CompanionStatusText.Text = "Companion step " + step + " executed: " + type;
        }
        CompanionStatusText.Text = "Companion stopped after the 20-step safety limit.";
    }

    private async Task<string> PostModelAsync(string model, object userContent, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = userContent } },
            temperature = 0.1,
            max_tokens = 500
        });
        using HttpResponseMessage response = await _companionHttp.PostAsync(
            "http://127.0.0.1:" + LocalLmStudioProxyPort + "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").ToString();
    }

    private async Task<string> PostCompanionJsonAsync(string route, object body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostCompanionRawAsync(route, JsonSerializer.Serialize(body), cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(text);
        return text;
    }

    private Task<HttpResponseMessage> PostCompanionRawAsync(string route, string body, CancellationToken cancellationToken) =>
        _companionHttp.PostAsync("http://127.0.0.1:" + ChatServerPort + route, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);

    private async Task<string> GetCompanionOwnerKeyAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _companionHttp.GetAsync("http://127.0.0.1:" + ChatServerPort + "/api/companion/status", cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("ownerKey").GetString() ?? "global";
    }

    private static string ExtractCompanionJson(string response)
    {
        string text = (response ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int newline = text.IndexOf('\n');
            int end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (newline >= 0 && end > newline) text = text.Substring(newline + 1, end - newline - 1).Trim();
        }
        int start = text.IndexOf('{'), finish = text.LastIndexOf('}');
        return start >= 0 && finish > start ? text.Substring(start, finish - start + 1) : text;
    }

    private void CompanionPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        ChatClientPermissionSnapshot permissions = _proxy.GetChatClientPermissionsDiagnostics("global");
        if (!permissions.CompanionEnabled || !permissions.CompanionScreenView)
        {
            CompanionStatusText.Text = "Enable Companion mode and See the screen first.";
            return;
        }
        byte[] jpeg = CapturePcAccessDesktopJpeg(1280, 720, 65);
        ShowCompanionPreview(jpeg);
        CompanionStatusText.Text = "Screen preview captured in memory; it was not saved.";
    }

    private void ShowCompanionPreview(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        CompanionPreviewImage.Source = image;
    }

    private void CompanionLoadPermissionsButton_Click(object sender, RoutedEventArgs e) => LoadCompanionPermissions();

    private void LoadCompanionPermissions()
    {
        ChatClientPermissionSnapshot p = _proxy.GetChatClientPermissionsDiagnostics("global");
        CompanionEnabledCheckBox.IsChecked = p.CompanionEnabled;
        CompanionScreenViewCheckBox.IsChecked = p.CompanionScreenView;
        CompanionCursorControlCheckBox.IsChecked = p.CompanionCursorControl;
        CompanionApplicationLaunchCheckBox.IsChecked = p.CompanionApplicationLaunch;
        CompanionApplicationControlCheckBox.IsChecked = p.CompanionApplicationControl;
        CompanionTerminalCommandsCheckBox.IsChecked = p.CompanionTerminalCommands;
        CompanionTranscriptCheckBox.IsChecked = p.CompanionActivityTranscriptStorage;
        CompanionSensitiveMemoryCheckBox.IsChecked = p.CompanionSensitiveMemory;
        CompanionFinancialActionsCheckBox.IsChecked = p.CompanionFinancialActions;
    }

    private void CompanionSavePermissionsButton_Click(object sender, RoutedEventArgs e)
    {
        ChatClientPermissionSnapshot p = _proxy.GetChatClientPermissionsDiagnostics("global");
        p.CompanionEnabled = CompanionEnabledCheckBox.IsChecked == true;
        p.CompanionScreenView = CompanionScreenViewCheckBox.IsChecked == true;
        p.CompanionCursorControl = CompanionCursorControlCheckBox.IsChecked == true;
        p.CompanionApplicationLaunch = CompanionApplicationLaunchCheckBox.IsChecked == true;
        p.CompanionApplicationControl = CompanionApplicationControlCheckBox.IsChecked == true;
        p.CompanionTerminalCommands = CompanionTerminalCommandsCheckBox.IsChecked == true;
        p.CompanionActivityTranscriptStorage = CompanionTranscriptCheckBox.IsChecked == true;
        p.CompanionSensitiveMemory = CompanionSensitiveMemoryCheckBox.IsChecked == true;
        p.CompanionFinancialActions = CompanionFinancialActionsCheckBox.IsChecked == true;
        _proxy.SaveChatClientPermissionsDiagnostics(p);
        CompanionStatusText.Text = "Companion permissions saved. All grants remain independent and default-off.";
    }

    private void EmergencyStopIntegratedCompanion(string source)
    {
        _companionCancellation?.Cancel();
        try { StopPcAccessRtmpPublisher(); } catch { }
        CompanionPreviewImage.Source = null;
        CompanionStatusText.Text = "EMERGENCY STOP completed from " + source + ". Companion input and tasks are stopped.";
    }

    private static string PromptForCompanionPhrase(string title, string warning, string expectedPhrase)
    {
        var input = new TextBox { Margin = new Thickness(0, 12, 0, 12) };
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(25, 5, 5)),
            Foreground = Brushes.Red,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = warning, Foreground = Brushes.Red, FontWeight = FontWeights.Bold, FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Type exactly:\n" + expectedPhrase, Foreground = Brushes.Red, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap },
                    input
                }
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "CONFIRM HIGH-RISK ACTION", Padding = new Thickness(12, 5, 12, 5), Background = Brushes.DarkRed, Foreground = Brushes.White };
        cancel.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        confirm.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        ((StackPanel)dialog.Content).Children.Add(buttons);
        return dialog.ShowDialog() == true ? input.Text : "";
    }

    private static void CleanupLegacyCompanionInstallation()
    {
        string socketJackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack");
        string marker = Path.Combine(socketJackRoot, "integrated-companion-cleanup-v1.marker");
        if (File.Exists(marker)) return;
        Directory.CreateDirectory(socketJackRoot);
        foreach (Process process in Process.GetProcessesByName("JackLLMCompanion"))
        {
            try { process.Kill(true); process.WaitForExit(3000); } catch { }
            finally { process.Dispose(); }
        }
        string legacyData = Path.GetFullPath(Path.Combine(socketJackRoot, "Companion"));
        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        string installedCompanion = Path.GetFullPath(Path.Combine(baseDirectory, "Companion"));
        if (legacyData.StartsWith(socketJackRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(legacyData))
            Directory.Delete(legacyData, recursive: true);
        if (installedCompanion.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(installedCompanion))
            Directory.Delete(installedCompanion, recursive: true);
        string looseExecutable = Path.Combine(baseDirectory, "JackLLMCompanion.exe");
        if (File.Exists(looseExecutable)) File.Delete(looseExecutable);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
}
