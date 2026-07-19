using System.Collections.ObjectModel;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class SessionsPage : ContentPage
{
    private readonly ServerInfo _server;
    private readonly JackLlmClient _client;
    private readonly Func<string, Task> _openSession;
    private readonly ObservableCollection<ChatSessionInfo> _sessions = new();
    private readonly CollectionView _list;
    private readonly ActivityIndicator _loading;
    private readonly Label _syncState;
    private readonly Label _branchSummary;
    private readonly RefreshView _refreshView;
    private bool _loaded;

    public SessionsPage(ServerInfo server, JackLlmClient client, Func<string, Task> openSession)
    {
        _server = server;
        _client = client;
        _openSession = openSession;
        Title = "Workspace";
        BackgroundColor = Color.FromArgb("#080D1A");

        _syncState = new Label { Text = "●  Syncing", FontSize = 11, TextColor = Color.FromArgb("#93C5FD"), VerticalTextAlignment = TextAlignment.Center };
        _branchSummary = new Label { Text = "└─ Sessions", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#CBD5E1"), Margin = new Thickness(18, 8, 0, 3) };
        _loading = new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#60A5FA"), HorizontalOptions = LayoutOptions.Center, HeightRequest = 24 };
        _list = new CollectionView
        {
            ItemsSource = _sessions,
            ItemTemplate = new DataTemplate(CreateSessionNode),
            SelectionMode = SelectionMode.None,
            EmptyView = new VerticalStackLayout
            {
                Spacing = 8,
                Margin = new Thickness(30),
                Children =
                {
                    new Label { Text = "◇", FontSize = 30, TextColor = Color.FromArgb("#475569"), HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = "No synchronized sessions yet", FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#CBD5E1"), HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = "Return to chat and tap ＋ to begin.", FontSize = 12, TextColor = Color.FromArgb("#94A3B8"), HorizontalTextAlignment = TextAlignment.Center }
                }
            }
        };

        ToolbarItems.Add(new ToolbarItem("↻", null, async () => await LoadAsync()) { AutomationId = "RefreshWorkspace" });

        var eyebrow = new Label { Text = "SOLUTION EXPLORER", CharacterSpacing = 1.4, FontSize = 10, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#64748B") };
        var rootTitle = new Label { Text = "▾  ▣  " + server.DisplayName, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
        var endpoint = new Label { Text = server.Endpoint, FontSize = 10, TextColor = Color.FromArgb("#94A3B8"), LineBreakMode = LineBreakMode.TailTruncation };
        var rootHeader = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        rootHeader.Add(rootTitle, 0); rootHeader.Add(_syncState, 1);
        var root = new Border
        {
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 8, 0, 2),
            BackgroundColor = Color.FromArgb("#121A2C"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = new VerticalStackLayout { Spacing = 4, Children = { rootHeader, endpoint } }
        };

        var pageContent = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Padding = new Thickness(12, 10, 12, 0),
            Children = { eyebrow, root.Row(1), _branchSummary.Row(2), _loading.Row(3), _list.Row(4) }
        };
        _refreshView = new RefreshView { Content = pageContent, RefreshColor = Color.FromArgb("#60A5FA") };
        _refreshView.Refreshing += async (_, _) =>
        {
            try { await LoadAsync(); }
            finally { _refreshView.IsRefreshing = false; }
        };
        Content = _refreshView;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        _syncState.Text = "●  Syncing";
        _syncState.TextColor = Color.FromArgb("#93C5FD");
        try
        {
            IReadOnlyList<ChatSessionInfo> sessions = await _client.GetSessionsAsync();
            _sessions.Clear();
            foreach (ChatSessionInfo session in sessions.OrderByDescending(item => item.UpdatedAt)) _sessions.Add(session);
            int messages = sessions.Sum(item => item.MessageCount);
            _branchSummary.Text = $"└─ ▾  Sessions  {sessions.Count:N0}   ·   {messages:N0} messages";
            _syncState.Text = "●  Synced now";
            _syncState.TextColor = Color.FromArgb("#6EE7B7");
            _loaded = true;
        }
        catch (Exception ex)
        {
            _syncState.Text = "●  Offline";
            _syncState.TextColor = Color.FromArgb("#FCA5A5");
            _branchSummary.Text = "└─ Sync failed · " + ex.Message;
        }
        finally
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
        }
    }

    private View CreateSessionNode()
    {
        var branch = new Label { Text = "├─", FontFamily = "monospace", FontSize = 14, TextColor = Color.FromArgb("#475569"), VerticalTextAlignment = TextAlignment.Start, Margin = new Thickness(2, 12, 0, 0) };
        var title = BoundLabel(nameof(ChatSessionInfo.Title), 15, Colors.White, FontAttributes.Bold);
        var date = BoundLabel(nameof(ChatSessionInfo.UpdatedDisplay), 10, Color.FromArgb("#94A3B8"));
        var activity = BoundLabel(nameof(ChatSessionInfo.ActivityDisplay), 11, Color.FromArgb("#CBD5E1"));
        var tokens = BoundLabel(nameof(ChatSessionInfo.TokenDisplay), 11, Color.FromArgb("#93C5FD"));
        var compute = BoundLabel(nameof(ChatSessionInfo.ComputeDisplay), 10, Color.FromArgb("#6EE7B7"));
        var model = BoundLabel(nameof(ChatSessionInfo.Model), 10, Color.FromArgb("#C4B5FD"));
        var open = new Button { Text = "↗", FontSize = 18, TextColor = Color.FromArgb("#60A5FA"), BackgroundColor = Colors.Transparent, WidthRequest = 46, HeightRequest = 44, Padding = 0, CornerRadius = 12 };

        var titleRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        titleRow.Add(title, 0); titleRow.Add(open, 1); Grid.SetRowSpan(open, 2); titleRow.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); titleRow.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); titleRow.Add(date, 0, 1);
        var details = new VerticalStackLayout { Spacing = 3, Margin = new Thickness(12, 7, 0, 0), Children = { activity, tokens, compute, model } };
        var node = new Border
        {
            Margin = new Thickness(0, 3),
            Padding = new Thickness(12, 8),
            BackgroundColor = Color.FromArgb("#111827"),
            Stroke = Color.FromArgb("#202C43"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 13 },
            Content = new VerticalStackLayout { Spacing = 1, Children = { titleRow, details } }
        };
        open.Clicked += async (_, _) => { if (node.BindingContext is ChatSessionInfo session) await OpenAsync(session); };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => { if (node.BindingContext is ChatSessionInfo session) await OpenAsync(session); };
        node.GestureRecognizers.Add(tap);

        var row = new Grid { ColumnDefinitions = { new ColumnDefinition(34), new ColumnDefinition(GridLength.Star) } };
        row.Add(branch, 0); row.Add(node, 1);
        return row;
    }

    private async Task OpenAsync(ChatSessionInfo session)
    {
        _syncState.Text = "●  Opening";
        _syncState.TextColor = Color.FromArgb("#93C5FD");
        await _openSession(session.Id);
        await Navigation.PopAsync();
    }

    private static Label BoundLabel(string path, double size, Color color, FontAttributes attributes = FontAttributes.None)
    {
        var label = new Label { FontSize = size, TextColor = color, FontAttributes = attributes, LineBreakMode = LineBreakMode.TailTruncation };
        label.SetBinding(Label.TextProperty, path);
        return label;
    }
}
