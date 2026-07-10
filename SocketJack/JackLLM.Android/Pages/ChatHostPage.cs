using System.Collections.ObjectModel;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Pages;

public sealed class ChatHostPage : ContentPage
{
    private readonly ServerInfo _server;
    private readonly JackLlmClient _client;
    private readonly ServerStore _store;
    private readonly MobileAudioService _audio = new();
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly List<AttachmentInfo> _attachments = new();
    private readonly CollectionView _messageList;
    private readonly Picker _models;
    private readonly Picker _services;
    private readonly Button _generalMode;
    private readonly Button _advancedMode;
    private readonly Editor _prompt;
    private readonly Label _status;
    private readonly Button _send;
    private IReadOnlyList<ModelInfo> _allModels = Array.Empty<ModelInfo>();
    private MobileChatMode _mode = MobileChatMode.General;
    private CancellationTokenSource? _streamCancellation;
    private string _sessionId = Guid.NewGuid().ToString("N");

    public ChatHostPage(ServerInfo server, JackLlmClient client, ServerStore store)
    {
        _server = server; _client = client; _store = store;
        Title = server.DisplayName;
        BackgroundColor = Color.FromArgb("#0B1020");
        _status = new Label { Text = "Connecting…", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11, Margin = new Thickness(4, 0) };
        _models = new Picker { Title = "Model", TextColor = Colors.White, TitleColor = Color.FromArgb("#94A3B8"), HorizontalOptions = LayoutOptions.Fill };
        _services = new Picker { Title = "Service", ItemsSource = new[] { "chat", "agent", "image", "video" }, SelectedIndex = 0, TextColor = Colors.White, WidthRequest = 112 };
        _generalMode = new Button { Text = "General Mode", CornerRadius = 14, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, FontSize = 12, Padding = new Thickness(10, 6) };
        _advancedMode = new Button { Text = "Advanced Mode", CornerRadius = 14, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, FontSize = 12, Padding = new Thickness(10, 6) };
        _generalMode.Clicked += (_, _) => SetMode(MobileChatMode.General);
        _advancedMode.Clicked += (_, _) => SetMode(MobileChatMode.Advanced);
        _messageList = new CollectionView { ItemsSource = _messages, ItemTemplate = new DataTemplate(MessageTemplate), ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepLastItemInView };
        _prompt = new Editor { Placeholder = "Message JackLLM…", AutoSize = EditorAutoSizeOption.TextChanges, MaximumHeightRequest = 130, TextColor = Colors.White, PlaceholderColor = Color.FromArgb("#64748B"), BackgroundColor = Colors.Transparent };
        _send = new Button { Text = "Send", CornerRadius = 12, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, WidthRequest = 72 };
        _send.Clicked += async (_, _) => { if (_streamCancellation is null) await SendAsync(); else Stop(); };
        var attach = new Button { Text = "+", FontSize = 24, CornerRadius = 12, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 48 };
        attach.Clicked += async (_, _) => await AddAttachmentAsync();
        var voice = new Button { Text = "Mic", CornerRadius = 12, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 58 };
        voice.Clicked += async (_, _) => await RecordVoiceAsync();

        ToolbarItems.Add(new ToolbarItem("Sessions", null, async () => await ChooseSessionAsync()));
        ToolbarItems.Add(new ToolbarItem("New", null, NewSession));
        ToolbarItems.Add(new ToolbarItem("Speak", null, async () => await SpeakLastAsync()));

        var modeRow = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(10, 8, 10, 4), BackgroundColor = Color.FromArgb("#111827"), Children = { _generalMode, _advancedMode } };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, BackgroundColor = Color.FromArgb("#111827"), Padding = new Thickness(10, 2) };
        header.Add(_models, 0); header.Add(_services, 1);
        var composer = new Border { Margin = new Thickness(10, 6, 10, 10), Padding = new Thickness(8), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 18 }, Content = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Children = { attach, voice.Column(1), _prompt.Column(2), _send.Column(3) } } };
        Content = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }, Children = { modeRow.Row(0), header.Row(1), _status.Row(2), _messageList.Row(3), composer.Row(4) } };
        RestoreModePreference();
        SetMode(_mode);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _client.ConnectAsync(_server);
            var models = await _client.GetModelsAsync();
            _allModels = models.ToList();
            _models.ItemDisplayBinding = new Binding(nameof(ModelInfo.Name));
            ApplyModelFilter();
            _status.Text = "Connected securely to " + new Uri(_server.Endpoint).Host;
            _server.IsSaved = true; _store.Save(_server);
        }
        catch (Exception ex) { _status.Text = "Connection failed: " + ex.Message; }
    }

    protected override void OnDisappearing() { base.OnDisappearing(); _streamCancellation?.Cancel(); }

    private void RestoreModePreference()
    {
        string value = Preferences.Default.Get(ModePreferenceKey, nameof(MobileChatMode.General));
        _mode = value.Equals(nameof(MobileChatMode.Advanced), StringComparison.OrdinalIgnoreCase) ? MobileChatMode.Advanced : MobileChatMode.General;
    }

    private void SetMode(MobileChatMode mode)
    {
        _mode = mode;
        Preferences.Default.Set(ModePreferenceKey, mode.ToString());
        bool advanced = mode == MobileChatMode.Advanced;
        _generalMode.BackgroundColor = advanced ? Color.FromArgb("#1F2937") : Color.FromArgb("#2563EB");
        _advancedMode.BackgroundColor = advanced ? Color.FromArgb("#7C3AED") : Color.FromArgb("#1F2937");
        _services.IsVisible = advanced;
        _services.SelectedIndex = advanced ? Math.Max(_services.SelectedIndex, 1) : 0;
        _prompt.Placeholder = advanced ? "Ask JackLLM to work, use tools, or generate media..." : "Chat with JackLLM...";
        _status.Text = advanced ? "Advanced Mode (Work): service controls and tool activity enabled" : "General Mode (chat): streamlined conversation";
        ApplyModelFilter();
    }

    private void ApplyModelFilter()
    {
        var previous = _models.SelectedItem as ModelInfo;
        List<ModelInfo> models = (_mode == MobileChatMode.General ? _allModels.Where(item => item.IsGeneralChatCandidate) : _allModels).ToList();
        if (models.Count == 0 && _allModels.Count > 0) models = _allModels.ToList();
        _models.ItemsSource = models;
        int selected = previous is null ? -1 : models.FindIndex(item => item.Id.Equals(previous.Id, StringComparison.OrdinalIgnoreCase));
        if (selected < 0 && models.Count > 0)
            selected = Math.Max(0, models.FindIndex(item => item.IsGeneralChatCandidate));
        _models.SelectedIndex = selected >= 0 ? selected : -1;
    }

    private string ModePreferenceKey => "jackllm.mobile.mode." + _server.LaunchKey;

    private async Task SendAsync()
    {
        string text = _prompt.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
        if (_models.SelectedItem is not ModelInfo model) { await DisplayAlertAsync("No model", "This Workstation did not report an available chat model.", "OK"); return; }
        var user = new ChatMessage { Role = "user", Content = text + AttachmentCaption() };
        var assistant = new ChatMessage { Role = "assistant", Status = "Starting…" };
        _messages.Add(user); _messages.Add(assistant); _prompt.Text = "";
        var attachments = _attachments.ToArray(); _attachments.Clear();
        _streamCancellation = new CancellationTokenSource(); _send.Text = "Stop"; _status.Text = "Generating…";
        try
        {
            string service = _mode == MobileChatMode.General ? "chat" : _services.SelectedItem?.ToString() ?? "agent";
            await foreach (ChatStreamEvent item in _client.StreamChatAsync(model.Id, service, _sessionId, _messages.Where(m => m != assistant).ToArray(), attachments, _streamCancellation.Token))
            {
                switch (item.Type.ToLowerInvariant())
                {
                    case "content": case "delta": assistant.Content += item.Content; break;
                    case "reasoning": assistant.Reasoning += item.Reasoning + item.Content; break;
                    case "progress": assistant.Status = string.IsNullOrWhiteSpace(item.Status) ? "Working…" : item.Status; break;
                    case "toolcall": case "tool_call": assistant.Tools.Add(new ToolActivity { Name = string.IsNullOrWhiteSpace(item.ToolName) ? "Tool" : item.ToolName, Status = item.ToolStatus, Detail = item.Status }); break;
                    case "error":
                        assistant.Content = string.IsNullOrWhiteSpace(item.Content) ? item.Status : item.Content;
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(assistant.Content) ? "The Workstation returned an error." : assistant.Content);
                    case "done": assistant.Status = ""; break;
                }
                OnPropertyChanged(nameof(_messages));
                _messageList.ItemsSource = null; _messageList.ItemsSource = _messages;
                if (_messages.Count > 0) _messageList.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: false);
            }
            _status.Text = "Ready";
        }
        catch (OperationCanceledException) { assistant.Status = "Stopped"; _status.Text = "Generation stopped"; }
        catch (Exception ex) { assistant.Status = "Error: " + ex.Message; _status.Text = assistant.Status; }
        finally { _streamCancellation.Dispose(); _streamCancellation = null; _send.Text = "Send"; }
    }

    private void Stop() { _streamCancellation?.Cancel(); if (!string.IsNullOrWhiteSpace(_client.ActiveStreamId)) _ = _client.StopAsync(_client.ActiveStreamId); }
    private void NewSession() { Stop(); _sessionId = Guid.NewGuid().ToString("N"); _messages.Clear(); _status.Text = "New conversation"; }

    private async Task ChooseSessionAsync()
    {
        try
        {
            var sessions = await _client.GetSessionsAsync();
            if (sessions.Count == 0) { await DisplayAlertAsync("Sessions", "No saved sessions were found.", "OK"); return; }
            string? title = await DisplayActionSheetAsync("Sessions", "Cancel", null, sessions.Select(s => s.Title).ToArray());
            ChatSessionInfo? selected = sessions.FirstOrDefault(s => s.Title == title);
            if (selected is not null) await LoadSessionAsync(selected.Id);
        }
        catch (Exception ex) { await DisplayAlertAsync("Sessions", ex.Message, "OK"); }
    }

    private async Task LoadSessionAsync(string id)
    {
        ChatSessionDetail detail = await _client.GetSessionAsync(id);
        _sessionId = detail.Id;
        _messages.Clear();
        foreach (ChatMessage message in detail.Messages)
            _messages.Add(message);
        if (!string.IsNullOrWhiteSpace(detail.Model) && _models.ItemsSource is IEnumerable<ModelInfo> models)
        {
            List<ModelInfo> list = models.ToList();
            int index = list.FindIndex(model => model.Id.Equals(detail.Model, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _models.SelectedIndex = index;
        }
        _status.Text = $"Loaded {detail.Title} ({detail.Messages.Count} messages, {detail.Files.Count} files)";
        if (_messages.Count > 0) _messageList.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: false);
    }

    private async Task AddAttachmentAsync()
    {
        string? action = await DisplayActionSheetAsync("Attach", "Cancel", null, "Choose file", "Take photo", "Choose photo");
        try
        {
            FileResult? file = action switch { "Take photo" => await MediaPicker.Default.CapturePhotoAsync(), "Choose photo" => (await MediaPicker.Default.PickPhotosAsync()).FirstOrDefault(), "Choose file" => await FilePicker.Default.PickAsync(), _ => null };
            if (file is null) return;
            await using Stream stream = await file.OpenReadAsync(); using var memory = new MemoryStream(); await stream.CopyToAsync(memory);
            _attachments.Add(new AttachmentInfo { Name = file.FileName, ContentType = file.ContentType ?? "application/octet-stream", Data = memory.ToArray() });
            _status.Text = $"{_attachments.Count} attachment{(_attachments.Count == 1 ? "" : "s")} ready";
        }
        catch (Exception ex) { await DisplayAlertAsync("Attachment", ex.Message, "OK"); }
    }

    private async Task RecordVoiceAsync()
    {
        PermissionStatus status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted) { await DisplayAlertAsync("Microphone", "Microphone permission is required for voice input.", "OK"); return; }
        try
        {
            if (!_audio.IsRecording)
            {
                await _audio.StartAsync();
                _status.Text = "Recording… tap Mic again to transcribe";
                return;
            }
            _status.Text = "Transcribing…";
            byte[] audio = await _audio.StopAsync();
            string transcript = await _client.TranscribeAsync(audio);
            _prompt.Text = string.IsNullOrWhiteSpace(_prompt.Text) ? transcript : _prompt.Text.TrimEnd() + " " + transcript;
            _status.Text = "Voice transcription ready";
        }
        catch (Exception ex) { _status.Text = "Voice input failed: " + ex.Message; }
    }

    private async Task SpeakLastAsync()
    {
        string text = _messages.LastOrDefault(item => item.Role == "assistant" && !string.IsNullOrWhiteSpace(item.Content))?.Content ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;
        try { _status.Text = "Generating speech…"; await _audio.PlayAsync(await _client.SynthesizeSpeechAsync(text)); _status.Text = "Ready"; }
        catch (Exception ex) { _status.Text = "Speech failed: " + ex.Message; }
    }

    private string AttachmentCaption() => _attachments.Count == 0 ? "" : "\n\n[Attached: " + string.Join(", ", _attachments.Select(a => a.Name)) + "]";

    private static View MessageTemplate()
    {
        var role = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#93C5FD") };
        role.SetBinding(Label.TextProperty, nameof(ChatMessage.Role));
        var content = new Label { FontSize = 15, TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap };
        content.SetBinding(Label.TextProperty, nameof(ChatMessage.Content));
        var reasoning = new Label { FontSize = 12, TextColor = Color.FromArgb("#94A3B8"), FontAttributes = FontAttributes.Italic };
        reasoning.SetBinding(Label.TextProperty, nameof(ChatMessage.Reasoning));
        var status = new Label { FontSize = 11, TextColor = Color.FromArgb("#60A5FA") };
        status.SetBinding(Label.TextProperty, nameof(ChatMessage.Status));
        var tools = new CollectionView
        {
            ItemTemplate = new DataTemplate(() =>
            {
                var label = new Label { FontSize = 11, TextColor = Color.FromArgb("#A7F3D0") };
                label.SetBinding(Label.TextProperty, new Binding(".", stringFormat: "Tool: {0}"));
                return label;
            }),
            HeightRequest = 36
        };
        tools.SetBinding(ItemsView.ItemsSourceProperty, nameof(ChatMessage.Tools));
        var border = new Border { Margin = new Thickness(10, 5), Padding = 12, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = new VerticalStackLayout { Spacing = 5, Children = { role, content, reasoning, tools, status } } };
        border.SetBinding(Border.BackgroundColorProperty, nameof(ChatMessage.BubbleColor));
        return border;
    }
}

public enum MobileChatMode
{
    General,
    Advanced
}

internal static class ChatGridExtensions
{
    public static T Column<T>(this T view, int column) where T : BindableObject { Grid.SetColumn(view, column); return view; }
}
