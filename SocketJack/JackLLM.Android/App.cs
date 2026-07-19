using JackLLM.Mobile.Pages;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls;

namespace JackLLM.Mobile;

public class App : Application
{
    private readonly Page _rootPage;
    private readonly NavigationPage _navigation;
    private readonly NotificationNavigationService _notificationNavigation;
    private readonly ServerStore _serverStore;
    private readonly ServerDirectoryService _directory;
    private readonly SecureCredentialStore _credentials;
    private readonly MobileGenerationCoordinator _generation;
    private readonly RecentSessionStore _recentSessions;

    public App(
        ServerListPage serverListPage,
        NotificationNavigationService notificationNavigation,
        ServerStore serverStore,
        ServerDirectoryService directory,
        SecureCredentialStore credentials,
        MobileGenerationCoordinator generation,
        RecentSessionStore recentSessions)
    {
        _notificationNavigation = notificationNavigation;
        _serverStore = serverStore;
        _directory = directory;
        _credentials = credentials;
        _generation = generation;
        _recentSessions = recentSessions;
        _navigation = new NavigationPage(serverListPage)
        {
            Title = "JackLLM Mobile",
            BarBackgroundColor = Color.FromArgb("#111827"),
            BarTextColor = Colors.White
        };
        _rootPage = _navigation;
        _notificationNavigation.PendingChanged += (_, _) => MainThread.BeginInvokeOnMainThread(async () => await OpenPendingSessionsAsync());
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_rootPage);
        MainThread.BeginInvokeOnMainThread(async () => await OpenPendingSessionsAsync());
        return window;
    }

    private async Task OpenPendingSessionsAsync()
    {
        NotificationNavigationTarget? target = _notificationNavigation.TakePending();
        if (target is null || string.IsNullOrWhiteSpace(target.ServerKey)) return;
        ServerInfo? saved = _serverStore.Load().FirstOrDefault(item => item.LaunchKey.Equals(target.ServerKey, StringComparison.OrdinalIgnoreCase));
        if (saved is null) return;
        var launch = new ServerInfo
        {
            ServerId = saved.ServerId,
            Name = saved.Name,
            OwnerUserName = saved.OwnerUserName,
            Endpoint = _directory.BuildLaunchUrl(saved),
            OpenAiBaseUrl = saved.OpenAiBaseUrl,
            SelectedModel = saved.SelectedModel,
            AvailableModels = saved.AvailableModels,
            CertificateFingerprint = saved.CertificateFingerprint,
            IsSaved = true,
            CredentialKey = saved.LaunchKey
        };
        var client = new JackLlmClient(_credentials);
        var chat = new ChatHostPage(launch, client, _serverStore, _generation, _recentSessions, target.SessionId);
        await _navigation.PopToRootAsync(false);
        await _navigation.PushAsync(chat, true);
    }
}
