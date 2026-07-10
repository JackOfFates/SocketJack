using Microsoft.Web.WebView2.Wpf;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JackLLM;

public sealed class SocketChatWindow : Window
{
    private readonly WebView2 _browser = new WebView2();
    private readonly string _url;

    public SocketChatWindow(string url)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        Title = "SocketChat";
        Width = 1180;
        Height = 760;
        MinWidth = 720;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(12, 16, 23));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 25, 37)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 50, 71)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 9, 14, 9) };
        header.Child = new TextBlock { Text = "SocketChat", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 15 };
        Grid.SetRow(header, 0); grid.Children.Add(header);
        Grid.SetRow(_browser, 1); grid.Children.Add(_browser);
        Content = grid;
        Loaded += SocketChatWindow_Loaded;
    }

    private async void SocketChatWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _browser.EnsureCoreWebView2Async();
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _browser.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            Content = new TextBlock { Text = "SocketChat could not start: " + ex.Message, Foreground = Brushes.White, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
        }
    }
}
