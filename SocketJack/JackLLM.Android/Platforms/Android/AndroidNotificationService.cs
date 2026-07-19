using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using JackLLM.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Platforms.Android;

public sealed class AndroidNotificationService : IMobileNotificationService
{
    internal const string ActiveChannelId = "jackllm_generation";
    internal const string AlertChannelId = "jackllm_responses";
    internal const int ActiveNotificationId = 41001;
    internal const int AlertNotificationId = 41002;
    internal const string ActionStart = "com.socketjack.jackllm.mobile.GENERATION_START";
    internal const string ActionStop = "com.socketjack.jackllm.mobile.GENERATION_STOP";
    internal const string ExtraServerKey = "jackllm_server_key";
    internal const string ExtraSessionId = "jackllm_session_id";
    private const string UnreadKey = "jackllm.mobile.notifications.unread";
    private const string LatestServerKey = "jackllm.mobile.notifications.latestServer";

    public async Task EnsurePermissionAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            try { await Permissions.RequestAsync<Permissions.PostNotifications>(); }
            catch { }
        }
    }

    public void StartGeneration(string serverKey)
    {
        Context context = Platform.AppContext;
        EnsureChannels(context);
        var intent = new Intent(context, typeof(GenerationForegroundService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraServerKey, serverKey ?? "");
        try { ContextCompat.StartForegroundService(context, intent); }
        catch { context.StartService(intent); }
    }

    public void NotifyThinkingCompleted(string serverKey, string sessionId)
    {
        if (AppVisibilityService.IsActive) return;
        Context context = Platform.AppContext;
        EnsureChannels(context);
        int unread = MobileNotificationText.IncrementUnread(Preferences.Default.Get(UnreadKey, 0));
        Preferences.Default.Set(UnreadKey, unread);
        Preferences.Default.Set(LatestServerKey, serverKey ?? "");

        Intent open = new(context, typeof(MainActivity));
        open.SetAction(Intent.ActionView);
        open.PutExtra(ExtraServerKey, serverKey ?? "");
        open.PutExtra(ExtraSessionId, sessionId ?? "");
        open.PutExtra("open_session", true);
        open.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntentFlags pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) pendingFlags |= PendingIntentFlags.Immutable;
        PendingIntent pending = PendingIntent.GetActivity(context, 41002, open, pendingFlags)!;

        var builder = new NotificationCompat.Builder(context, AlertChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("JackLLM Mobile");
        builder.SetContentText(MobileNotificationText.FormatUnread(unread));
        builder.SetVisibility((int)NotificationVisibility.Private);
        builder.SetContentIntent(pending);
        builder.SetAutoCancel(true);
        builder.SetOnlyAlertOnce(false);
        builder.SetNumber(unread);
        builder.SetCategory(NotificationCompat.CategoryMessage);
        Notification notification = builder.Build() ?? throw new InvalidOperationException("Unable to build the response notification.");
        NotificationManagerCompat.From(context)?.Notify(AlertNotificationId, notification);
    }

    public void StopGeneration()
    {
        Context context = Platform.AppContext;
        var intent = new Intent(context, typeof(GenerationForegroundService));
        intent.SetAction(ActionStop);
        try { context.StartService(intent); }
        catch { NotificationManagerCompat.From(context)?.Cancel(ActiveNotificationId); }
    }

    public void ResetUnread()
    {
        Preferences.Default.Set(UnreadKey, 0);
        NotificationManagerCompat.From(Platform.AppContext)?.Cancel(AlertNotificationId);
    }

    internal static Notification BuildActiveNotification(Context context, string serverKey)
    {
        EnsureChannels(context);
        Intent open = new(context, typeof(MainActivity));
        open.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntentFlags pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) pendingFlags |= PendingIntentFlags.Immutable;
        PendingIntent pending = PendingIntent.GetActivity(context, 41001, open, pendingFlags)!;
        var builder = new NotificationCompat.Builder(context, ActiveChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("JackLLM Mobile");
        builder.SetContentText("Generating response…");
        builder.SetVisibility((int)NotificationVisibility.Private);
        builder.SetContentIntent(pending);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(NotificationCompat.CategoryProgress);
        return builder.Build() ?? throw new InvalidOperationException("Unable to build the active-response notification.");
    }

    internal static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null) return;
        var active = new NotificationChannel(ActiveChannelId, "Active responses", NotificationImportance.Low)
        {
            Description = "Quiet status while JackLLM Mobile is generating a response"
        };
        active.SetSound(null, null);
        active.EnableVibration(false);
        var alerts = new NotificationChannel(AlertChannelId, "Response alerts", NotificationImportance.Default)
        {
            Description = "Alerts when a mobile prompt has finished thinking"
        };
        manager.CreateNotificationChannel(active);
        manager.CreateNotificationChannel(alerts);
    }
}

[Service(
    Name = "com.socketjack.jackllm.mobile.GenerationForegroundService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class GenerationForegroundService : Service
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == AndroidNotificationService.ActionStop)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
            else StopForeground(true);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        string serverKey = intent?.GetStringExtra(AndroidNotificationService.ExtraServerKey) ?? "";
        StartForeground(AndroidNotificationService.ActiveNotificationId, AndroidNotificationService.BuildActiveNotification(this, serverKey));
        return StartCommandResult.NotSticky;
    }
}
