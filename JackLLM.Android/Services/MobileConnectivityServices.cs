namespace JackLLM.Mobile.Services;

public sealed record MobileConnectivityStatus(
    bool HasNetwork,
    bool TailscaleInstalled,
    bool TailscaleEnabled)
{
    public string TailscaleDisplay => TailscaleEnabled
        ? "Tailscale enabled"
        : TailscaleInstalled ? "Tailscale disabled" : "Tailscale not installed";
}

public interface IMobileConnectivityService
{
    Task<MobileConnectivityStatus> GetStatusAsync();
    Task<bool> OpenTailscaleAsync();
}
