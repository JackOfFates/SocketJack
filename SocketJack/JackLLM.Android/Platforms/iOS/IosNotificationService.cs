using Foundation;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Storage;
using UIKit;
using UserNotifications;

namespace JackLLM.Mobile.Platforms.iOS;

public sealed class IosNotificationService : IMobileNotificationService
{
    internal const string ServerKey = "jackllm_server_key";
    internal const string SessionKey = "jackllm_session_id";
    private const string ActiveIdentifier = "jackllm-generation";
    private const string AlertIdentifier = "jackllm-response";
    private const string UnreadKey = "jackllm.mobile.notifications.unread";

    public async Task EnsurePermissionAsync()
    {
        try
        {
            await UNUserNotificationCenter.Current.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);
        }
        catch
        {
        }
    }

    public void StartGeneration(string serverKey)
    {
        // iOS doesn't allow an Android-style foreground service. Generation remains
        // active while iOS grants background execution time and the completion alert
        // brings the user back to the matching Workstation session.
    }

    public void NotifyThinkingCompleted(string serverKey, string sessionId)
    {
        if (AppVisibilityService.IsActive) return;
        int unread = MobileNotificationText.IncrementUnread(Preferences.Default.Get(UnreadKey, 0));
        Preferences.Default.Set(UnreadKey, unread);

        var content = new UNMutableNotificationContent
        {
            Title = "JackLLM Mobile",
            Body = MobileNotificationText.FormatUnread(unread),
            Badge = NSNumber.FromInt32(unread),
            Sound = UNNotificationSound.Default,
            UserInfo = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString(serverKey ?? ""), new NSString(sessionId ?? "") },
                new NSObject[] { new NSString(ServerKey), new NSString(SessionKey) })
        };
        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(0.1, false);
        var request = UNNotificationRequest.FromIdentifier(AlertIdentifier, content, trigger);
        UNUserNotificationCenter.Current.AddNotificationRequest(request, _ => { });
    }

    public void StopGeneration() =>
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests(new[] { ActiveIdentifier });

    public void ResetUnread()
    {
        Preferences.Default.Set(UnreadKey, 0);
        UNUserNotificationCenter.Current.RemoveDeliveredNotifications(new[] { AlertIdentifier });
        if (OperatingSystem.IsIOSVersionAtLeast(16))
            UNUserNotificationCenter.Current.SetBadgeCount(0, _ => { });
        else
            UIApplication.SharedApplication.ApplicationIconBadgeNumber = 0;
    }
}

internal sealed class JackLlmNotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(
        UNUserNotificationCenter center,
        UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);

    public override void DidReceiveNotificationResponse(
        UNUserNotificationCenter center,
        UNNotificationResponse response,
        Action completionHandler)
    {
        string serverKey = response.Notification.Request.Content.UserInfo[new NSString(IosNotificationService.ServerKey)]?.ToString() ?? "";
        string sessionId = response.Notification.Request.Content.UserInfo[new NSString(IosNotificationService.SessionKey)]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(serverKey))
        {
            NotificationNavigationService? navigation =
                IPlatformApplication.Current?.Services.GetService(typeof(NotificationNavigationService)) as NotificationNavigationService;
            navigation?.QueueSession(serverKey, sessionId);
        }
        completionHandler();
    }
}
