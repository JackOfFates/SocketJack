using Android.Content;
using Android.Net;
using heirowLLM.Mobile.Services;
using Microsoft.Maui.ApplicationModel;

namespace heirowLLM.Mobile.Platforms.Android;

public sealed class AndroidMobileConnectivityService : IMobileConnectivityService
{
    private const string TailscalePackage = "com.tailscale.ipn";

    public Task<MobileConnectivityStatus> GetStatusAsync()
    {
        Context context = Platform.AppContext;
        bool installed = context.PackageManager?.GetLaunchIntentForPackage(TailscalePackage) is not null;
        bool vpnActive = false;
        if (context.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                Network? activeNetwork = manager.ActiveNetwork;
                vpnActive = activeNetwork is not null &&
                    manager.GetNetworkCapabilities(activeNetwork)?.HasTransport(TransportType.Vpn) == true;
            }
            else
            {
#pragma warning disable CA1422, CS0618 // API 21-22 compatibility path.
                vpnActive = manager.ActiveNetworkInfo?.Type == ConnectivityType.Vpn;
#pragma warning restore CA1422, CS0618
            }
        }
        bool network = Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess != Microsoft.Maui.Networking.NetworkAccess.None;
        return Task.FromResult(new MobileConnectivityStatus(network, installed, installed && vpnActive));
    }

    public Task<bool> OpenTailscaleAsync()
    {
        Context context = Platform.AppContext;
        Intent? intent = context.PackageManager?.GetLaunchIntentForPackage(TailscalePackage);
        if (intent is null) return Task.FromResult(false);
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        context.StartActivity(intent);
        return Task.FromResult(true);
    }
}
