using JackLLM.Mobile.Pages;
using Microsoft.Maui.Controls;

namespace JackLLM.Mobile;

public class App : Application
{
    private readonly Page _rootPage;

    public App(ServerListPage serverListPage)
    {
        _rootPage = new NavigationPage(serverListPage)
        {
            Title = "JackLLM Mobile",
            BarBackgroundColor = Color.FromArgb("#111827"),
            BarTextColor = Colors.White
        };
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_rootPage);
}
