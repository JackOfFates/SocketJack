using System.Collections.ObjectModel;
using heirowLLM.Mobile.Controls;
using heirowLLM.Mobile.Models;
using heirowLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace heirowLLM.Mobile.Pages;

public sealed class ChatHostPage : ContentPage
{
    private readonly ServerInfo _server;
    private readonly HeirowLlmClient _client;
    private readonly ServerStore _store;
    private readonly SecureCredentialStore _credentials;
    private readonly MobileGenerationCoordinator _generation;
    private readonly RecentSessionStore _recentSessions;
    private readonly IChickenChaserOverlayService _chickenOverlay;
    private readonly ChickenChaserOverlayNavigationService _chickenOverlayNavigation;
    private readonly string _requestedSessionId;
    private readonly MobileAudioService _audio = new();
    private readonly ObservableCollection<ChatMessage> _messages = new();
    private readonly List<AttachmentInfo> _attachments = new();
    private readonly HorizontalStackLayout _attachmentStrip;
    private readonly ScrollView _attachmentScroll;
    private readonly CollectionView _messageList;
    private readonly Picker _models;
    private readonly Picker _services;
    private readonly Slider _reasoningSlider;
    private readonly Label _reasoningLabel;
    private readonly Entry _jackhammerCustomBudget;
    private readonly Switch _jackhammerToggle;
    private readonly Border _jackhammerToggleCard;
    private readonly Label _jackhammerToggleLabel;
    private readonly Switch _sessionReasoningInherit;
    private readonly Button _generalMode;
    private readonly Button _advancedMode;
    private readonly Button _planMode;
    private readonly Editor _prompt;
    private readonly Label _status;
    private readonly NetworkHealthView _networkHealth;
    private readonly Button _send;
    private readonly Button _steer;
    private readonly Button _stop;
    private readonly Border _liveActivityCard;
    private readonly Border _contextApprovalCard;
    private readonly Label _contextApprovalTitle;
    private readonly Label _contextApprovalSummary;
    private readonly Button _contextAllowOnce;
    private readonly Button _contextAllowAlways;
    private readonly Button _contextDeny;
    private readonly Label _liveActivityText;
    private readonly ProgressBar _liveProgress;
    private readonly ActivityIndicator _liveIndicator;
    private readonly Button _voice;
    private readonly Button _attach;
    private readonly Button _speakItem;
    private readonly Button _dreamItem;
    private readonly Button _pcAccessItem;
    private readonly Button _sqlManagerItem;
    private readonly Button _administrativeToolsItem;
    private readonly MobileNavigationDrawer _mobileDrawer;
    private readonly Label _drawerProjectStatus;
    private readonly Label _compactContextLabel;
    private readonly BoxView _alignmentTop;
    private readonly BoxView _alignmentBottom;
    private readonly Border _alignmentDrawer;
    private readonly Label _alignmentDrawerScore;
    private readonly Label _alignmentDrawerReason;
    private readonly Label _alignmentDrawerFeatures;
    private readonly Label _alignmentDrawerTraits;
    private readonly Label _alignmentDrawerRecovery;
    private readonly Label _alignmentDrawerModel;
    private readonly Border _alignmentLockScreen;
    private readonly Grid _contentRoot;
    private readonly Grid _chickenChaserOverlayButton;
    private readonly BoxView _chickenChaserNotificationDot;
    private readonly Label _chickenChaserOverlayStatus;
    private readonly Button _chickenChaserOverlayPermissionItem;
    private readonly Border _chickenChaserPermissionBanner;
    private readonly Border _chickenChaserPanel;
    private readonly VerticalStackLayout _chickenChaserMessages;
    private readonly ScrollView _chickenChaserMessageScroll;
    private readonly Editor _chickenChaserPrompt;
    private readonly Button _chickenChaserSend;
    private IReadOnlyList<ModelInfo> _allModels = Array.Empty<ModelInfo>();
    private MobileChatMode _mode = MobileChatMode.General;
    private CancellationTokenSource? _networkHealthCancellation;
    private CancellationTokenSource? _contextApprovalCancellation;
    private ContextApprovalRequest? _pendingContextApproval;
    private DateTimeOffset _lastAutoScroll = DateTimeOffset.MinValue;
    private bool _sessionInitialized;
    private volatile bool _pageActive;
    private ChatMessage? _activeAssistant;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private string _projectId = "unsorted";
    private string _projectName = "Unsorted";
    private string _sessionTitle = "New chat";
    private bool _dreamAvailable = true;
    private bool _sqlAdminAllowed;
    private bool _pcAccessAllowed;
    private MobileAlignmentSnapshot _alignment = new();
    private int _loreIndex;
    private bool _alignmentDrawerOpen;
    private bool _authenticationPageOpen;
    private bool _sessionKnownToServer;
    private PendingFollowUp? _pendingFollowUp;
    private bool _pendingFollowUpStarting;
    private bool _generationWasRunning;
    private bool _stickToLatest = true;

    private sealed record PendingFollowUp(string Text, AttachmentInfo[] Attachments);

    public ChatHostPage(
        ServerInfo server,
        HeirowLlmClient client,
        ServerStore store,
        SecureCredentialStore credentials,
        MobileGenerationCoordinator generation,
        RecentSessionStore recentSessions,
        string requestedSessionId = "")
    {
        _server = server; _client = client; _store = store; _credentials = credentials; _generation = generation;
        _recentSessions = recentSessions;
        _chickenOverlay = IPlatformApplication.Current?.Services.GetService(typeof(IChickenChaserOverlayService)) as IChickenChaserOverlayService
            ?? new UnsupportedChickenChaserOverlayService();
        _chickenOverlayNavigation = IPlatformApplication.Current?.Services.GetService(typeof(ChickenChaserOverlayNavigationService)) as ChickenChaserOverlayNavigationService
            ?? new ChickenChaserOverlayNavigationService();
        _requestedSessionId = requestedSessionId;
        Title = "heirowLLM";
        BackgroundColor = Color.FromArgb("#0B1020");
        NavigationPage.SetHasNavigationBar(this, false);
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
        _reasoningSlider = new Slider { Minimum = 0, Maximum = 5, Value = Preferences.Default.Get(ReasoningPreferenceKey, 4d), MinimumTrackColor = Color.FromArgb("#60A5FA"), MaximumTrackColor = Color.FromArgb("#334155"), ThumbColor = Color.FromArgb("#93C5FD") };
        _reasoningLabel = new Label { TextColor = Color.FromArgb("#BFDBFE"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center };
        _jackhammerCustomBudget = new Entry { Placeholder = "Work cap", Keyboard = Keyboard.Numeric, WidthRequest = 74, TextColor = Colors.White, PlaceholderColor = Color.FromArgb("#64748B"), FontSize = 11 };
        int savedCustomBudget = Preferences.Default.Get(JackhammerCustomBudgetPreferenceKey, 0);
        _jackhammerCustomBudget.Text = savedCustomBudget > 0 ? savedCustomBudget.ToString() : "";
        _jackhammerCustomBudget.Unfocused += (_, _) => SaveJackhammerCustomBudget();
        _jackhammerToggle = new Switch { OnColor = Color.FromArgb("#2563EB"), ThumbColor = Colors.White };
        _jackhammerToggleLabel = new Label { Text = "heirowForge", TextColor = Colors.White, FontSize = 11, VerticalTextAlignment = TextAlignment.Center };
        _jackhammerToggleCard = new Border
        {
            Padding = new Thickness(8, 2),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new HorizontalStackLayout { Spacing = 5, Children = { _jackhammerToggle, _jackhammerToggleLabel } }
        };
        _jackhammerToggle.IsToggled = Preferences.Default.ContainsKey(JackhammerPreferenceKey) && Preferences.Default.Get(JackhammerPreferenceKey, false);
        _jackhammerToggle.Toggled += (_, e) => SetJackhammerEnabled(e.Value, persist: true);
        UpdateJackhammerToggleUi();
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
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepItemsInView,
            ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems
        };
        _messageList.Scrolled += (_, e) =>
        {
            int lastIndex = _messages.Count - 1;
            _stickToLatest = lastIndex < 0 || e.LastVisibleItemIndex >= lastIndex;
        };
        _prompt = new Editor { Placeholder = "Message heirowLLM…", AutoSize = EditorAutoSizeOption.TextChanges, MaximumHeightRequest = 130, TextColor = Colors.White, PlaceholderColor = Color.FromArgb("#64748B"), BackgroundColor = Colors.Transparent };
        _send = new Button { Text = "↑", FontSize = 24, FontAttributes = FontAttributes.Bold, CornerRadius = 15, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, WidthRequest = 54 };
        _send.Clicked += async (_, _) => await SendOrQueueAsync();
        AutomationProperties.SetName(_send, "Send or queue prompt");
        AutomationProperties.SetHelpText(_send, "Send now, or queue this prompt after the running response");
        _steer = new Button { Text = "\u21aa", FontSize = 20, FontAttributes = FontAttributes.Bold, CornerRadius = 13, BackgroundColor = Color.FromArgb("#7C3AED"), TextColor = Colors.White, WidthRequest = 48, IsVisible = false, IsEnabled = false };
        _steer.Clicked += async (_, _) => await SteerAsync();
        AutomationProperties.SetName(_steer, "Steer running response");
        AutomationProperties.SetHelpText(_steer, "Apply this text to the response that is currently running");
        _prompt.TextChanged += (_, _) => UpdateComposerActions();
        _attach = new Button { Text = "＋", FontSize = 22, CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 46 };
        _attach.Clicked += async (_, _) => await AddAttachmentAsync();
        _voice = new Button { Text = "🎙", FontSize = 18, CornerRadius = 13, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, WidthRequest = 48 };
        _voice.Clicked += async (_, _) => await RecordVoiceAsync();
        _attachmentStrip = new HorizontalStackLayout { Spacing = 8, Padding = new Thickness(10, 4) };
        _attachmentScroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _attachmentStrip, IsVisible = false, MaximumHeightRequest = 112 };

        var modeRow = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(GridLength.Star) },
            ColumnSpacing = 6
        };
        modeRow.Add(_generalMode, 0); modeRow.Add(_advancedMode, 1); modeRow.Add(_planMode, 2);
        var modelOptions = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 8
        };
        modelOptions.Add(_models, 0); modelOptions.Add(_services, 1);
        var reasoningRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(12, 2), BackgroundColor = Color.FromArgb("#111827"), ColumnSpacing = 8 };
        reasoningRow.Add(_reasoningLabel, 0); reasoningRow.Add(_reasoningSlider, 1); reasoningRow.Add(_jackhammerCustomBudget, 2); reasoningRow.Add(new Label { Text = "Inherit", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center }, 3); reasoningRow.Add(_sessionReasoningInherit, 4);

        _mobileDrawer = new MobileNavigationDrawer("heirowLLM Mobile", _server.DisplayName);
        _mobileDrawer.ActionFailed += async (_, ex) => await DisplayAlertAsync("Menu action failed", ex.Message, "OK");
        VerticalStackLayout projectSection = _mobileDrawer.AddSection("File");
        _mobileDrawer.AddAction(projectSection, "▦", "Projects & Sessions", OpenSessionsAsync, "MenuProjectsSessions");
        _mobileDrawer.AddAction(projectSection, "📁", "Project Files", async () => await Navigation.PushAsync(new ProjectFilesPage(_client, _sessionId)), "MenuProjectFiles");
        _mobileDrawer.AddAction(projectSection, "⌂", "Workstations", async () => await Navigation.PopToRootAsync(false), "MenuWorkstations");

        VerticalStackLayout editSection = _mobileDrawer.AddSection("Edit");
        _drawerProjectStatus = _mobileDrawer.AddStatus(editSection, "Project: Unsorted", "MenuCurrentProject");
        _mobileDrawer.AddAction(editSection, "＋", "New Session", () => { NewSession(); return Task.CompletedTask; }, "MenuNewSession");
        _mobileDrawer.AddAction(editSection, "✎", "Edit Session Title", RenameCurrentSessionAsync, "MenuRenameSession");
        _mobileDrawer.AddAction(editSection, "↪", "Move Session to Project", MoveCurrentSessionAsync, "MenuMoveSession");
        _mobileDrawer.AddAction(editSection, "⌫", "Delete Session", DeleteCurrentSessionAsync, "MenuDeleteSession", danger: true);

        VerticalStackLayout toolsSection = _mobileDrawer.AddSection("Tools");
        _mobileDrawer.AddAction(toolsSection, "H", "heirowDirector", async () => await OpenWorkstationRouteAsync("/heirowDirector"), "MenuJackDirector");
        _mobileDrawer.AddAction(toolsSection, "A", "Agent Builder", async () => await OpenWorkstationRouteAsync("/Builder"), "MenuAgentBuilder");
        _pcAccessItem = _mobileDrawer.AddAction(toolsSection, "🖥", "PC Access", OpenPcAccessAsync, "PcAccess");
        _sqlManagerItem = _mobileDrawer.AddAction(toolsSection, "SQL", "SQL Manager", async () => await OpenWorkstationRouteAsync("/sql"), "MenuSqlManager");
        _administrativeToolsItem = _mobileDrawer.AddAction(toolsSection, "⚙", "Administrative Tools", async () => await Navigation.PushAsync(new MobileDiagnosticsPage(_client, showUsers: true)), "MenuAdministrativeTools");
        _speakItem = _mobileDrawer.AddAction(toolsSection, "🔊", "Read Latest Response", SpeakLastAsync, "SpeakResponse");

        VerticalStackLayout optionsSection = _mobileDrawer.AddSection("Options");
        _mobileDrawer.AddContent(optionsSection, new Border
        {
            Padding = 10,
            BackgroundColor = Color.FromArgb("#111A2B"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new VerticalStackLayout
            {
                Spacing = 9,
                Children = { modeRow, modelOptions, reasoningRow, _jackhammerToggleCard }
            }
        });
        _chickenChaserOverlayStatus = _mobileDrawer.AddStatus(optionsSection, "Floating Chicken Chaser: checking permission…", "ChickenChaserOverlayStatus");
        _chickenChaserOverlayPermissionItem = _mobileDrawer.AddAction(
            optionsSection,
            "◉",
            "Floating Chicken Chaser",
            ConfigureChickenChaserOverlayAsync,
            "ChickenChaserOverlayPermission");
        _dreamItem = _mobileDrawer.AddAction(optionsSection, "☾", "Dreaming", async () => await Navigation.PushAsync(new DreamManagementPage(_server, _client)), "DreamManagement");

        VerticalStackLayout helpSection = _mobileDrawer.AddSection("Help");
        _mobileDrawer.AddAction(helpSection, "!", "Errors / Diagnosis", async () => await Navigation.PushAsync(new MobileDiagnosticsPage(_client)), "MenuDiagnostics");
        _mobileDrawer.AddAction(helpSection, "?", "API Reference", async () => await OpenWorkstationRouteAsync("/api"), "MenuApiReference");
        _mobileDrawer.AddAction(helpSection, "ⓘ", "About / System Information", ShowAboutAsync, "MenuAbout");

        var menuButton = new Button
        {
            Text = "☰",
            FontSize = 25,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
            Padding = 0,
            AutomationId = "OpenMobileMenu"
        };
        AutomationProperties.SetHelpText(menuButton, "Open navigation menu");
        menuButton.Clicked += async (_, _) => await _mobileDrawer.OpenAsync();
        _compactContextLabel = new Label
        {
            Text = "General · Unsorted",
            FontSize = 10,
            TextColor = Color.FromArgb("#94A3B8"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        var compactTitle = new VerticalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "heirowLLM", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                _compactContextLabel
            }
        };
        var compactTopBar = new Grid
        {
            HeightRequest = 50,
            Padding = new Thickness(4, 1, 10, 1),
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            BackgroundColor = Color.FromArgb("#111827")
        };
        compactTopBar.Add(menuButton, 0); compactTopBar.Add(compactTitle, 1); compactTopBar.Add(_networkHealth, 2);
        _liveActivityText = new Label { Text = "Preparing compute…", TextColor = Color.FromArgb("#BFDBFE"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        _liveProgress = new ProgressBar { Progress = 0, ProgressColor = Color.FromArgb("#60A5FA"), BackgroundColor = Color.FromArgb("#26334D"), HeightRequest = 3 };
        _liveIndicator = new ActivityIndicator { IsRunning = false, Color = Color.FromArgb("#60A5FA"), WidthRequest = 20, HeightRequest = 20 };
        _stop = new Button { Text = "\u25a0", FontSize = 16, CornerRadius = 10, BackgroundColor = Color.FromArgb("#7F1D1D"), TextColor = Colors.White, WidthRequest = 42, HeightRequest = 36, Padding = 0 };
        AutomationProperties.SetName(_stop, "Stop response");
        AutomationProperties.SetHelpText(_stop, "Stop the running response and discard its queued follow-up");
        _stop.Clicked += async (_, _) => await StopAsync();
        var liveGrid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        liveGrid.Add(_liveIndicator, 0, 0); Grid.SetRowSpan(_liveIndicator, 2); liveGrid.Add(_liveActivityText, 1, 0); liveGrid.Add(_liveProgress, 1, 1);
        liveGrid.Add(_stop, 2, 0); Grid.SetRowSpan(_stop, 2);
        _liveActivityCard = new Border { IsVisible = false, Margin = new Thickness(10, 4), Padding = new Thickness(10, 7), BackgroundColor = Color.FromArgb("#101D36"), Stroke = Color.FromArgb("#1D4ED8"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = liveGrid };
        _contextApprovalTitle = new Label { Text = "heirow requests more context", TextColor = Colors.White, FontSize = 13, FontAttributes = FontAttributes.Bold };
        _contextApprovalSummary = new Label { TextColor = Color.FromArgb("#CBD5E1"), FontSize = 11, LineBreakMode = LineBreakMode.WordWrap };
        _contextAllowOnce = new Button { Text = "Allow once", CornerRadius = 10, BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, FontSize = 11, Padding = new Thickness(10, 6) };
        _contextAllowAlways = new Button { Text = "Always allow", CornerRadius = 10, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, FontSize = 11, Padding = new Thickness(10, 6) };
        _contextDeny = new Button { Text = "Deny", CornerRadius = 10, BackgroundColor = Color.FromArgb("#3F1D2A"), TextColor = Color.FromArgb("#FCA5A5"), FontSize = 11, Padding = new Thickness(10, 6) };
        _contextAllowOnce.Clicked += async (_, _) => await DecideContextApprovalAsync("allow_once");
        _contextAllowAlways.Clicked += async (_, _) => await DecideContextApprovalAsync("allow_always");
        _contextDeny.Clicked += async (_, _) => await DecideContextApprovalAsync("deny");
        _contextApprovalCard = new Border
        {
            IsVisible = false, Margin = new Thickness(10, 4), Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb("#172033"), Stroke = Color.FromArgb("#F59E0B"), StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new VerticalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    _contextApprovalTitle, _contextApprovalSummary,
                    new HorizontalStackLayout { Spacing = 7, Children = { _contextAllowOnce, _contextAllowAlways, _contextDeny } }
                }
            }
        };
        var composer = new Border { Margin = new Thickness(10, 6, 10, 10), Padding = new Thickness(8), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 18 }, Content = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, Children = { _attach, _voice.Column(1), _prompt.Column(2), _steer.Column(3), _send.Column(4) } } };
        _alignmentTop = new BoxView { HeightRequest = 3, VerticalOptions = LayoutOptions.Start, BackgroundColor = Color.FromArgb("#64748B") };
        _alignmentBottom = new BoxView { HeightRequest = 3, IsVisible = false };
        _alignmentDrawerScore = new Label { Text = "Neutral · 0", TextColor = Colors.White, FontSize = 15, FontAttributes = FontAttributes.Bold };
        _alignmentDrawerReason = new Label { Text = "Every Hero chooses a path.", TextColor = Color.FromArgb("#CBD5E1"), FontSize = 12 };
        _alignmentDrawerFeatures = new Label { Text = "All granted Guild privileges remain available.", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11 };
        _alignmentDrawerTraits = new Label { TextColor = Color.FromArgb("#CBD5E1"), FontSize = 11, LineHeight = 1.25 };
        _alignmentDrawerRecovery = new Label { Text = "You can only help others after you help yourself.", TextColor = Color.FromArgb("#64748B"), FontSize = 10 };
        _alignmentDrawerModel = new Label { Text = "Waiting for a completed Dream before Checks and Balances can run", TextColor = Color.FromArgb("#64748B"), FontSize = 9 };
        _alignmentDrawer = new Border
        {
            IsVisible = false, HeightRequest = 0, Padding = new Thickness(16, 12, 16, 9),
            BackgroundColor = Color.FromArgb("#101827"), Stroke = Color.FromArgb("#334155"), StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 0, 14, 14) },
            Content = new VerticalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    new Label { Text = "HERO ALIGNMENT", TextColor = Color.FromArgb("#7DD3FC"), FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1.5 },
                    _alignmentDrawerScore, _alignmentDrawerReason, _alignmentDrawerFeatures, _alignmentDrawerTraits,
                    _alignmentDrawerRecovery, _alignmentDrawerModel,
                    new Label { Text = "↑  Slide up to close", TextColor = Color.FromArgb("#64748B"), FontSize = 10, HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };
        var alignmentEdgeZone = new Grid { HeightRequest = 16, BackgroundColor = Colors.Transparent, Children = { _alignmentTop } };
        var openAlignmentSwipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Down, Threshold = 40 };
        openAlignmentSwipe.Swiped += (_, _) => SetAlignmentDrawerOpen(true);
        alignmentEdgeZone.GestureRecognizers.Add(openAlignmentSwipe);
        var closeAlignmentSwipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Up, Threshold = 36 };
        closeAlignmentSwipe.Swiped += (_, _) => SetAlignmentDrawerOpen(false);
        _alignmentDrawer.GestureRecognizers.Add(closeAlignmentSwipe);
        var bottomAlignmentSwipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Up, Threshold = 36 };
        bottomAlignmentSwipe.Swiped += (_, _) => SetAlignmentDrawerOpen(true);
        _alignmentBottom.GestureRecognizers.Add(bottomAlignmentSwipe);
        var ignoreChickenPermission = new Button
        {
            Text = "Ignore",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            HeightRequest = 34,
            BackgroundColor = Color.FromArgb("#334155"),
            TextColor = Colors.White,
            CornerRadius = 9
        };
        var turnOnChickenPermission = new Button
        {
            Text = "Turn On Permission",
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            Padding = new Thickness(11, 4),
            HeightRequest = 34,
            BackgroundColor = Color.FromArgb("#7C3AED"),
            TextColor = Colors.White,
            CornerRadius = 9
        };
        var chickenPermissionActions = new HorizontalStackLayout
        {
            Spacing = 7,
            HorizontalOptions = LayoutOptions.End,
            Children = { ignoreChickenPermission, turnOnChickenPermission }
        };
        _chickenChaserPermissionBanner = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12, 7),
            Margin = 0,
            BackgroundColor = Color.FromArgb("#24153D"),
            Stroke = Color.FromArgb("#8B5CF6"),
            StrokeThickness = 1,
            Content = new Grid
            {
                ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
                ColumnSpacing = 9,
                Children =
                {
                    new Label
                    {
                        Text = "Chicken Chaser needs permission",
                        TextColor = Colors.White,
                        FontSize = 12,
                        FontAttributes = FontAttributes.Bold,
                        VerticalTextAlignment = TextAlignment.Center,
                        LineBreakMode = LineBreakMode.WordWrap
                    }.Column(0),
                    chickenPermissionActions.Column(1)
                }
            }
        };
        ignoreChickenPermission.Clicked += (_, _) =>
        {
            Preferences.Default.Set(ChickenChaserPermissionBannerIgnoredKey, true);
            _chickenChaserPermissionBanner.IsVisible = false;
        };
        turnOnChickenPermission.Clicked += async (_, _) => await OpenChickenChaserPermissionAsync();
        AutomationProperties.SetName(ignoreChickenPermission, "Ignore Chicken Chaser permission notification");
        AutomationProperties.SetName(turnOnChickenPermission, "Turn On Permission");
        _contentRoot = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(16), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(3)
            },
            Children = { _chickenChaserPermissionBanner.Row(0), alignmentEdgeZone.Row(1), _alignmentDrawer.Row(2), compactTopBar.Row(3), _status.Row(4), _liveActivityCard.Row(5), _messageList.Row(6), _attachmentScroll.Row(7), _contextApprovalCard.Row(8), composer.Row(9), _alignmentBottom.Row(10) }
        };
        _chickenChaserMessages = new VerticalStackLayout
        {
            Spacing = 7,
            Children =
            {
                ChickenChaserMessage("How can I help?", false)
            }
        };
        _chickenChaserMessageScroll = new ScrollView
        {
            Content = _chickenChaserMessages,
            VerticalOptions = LayoutOptions.Fill,
            MaximumHeightRequest = 250
        };
        _chickenChaserPrompt = new Editor
        {
            Placeholder = "Ask Chicken Chaser…",
            AutoSize = EditorAutoSizeOption.TextChanges,
            MaximumHeightRequest = 110,
            TextColor = Colors.White,
            PlaceholderColor = Color.FromArgb("#64748B"),
            BackgroundColor = Color.FromArgb("#070B12")
        };
        _chickenChaserSend = new Button
        {
            Text = "↑",
            FontSize = 21,
            FontAttributes = FontAttributes.Bold,
            WidthRequest = 48,
            BackgroundColor = Color.FromArgb("#6D28D9"),
            TextColor = Colors.White,
            CornerRadius = 13
        };
        _chickenChaserSend.Clicked += async (_, _) => await SendChickenChaserAsync();
        AutomationProperties.SetName(_chickenChaserSend, "Send to Chicken Chaser");
        var chickenOpenCurrentSession = new Button
        {
            Text = "↗",
            FontSize = 20,
            WidthRequest = 42,
            HeightRequest = 38,
            Padding = 0,
            BackgroundColor = Color.FromArgb("#1F2937"),
            TextColor = Colors.White,
            CornerRadius = 11
        };
        AutomationProperties.SetName(chickenOpenCurrentSession, "Open in heirowLLM");
        AutomationProperties.SetHelpText(chickenOpenCurrentSession, "Close Chicken Chaser and open the current session in heirowLLM Mobile");
        chickenOpenCurrentSession.Clicked += (_, _) => OpenChickenChaserSessionInHeirowLlm();
        var chickenClose = new Button
        {
            Text = "×",
            FontSize = 20,
            WidthRequest = 42,
            HeightRequest = 38,
            Padding = 0,
            BackgroundColor = Colors.Transparent,
            TextColor = Colors.White
        };
        AutomationProperties.SetName(chickenClose, "Close Chicken Chaser");
        chickenClose.Clicked += (_, _) => SetChickenChaserOpen(false);
        var chickenHeader = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 6
        };
        chickenHeader.Add(new Label
        {
            Text = "Chicken Chaser",
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            FontSize = 15,
            VerticalTextAlignment = TextAlignment.Center
        }, 0);
        chickenHeader.Add(chickenOpenCurrentSession, 1);
        chickenHeader.Add(chickenClose, 2);
        var chickenCompose = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 7
        };
        chickenCompose.Add(_chickenChaserPrompt, 0);
        chickenCompose.Add(_chickenChaserSend, 1);
        _chickenChaserPanel = new Border
        {
            IsVisible = false,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            WidthRequest = 350,
            MaximumHeightRequest = 470,
            Margin = new Thickness(14, 18, 14, 88),
            Padding = new Thickness(12),
            BackgroundColor = Color.FromArgb("#E6080B10"),
            Stroke = Color.FromArgb("#A855F7"),
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = new Grid
            {
                RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
                RowSpacing = 8,
                Children = { chickenHeader.Row(0), _chickenChaserMessageScroll.Row(1), chickenCompose.Row(2) }
            }
        };
        _chickenChaserOverlayButton = CreateChickenChaserAvatarBubble(out _chickenChaserNotificationDot);
        _chickenChaserOverlayButton.AutomationId = "ChickenChaserOverlay";
        AutomationProperties.SetName(_chickenChaserOverlayButton, "Chicken Chaser");
        AutomationProperties.SetHelpText(_chickenChaserOverlayButton, "Open Chicken Chaser for the current mobile session");
        var chickenBubbleTap = new TapGestureRecognizer();
        chickenBubbleTap.Tapped += (_, _) => SetChickenChaserOpen(!_chickenChaserPanel.IsVisible);
        _chickenChaserOverlayButton.GestureRecognizers.Add(chickenBubbleTap);
        _alignmentLockScreen = new Border
        {
            IsVisible = false, Margin = new Thickness(22), Padding = new Thickness(24),
            BackgroundColor = Color.FromArgb("#130000"), Stroke = Color.FromArgb("#B91C1C"), StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 18 }, VerticalOptions = LayoutOptions.Center,
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Your Will Energy is low. Watch that.", TextColor = Color.FromArgb("#FCA5A5"), FontSize = 20, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = "The Guildmaster is disappointed in the path you chose. This account has been sealed.", TextColor = Colors.White, FontSize = 14, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = "Only the Hero can choose another road.", TextColor = Color.FromArgb("#94A3B8"), FontSize = 12, HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };
        Content = new Grid { Children = { _contentRoot, _alignmentLockScreen, _mobileDrawer } };
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
        _generation.AlignmentChanged -= OnAlignmentChanged;
        _generation.AlignmentChanged += OnAlignmentChanged;
        _generation.AuthenticationRequired -= OnAuthenticationRequired;
        _generation.AuthenticationRequired += OnAuthenticationRequired;
        AppVisibilityService.VisibilityChanged -= OnAppVisibilityChanged;
        AppVisibilityService.VisibilityChanged += OnAppVisibilityChanged;
        _chickenOverlayNavigation.PendingChanged -= OnChickenChaserOverlayPending;
        _chickenOverlayNavigation.PendingChanged += OnChickenChaserOverlayPending;
        await RefreshChickenChaserOverlayAsync();
        if (_chickenOverlayNavigation.TakePending()) SetChickenChaserOpen(true);
        try
        {
            MobileGenerationSnapshot activeGeneration = _generation.Current;
            bool ownsActiveStream = activeGeneration.IsGenerating &&
                activeGeneration.ServerKey.Equals(_server.LaunchKey, StringComparison.OrdinalIgnoreCase);
            if (!ownsActiveStream)
                await _client.ConnectAsync(_server);
            await RefreshMobileMenuPermissionsAsync();
            ApplyAlignment(await _client.GetAlignmentAsync());
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
            StartContextApprovalMonitor();
            ApplyGenerationSnapshot(_generation.Current);
        }
        catch (Exception ex) when (RequiresAuthentication(ex))
        {
            _status.Text = "Sign in to continue";
            await ShowAuthenticationAsync();
        }
        catch (Exception ex) { _status.Text = "Connection failed: " + ex.Message; }
    }

    private void UpdateAdministratorActions()
    {
        bool administrator = _client.IsAdministrator || _client.IsOwner;
        _pcAccessItem.IsVisible = administrator && _pcAccessAllowed;
        _sqlManagerItem.IsVisible = administrator && _sqlAdminAllowed;
        _administrativeToolsItem.IsVisible = administrator;
        _mobileDrawer.SetIdentity(string.IsNullOrWhiteSpace(_client.AuthenticatedUserName)
            ? _server.DisplayName
            : _server.DisplayName + " · " + _client.AuthenticatedUserName);
    }

    private async Task RefreshMobileMenuPermissionsAsync()
    {
        _sqlAdminAllowed = false;
        _pcAccessAllowed = false;
        if (_client.IsAdministrator || _client.IsOwner)
        {
            try
            {
                MobileMenuPermissionSnapshot permissions = await _client.GetMobileMenuPermissionsAsync();
                _sqlAdminAllowed = permissions.SqlAdmin;
                _pcAccessAllowed = permissions.PcAccess;
            }
            catch
            {
                // Keep privileged actions hidden until their permission state is confirmed.
            }
        }
        UpdateAdministratorActions();
    }

    private async Task OpenPcAccessAsync()
    {
        if ((!_client.IsAdministrator && !_client.IsOwner) || !_pcAccessAllowed)
        {
            UpdateAdministratorActions();
            return;
        }
        await Navigation.PushAsync(new PcAccessPage(_server, _client));
    }

    private Task OpenSessionsAsync() => Navigation.PushAsync(new SessionsPage(_server, _client, LoadSessionAsync, StartNewSessionAsync));

    private static Border ChickenChaserMessage(string text, bool user, bool error = false) => new()
    {
        Padding = new Thickness(9, 7),
        Margin = new Thickness(user ? 36 : 0, 2, user ? 0 : 36, 2),
        HorizontalOptions = user ? LayoutOptions.End : LayoutOptions.Start,
        BackgroundColor = Color.FromArgb(error ? "#15382D" : user ? "#38245D" : "#182231"),
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 11 },
        Content = new Label
        {
            Text = text,
            TextColor = error ? Color.FromArgb("#BBF7D0") : Colors.White,
            FontSize = 12,
            LineBreakMode = LineBreakMode.WordWrap
        }
    };

    private static Grid CreateChickenChaserAvatarBubble(out BoxView notificationDot)
    {
        var avatar = new Image
        {
            Aspect = Aspect.Fill,
            WidthRequest = 268,
            HeightRequest = 279
        };
        AbsoluteLayout.SetLayoutBounds(avatar, new Rect(-5, -3, 268, 279));
        var crop = new AbsoluteLayout
        {
            WidthRequest = 58,
            HeightRequest = 58,
            IsClippedToBounds = true,
            Children = { avatar }
        };
        var circle = new Border
        {
            WidthRequest = 58,
            HeightRequest = 58,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            BackgroundColor = Color.FromArgb("#241348"),
            Stroke = Color.FromArgb("#C084FC"),
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 29 },
            Content = crop
        };
        notificationDot = new BoxView
        {
            IsVisible = false,
            WidthRequest = 14,
            HeightRequest = 14,
            CornerRadius = 7,
            Color = Color.FromArgb("#EF4444"),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 1, 1, 0)
        };
        var bubble = new Grid
        {
            WidthRequest = 66,
            HeightRequest = 66,
            Margin = new Thickness(0, 0, 13, 15),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Children = { circle, notificationDot }
        };
        _ = LoadChickenChaserAvatarAsync(avatar);
        return bubble;
    }

    private static async Task LoadChickenChaserAvatarAsync(Image avatar)
    {
        try
        {
            await using Stream stream = await FileSystem.OpenAppPackageFileAsync("chicken_chaser_sprites.png");
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            byte[] bytes = memory.ToArray();
            await MainThread.InvokeOnMainThreadAsync(() =>
                avatar.Source = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false)));
        }
        catch { }
    }

    private void SetChickenChaserOpen(bool open)
    {
        _chickenChaserPanel.IsVisible = open;
        _chickenChaserOverlayButton.Opacity = open ? 1 : .72;
        if (open)
        {
            _chickenChaserNotificationDot.IsVisible = false;
            _chickenOverlay.SetNotificationDot(false);
            _chickenChaserPrompt.Focus();
        }
    }

    private async Task ConfigureChickenChaserOverlayAsync()
    {
        ChickenChaserOverlayStatus status = await _chickenOverlay.GetStatusAsync();
        if (!status.Supported)
        {
            await DisplayAlertAsync("Floating Chicken Chaser", "The over-app floating avatar is available on Android. Chicken Chaser remains available inside heirowLLM Mobile on this device.", "OK");
            return;
        }

        if (status.Enabled)
        {
            bool turnOff = await DisplayAlertAsync(
                "Floating Chicken Chaser is on",
                "The draggable avatar can appear above other apps. Android also keeps a quiet notification visible while the bubble is running.",
                "Turn off",
                "Keep on");
            if (turnOff) await _chickenOverlay.DisableAsync();
            await RefreshChickenChaserOverlayAsync();
            return;
        }

        if (!status.PermissionGranted)
        {
            bool openSettings = await DisplayAlertAsync(
                "Allow Chicken Chaser over other apps",
                "Android requires the Display over other apps permission for a true floating avatar. It lets Chicken Chaser draw only its draggable avatar above other apps; it does not read or control those apps. A quiet ongoing notification remains visible while the bubble is enabled. You can revoke the permission at any time in Android Settings.",
                "Open permission settings",
                "Not now");
            if (!openSettings) return;
            await _chickenOverlay.EnableAsync();
            if (!await _chickenOverlay.OpenPermissionSettingsAsync())
                await DisplayAlertAsync("Could not open Settings", "Open Android Settings, choose Apps > Special app access > Display over other apps, then allow heirowLLM Mobile.", "OK");
            return;
        }

        if (!await _chickenOverlay.EnableAsync())
            await DisplayAlertAsync("Could not start Chicken Chaser", "The permission is granted, but Android did not start the floating avatar. Try turning it off and on again.", "OK");
        await RefreshChickenChaserOverlayAsync();
    }

    private async Task OpenChickenChaserPermissionAsync()
    {
        await _chickenOverlay.EnableAsync();
        if (!await _chickenOverlay.OpenPermissionSettingsAsync())
            await DisplayAlertAsync("Could not open Settings", "Open Android Settings, choose Apps > Special app access > Display over other apps, then allow heirowLLM Mobile.", "OK");
    }

    private async Task RefreshChickenChaserOverlayAsync()
    {
        ChickenChaserOverlayStatus status = await _chickenOverlay.GetStatusAsync();
        if (status.Enabled) await _chickenOverlay.EnableAsync();
        _chickenChaserPermissionBanner.IsVisible = status.Supported &&
            !status.PermissionGranted &&
            !Preferences.Default.Get(ChickenChaserPermissionBannerIgnoredKey, false);
        _chickenChaserOverlayStatus.Text = !status.Supported
            ? "Floating Chicken Chaser: available on Android"
            : status.Enabled
                ? "Floating Chicken Chaser: on above other apps"
                : status.PermissionGranted
                    ? "Floating Chicken Chaser: ready to turn on"
                    : "Floating Chicken Chaser: permission required — tap below for a shortcut";
        _chickenChaserOverlayPermissionItem.Text = status.Enabled
            ? "◉   Turn off floating Chicken Chaser"
            : status.PermissionGranted
                ? "◉   Turn on floating Chicken Chaser"
                : "⚙   Open floating-avatar permission";
        _chickenChaserOverlayButton.IsVisible = !status.Enabled;
    }

    private void OnChickenChaserOverlayPending(object? sender, EventArgs e)
    {
        if (!_pageActive) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_pageActive && _chickenOverlayNavigation.TakePending()) SetChickenChaserOpen(true);
        });
    }

    private void OpenChickenChaserSessionInHeirowLlm()
    {
        SetChickenChaserOpen(false);
        if (_messages.Count > 0)
            _messageList.ScrollTo(_messages.Count - 1, position: ScrollToPosition.End, animate: true);
        _prompt.Focus();
    }

    private async Task SendChickenChaserAsync()
    {
        string value = (_chickenChaserPrompt.Text ?? "").Trim();
        if (value.Length == 0) return;
        _chickenChaserPrompt.Text = "";
        _chickenChaserMessages.Children.Add(ChickenChaserMessage(value, true));
        _chickenChaserSend.IsEnabled = false;
        try
        {
            string reply = await _client.AskChickenChaserAsync(value, _sessionId, _sessionTitle, _projectName, _messages.Count);
            _chickenChaserMessages.Children.Add(ChickenChaserMessage(reply, false));
            if (!AppVisibilityService.IsActive || !_chickenChaserPanel.IsVisible)
            {
                _chickenChaserNotificationDot.IsVisible = true;
                _chickenOverlay.SetNotificationDot(true);
            }
        }
        catch (Exception ex)
        {
            _chickenChaserMessages.Children.Add(ChickenChaserMessage(ex.Message, false, true));
        }
        finally
        {
            _chickenChaserSend.IsEnabled = true;
            await _chickenChaserMessageScroll.ScrollToAsync(0, double.MaxValue, true);
            _chickenChaserPrompt.Focus();
        }
    }

    private async Task OpenWorkstationRouteAsync(string route)
    {
        string endpoint = (_server.Endpoint ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("The Workstation endpoint is unavailable.");
        string normalizedRoute = "/" + (route ?? "").TrimStart('/');
        await Browser.Default.OpenAsync(new Uri(endpoint + normalizedRoute), BrowserLaunchMode.SystemPreferred);
    }

    private Task ShowAboutAsync() => DisplayAlertAsync(
        "heirowLLM Mobile",
        $"Workstation: {_server.DisplayName}\nEndpoint: {_server.Endpoint}\nUser: {(_client.AuthenticatedUserName.Length > 0 ? _client.AuthenticatedUserName : "Not signed in")}\nSession: {_sessionTitle}",
        "OK");

    private async Task RenameCurrentSessionAsync()
    {
        if (!_sessionKnownToServer)
        {
            await DisplayAlertAsync("Edit Session Title", "Send a message or add a file before naming this new session.", "OK");
            return;
        }
        string? title = await DisplayPromptAsync("Edit Session Title", "Session title", "Save", "Cancel", _sessionTitle, 160);
        title = (title ?? "").Trim();
        if (title.Length == 0 || title.Equals(_sessionTitle, StringComparison.Ordinal)) return;
        string previous = _sessionTitle;
        _sessionTitle = title;
        UpdateMobileMenuContext();
        try { await _client.RenameSessionAsync(_sessionId, title); }
        catch
        {
            _sessionTitle = previous;
            UpdateMobileMenuContext();
            throw;
        }
    }

    private async Task MoveCurrentSessionAsync()
    {
        if (!_sessionKnownToServer)
        {
            await DisplayAlertAsync("Move Session", "Send a message or add a file before moving this new session.", "OK");
            return;
        }
        ChatProjectInfo[] projects = (await _client.GetProjectsAsync(false)).Where(project => !project.Archived).ToArray();
        if (projects.Length == 0)
        {
            await DisplayAlertAsync("Move Session", "No available projects were found.", "OK");
            return;
        }
        string? selected = await DisplayActionSheetAsync("Move Session to Project", "Cancel", null, projects.Select(project => project.Name).ToArray());
        ChatProjectInfo? target = projects.FirstOrDefault(project => project.Name.Equals(selected, StringComparison.Ordinal));
        if (target is null || target.Id.Equals(_projectId, StringComparison.OrdinalIgnoreCase)) return;
        string previousId = _projectId;
        string previousName = _projectName;
        _projectId = target.Id;
        _projectName = target.Name;
        UpdateMobileMenuContext();
        try { await _client.MoveSessionAsync(_sessionId, target.Id); }
        catch
        {
            _projectId = previousId;
            _projectName = previousName;
            UpdateMobileMenuContext();
            throw;
        }
    }

    private async Task DeleteCurrentSessionAsync()
    {
        if (!_sessionKnownToServer)
        {
            if (await DisplayAlertAsync("Discard New Session", "Discard this unsaved conversation?", "Discard", "Cancel")) NewSession();
            return;
        }
        bool confirmed = await DisplayAlertAsync("Delete Session", $"Delete “{_sessionTitle}” permanently? This cannot be undone.", "Delete Session", "Cancel");
        if (!confirmed) return;
        await _client.DeleteSessionAsync(_sessionId);
        NewSession();
        _status.Text = "Session deleted";
    }

    private void UpdateMobileMenuContext()
    {
        string project = string.IsNullOrWhiteSpace(_projectName) ? (string.IsNullOrWhiteSpace(_projectId) ? "Unsorted" : _projectId) : _projectName;
        _drawerProjectStatus.Text = "Project: " + project;
        _compactContextLabel.Text = _mode + " · " + project + " · " + _sessionTitle;
    }

    protected override bool OnBackButtonPressed()
    {
        if (_mobileDrawer.IsOpen)
        {
            _ = _mobileDrawer.NavigateBackAsync();
            return true;
        }
        return base.OnBackButtonPressed();
    }

    protected override void OnDisappearing()
    {
        _pageActive = false;
        _generation.SnapshotChanged -= OnGenerationSnapshotChanged;
        _generation.AlignmentChanged -= OnAlignmentChanged;
        _generation.AuthenticationRequired -= OnAuthenticationRequired;
        AppVisibilityService.VisibilityChanged -= OnAppVisibilityChanged;
        _chickenOverlayNavigation.PendingChanged -= OnChickenChaserOverlayPending;
        base.OnDisappearing();
        _networkHealthCancellation?.Cancel();
        _networkHealthCancellation?.Dispose();
        _networkHealthCancellation = null;
        _contextApprovalCancellation?.Cancel();
        _contextApprovalCancellation?.Dispose();
        _contextApprovalCancellation = null;
        _pendingContextApproval = null;
        _contextApprovalCard.IsVisible = false;
    }

    private void StartNetworkHealthMonitor()
    {
        _networkHealthCancellation?.Cancel();
        _networkHealthCancellation?.Dispose();
        _networkHealthCancellation = new CancellationTokenSource();
        _ = MonitorNetworkHealthAsync(_networkHealthCancellation.Token);
    }

    private void StartContextApprovalMonitor()
    {
        _contextApprovalCancellation?.Cancel();
        _contextApprovalCancellation?.Dispose();
        _contextApprovalCancellation = new CancellationTokenSource();
        _ = MonitorContextApprovalsAsync(_contextApprovalCancellation.Token);
    }

    private async Task MonitorContextApprovalsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_generation.IsGenerating)
                {
                    IReadOnlyList<ContextApprovalRequest> approvals = await _client.GetContextApprovalsAsync(_sessionId, cancellationToken);
                    ContextApprovalRequest? next = approvals.FirstOrDefault();
                    await MainThread.InvokeOnMainThreadAsync(() => ShowContextApproval(next));
                }
                else if (_pendingContextApproval is not null)
                {
                    await MainThread.InvokeOnMainThreadAsync(() => ShowContextApproval(null));
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private void ShowContextApproval(ContextApprovalRequest? request)
    {
        _pendingContextApproval = request;
        _contextApprovalCard.IsVisible = request is not null;
        if (request is null) return;
        _contextApprovalTitle.Text = request.Capability switch
        {
            "fileAccess" => "heirow wants to inspect files",
            "companionObservation" => "heirow wants to observe the desktop",
            "runningApplications" => "heirow wants to inspect running applications",
            "windowsServices" => "heirow wants to inspect Windows services",
            "eventViewer" => "heirow wants to read Event Viewer",
            _ => "heirow requests more context"
        };
        _contextApprovalSummary.Text = (string.IsNullOrWhiteSpace(request.QuerySummary) ? "Read additional Workstation context" : request.QuerySummary) + "\nAllow once applies only to this tool call.";
        _contextAllowAlways.IsVisible = request.CanAlwaysAllow;
    }

    private async Task DecideContextApprovalAsync(string action)
    {
        ContextApprovalRequest? request = _pendingContextApproval;
        if (request is null) return;
        _contextAllowOnce.IsEnabled = _contextAllowAlways.IsEnabled = _contextDeny.IsEnabled = false;
        try
        {
            await _client.DecideContextApprovalAsync(request.Id, _sessionId, action);
            ShowContextApproval(null);
        }
        catch (Exception ex)
        {
            _status.Text = "Context approval failed: " + ex.Message;
        }
        finally
        {
            _contextAllowOnce.IsEnabled = _contextAllowAlways.IsEnabled = _contextDeny.IsEnabled = true;
        }
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
        _generalMode.BackgroundColor = !advanced && !planning ? Color.FromArgb("#2563EB") : Color.FromArgb("#1F2937");
        _advancedMode.BackgroundColor = advanced ? Color.FromArgb("#7C3AED") : Color.FromArgb("#1F2937");
        _planMode.BackgroundColor = planning ? Color.FromArgb("#7C3AED") : Color.FromArgb("#1F2937");
        _services.IsVisible = advanced;
        if (advanced && _services.SelectedIndex < 0) _services.SelectedIndex = 0;
        if (advanced && !Preferences.Default.ContainsKey(JackhammerPreferenceKey))
            SetJackhammerEnabled(true, persist: false);
        _prompt.Placeholder = planning ? "Describe what you want planned..." : advanced ? "Ask heirowLLM to work, use tools, or generate media..." : "Chat with heirowLLM...";
        _status.Text = planning
            ? "Plan Mode: read-only planning; no changes will be made"
            : advanced
                ? "Advanced Mode (Work): service controls and tool activity enabled"
                : "General Mode (chat): streamlined conversation";
        ApplyModelFilter();
        UpdateMobileMenuContext();
    }

    private void ApplyModelFilter()
    {
        var previous = _models.SelectedItem as ModelInfo;
        string selectedService = _services.SelectedItem as string ?? "agent";
        IEnumerable<ModelInfo> candidates;
        if (_mode is MobileChatMode.General or MobileChatMode.Plan)
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

    private string ModePreferenceKey => "heirowllm.mobile.mode." + _server.LaunchKey;
    private string ReasoningPreferenceKey => "heirowllm.mobile.reasoning." + _server.LaunchKey;
    private const string JackhammerPreferenceKey = "heirowllm.mobile.jackhammer.enabled";
    private const string JackhammerCustomBudgetPreferenceKey = "heirowllm.mobile.jackhammer.customBudget";
    private const string ChickenChaserPermissionBannerIgnoredKey = "heirowllm.mobile.chicken.permissionBannerIgnored";
    private static readonly string[] ReasoningLevels = ["Minimal", "Low", "Medium", "High", "Auto", "Ultra"];
    private static readonly int[] JackhammerPresetBudgets = [2, 5, 10, 20, 40, 100];
    private string EffectiveReasoningLevel => ReasoningLevels[(int)Math.Clamp(Math.Round(_reasoningSlider.Value), 0, 5)].ToLowerInvariant();
    private int EffectiveJackhammerTurnBudget
    {
        get
        {
            if (int.TryParse(_jackhammerCustomBudget.Text, out int custom) && custom > 0)
                return Math.Clamp(custom, 1, 200);
            return JackhammerPresetBudgets[(int)Math.Clamp(Math.Round(_reasoningSlider.Value), 0, 5)];
        }
    }
    private void UpdateReasoningUi() => _reasoningLabel.Text = "Reasoning: " + EffectiveReasoningLevel + $" · {EffectiveJackhammerTurnBudget} work turns" + (_sessionReasoningInherit.IsToggled ? " (global)" : " (session)");

    private void SaveJackhammerCustomBudget()
    {
        if (int.TryParse(_jackhammerCustomBudget.Text, out int value) && value > 0)
        {
            value = Math.Clamp(value, 1, 200);
            _jackhammerCustomBudget.Text = value.ToString();
            Preferences.Default.Set(JackhammerCustomBudgetPreferenceKey, value);
        }
        else
        {
            _jackhammerCustomBudget.Text = "";
            Preferences.Default.Remove(JackhammerCustomBudgetPreferenceKey);
        }
        UpdateReasoningUi();
    }

    private void SetJackhammerEnabled(bool enabled, bool persist)
    {
        if (_jackhammerToggle.IsToggled != enabled)
            _jackhammerToggle.IsToggled = enabled;
        if (persist)
            Preferences.Default.Set(JackhammerPreferenceKey, enabled);
        UpdateJackhammerToggleUi();
        if (enabled && _mode != MobileChatMode.Plan && _mode != MobileChatMode.Advanced)
            SetMode(MobileChatMode.Advanced);
        if (enabled && _mode != MobileChatMode.Plan)
            _services.SelectedIndex = 0;
    }

    private void UpdateJackhammerToggleUi()
    {
        bool enabled = _jackhammerToggle.IsToggled;
        _jackhammerToggleLabel.Text = "heirowForge " + (enabled ? "On" : "Off");
        _jackhammerToggleCard.BackgroundColor = enabled ? Color.FromArgb("#1D4ED8") : Color.FromArgb("#991B1B");
        _jackhammerToggleCard.Stroke = enabled ? Color.FromArgb("#60A5FA") : Color.FromArgb("#EF4444");
        AutomationProperties.SetHelpText(_jackhammerToggleCard, enabled
            ? "heirowForge autonomous work is on"
            : "heirowForge autonomous work is off");
    }

    private async Task SendOrQueueAsync()
    {
        if (_generation.IsGenerating)
        {
            await QueueFollowUpAsync();
            return;
        }
        await SendAsync();
    }

    private async Task QueueFollowUpAsync()
    {
        string text = _prompt.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
        if (_pendingFollowUp is not null)
        {
            await DisplayAlertAsync("Follow-up already queued", "Wait for the queued prompt to start, or use the steer symbol to redirect the response that is running.", "OK");
            return;
        }
        if (_attachments.Any(attachment => attachment.IsUploading || attachment.NeedsAttention))
        {
            await DisplayAlertAsync("Attachments not ready", "Wait for uploads to finish. Failed uploads must be retried or removed.", "OK");
            return;
        }

        _pendingFollowUp = new PendingFollowUp(text, _attachments.ToArray());
        _prompt.Text = "";
        _attachments.Clear();
        _attachmentStrip.Clear();
        _attachmentScroll.IsVisible = false;
        _status.Text = "Next prompt queued — use ↪ to steer the current response instead";
        UpdateComposerActions();
    }

    private async Task StartQueuedFollowUpAsync()
    {
        if (_pendingFollowUpStarting || _pendingFollowUp is null || _generation.IsGenerating) return;
        _pendingFollowUpStarting = true;
        PendingFollowUp followUp = _pendingFollowUp;
        _pendingFollowUp = null;
        try
        {
            _prompt.Text = followUp.Text;
            _attachments.AddRange(followUp.Attachments);
            RestoreAttachmentCards();
            await SendAsync();
        }
        finally
        {
            _pendingFollowUpStarting = false;
        }
    }

    private async Task SendAsync()
    {
        string text = _prompt.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text) && _attachments.Count == 0) return;
        if (_attachments.Any(attachment => attachment.IsUploading || attachment.NeedsAttention))
        {
            await DisplayAlertAsync("Attachments not ready", "Wait for uploads to finish. Failed uploads must be retried or removed.", "OK");
            return;
        }
        if (_models.SelectedItem is not ModelInfo model) { await DisplayAlertAsync("No model", "This Workstation did not report an available chat model.", "OK"); return; }
        string service = _mode == MobileChatMode.Plan
            ? "chat"
            : _jackhammerToggle.IsToggled
                ? "agent"
                : _mode == MobileChatMode.General
                    ? "chat"
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
        _stickToLatest = true;
        _attachments.Clear();
        _attachmentStrip.Clear();
        _attachmentScroll.IsVisible = false;
        _send.Text = "↑"; _status.Text = "Generating…";
        SetLiveActivity(true, AlignmentLoreForActivity("Warming up model"), 0);
        if (service.Equals("agent", StringComparison.OrdinalIgnoreCase) && !model.SupportsTools)
        {
            _messages.Remove(assistant);
            _messages.Remove(user);
            _attachments.AddRange(attachments);
            RestoreAttachmentCards();
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
            _server, _client, _sessionId, _projectId, model.Id, service,
            _mode == MobileChatMode.Plan ? "plan" : _mode == MobileChatMode.Advanced ? service : "chat", EffectiveReasoningLevel,
            _sessionReasoningInherit.IsToggled ? "inherit" : EffectiveReasoningLevel,
            model.SupportsTools,
            _jackhammerToggle.IsToggled, EffectiveJackhammerTurnBudget,
            requestMessages, attachments, priorServerMessageCount, user.Content));
        if (!started)
        {
            _messages.Remove(assistant);
            _messages.Remove(user);
            _attachments.AddRange(attachments);
            RestoreAttachmentCards();
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

    private Task StopAsync()
    {
        _pendingFollowUp = null;
        _status.Text = "Stopping response…";
        return _generation.StopAsync();
    }

    private async Task SteerAsync()
    {
        string direction = _prompt.Text?.Trim() ?? "";
        if (direction.Length == 0) return;
        if (_attachments.Count > 0)
        {
            await DisplayAlertAsync("Text steering only", "Remove attachments before steering the active response.", "OK");
            return;
        }
        _steer.IsEnabled = false;
        try
        {
            await _generation.SteerAsync(direction);
            _messages.Add(new ChatMessage { Role = "user", Content = direction, IsLocalOnly = false });
            _prompt.Text = "";
            _status.Text = "Steering accepted — applying at the next safe response break";
        }
        catch (Exception ex)
        {
            _status.Text = "Steering was not accepted";
            await DisplayAlertAsync("Could not steer response", ex.Message, "OK");
        }
        finally
        {
            UpdateComposerActions();
        }
    }

    private void UpdateComposerActions()
    {
        bool canSteerDraft = _generation.CanSteer && !string.IsNullOrWhiteSpace(_prompt.Text) && _attachments.Count == 0;
        _steer.IsVisible = canSteerDraft;
        _steer.IsEnabled = canSteerDraft;
        _send.IsEnabled = !_pendingFollowUpStarting;
    }

    private void OnAuthenticationRequired(object? sender, EventArgs e)
    {
        if (!_pageActive) return;
        MainThread.BeginInvokeOnMainThread(async () => await ShowAuthenticationAsync());
    }

    private async Task ShowAuthenticationAsync()
    {
        if (_authenticationPageOpen || !_pageActive) return;
        _authenticationPageOpen = true;
        var authPage = new WorkstationAuthPage(_credentials, _store, async _ =>
        {
            await _client.ConnectAsync(_server);
            bool resumed = await _generation.RetryPendingAsync();
            if (!resumed) _status.Text = "Signed in. Your session is ready.";
            await Navigation.PopAsync();
        }, _server.Endpoint);
        authPage.Disappearing += (_, _) => _authenticationPageOpen = false;
        await Navigation.PushAsync(authPage);
    }

    private static bool RequiresAuthentication(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return true;
        if (exception is HttpRequestException requestException &&
            requestException.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return true;
        string message = exception.Message ?? "";
        return message.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private void OnGenerationSnapshotChanged(object? sender, MobileGenerationSnapshot snapshot)
    {
        if (!_pageActive || !AppVisibilityService.IsActive) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_pageActive && AppVisibilityService.IsActive) ApplyGenerationSnapshot(snapshot);
        });
    }

    private void OnAlignmentChanged(object? sender, MobileAlignmentSnapshot alignment)
    {
        if (!_pageActive) return;
        MainThread.BeginInvokeOnMainThread(() => ApplyAlignment(alignment));
    }

    private void ApplyAlignment(MobileAlignmentSnapshot alignment)
    {
        _alignment = alignment ?? new MobileAlignmentSnapshot();
        bool negative = _alignment.Score < 0;
        bool positive = _alignment.Score > 0;
        _alignmentTop.IsVisible = !negative;
        _alignmentBottom.IsVisible = negative;
        _alignmentTop.Background = positive
            ? AlignmentGradient(Colors.Red, Colors.Lime, Colors.DeepSkyBlue, Colors.Magenta)
            : new SolidColorBrush(Color.FromArgb("#64748B"));
        _alignmentBottom.Background = AlignmentGradient(Colors.Black, Color.FromArgb("#7F1D1D"), Colors.Red, Colors.Black);
        _alignmentDrawer.BackgroundColor = negative ? Color.FromArgb("#160003") : Color.FromArgb("#101827");
        _alignmentDrawer.Stroke = negative ? Color.FromArgb("#991B1B") : Color.FromArgb("#334155");
        _alignmentDrawerScore.Text = $"{_alignment.Tier} · {_alignment.Score:+0;-0;0}";
        _alignmentDrawerReason.Text = _alignment.LastReason;
        _alignmentDrawerFeatures.Text = _alignment.DisabledFeatures is { Length: > 0 }
            ? "Withdrawn Guild privileges: " + string.Join(", ", _alignment.DisabledFeatures)
            : "All granted Guild privileges remain available.";
        _alignmentDrawerTraits.Text = BuildMobileAlignmentTraitText(_alignment.CharacterTraits);
        _alignmentDrawerRecovery.Text = _alignment.RecoveryGuidance;
        _alignmentDrawerModel.Text = string.Equals(_alignment.ChecksAndBalancesStatus, "retry-pending", StringComparison.OrdinalIgnoreCase)
            ? "Checks and Balances retry " + _alignment.ChecksAndBalancesRetryCount + " pending" + (string.IsNullOrWhiteSpace(_alignment.ChecksAndBalancesAttemptedModel) ? "" : " on " + _alignment.ChecksAndBalancesAttemptedModel) + (string.IsNullOrWhiteSpace(_alignment.ChecksAndBalancesError) ? "" : " · " + _alignment.ChecksAndBalancesError)
            : !string.IsNullOrWhiteSpace(_alignment.AssessmentModel)
            ? "Checks and Balances completed by " + _alignment.AssessmentModel
            : string.Equals(_alignment.ChecksAndBalancesStatus, "running", StringComparison.OrdinalIgnoreCase)
                ? "Checks and Balances is reading the completed Dream"
                : string.Equals(_alignment.ChecksAndBalancesStatus, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "Checks and Balances will retry after the next completed Dream"
                    : "Waiting for a completed Dream before Checks and Balances can run";
        BackgroundColor = negative ? Color.FromArgb("#090000") : Color.FromArgb("#0B1020");

        HashSet<string> disabled = new(_alignment.DisabledFeatures ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _dreamAvailable = _alignment.DreamsEnabled && !disabled.Contains("dream");
        _dreamItem.IsVisible = _dreamAvailable;
        _advancedMode.IsVisible = !disabled.Contains("agent");
        _attach.IsVisible = !disabled.Contains("uploads") && !disabled.Contains("filesystem");
        _voice.IsVisible = !disabled.Contains("media");

        string[] services = ["agent", "companion", "image_generation", "audio_generation", "video_generation"];
        _services.ItemsSource = services.Where(service =>
            !(disabled.Contains("agent") && service.Equals("agent", StringComparison.OrdinalIgnoreCase)) &&
            !(disabled.Contains("media") && service.EndsWith("_generation", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (_services.SelectedIndex < 0 && ((IEnumerable<string>)_services.ItemsSource).Any()) _services.SelectedIndex = 0;

        _generalMode.TextColor = Colors.White;
        _advancedMode.TextColor = Colors.White;
        _attach.TextColor = Colors.White;
        Color[] accents = [Color.FromArgb("#FF5A5F"), Color.FromArgb("#35E87B"), Color.FromArgb("#42A5FF")];
        for (int index = 0; index < Math.Min(3, _alignment.HighlightedFeatures?.Length ?? 0); index++)
        {
            string feature = _alignment.HighlightedFeatures![index];
            if (feature.Equals("agent", StringComparison.OrdinalIgnoreCase)) _advancedMode.TextColor = accents[index];
            else if (feature is "uploads" or "filesystem") _attach.TextColor = accents[index];
            else if (feature.Equals("chat", StringComparison.OrdinalIgnoreCase)) _generalMode.TextColor = accents[index];
        }

        _alignmentLockScreen.IsVisible = _alignment.Locked;
        _contentRoot.IsEnabled = !_alignment.Locked;
        _contentRoot.Opacity = _alignment.Locked ? 0.12 : 1;
        AutomationProperties.SetHelpText(_alignmentTop,
            $"Alignment {_alignment.Tier}, score {_alignment.Score}. Slide down from the top edge for details.");
    }

    private static LinearGradientBrush AlignmentGradient(params Color[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int index = 0; index < colors.Length; index++)
            brush.GradientStops.Add(new GradientStop(colors[index], colors.Length == 1 ? 0 : (float)index / (colors.Length - 1)));
        return brush;
    }

    private void SetAlignmentDrawerOpen(bool open)
    {
        if (_alignment.Locked || _alignmentDrawerOpen == open) return;
        _alignmentDrawerOpen = open;
        this.AbortAnimation("AlignmentDrawer");
        if (open)
        {
            _alignmentDrawer.IsVisible = true;
            _alignmentDrawer.HeightRequest = 0;
            this.Animate("AlignmentDrawer", value => _alignmentDrawer.HeightRequest = value,
                0, 356, 16, 280, Easing.CubicOut);
            return;
        }

        double start = Math.Max(0, _alignmentDrawer.HeightRequest);
        this.Animate("AlignmentDrawer", value => _alignmentDrawer.HeightRequest = value,
            start, 0, 16, 220, Easing.CubicIn, (value, cancelled) =>
            {
                if (cancelled || _alignmentDrawerOpen) return;
                _alignmentDrawer.HeightRequest = 0;
                _alignmentDrawer.IsVisible = false;
            });
    }

    private static string BuildMobileAlignmentTraitText(IReadOnlyDictionary<string, int>? traits)
    {
        string[] names = ["Nobility", "Humility", "Compassion", "Courage", "Honesty", "Mercy", "Generosity", "Discipline",
            "Responsibility", "Self-Respect", "Greed", "Cruelty", "Pride", "Deception", "Coercion", "Self-Sabotage"];
        HashSet<string> vices = new(["Greed", "Cruelty", "Pride", "Deception", "Coercion", "Self-Sabotage"], StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> readings = names.Select(name =>
        {
            int fallback = vices.Contains(name) ? 1 : 5;
            int value = traits != null && traits.TryGetValue(name, out int reported) ? Math.Clamp(reported, 1, 10) : fallback;
            return $"{name}: {value}/10";
        });
        return string.Join("\n", readings.Chunk(2).Select(pair => string.Join("     ", pair)));
    }

    private string AlignmentLoreForActivity(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) phase = "Preparing";
        if (phase.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("stopped", StringComparison.OrdinalIgnoreCase)) return phase;
        if (_alignment.Score < 0)
        {
            string[] hardMode = ["Hard Mode: The path darkens by your choices.", "Only the Hero can choose another road.", "Master of No One."];
            return hardMode[_loreIndex++ % hardMode.Length];
        }
        if (_alignment.Score > 0)
        {
            string[] good = ["Care for the Hero first; then help Albion.", "The good path is easy to walk, though the first step may feel hardest.", "heirow of few, master of none — choosing the brighter road."];
            return good[_loreIndex++ % good.Length];
        }
        if (phase.Contains("model", StringComparison.OrdinalIgnoreCase) || phase.Contains("warm", StringComparison.OrdinalIgnoreCase)) return "Choosing Between Good and Evil.";
        if (phase.Contains("reason", StringComparison.OrdinalIgnoreCase) || phase.Contains("think", StringComparison.OrdinalIgnoreCase)) return "Every Choice Leaves a Scar.";
        if (phase.Contains("response", StringComparison.OrdinalIgnoreCase) || phase.Contains("receiv", StringComparison.OrdinalIgnoreCase)) return "The Resistance Has Your Answer.";
        string[] neutral = ["You Don't Know heirow... Yet.", "Preventing Human Sacrifice.", "heirow of few, master of none", "Hero, Your Will Power Is Low."];
        return neutral[_loreIndex++ % neutral.Length];
    }

    private void OnAppVisibilityChanged(bool active)
    {
        if (!active || !_pageActive) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_pageActive || !AppVisibilityService.IsActive) return;
            ApplyGenerationSnapshot(_generation.Current);
            _ = RefreshChickenChaserOverlayAsync();
        });
    }

    private void ApplyGenerationSnapshot(MobileGenerationSnapshot snapshot)
    {
        if (!_pageActive || !AppVisibilityService.IsActive || string.IsNullOrWhiteSpace(snapshot.GenerationId)) return;
        if (!snapshot.ServerKey.Equals(_server.LaunchKey, StringComparison.OrdinalIgnoreCase) ||
            !snapshot.SessionId.Equals(_sessionId, StringComparison.OrdinalIgnoreCase)) return;

        bool generationJustFinished = _generationWasRunning && !snapshot.IsGenerating;
        _generationWasRunning = snapshot.IsGenerating;
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

        bool reasoningJustStarted = string.IsNullOrWhiteSpace(assistant.Reasoning) && !string.IsNullOrWhiteSpace(snapshot.Reasoning);
        assistant.Content = snapshot.Content;
        assistant.Reasoning = snapshot.Reasoning;
        assistant.Status = snapshot.Status;
        assistant.Telemetry = snapshot.Telemetry;
        assistant.RouteSummary = snapshot.RouteSummary;
        assistant.IsGenerating = snapshot.IsGenerating;
        assistant.IsIncomplete = snapshot.IsStopped;
        if (reasoningJustStarted)
            assistant.IsReasoningExpanded = true;
        else if (!snapshot.IsGenerating && !string.IsNullOrWhiteSpace(snapshot.Content))
            assistant.IsReasoningExpanded = false;
        assistant.Tools.Clear();
        foreach (ToolActivity tool in snapshot.Tools)
            assistant.Tools.Add(new ToolActivity { Name = tool.Name, Status = tool.Status, Detail = tool.Detail });
        ApplyJackhammerWorkSummary(assistant, snapshot.JackhammerSteps, snapshot.JackhammerGoal, snapshot.JackhammerStatus, snapshot.JackhammerProgressPercent, snapshot.JackhammerEnabled, snapshot.IsGenerating);

        _send.Text = "↑";
        UpdateComposerActions();
        _prompt.Placeholder = snapshot.IsGenerating ? "Type the next prompt, or use ↪ to steer…" : (_mode == MobileChatMode.Plan ? "Describe what you want planned..." : _mode == MobileChatMode.Advanced ? "Ask heirowLLM to work, use tools, or generate media..." : "Chat with heirowLLM...");
        _status.Text = snapshot.HasError ? snapshot.Status : snapshot.IsStopped ? "Generation stopped" : snapshot.IsGenerating ? (snapshot.Status.Length > 0 ? snapshot.Status : "Generating…") : "Ready";
        SetLiveActivity(snapshot.IsGenerating, AlignmentLoreForActivity(snapshot.Status), snapshot.Progress);
        if (_stickToLatest && DateTimeOffset.UtcNow - _lastAutoScroll > TimeSpan.FromMilliseconds(300))
        {
            _lastAutoScroll = DateTimeOffset.UtcNow;
            try { _messageList.ScrollTo(assistant, position: ScrollToPosition.End, animate: false); }
            catch { }
        }
        if (generationJustFinished && _pendingFollowUp is not null && !snapshot.IsStopped)
            _ = StartQueuedFollowUpAsync();
    }

    private void NewSession()
    {
        if (_generation.IsGenerating) _ = StopAsync();
        _sessionId = Guid.NewGuid().ToString("N");
        _sessionTitle = "New chat";
        _sessionKnownToServer = false;
        _attachments.Clear();
        _attachmentStrip.Clear();
        _attachmentScroll.IsVisible = false;
        _messages.Clear();
        _messages.Add(CreateWelcomeMessage());
        _sessionReasoningInherit.IsToggled = true;
        _reasoningSlider.Value = Preferences.Default.Get(ReasoningPreferenceKey, 4d);
        UpdateReasoningUi();
        _status.Text = "New conversation";
        _recentSessions.Remember(_server.LaunchKey, _sessionId);
        UpdateMobileMenuContext();
    }

    private Task StartNewSessionAsync(string projectId)
    {
        _projectId = string.IsNullOrWhiteSpace(projectId) ? "unsorted" : projectId;
        _projectName = _projectId.Equals("unsorted", StringComparison.OrdinalIgnoreCase) ? "Unsorted" : _projectId;
        NewSession();
        _status.Text = "New conversation in " + _projectId;
        return Task.CompletedTask;
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
        Role = "heirowLLM Mobile",
        IsLocalOnly = true,
        Content = """
            # Welcome to heirowLLM Mobile 👋

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
        _sessionKnownToServer = true;
        _sessionId = detail.Id;
        _projectId = string.IsNullOrWhiteSpace(detail.ProjectId) ? "unsorted" : detail.ProjectId;
        _projectName = string.IsNullOrWhiteSpace(detail.ProjectName)
            ? (_projectId.Equals("unsorted", StringComparison.OrdinalIgnoreCase) ? "Unsorted" : _projectId)
            : detail.ProjectName;
        _sessionTitle = detail.Title;
        _recentSessions.Remember(_server.LaunchKey, _sessionId);
        string savedReasoning = string.IsNullOrWhiteSpace(detail.ReasoningLevel) ? "inherit" : detail.ReasoningLevel.ToLowerInvariant();
        SetMode(detail.InteractionMode.Equals("plan", StringComparison.OrdinalIgnoreCase)
            ? MobileChatMode.Plan
            : detail.InteractionMode is "agent" or "companion"
                ? MobileChatMode.Advanced
                : MobileChatMode.General);
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
        UpdateMobileMenuContext();
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
            var attachment = new AttachmentInfo { Name = file.FileName, ContentType = file.ContentType ?? "application/octet-stream", Data = memory.ToArray() };
            _attachments.Add(attachment);
            AddAttachmentCard(attachment);
            if (!_sessionKnownToServer)
            {
                await _client.EnsureSessionAsync(_sessionId, _projectId, (_models.SelectedItem as ModelInfo)?.Id ?? "");
                _sessionKnownToServer = true;
            }
            await UploadAttachmentAsync(attachment);
        }
        catch (Exception ex) { await DisplayAlertAsync("Attachment", ex.Message, "OK"); }
    }

    private async Task UploadAttachmentAsync(AttachmentInfo attachment)
    {
        try
        {
            await _client.UploadProjectFileAsync(_sessionId, attachment, "\\", false, new Progress<double>(_ => RefreshAttachmentState()));
            _status.Text = $"{attachment.Name} saved to Project Files";
        }
        catch (Exception ex)
        {
            attachment.UploadState = "failed";
            attachment.UploadError = ex.Message;
            _status.Text = "Upload failed: " + ex.Message;
        }
        RefreshAttachmentState();
    }

    private void AddAttachmentCard(AttachmentInfo attachment)
    {
        View preview = attachment.IsImage
            ? new Image { Source = ImageSource.FromStream(() => new MemoryStream(attachment.Data)), Aspect = Aspect.AspectFill, WidthRequest = 96, HeightRequest = 82 }
            : new Label { Text = "📄\n" + attachment.Name, TextColor = Colors.White, WidthRequest = 124, HeightRequest = 82, LineBreakMode = LineBreakMode.TailTruncation };
        var scrim = new BoxView { BackgroundColor = Color.FromArgb("#880B1020"), InputTransparent = true };
        var progress = new ProgressBar { ProgressColor = Color.FromArgb("#60A5FA"), BackgroundColor = Color.FromArgb("#334155"), VerticalOptions = LayoutOptions.End, Margin = 5 };
        progress.SetBinding(ProgressBar.ProgressProperty, nameof(AttachmentInfo.UploadProgress));
        var percent = new Label { TextColor = Colors.White, FontAttributes = FontAttributes.Bold, FontSize = 11, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
        percent.SetBinding(Label.TextProperty, nameof(AttachmentInfo.UploadPercent));
        var retry = new Button { Text = "Retry", FontSize = 10, Padding = new Thickness(5, 1), BackgroundColor = Color.FromArgb("#991B1B"), TextColor = Colors.White, IsVisible = false, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start };
        var remove = new Button { Text = "×", FontSize = 14, Padding = 0, WidthRequest = 28, HeightRequest = 28, BackgroundColor = Color.FromArgb("#99000000"), TextColor = Colors.White, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start };
        var grid = new Grid { BindingContext = attachment, WidthRequest = attachment.IsImage ? 96 : 124, HeightRequest = 82, Children = { preview, scrim, percent, progress, retry, remove } };
        void UpdateCard()
        {
            ApplyAttachmentBlur(preview, attachment.IsUploading);
            scrim.IsVisible = attachment.IsUploading || attachment.NeedsAttention;
            progress.IsVisible = attachment.IsUploading;
            percent.IsVisible = attachment.IsUploading;
            retry.IsVisible = attachment.NeedsAttention;
        }
        attachment.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateCard);
        preview.HandlerChanged += (_, _) => UpdateCard();
        retry.Clicked += async (_, _) => await UploadAttachmentAsync(attachment);
        remove.Clicked += (_, _) =>
        {
            _attachments.Remove(attachment);
            _attachmentStrip.Remove(grid);
            RefreshAttachmentState();
        };
        UpdateCard();
        _attachmentStrip.Add(grid);
        _attachmentScroll.IsVisible = true;
        RefreshAttachmentState();
    }

    private static void ApplyAttachmentBlur(View preview, bool uploading)
    {
        preview.Opacity = uploading ? .58 : 1;
#if ANDROID
        if (preview.Handler?.PlatformView is Android.Views.View nativeView && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            nativeView.SetRenderEffect(uploading
                ? Android.Graphics.RenderEffect.CreateBlurEffect(18f, 18f, Android.Graphics.Shader.TileMode.Clamp!)
                : null);
        }
#elif IOS
        if (preview.Handler?.PlatformView is UIKit.UIView nativeView)
        {
            const int blurTag = 0x4A41434B;
            UIKit.UIView? existing = nativeView.ViewWithTag(blurTag);
            if (uploading && existing is null)
            {
                var blur = new UIKit.UIVisualEffectView(UIKit.UIBlurEffect.FromStyle(UIKit.UIBlurEffectStyle.SystemMaterialDark))
                {
                    Tag = blurTag,
                    Frame = nativeView.Bounds,
                    AutoresizingMask = UIKit.UIViewAutoresizing.FlexibleWidth | UIKit.UIViewAutoresizing.FlexibleHeight,
                    UserInteractionEnabled = false
                };
                nativeView.AddSubview(blur);
            }
            else if (!uploading)
            {
                existing?.RemoveFromSuperview();
            }
        }
#endif
    }

    private void RestoreAttachmentCards()
    {
        _attachmentStrip.Clear();
        foreach (AttachmentInfo attachment in _attachments) AddAttachmentCard(attachment);
    }

    private void RefreshAttachmentState()
    {
        _attachmentScroll.IsVisible = _attachmentStrip.Count > 0;
        bool blocked = _attachments.Any(item => item.IsUploading || item.NeedsAttention);
        if (!_generation.IsGenerating)
        {
            _send.IsEnabled = !blocked;
            _send.Opacity = blocked ? .45 : 1;
        }
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
        var continuationText = new Label { Text = "Response stopped before it finished. Continue from where it ended?", TextColor = Color.FromArgb("#FDE68A"), FontSize = 11, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap };
        var continueButton = new Button { Text = "\u21bb", FontSize = 18, CornerRadius = 9, BackgroundColor = Color.FromArgb("#92400E"), TextColor = Colors.White, WidthRequest = 42, HeightRequest = 34, Padding = 0 };
        AutomationProperties.SetName(continueButton, "Continue response");
        AutomationProperties.SetHelpText(continueButton, "Prepare a prompt that continues this incomplete response");
        continueButton.Clicked += (_, _) =>
        {
            _prompt.Text = "Continue the previous answer from exactly where it stopped. Do not repeat completed text.";
            _prompt.Focus();
            _status.Text = "Continuation is ready in the next prompt — review it, then send";
        };
        var continuationLayout = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        continuationLayout.Add(continuationText, 0);
        continuationLayout.Add(continueButton, 1);
        var continuationCard = new Border { Padding = new Thickness(10, 7), BackgroundColor = Color.FromArgb("#3B2A12"), Stroke = Color.FromArgb("#B45309"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 10 }, Content = continuationLayout };
        continuationCard.SetBinding(IsVisibleProperty, nameof(ChatMessage.NeedsContinuation));
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
        var workBrand = new Label { Text = "HEIROWFORGE", FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1.4, TextColor = Color.FromArgb("#7DD3FC") };
        var workStatus = new Label { FontSize = 9, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C4B5FD"), HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center };
        workStatus.SetBinding(Label.TextProperty, nameof(ChatMessage.WorkStatusLabel));
        var workHead = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        workHead.Add(workBrand, 0);
        workHead.Add(workStatus, 1);
        var workStep = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#F8FAFC"), LineBreakMode = LineBreakMode.WordWrap };
        workStep.SetBinding(Label.TextProperty, nameof(ChatMessage.WorkStepLabel));
        var workGoalCaption = new Label { Text = "GOAL", FontSize = 9, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1.2, TextColor = Color.FromArgb("#67E8F9") };
        workGoalCaption.SetBinding(IsVisibleProperty, nameof(ChatMessage.HasWorkGoal));
        var workGoal = new Label { FontSize = 12, TextColor = Color.FromArgb("#CDE6FF"), LineBreakMode = LineBreakMode.WordWrap };
        workGoal.SetBinding(Label.TextProperty, nameof(ChatMessage.WorkGoal));
        workGoal.SetBinding(IsVisibleProperty, nameof(ChatMessage.HasWorkGoal));
        var workProgress = new ProgressBar { HeightRequest = 7, BackgroundColor = Color.FromArgb("#26364D"), ProgressColor = Color.FromArgb("#818CF8") };
        workProgress.SetBinding(ProgressBar.ProgressProperty, nameof(ChatMessage.WorkProgress));
        var workPercent = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C4B5FD"), HorizontalTextAlignment = TextAlignment.End };
        workPercent.SetBinding(Label.TextProperty, nameof(ChatMessage.WorkProgressLabel));
        var workProgressGrid = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(46)) } };
        workProgressGrid.Add(workProgress, 0);
        workProgressGrid.Add(workPercent, 1);
        var workSteps = new VerticalStackLayout { Spacing = 8 };
        workSteps.SetBinding(BindableLayout.ItemsSourceProperty, nameof(ChatMessage.JackhammerSteps));
        BindableLayout.SetItemTemplate(workSteps, new DataTemplate(() =>
        {
            var number = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
            number.SetBinding(Label.TextProperty, nameof(JackhammerPlanStep.NumberLabel));
            number.SetBinding(Label.TextColorProperty, nameof(JackhammerPlanStep.AccentColor));
            var numberBox = new Border { WidthRequest = 31, HeightRequest = 31, Padding = 0, StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 9 }, Content = number };
            numberBox.SetBinding(Border.StrokeProperty, nameof(JackhammerPlanStep.AccentColor));
            numberBox.SetBinding(BackgroundColorProperty, nameof(JackhammerPlanStep.SurfaceColor));
            var kicker = new Label { FontSize = 9, FontAttributes = FontAttributes.Bold, CharacterSpacing = 0.8 };
            kicker.SetBinding(Label.TextProperty, nameof(JackhammerPlanStep.StepLabel));
            kicker.SetBinding(Label.TextColorProperty, nameof(JackhammerPlanStep.AccentColor));
            var title = new Label { FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#F1F5F9"), LineBreakMode = LineBreakMode.WordWrap };
            title.SetBinding(Label.TextProperty, nameof(JackhammerPlanStep.Title));
            var summary = new Label { FontSize = 11, TextColor = Color.FromArgb("#A9BAD0"), LineBreakMode = LineBreakMode.WordWrap };
            summary.SetBinding(Label.TextProperty, nameof(JackhammerPlanStep.Summary));
            var copy = new VerticalStackLayout { Spacing = 3, Children = { kicker, title, summary } };
            var rowGrid = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new ColumnDefinition(new GridLength(31)), new ColumnDefinition(GridLength.Star) } };
            rowGrid.Add(numberBox, 0);
            rowGrid.Add(copy, 1);
            var row = new Border { Padding = new Thickness(10), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = rowGrid };
            row.SetBinding(Border.StrokeProperty, nameof(JackhammerPlanStep.AccentColor));
            row.SetBinding(BackgroundColorProperty, nameof(JackhammerPlanStep.SurfaceColor));
            return row;
        }));
        var workFooter = new Label { FontSize = 10, TextColor = Color.FromArgb("#94A3B8"), LineBreakMode = LineBreakMode.WordWrap };
        workFooter.SetBinding(Label.TextProperty, nameof(ChatMessage.WorkSummary));
        var workLayout = new VerticalStackLayout { Spacing = 9, Children = { workHead, workStep, workGoalCaption, workGoal, workProgressGrid, workSteps, workFooter } };
        var workCard = new Border { Padding = new Thickness(13, 12), BackgroundColor = Color.FromArgb("#0C1729"), Stroke = Color.FromArgb("#385A86"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = workLayout };
        workCard.SetBinding(IsVisibleProperty, nameof(ChatMessage.HasWorkSummary));
        var border = new Border { Margin = new Thickness(10, 5), Padding = 12, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = new VerticalStackLayout { Spacing = 7, Children = { role, route, reasoningExpander, content, continuationCard, workCard, tools, telemetry, status } } };
        border.SetBinding(Border.BackgroundColorProperty, nameof(ChatMessage.BubbleColor));
        AttachMessageLongPress(border);
        return border;
    }

    private static void ApplyJackhammerWorkSummary(ChatMessage message, IEnumerable<JackhammerPlanStep> steps, string goal, string status, int progressPercent, bool jackhammerEnabled, bool isGenerating)
    {
        JackhammerPlanStep[] planned = steps.Take(8).ToArray();
        message.JackhammerSteps.Clear();
        if (!jackhammerEnabled && planned.Length == 0)
        {
            message.WorkSummary = "";
            message.WorkGoal = "";
            message.WorkStepLabel = "";
            return;
        }
        if (planned.Length == 0)
        {
            planned = new[]
            {
                new JackhammerPlanStep
                {
                    Status = "in_progress",
                    Title = "Shape the requested work",
                    Summary = "heirowForge is turning the request into a specific, ordered plan before taking action.",
                    Number = 1,
                    Total = 1
                }
            };
        }
        foreach (JackhammerPlanStep step in planned)
            message.JackhammerSteps.Add(step);
        int activeIndex = Array.FindIndex(planned, step => step.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase));
        if (activeIndex < 0)
            activeIndex = Math.Max(0, Array.FindLastIndex(planned, step => step.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)));
        int completed = planned.Count(step => step.Status.Equals("completed", StringComparison.OrdinalIgnoreCase));
        int calculatedProgress = (int)Math.Round(completed * 100d / Math.Max(1, planned.Length));
        message.WorkGoal = goal?.Trim() ?? "";
        message.WorkStatus = string.IsNullOrWhiteSpace(status) ? (isGenerating ? "in_progress" : "finished") : status;
        message.WorkProgressPercent = progressPercent >= 0 ? progressPercent : calculatedProgress;
        message.WorkStepLabel = $"Step {activeIndex + 1}/{planned.Length} · {planned[activeIndex].Title}";
        message.WorkSummary = isGenerating
            ? "Work continues automatically. Type direction and tap Steer to redirect this run."
            : "Work run complete. Review the step summaries and final answer above.";
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
                await Share.Default.RequestAsync(new ShareTextRequest { Text = message.Content, Title = "heirowLLM message" });
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
