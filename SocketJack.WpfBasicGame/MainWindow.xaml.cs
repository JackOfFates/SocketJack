using JackDebug.WPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SocketJack.WpfBasicGame;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window {
    private readonly ClickGame _game;

    public MainWindow() {
        InitializeComponent();
        BotEnabledCheck.IsChecked = false;
        _game = new ClickGame(this);
        PositionSideBySide(isHost: true);
        StartButton_Click(null, null);
        var w = new DebugWatcher(_game);
        w.IsRecursive = true;
        w.StartSession();
        DebugWatcher.CreateDebugWindow();
    }

    public MainWindow(int aaa) {
        InitializeComponent();
        BotEnabledCheck.IsChecked = false;
        JoinRadio.IsChecked = true;
        _game = new ClickGame(this);
        PositionSideBySide(isHost: false);
        StartButton_Click(null, null);
    }

    private void PositionSideBySide(bool isHost) {
        try {
            WindowStartupLocation = WindowStartupLocation.Manual;

            var leftBound = (int)SystemParameters.VirtualScreenLeft;
            var topBound = (int)SystemParameters.VirtualScreenTop;
            var widthBound = (int)SystemParameters.VirtualScreenWidth;
            var heightBound = (int)SystemParameters.VirtualScreenHeight;

            var w = Width;
            var h = Height;
            if (double.IsNaN(w) || w <= 0)
                w = 900;
            if (double.IsNaN(h) || h <= 0)
                h = 520;

            var wInt = (int)Math.Round(w);
            var hInt = (int)Math.Round(h);
            var totalW = wInt * 2;
            var left0 = leftBound + ((widthBound - totalW) / 2);
            var top0 = topBound + ((heightBound - hInt) / 2);

            Left = isHost ? left0 : left0 + wInt;
            Top = top0;
        } catch {
        }
    }

    public void ShareToggleButton_Click(object? sender, RoutedEventArgs? e) => _game.ToggleShare();

    private async void BotCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_game == null)
            return;
        await _game.OnBotCountChangedAsync();
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs? e) {
        if (_game == null)
            return;
        await _game.ToggleSessionAsync();
    }

    private void TargetButton_MouseDown(object sender, MouseButtonEventArgs e) => _game.OnTargetClicked(e);

    protected override void OnClosed(EventArgs e) {
        _game.Dispose();
        base.OnClosed(e);
    }
}