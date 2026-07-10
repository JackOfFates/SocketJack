using Microsoft.Win32;
using NAudio.Wave;
using SocketJack.SocketChat;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SocketJack.SocketChat.Windows;

public partial class SettingsWindow : Window
{
    private readonly SocketChatUserProfile _profile;
    private string _avatarPath;
    private WaveInEvent? _testMicrophone;
    public bool Saved { get; private set; }

    public SettingsWindow(SocketChatUserProfile profile)
    {
        InitializeComponent(); _profile = profile; _avatarPath = profile.AvatarPath;
        UsernameBox.Text = profile.Username; FingerprintBox.Text = Format(profile.DeviceFingerprint); BanKeyBox.Text = Format(profile.BanIdentityKey[..Math.Min(32, profile.BanIdentityKey.Length)]); CoordinatorUrlBox.Text = profile.CoordinatorUrl; HostCoordinatorBox.IsChecked = profile.HostCoordinator;
        NetworkEvidenceText.Text = "Network evidence: hashed adapter identity plus " + profile.ObservedIpAddresses.Count + " observed local address(es). Raw MAC addresses are never shared.";
        for (int i = 0; i < WaveIn.DeviceCount; i++) MicrophoneBox.Items.Add(new MicrophoneChoice(i, WaveIn.GetCapabilities(i).ProductName));
        if (MicrophoneBox.Items.Count > 0) MicrophoneBox.SelectedValue = profile.MicrophoneDeviceNumber < 0 ? 0 : profile.MicrophoneDeviceNumber;
        MicrophoneLevelSlider.Value = profile.MicrophoneLevel; VoiceActivationBox.IsChecked = profile.VoiceActivationEnabled; SensitivitySlider.Value = profile.VoiceActivationThresholdDb;
        NoiseSuppressionBox.IsChecked = profile.NoiseSuppressionEnabled; EchoCancellationBox.IsChecked = profile.EchoCancellationEnabled; AutomaticGainBox.IsChecked = profile.AutomaticGainEnabled; HighPassBox.IsChecked = profile.HighPassFilterEnabled;
        string[] keys = { "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftShift", "RightShift", "Space", "CapsLock", "M", "V", "F8", "F9" }; PushToTalkKeyBox.ItemsSource = keys; MuteKeyBox.ItemsSource = keys; PushToTalkKeyBox.SelectedItem = profile.PushToTalkKey; MuteKeyBox.SelectedItem = profile.ToggleMuteKey; PushToTalkBox.IsChecked = profile.PushToTalkEnabled;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens) MonitorBox.Items.Add(screen.DeviceName + " (" + screen.Bounds.Width + "x" + screen.Bounds.Height + ")"); MonitorBox.SelectedIndex = Math.Clamp(profile.StreamMonitorIndex, 0, Math.Max(0, MonitorBox.Items.Count - 1));
        StreamSourceBox.SelectedIndex = profile.StreamSource.StartsWith("Camera", StringComparison.OrdinalIgnoreCase) ? 1 : profile.StreamSource.StartsWith("Window", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        PreviewFpsBox.Text = profile.StreamPreviewFps.ToString(); BitrateBox.Text = profile.StreamBitrateKbps.ToString(); StreamPortBox.Text = profile.StreamPort.ToString(); ObsPathBox.Text = profile.ObsPath; VlcPathBox.Text = profile.VlcPath;
        RenderAvatar(); Closed += (_, _) => StopMicTest();
    }

    private void MicTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testMicrophone != null) { StopMicTest(); return; }
        if (MicrophoneBox.SelectedValue is not int device) return;
        try { _testMicrophone = new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 30 }; _testMicrophone.DataAvailable += TestMicrophone_DataAvailable; _testMicrophone.StartRecording(); MicTestButton.Content = "Stop test"; MicTestStatus.Text = "Listening…"; }
        catch (Exception ex) { StopMicTest(); MicTestStatus.Text = "Microphone unavailable: " + ex.Message; }
    }
    private void TestMicrophone_DataAvailable(object? sender, WaveInEventArgs e) { double sum = 0; int count = e.BytesRecorded / 2; for (int i = 0; i + 1 < e.BytesRecorded; i += 2) { double s = BitConverter.ToInt16(e.Buffer, i) / 32768d; sum += s * s; } double db = count == 0 ? -70 : Math.Max(-70, 20 * Math.Log10(Math.Sqrt(sum / count) + 1e-9)); Dispatcher.BeginInvoke(() => { MicMeter.Value = db; MicTestStatus.Text = db >= SensitivitySlider.Value ? "Voice detected (" + db.ToString("0") + " dB)" : "Below gate (" + db.ToString("0") + " dB)"; }); }
    private void StopMicTest() { try { _testMicrophone?.StopRecording(); _testMicrophone?.Dispose(); } catch { } _testMicrophone = null; if (MicTestButton != null) MicTestButton.Content = "Test microphone"; }
    private void MicrophoneLevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (MicrophoneLevelText != null) MicrophoneLevelText.Text = (e.NewValue * 100).ToString("0") + "%"; }
    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (SensitivityText != null) SensitivityText.Text = e.NewValue.ToString("0") + " dB"; }
    private void ChooseImage_Click(object sender, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Title = "Choose a SocketChat profile picture", Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" }; if (d.ShowDialog(this) == true) { _avatarPath = d.FileName; RenderAvatar(); } }
    private void RemoveImage_Click(object sender, RoutedEventArgs e) { _avatarPath = ""; RenderAvatar(); }
    private void RenderAvatar() { try { AvatarPreviewBrush.ImageSource = File.Exists(_avatarPath) ? new BitmapImage(new Uri(_avatarPath, UriKind.Absolute)) : null; } catch { AvatarPreviewBrush.ImageSource = null; } }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (UsernameBox.Text.Trim().Length < 2) { System.Windows.MessageBox.Show(this, "Username must be at least two characters.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(StreamPortBox.Text, out int port) || port is < 1 or > 65535 || !int.TryParse(PreviewFpsBox.Text, out int fps) || fps is < 1 or > 60 || !int.TryParse(BitrateBox.Text, out int bitrate) || bitrate is < 250 or > 100000) { System.Windows.MessageBox.Show(this, "Enter a valid RTMP port, 1–60 FPS, and bitrate from 250–100000 Kbps.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!Uri.TryCreate(CoordinatorUrlBox.Text.Trim(), UriKind.Absolute, out _)) { System.Windows.MessageBox.Show(this, "Enter a valid Tailscale coordinator URL.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _profile.Username = UsernameBox.Text.Trim(); _profile.AvatarPath = _avatarPath; _profile.CoordinatorUrl = CoordinatorUrlBox.Text.Trim().TrimEnd('/'); _profile.HostCoordinator = HostCoordinatorBox.IsChecked == true; _profile.MicrophoneDeviceNumber = MicrophoneBox.SelectedValue is int d ? d : -1; _profile.MicrophoneLevel = MicrophoneLevelSlider.Value; _profile.VoiceActivationEnabled = VoiceActivationBox.IsChecked == true; _profile.VoiceActivationThresholdDb = SensitivitySlider.Value;
        _profile.NoiseSuppressionEnabled = NoiseSuppressionBox.IsChecked == true; _profile.EchoCancellationEnabled = EchoCancellationBox.IsChecked == true; _profile.AutomaticGainEnabled = AutomaticGainBox.IsChecked == true; _profile.HighPassFilterEnabled = HighPassBox.IsChecked == true; _profile.PushToTalkEnabled = PushToTalkBox.IsChecked == true; _profile.PushToTalkKey = PushToTalkKeyBox.SelectedItem?.ToString() ?? "LeftCtrl"; _profile.ToggleMuteKey = MuteKeyBox.SelectedItem?.ToString() ?? "M";
        _profile.StreamPort = port; _profile.StreamPreviewFps = fps; _profile.StreamBitrateKbps = bitrate; _profile.StreamMonitorIndex = Math.Max(0, MonitorBox.SelectedIndex); _profile.StreamSource = ((StreamSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString()) ?? "Display"; _profile.ObsPath = ObsPathBox.Text.Trim(); _profile.VlcPath = VlcPathBox.Text.Trim(); Saved = true; DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string Format(string value) => string.Join("-", Enumerable.Range(0, value.Length / 4).Select(i => value.Substring(i * 4, 4)));
    private sealed record MicrophoneChoice(int DeviceNumber, string Name);
}
