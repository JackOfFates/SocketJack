using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SocketJack.Update;

public partial class MainWindow : Window {
    private readonly UpdateServerHost _host;
    private readonly UpdateViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private bool _shutdown;

    public MainWindow() {
        InitializeComponent();
        _host = new UpdateServerHost(UpdateServerOptions.Load());
        _viewModel = new UpdateViewModel(_host);
        DataContext = _viewModel;
        _host.StatusChanged += Host_StatusChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) {
        _host.Start();
        _viewModel.Refresh();
        UpdateAutoStartHeaderState();
        _refreshTimer.Start();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) {
        ShutdownServer();
    }

    public void ShutdownServer() {
        if (_shutdown)
            return;
        _shutdown = true;
        _refreshTimer.Stop();
        _host.StatusChanged -= Host_StatusChanged;
        _host.Dispose();
    }

    private void Host_StatusChanged(object? sender, EventArgs e) {
        Dispatcher.BeginInvoke(new Action(RefreshView));
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) {
        _host.Start();
        RefreshView();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) {
        _host.Stop();
        RefreshView();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) {
        RefreshView();
    }

    private void ChannelStartButton_Click(object sender, RoutedEventArgs e) {
        RunChannelAction(sender, channelId => _host.StartChannelProcess(channelId));
    }

    private void ChannelStopButton_Click(object sender, RoutedEventArgs e) {
        RunChannelAction(sender, channelId => _host.StopChannelProcess(channelId));
    }

    private void ChannelRestartButton_Click(object sender, RoutedEventArgs e) {
        RunChannelAction(sender, channelId => _host.RestartChannelProcess(channelId));
    }

    private void ChannelStartupButton_Click(object sender, RoutedEventArgs e) {
        RunChannelAction(sender, channelId => _host.AddChannelToWindowsStartup(channelId));
    }

    private void ChannelRemoveButton_Click(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { DataContext: UpdateChannelViewModel channel })
            return;
        if (!channel.CanRemove)
            return;

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            "Remove " + channel.DisplayName + " from the updater list?\n\nThis updates dynamicUpdates.json only. It does not delete update files or stop any running process.",
            "SocketJack Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        RunChannelAction(sender, channelId => _host.RemoveDynamicChannel(channelId));
    }

    private void RunChannelAction(object sender, Func<string, ProcessActionResult> action) {
        if (sender is not FrameworkElement { DataContext: UpdateChannelViewModel channel })
            return;

        try {
            ProcessActionResult result = action(channel.Id);
            if (!result.Ok)
                MessageBox.Show(this, result.Message, "SocketJack Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        } catch (Exception ex) {
            MessageBox.Show(this, ex.Message, "SocketJack Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally {
            RefreshView();
        }
    }

    private void HeaderAutoStartCheckBox_Click(object sender, RoutedEventArgs e) {
        if (sender is not CheckBox checkBox || checkBox.IsChecked == null)
            return;

        try {
            _host.SetAllManagedChannelAutoStart(checkBox.IsChecked == true);
        } catch (Exception ex) {
            MessageBox.Show(this, ex.Message, "SocketJack Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally {
            RefreshView();
        }
    }

    private void ChannelAutoStartCheckBox_Click(object sender, RoutedEventArgs e) {
        if (sender is not CheckBox { DataContext: UpdateChannelViewModel channel } checkBox)
            return;

        try {
            _host.SetChannelAutoStart(channel.Id, checkBox.IsChecked == true);
        } catch (Exception ex) {
            MessageBox.Show(this, ex.Message, "SocketJack Update", MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally {
            RefreshView();
        }
    }

    private void SetLoginButton_Click(object sender, RoutedEventArgs e) {
        string username = AdminUserTextBox.Text.Trim();
        string password = AdminPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) {
            MessageBox.Show(this, "Enter a username and password first.", "SocketJack Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _host.ConfigureAdmin(username, password);
        AdminPasswordBox.Clear();
        RefreshView();
    }

    private void RefreshView() {
        _viewModel.Refresh();
        UpdateAutoStartHeaderState();
    }

    private void UpdateAutoStartHeaderState() {
        IReadOnlyList<UpdateChannelViewModel> managedChannels = _viewModel.Channels.Where(channel => channel.IsProcessManaged).ToList();
        if (managedChannels.Count == 0) {
            AllAutoStartCheckBox.IsEnabled = false;
            AllAutoStartCheckBox.IsChecked = false;
            return;
        }

        AllAutoStartCheckBox.IsEnabled = true;
        bool allOn = managedChannels.All(channel => channel.AutoStartAfterUpdate);
        bool anyOn = managedChannels.Any(channel => channel.AutoStartAfterUpdate);
        AllAutoStartCheckBox.IsChecked = allOn ? true : anyOn ? null : false;
    }
}

public sealed class UpdateViewModel : INotifyPropertyChanged {
    private readonly UpdateServerHost _host;
    public ObservableCollection<UpdateChannelViewModel> Channels { get; } = new();

    public UpdateViewModel(UpdateServerHost host) {
        _host = host;
        Refresh();
    }

    public string Status => _host.Status;
    public string EndpointText => _host.EndpointText;
    public string AuthStatus => _host.HasAdminLogin
        ? "Bearer-token publishing is locked to the configured SocketJack Update login."
        : "No admin login is configured. Set one here before accepting remote publishes.";
    public string FooterText => "Publisher API: /api/socketjack/oauth/token, /api/update/publish/begin, /api/update/publish/archive, /api/update/publish/complete. GUI downloads: /Update/meta and /Update/{file}. Channel downloads: /update/{channel}/meta.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() {
        Channels.Clear();
        foreach (UpdateChannel channel in _host.GetChannelSnapshots())
            Channels.Add(new UpdateChannelViewModel(channel));
        OnChanged(nameof(Status));
        OnChanged(nameof(EndpointText));
        OnChanged(nameof(AuthStatus));
        OnChanged(nameof(FooterText));
    }

    private void OnChanged(string name) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class UpdateChannelViewModel {
    public UpdateChannelViewModel(UpdateChannel channel) {
        Id = channel.Id;
        DisplayName = channel.DisplayName;
        UpdateDirectory = channel.UpdateDirectory;
        FileCount = channel.FileCount;
        SizeText = FormatBytes(channel.ByteCount);
        IsProcessManaged = channel.IsProcessManaged;
        IsProcessRunning = channel.IsProcessRunning;
        ManagedProcessIds = channel.ManagedProcessIds ?? new List<int>();
        IsStartupEnabled = channel.IsStartupEnabled;
        AutoStartAfterUpdate = channel.AutoStartAfterUpdate;
        LastUpdateText = FormatDate(channel.LastUpdateUtc);
        IsDynamic = channel.IsDynamic;
        CanRemove = IsDynamic && !UpdateServerOptions.IsDefaultChannelId(channel.Id);
        CanStartProcess = IsProcessManaged && !IsProcessRunning;
        CanStopProcess = IsProcessManaged && IsProcessRunning;
        CanRestartProcess = IsProcessManaged;
        ProcessStatus = !IsProcessManaged
            ? "Unmanaged"
            : IsProcessRunning
                ? "Running" + (ManagedProcessIds.Count == 0 ? "" : " #" + string.Join(",", ManagedProcessIds))
                : "Closed";
        StartupButtonText = IsStartupEnabled ? "Startup On" : "Add Startup";
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string UpdateDirectory { get; }
    public int FileCount { get; }
    public string SizeText { get; }
    public bool IsProcessManaged { get; }
    public bool IsProcessRunning { get; }
    public List<int> ManagedProcessIds { get; }
    public bool IsStartupEnabled { get; }
    public bool AutoStartAfterUpdate { get; }
    public bool IsDynamic { get; }
    public bool CanRemove { get; }
    public bool CanStartProcess { get; }
    public bool CanStopProcess { get; }
    public bool CanRestartProcess { get; }
    public string ProcessStatus { get; }
    public string StartupButtonText { get; }
    public string LastUpdateText { get; }

    private static string FormatBytes(long bytes) {
        if (bytes < 1024) return bytes.ToString("N0") + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("N1") + " KB";
        if (bytes < 1024 * 1024 * 1024L) return (bytes / (1024.0 * 1024)).ToString("N1") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";
    }

    private static string FormatDate(string value) {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            return "Never";
        return parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }
}
