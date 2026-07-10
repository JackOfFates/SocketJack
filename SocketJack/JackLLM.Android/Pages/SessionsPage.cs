using System.Collections.ObjectModel;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class SessionsPage : ContentPage
{
    private readonly JackLlmClient _client;
    private readonly Func<string, Task> _openSession;
    private readonly ObservableCollection<ChatSessionInfo> _sessions = new();
    private readonly CollectionView _list;
    private readonly ActivityIndicator _loading;
    private readonly Label _summary;
    private bool _loaded;

    public SessionsPage(JackLlmClient client, Func<string, Task> openSession)
    {
        _client = client;
        _openSession = openSession;
        Title = "Sessions";
        BackgroundColor = Color.FromArgb("#0B1020");

        var back = new Button
        {
            Text = "‹  Back to chat",
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#93C5FD"),
            FontSize = 15,
            HorizontalOptions = LayoutOptions.Start,
            Padding = new Thickness(4, 4)
        };
        back.Clicked += async (_, _) => await Navigation.PopAsync();

        var heading = new Label { Text = "Workstation sessions", FontSize = 25, FontAttributes = FontAttributes.Bold, TextColor = Colors.White };
        _summary = new Label { Text = "Loading conversation history…", FontSize = 12, TextColor = Color.FromArgb("#94A3B8") };
        _loading = new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#60A5FA"), HorizontalOptions = LayoutOptions.Center };
        _list = new CollectionView
        {
            ItemsSource = _sessions,
            ItemTemplate = new DataTemplate(CreateSessionCard),
            SelectionMode = SelectionMode.None,
            EmptyView = new Label { Text = "No saved sessions were found.", TextColor = Color.FromArgb("#94A3B8"), HorizontalTextAlignment = TextAlignment.Center, Margin = 24 }
        };

        ToolbarItems.Add(new ToolbarItem("Refresh", null, async () => await LoadAsync()));
        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Padding = new Thickness(14, 8, 14, 0),
            Children =
            {
                back,
                heading.Row(1),
                _summary.Row(2),
                _loading.Row(3),
                _list.Row(4)
            }
        };
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
        try
        {
            IReadOnlyList<ChatSessionInfo> sessions = await _client.GetSessionsAsync();
            _sessions.Clear();
            foreach (ChatSessionInfo session in sessions) _sessions.Add(session);
            _summary.Text = $"{sessions.Count:N0} conversations · tap a card to continue it";
            _loaded = true;
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not load sessions: " + ex.Message;
        }
        finally
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
        }
    }

    private View CreateSessionCard()
    {
        var title = BoundLabel(nameof(ChatSessionInfo.Title), 17, Colors.White, FontAttributes.Bold);
        var date = BoundLabel(nameof(ChatSessionInfo.UpdatedDisplay), 11, Color.FromArgb("#94A3B8"));
        var activity = BoundLabel(nameof(ChatSessionInfo.ActivityDisplay), 12, Color.FromArgb("#CBD5E1"));
        var tokens = BoundLabel(nameof(ChatSessionInfo.TokenDisplay), 12, Color.FromArgb("#93C5FD"));
        var compute = BoundLabel(nameof(ChatSessionInfo.ComputeDisplay), 12, Color.FromArgb("#A7F3D0"));
        var model = BoundLabel(nameof(ChatSessionInfo.Model), 11, Color.FromArgb("#C4B5FD"));
        var open = new Label { Text = "Open  ›", TextColor = Color.FromArgb("#60A5FA"), FontAttributes = FontAttributes.Bold, FontSize = 13, HorizontalOptions = LayoutOptions.End };

        var card = new Border
        {
            Margin = new Thickness(0, 6),
            Padding = 14,
            BackgroundColor = Color.FromArgb("#151C2F"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Content = new VerticalStackLayout { Spacing = 6, Children = { title, date, activity, tokens, compute, model, open } }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => { if (card.BindingContext is ChatSessionInfo session) await OpenAsync(session); };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private async Task OpenAsync(ChatSessionInfo session)
    {
        _summary.Text = "Opening “" + session.Title + "”…";
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
