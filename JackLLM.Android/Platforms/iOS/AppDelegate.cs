using Foundation;
using JackLLM.Mobile.Platforms.iOS;
using JackLLM.Mobile.Services;
using Microsoft.Maui;
using UIKit;
using UserNotifications;

namespace JackLLM.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        bool launched = base.FinishedLaunching(application, launchOptions);
        UNUserNotificationCenter.Current.Delegate = new JackLlmNotificationDelegate();
        AppVisibilityService.SetActive(application.ApplicationState == UIApplicationState.Active);
        return launched;
    }

    public override void OnActivated(UIApplication application)
    {
        base.OnActivated(application);
        AppVisibilityService.SetActive(true);
        Resolve<IMobileNotificationService>()?.ResetUnread();
    }

    public override void DidEnterBackground(UIApplication application)
    {
        AppVisibilityService.SetActive(false);
        base.DidEnterBackground(application);
    }

    private static T? Resolve<T>() where T : class =>
        IPlatformApplication.Current?.Services.GetService(typeof(T)) as T;
}
