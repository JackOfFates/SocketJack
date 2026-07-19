using Android.Content;
using Android.Net;
using JackLLM.Mobile.Services;
using Microsoft.Maui.ApplicationModel;

namespace JackLLM.Mobile.Platforms.Android;

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
            vpnActive = manager.GetAllNetworks().Any(network =>
                manager.GetNetworkCapabilities(network)?.HasTransport(TransportType.Vpn) == true);
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
