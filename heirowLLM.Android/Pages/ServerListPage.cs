using heirowLLM.Mobile.Models;
using heirowLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace heirowLLM.Mobile.Pages;

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
        Title = "heirowLLM Mobile";
        BackgroundColor = Color.FromArgb("#0B1020");

        _status = new Label { Text = "Choose a Workstation", TextColor = Color.FromArgb("#94A3B8"), FontSize = 13 };
        _tailscaleStatus = new Label { Text = "Checking Tailscale...", TextColor = Color.FromArgb("#94A3B8"), FontSize = 12 };
        _openTailscale = new Button
        {
            Text = "\u21c4",
            BackgroundColor = Color.FromArgb("#B91C1C"),
            TextColor = Colors.White,
            CornerRadius = 12,
            FontSize = 20,
            MinimumWidthRequest = 0,
            Padding = new Thickness(8, 4),
            IsVisible = false
        };
        AutomationProperties.SetName(_openTailscale, "Open Tailscale");
        AutomationProperties.SetHelpText(_openTailscale, "Open Tailscale or install it");
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

        var signIn = new Button
        {
            Text = "\uD83D\uDD10",
            BackgroundColor = Color.FromArgb("#2563EB"),
            TextColor = Colors.White,
            CornerRadius = 12,
            FontSize = 18,
            MinimumWidthRequest = 0,
            Padding = new Thickness(8, 4),
            AutomationId = "OpenWorkstationLogin"
        };
        AutomationProperties.SetName(signIn, "Login or register");
        AutomationProperties.SetHelpText(signIn, "Login or register with a Workstation");
        signIn.Clicked += async (_, _) => await OpenAuthenticationAsync();
        var add = new Button
        {
            Text = "\u221e",
            BackgroundColor = Color.FromArgb("#334155"),
            TextColor = Colors.White,
            CornerRadius = 12,
            FontSize = 20,
            MinimumWidthRequest = 0,
            Padding = new Thickness(8, 4),
            AutomationId = "OpenWorkstationPairing"
        };
        AutomationProperties.SetName(add, "Pair Workstation");
        AutomationProperties.SetHelpText(add, "Pair a Workstation using a code");
        add.Clicked += async (_, _) => await AddServerAsync();
        var refresh = new Button { Text = "\u21bb", BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White, CornerRadius = 12, FontSize = 20, MinimumWidthRequest = 0, Padding = new Thickness(8, 4) };
        AutomationProperties.SetName(refresh, "Refresh Workstations");
        AutomationProperties.SetHelpText(refresh, "Refresh Workstation and Tailscale status");
        refresh.Clicked += async (_, _) => await ReloadAsync();
        var connectionActions = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 8
        };
        connectionActions.Add(signIn, 0);
        connectionActions.Add(add, 1);
        connectionActions.Add(refresh, 2);
        connectionActions.Add(_openTailscale, 3);
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
                        new Label { Text = "heirowLLM Mobile", FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                        _status,
                        _tailscaleStatus
                    }
                }.Row(0),
                connectionActions.Row(1),
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
        _status.Text = "Finding saved Workstations...";
        try
        {
            MobileConnectivityStatus mobileStatus = await _connectivity.GetStatusAsync();
            _tailscaleStatus.Text = mobileStatus.TailscaleDisplay;
            _tailscaleStatus.TextColor = mobileStatus.TailscaleEnabled ? Color.FromArgb("#6EE7B7") : Color.FromArgb("#FCA5A5");
            _openTailscale.IsVisible = !mobileStatus.TailscaleEnabled;

            List<ServerInfo> combined = _store.Load().ToList();

            await Task.WhenAll(combined.Select(server => ProbeServerAsync(server, mobileStatus)));
            _currentServers = combined.OrderByDescending(server => server.Online).ThenBy(server => server.DisplayName).ToList();
            _servers.ItemsSource = _currentServers.ToList();
            int online = combined.Count(server => server.Online);
            _status.Text = combined.Count == 0
                ? "No Workstations found. Login, register, or pair a Workstation."
                : $"{online} online · {combined.Count} saved";
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
        try { endpoint = HeirowLlmClient.NormalizeBaseUrl(endpoint); }
        catch (Exception ex) { await DisplayAlertAsync("Invalid address", ex.Message, "OK"); return; }

        string code = await DisplayPromptAsync("Pair Workstation user", "Enter the six-digit code created for your user on this Workstation.", keyboard: Keyboard.Numeric) ?? "";
        if (string.IsNullOrWhiteSpace(code))
        {
            await DisplayAlertAsync("Pairing required", "A Workstation pairing code is required. Server addresses and directory accounts do not grant access.", "OK");
            return;
        }
        var server = new ServerInfo { Name = new Uri(endpoint).Host, Endpoint = endpoint, Online = true, IsSaved = true };
        try
        {
            using var pairingClient = new HeirowLlmClient(_credentials);
            string token = await pairingClient.CompletePairingAsync(endpoint, code.Trim(), DeviceInfo.Current.Name);
            await _credentials.SetServerTokenAsync(server.LaunchKey, token);
        }
        catch (Exception ex) { await DisplayAlertAsync("Pairing failed", ex.Message, "OK"); return; }
        _store.Save(server);
        await ReloadAsync();
        await OpenServerAsync(server);
    }

    private async Task OpenAuthenticationAsync(string endpoint = "")
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = _store.Load().FirstOrDefault()?.Endpoint ?? "";
        await Navigation.PushAsync(new WorkstationAuthPage(
            _credentials,
            _store,
            async server =>
            {
                await Navigation.PopAsync(false);
                await ReloadAsync();
                await OpenServerAsync(server);
            },
            endpoint));
    }

    private async Task OpenServerAsync(ServerInfo server)
    {
        try
        {
            ServerInfo launch = BuildLaunch(server);
            using var verification = new HeirowLlmClient(_credentials);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await verification.ConnectAsync(launch, timeout.Token);
            string recentSession = _recentSessions.FindRecent(server.LaunchKey, TimeSpan.FromHours(1))?.SessionId ?? "";
            await Navigation.PushAsync(new ChatHostPage(launch, new HeirowLlmClient(_credentials), _store, _credentials, _generation, _recentSessions, recentSession));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await OpenAuthenticationAsync(server.Endpoint);
        }
        catch (UnauthorizedAccessException)
        {
            await OpenAuthenticationAsync(server.Endpoint);
        }
    }

    private async Task ProbeServerAsync(ServerInfo server, MobileConnectivityStatus mobileStatus)
    {
        try
        {
            using var client = new HeirowLlmClient(_credentials);
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
