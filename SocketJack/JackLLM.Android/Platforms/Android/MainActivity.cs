using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Content;
using Microsoft.Maui;
using JackLLM.Mobile.Platforms.Android;
using JackLLM.Mobile.Services;

namespace JackLLM.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : MauiAppCompatActivity
{
    private const int PickFolderRequest = 6017;
    private static TaskCompletionSource<Android.Net.Uri?>? _folderPicker;

    public static Task<Android.Net.Uri?> PickFolderAsync()
    {
        if (Platform.CurrentActivity is not MainActivity activity)
            return Task.FromResult<Android.Net.Uri?>(null);
        _folderPicker?.TrySetCanceled();
        _folderPicker = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        activity.StartActivityForResult(intent, PickFolderRequest);
        return _folderPicker.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickFolderRequest) return;
        Android.Net.Uri? uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null)
        {
            try
            {
                ActivityFlags granted = data is null
                    ? ActivityFlags.GrantReadUriPermission
                    : data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                ContentResolver?.TakePersistableUriPermission(uri, granted);
            }
            catch { }
        }
        _folderPicker?.TrySetResult(uri);
        _folderPicker = null;
    }
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleNotificationIntent(Intent);
    }

    protected override void OnNewIntent(Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleNotificationIntent(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppVisibilityService.SetActive(true);
        Resolve<IMobileNotificationService>()?.ResetUnread();
    }

    protected override void OnPause()
    {
        AppVisibilityService.SetActive(false);
        base.OnPause();
    }

    private void HandleNotificationIntent(Android.Content.Intent? intent)
    {
        if (intent?.GetBooleanExtra("open_sessions", false) != true) return;
        string serverKey = intent.GetStringExtra(AndroidNotificationService.ExtraServerKey) ?? "";
        Resolve<NotificationNavigationService>()?.QueueSessions(serverKey);
        intent.RemoveExtra("open_sessions");
    }

    private static T? Resolve<T>() where T : class =>
        IPlatformApplication.Current?.Services.GetService(typeof(T)) as T;
}
