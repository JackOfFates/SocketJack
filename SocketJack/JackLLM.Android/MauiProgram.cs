using JackLLM.Mobile.Pages;
using JackLLM.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace JackLLM.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
            });

        builder.Services.AddSingleton<SecureCredentialStore>();
        builder.Services.AddSingleton<ServerStore>();
        builder.Services.AddSingleton<ServerDirectoryService>();
        builder.Services.AddTransient<JackLlmClient>();
        builder.Services.AddSingleton<MobileAudioService>();
        builder.Services.AddSingleton<ServerListPage>();
        builder.Services.AddTransient<ChatHostPage>();

        return builder.Build();
    }
}
