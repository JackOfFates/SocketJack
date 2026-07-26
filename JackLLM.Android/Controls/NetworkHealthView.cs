namespace JackLLM.Mobile.Controls;

public sealed class NetworkHealthView : ContentView
{
    private static readonly Color Offline = Color.FromArgb("#475569");
    private readonly BoxView[] _bars;

    public NetworkHealthView()
    {
        _bars = new[] { 5, 9, 13, 17 }
            .Select(height => new BoxView
            {
                WidthRequest = 4,
                HeightRequest = height,
                CornerRadius = 1,
                Color = Offline,
                VerticalOptions = LayoutOptions.End
            })
            .ToArray();

        var layout = new HorizontalStackLayout
        {
            Spacing = 2,
            HeightRequest = 20,
            WidthRequest = 28,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        foreach (BoxView bar in _bars) layout.Children.Add(bar);
        Content = layout;
        SetBars(0);
    }

    public void SetBars(int count)
    {
        count = Math.Clamp(count, 0, 4);
        Color active = count switch
        {
            1 => Color.FromArgb("#EF4444"),
            2 => Color.FromArgb("#FACC15"),
            3 => Color.FromArgb("#65D46E"),
            4 => Color.FromArgb("#00E676"),
            _ => Offline
        };
        for (int index = 0; index < _bars.Length; index++)
            _bars[index].Color = index < count ? active : Offline;

        string description = count == 0 ? "Workstation offline" : $"Workstation network health: {count} of 4 bars";
        AutomationProperties.SetName(this, description);
        SemanticProperties.SetDescription(this, description);
    }
}
