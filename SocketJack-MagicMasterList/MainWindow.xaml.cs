using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using SocketJack.Net;
using SocketJack.Net.Payments;
using Forms = System.Windows.Forms;

namespace SocketJack.MagicMasterList;

public partial class MainWindow : Window {
    private readonly DispatcherTimer _refreshTimer;
    private MasterListServerHost? _host;
    private MasterListViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private bool _loadingPaymentPasswords;
    private bool _allowExit;
    private bool _shownTrayCloseNotice;
    private bool _shutdown;
    private bool _initialized;
    private bool _refreshing;

    public MainWindow() {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) {
        if (_initialized)
            return;

        _initialized = true;
        UpdateMaximizeRestoreGlyphs();

        try {
            StatusTextFallback("Loading master-list storage...");
            MasterListOptions options = await Task.Run(() => {
                MasterListViewModel.ApplySavedPaymentEnvironment();
                return MasterListOptions.Load();
            }).ConfigureAwait(true);

            _host = new MasterListServerHost(options);
            await _host.InitializeAsync().ConfigureAwait(true);
            _viewModel = new MasterListViewModel(_host);
            DataContext = _viewModel;
            LoadPaymentPasswordBoxes();

            _host.ServersChanged += Host_ServersChanged;
            _host.StatusChanged += Host_StatusChanged;
            _host.ShellRelaysChanged += Host_ShellRelaysChanged;

            await _viewModel.StartServerAsync().ConfigureAwait(true);
            await _viewModel.RefreshFeatureSurfacesAsync().ConfigureAwait(true);
            UpdateRefreshTimerState();
            EnsureTrayIcon();
            UpdateTrayIconStatusText();
        } catch (Exception ex) {
            StatusTextFallback("Startup failed: " + ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "SocketJack-MagicMasterList Startup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) {
        if (!_allowExit) {
            e.Cancel = true;
            HideToTray(true);
            return;
        }

        ShutdownServer();
    }

    public void ShutdownServer() {
        if (_shutdown)
            return;

        _shutdown = true;
        _refreshTimer.Stop();
        if (_host != null) {
            _host.ServersChanged -= Host_ServersChanged;
            _host.StatusChanged -= Host_StatusChanged;
            _host.ShellRelaysChanged -= Host_ShellRelaysChanged;
        }
        DisposeTrayIcon();
        _host?.Dispose();
    }

    private void Host_ServersChanged(object? sender, EventArgs e) {
        Dispatcher.BeginInvoke(new Action(async () => {
            if (_viewModel != null)
                await _viewModel.RefreshFromHostAsync().ConfigureAwait(true);
            UpdateTrayIconStatusText();
        }));
    }

    private void Host_StatusChanged(object? sender, EventArgs e) {
        Dispatcher.BeginInvoke(new Action(() => {
            _viewModel?.RefreshStatus();
            UpdateTrayIconStatusText();
        }));
    }

    private void Host_ShellRelaysChanged(object? sender, EventArgs e) {
        Dispatcher.BeginInvoke(new Action(async () => {
            if (_viewModel != null)
                await _viewModel.RefreshShellRelaysAsync().ConfigureAwait(true);
        }));
    }

    private bool ShouldRunRefreshTimer() =>
        !_shutdown &&
        _initialized &&
        _viewModel != null &&
        IsVisible &&
        WindowState != WindowState.Minimized;

    private void UpdateRefreshTimerState() {
        if (ShouldRunRefreshTimer()) {
            if (!_refreshTimer.IsEnabled)
                _refreshTimer.Start();
            return;
        }

        if (_refreshTimer.IsEnabled)
            _refreshTimer.Stop();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) {
        if (!ShouldRunRefreshTimer()) {
            UpdateRefreshTimerState();
            return;
        }

        if (_refreshing || _viewModel == null)
            return;

        _refreshing = true;
        try {
            await _viewModel.RefreshFromHostAsync().ConfigureAwait(true);
            UpdateTrayIconStatusText();
        } finally {
            _refreshing = false;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.StartServerAsync().ConfigureAwait(true);
        UpdateTrayIconStatusText();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.StopServerAsync().ConfigureAwait(true);
        UpdateTrayIconStatusText();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshFromHostAsync().ConfigureAwait(true);
        UpdateTrayIconStatusText();
    }

    private async void ReloadPaymentConfigButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel == null)
            return;
        await _viewModel.LoadPaymentConfigurationAsync().ConfigureAwait(true);
        LoadPaymentPasswordBoxes();
    }

    private void ResetPaymentProductsButton_Click(object sender, RoutedEventArgs e) {
        _viewModel?.ResetStripeTokenProducts();
    }

    private async void SavePaymentConfigButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel == null)
            return;
        CapturePaymentPasswordBoxes();
        await _viewModel.SavePaymentConfigurationAsync().ConfigureAwait(true);
        LoadPaymentPasswordBoxes();
    }

    private async void RefreshAccountsButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshAccountsAsync().ConfigureAwait(true);
    }

    private async void SaveAccountButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.SaveSelectedAccountAsync(resetTokens: false).ConfigureAwait(true);
    }

    private async void ResetAccountUsageButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.SaveSelectedAccountAsync(resetTokens: true).ConfigureAwait(true);
    }

    private async void RefreshRegistrationRequestsButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshRegistrationRequestsAsync().ConfigureAwait(true);
    }

    private async void ApproveRegistrationButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.DecideSelectedRegistrationRequestAsync(true).ConfigureAwait(true);
    }

    private async void DenyRegistrationButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.DecideSelectedRegistrationRequestAsync(false).ConfigureAwait(true);
    }

    private async void RefreshReportsButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshRuntimeReportsAsync().ConfigureAwait(true);
    }

    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshMasterLogsAsync().ConfigureAwait(true);
    }

    private async void RefreshUpdatesButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshUpdateManifestAsync().ConfigureAwait(true);
    }

    private void OpenUpdateFolderButton_Click(object sender, RoutedEventArgs e) {
        OpenFolder(_viewModel?.UpdateRootText ?? "");
    }

    private void OpenUpdateBaseUrlButton_Click(object sender, RoutedEventArgs e) {
        OpenUrl(_viewModel?.UpdateBaseUrlText ?? "");
    }

    private async void RefreshRoutesButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshRoutesAsync().ConfigureAwait(true);
    }

    private void UseSelectedServerForLookupButton_Click(object sender, RoutedEventArgs e) {
        _viewModel?.UseSelectedServerForLocationLookup();
    }

    private async void LookupLocationButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.LookupLocationAsync().ConfigureAwait(true);
    }

    private void PaymentPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) {
        if (!_loadingPaymentPasswords)
            CapturePaymentPasswordBoxes();
    }

    private void LoadPaymentPasswordBoxes() {
        if (_viewModel == null)
            return;

        _loadingPaymentPasswords = true;
        try {
            if (StripeSecretKeyPasswordBox != null)
                StripeSecretKeyPasswordBox.Password = _viewModel.StripeSecretKey ?? "";
            if (StripeWebhookSecretPasswordBox != null)
                StripeWebhookSecretPasswordBox.Password = _viewModel.StripeWebhookSecret ?? "";
        } finally {
            _loadingPaymentPasswords = false;
        }
    }

    private void CapturePaymentPasswordBoxes() {
        if (_viewModel == null)
            return;

        if (StripeSecretKeyPasswordBox != null)
            _viewModel.StripeSecretKey = StripeSecretKeyPasswordBox.Password ?? "";
        if (StripeWebhookSecretPasswordBox != null)
            _viewModel.StripeWebhookSecret = StripeWebhookSecretPasswordBox.Password ?? "";
    }

    private async void RefreshShellButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.RefreshShellRelaysAsync().ConfigureAwait(true);
    }

    private async void SaveShellButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel != null)
            await _viewModel.SaveShellRelayAsync().ConfigureAwait(true);
    }

    private void DeleteShellButton_Click(object sender, RoutedEventArgs e) {
        if (_viewModel?.SelectedShellRelay == null)
            return;

        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "Delete " + _viewModel.SelectedShellRelay.TitleText + " from shell routes?",
            "Delete Shell Route",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _ = _viewModel.DeleteSelectedShellRelayAsync();
    }

    private void OpenSelectedServerButton_Click(object sender, RoutedEventArgs e) {
        OpenServer(_viewModel?.SelectedServer);
    }

    private void CopySelectedServerUrlButton_Click(object sender, RoutedEventArgs e) {
        CopyServerUrl(_viewModel?.SelectedServer);
    }

    private void MarkSelectedServerOfflineButton_Click(object sender, RoutedEventArgs e) {
        MarkServerOffline(_viewModel?.SelectedServer);
    }

    private void DeleteSelectedServerButton_Click(object sender, RoutedEventArgs e) {
        DeleteServer(_viewModel?.SelectedServer);
    }

    private void OpenServerButton_Click(object sender, RoutedEventArgs e) {
        OpenServer((sender as FrameworkElement)?.Tag as RegisteredServer);
    }

    private void MarkServerOfflineButton_Click(object sender, RoutedEventArgs e) {
        MarkServerOffline((sender as FrameworkElement)?.Tag as RegisteredServer);
    }

    private void DeleteServerButton_Click(object sender, RoutedEventArgs e) {
        DeleteServer((sender as FrameworkElement)?.Tag as RegisteredServer);
    }

    private void OpenServer(RegisteredServer? server) {
        if (server == null)
            return;

        string url = RegisteredServer.FirstNonEmpty(server.LaunchUrl, server.Endpoint, RegisteredServer.BuildEndpoint(server.ConnectHost, server.ChatPort));
        if (string.IsNullOrWhiteSpace(url))
            return;

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void CopyServerUrl(RegisteredServer? server) {
        if (server == null)
            return;

        string url = RegisteredServer.FirstNonEmpty(server.LaunchUrl, server.Endpoint, RegisteredServer.BuildEndpoint(server.ConnectHost, server.ChatPort));
        if (!string.IsNullOrWhiteSpace(url))
            System.Windows.Clipboard.SetText(url);
    }

    private static void OpenUrl(string url) {
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void OpenFolder(string path) {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void MarkServerOffline(RegisteredServer? server) {
        if (server == null)
            return;

        if (_viewModel != null)
            await _viewModel.MarkServerOfflineAsync(server).ConfigureAwait(true);
    }

    private async void DeleteServer(RegisteredServer? server) {
        if (server == null)
            return;

        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "Delete " + server.TitleText + " from the master list?",
            "Delete Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes && _viewModel != null)
            await _viewModel.DeleteServerAsync(server).ConfigureAwait(true);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeRestoreGlyphs();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e) {
        UpdateMaximizeRestoreGlyphs();
        UpdateRefreshTimerState();
    }

    private void UpdateMaximizeRestoreGlyphs() {
        if (MaximizeGlyph == null || RestoreGlyph == null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(6);
    }

    private void EnsureTrayIcon() {
        if (_trayIcon != null)
            return;

        var menu = new Forms.ContextMenuStrip {
            ForeColor = System.Drawing.Color.Black,
            ShowItemToolTips = true
        };
        menu.Opening += (_, _) => Dispatcher.Invoke(() => RebuildTrayContextMenu(menu));
        _trayMenu = menu;
        RebuildTrayContextMenu(menu);

        _trayIcon = new Forms.NotifyIcon {
            Text = BuildTrayIconText(),
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void RebuildTrayContextMenu(Forms.ContextMenuStrip menu) {
        menu.Items.Clear();
        UpdateTrayIconStatusText();

        menu.Items.Add(CreateTrayInfoItem("SocketJack-MagicMasterList"));
        menu.Items.Add(CreateTrayInfoItem(BuildTrayServerCountText()));
        menu.Items.Add(CreateTrayInfoItem(_viewModel?.StatusText ?? "Loading"));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var showItem = CreateTrayMenuItem("Show Dashboard", ShowFromTray);
        menu.Items.Add(showItem);

        var startStopItem = CreateTrayMenuItem(_viewModel?.CanStop == true ? "Stop Server" : "Start Server", async () => {
            if (_viewModel == null)
                return;
            if (_viewModel.CanStop)
                await _viewModel.StopServerAsync().ConfigureAwait(true);
            else
                await _viewModel.StartServerAsync().ConfigureAwait(true);
            UpdateTrayIconStatusText();
        });
        menu.Items.Add(startStopItem);
        menu.Items.Add(CreateTrayMenuItem("Refresh", async () => {
            if (_viewModel != null)
                await _viewModel.RefreshFromHostAsync().ConfigureAwait(true);
            UpdateTrayIconStatusText();
        }));

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateTrayInfoItem("Website: " + (_viewModel?.PublicUrlText ?? "")));
        menu.Items.Add(CreateTrayInfoItem("SQL Admin: " + (_viewModel?.SqlAdminText ?? "")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateTrayMenuItem("Exit", ExitFromTray));
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(string text, Func<Task>? action = null) {
        var item = new Forms.ToolStripMenuItem(text ?? "");
        if (action != null)
            item.Click += async (_, _) => await action().ConfigureAwait(true);
        return item;
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(string text, Action action) {
        var item = new Forms.ToolStripMenuItem(text ?? "");
        item.Click += (_, _) => action();
        return item;
    }

    private static Forms.ToolStripMenuItem CreateTrayInfoItem(string text) {
        return new Forms.ToolStripMenuItem(text ?? "") {
            Enabled = false
        };
    }

    private void UpdateTrayIconStatusText() {
        if (_trayIcon != null)
            _trayIcon.Text = BuildTrayIconText();
    }

    private string BuildTrayIconText() {
        string state = _viewModel?.CanStop == true ? "running" : _viewModel == null ? "loading" : "stopped";
        return TrimNotifyIconText("SocketJack-MagicMasterList | " + state + " | " + BuildTrayServerCountText());
    }

    private string BuildTrayServerCountText() {
        int total = _viewModel?.Servers.Count ?? 0;
        int online = _viewModel?.Servers.Count(server => server.IsOnline) ?? 0;
        return online.ToString(CultureInfo.InvariantCulture) + " online / " + total.ToString(CultureInfo.InvariantCulture) + " registered";
    }

    private void StatusTextFallback(string message) {
        Title = "SocketJack-MagicMasterList - " + message;
    }

    private static string TrimNotifyIconText(string text) {
        text = (text ?? "").Trim();
        return text.Length <= 63 ? text : text.Substring(0, 60) + "...";
    }

    private void HideToTray(bool notify) {
        EnsureTrayIcon();
        Hide();
        ShowInTaskbar = false;
        UpdateRefreshTimerState();

        if (!notify || _trayIcon == null || _shownTrayCloseNotice)
            return;

        _shownTrayCloseNotice = true;
        _trayIcon.BalloonTipTitle = "SocketJack-MagicMasterList";
        _trayIcon.BalloonTipText = "Still running in the notification area. Use the tray menu to show it or exit.";
        _trayIcon.ShowBalloonTip(3000);
    }

    private void ShowFromTray() {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        UpdateRefreshTimerState();
    }

    private void ExitFromTray() {
        _allowExit = true;
        Close();
    }

    private void DisposeTrayIcon() {
        if (_trayIcon != null) {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
    }
}

internal sealed class MasterListViewModel : INotifyPropertyChanged {
    private static readonly PropertyInfo[] RegisteredServerDiffProperties = BuildDiffProperties<RegisteredServer>();
    private static readonly PropertyInfo[] ShellRelayDiffProperties = BuildDiffProperties<MasterShellRelay>();
    private readonly MasterListServerHost _host;
    private string _searchText = "";
    private string _statusText = "Idle";
    private string _serverCountText = "0 servers";
    private string _lastUpdatedText = "";
    private string _filterText = "0 total";
    private string _emptyStateDetail = "Waiting for host registrations.";
    private RegisteredServer? _selectedServer;
    private bool _canStart = true;
    private bool _canStop;
    private bool _canManageSelectedServer;
    private bool _canManageSelectedShell;
    private Visibility _emptyStateVisibility = Visibility.Visible;
    private MasterShellRelay? _selectedShellRelay;
    private string _shellName = "";
    private string _shellPublicPort = "18436";
    private string _shellAgentPort = "19436";
    private string _shellTargetHost = "127.0.0.1";
    private string _shellTargetPort = "11436";
    private string _stripePublishableKey = "";
    private string _stripeSecretKey = "";
    private string _stripeWebhookSecret = "";
    private string _stripeWebhookUrl = "";
    private string _stripeConnectClientId = "";
    private string _stripeConnectClientSecret = "";
    private string _stripeConnectRedirectUrl = "";
    private string _stripeCheckoutSuccessUrl = "";
    private string _stripeCheckoutCancelUrl = "";
    private string _stripeTokenUsdPerToken = "0.00005";
    private string _stripeServiceFeePercent = "1.5";
    private string _paymentConfigStatus = "";
    private AccountRecord? _selectedAccount;
    private AccountRegistrationRequest? _selectedRegistrationRequest;
    private SocketJackRuntimeReport? _selectedRuntimeReport;
    private SocketJackMasterLogEntry? _selectedMasterLog;
    private DirectoryHashFile? _selectedUpdateFile;
    private bool _selectedAccountEnabled;
    private bool _selectedAccountAdministrator;
    private bool _selectedAccountAutoPublish;
    private string _selectedAccountTokenLimit = "0";
    private string _selectedAccountTokenDelta = "0";
    private string _accountStatusText = "";
    private bool _pendingRegistrationsOnly = true;
    private string _registrationDecisionNote = "";
    private string _registrationStatusText = "";
    private string _runtimeReportDetailText = "Select a runtime report to inspect its details.";
    private string _masterLogDetailText = "Select a log entry to inspect its details.";
    private string _updateStatusText = "";
    private string _updateRootText = "";
    private string _updateBaseUrlText = "";
    private string _updateGeneratedText = "";
    private string _routeStatusText = "";
    private string _locationLookupTarget = "";
    private string _locationLookupStatus = "";

    public MasterListViewModel(MasterListServerHost host) {
        _host = host;
        ServersView = CollectionViewSource.GetDefaultView(Servers);
        ServersView.Filter = FilterServer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RegisteredServer> Servers { get; } = new();
    public ICollectionView ServersView { get; }
    public ObservableCollection<MasterShellRelay> ShellRelays { get; } = new();
    public ObservableCollection<StripeTokenProductConfig> StripeTokenProducts { get; } = new();
    public ObservableCollection<AccountRecord> Accounts { get; } = new();
    public ObservableCollection<AccountRegistrationRequest> RegistrationRequests { get; } = new();
    public ObservableCollection<SocketJackRuntimeReport> RuntimeReports { get; } = new();
    public ObservableCollection<SocketJackMasterLogEntry> MasterLogs { get; } = new();
    public ObservableCollection<DirectoryHashFile> UpdateFiles { get; } = new();
    public ObservableCollection<MasterRouteStatusItem> RouteStatusItems { get; } = new();

    public RegisteredServer? SelectedServer {
        get => _selectedServer;
        set {
            if (ReferenceEquals(_selectedServer, value))
                return;
            _selectedServer = value;
            OnPropertyChanged(nameof(SelectedServer));
            CanManageSelectedServer = _selectedServer != null;
        }
    }

    public string SearchText {
        get => _searchText;
        set {
            if (_searchText == value)
                return;
            _searchText = value ?? "";
            OnPropertyChanged(nameof(SearchText));
            ServersView.Refresh();
            RefreshCounts();
        }
    }

    public string StatusText {
        get => _statusText;
        private set => Set(ref _statusText, value, nameof(StatusText));
    }

    public string ServerCountText {
        get => _serverCountText;
        private set => Set(ref _serverCountText, value, nameof(ServerCountText));
    }

    public string LastUpdatedText {
        get => _lastUpdatedText;
        private set => Set(ref _lastUpdatedText, value, nameof(LastUpdatedText));
    }

    public string FilterText {
        get => _filterText;
        private set => Set(ref _filterText, value, nameof(FilterText));
    }

    public string EmptyStateDetail {
        get => _emptyStateDetail;
        private set => Set(ref _emptyStateDetail, value, nameof(EmptyStateDetail));
    }

    public bool CanStart {
        get => _canStart;
        private set => Set(ref _canStart, value, nameof(CanStart));
    }

    public bool CanStop {
        get => _canStop;
        private set => Set(ref _canStop, value, nameof(CanStop));
    }

    public bool CanManageSelectedServer {
        get => _canManageSelectedServer;
        private set => Set(ref _canManageSelectedServer, value, nameof(CanManageSelectedServer));
    }

    public MasterShellRelay? SelectedShellRelay {
        get => _selectedShellRelay;
        set {
            if (ReferenceEquals(_selectedShellRelay, value))
                return;
            _selectedShellRelay = value;
            OnPropertyChanged(nameof(SelectedShellRelay));
            CanManageSelectedShell = _selectedShellRelay != null;
            ApplySelectedShellToEditor();
            OnPropertyChanged(nameof(SelectedShellDebugText));
            OnPropertyChanged(nameof(SelectedShellEnabled));
            OnPropertyChanged(nameof(ShellEnabledText));
        }
    }

    public bool CanManageSelectedShell {
        get => _canManageSelectedShell;
        private set => Set(ref _canManageSelectedShell, value, nameof(CanManageSelectedShell));
    }

    public bool SelectedShellEnabled {
        get => SelectedShellRelay?.Enabled ?? false;
        set {
            if (SelectedShellRelay == null || SelectedShellRelay.Enabled == value)
                return;
            _host.SetShellRelayEnabled(SelectedShellRelay.Id, value);
            _ = RefreshShellRelaysAsync();
        }
    }

    public string ShellEnabledText => SelectedShellEnabled ? "Enabled" : "Disabled";
    public string SelectedShellDebugText => SelectedShellRelay?.DebugText ?? "Select a shell instance to view debug data.";

    public string ShellName {
        get => _shellName;
        set => Set(ref _shellName, value ?? "", nameof(ShellName));
    }

    public string ShellPublicPort {
        get => _shellPublicPort;
        set => Set(ref _shellPublicPort, value ?? "", nameof(ShellPublicPort));
    }

    public string ShellAgentPort {
        get => _shellAgentPort;
        set => Set(ref _shellAgentPort, value ?? "", nameof(ShellAgentPort));
    }

    public string ShellTargetHost {
        get => _shellTargetHost;
        set => Set(ref _shellTargetHost, value ?? "", nameof(ShellTargetHost));
    }

    public string ShellTargetPort {
        get => _shellTargetPort;
        set => Set(ref _shellTargetPort, value ?? "", nameof(ShellTargetPort));
    }

    public string StripePublishableKey {
        get => _stripePublishableKey;
        set => Set(ref _stripePublishableKey, value ?? "", nameof(StripePublishableKey));
    }

    public string StripeSecretKey {
        get => _stripeSecretKey;
        set => Set(ref _stripeSecretKey, value ?? "", nameof(StripeSecretKey));
    }

    public string StripeWebhookSecret {
        get => _stripeWebhookSecret;
        set => Set(ref _stripeWebhookSecret, value ?? "", nameof(StripeWebhookSecret));
    }

    public string StripeWebhookUrl {
        get => _stripeWebhookUrl;
        private set => Set(ref _stripeWebhookUrl, value ?? "", nameof(StripeWebhookUrl));
    }

    public string StripeConnectClientId {
        get => _stripeConnectClientId;
        set => Set(ref _stripeConnectClientId, value ?? "", nameof(StripeConnectClientId));
    }

    public string StripeConnectClientSecret {
        get => _stripeConnectClientSecret;
        set => Set(ref _stripeConnectClientSecret, value ?? "", nameof(StripeConnectClientSecret));
    }

    public string StripeConnectRedirectUrl {
        get => _stripeConnectRedirectUrl;
        set => Set(ref _stripeConnectRedirectUrl, value ?? "", nameof(StripeConnectRedirectUrl));
    }

    public string StripeCheckoutSuccessUrl {
        get => _stripeCheckoutSuccessUrl;
        set => Set(ref _stripeCheckoutSuccessUrl, value ?? "", nameof(StripeCheckoutSuccessUrl));
    }

    public string StripeCheckoutCancelUrl {
        get => _stripeCheckoutCancelUrl;
        set => Set(ref _stripeCheckoutCancelUrl, value ?? "", nameof(StripeCheckoutCancelUrl));
    }

    public string StripeTokenUsdPerToken {
        get => _stripeTokenUsdPerToken;
        set => Set(ref _stripeTokenUsdPerToken, value ?? "", nameof(StripeTokenUsdPerToken));
    }

    public string StripeServiceFeePercent {
        get => _stripeServiceFeePercent;
        set => Set(ref _stripeServiceFeePercent, value ?? "", nameof(StripeServiceFeePercent));
    }

    public string PaymentConfigStatus {
        get => _paymentConfigStatus;
        private set => Set(ref _paymentConfigStatus, value ?? "", nameof(PaymentConfigStatus));
    }

    public AccountRecord? SelectedAccount {
        get => _selectedAccount;
        set {
            if (ReferenceEquals(_selectedAccount, value))
                return;
            _selectedAccount = value;
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(CanManageSelectedAccount));
            ApplySelectedAccountToEditor();
        }
    }

    public bool CanManageSelectedAccount => SelectedAccount != null;

    public bool SelectedAccountEnabled {
        get => _selectedAccountEnabled;
        set => Set(ref _selectedAccountEnabled, value, nameof(SelectedAccountEnabled));
    }

    public bool SelectedAccountAdministrator {
        get => _selectedAccountAdministrator;
        set => Set(ref _selectedAccountAdministrator, value, nameof(SelectedAccountAdministrator));
    }

    public bool SelectedAccountAutoPublish {
        get => _selectedAccountAutoPublish;
        set => Set(ref _selectedAccountAutoPublish, value, nameof(SelectedAccountAutoPublish));
    }

    public string SelectedAccountTokenLimit {
        get => _selectedAccountTokenLimit;
        set => Set(ref _selectedAccountTokenLimit, value ?? "", nameof(SelectedAccountTokenLimit));
    }

    public string SelectedAccountTokenDelta {
        get => _selectedAccountTokenDelta;
        set => Set(ref _selectedAccountTokenDelta, value ?? "", nameof(SelectedAccountTokenDelta));
    }

    public string SelectedAccountUsageText => SelectedAccount == null
        ? "Select an account."
        : SelectedAccount.TokensUsed.ToString("N0", CultureInfo.CurrentCulture) + " used / " +
          (SelectedAccount.TokenLimit <= 0 ? "unlimited" : SelectedAccount.TokenLimit.ToString("N0", CultureInfo.CurrentCulture) + " limit");

    public string AccountStatusText {
        get => _accountStatusText;
        private set => Set(ref _accountStatusText, value ?? "", nameof(AccountStatusText));
    }

    public AccountRegistrationRequest? SelectedRegistrationRequest {
        get => _selectedRegistrationRequest;
        set {
            if (ReferenceEquals(_selectedRegistrationRequest, value))
                return;
            _selectedRegistrationRequest = value;
            OnPropertyChanged(nameof(SelectedRegistrationRequest));
            OnPropertyChanged(nameof(CanDecideSelectedRegistrationRequest));
        }
    }

    public bool CanDecideSelectedRegistrationRequest => SelectedRegistrationRequest != null &&
                                                        SelectedRegistrationRequest.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);

    public bool PendingRegistrationsOnly {
        get => _pendingRegistrationsOnly;
        set {
            if (_pendingRegistrationsOnly == value)
                return;
            _pendingRegistrationsOnly = value;
            OnPropertyChanged(nameof(PendingRegistrationsOnly));
            _ = RefreshRegistrationRequestsAsync();
        }
    }

    public string RegistrationDecisionNote {
        get => _registrationDecisionNote;
        set => Set(ref _registrationDecisionNote, value ?? "", nameof(RegistrationDecisionNote));
    }

    public string RegistrationStatusText {
        get => _registrationStatusText;
        private set => Set(ref _registrationStatusText, value ?? "", nameof(RegistrationStatusText));
    }

    public SocketJackRuntimeReport? SelectedRuntimeReport {
        get => _selectedRuntimeReport;
        set {
            if (ReferenceEquals(_selectedRuntimeReport, value))
                return;
            _selectedRuntimeReport = value;
            OnPropertyChanged(nameof(SelectedRuntimeReport));
            RuntimeReportDetailText = BuildRuntimeReportDetailText(value);
        }
    }

    public string RuntimeReportDetailText {
        get => _runtimeReportDetailText;
        private set => Set(ref _runtimeReportDetailText, value ?? "", nameof(RuntimeReportDetailText));
    }

    public SocketJackMasterLogEntry? SelectedMasterLog {
        get => _selectedMasterLog;
        set {
            if (ReferenceEquals(_selectedMasterLog, value))
                return;
            _selectedMasterLog = value;
            OnPropertyChanged(nameof(SelectedMasterLog));
            MasterLogDetailText = BuildMasterLogDetailText(value);
        }
    }

    public string MasterLogDetailText {
        get => _masterLogDetailText;
        private set => Set(ref _masterLogDetailText, value ?? "", nameof(MasterLogDetailText));
    }

    public DirectoryHashFile? SelectedUpdateFile {
        get => _selectedUpdateFile;
        set {
            if (ReferenceEquals(_selectedUpdateFile, value))
                return;
            _selectedUpdateFile = value;
            OnPropertyChanged(nameof(SelectedUpdateFile));
        }
    }

    public string UpdateStatusText {
        get => _updateStatusText;
        private set => Set(ref _updateStatusText, value ?? "", nameof(UpdateStatusText));
    }

    public string UpdateRootText {
        get => _updateRootText;
        private set => Set(ref _updateRootText, value ?? "", nameof(UpdateRootText));
    }

    public string UpdateBaseUrlText {
        get => _updateBaseUrlText;
        private set => Set(ref _updateBaseUrlText, value ?? "", nameof(UpdateBaseUrlText));
    }

    public string UpdateGeneratedText {
        get => _updateGeneratedText;
        private set => Set(ref _updateGeneratedText, value ?? "", nameof(UpdateGeneratedText));
    }

    public string RouteStatusText {
        get => _routeStatusText;
        private set => Set(ref _routeStatusText, value ?? "", nameof(RouteStatusText));
    }

    public string LocationLookupTarget {
        get => _locationLookupTarget;
        set => Set(ref _locationLookupTarget, value ?? "", nameof(LocationLookupTarget));
    }

    public string LocationLookupStatus {
        get => _locationLookupStatus;
        private set => Set(ref _locationLookupStatus, value ?? "", nameof(LocationLookupStatus));
    }

    public Visibility EmptyStateVisibility {
        get => _emptyStateVisibility;
        private set => Set(ref _emptyStateVisibility, value, nameof(EmptyStateVisibility));
    }

    public string PortText => _host.WebsitePortsText + " website / " + _host.Port.ToString() + " API";
    public string PublicUrlText => _host.WebsitePublicUrl + " website / " + _host.PublicUrl + " API";
    public string DataPathText => _host.DataPath;
    public string DatabaseText => _host.DatabaseName + "." + _host.TableName;
    public string SqlAdminText => RegisteredServer.FirstNonEmpty(_host.LocalSqlAdminUrl, _host.SqlAdminUrl);

    public async Task StartServerAsync() {
        await Task.Run(_host.Start).ConfigureAwait(true);
        RefreshStatus();
        await RefreshFromHostAsync().ConfigureAwait(true);
    }

    public async Task StopServerAsync() {
        await Task.Run(_host.Stop).ConfigureAwait(true);
        RefreshStatus();
    }

    public async Task RefreshFromHostAsync() {
        string selectedId = SelectedServer?.Id ?? "";
        List<RegisteredServer> servers = await Task.Run(_host.GetServers).ConfigureAwait(true);
        bool serversChanged = SyncCollection(Servers, servers, ServerDiffKey, RegisteredServerDiffProperties);
        if (serversChanged)
            ServersView.Refresh();
        SelectedServer = Servers.FirstOrDefault(server => string.Equals(server.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        RefreshStatus();
        RefreshCounts();
        await RefreshShellRelaysAsync().ConfigureAwait(true);
    }

    public async Task RefreshShellRelaysAsync() {
        string selectedId = SelectedShellRelay?.Id ?? "";
        List<MasterShellRelay> relays = await Task.Run(_host.GetShellRelays).ConfigureAwait(true);
        SyncCollection(ShellRelays, relays, ShellRelayDiffKey, ShellRelayDiffProperties);
        SelectedShellRelay = ShellRelays.FirstOrDefault(relay => string.Equals(relay.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
                             ShellRelays.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedShellDebugText));
        OnPropertyChanged(nameof(SelectedShellEnabled));
        OnPropertyChanged(nameof(ShellEnabledText));
    }

    private static PropertyInfo[] BuildDiffProperties<T>() =>
        typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .ToArray();

    private static bool SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, int, string> keySelector,
        IReadOnlyList<PropertyInfo> diffProperties) {
        bool changed = false;
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++) {
            T next = source[sourceIndex];
            string key = keySelector(next, sourceIndex);
            int currentIndex = FindCollectionIndex(target, key, keySelector);
            if (currentIndex < 0) {
                target.Insert(sourceIndex, next);
                changed = true;
            } else {
                if (currentIndex != sourceIndex) {
                    target.Move(currentIndex, sourceIndex);
                    changed = true;
                }

                if (!DiffPropertiesEqual(target[sourceIndex], next, diffProperties)) {
                    target[sourceIndex] = next;
                    changed = true;
                }
            }
        }

        while (target.Count > source.Count) {
            target.RemoveAt(target.Count - 1);
            changed = true;
        }

        return changed;
    }

    private static int FindCollectionIndex<T>(ObservableCollection<T> target, string key, Func<T, int, string> keySelector) {
        for (int i = 0; i < target.Count; i++) {
            if (string.Equals(keySelector(target[i], i), key, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool DiffPropertiesEqual<T>(T current, T next, IReadOnlyList<PropertyInfo> diffProperties) {
        foreach (PropertyInfo property in diffProperties) {
            if (!DiffValuesEqual(property.GetValue(current), property.GetValue(next)))
                return false;
        }

        return true;
    }

    private static bool DiffValuesEqual(object? current, object? next) {
        if (current is IReadOnlyList<string> currentStrings && next is IReadOnlyList<string> nextStrings)
            return currentStrings.SequenceEqual(nextStrings, StringComparer.Ordinal);
        return Equals(current, next);
    }

    private static string ServerDiffKey(RegisteredServer server, int index) =>
        !string.IsNullOrWhiteSpace(server.Id)
            ? server.Id
            : "__server_" + index.ToString(CultureInfo.InvariantCulture);

    private static string ShellRelayDiffKey(MasterShellRelay relay, int index) =>
        !string.IsNullOrWhiteSpace(relay.Id)
            ? relay.Id
            : "__shell_" + index.ToString(CultureInfo.InvariantCulture);

    public async Task RefreshFeatureSurfacesAsync() {
        await RefreshAccountsAsync().ConfigureAwait(true);
        await RefreshRuntimeReportsAsync().ConfigureAwait(true);
        await RefreshMasterLogsAsync().ConfigureAwait(true);
    }

    public async Task RefreshAccountsAsync() {
        string selectedUser = SelectedAccount?.UserName ?? "";
        List<AccountRecord> accounts = await Task.Run(_host.GetAccountsForUi).ConfigureAwait(true);
        Accounts.Clear();
        foreach (AccountRecord account in accounts)
            Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(account => string.Equals(account.UserName, selectedUser, StringComparison.OrdinalIgnoreCase)) ??
                          Accounts.FirstOrDefault();
        AccountStatusText = Accounts.Count.ToString(CultureInfo.InvariantCulture) + " account" + (Accounts.Count == 1 ? "" : "s");
    }

    public async Task SaveSelectedAccountAsync(bool resetTokens) {
        if (SelectedAccount == null)
            return;

        try {
            long tokenLimit = ParseLong(SelectedAccountTokenLimit, SelectedAccount.TokenLimit);
            long tokenDelta = ParseLong(SelectedAccountTokenDelta, 0);
            string userName = SelectedAccount.UserName;
            AccountRecord updated = await Task.Run(() => _host.UpdateAccountForUi(
                userName,
                SelectedAccountEnabled,
                SelectedAccountAdministrator,
                SelectedAccountAutoPublish,
                tokenLimit,
                resetTokens,
                tokenDelta)).ConfigureAwait(true);
            AccountStatusText = "Saved " + updated.UserName + ".";
            SelectedAccountTokenDelta = "0";
            await RefreshAccountsAsync().ConfigureAwait(true);
            SelectedAccount = Accounts.FirstOrDefault(account => string.Equals(account.UserName, updated.UserName, StringComparison.OrdinalIgnoreCase));
        } catch (Exception ex) {
            AccountStatusText = "Account update failed: " + TrimForDisplay(ex.Message, 180);
        }
    }

    public async Task RefreshRegistrationRequestsAsync() {
        string selectedId = SelectedRegistrationRequest?.Id ?? "";
        bool pendingOnly = PendingRegistrationsOnly;
        List<AccountRegistrationRequest> requests = await Task.Run(() => _host.GetRegistrationRequestsForUi(pendingOnly)).ConfigureAwait(true);
        RegistrationRequests.Clear();
        foreach (AccountRegistrationRequest request in requests)
            RegistrationRequests.Add(request);
        SelectedRegistrationRequest = RegistrationRequests.FirstOrDefault(request => string.Equals(request.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
                                      RegistrationRequests.FirstOrDefault();
        RegistrationStatusText = RegistrationRequests.Count.ToString(CultureInfo.InvariantCulture) + " registration request" + (RegistrationRequests.Count == 1 ? "" : "s");
    }

    public async Task DecideSelectedRegistrationRequestAsync(bool approved) {
        if (SelectedRegistrationRequest == null)
            return;

        try {
            string requestId = SelectedRegistrationRequest.Id;
            string note = RegistrationDecisionNote;
            AccountRegistrationRequest decided = await Task.Run(() => _host.DecideRegistrationRequestForUi(requestId, approved, "WPF admin", note)).ConfigureAwait(true);
            RegistrationStatusText = (approved ? "Approved " : "Denied ") + decided.UserName + ".";
            RegistrationDecisionNote = "";
            await RefreshRegistrationRequestsAsync().ConfigureAwait(true);
            await RefreshAccountsAsync().ConfigureAwait(true);
        } catch (Exception ex) {
            RegistrationStatusText = "Registration decision failed: " + TrimForDisplay(ex.Message, 180);
        }
    }

    public async Task RefreshRuntimeReportsAsync() {
        string selectedId = SelectedRuntimeReport?.Id ?? "";
        List<SocketJackRuntimeReport> reports = await Task.Run(() => _host.GetRuntimeReportsForUi(250)).ConfigureAwait(true);
        RuntimeReports.Clear();
        foreach (SocketJackRuntimeReport report in reports)
            RuntimeReports.Add(report);
        SelectedRuntimeReport = RuntimeReports.FirstOrDefault(report => string.Equals(report.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
                                RuntimeReports.FirstOrDefault();
        if (RuntimeReports.Count == 0)
            RuntimeReportDetailText = "No runtime reports have been recorded.";
    }

    public async Task RefreshMasterLogsAsync() {
        string selectedId = SelectedMasterLog?.Id ?? "";
        List<SocketJackMasterLogEntry> logs = await Task.Run(() => _host.GetMasterLogsForUi(100)).ConfigureAwait(true);
        MasterLogs.Clear();
        foreach (SocketJackMasterLogEntry log in logs)
            MasterLogs.Add(log);
        SelectedMasterLog = MasterLogs.FirstOrDefault(log => string.Equals(log.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
                            MasterLogs.FirstOrDefault();
        if (MasterLogs.Count == 0)
            MasterLogDetailText = "No master-list logs have been recorded.";
    }

    public async Task RefreshUpdateManifestAsync() {
        try {
            DirectoryHashManifest manifest = await Task.Run(_host.GetJackLLMUpdateManifestForUi).ConfigureAwait(true);
            UpdateFiles.Clear();
            foreach (DirectoryHashFile file in manifest.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
                UpdateFiles.Add(file);
            UpdateRootText = manifest.Root;
            UpdateBaseUrlText = manifest.BaseUrl;
            UpdateGeneratedText = string.IsNullOrWhiteSpace(manifest.GeneratedUtc) ? "Not generated" : manifest.GeneratedUtc;
            UpdateStatusText = manifest.Available
                ? manifest.Count.ToString(CultureInfo.InvariantCulture) + " update file" + (manifest.Count == 1 ? "" : "s") + " available."
                : "Update manifest unavailable: " + TrimForDisplay(manifest.Error, 180);
        } catch (Exception ex) {
            UpdateStatusText = "Update manifest failed: " + TrimForDisplay(ex.Message, 180);
        }
    }

    public async Task RefreshRoutesAsync() {
        List<MasterRouteStatusItem> items = await Task.Run(_host.GetRouteStatusItemsForUi).ConfigureAwait(true);
        RouteStatusItems.Clear();
        foreach (MasterRouteStatusItem item in items)
            RouteStatusItems.Add(item);
        RouteStatusText = RouteStatusItems.Count.ToString(CultureInfo.InvariantCulture) + " route surfaces tracked.";
    }

    public void UseSelectedServerForLocationLookup() {
        if (SelectedServer == null)
            return;
        LocationLookupTarget = RegisteredServer.FirstNonEmpty(SelectedServer.IpAddress, SelectedServer.ConnectHost, SelectedServer.Endpoint, SelectedServer.RegisteredByIp);
    }

    public async Task LookupLocationAsync() {
        try {
            string target = LocationLookupTarget;
            ServerLocationLookupResult result = await Task.Run(() => _host.LookupServerLocationForUi(target)).ConfigureAwait(true);
            LocationLookupStatus = result.Found
                ? RegisteredServer.FirstNonEmpty(result.Label, result.City + ", " + result.Region + ", " + result.Country, result.Ip) +
                  " | " + RegisteredServer.FirstNonEmpty(result.Isp, result.Source)
                : "Location unavailable: " + TrimForDisplay(result.Error, 180);
        } catch (Exception ex) {
            LocationLookupStatus = "Location lookup failed: " + TrimForDisplay(ex.Message, 180);
        }
    }

    public async Task SaveShellRelayAsync() {
        var relay = new MasterShellRelay {
            Id = SelectedShellRelay?.Id ?? "",
            Name = ShellName,
            PublicPort = ParsePort(ShellPublicPort, 18436),
            AgentPort = ParsePort(ShellAgentPort, 19436),
            TargetHost = "127.0.0.1",
            TargetPort = 11436,
            Enabled = SelectedShellEnabled
        };
        MasterShellRelay saved = await Task.Run(() => _host.UpsertShellRelay(relay)).ConfigureAwait(true);
        await RefreshShellRelaysAsync().ConfigureAwait(true);
        SelectedShellRelay = ShellRelays.FirstOrDefault(item => string.Equals(item.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DeleteSelectedShellRelayAsync() {
        if (SelectedShellRelay == null)
            return;
        string relayId = SelectedShellRelay.Id;
        await Task.Run(() => _host.RemoveShellRelay(relayId)).ConfigureAwait(true);
        await RefreshShellRelaysAsync().ConfigureAwait(true);
    }

    public async Task DeleteServerAsync(RegisteredServer server) {
        if (server == null || string.IsNullOrWhiteSpace(server.Id))
            return;

        string serverId = server.Id;
        await Task.Run(() => _host.Remove(serverId)).ConfigureAwait(true);
        await RefreshFromHostAsync().ConfigureAwait(true);
    }

    public static void ApplySavedPaymentEnvironment() {
        ApplyPaymentEnvironment(ReadPaymentConfigurationFromFile());
    }

    public async Task LoadPaymentConfigurationAsync() {
        PaymentConfig config = await Task.Run(() => NormalizePaymentConfig(ReadPaymentConfigurationFromFile())).ConfigureAwait(true);
        StripePublishableKey = config.StripePublishableKey;
        StripeSecretKey = config.StripeSecretKey;
        StripeWebhookSecret = config.StripeWebhookSecret;
        StripeWebhookUrl = config.StripeWebhookUrl;
        StripeConnectClientId = config.StripeConnectClientId;
        StripeConnectClientSecret = config.StripeConnectClientSecret;
        StripeConnectRedirectUrl = config.StripeConnectRedirectUrl;
        StripeCheckoutSuccessUrl = config.StripeCheckoutSuccessUrl;
        StripeCheckoutCancelUrl = config.StripeCheckoutCancelUrl;
        StripeTokenUsdPerToken = config.StripeTokenUsdPerToken;
        StripeServiceFeePercent = config.StripeServiceFeePercent;
        ReplaceStripeTokenProducts(config.StripeTokenProducts);
        ApplyPaymentEnvironment(config);
        PaymentConfigStatus = "Loaded payment config from " + GetAppSettingsPath();
    }

    public async Task SavePaymentConfigurationAsync() {
        PaymentConfig config = NormalizePaymentConfig(new PaymentConfig {
            StripePublishableKey = StripePublishableKey,
            StripeSecretKey = StripeSecretKey,
            StripeWebhookSecret = StripeWebhookSecret,
            StripeConnectClientId = StripeConnectClientId,
            StripeConnectClientSecret = StripeConnectClientSecret,
            StripeConnectRedirectUrl = StripeConnectRedirectUrl,
            StripeCheckoutSuccessUrl = StripeCheckoutSuccessUrl,
            StripeCheckoutCancelUrl = StripeCheckoutCancelUrl,
            StripeTokenUsdPerToken = StripeTokenUsdPerToken,
            StripeServiceFeePercent = StripeServiceFeePercent,
            StripeTokenProducts = StripeTokenProducts.Select(product => product.Clone()).ToList()
        });

        await Task.Run(() => WritePaymentConfigurationToFile(config)).ConfigureAwait(true);
        ApplyPaymentEnvironment(config);
        await LoadPaymentConfigurationAsync().ConfigureAwait(true);
        PaymentConfigStatus = "Saved payment config to " + GetAppSettingsPath() + " and persisted Stripe keys to the user environment. Restart the listener to reload website startup options.";
    }

    public void ResetStripeTokenProducts() {
        ReplaceStripeTokenProducts(StripeTokenProductCatalog.CreateDefaultProducts());
        PaymentConfigStatus = "Reset token products from the Stripe CSV export. Save to persist the changes.";
    }

    public async Task MarkServerOfflineAsync(RegisteredServer server) {
        if (server == null || string.IsNullOrWhiteSpace(server.Id))
            return;

        string serverId = server.Id;
        await Task.Run(() => _host.MarkOffline(serverId)).ConfigureAwait(true);
        await RefreshFromHostAsync().ConfigureAwait(true);
    }

    public void RefreshStatus() {
        string state = _host.IsApiListening && _host.IsWebsiteListening
            ? "Listening"
            : _host.IsApiListening
                ? "API only"
                : _host.IsWebsiteListening
                    ? "Website only"
                    : "Stopped";
        StatusText = state + " - " + _host.LastStatus;
        CanStart = !_host.IsListening;
        CanStop = _host.IsListening;
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(PublicUrlText));
        OnPropertyChanged(nameof(DataPathText));
        OnPropertyChanged(nameof(DatabaseText));
        OnPropertyChanged(nameof(SqlAdminText));
    }

    private bool FilterServer(object item) {
        return item is RegisteredServer server && server.MatchesSearch(SearchText);
    }

    private void RefreshCounts() {
        int total = Servers.Count;
        int filtered = ServersView.Cast<object>().Count();
        ServerCountText = total == 1 ? "1 server" : total.ToString() + " servers";
        LastUpdatedText = "Updated " + DateTime.Now.ToString("g");
        FilterText = string.IsNullOrWhiteSpace(SearchText)
            ? total.ToString() + " total"
            : filtered.ToString() + " of " + total.ToString() + " shown";
        EmptyStateVisibility = filtered == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateDetail = total == 0
            ? "Waiting for POST /api/lmvsproxy/servers registrations."
            : "No servers match the current search.";
    }

    private void ApplySelectedShellToEditor() {
        if (SelectedShellRelay == null)
            return;

        ShellName = SelectedShellRelay.Name;
        ShellPublicPort = SelectedShellRelay.PublicPort.ToString();
        ShellAgentPort = SelectedShellRelay.AgentPort.ToString();
        ShellTargetHost = SelectedShellRelay.TargetHost;
        ShellTargetPort = SelectedShellRelay.TargetPort.ToString();
    }

    private void ApplySelectedAccountToEditor() {
        if (SelectedAccount == null) {
            SelectedAccountEnabled = false;
            SelectedAccountAdministrator = false;
            SelectedAccountAutoPublish = false;
            SelectedAccountTokenLimit = "0";
            SelectedAccountTokenDelta = "0";
            OnPropertyChanged(nameof(SelectedAccountUsageText));
            return;
        }

        SelectedAccountEnabled = SelectedAccount.Enabled;
        SelectedAccountAdministrator = SelectedAccount.IsAdministrator;
        SelectedAccountAutoPublish = SelectedAccount.AutoPublish;
        SelectedAccountTokenLimit = SelectedAccount.TokenLimit.ToString(CultureInfo.InvariantCulture);
        SelectedAccountTokenDelta = "0";
        OnPropertyChanged(nameof(SelectedAccountUsageText));
    }

    private static int ParsePort(string value, int fallback) {
        return int.TryParse(value, out int port)
            ? Math.Clamp(port, 1, 65535)
            : fallback;
    }

    private static long ParseLong(string value, long fallback) {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? Math.Max(0, parsed)
            : fallback;
    }

    private static string BuildRuntimeReportDetailText(SocketJackRuntimeReport? report) {
        if (report == null)
            return "Select a runtime report to inspect its details.";

        return "Severity: " + report.Severity + Environment.NewLine +
               "Category: " + report.Category + Environment.NewLine +
               "Created: " + report.CreatedUtc + Environment.NewLine +
               "Provider: " + report.PrimaryProvider + " -> " + report.FallbackProvider + Environment.NewLine +
               "Operation: " + report.Operation + Environment.NewLine +
               "Server: " + RegisteredServer.FirstNonEmpty(report.ServerName, report.ServerId, report.PublicHost) + Environment.NewLine +
               "User: " + RegisteredServer.FirstNonEmpty(report.AuthenticatedUserName, report.ReportedByUserName, report.OwnerUserName) + Environment.NewLine +
               "Reason: " + report.Reason + Environment.NewLine + Environment.NewLine +
               report.Detail + Environment.NewLine + Environment.NewLine +
               report.ExtraJson;
    }

    private static string BuildMasterLogDetailText(SocketJackMasterLogEntry? log) {
        if (log == null)
            return "Select a log entry to inspect its details.";

        return "Level: " + log.Level + Environment.NewLine +
               "Category: " + log.Category + Environment.NewLine +
               "Created: " + log.CreatedUtc + Environment.NewLine +
               "User: " + log.UserName + Environment.NewLine +
               "Client IP: " + log.ClientIp + Environment.NewLine +
               "Path: " + log.Path + Environment.NewLine +
               "Message: " + log.Message + Environment.NewLine + Environment.NewLine +
               log.Detail;
    }

    private static string TrimForDisplay(string value, int maxLength) {
        value ??= "";
        if (maxLength <= 0 || value.Length <= maxLength)
            return value;
        return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
    }

    private static PaymentConfig ReadPaymentConfigurationFromFile() {
        JsonObject root = LoadAppSettingsRoot();
        JsonObject? section = root["SocketJackMagicMasterList"] as JsonObject;
        string websitePublicUrl = ResolveWebsitePublicUrl(section);
        return NormalizePaymentConfig(new PaymentConfig {
            StripePublishableKey = FirstNonEmpty(GetJsonText(section, "StripePublishableKey"), ReadEnvironment("STRIPE_PUBLISHABLE_KEY")),
            StripeSecretKey = FirstNonEmpty(GetJsonText(section, "StripeSecretKey"), ReadEnvironment("STRIPE_RESTRICTED_KEY"), ReadEnvironment("STRIPE_SECRET_KEY")),
            StripeWebhookSecret = FirstNonEmpty(GetJsonText(section, "StripeWebhookSecret"), ReadEnvironment("STRIPE_WEBHOOK_SECRET")),
            StripeWebhookUrl = BuildStripeWebhookUrl(websitePublicUrl),
            StripeConnectClientId = FirstNonEmpty(GetJsonText(section, "StripeConnectClientId"), ReadEnvironment("STRIPE_CONNECT_CLIENT_ID")),
            StripeConnectClientSecret = FirstNonEmpty(GetJsonText(section, "StripeConnectClientSecret"), ReadEnvironment("STRIPE_CONNECT_CLIENT_SECRET")),
            StripeConnectRedirectUrl = FirstNonEmpty(GetJsonText(section, "StripeConnectRedirectUrl"), ReadEnvironment("STRIPE_CONNECT_REDIRECT_URL"), BuildStripeConnectRedirectUrl(websitePublicUrl)),
            StripeCheckoutSuccessUrl = FirstNonEmpty(GetJsonText(section, "StripeCheckoutSuccessUrl"), ReadEnvironment("STRIPE_CHECKOUT_SUCCESS_URL"), BuildCheckoutSuccessUrl(websitePublicUrl)),
            StripeCheckoutCancelUrl = FirstNonEmpty(GetJsonText(section, "StripeCheckoutCancelUrl"), ReadEnvironment("STRIPE_CHECKOUT_CANCEL_URL"), BuildCheckoutCancelUrl(websitePublicUrl)),
            StripeTokenUsdPerToken = FirstNonEmpty(GetJsonText(section, "StripeTokenUsdPerToken"), ReadEnvironment("SOCKETJACK_TOKEN_USD_PER_TOKEN"), "0.00005"),
            StripeServiceFeePercent = FirstNonEmpty(GetJsonText(section, "StripeServiceFeePercent"), ReadEnvironment("SOCKETJACK_STRIPE_SERVICE_FEE_PERCENT"), "1.5"),
            StripeTokenProducts = ReadStripeTokenProducts(section)
        });
    }

    private static void WritePaymentConfigurationToFile(PaymentConfig config) {
        JsonObject root = LoadAppSettingsRoot();
        JsonObject section = root["SocketJackMagicMasterList"] as JsonObject ?? new JsonObject();
        root["SocketJackMagicMasterList"] = section;
        section["StripePublishableKey"] = config.StripePublishableKey ?? "";
        section["StripeConnectClientId"] = config.StripeConnectClientId ?? "";
        section["StripeConnectRedirectUrl"] = config.StripeConnectRedirectUrl ?? "";
        section["StripeCheckoutSuccessUrl"] = config.StripeCheckoutSuccessUrl ?? "";
        section["StripeCheckoutCancelUrl"] = config.StripeCheckoutCancelUrl ?? "";
        section["StripeTokenUsdPerToken"] = config.StripeTokenUsdPerToken ?? "0.00005";
        section["StripeServiceFeePercent"] = config.StripeServiceFeePercent ?? "1.5";
        section["StripeTokenProducts"] = JsonSerializer.SerializeToNode(StripeTokenProductCatalog.Normalize(config.StripeTokenProducts), new JsonSerializerOptions { WriteIndented = true });
        section.Remove("StripeSecretKey");
        section.Remove("StripeWebhookSecret");
        section.Remove("StripeConnectClientSecret");

        string path = GetAppSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        PersistPaymentEnvironment(config);
    }

    private static JsonObject LoadAppSettingsRoot() {
        string path = GetAppSettingsPath();
        if (!File.Exists(path))
            return new JsonObject();

        try {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            return node as JsonObject ?? new JsonObject();
        } catch {
            return new JsonObject();
        }
    }

    private static string GetAppSettingsPath() {
        string runtimePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(runtimePath))
            return runtimePath;
        string sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "appsettings.json"));
        return File.Exists(sourcePath) ? sourcePath : runtimePath;
    }

    private static PaymentConfig NormalizePaymentConfig(PaymentConfig config) {
        config ??= new PaymentConfig();
        config.StripeTokenUsdPerToken = NormalizeDecimalText(config.StripeTokenUsdPerToken, 0.00005, 0);
        config.StripeServiceFeePercent = NormalizeDecimalText(config.StripeServiceFeePercent, 1.5, 1.5);
        config.StripeTokenProducts = StripeTokenProductCatalog.Normalize(config.StripeTokenProducts);
        return config;
    }

    private static string NormalizeDecimalText(string value, double fallback, double minimum) {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            parsed = fallback;
        parsed = Math.Max(minimum, parsed);
        return parsed.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static void ApplyPaymentEnvironment(PaymentConfig config) {
        config = NormalizePaymentConfig(config);
        SetProcessEnvironment("STRIPE_PUBLISHABLE_KEY", config.StripePublishableKey);
        SetProcessEnvironment("STRIPE_RESTRICTED_KEY", config.StripeSecretKey);
        SetProcessEnvironment("STRIPE_SECRET_KEY", config.StripeSecretKey);
        SetProcessEnvironment("STRIPE_WEBHOOK_SECRET", config.StripeWebhookSecret);
        SetProcessEnvironment("STRIPE_CONNECT_CLIENT_ID", config.StripeConnectClientId);
        SetProcessEnvironment("STRIPE_CONNECT_CLIENT_SECRET", config.StripeConnectClientSecret);
        SetProcessEnvironment("STRIPE_CONNECT_REDIRECT_URL", config.StripeConnectRedirectUrl);
        SetProcessEnvironment("STRIPE_CHECKOUT_SUCCESS_URL", config.StripeCheckoutSuccessUrl);
        SetProcessEnvironment("STRIPE_CHECKOUT_CANCEL_URL", config.StripeCheckoutCancelUrl);
        SetProcessEnvironment("SOCKETJACK_TOKEN_USD_PER_TOKEN", config.StripeTokenUsdPerToken);
        SetProcessEnvironment("SOCKETJACK_STRIPE_SERVICE_FEE_PERCENT", config.StripeServiceFeePercent);
    }

    private static void PersistPaymentEnvironment(PaymentConfig config) {
        config = NormalizePaymentConfig(config);
        SetUserEnvironment("STRIPE_PUBLISHABLE_KEY", config.StripePublishableKey);
        SetUserEnvironment("STRIPE_RESTRICTED_KEY", config.StripeSecretKey);
        SetUserEnvironment("STRIPE_SECRET_KEY", config.StripeSecretKey);
        SetUserEnvironment("STRIPE_WEBHOOK_SECRET", config.StripeWebhookSecret);
        SetUserEnvironment("STRIPE_CONNECT_CLIENT_ID", config.StripeConnectClientId);
        SetUserEnvironment("STRIPE_CONNECT_CLIENT_SECRET", config.StripeConnectClientSecret);
        SetUserEnvironment("STRIPE_CONNECT_REDIRECT_URL", config.StripeConnectRedirectUrl);
        SetUserEnvironment("STRIPE_CHECKOUT_SUCCESS_URL", config.StripeCheckoutSuccessUrl);
        SetUserEnvironment("STRIPE_CHECKOUT_CANCEL_URL", config.StripeCheckoutCancelUrl);
        SetUserEnvironment("SOCKETJACK_TOKEN_USD_PER_TOKEN", config.StripeTokenUsdPerToken);
        SetUserEnvironment("SOCKETJACK_STRIPE_SERVICE_FEE_PERCENT", config.StripeServiceFeePercent);
    }

    private void ReplaceStripeTokenProducts(IEnumerable<StripeTokenProductConfig> products) {
        StripeTokenProducts.Clear();
        foreach (StripeTokenProductConfig product in StripeTokenProductCatalog.Normalize(products))
            StripeTokenProducts.Add(product);
    }

    private static List<StripeTokenProductConfig> ReadStripeTokenProducts(JsonObject? section) {
        if (section == null ||
            !section.TryGetPropertyValue("StripeTokenProducts", out JsonNode? node) ||
            node == null)
            return StripeTokenProductCatalog.CreateDefaultProducts().Select(product => product.Clone()).ToList();

        try {
            return StripeTokenProductCatalog.Normalize(
                JsonSerializer.Deserialize<List<StripeTokenProductConfig>>(
                    node.ToJsonString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []);
        } catch {
            return StripeTokenProductCatalog.CreateDefaultProducts().Select(product => product.Clone()).ToList();
        }
    }

    private static void SetProcessEnvironment(string name, string value) {
        Environment.SetEnvironmentVariable(
            name,
            string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
            EnvironmentVariableTarget.Process);
    }

    private static void SetUserEnvironment(string name, string value) {
        string? nextValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Environment.SetEnvironmentVariable(name, nextValue, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, nextValue, EnvironmentVariableTarget.Process);
    }

    private static string ReadEnvironment(string name) {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine));
    }

    private static string ResolveWebsitePublicUrl(JsonObject? section) {
        return NormalizePublicBaseUrl(FirstNonEmpty(
            GetJsonText(section, "WebsitePublicUrl"),
            GetJsonText(section, "PublicUrl"),
            ReadEnvironment("SOCKETJACK_MAGIC_MASTER_LIST_WEBSITE_PUBLIC_URL"),
            ReadEnvironment("SOCKETJACK_MAGIC_MASTER_LIST_PUBLIC_URL"),
            "https://socketjack.com/"));
    }

    private static string BuildStripeConnectRedirectUrl(string websitePublicUrl) {
        return CombinePublicUrl(websitePublicUrl, "api/socketjack/stripe/connect/redirect");
    }

    private static string BuildStripeWebhookUrl(string websitePublicUrl) {
        return CombinePublicUrl(websitePublicUrl, "api/socketjack/stripe/webhook");
    }

    private static string BuildCheckoutSuccessUrl(string websitePublicUrl) {
        return CombinePublicUrl(websitePublicUrl, "") + "?checkout=success&session_id={CHECKOUT_SESSION_ID}";
    }

    private static string BuildCheckoutCancelUrl(string websitePublicUrl) {
        return CombinePublicUrl(websitePublicUrl, "") + "?checkout=cancel";
    }

    private static string CombinePublicUrl(string baseUrl, string relativePath) {
        if (!Uri.TryCreate(NormalizePublicBaseUrl(baseUrl), UriKind.Absolute, out Uri? rootUri))
            rootUri = new Uri("https://socketjack.com/");
        return new Uri(rootUri, (relativePath ?? "").TrimStart('/')).ToString();
    }

    private static string NormalizePublicBaseUrl(string value) {
        string candidate = FirstNonEmpty(value, "https://socketjack.com/");
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            uri = new Uri("https://socketjack.com/");

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
        string path = uri.AbsolutePath.Trim('/');
        builder.Path = string.IsNullOrWhiteSpace(path) ? "/" : path + "/";
        builder.Query = "";
        builder.Fragment = "";
        return builder.Uri.ToString();
    }

    private static string GetJsonText(JsonObject? section, string name) {
        if (section == null || !section.TryGetPropertyValue(name, out JsonNode? node) || node == null)
            return "";
        return node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>() ?? ""
            : node.ToJsonString().Trim('"');
    }

    private static string FirstNonEmpty(params string?[] values) {
        foreach (string? value in values) {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private sealed class PaymentConfig {
        public string StripePublishableKey { get; set; } = "";
        public string StripeSecretKey { get; set; } = "";
        public string StripeWebhookSecret { get; set; } = "";
        public string StripeWebhookUrl { get; set; } = "";
        public string StripeConnectClientId { get; set; } = "";
        public string StripeConnectClientSecret { get; set; } = "";
        public string StripeConnectRedirectUrl { get; set; } = "";
        public string StripeCheckoutSuccessUrl { get; set; } = "";
        public string StripeCheckoutCancelUrl { get; set; } = "";
        public string StripeTokenUsdPerToken { get; set; } = "0.00005";
        public string StripeServiceFeePercent { get; set; } = "1.5";
        public List<StripeTokenProductConfig> StripeTokenProducts { get; set; } = new();
    }

    private void Set<T>(ref T field, T value, string propertyName) {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
