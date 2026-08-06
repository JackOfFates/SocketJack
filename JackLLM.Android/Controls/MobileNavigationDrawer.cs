using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Controls;

public sealed class MobileNavigationDrawer : Grid
{
    private readonly BoxView _scrim;
    private readonly Border _panel;
    private readonly VerticalStackLayout _sections;
    private readonly Label _identity;
    private bool _open;

    public MobileNavigationDrawer(string title, string subtitle)
    {
        IsVisible = false;
        InputTransparent = true;
        ZIndex = 100;

        _scrim = new BoxView { BackgroundColor = Colors.Black, Opacity = 0 };
        var dismissTap = new TapGestureRecognizer();
        dismissTap.Tapped += async (_, _) => await CloseAsync();
        _scrim.GestureRecognizers.Add(dismissTap);

        var close = new Button
        {
            Text = "×",
            FontSize = 28,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            WidthRequest = 48,
            HeightRequest = 48,
            Padding = 0,
            AutomationId = "CloseMobileMenu"
        };
        AutomationProperties.SetHelpText(close, "Close navigation menu");
        close.Clicked += async (_, _) => await CloseAsync();

        _identity = new Label
        {
            Text = subtitle,
            FontSize = 11,
            TextColor = Color.FromArgb("#94A3B8"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        var heading = new Grid
        {
            Padding = new Thickness(18, 12, 8, 10),
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }
        };
        heading.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = title, FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                _identity
            }
        }, 0);
        heading.Add(close, 1);

        _sections = new VerticalStackLayout { Spacing = 18, Padding = new Thickness(12, 6, 12, 28) };
        var scroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _sections
        };
        var panelGrid = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        panelGrid.Add(heading, 0, 0);
        panelGrid.Add(scroll, 0, 1);
        _panel = new Border
        {
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Fill,
            WidthRequest = 330,
            MaximumWidthRequest = 380,
            BackgroundColor = Color.FromArgb("#0B1220"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 18, 18, 0) },
            Content = panelGrid,
            TranslationX = -380
        };
        var closeSwipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Left, Threshold = 36 };
        closeSwipe.Swiped += async (_, _) => await CloseAsync();
        _panel.GestureRecognizers.Add(closeSwipe);

        Children.Add(_scrim);
        Children.Add(_panel);
        SizeChanged += (_, _) =>
        {
            if (Width <= 0) return;
            _panel.WidthRequest = Math.Min(380, Math.Max(276, Width * 0.88));
            if (!_open) _panel.TranslationX = -_panel.WidthRequest;
        };
    }

    public bool IsOpen => _open;

    public event EventHandler<Exception>? ActionFailed;

    public void SetIdentity(string value) => _identity.Text = value ?? "";

    public VerticalStackLayout AddSection(string title)
    {
        var content = new VerticalStackLayout { Spacing = 5 };
        _sections.Children.Add(new VerticalStackLayout
        {
            Spacing = 5,
            Children =
            {
                new Label
                {
                    Text = (title ?? "").ToUpperInvariant(),
                    Margin = new Thickness(10, 0, 0, 2),
                    FontSize = 10,
                    FontAttributes = FontAttributes.Bold,
                    CharacterSpacing = 1.5,
                    TextColor = Color.FromArgb("#64748B")
                },
                content
            }
        });
        return content;
    }

    public Button AddAction(VerticalStackLayout section, string icon, string label, Func<Task> action, string automationId, bool danger = false)
    {
        var button = new Button
        {
            Text = string.IsNullOrWhiteSpace(icon) ? label : icon + "   " + label,
            FontSize = 15,
            FontAttributes = FontAttributes.None,
            HorizontalOptions = LayoutOptions.Fill,
            HeightRequest = 54,
            Padding = new Thickness(14, 0),
            CornerRadius = 14,
            TextColor = danger ? Color.FromArgb("#FCA5A5") : Color.FromArgb("#E5E7EB"),
            BackgroundColor = danger ? Color.FromArgb("#2B1117") : Color.FromArgb("#111A2B"),
            AutomationId = automationId
        };
        AutomationProperties.SetHelpText(button, label);
        button.Clicked += async (_, _) =>
        {
            if (!button.IsEnabled) return;
            await CloseAsync();
            try { await action(); }
            catch (Exception ex) { ActionFailed?.Invoke(this, ex); }
        };
        section.Children.Add(button);
        return button;
    }

    public Label AddStatus(VerticalStackLayout section, string text, string automationId)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            TextColor = Color.FromArgb("#93C5FD"),
            Padding = new Thickness(14, 9),
            LineBreakMode = LineBreakMode.TailTruncation,
            AutomationId = automationId
        };
        section.Children.Add(new Border
        {
            BackgroundColor = Color.FromArgb("#0E1A30"),
            Stroke = Color.FromArgb("#1E3A5F"),
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = label
        });
        return label;
    }

    public void AddContent(VerticalStackLayout section, View content) => section.Children.Add(content);

    public Task OpenAsync()
    {
        if (_open) return Task.CompletedTask;
        _open = true;
        IsVisible = true;
        InputTransparent = false;
        _panel.TranslationX = -Math.Max(276, _panel.WidthRequest);
        _scrim.Opacity = 0;
        return AnimateAsync(opening: true);
    }

    public Task CloseAsync()
    {
        if (!_open) return Task.CompletedTask;
        _open = false;
        return AnimateAsync(opening: false);
    }

    private Task AnimateAsync(bool opening)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.AbortAnimation("MobileNavigationDrawer");
        double start = opening ? 0 : 1;
        double end = opening ? 1 : 0;
        this.Animate(
            "MobileNavigationDrawer",
            progress =>
            {
                _panel.TranslationX = -_panel.WidthRequest * (1 - progress);
                _scrim.Opacity = 0.62 * progress;
            },
            start,
            end,
            16,
            opening ? 240u : 190u,
            opening ? Easing.CubicOut : Easing.CubicIn,
            (_, cancelled) =>
            {
                if (!opening && !cancelled)
                {
                    IsVisible = false;
                    InputTransparent = true;
                    _panel.TranslationX = -_panel.WidthRequest;
                    _scrim.Opacity = 0;
                }
                completion.TrySetResult(true);
            });
        return completion.Task;
    }
}
