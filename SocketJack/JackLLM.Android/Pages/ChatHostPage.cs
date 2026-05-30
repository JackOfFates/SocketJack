using JackLLM.Android.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace JackLLM.Android.Pages;

public sealed class ChatHostPage : ContentPage
{
    private readonly string _launchUrl;
    private readonly WebView _webView;
    private readonly Label _statusLabel;

    public ChatHostPage(ServerInfo server, string launchUrl)
    {
        _launchUrl = launchUrl;

        Title = server.DisplayName;

        _statusLabel = new Label
        {
            Text = "Opening " + _launchUrl,
            TextColor = Colors.Gray,
            FontSize = 12
        };

        _webView = new WebView
        {
            Source = new UrlWebViewSource { Url = _launchUrl }
        };
#if ANDROID
        _webView.HandlerChanged += (_, __) => ConfigureAndroidWebView();
#endif

        _webView.Navigating += (_, __) =>
        {
            _statusLabel.Text = "Loading " + _launchUrl;
        };
        _webView.Navigated += (_, args) =>
        {
            _statusLabel.Text = "Loaded: " + args.Url;
        };

        ToolbarItems.Add(new ToolbarItem("Refresh", null, async () =>
        {
            if (Uri.TryCreate(_launchUrl, UriKind.Absolute, out Uri? _))
            {
                _webView.Source = _launchUrl;
            }
            await Task.CompletedTask;
        }));

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star }
            },
            RowSpacing = 6,
            Padding = new Thickness(0),
            Children = { }
        };

        layout.Children.Add(_statusLabel);
        Grid.SetColumn(_statusLabel, 0);
        Grid.SetRow(_statusLabel, 0);
        layout.Children.Add(_webView);
        Grid.SetColumn(_webView, 0);
        Grid.SetRow(_webView, 1);

        Content = layout;
    }

#if ANDROID
    private void ConfigureAndroidWebView()
    {
        if (_webView.Handler?.PlatformView is not global::Android.Webkit.WebView nativeWebView)
            return;

        nativeWebView.SetWebChromeClient(new VoiceWebChromeClient());
    }

    private sealed class VoiceWebChromeClient : global::Android.Webkit.WebChromeClient
    {
        public override void OnPermissionRequest(global::Android.Webkit.PermissionRequest? request)
        {
            if (request is null)
                return;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    string[] resources = request.GetResources() ?? Array.Empty<string>();
                    string[] audioResources = resources
                        .Where(resource => string.Equals(resource, global::Android.Webkit.PermissionRequest.ResourceAudioCapture, StringComparison.Ordinal))
                        .ToArray();

                    if (audioResources.Length == 0)
                    {
                        request.Deny();
                        return;
                    }

                    PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
                    if (status != PermissionStatus.Granted)
                        status = await Permissions.RequestAsync<Permissions.Microphone>();

                    if (status == PermissionStatus.Granted)
                        request.Grant(audioResources);
                    else
                        request.Deny();
                }
                catch
                {
                    try { request.Deny(); } catch { }
                }
            });
        }
    }
#endif
}
