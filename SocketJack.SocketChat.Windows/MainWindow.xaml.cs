using NAudio.Wave;
using SocketJack.SocketChat;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.IO.Compression;

namespace SocketJack.SocketChat.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly ObservableCollection<MessageViewModel> _visibleMessages = new();
    private readonly List<SocketChatMessage> _messages = new();
    private SocketChatManagedDatabase _database = null!;
    private SocketChatDeviceIdentity _identity = null!;
    private byte[] _localKey = null!;
    private string _storageRoot = "";
    private ProfileStore _profileStore = null!;
    private SocketChatUserProfile _profile = null!;
    private ChannelViewModel? _selectedChannel;
    private WaveInEvent? _microphone;
    private bool _muted;
    private readonly List<SocketChatAttachment> _pendingAttachments = new();
    private StreamWindow? _streamWindow;
    private DiscoverWindow? _discoverWindow;
    private SocketChatCoordinatorServer? _coordinatorServer;
    private string? _editingMessageId;
    private readonly HashSet<string> _editedMessageIds = new(StringComparer.Ordinal);

    public MainWindow()
    {
        InitializeComponent();
        InitializeStorage();
        ChannelList.ItemsSource = _channels;
        MessageList.ItemsSource = _visibleMessages;
        _channels.Add(new ChannelViewModel("general", "general", "#", ChannelKind.Text));
        _channels.Add(new ChannelViewModel("files", "files", "#", ChannelKind.Text));
        _channels.Add(new ChannelViewModel("lobby-voice", "Lobby voice", "◖", ChannelKind.Voice));
        _channels.Add(new ChannelViewModel("pair-device", "Pair a device", "+", ChannelKind.Pair));
        LoadMessages();
        _profileStore = new ProfileStore(_storageRoot, _identity.Fingerprint);
        _profile = _profileStore.Load();
        _profileStore.Save(_profile);
        if (_profile.HostCoordinator) { _coordinatorServer = new SocketChatCoordinatorServer(4280, Path.Combine(_storageRoot, "Coordinator", "directory.json")); _coordinatorServer.Start(); }
        RenderProfile();
        FingerprintText.Text = FormatFingerprint(_identity.Fingerprint);
        ChannelList.SelectedIndex = 0;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        Closed += (_, _) => DisposeResources();
    }

    private void InitializeStorage()
    {
        _storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "SocketChat");
        Directory.CreateDirectory(_storageRoot);
        _identity = SocketChatDeviceIdentity.LoadOrCreate(Path.Combine(_storageRoot, ".device-key"));
        string keyPath = Path.Combine(_storageRoot, ".local-store-key");
        if (File.Exists(keyPath)) _localKey = Convert.FromBase64String(File.ReadAllText(keyPath));
        else { _localKey = RandomNumberGenerator.GetBytes(32); File.WriteAllText(keyPath, Convert.ToBase64String(_localKey)); File.SetAttributes(keyPath, File.GetAttributes(keyPath) | FileAttributes.Hidden); }
        _database = new SocketChatManagedDatabase(Path.Combine(_storageRoot, "Database"));
    }

    private void LoadMessages()
    {
        foreach (SocketChatEnvelope envelope in _database.Read("local-lobby", 1000))
        {
            try
            {
                byte[] payload = SocketChatCrypto.Decrypt(_localKey, envelope.CipherText);
                if (envelope.Kind == SocketChatRecordKind.Message)
                {
                    var message = JsonSerializer.Deserialize<SocketChatMessage>(payload);
                    if (message != null && _messages.All(m => m.Id != message.Id)) _messages.Add(message);
                }
                else if (envelope.Kind == SocketChatRecordKind.Edit)
                {
                    var edit = JsonSerializer.Deserialize<SocketChatMessageMutation>(payload);
                    int index = edit == null ? -1 : _messages.FindIndex(m => m.Id == edit.MessageId);
                    if (index >= 0 && edit!.Message != null) { _messages[index] = edit.Message; _editedMessageIds.Add(edit.MessageId); }
                }
                else if (envelope.Kind == SocketChatRecordKind.Delete)
                {
                    ApplyDelete(JsonSerializer.Deserialize<SocketChatDeleteMutation>(payload), false);
                }
            }
            catch { }
        }
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelList.SelectedItem is not ChannelViewModel channel) return;
        _selectedChannel = channel;
        ChannelTitle.Text = channel.Kind == ChannelKind.Text ? "# " + channel.Name : channel.Glyph + " " + channel.Name;
        ChannelDescription.Text = channel.Kind switch { ChannelKind.Voice => "Direct voice with host-relay fallback", ChannelKind.Pair => "Secure one-time device pairing", _ => "Encrypted managed SocketJack chat" };
        VoicePanel.Visibility = channel.Kind == ChannelKind.Voice ? Visibility.Visible : Visibility.Collapsed;
        ComposerPanel.Visibility = channel.Kind == ChannelKind.Text ? Visibility.Visible : Visibility.Collapsed;
        PairingButton.Visibility = channel.Kind == ChannelKind.Pair ? Visibility.Visible : Visibility.Collapsed;
        PairingCodeText.Text = "";
        RefreshMessages();
    }

    private void RefreshMessages()
    {
        _visibleMessages.Clear();
        if (_selectedChannel == null) return;
        if (_selectedChannel.Kind == ChannelKind.Text)
        {
            foreach (var message in _messages.Where(m => string.Equals(m.ChannelId, _selectedChannel.Id, StringComparison.Ordinal)).OrderBy(m => m.Sequence))
                _visibleMessages.Add(new MessageViewModel(message, string.Equals(message.SenderFingerprint, _identity.Fingerprint, StringComparison.Ordinal) ? _profile.Username : null, string.Equals(message.SenderFingerprint, _identity.Fingerprint, StringComparison.Ordinal), _editedMessageIds.Contains(message.Id)));
        }
        EmptyPanel.Visibility = _visibleMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _selectedChannel.Kind switch { ChannelKind.Pair => "Pair a device", ChannelKind.Voice => "Lobby voice", _ => "Welcome to #" + _selectedChannel.Name };
        EmptyText.Text = _selectedChannel.Kind switch { ChannelKind.Pair => "Create a one-time six-character code to securely pair another SocketChat device.", ChannelKind.Voice => "Join the voice channel to activate your microphone and connect to peers.", _ => "Messages in this channel are encrypted in the managed SocketJack database." };
        MessageBox.ToolTip = _selectedChannel.Kind == ChannelKind.Text ? "Message #" + _selectedChannel.Name : null;
        if (_visibleMessages.Count > 0) MessageList.ScrollIntoView(_visibleMessages[^1]);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();
    private void MessageBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; SendMessage(); } }

    private void SendMessage()
    {
        if (_selectedChannel?.Kind != ChannelKind.Text || (string.IsNullOrWhiteSpace(MessageBox.Text) && _pendingAttachments.Count == 0)) return;
        if (_editingMessageId != null)
        {
            SocketChatMessage? original = _messages.FirstOrDefault(m => m.Id == _editingMessageId);
            if (original == null) { EndEditing(); return; }
            original.Text = MessageBox.Text.Trim();
            AppendMutation(SocketChatRecordKind.Edit, new SocketChatMessageMutation { MessageId = original.Id, Message = original });
            _editedMessageIds.Add(original.Id);
            EndEditing();
            RefreshMessages();
            return;
        }
        long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = new SocketChatMessage { LobbyId = "local-lobby", ChannelId = _selectedChannel.Id, SenderFingerprint = _identity.Fingerprint, Text = MessageBox.Text.Trim(), Sequence = sequence, Attachments = _pendingAttachments.ToList() };
        string cipher = SocketChatCrypto.Encrypt(_localKey, JsonSerializer.SerializeToUtf8Bytes(message));
        var envelope = new SocketChatEnvelope { Kind = SocketChatRecordKind.Message, LobbyId = message.LobbyId, Epoch = 1, SenderFingerprint = _identity.Fingerprint, Sequence = sequence, CipherText = cipher };
        envelope.Signature = _identity.Sign(string.Join("|", envelope.Version, envelope.Kind, envelope.LobbyId, envelope.Epoch, envelope.SenderFingerprint, envelope.Sequence, envelope.CipherText));
        _database.Append(envelope);
        _messages.Add(message);
        _pendingAttachments.Clear();
        AttachButton.Content = "Attach";
        MessageBox.Clear();
        RefreshMessages();
    }

    private void EditMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageViewModel vm || !vm.CanManage) return;
        _editingMessageId = vm.Message.Id;
        MessageBox.Text = vm.Message.Text;
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
        AttachButton.IsEnabled = false;
        SendButton.Content = "Save";
        CancelEditButton.Visibility = Visibility.Visible;
    }

    private void CancelEditButton_Click(object sender, RoutedEventArgs e) => EndEditing();

    private void EndEditing()
    {
        _editingMessageId = null;
        MessageBox.Clear();
        AttachButton.IsEnabled = true;
        SendButton.Content = "Send";
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageViewModel vm || !vm.CanManage) return;
        if (System.Windows.MessageBox.Show(this, "Delete this message and its attached files?", "SocketChat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deletion = new SocketChatDeleteMutation { MessageId = vm.Message.Id, DeleteMessage = true, AttachmentIds = vm.Message.Attachments.Select(a => a.Id).ToList() };
        AppendMutation(SocketChatRecordKind.Delete, deletion);
        ApplyDelete(deletion, true);
        RefreshMessages();
    }

    private void DeleteAttachments_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageViewModel vm || !vm.CanManage || vm.Attachments.Count == 0) return;
        if (System.Windows.MessageBox.Show(this, "Delete the attached file" + (vm.Attachments.Count == 1 ? "" : "s") + " from this message?", "SocketChat", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var deletion = new SocketChatDeleteMutation { MessageId = vm.Message.Id, AttachmentIds = vm.Attachments.Select(a => a.Id).ToList() };
        AppendMutation(SocketChatRecordKind.Delete, deletion);
        ApplyDelete(deletion, true);
        RefreshMessages();
    }

    private void AppendMutation(SocketChatRecordKind kind, object payload)
    {
        long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var envelope = new SocketChatEnvelope { Kind = kind, LobbyId = "local-lobby", Epoch = 1, SenderFingerprint = _identity.Fingerprint, Sequence = sequence, CipherText = SocketChatCrypto.Encrypt(_localKey, JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType())) };
        envelope.Signature = _identity.Sign(string.Join("|", envelope.Version, envelope.Kind, envelope.LobbyId, envelope.Epoch, envelope.SenderFingerprint, envelope.Sequence, envelope.CipherText));
        _database.Append(envelope);
    }

    private void ApplyDelete(SocketChatDeleteMutation? deletion, bool deleteCachedFiles)
    {
        if (deletion == null) return;
        SocketChatMessage? message = _messages.FirstOrDefault(m => m.Id == deletion.MessageId);
        if (message == null) return;
        var ids = deletion.AttachmentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var attachment in message.Attachments.Where(a => ids.Contains(a.Id)).ToList())
        {
            if (deleteCachedFiles && !string.IsNullOrWhiteSpace(attachment.DropboxPath)) { try { File.Delete(attachment.DropboxPath); } catch { } }
            message.Attachments.Remove(attachment);
        }
        if (deletion.DeleteMessage) { _messages.Remove(message); _editedMessageIds.Remove(message.Id); }
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Attach files to SocketChat", Multiselect = true, Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        string directory = Path.Combine(_storageRoot, "Attachments");
        Directory.CreateDirectory(directory);
        foreach (string path in dialog.FileNames)
        {
            var info = new FileInfo(path);
            if (info.Length > 10 * 1024 * 1024) { System.Windows.MessageBox.Show(this, info.Name + " exceeds the 10 MB attachment limit.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Warning); continue; }
            byte[] bytes = File.ReadAllBytes(path);
            string id = Guid.NewGuid().ToString("N");
            string encryptedPath = Path.Combine(directory, id + ".sjattachment");
            File.WriteAllText(encryptedPath, SocketChatCrypto.Encrypt(_localKey, bytes));
            _pendingAttachments.Add(new SocketChatAttachment { Id = id, FileName = info.Name, ContentType = GetContentType(info.Extension), Length = info.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), DropboxPath = encryptedPath });
        }
        AttachButton.Content = _pendingAttachments.Count == 0 ? "Attach" : "Attach (" + _pendingAttachments.Count + ")";
    }

    private void StreamButton_Click(object sender, RoutedEventArgs e)
    {
        if (_streamWindow != null) { _streamWindow.Activate(); return; }
        _streamWindow = new StreamWindow(_profile) { Owner = this };
        _streamWindow.Closed += (_, _) => _streamWindow = null;
        _streamWindow.Show();
    }

    private void DownloadAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MessageViewModel message || message.Attachments.Count == 0) return;
        try
        {
            if (message.Attachments.Count == 1)
            {
                SocketChatAttachment attachment = message.Attachments[0];
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save SocketChat attachment", FileName = attachment.FileName, Filter = "All files|*.*" };
                if (dialog.ShowDialog(this) != true) return;
                File.WriteAllBytes(dialog.FileName, ReadAttachment(attachment));
            }
            else
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save SocketChat attachments as ZIP", FileName = "SocketChat-files.zip", DefaultExt = ".zip", Filter = "ZIP archive|*.zip" };
                if (dialog.ShowDialog(this) != true) return;
                using var stream = File.Create(dialog.FileName);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SocketChatAttachment attachment in message.Attachments)
                {
                    string name = SafeZipEntryName(attachment.FileName, usedNames);
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using Stream destination = entry.Open();
                    byte[] bytes = ReadAttachment(attachment);
                    destination.Write(bytes, 0, bytes.Length);
                }
            }
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, "The attachment could not be downloaded: " + ex.Message, "SocketChat", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private byte[] ReadAttachment(SocketChatAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.DropboxPath) || !File.Exists(attachment.DropboxPath)) throw new FileNotFoundException("The encrypted attachment is not cached on this device.", attachment.FileName);
        byte[] bytes = SocketChatCrypto.Decrypt(_localKey, File.ReadAllText(attachment.DropboxPath));
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.IsNullOrWhiteSpace(attachment.Sha256) && !string.Equals(hash, attachment.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Attachment integrity verification failed.");
        return bytes;
    }

    private static string SafeZipEntryName(string fileName, HashSet<string> used)
    {
        string clean = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(clean)) clean = "attachment";
        string candidate = clean; int suffix = 2;
        while (!used.Add(candidate)) candidate = Path.GetFileNameWithoutExtension(clean) + " (" + suffix++ + ")" + Path.GetExtension(clean);
        return candidate;
    }

    private void PairingButton_Click(object sender, RoutedEventArgs e) => PairingCodeText.Text = SocketChatDeviceIdentity.CreatePairingCode();

    private void JoinVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int device = _profile.MicrophoneDeviceNumber >= 0 ? _profile.MicrophoneDeviceNumber : 0;
            _microphone ??= new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 20 };
            _microphone.DataAvailable += Microphone_DataAvailable;
            _microphone.StartRecording();
            VoiceStatusText.Text = "Microphone ready · waiting for peers";
            JoinVoiceButton.Content = "Joined";
            JoinVoiceButton.IsEnabled = false;
        }
        catch (Exception ex) { VoiceStatusText.Text = "Microphone unavailable: " + ex.Message; }
    }

    private bool _pushToTalkHeld;
    private void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
    {
        double sum = 0; int count = e.BytesRecorded / 2;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2) { double sample = BitConverter.ToInt16(e.Buffer, i) / 32768d; sum += sample * sample; }
        double db = count == 0 ? -70 : 20 * Math.Log10(Math.Sqrt(sum / count) + 1e-9);
        bool gateOpen = !_profile.VoiceActivationEnabled || db >= _profile.VoiceActivationThresholdDb;
        bool canSend = !_muted && gateOpen && (!_profile.PushToTalkEnabled || _pushToTalkHeld);
        Dispatcher.BeginInvoke(() => VoiceStatusText.Text = _muted ? "Microphone muted" : canSend ? "Transmitting · " + db.ToString("0") + " dB" : "Listening · below voice gate");
        if (!canSend) return;
        // Audio level and enabled filters are applied by the peer encoder before transmission.
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (KeyMatches(e.Key, _profile.ToggleMuteKey) && !e.IsRepeat) { _muted = !_muted; MuteButton.Content = _muted ? "Unmute" : "Mute"; }
        if (KeyMatches(e.Key, _profile.PushToTalkKey)) _pushToTalkHeld = true;
    }
    private void MainWindow_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e) { if (KeyMatches(e.Key, _profile.PushToTalkKey)) _pushToTalkHeld = false; }
    private static bool KeyMatches(Key key, string configured) => string.Equals(key.ToString(), configured, StringComparison.OrdinalIgnoreCase);

    private void LeaveVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _microphone?.StopRecording();
        VoiceStatusText.Text = "Microphone disconnected";
        JoinVoiceButton.Content = "Join voice";
        JoinVoiceButton.IsEnabled = true;
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        MuteButton.Content = _muted ? "Unmute" : "Mute";
        VoiceStatusText.Text = _muted ? "Microphone muted" : (_microphone == null ? "Microphone disconnected" : "Microphone ready · waiting for peers");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_profile) { Owner = this };
        if (window.ShowDialog() == true && window.Saved)
        {
            _profileStore.Save(_profile);
            RenderProfile();
            RefreshMessages();
        }
    }

    private void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_discoverWindow != null) { _discoverWindow.Activate(); return; }
        _discoverWindow = new DiscoverWindow(_profile, _profileStore, _identity) { Owner = this };
        _discoverWindow.ServerJoined += server => { LobbyNameText.Text = server.Name; LobbyStatusText.Text = server.Transport + " · host " + server.HostFingerprint[..Math.Min(8, server.HostFingerprint.Length)]; };
        _discoverWindow.Closed += (_, _) => _discoverWindow = null;
        _discoverWindow.Show();
    }

    private void RenderProfile()
    {
        UsernameText.Text = _profile.Username;
        MemberUsernameText.Text = _profile.Username;
        string initials = string.Concat(_profile.Username.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
        if (string.IsNullOrWhiteSpace(initials)) initials = "SC";
        UserInitialsText.Text = initials;
        MemberInitialsText.Text = initials;
        BitmapImage? image = null;
        try { if (File.Exists(_profile.AvatarPath)) image = new BitmapImage(new Uri(_profile.AvatarPath, UriKind.Absolute)); } catch { }
        UserAvatarBrush.ImageSource = image;
        MemberAvatarBrush.ImageSource = image;
        UserInitialsText.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        MemberInitialsText.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DropboxButton_Click(object sender, RoutedEventArgs e) => System.Windows.MessageBox.Show("Coordinator: " + _profile.CoordinatorUrl + "\n\nPayload route: Tailscale direct P2P, then your peer relay, then encrypted DERP fallback.", "SocketChat Tailscale", MessageBoxButton.OK, MessageBoxImage.Information);
    private void DisposeResources() { try { _microphone?.StopRecording(); _microphone?.Dispose(); } catch { } _coordinatorServer?.Dispose(); _identity.Dispose(); }
    private static string FormatFingerprint(string value) => string.Join("-", Enumerable.Range(0, value.Length / 4).Select(i => value.Substring(i * 4, 4)));
    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".pdf" => "application/pdf", ".txt" => "text/plain", _ => "application/octet-stream" };
}

public enum ChannelKind { Text, Voice, Pair }
public sealed record ChannelViewModel(string Id, string Name, string Glyph, ChannelKind Kind);
public sealed class MessageViewModel
{
    public SocketChatMessage Message { get; }
    public bool CanManage { get; }
    public string Sender { get; }
    public string Time { get; }
    public string Text { get; }
    public IReadOnlyList<SocketChatAttachment> Attachments { get; }
    public Visibility AttachmentVisibility => Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string AttachmentTitle => Attachments.Count == 1 ? Attachments[0].FileName : Attachments.Count + " files";
    public string AttachmentDetail => Attachments.Count == 1 ? FormatSize(Attachments[0].Length) : string.Join(", ", Attachments.Select(a => a.FileName));
    public string DownloadLabel => Attachments.Count > 1 ? "Download ZIP" : "Download";
    public Visibility ManageVisibility => CanManage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditedVisibility { get; }
    public MessageViewModel(SocketChatMessage message, string? displayName = null, bool canManage = false, bool edited = false) { Message = message; CanManage = canManage; Sender = !string.IsNullOrWhiteSpace(displayName) ? displayName : string.IsNullOrWhiteSpace(message.SenderFingerprint) ? "LOCAL" : message.SenderFingerprint[..Math.Min(8, message.SenderFingerprint.Length)]; Time = message.SentUtc.LocalDateTime.ToString("h:mm tt"); Text = message.Text; Attachments = message.Attachments ?? new List<SocketChatAttachment>(); EditedVisibility = edited ? Visibility.Visible : Visibility.Collapsed; }
    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.0") + " MB" : Math.Max(1, bytes / 1024) + " KB";
}
