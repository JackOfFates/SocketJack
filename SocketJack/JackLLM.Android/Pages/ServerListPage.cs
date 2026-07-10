using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class ServerListPage : ContentPage
{
    private readonly ServerDirectoryService _directory;
    private readonly ServerStore _store;
    private readonly SecureCredentialStore _credentials;
    private readonly CollectionView _servers;
    private readonly Label _status;
    private bool _loaded;

    public ServerListPage(ServerDirectoryService directory, ServerStore store, SecureCredentialStore credentials)
    {
        _directory = directory;
        _store = store;
        _credentials = credentials;
        Title = "JackLLM Mobile";
        BackgroundColor = Color.FromArgb("#0B1020");

        _status = new Label { Text = "Choose a Workstation", TextColor = Color.FromArgb("#94A3B8"), FontSize = 13 };
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

        Content = new Grid
        {
            Padding = new Thickness(16, 12),
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            RowSpacing = 12,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = "JackLLM Mobile", FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                        _status
                    }
                }.Row(0),
                new HorizontalStackLayout { Spacing = 10, Children = { add, refresh } }.Row(1),
                _servers.Row(2)
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) { _loaded = true; await ReloadAsync(); }
    }

    private async Task ReloadAsync()
    {
        _status.Text = "Finding saved and public Workstations…";
        try
        {
            var combined = _store.Load().ToList();
            _servers.ItemsSource = combined.ToList();
            if (combined.Count > 0)
                _status.Text = $"{combined.Count} saved Workstation{(combined.Count == 1 ? "" : "s")} available";
            foreach (ServerInfo server in await _directory.LoadAsync())
                if (!combined.Any(item => item.LaunchKey.Equals(server.LaunchKey, StringComparison.OrdinalIgnoreCase))) combined.Add(server);
            _servers.ItemsSource = combined;
            _status.Text = combined.Count == 0 ? "No Workstations found. Add one by address or pairing code." : $"{combined.Count} Workstation{(combined.Count == 1 ? "" : "s")} available";
        }
        catch (Exception ex) { _status.Text = "Server refresh failed: " + ex.Message; }
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
        var launch = new ServerInfo
        {
            ServerId = server.ServerId, Name = server.Name, OwnerUserName = server.OwnerUserName,
            Endpoint = _directory.BuildLaunchUrl(server), OpenAiBaseUrl = server.OpenAiBaseUrl,
            SelectedModel = server.SelectedModel, AvailableModels = server.AvailableModels,
            CertificateFingerprint = server.CertificateFingerprint, IsSaved = server.IsSaved
        };
        await Navigation.PushAsync(new ChatHostPage(launch, new JackLlmClient(_credentials), _store));
    }

    private static View ServerCard()
    {
        var title = new Label { FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Colors.White };
        title.SetBinding(Label.TextProperty, nameof(ServerInfo.DisplayName));
        var model = new Label { FontSize = 12, TextColor = Color.FromArgb("#94A3B8") };
        model.SetBinding(Label.TextProperty, nameof(ServerInfo.ModelLine));
        var hardware = new Label { FontSize = 12, TextColor = Color.FromArgb("#64748B") };
        hardware.SetBinding(Label.TextProperty, nameof(ServerInfo.HardwareLine));
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10), Padding = 14, BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"),
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = new VerticalStackLayout { Spacing = 5, Children = { title, model, hardware } }
        };
    }
}

internal static class GridExtensions
{
    public static T Row<T>(this T view, int row) where T : BindableObject { Grid.SetRow(view, row); return view; }
}
