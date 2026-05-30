using JackLLM.Android.Models;
using JackLLM.Android.Services;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JackLLM.Android.Pages;

public sealed class ServerListPage : ContentPage
{
    private readonly ServerDirectoryService _directoryService;
    private readonly RefreshView _refreshView;
    private readonly CollectionView _collectionView;
    private readonly Label _statusLabel;
    private readonly ActivityIndicator _loadingIndicator;

    public ServerListPage(ServerDirectoryService directoryService)
    {
        _directoryService = directoryService;
        Title = "JackLLM Servers";

        _statusLabel = new Label
        {
            Text = "Loading servers...",
            FontSize = 12,
            TextColor = Colors.Gray
        };

        _loadingIndicator = new ActivityIndicator
        {
            IsRunning = true,
            IsVisible = true,
            HeightRequest = 20,
            WidthRequest = 20
        };

        _collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(CreateServerTemplate)
        };
        _collectionView.SelectionChanged += OnSelectionChanged;

        _refreshView = new RefreshView();
        _refreshView.Content = _collectionView;
        _refreshView.Refreshing += OnRefreshRequested;

        Content = new VerticalStackLayout
        {
            Padding = new Thickness(12),
            Spacing = 12,
            Children =
            {
                new Label
                {
                    Text = "Select a server to launch JackLLM in-app.",
                    FontSize = 14
                },
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        _loadingIndicator,
                        _statusLabel
                    }
                },
                new Button
                {
                    Text = "Refresh server list",
                    Command = new Command(async () => await ReloadAsync())
                },
                _refreshView
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ReloadAsync();
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count == 0)
            return;

        if (e.CurrentSelection[0] is not ServerInfo selected)
            return;

        _collectionView.SelectedItem = null;
        await NavigateToServerAsync(selected);
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        await ReloadAsync();
        _refreshView.IsRefreshing = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            _loadingIndicator.IsRunning = true;
            _loadingIndicator.IsVisible = true;
            _statusLabel.Text = "Loading available servers...";

            IReadOnlyList<ServerInfo> servers = await _directoryService.LoadAsync();
            _collectionView.ItemsSource = servers;
            _statusLabel.Text = servers.Count == 0
                ? "No servers found from master lists."
                : $"{servers.Count} server(s) loaded.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Unable to load server list: " + ex.Message;
            _collectionView.ItemsSource = Array.Empty<ServerInfo>();
        }
        finally
        {
            _loadingIndicator.IsRunning = false;
            _loadingIndicator.IsVisible = false;
        }
    }

    private async Task NavigateToServerAsync(ServerInfo selectedServer)
    {
        string launchUrl = _directoryService.BuildLaunchUrl(selectedServer);
        await Navigation.PushAsync(new ChatHostPage(selectedServer, launchUrl));
    }

    private static View CreateServerTemplate()
    {
        var title = new Label();
        title.SetBinding(Label.TextProperty, nameof(ServerInfo.DisplayName));
        title.FontSize = 16;

        var details = new Label();
        details.SetBinding(Label.TextProperty, nameof(ServerInfo.ModelLine));
        details.TextColor = Colors.Gray;
        details.FontSize = 12;

        var meta = new Label();
        meta.SetBinding(Label.TextProperty, nameof(ServerInfo.HardwareLine));
        meta.TextColor = Colors.Gray;
        meta.FontSize = 12;

        var status = new Label();
        status.TextColor = Colors.Gray;
        status.FontSize = 12;
        status.SetBinding(Label.TextProperty, new Binding(nameof(ServerInfo.Online), converter: new BoolToStatusTextConverter()));

        return new Border
        {
            Padding = new Thickness(12, 10),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            Stroke = Colors.Black.WithAlpha(0.2f),
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    title,
                    details,
                    meta,
                    status
                }
            }
        };
    }
}

internal sealed class BoolToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool online)
            return online ? "Online" : "Offline";
        return "Unknown";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return false;
    }
}
