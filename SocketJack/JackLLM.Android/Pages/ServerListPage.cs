using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class ServerListPage : ContentPage
{
    private readonly ServerDirectoryService _directory;
    private readonly ServerStore _store;
    private readonly SecureCredentialStore _credentials;
    private readonly MobileGenerationCoordinator _generation;
    private readonly RecentSessionStore _recentSessions;
    private readonly IMobileConnectivityService _connectivity;
    private readonly CollectionView _servers;
    private readonly Label _status;
    private readonly Label _tailscaleStatus;
    private readonly Button _openTailscale;
    private readonly RefreshView _refreshView;
    private List<ServerInfo> _currentServers = new();
    private bool _loaded;

    public ServerListPage(
        ServerDirectoryService directory,
        ServerStore store,
        SecureCredentialStore credentials,
        MobileGenerationCoordinator generation,
        RecentSessionStore recentSessions,
        IMobileConnectivityService connectivity)
    {
        _directory = directory;
        _store = store;
        _credentials = credentials;
        _generation = generation;
        _recentSessions = recentSessions;
        _connectivity = connectivity;
        Title = "JackLLM Mobile";
        BackgroundColor = Color.FromArgb("#0B1020");

        _status = new Label { Text = "Choose a Workstation", TextColor = Color.FromArgb("#94A3B8"), FontSize = 13 };
        _tailscaleStatus = new Label { Text = "Checking Tailscale...", TextColor = Color.FromArgb("#94A3B8"), FontSize = 12 };
        _openTailscale = new Button
        {
            Text = "Open Tailscale",
            BackgroundColor = Color.FromArgb("#B91C1C"),
            TextColor = Colors.White,
            CornerRadius = 12,
            IsVisible = false
        };
        _openTailscale.Clicked += async (_, _) =>
        {
            if (!await _connectivity.OpenTailscaleAsync())
                await Launcher.Default.OpenAsync("https://tailscale.com/download");
        };

        _servers = new CollectionView { SelectionMode = SelectionMode.Single, ItemTemplate = new DataTemplate(ServerCard) };
        _servers.SelectionChanged += async (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is not ServerInfo server) return;
            _servers.SelectedItem = null;
            await OpenServerAsync(server);
        };

        var add = new Button { Text = "Add or pair", BackgroundColor = Color.FromArgb("#2563EB"), TextColor = Colors.White, CornerRadius = 12 };
        add.Clicked += async (_, _) => await AddServerAsync();
        var refresh = new Button { Text = "Refresh", BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, CornerRadius = 12 };
        refresh.Clicked += async (_, _) => await ReloadAsync();
        ToolbarItems.Add(new ToolbarItem("🖥", null, async () => await OpenPcAccessAsync()) { AutomationId = "PcAccess" });

        var pageContent = new Grid
        {
            Padding = new Thickness(16, 12),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)
            },
            RowSpacing = 12,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = "JackLLM Mobile", FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                        _status,
                        _tailscaleStatus
                    }
                }.Row(0),
                new HorizontalStackLayout { Spacing = 10, Children = { add, refresh, _openTailscale } }.Row(1),
                new Label { Text = "Pull down to refresh connection status.", FontSize = 10, TextColor = Color.FromArgb("#64748B") }.Row(2),
                _servers.Row(3)
            }
        };
        _refreshView = new RefreshView { Content = pageContent, RefreshColor = Color.FromArgb("#60A5FA") };
        _refreshView.Refreshing += async (_, _) =>
        {
            try { await ReloadAsync(); }
            finally { _refreshView.IsRefreshing = false; }
        };
        Content = _refreshView;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) _loaded = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _status.Text = "Finding saved and public Workstations...";
        try
        {
            MobileConnectivityStatus mobileStatus = await _connectivity.GetStatusAsync();
            _tailscaleStatus.Text = mobileStatus.TailscaleDisplay;
            _tailscaleStatus.TextColor = mobileStatus.TailscaleEnabled ? Color.FromArgb("#6EE7B7") : Color.FromArgb("#FCA5A5");
            _openTailscale.IsVisible = !mobileStatus.TailscaleEnabled;

            List<ServerInfo> combined = _store.Load().ToList();
            foreach (ServerInfo server in await _directory.LoadAsync())
            {
                if (!combined.Any(item => item.LaunchKey.Equals(server.LaunchKey, StringComparison.OrdinalIgnoreCase)))
                    combined.Add(server);
            }

            await Task.WhenAll(combined.Select(server => ProbeServerAsync(server, mobileStatus)));
            _currentServers = combined.OrderByDescending(server => server.Online).ThenBy(server => server.DisplayName).ToList();
            _servers.ItemsSource = _currentServers.ToList();
            int online = combined.Count(server => server.Online);
            _status.Text = combined.Count == 0
                ? "No Workstations found. Add one by address or pairing code."
                : $"{online} online · {combined.Count} saved/available";
        }
        catch (Exception ex)
        {
            _status.Text = "Server refresh failed: " + ex.Message;
        }
    }

    private async Task AddServerAsync()
    {
        string endpoint = await DisplayPromptAsync("Add Workstation", "HTTPS address", initialValue: "https://", keyboard: Keyboard.Url) ?? "";
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        try { endpoint = JackLlmClient.NormalizeBaseUrl(endpoint); }
        catch (Exception ex) { await DisplayAlertAsync("Invalid address", ex.Message, "OK"); return; }

        string code = await DisplayPromptAsync("Pairing", "Enter the six-digit pairing code, or leave blank if this server uses SocketJack sign-in.", keyboard: Keyboard.Numeric) ?? "";
        var server = new ServerInfo { Name = new Uri(endpoint).Host, Endpoint = endpoint, Online = true, IsSaved = true };
        if (!string.IsNullOrWhiteSpace(code))
        {
            try
            {
                using var pairingClient = new JackLlmClient(_credentials);
                string token = await pairingClient.CompletePairingAsync(endpoint, code.Trim(), DeviceInfo.Current.Name);
                await _credentials.SetServerTokenAsync(server.LaunchKey, token);
            }
            catch (Exception ex) { await DisplayAlertAsync("Pairing failed", ex.Message, "OK"); return; }
        }
        _store.Save(server);
        await ReloadAsync();
        await OpenServerAsync(server);
    }

    private async Task OpenServerAsync(ServerInfo server)
    {
        ServerInfo launch = BuildLaunch(server);
        string recentSession = _recentSessions.FindRecent(server.LaunchKey, TimeSpan.FromHours(1))?.SessionId ?? "";
        await Navigation.PushAsync(new ChatHostPage(launch, new JackLlmClient(_credentials), _store, _generation, _recentSessions, recentSession));
    }

    private async Task OpenPcAccessAsync()
    {
        ServerInfo? server = _currentServers.FirstOrDefault(item => item.IsSaved) ?? _store.Load().FirstOrDefault();
        if (server is null)
        {
            await DisplayAlertAsync("PC Access", "Add or pair a Workstation first.", "OK");
            return;
        }
        await Navigation.PushAsync(new PcAccessPage(BuildLaunch(server), new JackLlmClient(_credentials)));
    }

    private async Task ProbeServerAsync(ServerInfo server, MobileConnectivityStatus mobileStatus)
    {
        try
        {
            using var client = new JackLlmClient(_credentials);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(BuildLaunch(server), timeout.Token);
            server.Online = true;
            server.ConnectionLine = "● Workstation online";
            server.ConnectionColor = Color.FromArgb("#6EE7B7");
        }
        catch
        {
            server.Online = false;
            server.ConnectionLine = "● Workstation offline";
            server.ConnectionColor = Color.FromArgb("#FCA5A5");
        }
        server.TailscaleLine = mobileStatus.TailscaleDisplay;
    }

    private ServerInfo BuildLaunch(ServerInfo server) => new()
    {
        ServerId = server.ServerId,
        Name = server.Name,
        OwnerUserName = server.OwnerUserName,
        Endpoint = _directory.BuildLaunchUrl(server),
        OpenAiBaseUrl = server.OpenAiBaseUrl,
        SelectedModel = server.SelectedModel,
        AvailableModels = server.AvailableModels,
        CertificateFingerprint = server.CertificateFingerprint,
        IsSaved = server.IsSaved,
        CredentialKey = server.LaunchKey
    };

    private static View ServerCard()
    {
        var title = new Label { FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Colors.White };
        title.SetBinding(Label.TextProperty, nameof(ServerInfo.DisplayName));
        var connection = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold };
        connection.SetBinding(Label.TextProperty, nameof(ServerInfo.ConnectionLine));
        connection.SetBinding(Label.TextColorProperty, nameof(ServerInfo.ConnectionColor));
        var tailscale = new Label { FontSize = 11, TextColor = Color.FromArgb("#94A3B8") };
        tailscale.SetBinding(Label.TextProperty, nameof(ServerInfo.TailscaleLine));
        var model = new Label { FontSize = 12, TextColor = Color.FromArgb("#94A3B8") };
        model.SetBinding(Label.TextProperty, nameof(ServerInfo.ModelLine));
        var hardware = new Label { FontSize = 12, TextColor = Color.FromArgb("#64748B") };
        hardware.SetBinding(Label.TextProperty, nameof(ServerInfo.HardwareLine));
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = 14,
            BackgroundColor = Color.FromArgb("#151C2F"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = new VerticalStackLayout { Spacing = 5, Children = { title, connection, tailscale, model, hardware } }
        };
    }
}

internal static class GridExtensions
{
    public static T Row<T>(this T view, int row) where T : BindableObject { Grid.SetRow(view, row); return view; }
}
