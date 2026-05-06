using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using LmVs;
using SocketJack.Net;
using SocketJack.WPF.Controller;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;

namespace LmVsProxyGUI;

public partial class MainWindow : Window {
    private const int ServerPort = 11434;
    private const int ChatServerPort = 11436;
    private const int FtpServerPort = 2121;
    private const int LocalLmStudioProxyPort = 11435;
    private const string WindowsStartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsStartupValueName = "LmVsProxyGUI";
    private const string LmStudioProcessName = "LM Studio";
    private const string LmStudioExecutableName = "LM Studio.exe";
    private const string LmStudioFolderName = "LM Studio";
    private static readonly int[] ForwardedTcpPorts = { ChatServerPort, FtpServerPort };

    private readonly SocketJack.Net.LmVsProxy _proxy;
    private readonly DispatcherTimer _metricsTimer;
    private readonly ObservableCollection<ChatSessionListItem> _sessionItems = new();
    private readonly ObservableCollection<ChatSessionListItem> _storedSessionItems = new();
    private readonly ObservableCollection<AccessibleDirectoryListItem> _accessibleDirectoryItems = new();
    private readonly ObservableCollection<FileTreeItem> _workspaceTreeItems = new();
    private readonly ObservableCollection<ServiceMetricItem> _serviceMetricItems = new();
    private readonly ObservableCollection<PipelineNodeItem> _pipelineItems = new();
    private readonly ObservableCollection<FilesystemPermissionRequestItem> _filesystemPermissionRequests = new();
    private readonly ObservableCollection<TerminalPermissionRequestItem> _terminalPermissionRequests = new();
    private readonly Dictionary<string, Window> _terminalPermissionToasts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> _registrationRequestToasts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _servicePulseUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly BandwidthMetricTracker _chatBandwidthInTracker = new("Inbound");
    private readonly BandwidthMetricTracker _chatBandwidthOutTracker = new("Outbound");
    private readonly BandwidthMetricTracker _networkUpTracker = new("Network Up");
    private readonly BandwidthMetricTracker _networkDownTracker = new("Network Down");
    private readonly RateMetricTracker _ioTracker = new("I/O");
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "LmVsProxyGUI.settings.json");
    private readonly bool _startHiddenRequested;
    private LmVsProxyGuiSettings _settings = new();
    private Forms.NotifyIcon? _trayIcon;
    private TcpDuplicator? _remoteLmStudioProxy;
    private DateTimeOffset? _startedAt;
    private long _logLines;
    private long _errorLines;
    private long _requestCount;
    private long _responsesBridgeCount;
    private long _toolCallCount;
    private long _streamCount;
    private long _chatUiEventCount;
    private long _lmStudioEventCount;
    private long _agentServiceEventCount;
    private long _internetSearchEventCount;
    private long _reflectionServiceEventCount;
    private long _promptIntellisenseEventCount;
    private long _sessionFileEventCount;
    private long _terminalServiceEventCount;
    private long _ftpEventCount;
    private long _portForwardingEventCount;
    private bool _isStopping;
    private bool _allowExit;
    private bool _isProbing;
    private bool _uiReady;
    private bool _isApplyingSettings;
    private bool _portForwardingEnabled = true;
    private bool _portForwardingEventsRegistered;
    private bool _portForwardingInProgress;
    private bool _portForwardingDiscoveryStarted;
    private bool _portForwardingAttempted;
    private bool _portForwardingSucceeded;
    private string _sessionListSignature = "";
    private string _storedSessionListSignature = "";
    private string _workspaceTreeSignature = "";
    private string _servicePanelSignature = "";
    private string _pipelineSignature = "";
    private string _selectedSessionId = "";
    private string _selectedOwnerKey = "";
    private string _accessibleDirectorySignature = "";
    private string _activePipelineKind = "idle";
    private string _lmStatusText = "Not checked";
    private string _lmLatencyText = "Latency: -";
    private Brush _lmStatusBrush = Brushes.SlateGray;
    private string _lmEndpointText = "";
    private string _gpuTdpText = "GPU TDP: not detected";
    private string _proxyEndpointText = "";
    private string _chatUiEndpointText = "";
    private string _vsEndpointText = "";
    private string _lastEventText = "Last event: -";
    private DateTimeOffset _lastWorkspaceRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastChatBandwidthSampleUtc;
    private DateTimeOffset? _lastNetworkSampleUtc;
    private DateTimeOffset? _lastIoSampleUtc;
    private DateTimeOffset _lastGpuHealthSampleUtc = DateTimeOffset.MinValue;
    private long _lastChatBandwidthBytesIn;
    private long _lastChatBandwidthBytesOut;
    private long _lastNetworkBytesUp;
    private long _lastNetworkBytesDown;
    private ulong _lastIoReadBytes;
    private ulong _lastIoWriteBytes;
    private ulong? _lastCpuIdleTicks;
    private ulong? _lastCpuKernelTicks;
    private ulong? _lastCpuUserTicks;
    private BandwidthMetricSnapshot _chatBandwidthInSnapshot = BandwidthMetricSnapshot.Empty("Inbound");
    private BandwidthMetricSnapshot _chatBandwidthOutSnapshot = BandwidthMetricSnapshot.Empty("Outbound");
    private BandwidthMetricSnapshot _networkUpSnapshot = BandwidthMetricSnapshot.Empty("Network Up");
    private BandwidthMetricSnapshot _networkDownSnapshot = BandwidthMetricSnapshot.Empty("Network Down");
    private RateMetricSnapshot _ioSnapshot = RateMetricSnapshot.Empty("I/O");
    private double _ioReadBytesPerSecond;
    private double _ioWriteBytesPerSecond;
    private double _gpuUtilizationPercent;
    private double _vramUsagePercent;
    private double _ramUsagePercent;
    private double _cpuUsagePercent;
    private string _gpuUtilizationHealthText = "GPU Utilization: unavailable";
    private string _vramUsageHealthText = "VRAM Usage: unavailable";

    public MainWindow(bool startHiddenRequested = false) {
        _startHiddenRequested = startHiddenRequested;
        InitializeComponent();
        if (_startHiddenRequested) {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }

        _proxy = new SocketJack.Net.LmVsProxy("localhost", LocalLmStudioProxyPort, ServerPort, ChatServerPort) {
            PromptTimeout = TimeSpan.FromMinutes(30)
        };
        _proxy.OutputLog += OnProxyOutputLog;
        _proxy.FilesystemPermissionRequested += OnFilesystemPermissionRequested;
        _proxy.TerminalPermissionRequested += OnTerminalPermissionRequested;
        _proxy.WebAuthRegistrationRequested += OnWebAuthRegistrationRequested;

        _metricsTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(1)
        };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();

        SessionsListBox.ItemsSource = _sessionItems;
        AccessibleDirectoriesListBox.ItemsSource = _accessibleDirectoryItems;
        WorkspaceTreeView.ItemsSource = _workspaceTreeItems;
        ServicesItemsControl.ItemsSource = _serviceMetricItems;
        PipelineItemsControl.ItemsSource = _pipelineItems;
        FilesystemPermissionRequestsControl.ItemsSource = _filesystemPermissionRequests;
        TerminalPermissionRequestsControl.ItemsSource = _terminalPermissionRequests;
        InitializeStaticText();
        _settings = LoadSettings();
        ApplySettingsToUi(_settings);
        ApplyStorageSettingsToProxy(_settings);
        ApplyOpenRegistrationSettingToProxy(_settings);
        ApplyDetectedGpuTdpToProxy();
        _portForwardingEnabled = PortForwardingCheckBox.IsChecked.GetValueOrDefault(true);
        _uiReady = true;
        RefreshMetrics();
        LoadPendingRegistrationRequests();
        UpdateWindowChromeState();
        UpdateResponsiveLayout();
    }

    public void ShutdownProxy() {
        if (_isStopping)
            return;

        _isStopping = true;
        try {
            SaveSettings();
            UnregisterPortForwardingEvents();
            foreach (Window toast in _terminalPermissionToasts.Values.ToList()) {
                try { toast.Close(); } catch { }
            }
            _terminalPermissionToasts.Clear();
            foreach (Window toast in _registrationRequestToasts.Values.ToList()) {
                try { toast.Close(); } catch { }
            }
            _registrationRequestToasts.Clear();
            StopProxy();
            DisposeTrayIcon();
        } finally {
            _isStopping = false;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) {
        LmVsProxyWpfRemoteControl.RegisterAdminPanel(this);
        _metricsTimer.Start();
        RefreshSessionsPanel(true);
        RefreshWorkspaceExplorer(true);
        if (StartByDefaultCheckBox.IsChecked.GetValueOrDefault(true)) {
            AppendLog("Start by default enabled. Starting LmVsProxy.");
            _ = StartProxyAsync();
        } else {
            BeginPortForwardingIfEnabled("startup");
            _ = ProbeLmStudioAsync();
        }
        EnsureTrayIcon();
        Dispatcher.BeginInvoke(new Action(UpdateResponsiveLayout), DispatcherPriority.Loaded);
        if (_startHiddenRequested)
            Dispatcher.BeginInvoke(new Action(HideToTray), DispatcherPriority.ApplicationIdle);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (!_allowExit && _trayIcon != null) {
            e.Cancel = true;
            HideToTray();
            return;
        }

        SaveSettings();
        ShutdownProxy();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        EnableAeroBlur();
    }

    private void Window_StateChanged(object? sender, EventArgs e) {
        UpdateWindowChromeState();
        UpdateResponsiveLayout();
        if (WindowState != WindowState.Minimized)
            EnableAeroBlur();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout() {
        if (SettingsGroupsPanel == null || MainWorkspaceGrid == null)
            return;

        double viewportWidth = MainScrollViewer?.ViewportWidth > 0
            ? MainScrollViewer.ViewportWidth
            : Math.Max(0, ActualWidth - 28);
        if (double.IsNaN(viewportWidth) || double.IsInfinity(viewportWidth) || viewportWidth <= 0)
            return;

        Thickness scrollPadding = MainScrollViewer?.Padding ?? new Thickness(0);
        double contentWidth = Math.Max(320, viewportWidth - scrollPadding.Left - scrollPadding.Right);
        double settingsWidth = SettingsGroupsPanel.ActualWidth > 0
            ? SettingsGroupsPanel.ActualWidth
            : Math.Max(280, contentWidth - 22);
        int settingsColumns = settingsWidth >= 1180 ? 4 : settingsWidth >= 880 ? 3 : settingsWidth >= 560 ? 2 : 1;
        double settingsGutter = settingsColumns > 1 ? 10 * (settingsColumns - 1) : 0;
        SettingsGroupsPanel.ItemWidth = Math.Max(240, Math.Floor((settingsWidth - settingsGutter) / settingsColumns));

        bool stackedWorkspace = contentWidth < 1040;
        if (stackedWorkspace) {
            SidebarColumn.Width = new GridLength(1, GridUnitType.Star);
            DiagnosticsColumn.Width = new GridLength(0);
            MainWorkspacePrimaryRow.Height = GridLength.Auto;
            MainWorkspaceStackedRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SidebarPanel, 0);
            Grid.SetColumn(SidebarPanel, 0);
            Grid.SetColumnSpan(SidebarPanel, 2);
            SidebarPanel.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(DiagnosticsPanel, 1);
            Grid.SetColumn(DiagnosticsPanel, 0);
            Grid.SetColumnSpan(DiagnosticsPanel, 2);
        } else {
            SidebarColumn.Width = new GridLength(contentWidth >= 1320 ? 320 : 300);
            DiagnosticsColumn.Width = new GridLength(1, GridUnitType.Star);
            MainWorkspacePrimaryRow.Height = new GridLength(1, GridUnitType.Star);
            MainWorkspaceStackedRow.Height = new GridLength(0);
            Grid.SetRow(SidebarPanel, 0);
            Grid.SetColumn(SidebarPanel, 0);
            Grid.SetColumnSpan(SidebarPanel, 1);
            SidebarPanel.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRow(DiagnosticsPanel, 0);
            Grid.SetColumn(DiagnosticsPanel, 1);
            Grid.SetColumnSpan(DiagnosticsPanel, 1);
        }

        double servicesWidth = DiagnosticsPanel.ActualWidth > 0
            ? DiagnosticsPanel.ActualWidth
            : Math.Max(320, contentWidth - SidebarColumn.ActualWidth - 16);
        if (FindVisualChild<UniformGrid>(ServicesItemsControl) is UniformGrid servicesUniformGrid)
            servicesUniformGrid.Columns = servicesWidth >= 940 ? 4 : servicesWidth >= 700 ? 3 : servicesWidth >= 460 ? 2 : 1;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        if (parent == null)
            return null;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            T? descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        Close();
    }

    private void UpdateWindowChromeState() {
        if (WindowFrame == null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(6);
        WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(1);

        WindowChrome? chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null)
            chrome.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(6);
        ApplyNativeCornerPreference(maximized);

        if (MaximizeGlyph != null)
            MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        if (RestoreGlyph != null)
            RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        if (MaximizeRestoreButton != null)
            MaximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WmGetMinMaxInfo) {
            ApplyMaximizedWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void EnableAeroBlur() {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        if (HwndSource.FromHwnd(handle) is HwndSource source && source.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;

        if (!TryEnableAccentBlur(handle))
            TryEnableDwmBlur(handle);
        ApplyNativeCornerPreference(WindowState == WindowState.Maximized);
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e) {
        if (_proxy.IsListening) {
            StopProxy();
            return;
        }

        await StartProxyAsync();
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e) {
        await ProbeLmStudioAsync();
    }

    private void OpenChatButton_Click(object sender, RoutedEventArgs e) {
        if (!_proxy.IsChatServerCreated || !_proxy.ChatServer.IsListening) {
            SetStatus("Chat UI is not running.");
            return;
        }

        Process.Start(new ProcessStartInfo(_proxy.ChatServerUrl) {
            UseShellExecute = true
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) {
        LogTextBox.Clear();
        _logLines = 0;
        _errorLines = 0;
        _requestCount = 0;
        _responsesBridgeCount = 0;
        _toolCallCount = 0;
        _streamCount = 0;
        _chatUiEventCount = 0;
        _lmStudioEventCount = 0;
        _agentServiceEventCount = 0;
        _internetSearchEventCount = 0;
        _reflectionServiceEventCount = 0;
        _promptIntellisenseEventCount = 0;
        _sessionFileEventCount = 0;
        _ftpEventCount = 0;
        _portForwardingEventCount = 0;
        _servicePulseUtc.Clear();
        ResetChatBandwidthMetrics();
        _activePipelineKind = "idle";
        _lastEventText = "Last event: -";
        RefreshMetrics();
    }

    private void RefreshSessionsButton_Click(object sender, RoutedEventArgs e) {
        RefreshSessionsPanel(true);
        RefreshStoredSessionsMenuItems(true);
        RefreshAccessibleDirectories(true);
        RefreshWorkspaceExplorer(true);
    }
    private static readonly Brush ContextMenuTextBrush = Brushes.Black;

    private static ContextMenu CreateReadableContextMenu() {
        return new ContextMenu {
            Background = Brushes.White,
            Foreground = ContextMenuTextBrush
        };
    }

    private static MenuItem CreateReadableMenuItem(string header, object? tag = null, bool isEnabled = true, string? toolTip = null) {
        var menuItem = new MenuItem {
            Header = new TextBlock {
                Text = header,
                Foreground = ContextMenuTextBrush
            },
            Foreground = ContextMenuTextBrush,
            IsEnabled = isEnabled
        };

        if (tag != null)
            menuItem.Tag = tag;
        if (!string.IsNullOrEmpty(toolTip))
            menuItem.ToolTip = toolTip;

        return menuItem;
    }

    private void StoredSessionsMenuButton_Click(object sender, RoutedEventArgs e) {
        RefreshStoredSessionsMenuItems(true);

        var menu = CreateReadableContextMenu();
        if (_storedSessionItems.Count == 0) {
            menu.Items.Add(CreateReadableMenuItem("No stored database sessions", isEnabled: false));
        } else {
            foreach (ChatSessionListItem item in _storedSessionItems) {
                var menuItem = CreateReadableMenuItem(
                    item.Title,
                    item,
                    toolTip: item.OwnerLine + Environment.NewLine + item.DetailLine + Environment.NewLine + item.UpdatedLine);
                var selectItem = CreateReadableMenuItem("Use for explorer", item);
                selectItem.Click += StoredSessionMenuItem_Click;
                menuItem.Items.Add(selectItem);
                menuItem.Items.Add(new Separator());
                menuItem.Items.Add(BuildRestrictionMenu("Mute", item, "mute"));
                menuItem.Items.Add(BuildRestrictionMenu("Ban", item, "ban"));
                menuItem.Items.Add(new Separator());
                menuItem.Items.Add(BuildAdminActionItem("Enable", item, () => {
                    string ownerKey = ResolveSessionOwnerKey(item);
                    ChatClientPermissionSnapshot snapshot = _proxy.EnableChatClient(ownerKey);
                    AppendLog("Enabled chat client permissions for " + snapshot.OwnerKey + ".");
                }));
                menuItem.Items.Add(BuildAdminActionItem("Edit permissions", item, () => ShowClientPermissionsDialog(item)));
                menu.Items.Add(menuItem);
            }
        }

        StoredSessionsMenuButton.ContextMenu = menu;
        menu.PlacementTarget = StoredSessionsMenuButton;
        menu.IsOpen = true;
    }

    private void StoredSessionMenuItem_Click(object sender, RoutedEventArgs e) {
        if ((sender as MenuItem)?.Tag is not ChatSessionListItem item)
            return;

        _selectedSessionId = item.Id;
        _selectedOwnerKey = item.OwnerKey;
        SessionsSummaryText.Text = "Filesystem owner switched to stored session: " + item.Title;
        _accessibleDirectorySignature = "";
        _workspaceTreeSignature = "";
        RefreshAccessibleDirectories(true);
        RefreshWorkspaceExplorer(true);
    }

    private void SessionAdminButton_Click(object sender, RoutedEventArgs e) {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not ChatSessionListItem item)
            return;

        string ownerKey = ResolveSessionOwnerKey(item);
        var menu = CreateReadableContextMenu();
        menu.Items.Add(CreateReadableMenuItem(
            string.IsNullOrWhiteSpace(ownerKey) ? "Owner unknown" : ownerKey,
            isEnabled: false));
        menu.Items.Add(BuildRestrictionMenu("Mute", item, "mute"));
        menu.Items.Add(BuildRestrictionMenu("Ban", item, "ban"));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildAdminActionItem("Enable", item, () => {
            ChatClientPermissionSnapshot snapshot = _proxy.EnableChatClient(ownerKey);
            AppendLog("Enabled chat client permissions for " + snapshot.OwnerKey + ".");
            RefreshSessionsPanel(true);
        }));
        menu.Items.Add(BuildAdminActionItem("Edit permissions", item, () => ShowClientPermissionsDialog(item)));

        if (sender is Button button) {
            button.ContextMenu = menu;
            menu.PlacementTarget = button;
        }
        menu.IsOpen = true;
    }

    private MenuItem BuildRestrictionMenu(string header, ChatSessionListItem item, string restriction) {
        var menu = CreateReadableMenuItem(header);
        menu.Items.Add(BuildRestrictionActionItem(header + " 10 minutes", item, restriction, TimeSpan.FromMinutes(10)));
        menu.Items.Add(BuildRestrictionActionItem(header + " 30 minutes", item, restriction, TimeSpan.FromMinutes(30)));
        menu.Items.Add(BuildRestrictionActionItem(header + " 60 minutes", item, restriction, TimeSpan.FromMinutes(60)));
        menu.Items.Add(BuildRestrictionActionItem(header + " 2 hours", item, restriction, TimeSpan.FromHours(2)));
        menu.Items.Add(BuildRestrictionActionItem(header + " 6 hours", item, restriction, TimeSpan.FromHours(6)));
        menu.Items.Add(BuildRestrictionActionItem(header + " until enabled", item, restriction, null));
        return menu;
    }

    private MenuItem BuildRestrictionActionItem(string header, ChatSessionListItem item, string restriction, TimeSpan? duration) {
        return BuildAdminActionItem(header, item, () => {
            string ownerKey = ResolveSessionOwnerKey(item);
            ChatClientPermissionSnapshot snapshot = _proxy.RestrictChatClient(ownerKey, restriction, duration);
            string label = string.Equals(restriction, "ban", StringComparison.OrdinalIgnoreCase) ? "Banned" : "Muted";
            AppendLog(label + " chat client " + snapshot.OwnerKey + ".");
            RefreshSessionsPanel(true);
        });
    }

    private MenuItem BuildAdminActionItem(string header, ChatSessionListItem item, Action action) {
        var menuItem = CreateReadableMenuItem(header, item);
        menuItem.Click += (_, eventArgs) => {
            eventArgs.Handled = true;
            try {
                action();
            } catch (Exception ex) {
                AppendLog("Admin action failed: " + TrimForDisplay(ex.Message, 180));
            }
        };
        return menuItem;
    }

    private void ShowClientPermissionsDialog(ChatSessionListItem item) {
        string ownerKey = ResolveSessionOwnerKey(item);
        ChatClientPermissionSnapshot snapshot = _proxy.GetChatClientPermissionsDiagnostics(ownerKey);
        ownerKey = snapshot.OwnerKey;

        var dialog = new Window {
            Title = "Edit permissions",
            Owner = this,
            Width = 540,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(42, 46, 54))
        };

        var root = new StackPanel {
            Margin = new Thickness(16)
        };
        root.Children.Add(new TextBlock {
            Text = ownerKey,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        });

        CheckBox agentAccess = CreatePermissionCheckBox("Agent Access", snapshot.AgentAccess);
        CheckBox fileUploads = CreatePermissionCheckBox("File Uploads", snapshot.FileUploads);
        CheckBox imageUploads = CreatePermissionCheckBox("Image Uploads", snapshot.ImageUploads);
        CheckBox internetSearch = CreatePermissionCheckBox("Internet Search", snapshot.InternetSearch);
        CheckBox vsCopilotTools = CreatePermissionCheckBox("VS Copilot Tools", snapshot.VsCopilotTools);
        CheckBox fileDownloads = CreatePermissionCheckBox("File Downloads", snapshot.FileDownloads);
        CheckBox ftpServer = CreatePermissionCheckBox("FTP Server", snapshot.FtpServer);
        CheckBox sqlAdmin = CreatePermissionCheckBox("SQL Admin", snapshot.SqlAdmin);
        CheckBox terminalCommands = CreatePermissionCheckBox("Terminal Commands", snapshot.TerminalCommands);
        CheckBox terminalForeverApproved = CreatePermissionCheckBox("Terminal Forever Approved", snapshot.TerminalForeverApproved);
        terminalForeverApproved.ToolTip = "THIS IS DANGEROUS AND CAN BE USED TO HARM YOUR COMPUTER.";

        root.Children.Add(agentAccess);
        root.Children.Add(fileUploads);
        root.Children.Add(imageUploads);
        root.Children.Add(internetSearch);
        root.Children.Add(vsCopilotTools);
        root.Children.Add(fileDownloads);
        root.Children.Add(ftpServer);
        root.Children.Add(sqlAdmin);
        root.Children.Add(terminalCommands);
        root.Children.Add(terminalForeverApproved);

        root.Children.Add(new TextBlock {
            Text = "Terminal command rules",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 6)
        });
        var terminalRulesPanel = new StackPanel {
            Margin = new Thickness(0, 0, 0, 4)
        };
        root.Children.Add(terminalRulesPanel);

        Action renderTerminalRules = () => { };
        renderTerminalRules = () => {
            terminalRulesPanel.Children.Clear();
            IReadOnlyList<TerminalPermissionRuleSnapshot> rules = _proxy.GetTerminalPermissionRulesDiagnostics(ownerKey);
            if (rules.Count == 0) {
                terminalRulesPanel.Children.Add(new TextBlock {
                    Text = "No saved terminal command rules for this client.",
                    Foreground = new SolidColorBrush(Color.FromRgb(190, 202, 220)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                return;
            }

            foreach (TerminalPermissionRuleSnapshot rule in rules) {
                var rowBorder = new Border {
                    Background = new SolidColorBrush(Color.FromRgb(32, 37, 46)),
                    BorderBrush = new SolidColorBrush(rule.Decision == "allow" ? Color.FromRgb(64, 201, 162) : Color.FromRgb(255, 92, 122)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var row = new DockPanel {
                    LastChildFill = true
                };
                var remove = new Button {
                    Content = "Remove",
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    MinWidth = 70,
                    Tag = rule
                };
                remove.Click += (_, _) => {
                    _proxy.RemoveTerminalPermissionRule(ownerKey, rule.CommandHash);
                    AppendLog("Removed terminal " + rule.Decision + " rule for " + ownerKey + ": " + rule.Command);
                    renderTerminalRules();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock {
                    Text = rule.Decision.ToUpperInvariant() + " | " + TrimForDisplay(rule.Command, 180),
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                });
                rowBorder.Child = row;
                terminalRulesPanel.Children.Add(rowBorder);
            }
        };
        renderTerminalRules();

        var actions = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button {
            Content = "Cancel",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 72
        };
        var save = new Button {
            Content = "Save",
            Padding = new Thickness(12, 4, 12, 4),
            MinWidth = 72
        };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) => {
            try {
                snapshot.OwnerKey = ownerKey;
                snapshot.AgentAccess = agentAccess.IsChecked.GetValueOrDefault();
                snapshot.FileUploads = fileUploads.IsChecked.GetValueOrDefault();
                snapshot.ImageUploads = imageUploads.IsChecked.GetValueOrDefault();
                snapshot.InternetSearch = internetSearch.IsChecked.GetValueOrDefault();
                snapshot.VsCopilotTools = vsCopilotTools.IsChecked.GetValueOrDefault();
                snapshot.FileDownloads = fileDownloads.IsChecked.GetValueOrDefault();
                snapshot.FtpServer = ftpServer.IsChecked.GetValueOrDefault();
                snapshot.SqlAdmin = sqlAdmin.IsChecked.GetValueOrDefault();
                snapshot.TerminalCommands = terminalCommands.IsChecked.GetValueOrDefault();
                snapshot.TerminalForeverApproved = terminalForeverApproved.IsChecked.GetValueOrDefault();
                ChatClientPermissionSnapshot updated = _proxy.SaveChatClientPermissionsDiagnostics(snapshot);
                AppendLog("Saved chat client permissions for " + updated.OwnerKey + ".");
                RefreshSessionsPanel(true);
                RefreshAccessibleDirectories(true);
                RefreshWorkspaceExplorer(true);
                dialog.Close();
            } catch (Exception ex) {
                AppendLog("Permission save failed: " + TrimForDisplay(ex.Message, 180));
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        root.Children.Add(actions);
        dialog.Content = new ScrollViewer {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        dialog.ShowDialog();
    }

    private static CheckBox CreatePermissionCheckBox(string label, bool isChecked) {
        return new CheckBox {
            Content = label,
            IsChecked = isChecked,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private string ResolveSessionOwnerKey(ChatSessionListItem item) {
        if (!string.IsNullOrWhiteSpace(item.OwnerKey))
            return item.OwnerKey;
        if (!string.IsNullOrWhiteSpace(_selectedOwnerKey))
            return _selectedOwnerKey;
        return _proxy.GetDefaultLocalChatOwnerKey();
    }

    private void RefreshExplorerButton_Click(object sender, RoutedEventArgs e) {
        RefreshAccessibleDirectories(true);
        RefreshWorkspaceExplorer(true);
    }

    private void SessionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        ChatSessionListItem? selectedItem = SessionsListBox.SelectedItem as ChatSessionListItem;
        _selectedSessionId = selectedItem?.Id ?? "";
        _selectedOwnerKey = selectedItem?.OwnerKey ?? "";
        _accessibleDirectorySignature = "";
        _workspaceTreeSignature = "";
        RefreshAccessibleDirectories(true);
        RefreshWorkspaceExplorer(true);
    }

    private void AccessibleDirectoriesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (AccessibleDirectoriesListBox.SelectedItem is AccessibleDirectoryListItem item)
            FilesystemAccessStatusText.Text = item.Path;
    }

    private void BrowseAccessibleDirectoryButton_Click(object sender, RoutedEventArgs e) {
        try {
            var dialog = new OpenFolderDialog {
                Title = "Select accessible directory",
                Multiselect = false
            };
            if (AccessibleDirectoriesListBox.SelectedItem is AccessibleDirectoryListItem selected &&
                Directory.Exists(selected.Path))
                dialog.InitialDirectory = selected.Path;

            if (dialog.ShowDialog(this) == true)
                AddAccessibleDirectoryPath(dialog.FolderName);
        } catch (Exception ex) {
            FilesystemAccessStatusText.Text = "Add failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void RemoveAccessibleDirectoryEntryButton_Click(object sender, RoutedEventArgs e) {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not AccessibleDirectoryListItem item)
            return;

        RemoveAccessibleDirectoryPath(item.Path);
    }

    private void AddAccessibleDirectoryPath(string path) {
        try {
            string ownerKey = ResolveActiveFilesystemOwnerKey();
            _proxy.AddChatFilesystemAccessDirectory(ownerKey, path);
            FilesystemAccessStatusText.Text = "Added directory.";
            _accessibleDirectorySignature = "";
            _workspaceTreeSignature = "";
            RefreshAccessibleDirectories(true);
            RefreshWorkspaceExplorer(true);
        } catch (Exception ex) {
            FilesystemAccessStatusText.Text = "Add failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void RemoveAccessibleDirectoryPath(string path) {
        try {
            string ownerKey = ResolveActiveFilesystemOwnerKey();
            _proxy.RemoveChatFilesystemAccessDirectory(ownerKey, path);
            FilesystemAccessStatusText.Text = "Removed directory.";
            _accessibleDirectorySignature = "";
            _workspaceTreeSignature = "";
            RefreshAccessibleDirectories(true);
            RefreshWorkspaceExplorer(true);
        } catch (Exception ex) {
            FilesystemAccessStatusText.Text = "Remove failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void RemoteLmStudioCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (RemoteServerTextBox != null)
            RemoteServerTextBox.IsEnabled = RemoteLmStudioCheckBox.IsChecked.GetValueOrDefault(false) && !_proxy.IsListening;
        SaveSettingsIfReady();
        RefreshMetrics();
    }

    private void RemoteServerTextBox_TextChanged(object sender, TextChangedEventArgs e) {
        SaveSettingsIfReady();
    }

    private void StartByDefaultCheckBox_Changed(object sender, RoutedEventArgs e) {
        SaveSettingsIfReady();
    }

    private void WindowsStartupCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (HideToTrayOnStartupCheckBox != null)
            HideToTrayOnStartupCheckBox.IsEnabled = WindowsStartupCheckBox.IsChecked.GetValueOrDefault(false);
        SaveSettingsIfReady();
    }

    private void HideToTrayOnStartupCheckBox_Changed(object sender, RoutedEventArgs e) {
        SaveSettingsIfReady();
    }

    private void AllowOpenRegistrationCheckBox_Changed(object sender, RoutedEventArgs e) {
        if (OpenRegistrationWarningText != null)
            OpenRegistrationWarningText.Visibility = AllowOpenRegistrationCheckBox?.IsChecked.GetValueOrDefault(false) == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        SaveSettingsIfReady();
    }

    private void PortForwardingCheckBox_Changed(object sender, RoutedEventArgs e) {
        _portForwardingEnabled = PortForwardingCheckBox?.IsChecked.GetValueOrDefault(false) == true;
        if (!_uiReady)
            return;

        SaveSettingsIfReady();
        if (_portForwardingEnabled) {
            BeginPortForwardingIfEnabled("toggle enabled");
            return;
        }

        UpdatePortForwardStatus("Disabled. TCP " + FormatForwardedPorts() + " will not be forwarded.", Brushes.SlateGray);
        AppendLog("Port forwarding disabled. No app/API ports were forwarded; " + ServerPort + " and " + LocalLmStudioProxyPort + " remain local.");
    }

    private void StorageCostSettings_Changed(object sender, RoutedEventArgs e) {
        UpdateStorageCostFactorText();
        SaveSettingsIfReady();
    }

    private string GetSelectedStorageType() {
        if (StorageTypeComboBox?.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "Nvme";
        return string.IsNullOrWhiteSpace(_settings.StorageType) ? "Nvme" : _settings.StorageType;
    }

    private void SelectStorageType(string storageType) {
        string target = NormalizeStorageType(storageType);
        if (StorageTypeComboBox == null)
            return;

        for (int i = 0; i < StorageTypeComboBox.Items.Count; i++) {
            if (StorageTypeComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase)) {
                StorageTypeComboBox.SelectedIndex = i;
                return;
            }
        }

        StorageTypeComboBox.SelectedIndex = 0;
    }

    private void UpdateStorageCostFactorText() {
        if (StorageCostFactorText != null)
            StorageCostFactorText.Text = ClampStorageCostFactor(StorageCostFactorSlider?.Value ?? 0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    }

    private void ApplyStorageSettingsToProxy(LmVsProxyGuiSettings settings) {
        try {
            _proxy.ConfigureChatStorageCosts(settings.StorageType, settings.StorageCostFactor);
        } catch (Exception ex) {
            if (_uiReady && SettingsStatusText != null)
                SettingsStatusText.Text = "Storage cost settings failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void ApplyOpenRegistrationSettingToProxy(LmVsProxyGuiSettings settings) {
        try {
            _proxy.AllowOpenRegistration = settings.AllowOpenRegistration;
        } catch (Exception ex) {
            if (_uiReady && SettingsStatusText != null)
                SettingsStatusText.Text = "Registration setting failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void ApplyDetectedGpuTdpToProxy() {
        try {
            GpuTdpDetectionSnapshot snapshot = _proxy.DetectAndApplyNvidiaGpuTdpWatts();
            if (snapshot.TdpWatts > 0) {
                string watts = snapshot.TdpWatts.ToString("0.#", CultureInfo.InvariantCulture);
                _gpuTdpText =  watts + "W | " + TrimForDisplay(snapshot.GpuName, 36);
                UpdateGpuTdpBillingText();
                AppendLog("Detected NVIDIA GPU TDP: " + snapshot.GpuName + " -> " + watts + "W (" + snapshot.Source + ").");
                return;
            }

            _gpuTdpText = "GPU TDP: no NVIDIA GPU detected";
            UpdateGpuTdpBillingText();
            AppendLog("No NVIDIA GPU TDP detected. Intel, AMD, and integrated graphics are ignored.");
        } catch (Exception ex) {
            _gpuTdpText = "detection failed";
            UpdateGpuTdpBillingText();
            AppendLog("NVIDIA GPU TDP detection failed: " + TrimForDisplay(ex.Message, 180));
        }
    }

    private void UpdateGpuTdpBillingText() {
        if (GpuTdpBillingText != null)
            GpuTdpBillingText.Text = _gpuTdpText;
    }

    private void UpdateCpuTdpBillingText() {
        if (CpuTdpBillingText == null)
            return;

        try {
            double watts = Math.Max(0, _proxy.ChatCpuTdpWatts);
            CpuTdpBillingText.Text = watts.ToString("0.#", CultureInfo.InvariantCulture) + "W";
            CpuTdpBillingText.ToolTip = "CPU TDP estimate used for billing: " + CpuTdpBillingText.Text;
        } catch (Exception ex) {
            CpuTdpBillingText.Text = "unavailable";
            CpuTdpBillingText.ToolTip = "CPU TDP estimate unavailable: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private static string NormalizeStorageType(string storageType) {
        if (string.Equals(storageType, "SATA SSD", StringComparison.OrdinalIgnoreCase))
            return "SATA SSD";
        if (string.Equals(storageType, "SATA HDD", StringComparison.OrdinalIgnoreCase))
            return "SATA HDD";
        return "Nvme";
    }

    private static double ClampStorageCostFactor(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Max(-1, Math.Min(1, value));
    }

    private LmVsProxyGuiSettings LoadSettings() {
        try {
            if (!File.Exists(_settingsPath)) {
                SettingsStatusText.Text = "Using defaults. Settings will save on close.";
                return new LmVsProxyGuiSettings();
            }

            string json = File.ReadAllText(_settingsPath);
            LmVsProxyGuiSettings? loaded = JsonSerializer.Deserialize<LmVsProxyGuiSettings>(json);
            SettingsStatusText.Text = "Loaded settings from runtime directory.";
            return loaded ?? new LmVsProxyGuiSettings();
        } catch (Exception ex) {
            SettingsStatusText.Text = "Settings load failed: " + TrimForDisplay(ex.Message, 120);
            return new LmVsProxyGuiSettings();
        }
    }

    private void ApplySettingsToUi(LmVsProxyGuiSettings settings) {
        _isApplyingSettings = true;
        try {
            RemoteLmStudioCheckBox.IsChecked = settings.RemoteLmStudioEnabled;
            RemoteServerTextBox.Text = string.IsNullOrWhiteSpace(settings.RemoteServer)
                ? "127.0.0.1:" + LocalLmStudioProxyPort
                : settings.RemoteServer;
            PortForwardingCheckBox.IsChecked = settings.PortForwardingEnabled;
            StartByDefaultCheckBox.IsChecked = settings.StartProxyOnOpen;
            WindowsStartupCheckBox.IsChecked = settings.WindowsStartupEnabled;
            HideToTrayOnStartupCheckBox.IsChecked = settings.HideToTrayOnStartup;
            HideToTrayOnStartupCheckBox.IsEnabled = settings.WindowsStartupEnabled;
            AllowOpenRegistrationCheckBox.IsChecked = settings.AllowOpenRegistration;
            OpenRegistrationWarningText.Visibility = settings.AllowOpenRegistration ? Visibility.Visible : Visibility.Collapsed;
            SelectStorageType(settings.StorageType);
            StorageCostFactorSlider.Value = ClampStorageCostFactor(settings.StorageCostFactor);
            UpdateStorageCostFactorText();
            ApplyWindowSettings(settings);
        } finally {
            _isApplyingSettings = false;
        }
    }

    private void ApplyWindowSettings(LmVsProxyGuiSettings settings) {
        Rect workArea = SystemParameters.WorkArea;
        double maxWidth = Math.Max(MinWidth, workArea.Width - 32);
        double maxHeight = Math.Max(MinHeight, workArea.Height - 32);
        double targetWidth = settings.WindowWidth >= MinWidth ? settings.WindowWidth : Width;
        double targetHeight = settings.WindowHeight >= MinHeight ? settings.WindowHeight : Height;

        Width = Math.Min(Math.Max(MinWidth, targetWidth), maxWidth);
        Height = Math.Min(Math.Max(MinHeight, targetHeight), maxHeight);

        if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue) {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Min(Math.Max(workArea.Left + 8, settings.WindowLeft.Value), Math.Max(workArea.Left + 8, workArea.Right - Width - 8));
            Top = Math.Min(Math.Max(workArea.Top + 8, settings.WindowTop.Value), Math.Max(workArea.Top + 8, workArea.Bottom - Height - 8));
        } else {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left + Math.Max(8, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(8, (workArea.Height - Height) / 2);
        }
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveSettingsIfReady() {
        if (!_uiReady || _isApplyingSettings)
            return;

        SaveSettings();
    }

    private void SaveSettings() {
        try {
            _settings = CaptureSettings();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? AppContext.BaseDirectory);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, options));
            ApplyWindowsStartupRegistration(_settings);
            ApplyStorageSettingsToProxy(_settings);
            ApplyOpenRegistrationSettingToProxy(_settings);
            if (_uiReady && SettingsStatusText != null)
                SettingsStatusText.Text = "Saved settings to runtime directory.";
        } catch (Exception ex) {
            if (_uiReady && SettingsStatusText != null)
                SettingsStatusText.Text = "Settings save failed: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private LmVsProxyGuiSettings CaptureSettings() {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new LmVsProxyGuiSettings {
            StartProxyOnOpen = StartByDefaultCheckBox.IsChecked.GetValueOrDefault(true),
            RemoteLmStudioEnabled = RemoteLmStudioCheckBox.IsChecked.GetValueOrDefault(false),
            RemoteServer = RemoteServerTextBox.Text ?? "",
            PortForwardingEnabled = PortForwardingCheckBox.IsChecked.GetValueOrDefault(true),
            WindowsStartupEnabled = WindowsStartupCheckBox.IsChecked.GetValueOrDefault(false),
            HideToTrayOnStartup = HideToTrayOnStartupCheckBox.IsChecked.GetValueOrDefault(true),
            AllowOpenRegistration = AllowOpenRegistrationCheckBox.IsChecked.GetValueOrDefault(false),
            StorageType = GetSelectedStorageType(),
            StorageCostFactor = ClampStorageCostFactor(StorageCostFactorSlider.Value),
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowMaximized = WindowState == WindowState.Maximized
        };
    }

    private void ApplyWindowsStartupRegistration(LmVsProxyGuiSettings settings) {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistryPath, writable: true);
        if (runKey == null)
            return;

        if (settings.WindowsStartupEnabled) {
            string executablePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrWhiteSpace(executablePath)) {
                try {
                    executablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                } catch {
                    executablePath = "";
                }
            }

            if (string.IsNullOrWhiteSpace(executablePath))
                return;

            string command = "\"" + executablePath + "\"";
            if (settings.HideToTrayOnStartup)
                command += " --tray";
            runKey.SetValue(WindowsStartupValueName, command, RegistryValueKind.String);
        } else {
            runKey.DeleteValue(WindowsStartupValueName, throwOnMissingValue: false);
        }
    }

    private void EnsureTrayIcon() {
        if (_trayIcon != null)
            return;

        var menu = new Forms.ContextMenuStrip() { ForeColor = System.Drawing.Color.Black };
        var showItem = new Forms.ToolStripMenuItem("Show");
        showItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var openChatItem = new Forms.ToolStripMenuItem("Open Chat UI");
        openChatItem.Click += (_, _) => Dispatcher.Invoke(() => OpenChatButton_Click(this, new RoutedEventArgs()));
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitFromTray);
        menu.Items.Add(showItem);
        menu.Items.Add(openChatItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon {
            Text = "LmVsProxy GUI",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void HideToTray() {
        EnsureTrayIcon();
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray() {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray() {
        _allowExit = true;
        Close();
    }

    private void DisposeTrayIcon() {
        if (_trayIcon == null)
            return;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private async Task StartProxyAsync() {
        StartStopButton.IsEnabled = false;
        StartStopButton.Content = "Starting...";
        SetStatus("Starting LmVsProxy.");
        ResetChatBandwidthMetrics();

        try {
            StartRemoteLmStudioProxyIfEnabled();

            if (!_proxy.Start()) {
                SetStatus("LmVsProxy was already running.");
                return;
            }

            if (!_proxy.ChatServer.IsListening)
                _proxy.ChatServer.Listen();

            BeginPortForwardingIfEnabled("proxy start");
            _startedAt = DateTimeOffset.UtcNow;
            AppendLog("VS Copilot endpoint: http://localhost:" + ServerPort + "/v1/responses");
            AppendLog("VS chat-completions endpoint: http://localhost:" + ServerPort + "/v1/chat/completions");
            AppendLog("LM Studio endpoint: http://localhost:" + LocalLmStudioProxyPort + "/v1/chat/completions");
            AppendLog("Chat UI: " + _proxy.ChatServerUrl);
            SetStatus("LmVsProxy is running.");
            await ProbeLmStudioAsync();
        } catch (Exception ex) {
            StopRemoteLmStudioProxy();
            AppendLog("Start error: " + ex.Message);
            SetStatus("Start failed: " + ex.Message);
        } finally {
            StartStopButton.IsEnabled = true;
            RefreshMetrics();
        }
    }

    private void BeginPortForwardingIfEnabled(string reason) {
        if (!_portForwardingEnabled)
            return;

        RegisterPortForwardingEvents();

        if (_portForwardingSucceeded) {
            UpdatePortForwardStatus("Succeeded: " + FormatForwardedPorts() + ". Failed: none.", Brushes.LimeGreen);
            return;
        }

        if (!NIC.InterfaceDiscovered || NIC.NAT == null) {
            UpdatePortForwardStatus("Enabled. Waiting for NAT discovery to forward TCP " + FormatForwardedPorts() + ".", Brushes.Orange);
            if (!_portForwardingDiscoveryStarted) {
                AppendLog("Port forwarding enabled for remote web/FTP access (" + reason + "). Waiting for NAT discovery. Targets: " + FormatForwardedPorts() + ". Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
                _ = MarkPortForwardingTimeoutAsync();
            }
            _portForwardingDiscoveryStarted = true;
            NIC.DiscoverNAT();
            return;
        }

        _ = ForwardWebPortAsync(reason);
    }

    private void RegisterPortForwardingEvents() {
        if (_portForwardingEventsRegistered)
            return;

        NIC.OnInterfaceDiscovered += OnNicInterfaceDiscovered;
        NIC.OnError += OnNicError;
        _portForwardingEventsRegistered = true;
    }

    private void UnregisterPortForwardingEvents() {
        if (!_portForwardingEventsRegistered)
            return;

        NIC.OnInterfaceDiscovered -= OnNicInterfaceDiscovered;
        NIC.OnError -= OnNicError;
        _portForwardingEventsRegistered = false;
    }

    private void OnNicInterfaceDiscovered(int mtu, IPAddress localIp) {
        Dispatcher.InvokeAsync(() =>
        {
            AppendLog("NAT interface discovered for port forwarding: " + localIp + " MTU " + mtu + ". Forwarding TCP " + FormatForwardedPorts() + ".");
            _ = ForwardWebPortAsync("NAT discovery");
        });
    }

    private void OnNicError(Exception ex) {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_portForwardingEnabled || _portForwardingSucceeded || _portForwardingInProgress)
                return;

            UpdatePortForwardStatus("Succeeded: none. Failed: " + FormatForwardedPorts() + ".", Brushes.IndianRed);
            AppendLog("Port forwarding failed. Succeeded: none. Failed: " + FormatForwardedPorts() + " (" + ex.Message + "). Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
        });
    }

    private async Task ForwardWebPortAsync(string reason) {
        if (!_portForwardingEnabled || _portForwardingInProgress)
            return;

        _portForwardingInProgress = true;
        _portForwardingAttempted = true;
        UpdatePortForwardStatus("Forwarding TCP " + FormatForwardedPorts() + "...", Brushes.Orange);

        try {
            List<int> succeeded = new();
            List<string> failed = new();
            foreach (int port in ForwardedTcpPorts) {
                try {
                    bool forwarded = await NIC.ForwardPort(port);
                    if (forwarded)
                        succeeded.Add(port);
                    else
                        failed.Add(port.ToString(CultureInfo.InvariantCulture));
                } catch (Exception ex) {
                    failed.Add(port.ToString(CultureInfo.InvariantCulture) + " (" + ex.Message + ")");
                }
            }

            if (!_portForwardingEnabled)
                return;

            _portForwardingSucceeded = failed.Count == 0 && succeeded.Count == ForwardedTcpPorts.Length;

            string succeededText = succeeded.Count == 0 ? "none" : string.Join(", ", succeeded);
            string failedText = failed.Count == 0 ? "none" : string.Join(", ", failed);
            Brush statusBrush = failed.Count == 0 ? Brushes.LimeGreen : succeeded.Count == 0 ? Brushes.IndianRed : Brushes.Orange;

            if (failed.Count == 0) {
                UpdatePortForwardStatus("Succeeded: " + succeededText + ". Failed: none.", statusBrush);
                AppendLog("Port forwarding succeeded (" + reason + "). Succeeded: " + succeededText + ". Failed: none. Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
            } else {
                UpdatePortForwardStatus("Succeeded: " + succeededText + ". Failed: " + failedText + ".", statusBrush);
                AppendLog("Port forwarding failed (" + reason + "). Succeeded: " + succeededText + ". Failed: " + failedText + ". Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
            }
        } catch (Exception ex) {
            if (!_portForwardingEnabled)
                return;

            _portForwardingSucceeded = false;
            UpdatePortForwardStatus("Succeeded: none. Failed: " + FormatForwardedPorts() + ".", Brushes.IndianRed);
            AppendLog("Port forwarding failed (" + reason + "). Succeeded: none. Failed: " + FormatForwardedPorts() + " (" + ex.Message + "). Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
        } finally {
            _portForwardingInProgress = false;
        }
    }

    private async Task MarkPortForwardingTimeoutAsync() {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (!_portForwardingEnabled || _portForwardingAttempted || _portForwardingSucceeded || NIC.InterfaceDiscovered)
            return;

        UpdatePortForwardStatus("Succeeded: none. Failed: " + FormatForwardedPorts() + " (NAT discovery timed out).", Brushes.IndianRed);
        AppendLog("Port forwarding failed. Succeeded: none. Failed: " + FormatForwardedPorts() + " (NAT discovery timed out). Skipped: " + ServerPort + ", " + LocalLmStudioProxyPort + ".");
    }

    private void UpdatePortForwardStatus(string text, Brush brush) {
        PortForwardStatusText.Text = text;
        PortForwardStatusText.Foreground = brush;
    }

    private static string FormatForwardedPorts() {
        return string.Join(", ", ForwardedTcpPorts);
    }

    private void StopProxy() {
        StartStopButton.IsEnabled = false;
        StartStopButton.Content = "Stopping...";
        SetStatus("Stopping LmVsProxy.");

        try {
            if (_proxy.IsListening || (_proxy.IsChatServerCreated && _proxy.ChatServer.IsListening))
                _proxy.Stop();
            StopRemoteLmStudioProxy();
            _startedAt = null;
            ResetChatBandwidthMetrics();
            SetStatus("LmVsProxy stopped.");
        } catch (Exception ex) {
            AppendLog("Stop error: " + ex.Message);
            SetStatus("Stop failed: " + ex.Message);
        } finally {
            StartStopButton.IsEnabled = true;
            RefreshMetrics();
        }
    }

    private async Task ProbeLmStudioAsync() {
        if (_isProbing)
            return;

        _isProbing = true;
        ProbeButton.IsEnabled = false;
        _lmStatusText = "Checking process...";
        _lmStatusBrush = Brushes.Orange;
        _lmLatencyText = "Process: checking";

        try {
            int? runningProcessId = FindLmStudioProcessId();
            if (runningProcessId.HasValue) {
                SetLmStudioRunningStatus(runningProcessId.Value);
                return;
            }

            string executablePath = ResolveLmStudioExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath)) {
                _lmStatusText = "Not running";
                _lmLatencyText = "App not found";
                _lmStatusBrush = Brushes.IndianRed;
                AppendLog("LM Studio is not running and " + LmStudioExecutableName + " was not found under " + GetLocalProgramsLmStudioPath() + ".");
                return;
            }

            _lmStatusText = "Starting";
            _lmLatencyText = "Process: launch requested";
            _lmStatusBrush = Brushes.Orange;
            RefreshMetrics();
            AppendLog("LM Studio is not running; starting " + executablePath);

            if (TryStartLmStudio(executablePath, out string error)) {
                await Task.Delay(TimeSpan.FromMilliseconds(1200));
                runningProcessId = FindLmStudioProcessId();
                if (runningProcessId.HasValue) {
                    SetLmStudioRunningStatus(runningProcessId.Value);
                } else {
                    _lmStatusText = "Starting";
                    _lmLatencyText = "Process: launch requested";
                    _lmStatusBrush = Brushes.Orange;
                }
            } else {
                _lmStatusText = "Start failed";
                _lmLatencyText = TrimForDisplay(error, 90);
                _lmStatusBrush = Brushes.IndianRed;
                AppendLog("LM Studio start failed: " + error);
            }
        } finally {
            _lmStudioEventCount++;
            PulseService("LM Studio");
            _isProbing = false;
            ProbeButton.IsEnabled = true;
            RefreshMetrics();
        }
    }

    private static int? FindLmStudioProcessId() {
        Process[] processes;
        try {
            processes = Process.GetProcessesByName(LmStudioProcessName);
        } catch {
            return null;
        }

        try {
            foreach (Process process in processes) {
                try {
                    if (!process.HasExited)
                        return process.Id;
                } catch {
                    // Ignore races where Windows exits the process while we enumerate it.
                }
            }
        } finally {
            foreach (Process process in processes)
                process.Dispose();
        }

        return null;
    }

    private void SetLmStudioRunningStatus(int processId) {
        _lmStatusText = "Running";
        _lmLatencyText = "Process: PID " + processId;
        _lmStatusBrush = Brushes.LimeGreen;
    }

    private static string ResolveLmStudioExecutablePath() {
        string expectedPath = Path.Combine(GetLocalProgramsLmStudioPath(), LmStudioExecutableName);
        if (File.Exists(expectedPath))
            return expectedPath;

        try {
            string root = GetLocalProgramsPath();
            if (Directory.Exists(root)) {
                return Directory.EnumerateFiles(root, LmStudioExecutableName, SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Contains(Path.DirectorySeparatorChar + LmStudioFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ?? "";
            }
        } catch {
            return "";
        }

        return "";
    }

    private static string GetLocalProgramsLmStudioPath() {
        return Path.Combine(GetLocalProgramsPath(), LmStudioFolderName);
    }

    private static string GetLocalProgramsPath() {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        return Path.Combine(localAppData, "Programs");
    }

    private static bool TryStartLmStudio(string executablePath, out string error) {
        try {
            var startInfo = new ProcessStartInfo(executablePath) {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? GetLocalProgramsLmStudioPath()
            };
            Process.Start(startInfo);
            error = "";
            return true;
        } catch (Exception ex) {
            error = ex.Message;
            return false;
        }
    }

    private void StartRemoteLmStudioProxyIfEnabled() {
        if (!RemoteLmStudioCheckBox.IsChecked.GetValueOrDefault(false))
            return;

        if (!TryParseRemoteServer(RemoteServerTextBox.Text, out string remoteHost, out int remotePort))
            throw new InvalidOperationException("Remote server must be a host, host:port, or http(s) URL.");

        StopRemoteLmStudioProxy();
        _remoteLmStudioProxy = new TcpDuplicator(remoteHost, remotePort, LocalLmStudioProxyPort);
        _remoteLmStudioProxy.Start();
        RemoteServerTextBox.IsEnabled = false;
        AppendLog("Remote LM Studio tunnel: 127.0.0.1:" + LocalLmStudioProxyPort + " -> " + remoteHost + ":" + remotePort);
    }

    private void StopRemoteLmStudioProxy() {
        if (_remoteLmStudioProxy == null)
            return;

        try {
            if (_remoteLmStudioProxy.IsRunning)
                AppendLog("Stopping remote LM Studio tunnel on 127.0.0.1:" + LocalLmStudioProxyPort);
            _remoteLmStudioProxy.Dispose();
        } catch (Exception ex) {
            AppendLog("Remote LM Studio tunnel stop error: " + ex.Message);
        } finally {
            _remoteLmStudioProxy = null;
            RemoteServerTextBox.IsEnabled = RemoteLmStudioCheckBox.IsChecked.GetValueOrDefault(false) && !_proxy.IsListening;
        }
    }

    private static bool TryParseRemoteServer(string? input, out string host, out int port) {
        host = "";
        port = LocalLmStudioProxyPort;

        string value = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)) {
            host = uri.Host;
            if (uri.Port > 0)
                port = uri.Port;
            return true;
        }

        if (value.StartsWith("[", StringComparison.Ordinal)) {
            int close = value.IndexOf(']');
            if (close > 1) {
                host = value.Substring(1, close - 1);
                if (value.Length > close + 2 && value[close + 1] == ':' && !int.TryParse(value[(close + 2)..], out port))
                    return false;
                return !string.IsNullOrWhiteSpace(host);
            }
        }

        int lastColon = value.LastIndexOf(':');
        if (lastColon > 0 && value.IndexOf(':') == lastColon) {
            host = value[..lastColon].Trim();
            if (!int.TryParse(value[(lastColon + 1)..].Trim(), out port))
                return false;
        } else {
            host = value;
        }

        return !string.IsNullOrWhiteSpace(host) && port > 0 && port <= 65535;
    }

    private void OnProxyOutputLog(object? sender, OutputLogEventArgs e) {
        Dispatcher.InvokeAsync(() =>
        {
            AppendLog(e.Message);
            RecordDiagnostics(e.Message);
            RefreshMetrics();
        });
    }

    private void LoadPendingRegistrationRequests() {
        try {
            foreach (WebAuthRegistrationRequestSnapshot request in _proxy.GetPendingWebAuthRegistrationRequests())
                ShowRegistrationRequestToast(RegistrationRequestItem.FromSnapshot(request));
        } catch (Exception ex) {
            AppendLog("Registration requests unavailable: " + TrimForDisplay(ex.Message, 180));
        }
    }

    private void OnWebAuthRegistrationRequested(object? sender, WebAuthRegistrationRequestEventArgs e) {
        if (e.Request == null)
            return;

        Dispatcher.InvokeAsync(() => {
            var item = RegistrationRequestItem.FromSnapshot(e.Request);
            ShowRegistrationRequestToast(item);
            AppendLog("Registration request: " + item.Title + Environment.NewLine + item.Detail);
            PulseService("Web UI Chat", "LmVsProxy");
            RefreshMetrics();
        });
    }

    private void OnFilesystemPermissionRequested(object? sender, FilesystemPermissionRequestEventArgs e) {
        if (e.Request == null)
            return;

        Dispatcher.InvokeAsync(() => {
            if (_filesystemPermissionRequests.Any(item => item.Id == e.Request.Id))
                return;

            var item = FilesystemPermissionRequestItem.FromSnapshot(e.Request);
            _filesystemPermissionRequests.Insert(0, item);
            AppendLog("RUN filesystem permission request: " + item.Title + Environment.NewLine + item.Detail);
            PulseService("Session Files", "LmVsProxy");
            RefreshMetrics();
        });
    }

    private void OnTerminalPermissionRequested(object? sender, TerminalPermissionRequestEventArgs e) {
        if (e.Request == null)
            return;

        Dispatcher.InvokeAsync(() => {
            if (_terminalPermissionRequests.Any(item => item.Id == e.Request.Id))
                return;

            var item = TerminalPermissionRequestItem.FromSnapshot(e.Request);
            _terminalPermissionRequests.Insert(0, item);
            ShowTerminalPermissionToast(item);
            AppendLog("RUN terminal permission request: " + item.Title + Environment.NewLine + item.Detail);
            _terminalServiceEventCount++;
            PulseService("Terminal", "LmVsProxy");
            RefreshMetrics();
        });
    }

    private void ShowRegistrationRequestToast(RegistrationRequestItem request) {
        if (request == null || string.IsNullOrWhiteSpace(request.Id))
            return;

        CloseRegistrationRequestToast(request.Id);

        var toast = new Window {
            Title = "Registration request",
            Owner = IsVisible ? this : null,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 42)),
            AllowsTransparency = false
        };

        var border = new Border {
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14)
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock {
            Text = request.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock {
            Text = request.Detail,
            Foreground = new SolidColorBrush(Color.FromRgb(196, 205, 218)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });

        var buttons = new WrapPanel();
        AddRegistrationToastButton(buttons, "Approve request", request, () => {
            WebAuthRegistrationRequestSnapshot snapshot = _proxy.ApproveWebAuthRegistrationRequest(request.Id);
            AppendLog("Approved registration request for " + snapshot.UserName + " from " + snapshot.ClientIp + ".");
        });
        AddRegistrationToastButton(buttons, "Deny", request, () => {
            WebAuthRegistrationRequestSnapshot snapshot = _proxy.DenyWebAuthRegistrationRequest(request.Id);
            AppendLog("Denied registration request for " + snapshot.UserName + " from " + snapshot.ClientIp + ".");
        }, new SolidColorBrush(Color.FromRgb(88, 28, 35)));

        root.Children.Add(buttons);
        border.Child = root;
        toast.Content = border;
        toast.Closed += (_, _) => _registrationRequestToasts.Remove(request.Id);
        _registrationRequestToasts[request.Id] = toast;
        toast.Show();
        PositionApprovalToasts();
    }

    private void AddRegistrationToastButton(WrapPanel panel, string label, RegistrationRequestItem request, Action action, Brush? background = null) {
        var button = new Button {
            Content = label,
            Tag = request,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(9, 4, 9, 4),
            Background = background ?? new SolidColorBrush(Color.FromRgb(38, 52, 72)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250))
        };
        button.Click += (_, _) => {
            try {
                action();
                CloseRegistrationRequestToast(request.Id);
                RefreshSessionsPanel(true);
                RefreshMetrics();
            } catch (Exception ex) {
                AppendLog("Registration request decision failed: " + TrimForDisplay(ex.Message, 180));
            }
        };
        panel.Children.Add(button);
    }

    private void CloseRegistrationRequestToast(string requestId) {
        if (string.IsNullOrWhiteSpace(requestId))
            return;
        if (!_registrationRequestToasts.TryGetValue(requestId, out Window? toast))
            return;

        _registrationRequestToasts.Remove(requestId);
        try {
            toast.Close();
        } catch { }
        PositionApprovalToasts();
    }

    private void TerminalPermissionApprove_Click(object sender, RoutedEventArgs e) {
        ApplyTerminalPermissionDecision(sender, request => {
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: false, foreverApprove: false);
            AppendLog("Approved terminal command once for " + request.OwnerKey + ": " + request.Command);
        });
    }

    private void TerminalPermissionAlwaysApprove_Click(object sender, RoutedEventArgs e) {
        ApplyTerminalPermissionDecision(sender, request => {
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: true, foreverApprove: false);
            AppendLog("Always approved terminal command for " + request.OwnerKey + ": " + request.Command);
        });
    }

    private void TerminalPermissionDeny_Click(object sender, RoutedEventArgs e) {
        ApplyTerminalPermissionDecision(sender, request => {
            _proxy.DenyTerminalPermissionRequest(request.Id, alwaysDeny: false);
            AppendLog("Denied terminal command once for " + request.OwnerKey + ": " + request.Command);
        });
    }

    private void TerminalPermissionAlwaysDeny_Click(object sender, RoutedEventArgs e) {
        ApplyTerminalPermissionDecision(sender, request => {
            _proxy.DenyTerminalPermissionRequest(request.Id, alwaysDeny: true);
            AppendLog("Always denied terminal command for " + request.OwnerKey + ": " + request.Command);
        });
    }

    private void TerminalPermissionForeverApprove_Click(object sender, RoutedEventArgs e) {
        ApplyTerminalPermissionDecision(sender, request => {
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: true, foreverApprove: true);
            AppendLog("FOREVER APPROVED terminal commands for " + request.OwnerKey + ".");
            RefreshSessionsPanel(true);
        });
    }

    private void ApplyTerminalPermissionDecision(object sender, Action<TerminalPermissionRequestItem> action) {
        if ((sender as FrameworkElement)?.Tag is not TerminalPermissionRequestItem request)
            return;

        try {
            action(request);
            _terminalPermissionRequests.Remove(request);
            CloseTerminalPermissionToast(request.Id);
            RefreshMetrics();
        } catch (Exception ex) {
            AppendLog("Terminal permission decision failed: " + TrimForDisplay(ex.Message, 180));
        }
    }

    private void ApplyTerminalPermissionDecision(TerminalPermissionRequestItem request, Action<TerminalPermissionRequestItem> action) {
        if (request == null)
            return;

        try {
            action(request);
            _terminalPermissionRequests.Remove(request);
            CloseTerminalPermissionToast(request.Id);
            RefreshMetrics();
        } catch (Exception ex) {
            AppendLog("Terminal permission decision failed: " + TrimForDisplay(ex.Message, 180));
        }
    }

    private void ShowTerminalPermissionToast(TerminalPermissionRequestItem request) {
        if (request == null || string.IsNullOrWhiteSpace(request.Id))
            return;

        CloseTerminalPermissionToast(request.Id);

        var toast = new Window {
            Title = "Terminal command approval",
            Owner = IsVisible ? this : null,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 42)),
            AllowsTransparency = false
        };

        var border = new Border {
            Background = new SolidColorBrush(Color.FromRgb(31, 35, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 176, 32)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14)
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock {
            Text = request.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock {
            Text = request.Detail,
            Foreground = new SolidColorBrush(Color.FromRgb(196, 205, 218)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12)
        });

        var buttons = new WrapPanel();
        AddTerminalToastButton(buttons, "Approve IP", request, () =>
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: false, foreverApprove: false));
        AddTerminalToastButton(buttons, "Always Approve IP", request, () =>
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: true, foreverApprove: false));
        AddTerminalToastButton(buttons, "Deny", request, () =>
            _proxy.DenyTerminalPermissionRequest(request.Id, alwaysDeny: false));
        AddTerminalToastButton(buttons, "Always Deny IP", request, () =>
            _proxy.DenyTerminalPermissionRequest(request.Id, alwaysDeny: true));
        AddTerminalToastButton(buttons, "\u26A0 FOREVER APPROVE", request, () =>
            _proxy.ApproveTerminalPermissionRequest(request.Id, alwaysApprove: true, foreverApprove: true),
            new SolidColorBrush(Color.FromRgb(185, 28, 28)),
            new SolidColorBrush(Color.FromRgb(255, 241, 106)),
            "THIS IS DANGEROUS AND CAN BE USED TO HARM YOUR COMPUTER.");

        root.Children.Add(buttons);
        border.Child = root;
        toast.Content = border;
        toast.Closed += (_, _) => _terminalPermissionToasts.Remove(request.Id);
        _terminalPermissionToasts[request.Id] = toast;
        toast.Show();
        PositionTerminalPermissionToasts();
    }

    private void AddTerminalToastButton(WrapPanel panel, string label, TerminalPermissionRequestItem request, Action action, Brush? background = null, Brush? foreground = null, string? toolTip = null) {
        var button = new Button {
            Content = label,
            Tag = request,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(9, 4, 9, 4),
            Background = background ?? new SolidColorBrush(Color.FromRgb(38, 52, 72)),
            Foreground = foreground ?? Brushes.White,
            BorderBrush = foreground ?? new SolidColorBrush(Color.FromRgb(69, 97, 124)),
            ToolTip = toolTip
        };
        button.Click += (_, _) => ApplyTerminalPermissionDecision(request, item => {
            action();
            AppendLog(label + ": " + item.Command);
        });
        panel.Children.Add(button);
    }

    private void CloseTerminalPermissionToast(string requestId) {
        if (string.IsNullOrWhiteSpace(requestId))
            return;
        if (!_terminalPermissionToasts.TryGetValue(requestId, out Window? toast))
            return;

        _terminalPermissionToasts.Remove(requestId);
        try {
            toast.Close();
        } catch { }
        PositionTerminalPermissionToasts();
    }

    private void PositionTerminalPermissionToasts() {
        PositionApprovalToasts();
    }

    private void PositionApprovalToasts() {
        if (_terminalPermissionToasts.Count == 0 && _registrationRequestToasts.Count == 0)
            return;

        Rect workArea = SystemParameters.WorkArea;
        double offset = 16;
        foreach (Window toast in _terminalPermissionToasts.Values.Concat(_registrationRequestToasts.Values).ToList()) {
            try {
                toast.UpdateLayout();
                double height = toast.ActualHeight > 0 ? toast.ActualHeight : toast.Height;
                toast.Left = Math.Max(workArea.Left + 8, workArea.Right - toast.Width - 16);
                toast.Top = Math.Max(workArea.Top + 8, workArea.Bottom - height - offset);
                offset += height + 10;
            } catch { }
        }
    }

    private void FilesystemPermissionAllow_Click(object sender, RoutedEventArgs e) {
        ApplyFilesystemPermissionDecision(sender, request => {
            _proxy.ApproveFilesystemPermissionRequest(request.Id, alwaysAllow: false);
            AppendLog("Allowed once: " + request.DirectoryPath + " for " + request.OwnerKey + ".");
        });
    }

    private void FilesystemPermissionAlwaysAllow_Click(object sender, RoutedEventArgs e) {
        ApplyFilesystemPermissionDecision(sender, request => {
            _proxy.ApproveFilesystemPermissionRequest(request.Id, alwaysAllow: true);
            AppendLog("Always allowed: " + request.DirectoryPath + " for " + request.OwnerKey + ".");
            RefreshAccessibleDirectories(true);
            RefreshWorkspaceExplorer(true);
        });
    }

    private void FilesystemPermissionDeny_Click(object sender, RoutedEventArgs e) {
        ApplyFilesystemPermissionDecision(sender, request => {
            _proxy.DenyFilesystemPermissionRequest(request.Id, alwaysDeny: false);
            AppendLog("Denied once: " + request.DirectoryPath + " for " + request.OwnerKey + ".");
        });
    }

    private void FilesystemPermissionAlwaysDeny_Click(object sender, RoutedEventArgs e) {
        ApplyFilesystemPermissionDecision(sender, request => {
            _proxy.DenyFilesystemPermissionRequest(request.Id, alwaysDeny: true);
            AppendLog("Always denied: " + request.DirectoryPath + " for " + request.OwnerKey + ".");
        });
    }

    private void FilesystemPermissionBan_Click(object sender, RoutedEventArgs e) {
        if ((sender as FrameworkElement)?.Tag is not FilesystemPermissionRequestItem request)
            return;

        var menu = CreateReadableContextMenu();
        menu.Items.Add(BuildFilesystemPermissionBanItem("10 minutes", request, TimeSpan.FromMinutes(10)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("30 minutes", request, TimeSpan.FromMinutes(30)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("2 hours", request, TimeSpan.FromHours(2)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("6 hours", request, TimeSpan.FromHours(6)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("1 day", request, TimeSpan.FromDays(1)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("7 days", request, TimeSpan.FromDays(7)));
        menu.Items.Add(BuildFilesystemPermissionBanItem("Until enabled", request, null));
        if (sender is Button button) {
            button.ContextMenu = menu;
            menu.PlacementTarget = button;
        }
        menu.IsOpen = true;
    }

    private MenuItem BuildFilesystemPermissionBanItem(string header, FilesystemPermissionRequestItem request, TimeSpan? duration) {
        var item = CreateReadableMenuItem(header, request);
        item.Click += (_, eventArgs) => {
            eventArgs.Handled = true;
            try {
                ChatClientPermissionSnapshot snapshot = _proxy.RestrictChatClient(request.OwnerKey, "ban", duration);
                _proxy.DenyFilesystemPermissionRequest(request.Id, alwaysDeny: false);
                _filesystemPermissionRequests.Remove(request);
                AppendLog("Banned " + snapshot.OwnerKey + " for filesystem permission request.");
                RefreshSessionsPanel(true);
            } catch (Exception ex) {
                AppendLog("Ban failed: " + TrimForDisplay(ex.Message, 180));
            }
        };
        return item;
    }

    private void ApplyFilesystemPermissionDecision(object sender, Action<FilesystemPermissionRequestItem> action) {
        if ((sender as FrameworkElement)?.Tag is not FilesystemPermissionRequestItem request)
            return;

        try {
            action(request);
            _filesystemPermissionRequests.Remove(request);
        } catch (Exception ex) {
            AppendLog("Filesystem permission decision failed: " + TrimForDisplay(ex.Message, 180));
        }
    }

    private void AppendLog(string? text) {
        if (string.IsNullOrEmpty(text))
            return;

        bool isAtEnd = LogTextBox.VerticalOffset >= (LogTextBox.ExtentHeight - LogTextBox.ViewportHeight) * 0.9;
        string safe = NormalizeLogText(text);
        if (!safe.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            safe += Environment.NewLine;

        Debug.Write(safe);
        LogTextBox.AppendText(safe);
        if (isAtEnd)
            LogTextBox.ScrollToEnd();
    }

    private static string NormalizeLogText(string text) {
        var builder = new StringBuilder(text.Length);
        foreach (char ch in text) {
            if (ch == '\r' || ch == '\n' || ch == '\t' || ch >= ' ')
                builder.Append(ch <= '\u00FF' ? ch : "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            else
                builder.Append(' ');
        }
        return builder.ToString();
    }

    private void RecordDiagnostics(string? text) {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        _logLines += Math.Max(1, lines.Length);

        string lower = text.ToLowerInvariant();
        PulseService("LmVsProxy");
        if (lower.Contains("[request]"))
            _requestCount++;
        if (lower.Contains("[responses bridge]") || lower.Contains("/v1/responses"))
            _responsesBridgeCount++;
        if (lower.Contains("tool call") || lower.Contains("tool_calls") || lower.Contains("[tool calls]"))
            _toolCallCount++;
        if (lower.Contains("stream"))
            _streamCount++;
        if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("exception") || lower.Contains("invalid"))
            _errorLines++;

        if (lower.Contains("lm studio")) {
            _lmStudioEventCount++;
            PulseService("LM Studio");
        }
        if (lower.Contains("[chat ui]")) {
            _chatUiEventCount += Math.Max(1, lines.Length);
            _activePipelineKind = "web-chat";
            PulseService("Web Client UI", "LmVsProxy", "LM Studio");
        }
        if (lower.Contains("vs copilot") || lower.Contains("/v1/responses") || lower.Contains("[http]")) {
            _activePipelineKind = "vs-copilot";
            PulseService("VS Copilot", "LmVsProxy", "LM Studio");
        }
        if (lower.Contains("agent") || lower.Contains("tool call") || lower.Contains("tool_calls")) {
            _agentServiceEventCount++;
            PulseService("Agent Tools", "LmVsProxy", "LM Studio");
            if (lower.Contains("[chat ui]"))
                _activePipelineKind = "agent";
        }
        if (lower.Contains("internet_search") || lower.Contains("searchinternet") || lower.Contains("[proxy research]")) {
            _internetSearchEventCount++;
            _activePipelineKind = "internet-search";
            PulseService("Internet Search", "Agent Tools", "LmVsProxy", "LM Studio");
        }
        if (lower.Contains("reflectionservice") || lower.Contains("reflection service") || lower.Contains("/reflect")) {
            _reflectionServiceEventCount++;
            _activePipelineKind = "reflection";
            PulseService("Reflection", "LmVsProxy", "LM Studio");
        }
        if (lower.Contains("prompt intellisense")) {
            _promptIntellisenseEventCount++;
            _activePipelineKind = "prompt-intellisense";
            PulseService("Prompt Intellisense", "Web Client UI");
        }
        if (lower.Contains("file upload") || lower.Contains("session file") || lower.Contains("download_file") || lower.Contains("sessionfiles")) {
            _sessionFileEventCount++;
            PulseService("Session Files", "Agent Tools");
        }
        if (lower.Contains("terminal service") || lower.Contains("[terminal permission]") || lower.Contains("run_command_in_terminal")) {
            _terminalServiceEventCount++;
            _activePipelineKind = "terminal";
            PulseService("Terminal", "LmVsProxy", "Agent Tools");
        }
        if (lower.Contains("ftp")) {
            _ftpEventCount++;
            _activePipelineKind = "ftp";
            PulseService("FTP", "Session Files");
        }
        if (lower.Contains("port forwarding")) {
            _portForwardingEventCount++;
            PulseService("Port Forwarding");
        }

        _lastEventText = "Last event: " + TrimForDisplay(lines[^1], 180);
    }

    private void PulseService(params string[] names) {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string name in names) {
            if (!string.IsNullOrWhiteSpace(name))
                _servicePulseUtc[name] = now;
        }
    }

    private bool WasServicePulsed(string name, DateTimeOffset now) {
        return !string.IsNullOrWhiteSpace(name) &&
               _servicePulseUtc.TryGetValue(name, out DateTimeOffset lastPulse) &&
               now - lastPulse <= TimeSpan.FromMilliseconds(950);
    }

    private void RefreshMetrics() {
        bool running = _proxy.IsListening;
        StartStopButton.Content = running ? "Stop Proxy" : "Start Proxy";
        StartStopButton.Background = running ? new SolidColorBrush(Color.FromRgb(137, 52, 66)) : new SolidColorBrush(Color.FromRgb(31, 122, 104));
        OpenChatButton.IsEnabled = _proxy.IsChatServerCreated && _proxy.ChatServer.IsListening;
        RemoteServerTextBox.IsEnabled = RemoteLmStudioCheckBox.IsChecked.GetValueOrDefault(false) && !running;
        UpdateGpuTdpBillingText();
        UpdateCpuTdpBillingText();
        UpdateChatBandwidthMetrics();
        UpdateHealthMetrics();
        RefreshSessionsPanel(false);
        RefreshWorkspaceExplorer(false);
        RefreshServicesPanel();
    }

    private void RefreshSessionsPanel(bool force) {
        try {
            IReadOnlyList<ActivePromptSessionDiagnosticsSnapshot> sessions = _proxy.GetActivePromptSessionDiagnostics();
            var items = sessions.Select(ChatSessionListItem.FromPromptSnapshot).ToList();
            string signature = string.Join("|", items.Select(item => item.Signature));
            if (force || !string.Equals(signature, _sessionListSignature, StringComparison.Ordinal)) {
                string selectedSessionId = _selectedSessionId;
                _sessionItems.Clear();
                foreach (ChatSessionListItem item in items)
                    _sessionItems.Add(item);
                _sessionListSignature = signature;

                ChatSessionListItem? selectedItem = null;
                if (!string.IsNullOrWhiteSpace(selectedSessionId))
                    selectedItem = _sessionItems.FirstOrDefault(item => string.Equals(item.Id, selectedSessionId, StringComparison.Ordinal));
                selectedItem ??= _sessionItems.FirstOrDefault();
                if (selectedItem != null && !ReferenceEquals(SessionsListBox.SelectedItem, selectedItem)) {
                    SessionsListBox.SelectedItem = selectedItem;
                    _selectedSessionId = selectedItem.Id;
                    _selectedOwnerKey = selectedItem.OwnerKey;
                } else if (selectedItem != null) {
                    _selectedSessionId = selectedItem.Id;
                    _selectedOwnerKey = selectedItem.OwnerKey;
                } else if (string.IsNullOrWhiteSpace(_selectedOwnerKey)) {
                    _selectedSessionId = "";
                    _selectedOwnerKey = "";
                }
            }

            SessionsSummaryText.Text = items.Count == 1
                ? "1 current LLM prompt session"
                : items.Count + " current LLM prompt sessions";
        } catch (Exception ex) {
            SessionsSummaryText.Text = "Sessions unavailable: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private void RefreshStoredSessionsMenuItems(bool force) {
        try {
            IReadOnlyList<ChatSessionDiagnosticsSnapshot> sessions = _proxy.GetChatSessionDiagnostics();
            var items = sessions.Select(ChatSessionListItem.FromSnapshot).ToList();
            string signature = string.Join("|", items.Select(item => item.Signature));
            if (!force && string.Equals(signature, _storedSessionListSignature, StringComparison.Ordinal))
                return;

            _storedSessionItems.Clear();
            foreach (ChatSessionListItem item in items)
                _storedSessionItems.Add(item);
            _storedSessionListSignature = signature;
        } catch {
            _storedSessionItems.Clear();
            _storedSessionListSignature = "";
        }
    }

    private void RefreshWorkspaceExplorer(bool force) {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - _lastWorkspaceRefreshUtc < TimeSpan.FromSeconds(5))
            return;

        _lastWorkspaceRefreshUtc = now;

        try {
            List<AccessibleDirectoryListItem> directories = RefreshAccessibleDirectories(force);
            string ownerKey = ResolveActiveFilesystemOwnerKey();
            string signature = BuildExplorerSignature(ownerKey, directories);
            if (!force && string.Equals(signature, _workspaceTreeSignature, StringComparison.Ordinal))
                return;

            _workspaceTreeSignature = signature;
            _workspaceTreeItems.Clear();

            if (directories.Count == 0) {
                _workspaceTreeItems.Add(new FileTreeItem {
                    DisplayName = "No accessible directories",
                    Detail = "Add a directory above to make it available to the Web UI agent.",
                    Icon = "[dir]",
                    AccentBrush = Brushes.SlateGray,
                    FontWeight = FontWeights.SemiBold,
                    IsExpanded = true
                });
            } else {
                bool first = true;
                foreach (AccessibleDirectoryListItem directory in directories) {
                    FileTreeItem node = BuildDirectoryTree(
                        directory.Path,
                        GetDirectoryDisplayName(directory.Path),
                        3,
                        first,
                        true);
                    if (!directory.Exists)
                        node = new FileTreeItem {
                            DisplayName = GetDirectoryDisplayName(directory.Path),
                            Detail = TrimPathForDisplay(directory.Path, 84) + " (missing)",
                            Icon = "[dir]",
                            AccentBrush = Brushes.SlateGray,
                            FontWeight = FontWeights.SemiBold,
                            IsExpanded = first
                        };

                    _workspaceTreeItems.Add(node);
                    first = false;
                }
            }

            WorkspaceSummaryText.Text = "Explorer roots: " + directories.Count + " accessible director" + (directories.Count == 1 ? "y" : "ies");
        } catch (Exception ex) {
            WorkspaceSummaryText.Text = "Explorer unavailable: " + TrimForDisplay(ex.Message, 120);
        }
    }

    private List<AccessibleDirectoryListItem> RefreshAccessibleDirectories(bool force) {
        string ownerKey = ResolveActiveFilesystemOwnerKey();
        try {
            IReadOnlyList<ChatFilesystemAccessSnapshot> snapshots =
                _proxy.GetChatFilesystemAccessDiagnostics(ownerKey);
            List<AccessibleDirectoryListItem> items = snapshots.Select(AccessibleDirectoryListItem.FromSnapshot).ToList();
            string signature = ownerKey + "\u001E" + string.Join("\u001F", items.Select(item => item.Signature));
            if (force || !string.Equals(signature, _accessibleDirectorySignature, StringComparison.Ordinal)) {
                _accessibleDirectoryItems.Clear();
                foreach (AccessibleDirectoryListItem item in items)
                    _accessibleDirectoryItems.Add(item);
                _accessibleDirectorySignature = signature;
            }

            FilesystemOwnerText.Text = "Owner: " + ownerKey;
            if (string.IsNullOrWhiteSpace(FilesystemAccessStatusText.Text) ||
                FilesystemAccessStatusText.Text.StartsWith("Loaded", StringComparison.OrdinalIgnoreCase) ||
                FilesystemAccessStatusText.Text.StartsWith("No", StringComparison.OrdinalIgnoreCase)) {
                FilesystemAccessStatusText.Text = items.Count == 0
                    ? "No accessible directories defined."
                    : "Loaded " + items.Count + " accessible director" + (items.Count == 1 ? "y." : "ies.");
            }
            return items;
        } catch (Exception ex) {
            FilesystemOwnerText.Text = "Owner: " + ownerKey;
            FilesystemAccessStatusText.Text = "Access list unavailable: " + TrimForDisplay(ex.Message, 120);
            return _accessibleDirectoryItems.ToList();
        }
    }

    private string ResolveActiveFilesystemOwnerKey() {
        if (!string.IsNullOrWhiteSpace(_selectedOwnerKey))
            return _selectedOwnerKey;

        ChatSessionListItem? selectedItem = SessionsListBox.SelectedItem as ChatSessionListItem;
        if (!string.IsNullOrWhiteSpace(selectedItem?.OwnerKey))
            return selectedItem.OwnerKey;

        string firstOwner = _sessionItems.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.OwnerKey))?.OwnerKey ?? "";
        return string.IsNullOrWhiteSpace(firstOwner) ? _proxy.GetDefaultLocalChatOwnerKey() : firstOwner;
    }

    private string ResolveSelectedSessionFilesPath() {
        string sessionRoot = _proxy.ChatSessionFilesRoot;
        if (!string.IsNullOrWhiteSpace(_selectedSessionId))
            return Path.Combine(sessionRoot, SanitizePathSegment(_selectedSessionId));

        return sessionRoot;
    }

    private static string BuildExplorerSignature(string ownerKey, IEnumerable<AccessibleDirectoryListItem> directories) {
        return (ownerKey ?? "") + "|" + string.Join("|", directories.Select(item => item.Path + "|" + item.Exists + "|" + BuildDirectorySignature(item.Path)));
    }

    private static string BuildDirectorySignature(string path) {
        try {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
                return path + "|missing";

            long fileCount = 0;
            long directoryCount = 0;
            try {
                fileCount = info.EnumerateFiles().LongCount();
                directoryCount = info.EnumerateDirectories().LongCount();
            } catch { }

            return info.FullName + "|" + info.LastWriteTimeUtc.Ticks + "|" + fileCount + "|" + directoryCount;
        } catch {
            return path + "|unavailable";
        }
    }

    private FileTreeItem BuildDirectoryTree(string path, string displayName, int maxDepth, bool expanded, bool applyDefaultExclusions) {
        var info = new DirectoryInfo(path);
        if (!info.Exists) {
            return new FileTreeItem {
                DisplayName = displayName,
                Detail = TrimPathForDisplay(path, 84) + " (not created yet)",
                Icon = "[dir]",
                AccentBrush = Brushes.SlateGray,
                FontWeight = FontWeights.SemiBold,
                IsExpanded = expanded
            };
        }

        var item = new FileTreeItem {
            DisplayName = displayName,
            Detail = TrimPathForDisplay(info.FullName, 84),
            Icon = "[dir]",
            AccentBrush = Brushes.DeepSkyBlue,
            FontWeight = FontWeights.SemiBold,
            IsExpanded = expanded
        };

        AddDirectoryChildren(item, info, maxDepth, applyDefaultExclusions);
        return item;
    }

    private void AddDirectoryChildren(FileTreeItem parent, DirectoryInfo directory, int remainingDepth, bool applyDefaultExclusions) {
        if (remainingDepth <= 0)
            return;

        const int maxChildrenPerDirectory = 80;
        int added = 0;

        try {
            foreach (DirectoryInfo childDirectory in directory.EnumerateDirectories()
                         .Where(item => !applyDefaultExclusions || !IsDefaultExplorerExcluded(item.Name))
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)) {
                if (added >= maxChildrenPerDirectory)
                    break;

                var child = new FileTreeItem {
                    DisplayName = childDirectory.Name,
                    Detail = FormatDirectoryDetail(childDirectory),
                    Icon = "[dir]",
                    AccentBrush = Brushes.DeepSkyBlue,
                    FontWeight = FontWeights.Normal,
                    IsExpanded = false
                };
                AddDirectoryChildren(child, childDirectory, remainingDepth - 1, applyDefaultExclusions);
                parent.Children.Add(child);
                added++;
            }

            foreach (FileInfo file in directory.EnumerateFiles()
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)) {
                if (added >= maxChildrenPerDirectory)
                    break;

                bool image = IsImageFile(file.Extension);
                parent.Children.Add(new FileTreeItem {
                    DisplayName = file.Name,
                    Detail = FormatBytes(file.Length) + " | " + file.LastWriteTime.ToString("M/d h:mm tt", CultureInfo.CurrentCulture),
                    Icon = image ? "[img]" : "[file]",
                    AccentBrush = image ? Brushes.MediumPurple : Brushes.LightSteelBlue,
                    FontWeight = FontWeights.Normal,
                    IsExpanded = false
                });
                added++;
            }

            int remaining = CountRemainingChildren(directory, maxChildrenPerDirectory, applyDefaultExclusions);
            if (remaining > 0) {
                parent.Children.Add(new FileTreeItem {
                    DisplayName = "... " + remaining + " more",
                    Detail = "Use Refresh after narrowing the folder.",
                    Icon = "[...]",
                    AccentBrush = Brushes.SlateGray,
                    FontWeight = FontWeights.Normal,
                    IsExpanded = false
                });
            }
        } catch (Exception ex) {
            parent.Children.Add(new FileTreeItem {
                DisplayName = "Unavailable",
                Detail = TrimForDisplay(ex.Message, 90),
                Icon = "[!]",
                AccentBrush = Brushes.IndianRed,
                FontWeight = FontWeights.Normal,
                IsExpanded = false
            });
        }
    }

    private static int CountRemainingChildren(DirectoryInfo directory, int maxChildren, bool applyDefaultExclusions) {
        try {
            int directories = directory.EnumerateDirectories()
                .Count(item => !applyDefaultExclusions || !IsDefaultExplorerExcluded(item.Name));
            int files = directory.EnumerateFiles().Count();
            return Math.Max(0, directories + files - maxChildren);
        } catch {
            return 0;
        }
    }

    private static bool IsDefaultExplorerExcluded(string name) {
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageFile(string extension) {
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDirectoryDetail(DirectoryInfo directory) {
        try {
            int directoryCount = directory.EnumerateDirectories().Take(51).Count();
            int fileCount = directory.EnumerateFiles().Take(51).Count();
            string directoryText = directoryCount > 50 ? "50+ folders" : directoryCount + " folders";
            string fileText = fileCount > 50 ? "50+ files" : fileCount + " files";
            return directoryText + ", " + fileText;
        } catch {
            return "Contents unavailable";
        }
    }

    private static string SanitizePathSegment(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        string result = value.Trim();
        foreach (char ch in Path.GetInvalidFileNameChars())
            result = result.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }

    private static string GetDirectoryDisplayName(string path) {
        if (string.IsNullOrWhiteSpace(path))
            return "Directory";

        try {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            string root = Path.GetPathRoot(path) ?? "";
            return string.IsNullOrWhiteSpace(root) ? path : root;
        } catch {
            return path;
        }
    }

    private static string TrimPathForDisplay(string path, int maxLength) {
        if (string.IsNullOrWhiteSpace(path))
            return "-";

        string compact = path.Trim();
        if (compact.Length <= maxLength)
            return compact;

        int keep = Math.Max(12, maxLength - 3);
        return "..." + compact[^keep..];
    }

    private void RefreshServicesPanel() {
        var services = BuildServiceMetricItems();
        string signature = string.Join("|", services.Select(item => item.Signature));
        if (!string.Equals(signature, _servicePanelSignature, StringComparison.Ordinal)) {
            _serviceMetricItems.Clear();
            foreach (ServiceMetricItem item in services)
                _serviceMetricItems.Add(item);
            _servicePanelSignature = signature;
        }

        var pipeline = BuildPipelineItems();
        string pipelineSignature = string.Join("|", pipeline.Select(item => item.Signature));
        if (!string.Equals(pipelineSignature, _pipelineSignature, StringComparison.Ordinal)) {
            _pipelineItems.Clear();
            foreach (PipelineNodeItem item in pipeline)
                _pipelineItems.Add(item);
            _pipelineSignature = pipelineSignature;
        }

        PipelineSummaryText.Text = BuildPipelineSummary();
    }

    private List<ServiceMetricItem> BuildServiceMetricItems() {
        bool running = _proxy.IsListening;
        bool chatRunning = _proxy.IsChatServerCreated && _proxy.ChatServer.IsListening;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string uptime = _startedAt.HasValue
            ? "Uptime: " + FormatDuration(DateTimeOffset.UtcNow - _startedAt.Value)
            : "Uptime: -";
        string remoteTunnel = _remoteLmStudioProxy?.IsRunning == true ? "Remote tunnel: running" : "Remote tunnel: off";
        (long chatBytesIn, long chatBytesOut, bool chatBytesAvailable) = GetChatServerBytes();

        return new List<ServiceMetricItem> {
            CreateServiceMetricItem("Web Client UI", chatRunning ? "Running" : "Stopped", GetChatServerClientText() + " | Events: " + _chatUiEventCount, _chatUiEndpointText + " | " + FormatChatServerByteText(chatBytesIn, chatBytesOut, chatBytesAvailable), now, _chatBandwidthInSnapshot, _chatBandwidthOutSnapshot, chatBytesAvailable),
            CreateServiceMetricItem("LmVsProxy", running ? "Running" : "Stopped", "Requests: " + _requestCount + " | Streams: " + _streamCount, _proxyEndpointText + " | " + uptime, now),
            CreateServiceMetricItem("LM Studio", _lmStatusText, _lmLatencyText + " | Events: " + _lmStudioEventCount, _lmEndpointText + " | " + remoteTunnel + " | " + _gpuTdpText, now),
            CreateServiceMetricItem("VS Copilot", running ? "Ready" : "Waiting", "Responses: " + _responsesBridgeCount + " | Tool calls: " + _toolCallCount, _vsEndpointText + " | Errors: " + _errorLines + " | " + _lastEventText, now),
            CreateServiceMetricItem("Agent Tools", _agentServiceEventCount > 0 ? "Used" : "Available", "Tool events: " + _toolCallCount, "Read, search, edit, list", now),
            CreateServiceMetricItem("Internet Search", _internetSearchEventCount > 0 ? "Used" : "Permission gated", "Events: " + _internetSearchEventCount, "SearchInternet/SearchService", now),
            CreateServiceMetricItem("Reflection", _reflectionServiceEventCount > 0 ? "Used" : "Available", "Events: " + _reflectionServiceEventCount, "Assembly/type inspection", now),
            CreateServiceMetricItem("Prompt Intellisense", _promptIntellisenseEventCount > 0 ? "Used" : "Available", "Events: " + _promptIntellisenseEventCount, "Prompt metadata", now),
            CreateServiceMetricItem("Session Files", Directory.Exists(_proxy.ChatSessionFilesRoot) ? "Online" : "Not created", "Events: " + _sessionFileEventCount, TrimPathForDisplay(_proxy.ChatSessionFilesRoot, 36), now),
            CreateServiceMetricItem("Terminal", _terminalServiceEventCount > 0 ? "Used" : "Approval gated", "Events: " + _terminalServiceEventCount, "PowerShell/console commands", now),
            CreateServiceMetricItem("FTP", _ftpEventCount > 0 ? "Used" : "Optional", "Events: " + _ftpEventCount, "Chat file transport", now),
            CreateServiceMetricItem("Port Forwarding", _portForwardingEnabled ? (_portForwardingSucceeded ? "Forwarded" : "Enabled") : "Disabled", "Events: " + _portForwardingEventCount, "TCP " + FormatForwardedPorts(), now)
        };
    }

    private ServiceMetricItem CreateServiceMetricItem(string name, string status, string countLine, string detail, DateTimeOffset now) {
        return new ServiceMetricItem(name, status, countLine, detail, WasServicePulsed(name, now), BandwidthMetricSnapshot.Empty("Inbound"), BandwidthMetricSnapshot.Empty("Outbound"), false);
    }

    private ServiceMetricItem CreateServiceMetricItem(string name, string status, string countLine, string detail, DateTimeOffset now, BandwidthMetricSnapshot bandwidthIn, BandwidthMetricSnapshot bandwidthOut, bool hasBandwidth) {
        return new ServiceMetricItem(name, status, countLine, detail, WasServicePulsed(name, now), bandwidthIn, bandwidthOut, hasBandwidth);
    }

    private List<PipelineNodeItem> BuildPipelineItems() {
        string[] names = _activePipelineKind switch {
            "web-chat" => new[] { "Web UI Chat", "LmVsProxy", "LM Studio" },
            "agent" => new[] { "Web UI Chat", "Agent Tools", "LmVsProxy", "LM Studio" },
            "internet-search" => new[] { "Client", "LmVsProxy", "Internet Search", "LM Studio" },
            "reflection" => new[] { "Client", "LmVsProxy", "Reflection", "LM Studio" },
            "prompt-intellisense" => new[] { "Prompt", "Prompt Intellisense", "Web UI Chat" },
            "terminal" => new[] { "Web UI Chat", "Terminal", "LmVsProxy", "LM Studio" },
            "ftp" => new[] { "Web UI Chat", "FTP", "Session Files" },
            "vs-copilot" => new[] { "VS Copilot", "LmVsProxy", "LM Studio" },
            _ => _proxy.IsListening
                ? new[] { "VS Copilot", "LmVsProxy", "LM Studio" }
                : new[] { "Waiting", "LmVsProxy", "LM Studio" }
        };

        var items = new List<PipelineNodeItem>();
        for (int i = 0; i < names.Length; i++) {
            bool last = i == names.Length - 1;
            items.Add(new PipelineNodeItem {
                Name = names[i],
                Detail = GetPipelineNodeDetail(names[i]),
                ArrowVisibility = last ? Visibility.Collapsed : Visibility.Visible,
                BackgroundBrush = i == 0 ? new SolidColorBrush(Color.FromRgb(19, 39, 54)) : new SolidColorBrush(Color.FromRgb(17, 25, 36)),
                BorderBrush = names[i].Equals("LM Studio", StringComparison.OrdinalIgnoreCase) ? _lmStatusBrush : new SolidColorBrush(Color.FromRgb(67, 94, 116))
            });
        }

        return items;
    }

    private string BuildPipelineSummary() {
        return _activePipelineKind switch {
            "web-chat" => "Current pipeline: browser chat to LM Studio",
            "agent" => "Current pipeline: browser agent tools to LM Studio",
            "internet-search" => "Current pipeline: search service feeding LM Studio",
            "reflection" => "Current pipeline: reflection service feeding LM Studio",
            "prompt-intellisense" => "Current pipeline: prompt suggestions",
            "ftp" => "Current pipeline: file transfer",
            "vs-copilot" => "Current pipeline: VS Copilot to LM Studio",
            _ => _proxy.IsListening ? "Pipeline ready: clients can route through LmVsProxy" : "Pipeline idle"
        };
    }

    private static string GetPipelineNodeDetail(string name) {
        return name switch {
            "VS Copilot" => "client",
            "Web UI Chat" => "client",
            "LmVsProxy" => "bridge",
            "LM Studio" => "model",
            "Agent Tools" => "tools",
            "Internet Search" => "service",
            "Reflection" => "service",
            "Prompt Intellisense" => "service",
            "FTP" => "service",
            "Session Files" => "storage",
            "Prompt" => "input",
            _ => "idle"
        };
    }

    private string GetChatServerClientText() {
        if (!_proxy.IsChatServerCreated)
            return "Chat clients: -";

        try {
            return "Chat clients: " + _proxy.ChatServer.Clients.Count;
        } catch {
            return "Chat clients: unavailable";
        }
    }

    private string GetChatServerByteText() {
        if (!_proxy.IsChatServerCreated)
            return "HTTP bytes: -";

        try {
            long received = _proxy.ChatServer.HttpTotalBytesReceived;
            long sent = _proxy.ChatServer.HttpTotalBytesSent;
            return FormatChatServerByteText(received, sent, true);
        } catch {
            return "HTTP bytes: unavailable";
        }
    }

    private (long Received, long Sent, bool Available) GetChatServerBytes() {
        if (!_proxy.IsChatServerCreated)
            return (0, 0, false);

        try {
            return (_proxy.ChatServer.HttpTotalBytesReceived, _proxy.ChatServer.HttpTotalBytesSent, true);
        } catch {
            return (0, 0, false);
        }
    }

    private void UpdateChatBandwidthMetrics() {
        (long received, long sent, bool available) = GetChatServerBytes();
        if (!available) {
            ResetChatBandwidthMetrics();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_lastChatBandwidthSampleUtc.HasValue ||
            received < _lastChatBandwidthBytesIn ||
            sent < _lastChatBandwidthBytesOut) {
            _lastChatBandwidthSampleUtc = now;
            _lastChatBandwidthBytesIn = Math.Max(0, received);
            _lastChatBandwidthBytesOut = Math.Max(0, sent);
            _chatBandwidthInSnapshot = _chatBandwidthInTracker.Update(0, received);
            _chatBandwidthOutSnapshot = _chatBandwidthOutTracker.Update(0, sent);
            return;
        }

        double elapsedSeconds = Math.Max(0.001, (now - _lastChatBandwidthSampleUtc.Value).TotalSeconds);
        long receivedDelta = Math.Max(0, received - _lastChatBandwidthBytesIn);
        long sentDelta = Math.Max(0, sent - _lastChatBandwidthBytesOut);
        double inboundKbps = Math.Max(0, receivedDelta * 8.0 / elapsedSeconds / 1000.0);
        double outboundKbps = Math.Max(0, sentDelta * 8.0 / elapsedSeconds / 1000.0);

        _lastChatBandwidthSampleUtc = now;
        _lastChatBandwidthBytesIn = received;
        _lastChatBandwidthBytesOut = sent;
        _chatBandwidthInSnapshot = _chatBandwidthInTracker.Update(inboundKbps, received);
        _chatBandwidthOutSnapshot = _chatBandwidthOutTracker.Update(outboundKbps, sent);

        if (receivedDelta > 0 || sentDelta > 0)
            PulseService("Web Client UI", "LmVsProxy");
    }

    private void ResetChatBandwidthMetrics() {
        _lastChatBandwidthSampleUtc = null;
        _lastChatBandwidthBytesIn = 0;
        _lastChatBandwidthBytesOut = 0;
        _chatBandwidthInTracker.Reset();
        _chatBandwidthOutTracker.Reset();
        _chatBandwidthInSnapshot = BandwidthMetricSnapshot.Empty("Inbound");
        _chatBandwidthOutSnapshot = BandwidthMetricSnapshot.Empty("Outbound");
    }

    private void UpdateHealthMetrics() {
        UpdateGpuHealthMetrics();
        UpdateRamHealthMetric();
        UpdateCpuHealthMetric();
        UpdateNetworkHealthMetrics();
        UpdateIoHealthMetric();
    }

    private void UpdateGpuHealthMetrics() {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastGpuHealthSampleUtc >= TimeSpan.FromSeconds(4)) {
            _lastGpuHealthSampleUtc = now;
            RefreshGpuHealthSnapshot();
        }

        GpuUtilizationHealthText.Text = _gpuUtilizationHealthText;
        VramUsageHealthText.Text = _vramUsageHealthText;
        ApplyPercentProgressBar(GpuUtilizationProgressBar, _gpuUtilizationPercent, "GPU utilization");
        ApplyPercentProgressBar(VramUsageProgressBar, _vramUsagePercent, "VRAM usage");
    }

    private void RefreshGpuHealthSnapshot() {
        try {
            var startInfo = new ProcessStartInfo {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            if (process == null) {
                SetGpuHealthUnavailable();
                return;
            }

            if (!process.WaitForExit(750)) {
                TryKillProcess(process);
                SetGpuHealthUnavailable();
                return;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) {
                SetGpuHealthUnavailable();
                return;
            }

            string? firstLine = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine)) {
                SetGpuHealthUnavailable();
                return;
            }

            string[] parts = firstLine.Split(',').Select(part => part.Trim()).ToArray();
            if (parts.Length < 3 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double utilization) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double memoryUsedMb) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double memoryTotalMb) ||
                memoryTotalMb <= 0) {
                SetGpuHealthUnavailable();
                return;
            }

            double memoryPercent = Math.Clamp(memoryUsedMb / memoryTotalMb * 100.0, 0, 100);
            _gpuUtilizationPercent = Math.Clamp(utilization, 0, 100);
            _vramUsagePercent = memoryPercent;
            _gpuUtilizationHealthText = FormatPercent(utilization);
            _vramUsageHealthText = memoryUsedMb.ToString("0", CultureInfo.CurrentCulture) + "/" +
                                   memoryTotalMb.ToString("0", CultureInfo.CurrentCulture) + " MB (" +
                                   FormatPercent(memoryPercent) + ")";
        } catch {
            SetGpuHealthUnavailable();
        }
    }

    private void SetGpuHealthUnavailable() {
        _gpuUtilizationPercent = 0;
        _vramUsagePercent = 0;
        _gpuUtilizationHealthText = "unavailable";
        _vramUsageHealthText = "unavailable";
    }

    private static void TryKillProcess(Process process) {
        try {
            process.Kill(entireProcessTree: true);
        } catch {
        }
    }

    private void UpdateRamHealthMetric() {
        var status = new MemoryStatusEx {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) {
            RamUsageHealthText.Text = "unavailable";
            _ramUsagePercent = 0;
            ApplyPercentProgressBar(RamUsageProgressBar, 0, "RAM usage unavailable");
            return;
        }

        ulong used = status.TotalPhys > status.AvailPhys ? status.TotalPhys - status.AvailPhys : 0;
        _ramUsagePercent = Math.Clamp(status.MemoryLoad, 0, 100);
        RamUsageHealthText.Text = FormatPercent(status.MemoryLoad) + " (" +
                                  FormatBytes(ToDisplayByteCount(used)) + "/" +
                                  FormatBytes(ToDisplayByteCount(status.TotalPhys)) + ")";
        ApplyPercentProgressBar(RamUsageProgressBar, _ramUsagePercent, "RAM usage");
    }

    private void UpdateCpuHealthMetric() {
        if (!GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime)) {
            CpuUsageHealthText.Text = "unavailable";
            _cpuUsagePercent = 0;
            ApplyPercentProgressBar(CpuUsageProgressBar, 0, "CPU usage unavailable");
            return;
        }

        ulong idleTicks = ToUInt64(idleTime);
        ulong kernelTicks = ToUInt64(kernelTime);
        ulong userTicks = ToUInt64(userTime);
        if (!_lastCpuIdleTicks.HasValue ||
            !_lastCpuKernelTicks.HasValue ||
            !_lastCpuUserTicks.HasValue ||
            idleTicks < _lastCpuIdleTicks.Value ||
            kernelTicks < _lastCpuKernelTicks.Value ||
            userTicks < _lastCpuUserTicks.Value) {
            _lastCpuIdleTicks = idleTicks;
            _lastCpuKernelTicks = kernelTicks;
            _lastCpuUserTicks = userTicks;
            CpuUsageHealthText.Text = "measuring";
            _cpuUsagePercent = 0;
            ApplyPercentProgressBar(CpuUsageProgressBar, 0, "CPU usage measuring");
            return;
        }

        ulong idleDelta = idleTicks - _lastCpuIdleTicks.Value;
        ulong kernelDelta = kernelTicks - _lastCpuKernelTicks.Value;
        ulong userDelta = userTicks - _lastCpuUserTicks.Value;
        ulong totalDelta = kernelDelta + userDelta;
        double usagePercent = totalDelta == 0
            ? 0
            : Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100.0, 0, 100);

        _lastCpuIdleTicks = idleTicks;
        _lastCpuKernelTicks = kernelTicks;
        _lastCpuUserTicks = userTicks;
        _cpuUsagePercent = usagePercent;
        CpuUsageHealthText.Text = FormatPercent(usagePercent);
        ApplyPercentProgressBar(CpuUsageProgressBar, _cpuUsagePercent, "CPU usage");
    }

    private void UpdateNetworkHealthMetrics() {
        if (!TryGetNetworkByteTotals(out long bytesUp, out long bytesDown)) {
            ResetNetworkHealthMetrics();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_lastNetworkSampleUtc.HasValue ||
            bytesUp < _lastNetworkBytesUp ||
            bytesDown < _lastNetworkBytesDown) {
            _lastNetworkSampleUtc = now;
            _lastNetworkBytesUp = Math.Max(0, bytesUp);
            _lastNetworkBytesDown = Math.Max(0, bytesDown);
            _networkUpSnapshot = _networkUpTracker.Update(0, bytesUp);
            _networkDownSnapshot = _networkDownTracker.Update(0, bytesDown);
            ApplyNetworkHealthText();
            return;
        }

        double elapsedSeconds = Math.Max(0.001, (now - _lastNetworkSampleUtc.Value).TotalSeconds);
        long upDelta = Math.Max(0, bytesUp - _lastNetworkBytesUp);
        long downDelta = Math.Max(0, bytesDown - _lastNetworkBytesDown);
        double upKbps = Math.Max(0, upDelta * 8.0 / elapsedSeconds / 1000.0);
        double downKbps = Math.Max(0, downDelta * 8.0 / elapsedSeconds / 1000.0);

        _lastNetworkSampleUtc = now;
        _lastNetworkBytesUp = bytesUp;
        _lastNetworkBytesDown = bytesDown;
        _networkUpSnapshot = _networkUpTracker.Update(upKbps, bytesUp);
        _networkDownSnapshot = _networkDownTracker.Update(downKbps, bytesDown);
        ApplyNetworkHealthText();
    }

    private void ResetNetworkHealthMetrics() {
        _lastNetworkSampleUtc = null;
        _lastNetworkBytesUp = 0;
        _lastNetworkBytesDown = 0;
        _networkUpTracker.Reset();
        _networkDownTracker.Reset();
        _networkUpSnapshot = BandwidthMetricSnapshot.Empty("Network Up");
        _networkDownSnapshot = BandwidthMetricSnapshot.Empty("Network Down");
        ApplyNetworkHealthText();
    }

    private void ApplyNetworkHealthText() {
        NetworkUpHealthText.Text = FormatKbps(_networkUpSnapshot.LiveKbps);
        NetworkDownHealthText.Text = FormatKbps(_networkDownSnapshot.LiveKbps);
        ApplyBandwidthProgressBar(NetworkUpProgressBar, _networkUpSnapshot);
        ApplyBandwidthProgressBar(NetworkDownProgressBar, _networkDownSnapshot);
    }

    private void UpdateIoHealthMetric() {
        if (!TryGetProcessIoBytes(out ulong readBytes, out ulong writeBytes, out ulong totalBytes)) {
            ResetIoHealthMetric();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_lastIoSampleUtc.HasValue ||
            readBytes < _lastIoReadBytes ||
            writeBytes < _lastIoWriteBytes) {
            _lastIoSampleUtc = now;
            _lastIoReadBytes = readBytes;
            _lastIoWriteBytes = writeBytes;
            _ioReadBytesPerSecond = 0;
            _ioWriteBytesPerSecond = 0;
            _ioSnapshot = _ioTracker.Update(0, ToDisplayByteCount(totalBytes));
            ApplyIoHealthText();
            return;
        }

        double elapsedSeconds = Math.Max(0.001, (now - _lastIoSampleUtc.Value).TotalSeconds);
        ulong readDelta = readBytes - _lastIoReadBytes;
        ulong writeDelta = writeBytes - _lastIoWriteBytes;
        ulong totalDelta = SaturatingAdd(readDelta, writeDelta);
        _ioReadBytesPerSecond = Math.Max(0, readDelta / elapsedSeconds);
        _ioWriteBytesPerSecond = Math.Max(0, writeDelta / elapsedSeconds);
        double bytesPerSecond = Math.Max(0, totalDelta / elapsedSeconds);

        _lastIoSampleUtc = now;
        _lastIoReadBytes = readBytes;
        _lastIoWriteBytes = writeBytes;
        _ioSnapshot = _ioTracker.Update(bytesPerSecond, ToDisplayByteCount(totalBytes));
        ApplyIoHealthText();
    }

    private void ResetIoHealthMetric() {
        _lastIoSampleUtc = null;
        _lastIoReadBytes = 0;
        _lastIoWriteBytes = 0;
        _ioReadBytesPerSecond = 0;
        _ioWriteBytesPerSecond = 0;
        _ioTracker.Reset();
        _ioSnapshot = RateMetricSnapshot.Empty("I/O");
        ApplyIoHealthText();
    }

    private void ApplyIoHealthText() {
        IoReadHealthText.Text = FormatBytesPerSecond(_ioReadBytesPerSecond);
        IoWriteHealthText.Text = FormatBytesPerSecond(_ioWriteBytesPerSecond);
        IoTotalHealthText.Text = FormatBytesPerSecond(_ioSnapshot.LiveBytesPerSecond);
        ApplyRateProgressBar(IoProgressBar, _ioSnapshot);
    }

    private static void ApplyPercentProgressBar(System.Windows.Controls.ProgressBar progressBar, double percent, string label) {
        double value = double.IsNaN(percent) || double.IsInfinity(percent) ? 0 : Math.Clamp(percent, 0, 100);
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;
        progressBar.Value = value;
        progressBar.ToolTip = label + ": " + FormatPercent(value);
    }

    private static void ApplyBandwidthProgressBar(System.Windows.Controls.ProgressBar progressBar, BandwidthMetricSnapshot snapshot) {
        progressBar.Minimum = snapshot.MinimumKbps;
        progressBar.Maximum = snapshot.MaximumKbps;
        progressBar.Value = snapshot.ValueKbps;
        progressBar.ToolTip = snapshot.ToolTip;
    }

    private static void ApplyRateProgressBar(System.Windows.Controls.ProgressBar progressBar, RateMetricSnapshot snapshot) {
        progressBar.Minimum = snapshot.MinimumBytesPerSecond;
        progressBar.Maximum = snapshot.MaximumBytesPerSecond;
        progressBar.Value = snapshot.ValueBytesPerSecond;
        progressBar.ToolTip = snapshot.ToolTip;
    }

    private static bool TryGetNetworkByteTotals(out long bytesUp, out long bytesDown) {
        bytesUp = 0;
        bytesDown = 0;
        bool found = false;

        try {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                IPv4InterfaceStatistics statistics = networkInterface.GetIPv4Statistics();
                bytesUp = SaturatingAdd(bytesUp, Math.Max(0, statistics.BytesSent));
                bytesDown = SaturatingAdd(bytesDown, Math.Max(0, statistics.BytesReceived));
                found = true;
            }
        } catch {
            return false;
        }

        return found;
    }

    private static bool TryGetProcessIoBytes(out ulong readBytes, out ulong writeBytes, out ulong totalBytes) {
        readBytes = 0;
        writeBytes = 0;
        totalBytes = 0;
        try {
            using Process currentProcess = Process.GetCurrentProcess();
            if (!GetProcessIoCounters(currentProcess.Handle, out ProcessIoCounters counters))
                return false;

            readBytes = counters.ReadTransferCount;
            writeBytes = counters.WriteTransferCount;
            totalBytes = SaturatingAdd(readBytes, writeBytes);
            return true;
        } catch {
            return false;
        }
    }

    private static string FormatChatServerByteText(long received, long sent, bool available) {
        if (!available)
            return "HTTP bytes: -";
        return "HTTP bytes: in " + FormatBytes(received) + " / out " + FormatBytes(sent);
    }

    private void InitializeStaticText() {
        _lmEndpointText = "Endpoint: http://localhost:" + LocalLmStudioProxyPort + "/v1/chat/completions";
        _proxyEndpointText = "Proxy: http://localhost:" + ServerPort + "/v1/chat/completions";
        _chatUiEndpointText = "Chat UI: http://localhost:" + ChatServerPort + "/";
        _vsEndpointText = "Endpoint: http://localhost:" + ServerPort + "/v1/responses";
    }

    private void SetStatus(string text) {
        StatusBarText.Text = text;
    }

    private static string TrimForDisplay(string text, int maxLength) {
        text = (text ?? "").Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static string FormatDuration(TimeSpan value) {
        if (value.TotalHours >= 1)
            return value.ToString(@"hh\:mm\:ss");
        return value.ToString(@"mm\:ss");
    }

    private static string FormatBytes(long bytes) {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }

    private static string FormatPercent(double percent) {
        double value = double.IsNaN(percent) || double.IsInfinity(percent) ? 0 : Math.Clamp(percent, 0, 100);
        return value.ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    private static string FormatKbps(double kbps) {
        double value = SanitizeBandwidthValue(kbps);
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " kbps";
    }

    private static string FormatBytesPerSecond(double bytesPerSecond) {
        double value = SanitizeRateValue(bytesPerSecond);
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static long ToDisplayByteCount(ulong bytes) {
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }

    private static long SaturatingAdd(long left, long right) {
        if (right > long.MaxValue - left)
            return long.MaxValue;
        return left + right;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) {
        if (right > ulong.MaxValue - left)
            return ulong.MaxValue;
        return left + right;
    }

    private static ulong ToUInt64(NativeFileTime value) {
        return ((ulong)(uint)value.HighDateTime << 32) | (uint)value.LowDateTime;
    }

    private static double SanitizeBandwidthValue(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Max(0, value);
    }

    private static double SanitizeRateValue(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Max(0, value);
    }

    private static bool TryEnableAccentBlur(IntPtr handle) {
        try {
            var accent = new AccentPolicy {
                AccentState = AccentState.AccentEnableBlurBehind,
                AccentFlags = 2,
                GradientColor = unchecked((int)0xA8160F0A)
            };
            int accentSize = Marshal.SizeOf<AccentPolicy>();
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData {
                    Attribute = WindowCompositionAttribute.WcaAccentPolicy,
                    Data = accentPtr,
                    SizeOfData = accentSize
                };
                return SetWindowCompositionAttribute(handle, ref data) != 0;
            } finally {
                Marshal.FreeHGlobal(accentPtr);
            }
        } catch {
            return false;
        }
    }

    private static bool TryEnableDwmBlur(IntPtr handle) {
        try {
            var blur = new DwmBlurBehind {
                Flags = DwmBbEnable,
                Enable = true,
                BlurRegion = IntPtr.Zero,
                TransitionOnMaximized = true
            };
            bool enabled = DwmEnableBlurBehindWindow(handle, ref blur) == 0;
            var margins = new DwmMargins {
                LeftWidth = -1,
                RightWidth = -1,
                TopHeight = -1,
                BottomHeight = -1
            };
            return DwmExtendFrameIntoClientArea(handle, ref margins) == 0 || enabled;
        } catch {
            return false;
        }
    }

    private void ApplyNativeCornerPreference(bool maximized) {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        try {
            int preference = maximized
                ? (int)DwmWindowCornerPreference.DoNotRound
                : (int)DwmWindowCornerPreference.Round;
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowAttribute.WindowCornerPreference,
                ref preference,
                sizeof(int));
        } catch {
        }
    }

    private static void ApplyMaximizedWorkArea(IntPtr hwnd, IntPtr lParam) {
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MonitorInfo {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        NativeRect workArea = monitorInfo.WorkArea;
        NativeRect monitorArea = monitorInfo.MonitorArea;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorArea.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorArea.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize.X = minMaxInfo.MaxSize.X;
        minMaxInfo.MaxTrackSize.Y = minMaxInfo.MaxSize.Y;

        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int DwmBbEnable = 0x1;
    private const int MonitorDefaultToNearest = 0x00000002;

    private enum AccentState {
        AccentDisabled = 0,
        AccentEnableGradient = 1,
        AccentEnableTransparentGradient = 2,
        AccentEnableBlurBehind = 3,
        AccentEnableAcrylicBlurBehind = 4
    }

    private enum WindowCompositionAttribute {
        WcaAccentPolicy = 19
    }

    private enum DwmWindowAttribute {
        WindowCornerPreference = 33
    }

    private enum DwmWindowCornerPreference {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind {
        public int Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;
        public IntPtr BlurRegion;
        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIoCounters {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out ProcessIoCounters counters);

    private sealed class ChatSessionListItem {
        public string Id { get; init; } = "";
        public string OwnerKey { get; init; } = "";
        public string Title { get; init; } = "";
        public string OwnerLine { get; init; } = "";
        public string DetailLine { get; init; } = "";
        public string UpdatedLine { get; init; } = "";
        public string Signature { get; init; } = "";

        public static ChatSessionListItem FromPromptSnapshot(ActivePromptSessionDiagnosticsSnapshot snapshot) {
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "LLM prompt" : snapshot.Title.Trim();
            string source = string.IsNullOrWhiteSpace(snapshot.Source) ? "LLM Prompt" : snapshot.Source.Trim();
            string ownerKey = string.IsNullOrWhiteSpace(snapshot.OwnerKey) ? "" : snapshot.OwnerKey.Trim();
            string owner = string.IsNullOrWhiteSpace(ownerKey) ? "owner unknown" : ownerKey;
            string model = string.IsNullOrWhiteSpace(snapshot.Model) ? "model unknown" : snapshot.Model.Trim();
            string status = string.IsNullOrWhiteSpace(snapshot.Status) ? "Running" : snapshot.Status.Trim();
            string phase = string.IsNullOrWhiteSpace(snapshot.Phase) ? status : snapshot.Phase.Trim();
            string updated = FormatSessionDate(snapshot.UpdatedUtc);
            string elapsed = FormatDuration(TimeSpan.FromSeconds(Math.Max(0, snapshot.ElapsedSeconds)));

            return new ChatSessionListItem {
                Id = snapshot.Id,
                OwnerKey = ownerKey,
                Title = title,
                OwnerLine = source + " | Owner: " + owner,
                DetailLine = status + " | " + phase + " | " + model + " | " + elapsed,
                UpdatedLine = "Updated: " + updated,
                Signature = snapshot.Id + "\u001F" + title + "\u001F" + owner + "\u001F" + source + "\u001F" + status + "\u001F" + phase + "\u001F" + snapshot.UpdatedUtc + "\u001F" + snapshot.ElapsedSeconds + "\u001F" + model
            };
        }

        public static ChatSessionListItem FromSnapshot(ChatSessionDiagnosticsSnapshot snapshot) {
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "New chat" : snapshot.Title.Trim();
            string model = string.IsNullOrWhiteSpace(snapshot.Model) ? "model unknown" : snapshot.Model.Trim();
            string ownerKey = string.IsNullOrWhiteSpace(snapshot.OwnerKey) ? "" : snapshot.OwnerKey.Trim();
            string owner = string.IsNullOrWhiteSpace(ownerKey) ? "owner unknown" : ownerKey;
            string updated = FormatSessionDate(snapshot.UpdatedUtc);

            return new ChatSessionListItem {
                Id = snapshot.Id,
                OwnerKey = ownerKey,
                Title = title,
                OwnerLine = "Owner: " + owner,
                DetailLine = snapshot.MessageCount + " messages, " + snapshot.FileCount + " files, " + model,
                UpdatedLine = "Updated: " + updated,
                Signature = snapshot.Id + "\u001F" + title + "\u001F" + owner + "\u001F" + snapshot.UpdatedUtc + "\u001F" + snapshot.MessageCount + "\u001F" + snapshot.FileCount + "\u001F" + model
            };
        }

        private static string FormatSessionDate(string value) {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                return parsed.ToLocalTime().ToString("M/d/yyyy h:mm:ss tt", CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }

    private sealed class AccessibleDirectoryListItem {
        public string Path { get; init; } = "";
        public bool Exists { get; init; }
        public string Name { get; init; } = "";
        public string DetailLine { get; init; } = "";
        public Brush StatusBrush { get; init; } = Brushes.SlateGray;
        public string Signature { get; init; } = "";

        public static AccessibleDirectoryListItem FromSnapshot(ChatFilesystemAccessSnapshot snapshot) {
            string path = snapshot?.Path ?? "";
            bool exists = snapshot != null && snapshot.Exists;
            return new AccessibleDirectoryListItem {
                Path = path,
                Exists = exists,
                Name = GetDirectoryDisplayName(path),
                DetailLine = (exists ? "Accessible" : "Missing") + " | " + TrimPathForDisplay(path, 58),
                StatusBrush = exists ? Brushes.LimeGreen : Brushes.Orange,
                Signature = path + "\u001F" + exists + "\u001F" + (snapshot?.CreatedUtc ?? "")
            };
        }
    }

    private sealed class FilesystemPermissionRequestItem {
        public string Id { get; init; } = "";
        public string OwnerKey { get; init; } = "";
        public string DirectoryPath { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";

        public static FilesystemPermissionRequestItem FromSnapshot(FilesystemPermissionRequestSnapshot snapshot) {
            string owner = string.IsNullOrWhiteSpace(snapshot.OwnerKey) ? "unknown client" : snapshot.OwnerKey;
            string ip = string.IsNullOrWhiteSpace(snapshot.ClientIp) ? owner : snapshot.ClientIp;
            string operation = string.IsNullOrWhiteSpace(snapshot.Operation) ? "access files" : snapshot.Operation;
            string directory = string.IsNullOrWhiteSpace(snapshot.DirectoryPath) ? snapshot.FullPath : snapshot.DirectoryPath;
            return new FilesystemPermissionRequestItem {
                Id = snapshot.Id,
                OwnerKey = owner,
                DirectoryPath = directory,
                Title = "Filesystem permission requested by " + ip,
                Detail = operation + " | " + TrimPathForDisplay(snapshot.FullPath, 130) + Environment.NewLine +
                         "Directory: " + TrimPathForDisplay(directory, 130)
            };
        }
    }

    private sealed class TerminalPermissionRequestItem {
        public string Id { get; init; } = "";
        public string OwnerKey { get; init; } = "";
        public string ClientIp { get; init; } = "";
        public string Command { get; init; } = "";
        public string Shell { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";

        public static TerminalPermissionRequestItem FromSnapshot(TerminalPermissionRequestSnapshot snapshot) {
            string owner = string.IsNullOrWhiteSpace(snapshot.OwnerKey) ? "unknown client" : snapshot.OwnerKey;
            string ip = string.IsNullOrWhiteSpace(snapshot.ClientIp) ? owner : snapshot.ClientIp;
            string shell = string.IsNullOrWhiteSpace(snapshot.Shell) ? "powershell" : snapshot.Shell;
            string command = snapshot.Command ?? "";
            string workingDirectory = string.IsNullOrWhiteSpace(snapshot.WorkingDirectory) ? "default working directory" : snapshot.WorkingDirectory;
            string summary = string.IsNullOrWhiteSpace(snapshot.Summary) ? "Terminal command requested" : snapshot.Summary;
            return new TerminalPermissionRequestItem {
                Id = snapshot.Id,
                OwnerKey = owner,
                ClientIp = ip,
                Command = command,
                Shell = shell,
                WorkingDirectory = workingDirectory,
                Title = "Terminal command requested by " + ip,
                Detail = summary + Environment.NewLine +
                         shell + " | " + TrimForDisplay(command, 220) + Environment.NewLine +
                         "Working directory: " + TrimPathForDisplay(workingDirectory, 130)
            };
        }
    }

    private sealed class RegistrationRequestItem {
        public string Id { get; init; } = "";
        public string UserName { get; init; } = "";
        public string ClientIp { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";

        public static RegistrationRequestItem FromSnapshot(WebAuthRegistrationRequestSnapshot snapshot) {
            string userName = string.IsNullOrWhiteSpace(snapshot.UserName) ? "unknown user" : snapshot.UserName;
            string ip = string.IsNullOrWhiteSpace(snapshot.ClientIp) ? "unknown IP" : snapshot.ClientIp;
            return new RegistrationRequestItem {
                Id = snapshot.Id,
                UserName = userName,
                ClientIp = ip,
                Title = "Registration requested by " + userName,
                Detail = "Client IP: " + ip + Environment.NewLine +
                         "Requested: " + (string.IsNullOrWhiteSpace(snapshot.RequestedUtc) ? "-" : snapshot.RequestedUtc)
            };
        }
    }

    private sealed class FileTreeItem {
        public string DisplayName { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Icon { get; init; } = "[file]";
        public Brush AccentBrush { get; init; } = Brushes.LightSteelBlue;
        public FontWeight FontWeight { get; init; } = FontWeights.Normal;
        public bool IsExpanded { get; set; }
        public ObservableCollection<FileTreeItem> Children { get; } = new();
    }

    private sealed class BandwidthMetricTracker {
        private const int MaxSamples = 180;
        private readonly string _direction;
        private readonly Queue<double> _samples = new();
        private bool _hasRange;
        private double _rangeMinimumKbps;
        private double _rangeMaximumKbps;
        private double _recordedMinimumKbps;
        private double _recordedMaximumKbps;

        public BandwidthMetricTracker(string direction) {
            _direction = string.IsNullOrWhiteSpace(direction) ? "Bandwidth" : direction;
            Reset();
        }

        public void Reset() {
            _samples.Clear();
            _hasRange = false;
            _rangeMinimumKbps = 0;
            _rangeMaximumKbps = 100;
            _recordedMinimumKbps = double.NaN;
            _recordedMaximumKbps = 0;
        }

        public BandwidthMetricSnapshot Update(double liveKbps, long totalBytes) {
            liveKbps = SanitizeBandwidthValue(liveKbps);
            totalBytes = Math.Max(0, totalBytes);

            _samples.Enqueue(liveKbps);
            while (_samples.Count > MaxSamples)
                _samples.Dequeue();

            if (liveKbps > 0) {
                _recordedMinimumKbps = double.IsNaN(_recordedMinimumKbps)
                    ? liveKbps
                    : Math.Min(_recordedMinimumKbps, liveKbps);
            }
            _recordedMaximumKbps = Math.Max(_recordedMaximumKbps, liveKbps);

            double[] activeSamples = _samples.Where(sample => sample > 0).OrderBy(sample => sample).ToArray();
            double[] sortedSamples = activeSamples.Length > 0
                ? activeSamples
                : _samples.OrderBy(sample => sample).ToArray();

            double onePercentLow = Percentile(sortedSamples, 0.01);
            double onePercentHigh = Percentile(sortedSamples, 0.99);
            double targetMinimum = sortedSamples.Length == 0 ? 0 : Math.Min(liveKbps, onePercentLow);
            double targetMaximum = sortedSamples.Length == 0 ? Math.Max(1, liveKbps) : Math.Max(liveKbps, onePercentHigh);
            if (liveKbps <= 0)
                targetMinimum = 0;

            targetMinimum = SanitizeBandwidthValue(targetMinimum);
            targetMaximum = SanitizeBandwidthValue(targetMaximum);
            double minimumGap = Math.Max(1, targetMaximum * 0.08);
            if (targetMaximum - targetMinimum < minimumGap)
                targetMaximum = targetMinimum + minimumGap;

            if (!_hasRange) {
                _rangeMinimumKbps = targetMinimum;
                _rangeMaximumKbps = targetMaximum;
                _hasRange = true;
            } else {
                double minimumAlpha = targetMinimum < _rangeMinimumKbps ? 0.45 : 0.10;
                double maximumAlpha = targetMaximum > _rangeMaximumKbps ? 0.45 : 0.10;
                _rangeMinimumKbps = Interpolate(_rangeMinimumKbps, targetMinimum, minimumAlpha);
                _rangeMaximumKbps = Interpolate(_rangeMaximumKbps, targetMaximum, maximumAlpha);
            }

            _rangeMinimumKbps = SanitizeBandwidthValue(_rangeMinimumKbps);
            _rangeMaximumKbps = SanitizeBandwidthValue(_rangeMaximumKbps);
            if (_rangeMaximumKbps <= _rangeMinimumKbps)
                _rangeMaximumKbps = _rangeMinimumKbps + 1;

            double valueKbps = Math.Min(_rangeMaximumKbps, Math.Max(_rangeMinimumKbps, liveKbps));
            if (liveKbps > 0 && valueKbps <= _rangeMinimumKbps) {
                double visibleFloor = (_rangeMaximumKbps - _rangeMinimumKbps) * 0.05;
                valueKbps = Math.Min(_rangeMaximumKbps, _rangeMinimumKbps + Math.Max(0.01, visibleFloor));
            }

            return new BandwidthMetricSnapshot(
                _direction,
                liveKbps,
                _rangeMinimumKbps,
                _rangeMaximumKbps,
                valueKbps,
                double.IsNaN(_recordedMinimumKbps) ? 0 : _recordedMinimumKbps,
                _recordedMaximumKbps,
                onePercentLow,
                onePercentHigh,
                _samples.Count,
                totalBytes);
        }

        private static double Interpolate(double current, double target, double alpha) {
            alpha = Math.Min(1, Math.Max(0, alpha));
            return current + ((target - current) * alpha);
        }

        private static double Percentile(double[] sortedSamples, double percentile) {
            if (sortedSamples.Length == 0)
                return 0;

            double clampedPercentile = Math.Min(1, Math.Max(0, percentile));
            double position = clampedPercentile * (sortedSamples.Length - 1);
            int lower = Math.Max(0, Math.Min(sortedSamples.Length - 1, (int)Math.Floor(position)));
            int upper = Math.Max(0, Math.Min(sortedSamples.Length - 1, (int)Math.Ceiling(position)));
            if (lower == upper)
                return sortedSamples[lower];

            double weight = position - lower;
            return sortedSamples[lower] + ((sortedSamples[upper] - sortedSamples[lower]) * weight);
        }
    }

    private readonly struct BandwidthMetricSnapshot {
        public BandwidthMetricSnapshot(string direction, double liveKbps, double minimumKbps, double maximumKbps, double valueKbps, double recordedMinimumKbps, double recordedMaximumKbps, double onePercentLowKbps, double onePercentHighKbps, int sampleCount, long totalBytes) {
            Direction = string.IsNullOrWhiteSpace(direction) ? "Bandwidth" : direction;
            LiveKbps = SanitizeBandwidthValue(liveKbps);
            MinimumKbps = SanitizeBandwidthValue(minimumKbps);
            MaximumKbps = Math.Max(MinimumKbps + 1, SanitizeBandwidthValue(maximumKbps));
            ValueKbps = Math.Min(MaximumKbps, Math.Max(MinimumKbps, SanitizeBandwidthValue(valueKbps)));
            RecordedMinimumKbps = SanitizeBandwidthValue(recordedMinimumKbps);
            RecordedMaximumKbps = SanitizeBandwidthValue(recordedMaximumKbps);
            OnePercentLowKbps = SanitizeBandwidthValue(onePercentLowKbps);
            OnePercentHighKbps = SanitizeBandwidthValue(onePercentHighKbps);
            SampleCount = Math.Max(0, sampleCount);
            TotalBytes = Math.Max(0, totalBytes);
        }

        public string Direction { get; }
        public double LiveKbps { get; }
        public double MinimumKbps { get; }
        public double MaximumKbps { get; }
        public double ValueKbps { get; }
        public double RecordedMinimumKbps { get; }
        public double RecordedMaximumKbps { get; }
        public double OnePercentLowKbps { get; }
        public double OnePercentHighKbps { get; }
        public int SampleCount { get; }
        public long TotalBytes { get; }

        public string ToolTip =>
            Direction + Environment.NewLine +
            "Live: " + FormatKbps(LiveKbps) + Environment.NewLine +
            "Adaptive min/max: " + FormatKbps(MinimumKbps) + " / " + FormatKbps(MaximumKbps) + Environment.NewLine +
            "Recorded min/max: " + FormatKbps(RecordedMinimumKbps) + " / " + FormatKbps(RecordedMaximumKbps) + Environment.NewLine +
            "1% low/high: " + FormatKbps(OnePercentLowKbps) + " / " + FormatKbps(OnePercentHighKbps) + Environment.NewLine +
            "Total: " + FormatBytes(TotalBytes);

        public static BandwidthMetricSnapshot Empty(string direction) {
            return new BandwidthMetricSnapshot(direction, 0, 0, 100, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class RateMetricTracker {
        private const int MaxSamples = 60;
        private readonly string _name;
        private readonly Queue<double> _samples = new();
        private bool _hasRange;
        private double _rangeMinimumBytesPerSecond;
        private double _rangeMaximumBytesPerSecond;
        private double _recordedMinimumBytesPerSecond;
        private double _recordedMaximumBytesPerSecond;

        public RateMetricTracker(string name) {
            _name = string.IsNullOrWhiteSpace(name) ? "Rate" : name;
            Reset();
        }

        public void Reset() {
            _samples.Clear();
            _hasRange = false;
            _rangeMinimumBytesPerSecond = 0;
            _rangeMaximumBytesPerSecond = 1024;
            _recordedMinimumBytesPerSecond = double.NaN;
            _recordedMaximumBytesPerSecond = 0;
        }

        public RateMetricSnapshot Update(double liveBytesPerSecond, long totalBytes) {
            liveBytesPerSecond = SanitizeRateValue(liveBytesPerSecond);
            totalBytes = Math.Max(0, totalBytes);

            _samples.Enqueue(liveBytesPerSecond);
            while (_samples.Count > MaxSamples)
                _samples.Dequeue();

            if (liveBytesPerSecond > 0) {
                _recordedMinimumBytesPerSecond = double.IsNaN(_recordedMinimumBytesPerSecond)
                    ? liveBytesPerSecond
                    : Math.Min(_recordedMinimumBytesPerSecond, liveBytesPerSecond);
            }
            _recordedMaximumBytesPerSecond = Math.Max(_recordedMaximumBytesPerSecond, liveBytesPerSecond);

            double[] activeSamples = _samples.Where(sample => sample > 0).OrderBy(sample => sample).ToArray();
            double[] sortedSamples = activeSamples.Length > 0
                ? activeSamples
                : _samples.OrderBy(sample => sample).ToArray();

            double onePercentLow = Percentile(sortedSamples, 0.01);
            double onePercentHigh = Percentile(sortedSamples, 0.99);
            double targetMinimum = sortedSamples.Length == 0 ? 0 : Math.Min(liveBytesPerSecond, onePercentLow);
            double targetMaximum = sortedSamples.Length == 0 ? Math.Max(1, liveBytesPerSecond) : Math.Max(liveBytesPerSecond, onePercentHigh);
            if (liveBytesPerSecond <= 0)
                targetMinimum = 0;

            targetMinimum = SanitizeRateValue(targetMinimum);
            targetMaximum = SanitizeRateValue(targetMaximum);
            double minimumGap = Math.Max(1, targetMaximum * 0.08);
            if (targetMaximum - targetMinimum < minimumGap)
                targetMaximum = targetMinimum + minimumGap;

            if (!_hasRange) {
                _rangeMinimumBytesPerSecond = targetMinimum;
                _rangeMaximumBytesPerSecond = targetMaximum;
                _hasRange = true;
            } else {
                double minimumAlpha = targetMinimum < _rangeMinimumBytesPerSecond ? 0.45 : 0.10;
                double maximumAlpha = targetMaximum > _rangeMaximumBytesPerSecond ? 0.45 : 0.10;
                _rangeMinimumBytesPerSecond = Interpolate(_rangeMinimumBytesPerSecond, targetMinimum, minimumAlpha);
                _rangeMaximumBytesPerSecond = Interpolate(_rangeMaximumBytesPerSecond, targetMaximum, maximumAlpha);
            }

            _rangeMinimumBytesPerSecond = SanitizeRateValue(_rangeMinimumBytesPerSecond);
            _rangeMaximumBytesPerSecond = SanitizeRateValue(_rangeMaximumBytesPerSecond);
            if (_rangeMaximumBytesPerSecond <= _rangeMinimumBytesPerSecond)
                _rangeMaximumBytesPerSecond = _rangeMinimumBytesPerSecond + 1;

            double valueBytesPerSecond = Math.Min(_rangeMaximumBytesPerSecond, Math.Max(_rangeMinimumBytesPerSecond, liveBytesPerSecond));
            if (liveBytesPerSecond > 0 && valueBytesPerSecond <= _rangeMinimumBytesPerSecond) {
                double visibleFloor = (_rangeMaximumBytesPerSecond - _rangeMinimumBytesPerSecond) * 0.05;
                valueBytesPerSecond = Math.Min(_rangeMaximumBytesPerSecond, _rangeMinimumBytesPerSecond + Math.Max(0.01, visibleFloor));
            }

            return new RateMetricSnapshot(
                _name,
                liveBytesPerSecond,
                _rangeMinimumBytesPerSecond,
                _rangeMaximumBytesPerSecond,
                valueBytesPerSecond,
                double.IsNaN(_recordedMinimumBytesPerSecond) ? 0 : _recordedMinimumBytesPerSecond,
                _recordedMaximumBytesPerSecond,
                onePercentLow,
                onePercentHigh,
                _samples.Count,
                totalBytes);
        }

        private static double Interpolate(double current, double target, double alpha) {
            alpha = Math.Min(1, Math.Max(0, alpha));
            return current + ((target - current) * alpha);
        }

        private static double Percentile(double[] sortedSamples, double percentile) {
            if (sortedSamples.Length == 0)
                return 0;

            double clampedPercentile = Math.Min(1, Math.Max(0, percentile));
            double position = clampedPercentile * (sortedSamples.Length - 1);
            int lower = Math.Max(0, Math.Min(sortedSamples.Length - 1, (int)Math.Floor(position)));
            int upper = Math.Max(0, Math.Min(sortedSamples.Length - 1, (int)Math.Ceiling(position)));
            if (lower == upper)
                return sortedSamples[lower];

            double weight = position - lower;
            return sortedSamples[lower] + ((sortedSamples[upper] - sortedSamples[lower]) * weight);
        }
    }

    private readonly struct RateMetricSnapshot {
        public RateMetricSnapshot(string name, double liveBytesPerSecond, double minimumBytesPerSecond, double maximumBytesPerSecond, double valueBytesPerSecond, double recordedMinimumBytesPerSecond, double recordedMaximumBytesPerSecond, double onePercentLowBytesPerSecond, double onePercentHighBytesPerSecond, int sampleCount, long totalBytes) {
            Name = string.IsNullOrWhiteSpace(name) ? "Rate" : name;
            LiveBytesPerSecond = SanitizeRateValue(liveBytesPerSecond);
            MinimumBytesPerSecond = SanitizeRateValue(minimumBytesPerSecond);
            MaximumBytesPerSecond = Math.Max(MinimumBytesPerSecond + 1, SanitizeRateValue(maximumBytesPerSecond));
            ValueBytesPerSecond = Math.Min(MaximumBytesPerSecond, Math.Max(MinimumBytesPerSecond, SanitizeRateValue(valueBytesPerSecond)));
            RecordedMinimumBytesPerSecond = SanitizeRateValue(recordedMinimumBytesPerSecond);
            RecordedMaximumBytesPerSecond = SanitizeRateValue(recordedMaximumBytesPerSecond);
            OnePercentLowBytesPerSecond = SanitizeRateValue(onePercentLowBytesPerSecond);
            OnePercentHighBytesPerSecond = SanitizeRateValue(onePercentHighBytesPerSecond);
            SampleCount = Math.Max(0, sampleCount);
            TotalBytes = Math.Max(0, totalBytes);
        }

        public string Name { get; }
        public double LiveBytesPerSecond { get; }
        public double MinimumBytesPerSecond { get; }
        public double MaximumBytesPerSecond { get; }
        public double ValueBytesPerSecond { get; }
        public double RecordedMinimumBytesPerSecond { get; }
        public double RecordedMaximumBytesPerSecond { get; }
        public double OnePercentLowBytesPerSecond { get; }
        public double OnePercentHighBytesPerSecond { get; }
        public int SampleCount { get; }
        public long TotalBytes { get; }

        public string ToolTip =>
            Name + Environment.NewLine +
            "Live: " + FormatBytesPerSecond(LiveBytesPerSecond) + Environment.NewLine +
            "Adaptive min/max: " + FormatBytesPerSecond(MinimumBytesPerSecond) + " / " + FormatBytesPerSecond(MaximumBytesPerSecond) + Environment.NewLine +
            "Recorded min/max: " + FormatBytesPerSecond(RecordedMinimumBytesPerSecond) + " / " + FormatBytesPerSecond(RecordedMaximumBytesPerSecond) + Environment.NewLine +
            "1% low/high: " + FormatBytesPerSecond(OnePercentLowBytesPerSecond) + " / " + FormatBytesPerSecond(OnePercentHighBytesPerSecond) + Environment.NewLine +
            "Total: " + FormatBytes(TotalBytes);

        public static RateMetricSnapshot Empty(string name) {
            return new RateMetricSnapshot(name, 0, 0, 1024, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class ServiceMetricItem {
        public ServiceMetricItem(string name, string status, string countLine, string detail, bool isPulsing, BandwidthMetricSnapshot bandwidthIn, BandwidthMetricSnapshot bandwidthOut, bool hasBandwidth) {
            Name = name;
            Status = string.IsNullOrWhiteSpace(status) ? "-" : status;
            CountLine = string.IsNullOrWhiteSpace(countLine) ? "-" : countLine;
            Detail = string.IsNullOrWhiteSpace(detail) ? "-" : detail;
            IsPulsing = isPulsing;
            BandwidthVisibility = hasBandwidth ? Visibility.Visible : Visibility.Collapsed;
            BandwidthInMinimum = bandwidthIn.MinimumKbps;
            BandwidthInMaximum = bandwidthIn.MaximumKbps;
            BandwidthInValue = bandwidthIn.ValueKbps;
            BandwidthOutMinimum = bandwidthOut.MinimumKbps;
            BandwidthOutMaximum = bandwidthOut.MaximumKbps;
            BandwidthOutValue = bandwidthOut.ValueKbps;
            BandwidthInText = hasBandwidth ? bandwidthIn.ToolTip : "Inbound: -";
            BandwidthOutText = hasBandwidth ? bandwidthOut.ToolTip : "Outbound: -";
        }

        public string Name { get; }
        public string Status { get; }
        public string CountLine { get; }
        public string Detail { get; }
        public bool IsPulsing { get; }
        public Visibility BandwidthVisibility { get; }
        public double BandwidthInMinimum { get; }
        public double BandwidthInMaximum { get; }
        public double BandwidthInValue { get; }
        public double BandwidthOutMinimum { get; }
        public double BandwidthOutMaximum { get; }
        public double BandwidthOutValue { get; }
        public string BandwidthInText { get; }
        public string BandwidthOutText { get; }
        public string Signature => Name + "\u001F" + Status + "\u001F" + CountLine + "\u001F" + Detail + "\u001F" + IsPulsing + "\u001F" + BandwidthVisibility + "\u001F" + BandwidthInMinimum + "\u001F" + BandwidthInMaximum + "\u001F" + BandwidthInValue + "\u001F" + BandwidthOutMinimum + "\u001F" + BandwidthOutMaximum + "\u001F" + BandwidthOutValue + "\u001F" + BandwidthInText + "\u001F" + BandwidthOutText;
    }

    private sealed class PipelineNodeItem {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public Visibility ArrowVisibility { get; init; } = Visibility.Visible;
        public Brush BackgroundBrush { get; init; } = Brushes.Transparent;
        public Brush BorderBrush { get; init; } = Brushes.SlateGray;
        public string Signature => Name + "\u001F" + Detail + "\u001F" + ArrowVisibility;
    }

    private sealed class LmVsProxyGuiSettings {
        public bool StartProxyOnOpen { get; set; } = true;
        public bool RemoteLmStudioEnabled { get; set; } = false;
        public string RemoteServer { get; set; } = "127.0.0.1:11435";
        public bool PortForwardingEnabled { get; set; } = true;
        public bool WindowsStartupEnabled { get; set; } = false;
        public bool HideToTrayOnStartup { get; set; } = true;
        public bool AllowOpenRegistration { get; set; } = false;
        public string StorageType { get; set; } = "Nvme";
        public double StorageCostFactor { get; set; } = 0;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double WindowWidth { get; set; } = 1280;
        public double WindowHeight { get; set; } = 820;
        public bool WindowMaximized { get; set; } = false;
    }
}
