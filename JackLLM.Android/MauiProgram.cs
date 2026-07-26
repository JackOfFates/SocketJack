using JackLLM.Mobile.Pages;
using JackLLM.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using LibVLCSharp.MAUI;
#if ANDROID
using JackLLM.Mobile.Platforms.Android;
#elif IOS
using JackLLM.Mobile.Platforms.iOS;
#endif

namespace JackLLM.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseLibVLCSharp()
            .ConfigureMauiHandlers(handlers =>
            {
#if ANDROID
                handlers.AddHandler<LibVLCSharp.MAUI.VideoView, TextureVideoViewHandler>();
#endif
            })
            .ConfigureFonts(fonts =>
            {
            });

        builder.Services.AddSingleton<SecureCredentialStore>();
        builder.Services.AddSingleton<ServerStore>();
        builder.Services.AddSingleton<ServerDirectoryService>();
        builder.Services.AddTransient<JackLlmClient>();
        builder.Services.AddSingleton<MobileAudioService>();
        builder.Services.AddSingleton<NotificationNavigationService>();
        builder.Services.AddSingleton<RecentSessionStore>();
#if ANDROID
        builder.Services.AddSingleton<IMobileNotificationService, AndroidNotificationService>();
        builder.Services.AddSingleton<IMobileConnectivityService, AndroidMobileConnectivityService>();
#elif IOS
        builder.Services.AddSingleton<IMobileNotificationService, IosNotificationService>();
        builder.Services.AddSingleton<IMobileConnectivityService, IosMobileConnectivityService>();
#endif
        builder.Services.AddSingleton<MobileGenerationCoordinator>();
        builder.Services.AddSingleton<ServerListPage>();
        builder.Services.AddTransient<ChatHostPage>();

        return builder.Build();
    }
}
