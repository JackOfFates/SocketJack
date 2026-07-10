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
    private readonly Border _liveActivityCard;
    private readonly Label _liveActivityText;
    private readonly ProgressBar _liveProgress;
    private readonly ActivityIndicator _liveIndicator;
    private IReadOnlyList<ModelInfo> _allModels = Array.Empty<ModelInfo>();
    private MobileChatMode _mode = MobileChatMode.General;
    private CancellationTokenSource? _streamCancellation;
    private CancellationTokenSource? _telemetryCancellation;
    private DateTimeOffset _lastAutoScroll = DateTimeOffset.MinValue;
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

        ToolbarItems.Add(new ToolbarItem("Sessions", null, async () => await Navigation.PushAsync(new SessionsPage(_client, LoadSessionAsync))));
        ToolbarItems.Add(new ToolbarItem("New", null, NewSession));
        ToolbarItems.Add(new ToolbarItem("Speak", null, async () => await SpeakLastAsync()));

        var modeRow = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(10, 8, 10, 4), BackgroundColor = Color.FromArgb("#111827"), Children = { _generalMode, _advancedMode } };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, BackgroundColor = Color.FromArgb("#111827"), Padding = new Thickness(10, 2) };
        header.Add(_models, 0); header.Add(_services, 1);
        _liveActivityText = new Label { Text = "Preparing compute…", TextColor = Color.FromArgb("#BFDBFE"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        _liveProgress = new ProgressBar { Progress = 0, ProgressColor = Color.FromArgb("#60A5FA"), BackgroundColor = Color.FromArgb("#26334D"), HeightRequest = 3 };
        _liveIndicator = new ActivityIndicator { IsRunning = false, Color = Color.FromArgb("#60A5FA"), WidthRequest = 20, HeightRequest = 20 };
        var liveGrid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        liveGrid.Add(_liveIndicator, 0, 0); Grid.SetRowSpan(_liveIndicator, 2); liveGrid.Add(_liveActivityText, 1, 0); liveGrid.Add(_liveProgress, 1, 1);
        _liveActivityCard = new Border { IsVisible = false, Margin = new Thickness(10, 4), Padding = new Thickness(10, 7), BackgroundColor = Color.FromArgb("#101D36"), Stroke = Color.FromArgb("#1D4ED8"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = liveGrid };
        var composer = new Border { Margin = new Thickness(10, 6, 10, 10), Padding = new Thickness(8), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 18 }, Content = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Children = { attach, voice.Column(1), _prompt.Column(2), _send.Column(3) } } };
        Content = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }, Children = { modeRow.Row(0), header.Row(1), _status.Row(2), _liveActivityCard.Row(3), _messageList.Row(4), composer.Row(5) } };
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
        var assistant = new ChatMessage { Role = "assistant", Status = "Starting…", IsGenerating = true, IsReasoningExpanded = true };
        _messages.Add(user); _messages.Add(assistant); _prompt.Text = ""; _prompt.Unfocus();
        var attachments = _attachments.ToArray(); _attachments.Clear();
        _streamCancellation = new CancellationTokenSource();
        _telemetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_streamCancellation.Token);
        _send.Text = "Stop"; _status.Text = "Generating…";
        SetLiveActivity(true, "Warming up model…", 0);
        Task telemetryTask = MonitorHardwareAsync(assistant, _telemetryCancellation.Token);
        try
        {
            string service = _mode == MobileChatMode.General ? "chat" : _services.SelectedItem?.ToString() ?? "agent";
            await foreach (ChatStreamEvent item in _client.StreamChatAsync(model.Id, service, _sessionId, _messages.Where(m => m != assistant).ToArray(), attachments, _streamCancellation.Token))
            {
                string eventType = item.Type.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                switch (eventType)
                {
                    case "content": case "delta": case "contentdelta": case "answer": case "message":
                        assistant.Content += item.Content;
                        if (!string.IsNullOrWhiteSpace(item.Reasoning)) assistant.Reasoning += item.Reasoning;
                        ExtractEmbeddedReasoning(assistant);
                        if (assistant.HasReasoning && !assistant.IsCapturingEmbeddedReasoning) assistant.IsReasoningExpanded = false;
                        break;
                    case "reasoning": case "reasoningdelta": case "thinking": case "thinkingdelta":
                        assistant.Reasoning += item.Reasoning + item.Content;
                        break;
                    case "progress":
                        assistant.Status = string.IsNullOrWhiteSpace(item.Status) ? "Working…" : item.Status;
                        SetLiveActivity(true, assistant.Status, NormalizeProgress(item.Progress));
                        break;
                    case "usage": UpdateUsageTelemetry(assistant, item); break;
                    case "toolcall": assistant.Tools.Add(new ToolActivity { Name = string.IsNullOrWhiteSpace(item.ToolName) ? "Tool" : item.ToolName, Status = item.ToolStatus, Detail = item.Status }); break;
                    case "error":
                        assistant.Content = string.IsNullOrWhiteSpace(item.Content) ? item.Status : item.Content;
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(assistant.Content) ? "The Workstation returned an error." : assistant.Content);
                    case "done": case "complete": case "completed": case "end":
                        assistant.Status = "";
                        assistant.IsReasoningExpanded = false;
                        break;
                    default:
                        if (!string.IsNullOrWhiteSpace(item.Reasoning)) assistant.Reasoning += item.Reasoning;
                        if (!string.IsNullOrWhiteSpace(item.Content)) assistant.Content += item.Content;
                        ExtractEmbeddedReasoning(assistant);
                        break;
                }
                if (DateTimeOffset.UtcNow - _lastAutoScroll > TimeSpan.FromMilliseconds(300))
                {
                    _lastAutoScroll = DateTimeOffset.UtcNow;
                    _messageList.ScrollTo(assistant, position: ScrollToPosition.End, animate: false);
                }
            }
            await ReconcileCompletedSessionAsync(assistant);
            assistant.IsGenerating = false;
            assistant.IsReasoningExpanded = false;
            assistant.Status = "";
            _status.Text = "Ready";
        }
        catch (OperationCanceledException) { assistant.Status = "Stopped"; assistant.IsGenerating = false; _status.Text = "Generation stopped"; }
        catch (Exception ex) { assistant.Status = "Error: " + ex.Message; assistant.IsGenerating = false; _status.Text = assistant.Status; }
        finally
        {
            _telemetryCancellation?.Cancel();
            try { await telemetryTask; } catch (OperationCanceledException) { }
            _telemetryCancellation?.Dispose(); _telemetryCancellation = null;
            SetLiveActivity(false, "", 0);
            _streamCancellation.Dispose(); _streamCancellation = null; _send.Text = "Send";
            if (_messages.Count > 0) _messageList.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        }
    }

    private void Stop() { _streamCancellation?.Cancel(); if (!string.IsNullOrWhiteSpace(_client.ActiveStreamId)) _ = _client.StopAsync(_client.ActiveStreamId); }
    private void NewSession() { Stop(); _sessionId = Guid.NewGuid().ToString("N"); _messages.Clear(); _status.Text = "New conversation"; }

    private async Task LoadSessionAsync(string id)
    {
        ChatSessionDetail detail = await _client.GetSessionAsync(id);
        _sessionId = detail.Id;
        _messages.Clear();
        foreach (ChatMessage message in detail.Messages)
        {
            if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                ExtractEmbeddedReasoning(message);
                message.IsGenerating = false;
                message.IsReasoningExpanded = false;
            }
            _messages.Add(message);
        }
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

    private async Task MonitorHardwareAsync(ChatMessage assistant, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HardwareSnapshot hardware = await _client.GetHardwareAsync(cancellationToken);
                assistant.Telemetry = hardware.Display;
                _liveActivityText.Text = hardware.Display;
            }
            catch (OperationCanceledException) { break; }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task ReconcileCompletedSessionAsync(ChatMessage streamedAssistant)
    {
        try
        {
            ChatSessionDetail detail = await _client.GetSessionAsync(_sessionId);
            ChatMessage? saved = detail.Messages.LastOrDefault(message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
            if (saved is null) return;
            if (saved.Content.Length >= streamedAssistant.Content.Length) streamedAssistant.Content = saved.Content;
            if (saved.Reasoning.Length >= streamedAssistant.Reasoning.Length) streamedAssistant.Reasoning = saved.Reasoning;
            ExtractEmbeddedReasoning(streamedAssistant);
        }
        catch { }
    }

    private void UpdateUsageTelemetry(ChatMessage assistant, ChatStreamEvent item)
    {
        string prompt = item.PromptTokensTotal > 0 ? $" · Prompt {item.PromptTokensLoaded:N0}/{item.PromptTokensTotal:N0}" : "";
        assistant.Status = $"Tokens {item.TokensUsed:N0} · GPU compute {item.GpuSecondsUsed:0.##}s · CPU {item.CpuComputeSecondsUsed:0.##}s · RAM {item.RamGbSecondsUsed:0.##} GB·s{prompt}";
    }

    private void SetLiveActivity(bool visible, string text, double progress)
    {
        _liveActivityCard.IsVisible = visible;
        _liveIndicator.IsRunning = visible;
        if (!string.IsNullOrWhiteSpace(text)) _liveActivityText.Text = text;
        _liveProgress.Progress = progress;
    }

    private static double NormalizeProgress(double? value)
    {
        if (!value.HasValue) return 0;
        return Math.Clamp(value.Value > 1 ? value.Value / 100d : value.Value, 0, 1);
    }

    private static void ExtractEmbeddedReasoning(ChatMessage message)
    {
        string content = message.Content;
        if (string.IsNullOrEmpty(content)) return;

        if (!message.IsCapturingEmbeddedReasoning)
        {
            int thinkStart = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (thinkStart >= 0)
            {
                message.Content = content[..thinkStart];
                content = content[(thinkStart + 7)..];
                message.IsCapturingEmbeddedReasoning = true;
            }
            else
            {
                string[] thinkingMarkers = { "**Thinking:**", "**Thinking:** ", "Thinking:" };
                string? marker = thinkingMarkers.FirstOrDefault(value => content.TrimStart().StartsWith(value, StringComparison.OrdinalIgnoreCase));
                if (marker is null) return;
                content = content.TrimStart()[marker.Length..].TrimStart();
                message.Content = "";
                message.IsCapturingEmbeddedReasoning = true;
            }
        }

        if (!message.IsCapturingEmbeddedReasoning) return;
        string[] answerMarkers = { "</think>", "**Answer:**", "**Final Answer:**", "Final Answer:" };
        int markerIndex = -1;
        string? answerMarker = null;
        foreach (string candidate in answerMarkers)
        {
            int index = content.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (markerIndex < 0 || index < markerIndex)) { markerIndex = index; answerMarker = candidate; }
        }
        if (markerIndex < 0)
        {
            message.Reasoning += content;
            message.Content = "";
            return;
        }

        message.Reasoning += content[..markerIndex].TrimEnd();
        message.Content = content[(markerIndex + answerMarker!.Length)..].TrimStart();
        message.IsCapturingEmbeddedReasoning = false;
        message.IsReasoningExpanded = false;
    }

    private static View MessageTemplate()
    {
        var role = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#93C5FD") };
        role.SetBinding(Label.TextProperty, nameof(ChatMessage.Role));
        var content = new Label { FontSize = 15, TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap };
        content.SetBinding(Label.TextProperty, nameof(ChatMessage.Content));
        var reasoning = new Label { FontSize = 12, TextColor = Color.FromArgb("#CBD5E1"), FontAttributes = FontAttributes.Italic, LineBreakMode = LineBreakMode.WordWrap };
        reasoning.SetBinding(Label.TextProperty, nameof(ChatMessage.Reasoning));
        reasoning.SetBinding(IsVisibleProperty, nameof(ChatMessage.IsReasoningExpanded));
        var reasoningTitle = new Label { FontSize = 12, TextColor = Color.FromArgb("#93C5FD"), FontAttributes = FontAttributes.Bold };
        reasoningTitle.SetBinding(Label.TextProperty, nameof(ChatMessage.ReasoningHeader));
        var reasoningChevron = new Label { FontSize = 15, TextColor = Color.FromArgb("#93C5FD"), HorizontalOptions = LayoutOptions.End };
        reasoningChevron.SetBinding(Label.TextProperty, nameof(ChatMessage.ReasoningChevron));
        var reasoningHeader = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Children = { reasoningTitle, reasoningChevron.Column(1) } };
        var reasoningExpander = new Border { Padding = new Thickness(10, 7), BackgroundColor = Color.FromArgb("#17233A"), Stroke = Color.FromArgb("#334155"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 10 }, Content = new VerticalStackLayout { Spacing = 6, Children = { reasoningHeader, reasoning } } };
        reasoningExpander.SetBinding(IsVisibleProperty, nameof(ChatMessage.ShowReasoning));
        var reasoningTap = new TapGestureRecognizer();
        reasoningTap.Tapped += (_, _) => { if (reasoningExpander.BindingContext is ChatMessage message) message.IsReasoningExpanded = !message.IsReasoningExpanded; };
        reasoningExpander.GestureRecognizers.Add(reasoningTap);
        var telemetry = new Label { FontSize = 10, TextColor = Color.FromArgb("#A7F3D0"), LineBreakMode = LineBreakMode.WordWrap };
        telemetry.SetBinding(Label.TextProperty, nameof(ChatMessage.Telemetry));
        telemetry.SetBinding(IsVisibleProperty, nameof(ChatMessage.HasTelemetry));
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
        var border = new Border { Margin = new Thickness(10, 5), Padding = 12, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = new VerticalStackLayout { Spacing = 7, Children = { role, reasoningExpander, content, tools, telemetry, status } } };
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
