using System.Net.NetworkInformation;
using Foundation;
using JackLLM.Mobile.Services;
using UIKit;

namespace JackLLM.Mobile.Platforms.iOS;

public sealed class IosMobileConnectivityService : IMobileConnectivityService
{
    private static readonly NSUrl TailscaleUrl = new("tailscale://");

    public Task<MobileConnectivityStatus> GetStatusAsync()
    {
        bool installed = UIApplication.SharedApplication.CanOpenUrl(TailscaleUrl);
        bool vpnActive = NetworkInterface.GetAllNetworkInterfaces().Any(item =>
            item.Name.StartsWith("utun", StringComparison.OrdinalIgnoreCase) && item.OperationalStatus == OperationalStatus.Up);
        bool network = Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess != Microsoft.Maui.Networking.NetworkAccess.None;
        return Task.FromResult(new MobileConnectivityStatus(network, installed, installed && vpnActive));
    }

    public async Task<bool> OpenTailscaleAsync()
    {
        if (!UIApplication.SharedApplication.CanOpenUrl(TailscaleUrl)) return false;
        return await UIApplication.SharedApplication.OpenUrlAsync(TailscaleUrl, new UIApplicationOpenUrlOptions());
    }
}
