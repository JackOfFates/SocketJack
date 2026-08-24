using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace heirowLLM.Security;

public sealed record NetworkBindingResult(
    bool PublicIpVerified,
    string? PublicIp,
    string LocalIpBinding,
    string? Error = null);

public interface INetworkBindingProvider {
    Task<NetworkBindingResult> GetCurrentAsync(CancellationToken cancellationToken);
}

public sealed class NetworkBindingProvider : INetworkBindingProvider {
    private static readonly Uri[] PublicIpEndpoints = {
        new("https://api.ipify.org"),
        new("https://checkip.amazonaws.com")
    };
    private readonly HttpClient _httpClient;

    public NetworkBindingProvider(HttpClient? httpClient = null) {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    public async Task<NetworkBindingResult> GetCurrentAsync(CancellationToken cancellationToken) {
        string localBinding = GetLocalIpBinding();
        Task<string?>[] requests = PublicIpEndpoints
            .Select(endpoint => GetPublicIpv4Async(endpoint, cancellationToken))
            .ToArray();
        string?[] results = await Task.WhenAll(requests);
        string[] verified = results.Where(value => value != null).Distinct(StringComparer.Ordinal).ToArray()!;
        if (verified.Length > 1)
            return new(false, null, localBinding, "Public IP verification providers returned conflicting addresses.");
        if (verified.Length == 0)
            return new(false, null, localBinding, "The public IP could not be verified.");
        return new(true, verified[0], localBinding);
    }

    private async Task<string?> GetPublicIpv4Async(Uri endpoint, CancellationToken cancellationToken) {
        try {
            string value = (await _httpClient.GetStringAsync(endpoint, cancellationToken)).Trim();
            return IPAddress.TryParse(value, out IPAddress? address) &&
                   address.AddressFamily == AddressFamily.InterNetwork
                ? address.ToString()
                : null;
        } catch {
            return null;
        }
    }

    internal static string GetLocalIpBinding() {
        return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(network => network.GetIPProperties().GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Select(address => address.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(address => address, StringComparer.Ordinal));
    }
}
