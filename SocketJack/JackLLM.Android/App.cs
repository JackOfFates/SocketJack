using JackLLM.Android.Pages;
using Microsoft.Maui.Controls;

namespace JackLLM.Android;

public class App : Application
{
    public App(ServerListPage serverListPage)
    {
        MainPage = new NavigationPage(serverListPage)
        {
            Title = "JackLLM"
        };
    }
}

