using SocketJack.SocketChat;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace SocketJack.SocketChat.Windows;

public sealed class ProfileStore
{
    private readonly string _path;
    private readonly string _fingerprint;

    public ProfileStore(string root, string fingerprint)
    {
        _path = Path.Combine(root, "profile.json");
        _fingerprint = fingerprint;
    }

    public SocketChatUserProfile Load()
    {
        SocketChatUserProfile profile;
        try { profile = File.Exists(_path) ? JsonSerializer.Deserialize<SocketChatUserProfile>(File.ReadAllText(_path)) ?? new SocketChatUserProfile() : new SocketChatUserProfile(); }
        catch { profile = new SocketChatUserProfile(); }
        profile.DeviceFingerprint = _fingerprint;
        profile.NetworkIdentityHash = BuildNetworkIdentityHash();
        profile.ObservedIpAddresses = GetLocalAddresses();
        profile.BanIdentityKey = BuildBanKey(_fingerprint, profile.NetworkIdentityHash);
        if (string.IsNullOrWhiteSpace(profile.Username)) profile.Username = "SocketChat User";
        if (profile.CoordinatorUrl.StartsWith("http://100.", StringComparison.OrdinalIgnoreCase) || IsTailscaleIpUrl(profile.CoordinatorUrl))
            profile.CoordinatorUrl = "http://" + Environment.MachineName.ToLowerInvariant();
        else if (string.IsNullOrWhiteSpace(profile.CoordinatorUrl) || profile.CoordinatorUrl.Contains("127.0.0.1", StringComparison.Ordinal))
            profile.CoordinatorUrl = FindTailscaleCoordinatorUrl() ?? "http://127.0.0.1:4280";
        return profile;
    }

    public void Save(SocketChatUserProfile profile)
    {
        profile.DeviceFingerprint = _fingerprint;
        profile.NetworkIdentityHash = BuildNetworkIdentityHash();
        profile.ObservedIpAddresses = GetLocalAddresses();
        profile.BanIdentityKey = BuildBanKey(_fingerprint, profile.NetworkIdentityHash);
        profile.UpdatedUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(_path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string BuildNetworkIdentityHash()
    {
        string material = string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetPhysicalAddress().ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal));
        return Hash(material);
    }

    private static List<string> GetLocalAddresses()
    {
        try { return Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a)).Select(a => a.ToString()).Distinct().ToList(); }
        catch { return new List<string>(); }
    }

    private static string BuildBanKey(string fingerprint, string networkHash) => Hash("SocketChat/Ban/v1|" + fingerprint + "|" + networkHash);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
    private static string DefaultMasterListPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox", "SocketChat", "masterlist.json");
    private static string? FindTailscaleCoordinatorUrl()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] bytes = address.Address.GetAddressBytes();
                        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return "http://" + Environment.MachineName.ToLowerInvariant();
                    }
        }
        catch { }
        return null;
    }
    private static bool IsTailscaleIpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IPAddress.TryParse(uri.Host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes(); return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }
}
