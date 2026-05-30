using JackLLM.Android.Pages;
using JackLLM.Android.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace JackLLM.Android;

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

        builder.Services.AddSingleton<ServerDirectoryService>();
        builder.Services.AddSingleton<ServerListPage>();
        builder.Services.AddTransient<ChatHostPage>();

        return builder.Build();
    }
}

