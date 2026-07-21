using System.Collections.ObjectModel;
using JackLLM.Mobile.Controls;
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
    private readonly MobileGenerationCoordinator _generation;
    private readonly RecentSessionStore _recentSessions;
    private readonly string _requestedSessionId;
    private readonly MobileAudioService _audio = new();
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly List<AttachmentInfo> _attachments = new();
    private readonly CollectionView _messageList;
    private readonly Picker _models;
    private readonly Picker _services;
    private readonly Slider _reasoningSlider;
    private readonly Label _reasoningLabel;
    private readonly Switch _sessionReasoningInherit;
    private readonly Button _generalMode;
    private readonly Button _advancedMode;
    private readonly Button _planMode;
    private readonly Editor _prompt;
    private readonly Label _status;
    private readonly NetworkHealthView _networkHealth;
    private readonly Button _send;
    private readonly Border _liveActivityCard;
    private readonly Label _liveActivityText;
    private readonly ProgressBar _liveProgress;
    private readonly ActivityIndicator _liveIndicator;
    private readonly Button _voice;
    private readonly ToolbarItem _speakItem;
    private IReadOnlyList<ModelInfo> _allModels = Array.Empty<ModelInfo>();
    private MobileChatMode _mode = MobileChatMode.General;
    private CancellationTokenSource? _networkHealthCancellation;
    private DateTimeOffset _lastAutoScroll = DateTimeOffset.MinValue;
    private bool _sessionInitialized;
    private volatile bool _pageActive;
    private ChatMessage? _activeAssistant;
    private string _sessionId = Guid.NewGuid().ToString("N");

    public ChatHostPage(
        ServerInfo server,
        JackLlmClient client,
        ServerStore store,
        MobileGenerationCoordinator generation,
        RecentSessionStore recentSessions,
        string requestedSessionId = "")
    {
        _server = server; _client = client; _store = store; _generation = generation;
        _recentSessions = recentSessions;
        _requestedSessionId = requestedSessionId;
        Title = "JackLLM";
        BackgroundColor = Color.FromArgb("#0B1020");
        _status = new Label { Text = "Connecting…", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11, Margin = new Thickness(4, 0) };
        _networkHealth = new NetworkHealthView { Margin = new Thickness(6, 0), VerticalOptions = LayoutOptions.Center };
        _models = new Picker { Title = "Model", TextColor = Colors.White, TitleColor = Color.FromArgb("#94A3B8"), HorizontalOptions = LayoutOptions.Fill };
        _services = new Picker
        {
            Title = "Service",
            ItemsSource = new[] { "agent", "companion", "image_generation", "audio_generation", "video_generation" },
            SelectedIndex = 0,
            TextColor = Colors.White,
            WidthRequest = 142
        };
        _services.SelectedIndexChanged += (_, _) => ApplyModelFilter();
        _reasoningSlider = new Slider { Minimum = 0, Maximum = 4, Value = Preferences.Default.Get(ReasoningPreferenceKey, 4d), MinimumTrackColor = Color.FromArgb("#60A5FA"), MaximumTrackColor = Color.FromArgb("#334155"), ThumbColor = Color.FromArgb("#93C5FD") };
        _reasoningLabel = new Label { TextColor = Color.FromArgb("#BFDBFE"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center };
        _sessionReasoningInherit = new Switch { IsToggled = true, OnColor = Color.FromArgb("#2563EB") };
        _reasoningSlider.ValueChanged += (_, e) => { _reasoningSlider.Value = Math.Round(e.NewValue); UpdateReasoningUi(); if (_sessionReasoningInherit.IsToggled) Preferences.Default.Set(ReasoningPreferenceKey, _reasoningSlider.Value); };
        _sessionReasoningInherit.Toggled += (_, _) => UpdateReasoningUi();
        _generalMode = new Button { Text = "●  General", CornerRadius = 13, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, FontSize = 11, Padding = new Thickness(9, 5) };
        _advancedMode = new Button { Text = "◆  Advanced", CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, FontSize = 11, Padding = new Thickness(9, 5) };
        _generalMode.Clicked += (_, _) => SetMode(MobileChatMode.General);
        _advancedMode.Clicked += (_, _) => SetMode(MobileChatMode.Advanced);
        _planMode = new Button { Text = "Plan", CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, FontSize = 11, Padding = new Thickness(9, 5) };
        _planMode.Clicked += (_, _) => SetMode(MobileChatMode.Plan);
        _messageList = new CollectionView
        {
            ItemsSource = _messages,
            ItemTemplate = new DataTemplate(MessageTemplate),
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepLastItemInView,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems
        };
        _prompt = new Editor { Placeholder = "Message JackLLM…", AutoSize = EditorAutoSizeOption.TextChanges, MaximumHeightRequest = 130, TextColor = Colors.White, PlaceholderColor = Color.FromArgb("#64748B"), BackgroundColor = Colors.Transparent };
        _send = new Button { Text = "↑", FontSize = 24, FontAttributes = FontAttributes.Bold, CornerRadius = 15, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, WidthRequest = 54 };
        _send.Clicked += async (_, _) => { if (!_generation.IsGenerating) await SendAsync(); else await StopAsync(); };
        var attach = new Button { Text = "＋", FontSize = 22, CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 46 };
        attach.Clicked += async (_, _) => await AddAttachmentAsync();
        _voice = new Button { Text = "🎙", FontSize = 18, CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 48 };
        _voice.Clicked += async (_, _) => await RecordVoiceAsync();

        var workspaceItem = new ToolbarItem("▦", null, async () => await Navigation.PushAsync(new SessionsPage(_server, _client, LoadSessionAsync))) { AutomationId = "WorkspaceSessions" };
        var dreamItem = new ToolbarItem("☾", null, async () => await Navigation.PushAsync(new DreamManagementPage(_server, _client))) { AutomationId = "DreamManagement" };
        var newItem = new ToolbarItem("＋", null, NewSession) { AutomationId = "NewSession" };
        _speakItem = new ToolbarItem("🔊", null, async () => await SpeakLastAsync()) { AutomationId = "SpeakResponse" };
        AutomationProperties.SetHelpText(workspaceItem, "Workspace and synchronized sessions");
        AutomationProperties.SetHelpText(newItem, "New session");
        AutomationProperties.SetHelpText(_speakItem, "Read the latest response aloud");
        ToolbarItems.Add(workspaceItem);
        ToolbarItems.Add(dreamItem);
        ToolbarItems.Add(newItem);
        ToolbarItems.Add(_speakItem);

        var modeRow = new HorizontalStackLayout { Spacing = 7, Padding = new Thickness(10, 6, 10, 3), BackgroundColor = Color.FromArgb("#111827"), Children = { _generalMode, _advancedMode, _planMode } };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, BackgroundColor = Color.FromArgb("#111827"), Padding = new Thickness(10, 2) };
        header.Add(_models, 0); header.Add(_networkHealth, 1); header.Add(_services, 2);
        var reasoningRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(12, 2), BackgroundColor = Color.FromArgb("#111827"), ColumnSpacing = 8 };
        reasoningRow.Add(_reasoningLabel, 0); reasoningRow.Add(_reasoningSlider, 1); reasoningRow.Add(new Label { Text = "Inherit", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center }, 2); reasoningRow.Add(_sessionReasoningInherit, 3);
        _liveActivityText = new Label { Text = "Preparing compute…", TextColor = Color.FromArgb("#BFDBFE"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        _liveProgress = new ProgressBar { Progress = 0, ProgressColor = Color.FromArgb("#60A5FA"), BackgroundColor = Color.FromArgb("#26334D"), HeightRequest = 3 };
        _liveIndicator = new ActivityIndicator { IsRunning = false, Color = Color.FromArgb("#60A5FA"), WidthRequest = 20, HeightRequest = 20 };
        var liveGrid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        liveGrid.Add(_liveIndicator, 0, 0); Grid.SetRowSpan(_liveIndicator, 2); liveGrid.Add(_liveActivityText, 1, 0); liveGrid.Add(_liveProgress, 1, 1);
        _liveActivityCard = new Border { IsVisible = false, Margin = new Thickness(10, 4), Padding = new Thickness(10, 7), BackgroundColor = Color.FromArgb("#101D36"), Stroke = Color.FromArgb("#1D4ED8"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = liveGrid };
        var composer = new Border { Margin = new Thickness(10, 6, 10, 10), Padding = new Thickness(8), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 18 }, Content = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Children = { attach, _voice.Column(1), _prompt.Column(2), _send.Column(3) } } };
        Content = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }, Children = { modeRow.Row(0), header.Row(1), reasoningRow.Row(2), _status.Row(3), _liveActivityCard.Row(4), _messageList.Row(5), composer.Row(6) } };
        RestoreModePreference();
        UpdateReasoningUi();
        SetMode(_mode);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _pageActive = true;
        _generation.SnapshotChanged -= OnGenerationSnapshotChanged;
        _generation.SnapshotChanged += OnGenerationSnapshotChanged;
        AppVisibilityService.VisibilityChanged -= OnAppVisibilityChanged;
        AppVisibilityService.VisibilityChanged += OnAppVisibilityChanged;
        try
        {
            await _client.ConnectAsync(_server);
            var models = await _client.GetModelsAsync();
            bool voiceSupported = await _client.SupportsVoiceAsync();
            _voice.IsEnabled = voiceSupported;
            _voice.Opacity = voiceSupported ? 1 : 0.4;
            _speakItem.IsEnabled = voiceSupported;
            AutomationProperties.SetHelpText(_voice, voiceSupported ? "Record a voice prompt" : "Voice is unavailable on this Workstation");
            AutomationProperties.SetHelpText(_speakItem, voiceSupported ? "Read the latest response aloud" : "Voice is unavailable on this Workstation");
            _allModels = models.ToList();
            _models.ItemDisplayBinding = new Binding(nameof(ModelInfo.Name));
            ApplyModelFilter();
            _status.Text = "Connected securely to " + new Uri(_server.Endpoint).Host;
            _server.IsSaved = true; _store.Save(_server);
            StartNetworkHealthMonitor();
            await InitializeSessionAsync();
            ApplyGenerationSnapshot(_generation.Current);
        }
        catch (Exception ex) { _status.Text = "Connection failed: " + ex.Message; }
    }

    protected override void OnDisappearing()
    {
        _pageActive = false;
        _generation.SnapshotChanged -= OnGenerationSnapshotChanged;
        AppVisibilityService.VisibilityChanged -= OnAppVisibilityChanged;
        base.OnDisappearing();
        _networkHealthCancellation?.Cancel();
        _networkHealthCancellation?.Dispose();
        _networkHealthCancellation = null;
    }

    private void StartNetworkHealthMonitor()
    {
        _networkHealthCancellation?.Cancel();
        _networkHealthCancellation?.Dispose();
        _networkHealthCancellation = new CancellationTokenSource();
        _ = MonitorNetworkHealthAsync(_networkHealthCancellation.Token);
    }

    private async Task MonitorNetworkHealthAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan? latency = await _client.MeasureHealthAsync(cancellationToken);
            int bars = latency switch
            {
                null => 0,
                { TotalMilliseconds: <= 150 } => 4,
                { TotalMilliseconds: <= 400 } => 3,
                { TotalMilliseconds: <= 1000 } => 2,
                _ => 1
            };
            Dispatcher.Dispatch(() => _networkHealth.SetBars(bars));
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RestoreModePreference()
    {
        string value = Preferences.Default.Get(ModePreferenceKey, nameof(MobileChatMode.General));
        _mode = Enum.TryParse<MobileChatMode>(value, true, out var parsed) ? parsed : MobileChatMode.General;
    }

    private void SetMode(MobileChatMode mode)
    {
        _mode = mode;
        Preferences.Default.Set(ModePreferenceKey, mode.ToString());
        bool advanced = mode == MobileChatMode.Advanced;
        bool planning = mode == MobileChatMode.Plan;
        _generalMode.BackgroundColor = advanced ? Color.FromArgb("#1F2937") : Color.FromArgb("#2563EB");
        _advancedMode.BackgroundColor = advanced ? Color.FromArgb("#7C3AED") : Color.FromArgb("#1F2937");
        _planMode.BackgroundColor = planning ? Color.FromArgb("#7C3AED") : Color.FromArgb("#1F2937");
        _services.IsVisible = advanced;
        if (advanced && _services.SelectedIndex < 0) _services.SelectedIndex = 0;
        _prompt.Placeholder = planning ? "Describe what you want planned..." : advanced ? "Ask JackLLM to work, use tools, or generate media..." : "Chat with JackLLM...";
        _status.Text = advanced ? "Advanced Mode (Work): service controls and tool activity enabled" : "General Mode (chat): streamlined conversation";
        ApplyModelFilter();
    }

    private void ApplyModelFilter()
    {
        var previous = _models.SelectedItem as ModelInfo;
        string selectedService = _services.SelectedItem as string ?? "agent";
        IEnumerable<ModelInfo> candidates;
        if (_mode == MobileChatMode.General)
        {
            candidates = _allModels.Where(item => item.IsGeneralChatCandidate);
        }
        else if (selectedService.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            // Agent mode must prefer a concrete tool-capable runtime. The Auto
            // router can temporarily advertise a stale loaded model and finish
            // with reasoning but no visible answer on mobile.
            List<ModelInfo> concreteToolModels = _allModels
                .Where(item => item.SupportsTools && item.IsAvailable && !item.Id.Equals("auto", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.IsLoaded)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            candidates = concreteToolModels.Count > 0
                ? concreteToolModels
                : _allModels.Where(item => item.SupportsTools && item.IsAvailable);
        }
        else
        {
            candidates = _allModels;
        }
        List<ModelInfo> models = candidates.ToList();
        if (models.Count == 0 && _allModels.Count > 0) models = _allModels.ToList();
        _models.ItemsSource = models;
        int selected = previous is null ? -1 : models.FindIndex(item => item.Id.Equals(previous.Id, StringComparison.OrdinalIgnoreCase));
        if (selected < 0 && models.Count > 0)
            selected = Math.Max(0, models.FindIndex(item => item.IsGeneralChatCandidate));
        _models.SelectedIndex = selected >= 0 ? selected : -1;
    }

    private string ModePreferenceKey => "jackllm.mobile.mode." + _server.LaunchKey;
    private string ReasoningPreferenceKey => "jackllm.mobile.reasoning." + _server.LaunchKey;
    private static readonly string[] ReasoningLevels = ["Minimal", "Low", "Medium", "High", "Auto"];
    private string EffectiveReasoningLevel => ReasoningLevels[(int)Math.Clamp(Math.Round(_reasoningSlider.Value), 0, 4)].ToLowerInvariant();
    private void UpdateReasoningUi() => _reasoningLabel.Text = "Reasoning: " + EffectiveReasoningLevel + (_sessionReasoningInherit.IsToggled ? " (global)" : " (session)");

    private async Task SendAsync()
    {
        string text = _prompt.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
        if (_models.SelectedItem is not ModelInfo model) { await DisplayAlertAsync("No model", "This Workstation did not report an available chat model.", "OK"); return; }
        string service = _mode == MobileChatMode.General || _mode == MobileChatMode.Plan
            ? (_mode == MobileChatMode.Plan ? "agent" : "chat")
            : _services.SelectedItem as string ?? "agent";
        AttachmentInfo[] attachments = _attachments.ToArray();
        bool hasImages = attachments.Any(attachment => attachment.IsImage);
        if (hasImages && !model.SupportsImages)
        {
            bool toolsRequired = service.Equals("agent", StringComparison.OrdinalIgnoreCase);
            List<ModelInfo> visibleModels = ((_models.ItemsSource as IEnumerable<ModelInfo>) ?? Array.Empty<ModelInfo>()).ToList();
            ModelInfo? compatible = visibleModels
                .Where(candidate => candidate.IsAvailable && candidate.SupportsImages && (!toolsRequired || candidate.SupportsTools))
                .OrderByDescending(candidate => candidate.IsLoaded)
                .FirstOrDefault();
            if (compatible is null)
            {
                string requirement = toolsRequired ? "vision and tools" : "vision";
                _status.Text = $"Choose a model that supports {requirement}";
                await DisplayAlertAsync("Image-capable model required", toolsRequired
                    ? "The selected model cannot see images. Advanced Agent mode requires one model that supports both vision and tools."
                    : "The selected model cannot see images. Choose a vision-capable chat model.", "OK");
                return;
            }
            _models.SelectedIndex = visibleModels.FindIndex(candidate => candidate.Id.Equals(compatible.Id, StringComparison.OrdinalIgnoreCase));
            model = compatible;
            _status.Text = "Using " + model.Name + " for image understanding";
        }
        int priorServerMessageCount = _messages.Count(message => !message.IsLocalOnly);
        var user = new ChatMessage { Role = "user", Content = text + AttachmentCaption() };
        var assistant = new ChatMessage { Role = "assistant", Status = "Starting…", IsGenerating = true, IsReasoningExpanded = true };
        _messages.Add(user); _messages.Add(assistant); _prompt.Text = ""; _prompt.Unfocus();
        _attachments.Clear();
        _send.Text = "■"; _status.Text = "Generating…";
        SetLiveActivity(true, "Warming up model…", 0);
        if (service.Equals("agent", StringComparison.OrdinalIgnoreCase) && !model.SupportsTools)
        {
            _messages.Remove(assistant);
            _messages.Remove(user);
            _attachments.AddRange(attachments);
            _prompt.Text = text;
            _status.Text = "Choose a tool-capable model for Agent";
            _send.Text = "↑";
            SetLiveActivity(false, "", 0);
            await DisplayAlertAsync("Agent model required", "Advanced Agent mode needs a model that supports tools. Choose Auto or a tool-capable model.", "OK");
            return;
        }
        ChatMessage[] requestMessages = _messages
            .Where(message => message != assistant && !message.IsLocalOnly)
            .Select(message => new ChatMessage { Role = message.Role, Content = message.Content })
            .ToArray();
        if (_mode == MobileChatMode.Plan && requestMessages.Length > 0)
        {
            ChatMessage planUser = requestMessages[^1];
            planUser.Content = "[PLAN MODE: read-only inspection only. Do not implement or modify files. Ask focused clarification questions with 2-3 options and accept a custom answer. Produce a decision-complete <proposed_plan> and wait for explicit approval.]\n\n" + planUser.Content;
        }
        _recentSessions.Remember(_server.LaunchKey, _sessionId);
        bool started = await _generation.StartAsync(new MobileGenerationRequest(
            _server, _client, _sessionId, model.Id, service, EffectiveReasoningLevel,
            _sessionReasoningInherit.IsToggled ? "inherit" : EffectiveReasoningLevel,
            requestMessages, attachments, priorServerMessageCount, user.Content));
        if (!started)
        {
            _messages.Remove(assistant);
            _messages.Remove(user);
            _attachments.AddRange(attachments);
            _prompt.Text = text;
            _status.Text = "Another mobile response is still generating";
            _send.Text = "↑";
            SetLiveActivity(false, "", 0);
            return;
        }
        assistant.GenerationId = _generation.Current.GenerationId;
        _activeAssistant = assistant;
        ApplyGenerationSnapshot(_generation.Current);
    }

    private Task StopAsync() => _generation.StopAsync();

    private void OnGenerationSnapshotChanged(object? sender, MobileGenerationSnapshot snapshot)
    {
        if (!_pageActive || !AppVisibilityService.IsActive) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_pageActive && AppVisibilityService.IsActive) ApplyGenerationSnapshot(snapshot);
        });
    }

    private void OnAppVisibilityChanged(bool active)
    {
        if (!active || !_pageActive) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_pageActive && AppVisibilityService.IsActive) ApplyGenerationSnapshot(_generation.Current);
        });
    }

    private void ApplyGenerationSnapshot(MobileGenerationSnapshot snapshot)
    {
        if (!_pageActive || !AppVisibilityService.IsActive || string.IsNullOrWhiteSpace(snapshot.GenerationId)) return;
        if (!snapshot.ServerKey.Equals(_server.LaunchKey, StringComparison.OrdinalIgnoreCase) ||
            !snapshot.SessionId.Equals(_sessionId, StringComparison.OrdinalIgnoreCase)) return;

        ChatMessage? assistant = _activeAssistant;
        if (assistant is null || !_messages.Contains(assistant) || !assistant.GenerationId.Equals(snapshot.GenerationId, StringComparison.Ordinal))
        {
            assistant = _messages.FirstOrDefault(message => message.GenerationId.Equals(snapshot.GenerationId, StringComparison.Ordinal));
            if (assistant is null)
            {
                List<ChatMessage> serverMessages = _messages.Where(message => !message.IsLocalOnly).ToList();
                assistant = serverMessages
                    .Skip(Math.Min(snapshot.PriorServerMessageCount, serverMessages.Count))
                    .LastOrDefault(message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
            }
            if (assistant is null)
            {
                ChatMessage? lastServerMessage = _messages.LastOrDefault(message => !message.IsLocalOnly);
                if (lastServerMessage is null || !lastServerMessage.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                    !lastServerMessage.Content.Equals(snapshot.UserContent, StringComparison.Ordinal))
                    _messages.Add(new ChatMessage { Role = "user", Content = snapshot.UserContent, GenerationId = snapshot.GenerationId });
                assistant = new ChatMessage { Role = "assistant", GenerationId = snapshot.GenerationId };
                _messages.Add(assistant);
            }
            assistant.GenerationId = snapshot.GenerationId;
            _activeAssistant = assistant;
        }

        assistant.Content = snapshot.Content;
        assistant.Reasoning = snapshot.Reasoning;
        assistant.Status = snapshot.Status;
        assistant.Telemetry = snapshot.Telemetry;
        assistant.RouteSummary = snapshot.RouteSummary;
        assistant.IsGenerating = snapshot.IsGenerating;
        assistant.IsReasoningExpanded = snapshot.IsGenerating && string.IsNullOrWhiteSpace(snapshot.Content);
        assistant.Tools.Clear();
        foreach (ToolActivity tool in snapshot.Tools)
            assistant.Tools.Add(new ToolActivity { Name = tool.Name, Status = tool.Status, Detail = tool.Detail });

        _send.Text = snapshot.IsGenerating ? "■" : "↑";
        _status.Text = snapshot.HasError ? snapshot.Status : snapshot.IsStopped ? "Generation stopped" : snapshot.IsGenerating ? (snapshot.Status.Length > 0 ? snapshot.Status : "Generating…") : "Ready";
        SetLiveActivity(snapshot.IsGenerating, string.IsNullOrWhiteSpace(snapshot.Telemetry) ? snapshot.Status : snapshot.Telemetry, snapshot.Progress);
        if (DateTimeOffset.UtcNow - _lastAutoScroll > TimeSpan.FromMilliseconds(300))
        {
            _lastAutoScroll = DateTimeOffset.UtcNow;
            try { _messageList.ScrollTo(assistant, position: ScrollToPosition.End, animate: false); }
            catch { }
        }
    }

    private void NewSession()
    {
        if (_generation.IsGenerating) _ = StopAsync();
        _sessionId = Guid.NewGuid().ToString("N");
        _messages.Clear();
        _messages.Add(CreateWelcomeMessage());
        _sessionReasoningInherit.IsToggled = true;
        _reasoningSlider.Value = Preferences.Default.Get(ReasoningPreferenceKey, 4d);
        UpdateReasoningUi();
        _status.Text = "New conversation";
        _recentSessions.Remember(_server.LaunchKey, _sessionId);
    }

    private async Task InitializeSessionAsync()
    {
        if (_sessionInitialized) return;
        _sessionInitialized = true;
        MobileGenerationSnapshot active = _generation.Current;
        if (active.IsGenerating && active.ServerKey.Equals(_server.LaunchKey, StringComparison.OrdinalIgnoreCase))
        {
            _sessionId = active.SessionId;
            _messages.Clear();
            ApplyGenerationSnapshot(active);
            _recentSessions.Remember(_server.LaunchKey, _sessionId);
            return;
        }
        string preferredSessionId = _requestedSessionId;
        if (string.IsNullOrWhiteSpace(preferredSessionId))
            preferredSessionId = _recentSessions.FindRecent(_server.LaunchKey, TimeSpan.FromHours(1))?.SessionId ?? "";
        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            try
            {
                await LoadSessionAsync(preferredSessionId);
                _status.Text = "Continued your recent session";
                return;
            }
            catch
            {
                // The remembered session may have been deleted on another client.
            }
        }
        try
        {
            IReadOnlyList<ChatSessionInfo> sessions = await _client.GetSessionsAsync();
            ChatSessionInfo? latest = sessions
                .Where(session => session.UpdatedAt != default)
                .OrderByDescending(session => session.UpdatedAt)
                .FirstOrDefault();
            if (latest is not null && latest.UpdatedAt >= DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)))
            {
                await LoadSessionAsync(latest.Id);
                _status.Text = "Continued recent session · " + latest.UpdatedDisplay;
                return;
            }
        }
        catch
        {
            // Session discovery should never prevent the user from starting a chat.
        }
        NewSession();
    }

    private static ChatMessage CreateWelcomeMessage() => new()
    {
        Role = "JackLLM Mobile",
        IsLocalOnly = true,
        Content = """
            # Welcome to JackLLM Mobile 👋

            Your setup shapes every answer:

            - **Model** changes knowledge, style, and capability.
            - **Reasoning** can improve difficult answers, but may take longer.
            - **Speed** depends on the model, reasoning level, and Workstation hardware.

            **Pick what feels right, then start chatting.**
            """
    };

    public async Task LoadSessionAsync(string id)
    {
        ChatSessionDetail detail = await _client.GetSessionAsync(id);
        _sessionId = detail.Id;
        _recentSessions.Remember(_server.LaunchKey, _sessionId);
        string savedReasoning = string.IsNullOrWhiteSpace(detail.ReasoningLevel) ? "inherit" : detail.ReasoningLevel.ToLowerInvariant();
        _sessionReasoningInherit.IsToggled = savedReasoning == "inherit";
        if (!_sessionReasoningInherit.IsToggled)
        {
            int reasoningIndex = Array.FindIndex(ReasoningLevels, level => level.Equals(savedReasoning, StringComparison.OrdinalIgnoreCase));
            if (reasoningIndex >= 0) _reasoningSlider.Value = reasoningIndex;
        }
        else _reasoningSlider.Value = Preferences.Default.Get(ReasoningPreferenceKey, 4d);
        UpdateReasoningUi();
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

    private async Task ReconcileCompletedSessionAsync(ChatMessage streamedAssistant, int priorServerMessageCount)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                ChatSessionDetail detail = await _client.GetSessionAsync(_sessionId);
                // Session persistence can lag behind the completed stream. Only
                // reconcile against messages created by this request; otherwise a
                // fast refresh can copy the previous assistant response into the
                // new bubble. Retry briefly until the request-local answer exists.
                ChatMessage? saved = detail.Messages
                    .Skip(Math.Min(priorServerMessageCount, detail.Messages.Count))
                    .LastOrDefault(message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                        && (!string.IsNullOrWhiteSpace(message.Content) || !string.IsNullOrWhiteSpace(message.Reasoning)));
                if (saved is not null)
                {
                    if (saved.Content.Length >= streamedAssistant.Content.Length) streamedAssistant.Content = saved.Content;
                    if (saved.Reasoning.Length >= streamedAssistant.Reasoning.Length) streamedAssistant.Reasoning = saved.Reasoning;
                    ExtractEmbeddedReasoning(streamedAssistant);
                    return;
                }
            }
            catch { }

            if (attempt < 9) await Task.Delay(300);
        }
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

    private static async Task AppendBufferedAsync(ChatMessage message, string text, bool reasoning, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length <= 64)
        {
            if (reasoning) message.Reasoning += text;
            else { message.Content += text; ExtractEmbeddedReasoning(message); }
            return;
        }

        int targetChunks = Math.Clamp(text.Length / 12, 1, 100);
        int targetChunkSize = Math.Max(12, (int)Math.Ceiling(text.Length / (double)targetChunks));
        int offset = 0;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(text.Length, offset + targetChunkSize);
            if (end < text.Length)
            {
                int wordEnd = text.IndexOfAny([' ', '\n', '\t'], end);
                if (wordEnd >= 0 && wordEnd - end <= 10) end = wordEnd + 1;
            }
            string chunk = text[offset..end];
            if (reasoning) message.Reasoning += chunk;
            else { message.Content += chunk; ExtractEmbeddedReasoning(message); }
            offset = end;
            if (offset < text.Length) await Task.Delay(20, cancellationToken);
        }
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

    private View MessageTemplate()
    {
        var role = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#93C5FD") };
        role.SetBinding(Label.TextProperty, nameof(ChatMessage.Role));
        var content = new MarkdownMessageView();
        content.SetBinding(MarkdownMessageView.MarkdownProperty, nameof(ChatMessage.Content));
        var route = new Label { FontSize = 11, TextColor = Color.FromArgb("#C4B5FD"), BackgroundColor = Color.FromArgb("#312E81"), Padding = new Thickness(8, 4) };
        route.SetBinding(Label.TextProperty, nameof(ChatMessage.RouteSummary));
        route.SetBinding(IsVisibleProperty, nameof(ChatMessage.HasRouteSummary));
        var reasoning = new MarkdownMessageView();
        reasoning.SetBinding(MarkdownMessageView.MarkdownProperty, nameof(ChatMessage.Reasoning));
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
        var border = new Border { Margin = new Thickness(10, 5), Padding = 12, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = new VerticalStackLayout { Spacing = 7, Children = { role, route, reasoningExpander, content, tools, telemetry, status } } };
        border.SetBinding(Border.BackgroundColorProperty, nameof(ChatMessage.BubbleColor));
        AttachMessageLongPress(border);
        return border;
    }

    private void AttachMessageLongPress(Border bubble)
    {
        CancellationTokenSource? hold = null;
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, _) =>
        {
            hold?.Cancel();
            hold?.Dispose();
            hold = new CancellationTokenSource();
            CancellationToken token = hold.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(650, token);
                    if (!token.IsCancellationRequested)
                        MainThread.BeginInvokeOnMainThread(async () => await ShowMessageActionsAsync(bubble));
                }
                catch (OperationCanceledException) { }
            });
        };
        void CancelHold()
        {
            hold?.Cancel();
            hold?.Dispose();
            hold = null;
        }
        pointer.PointerReleased += (_, _) => CancelHold();
        pointer.PointerExited += (_, _) => CancelHold();
        bubble.GestureRecognizers.Add(pointer);
    }

    private async Task ShowMessageActionsAsync(Border bubble)
    {
        if (bubble.BindingContext is not ChatMessage message || string.IsNullOrWhiteSpace(message.Content)) return;
        bool assistant = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
        string[] actions = assistant && _speakItem.IsEnabled
            ? ["Copy", "Re-prompt", "Quote in reply", "Share", "Read aloud"]
            : ["Copy", "Re-prompt", "Quote in reply", "Share"];
        string? action = await DisplayActionSheetAsync(assistant ? "Assistant message" : "Your prompt", "Cancel", null, actions);
        switch (action)
        {
            case "Copy":
                await Clipboard.Default.SetTextAsync(message.Content);
                _status.Text = "Copied to clipboard";
                break;
            case "Re-prompt":
                _prompt.Text = message.Content;
                _prompt.Focus();
                _status.Text = "Prompt ready to resend";
                break;
            case "Quote in reply":
                string quote = string.Join("\n", message.Content.Replace("\r", "").Split('\n').Select(line => "> " + line));
                _prompt.Text = string.IsNullOrWhiteSpace(_prompt.Text) ? quote + "\n\n" : _prompt.Text.TrimEnd() + "\n\n" + quote + "\n\n";
                _prompt.Focus();
                break;
            case "Share":
                await Share.Default.RequestAsync(new ShareTextRequest { Text = message.Content, Title = "JackLLM message" });
                break;
            case "Read aloud":
                try { _status.Text = "Generating speech…"; await _audio.PlayAsync(await _client.SynthesizeSpeechAsync(message.Content)); _status.Text = "Ready"; }
                catch (Exception ex) { _status.Text = "Speech failed: " + ex.Message; }
                break;
        }
    }
}

public enum MobileChatMode
{
    General,
    Advanced,
    Plan
}

internal static class ChatGridExtensions
{
    public static T Column<T>(this T view, int column) where T : BindableObject { Grid.SetColumn(view, column); return view; }
}
