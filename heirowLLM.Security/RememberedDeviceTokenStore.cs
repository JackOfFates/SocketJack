using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace heirowLLM.Security;

public sealed record RememberedDeviceToken(string Token, DateTimeOffset ExpiresUtc);

public sealed class RememberedDeviceTokenStore {
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SocketJack.heirowLLM.RememberedDeviceToken.v1");
    private readonly string _path;

    public RememberedDeviceTokenStore(string? path = null) {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "heirowLLM", "Security", "remembered-device-token.bin");
    }

    public RememberedDeviceToken? Load() {
        try {
            if (!File.Exists(_path)) return null;
            byte[] encrypted = File.ReadAllBytes(_path);
            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try {
                RememberedDeviceToken? token = JsonSerializer.Deserialize<RememberedDeviceToken>(plain, SecurityProtocol.Json);
                if (token == null || string.IsNullOrWhiteSpace(token.Token) || token.ExpiresUtc <= DateTimeOffset.UtcNow) {
                    Delete();
                    return null;
                }
                return token;
            } finally {
                CryptographicOperations.ZeroMemory(plain);
            }
        } catch {
            Delete();
            return null;
        }
    }

    public void Save(string token, DateTimeOffset expiresUtc) {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A remembered-device token is required.", nameof(token));
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new RememberedDeviceToken(token, expiresUtc), SecurityProtocol.Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, _path, true);
    }

    public void Delete() {
        try { File.Delete(_path); } catch (FileNotFoundException) { }
    }
}
