using Microsoft.Maui.Controls.Shapes;

namespace heirowLLM.Mobile.Controls;

public sealed class MobileNavigationDrawer : Grid
{
    private readonly BoxView _scrim;
    private readonly Border _panel;
    private readonly Grid _categoryView;
    private readonly Grid _detailView;
    private readonly VerticalStackLayout _categoryList;
    private readonly ScrollView _detailScroll;
    private readonly Label _detailTitle;
    private readonly Label _identity;
    private bool _open;
    private bool _showingSection;
    private bool _navigationAnimating;

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

        _categoryList = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(12, 8, 12, 28) };
        _categoryView = new Grid
        {
            Children =
            {
                new ScrollView
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Never,
                    Content = _categoryList
                }
            }
        };

        var back = new Button
        {
            Text = "‹  Back",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#BAE6FD"),
            BackgroundColor = Color.FromArgb("#10213A"),
            BorderColor = Color.FromArgb("#28517C"),
            BorderWidth = 1,
            CornerRadius = 12,
            HeightRequest = 46,
            Padding = new Thickness(13, 0),
            HorizontalOptions = LayoutOptions.Start,
            AutomationId = "MobileMenuBack"
        };
        AutomationProperties.SetHelpText(back, "Back to menu categories");
        back.Clicked += async (_, _) => await ShowCategoriesAsync();
        _detailTitle = new Label
        {
            FontSize = 20,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            VerticalTextAlignment = TextAlignment.Center
        };
        var detailHeading = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            ColumnSpacing = 12,
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        detailHeading.Add(back, 0);
        detailHeading.Add(_detailTitle, 1);
        _detailScroll = new ScrollView { VerticalScrollBarVisibility = ScrollBarVisibility.Never };
        _detailView = new Grid
        {
            IsVisible = false,
            Opacity = 0,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        _detailView.Add(detailHeading, 0, 0);
        _detailView.Add(_detailScroll, 0, 1);
        var backSwipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Right, Threshold = 34 };
        backSwipe.Swiped += async (_, _) => await ShowCategoriesAsync();
        _detailView.GestureRecognizers.Add(backSwipe);

        var navigationHost = new Grid { IsClippedToBounds = true };
        navigationHost.Children.Add(_categoryView);
        navigationHost.Children.Add(_detailView);
        var panelGrid = new Grid
        {
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }
        };
        panelGrid.Add(heading, 0, 0);
        panelGrid.Add(navigationHost, 0, 1);
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

    public bool IsShowingSection => _showingSection;

    public event EventHandler<Exception>? ActionFailed;

    public void SetIdentity(string value) => _identity.Text = value ?? "";

    public VerticalStackLayout AddSection(string title)
    {
        string sectionTitle = string.IsNullOrWhiteSpace(title) ? "Menu" : title.Trim();
        var content = new VerticalStackLayout { Spacing = 7, Padding = new Thickness(12, 5, 12, 28) };
        var label = new Label
        {
            Text = sectionTitle,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#E5E7EB"),
            VerticalTextAlignment = TextAlignment.Center
        };
        var chevron = new Label
        {
            Text = "›",
            FontSize = 28,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#7DD3FC"),
            VerticalTextAlignment = TextAlignment.Center
        };
        var row = new Grid
        {
            HeightRequest = 58,
            Padding = new Thickness(16, 0, 14, 0),
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }
        };
        row.Add(label, 0);
        row.Add(chevron, 1);
        var category = new Border
        {
            BackgroundColor = Color.FromArgb("#111A2B"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = row,
            AutomationId = "MobileMenuCategory" + new string(sectionTitle.Where(char.IsLetterOrDigit).ToArray())
        };
        AutomationProperties.SetName(category, sectionTitle);
        AutomationProperties.SetHelpText(category, "Open " + sectionTitle);
        var openSection = new TapGestureRecognizer();
        openSection.Tapped += async (_, _) => await ShowSectionAsync(sectionTitle, content);
        category.GestureRecognizers.Add(openSection);
        _categoryList.Children.Add(category);
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
        ResetToCategories();
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

    public Task NavigateBackAsync() => _showingSection ? ShowCategoriesAsync() : CloseAsync();

    private Task ShowSectionAsync(string title, View content)
    {
        if (!_open || _navigationAnimating || _showingSection) return Task.CompletedTask;
        _detailTitle.Text = title;
        _detailScroll.Content = null;
        _detailScroll.Content = content;
        _showingSection = true;
        return AnimateSectionAsync(forward: true);
    }

    private Task ShowCategoriesAsync()
    {
        if (!_showingSection || _navigationAnimating) return Task.CompletedTask;
        return AnimateSectionAsync(forward: false);
    }

    private Task AnimateSectionAsync(bool forward)
    {
        _navigationAnimating = true;
        _categoryView.IsVisible = true;
        _detailView.IsVisible = true;
        double width = Math.Max(276, _panel.WidthRequest);
        if (forward)
        {
            _categoryView.TranslationX = 0;
            _categoryView.Opacity = 1;
            _detailView.TranslationX = width;
            _detailView.Opacity = 0;
        }
        else
        {
            _categoryView.TranslationX = -width * .28;
            _categoryView.Opacity = 0;
            _detailView.TranslationX = 0;
            _detailView.Opacity = 1;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.AbortAnimation("MobileNavigationSection");
        this.Animate(
            "MobileNavigationSection",
            progress =>
            {
                double eased = Easing.CubicOut.Ease(progress);
                if (forward)
                {
                    _categoryView.TranslationX = -width * .28 * eased;
                    _categoryView.Opacity = 1 - eased;
                    _detailView.TranslationX = width * (1 - eased);
                    _detailView.Opacity = eased;
                }
                else
                {
                    _categoryView.TranslationX = -width * .28 * (1 - eased);
                    _categoryView.Opacity = eased;
                    _detailView.TranslationX = width * eased;
                    _detailView.Opacity = 1 - eased;
                }
            },
            0,
            1,
            16,
            220,
            Easing.Linear,
            (_, _) =>
            {
                _navigationAnimating = false;
                if (forward)
                {
                    _categoryView.IsVisible = false;
                }
                else
                {
                    _showingSection = false;
                    _detailView.IsVisible = false;
                    _detailScroll.Content = null;
                }
                completion.TrySetResult(true);
            });
        return completion.Task;
    }

    private void ResetToCategories()
    {
        this.AbortAnimation("MobileNavigationSection");
        _navigationAnimating = false;
        _showingSection = false;
        _categoryView.IsVisible = true;
        _categoryView.TranslationX = 0;
        _categoryView.Opacity = 1;
        _detailView.IsVisible = false;
        _detailView.TranslationX = Math.Max(276, _panel.WidthRequest);
        _detailView.Opacity = 0;
        _detailScroll.Content = null;
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
                    ResetToCategories();
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
