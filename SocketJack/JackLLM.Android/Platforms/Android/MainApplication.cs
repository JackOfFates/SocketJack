using Android.App;
using Android.Runtime;
using Microsoft.Maui;

namespace JackLLM.Android;

[Application(Theme = "@style/Maui.SplashTheme", UsesCleartextTraffic = false)]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
