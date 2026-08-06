using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class MobileDiagnosticsPage : ContentPage
{
    private readonly JackLlmClient _client;
    private readonly bool _showUsers;
    private readonly VerticalStackLayout _content;
    private readonly Label _status;
    private readonly RefreshView _refresh;
    private MobileDiagnosticsSnapshot _snapshot = new();
    private bool _loaded;

    public MobileDiagnosticsPage(JackLlmClient client, bool showUsers = false)
    {
        _client = client;
        _showUsers = showUsers;
        Title = showUsers ? "Administrative Tools" : "Errors / Diagnosis";
        BackgroundColor = Color.FromArgb("#080D1A");

        _status = new Label { Text = "Loading diagnostics…", TextColor = Color.FromArgb("#93C5FD"), FontSize = 11 };
        _content = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(12, 8, 12, 30) };
        _refresh = new RefreshView
        {
            RefreshColor = Color.FromArgb("#60A5FA"),
            Content = new ScrollView { Content = _content }
        };
        _refresh.Refreshing += async (_, _) =>
        {
            try { await LoadAsync(); }
            finally { _refresh.IsRefreshing = false; }
        };
        ToolbarItems.Add(new ToolbarItem("Copy", null, async () => await CopyAsync()) { AutomationId = "CopyDiagnostics" });
        ToolbarItems.Add(new ToolbarItem("Refresh", null, async () => await LoadAsync()) { AutomationId = "RefreshDiagnostics" });
        Content = _refresh;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = "Refreshing…";
        try
        {
            _snapshot = await _client.GetDiagnosticsAsync();
            _loaded = true;
            Render();
        }
        catch (Exception ex)
        {
            _content.Children.Clear();
            _status.Text = "Diagnostics unavailable";
            _content.Children.Add(_status);
            _content.Children.Add(Card("Request failed", ex.Message, "error"));
        }
    }

    private void Render()
    {
        _content.Children.Clear();
        _status.Text = "Updated " + FormatDate(_snapshot.GeneratedUtc);
        _content.Children.Add(_status);
        MobileDiagnosticsHealth health = _snapshot.Health ?? new MobileDiagnosticsHealth();
        _content.Children.Add(Card(
            "Server health · " + (string.IsNullOrWhiteSpace(health.Tier) ? "unknown" : health.Tier),
            $"Score {health.Score:0.#} · {health.ActivePrompts:N0} active prompts · {health.FailedRequests:N0} failed requests · uptime {TimeSpan.FromSeconds(Math.Max(0, health.UptimeSeconds)):d\\:hh\\:mm}",
            health.FailedRequests > 0 ? "warning" : "ok"));

        if (_snapshot.IsAdministrator && (_showUsers || _snapshot.Users.Count > 0))
        {
            _content.Children.Add(SectionLabel("USERS · LIVE APPLICATION TRAFFIC"));
            double throughputMaximum = Math.Max(1, _snapshot.Users.Select(user => user.InboundBytesPerSecond + user.OutboundBytesPerSecond).DefaultIfEmpty(0).Max());
            foreach (MobileDiagnosticsUser user in _snapshot.Users.OrderByDescending(user => user.ConnectionState == "online").ThenBy(user => user.UserName))
                _content.Children.Add(UserCard(user, throughputMaximum));
        }

        _content.Children.Add(SectionLabel("RECENT ERRORS / EVENTS"));
        if (_snapshot.Events.Count == 0)
            _content.Children.Add(Card("No recent errors", "Nothing owner-scoped has been reported since the last server snapshot.", "ok"));
        else
            foreach (MobileDiagnosticsEvent item in _snapshot.Events.Take(100))
                _content.Children.Add(Card(
                    string.Join(" · ", new[] { item.Category, item.Name, item.Status }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    FormatDate(item.CreatedUtc) + (string.IsNullOrWhiteSpace(item.Route) ? "" : " · " + item.Route) + "\n" + item.Detail,
                    item.Status.Contains("fail", StringComparison.OrdinalIgnoreCase) || item.Status.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error" : "info"));
    }

    private View UserCard(MobileDiagnosticsUser user, double throughputMaximum)
    {
        string name = string.IsNullOrWhiteSpace(user.UserName) ? user.OwnerKey : user.UserName;
        double tokenRatio = user.Unlimited
            ? Math.Min(1, Math.Log10(Math.Max(1, user.TokensUsed) + 1) / 7d)
            : user.TokenLimit > 0 ? Math.Clamp((double)user.TokensUsed / user.TokenLimit, 0, 1) : 0;
        double throughput = Math.Max(0, user.InboundBytesPerSecond + user.OutboundBytesPerSecond);
        string tokenText = user.Unlimited
            ? $"{user.TokensUsed:N0} tokens · unlimited"
            : $"{user.TokensUsed:N0} / {user.TokenLimit:N0} tokens";
        string trafficText = $"↓ {HumanizeRate(user.InboundBytesPerSecond)} · ↑ {HumanizeRate(user.OutboundBytesPerSecond)} · total {HumanizeBytes(user.InboundBytesTotal + user.OutboundBytesTotal)}";
        var stack = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
                    Children =
                    {
                        new Label { Text = name, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                        new Label { Text = user.ConnectionState.Replace('_', ' '), FontSize = 11, TextColor = ConnectionColor(user.ConnectionState) }.Column(1)
                    }
                },
                new Label { Text = tokenText, FontSize = 11, TextColor = Color.FromArgb("#CBD5E1") },
                RgbMeter(tokenRatio, "Token usage: " + tokenText),
                new Label { Text = trafficText, FontSize = 11, TextColor = Color.FromArgb("#CBD5E1") },
                RgbMeter(Math.Clamp(throughput / throughputMaximum, 0, 1), "Application traffic: " + trafficText),
                new Label { Text = $"{user.AuthenticatedSessionCount:N0} signed-in sessions · last seen {FormatDate(user.LastSeenUtc)}", FontSize = 10, TextColor = Color.FromArgb("#64748B") }
            }
        };
        return new Border { Padding = 12, BackgroundColor = Color.FromArgb("#10192A"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 14 }, Content = stack };
    }

    private static View RgbMeter(double ratio, string helpText)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        var fill = new BoxView
        {
            HorizontalOptions = LayoutOptions.Start,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.DeepSkyBlue, 0), new(Colors.LimeGreen, .35f), new(Colors.Gold, .65f), new(Colors.HotPink, 1)
                },
                new Point(0, 0), new Point(1, 0))
        };
        var meter = new Grid
        {
            HeightRequest = 8,
            BackgroundColor = Color.FromArgb("#253047"),
            Clip = new RoundRectangleGeometry { CornerRadius = 4, Rect = new Rect(0, 0, 500, 8) },
            Children = { fill }
        };
        meter.SizeChanged += (_, _) => fill.WidthRequest = Math.Max(ratio > 0 ? 2 : 0, meter.Width * ratio);
        AutomationProperties.SetHelpText(meter, helpText);
        return meter;
    }

    private static Border Card(string title, string detail, string severity)
    {
        Color stroke = severity == "error" ? Color.FromArgb("#B91C1C") : severity == "warning" ? Color.FromArgb("#A16207") : severity == "ok" ? Color.FromArgb("#047857") : Color.FromArgb("#334155");
        return new Border
        {
            Padding = 12,
            BackgroundColor = Color.FromArgb("#10192A"),
            Stroke = stroke,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Content = new VerticalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Label { Text = title, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                    new Label { Text = detail, FontSize = 11, TextColor = Color.FromArgb("#CBD5E1"), LineBreakMode = LineBreakMode.WordWrap }
                }
            }
        };
    }

    private static Label SectionLabel(string text) => new() { Text = text, Margin = new Thickness(4, 8, 0, 0), FontSize = 10, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1.2, TextColor = Color.FromArgb("#64748B") };

    private async Task CopyAsync()
    {
        string events = string.Join("\n", _snapshot.Events.Select(item => $"{item.CreatedUtc} [{item.Status}] {item.Category}/{item.Name}: {item.Detail}"));
        await Clipboard.Default.SetTextAsync($"JackLLM diagnostics {_snapshot.GeneratedUtc}\nHealth: {_snapshot.Health.Tier} ({_snapshot.Health.Score:0.#})\n{events}");
        _status.Text = "Copied redacted diagnostics";
    }

    private static string FormatDate(string value) => DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed.ToLocalTime().ToString("g") : "unknown";
    private static string HumanizeRate(double value) => HumanizeBytes((long)Math.Max(0, value)) + "/s";
    private static string HumanizeBytes(long value) => value switch
    {
        >= 1_073_741_824 => $"{value / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{value / 1_048_576d:0.0} MB",
        >= 1024 => $"{value / 1024d:0.0} KB",
        _ => value + " B"
    };
    private static Color ConnectionColor(string state) => state switch
    {
        "online" => Color.FromArgb("#6EE7B7"),
        "signed_in_idle" => Color.FromArgb("#FCD34D"),
        _ => Color.FromArgb("#94A3B8")
    };
}
