using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace LlmRuntime;

public interface ILlmToolSecretStore
{
    void SetSecret(string secretId, string secretValue);

    string? GetSecret(string secretId);

    bool DeleteSecret(string secretId);
}

public sealed class EncryptedLocalToolSecretStore : ILlmToolSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _secretRoot;
    private readonly string _keyPath;
    private readonly string _secretsPath;
    private readonly object _sync = new();

    public EncryptedLocalToolSecretStore(string toolRoot)
    {
        if (string.IsNullOrWhiteSpace(toolRoot))
            throw new ArgumentException("Tool root is required.", nameof(toolRoot));

        _secretRoot = Path.Combine(toolRoot, ".secrets");
        _keyPath = Path.Combine(_secretRoot, "tool-secrets.key");
        _secretsPath = Path.Combine(_secretRoot, "tool-secrets.json");
        Directory.CreateDirectory(_secretRoot);
        TryHideDirectory(_secretRoot);
    }

    public void SetSecret(string secretId, string secretValue)
    {
        secretId = NormalizeSecretId(secretId);
        lock (_sync)
        {
            var secrets = LoadSecrets();
            secrets[secretId] = Encrypt(secretValue ?? "");
            SaveSecrets(secrets);
        }
    }

    public string? GetSecret(string secretId)
    {
        secretId = NormalizeSecretId(secretId);
        lock (_sync)
        {
            var secrets = LoadSecrets();
            return secrets.TryGetValue(secretId, out var encrypted) ? Decrypt(encrypted) : null;
        }
    }

    public bool DeleteSecret(string secretId)
    {
        secretId = NormalizeSecretId(secretId);
        lock (_sync)
        {
            var secrets = LoadSecrets();
            if (!secrets.Remove(secretId))
                return false;
            SaveSecrets(secrets);
            return true;
        }
    }

    private EncryptedSecret Encrypt(string value)
    {
        if (OperatingSystem.IsWindows())
            return ProtectWithDpapi(value);

        return EncryptWithLocalKey(value);
    }

    [SupportedOSPlatform("windows")]
    private static EncryptedSecret ProtectWithDpapi(string value)
    {
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        return new EncryptedSecret
        {
            Algorithm = "DPAPI-CurrentUser",
            Ciphertext = Convert.ToBase64String(cipher),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private EncryptedSecret EncryptWithLocalKey(string value)
    {
        byte[] key = GetOrCreateKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag);
            return new EncryptedSecret
            {
                Algorithm = "AES-256-GCM",
                Iv = Convert.ToBase64String(nonce),
                Tag = Convert.ToBase64String(tag),
                Ciphertext = Convert.ToBase64String(cipher),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private string Decrypt(EncryptedSecret secret)
    {
        if (secret.Algorithm.Equals("DPAPI-CurrentUser", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DPAPI-protected tool secrets can only be read by the same Windows user profile that wrote them.");

            return UnprotectWithDpapi(secret);
        }

        return DecryptWithLocalKey(secret);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWithDpapi(EncryptedSecret secret)
    {
        byte[] cipher = Convert.FromBase64String(secret.Ciphertext);
        byte[] plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private string DecryptWithLocalKey(EncryptedSecret secret)
    {
        byte[] key = GetOrCreateKey();
        byte[] iv = Convert.FromBase64String(secret.Iv);
        byte[] cipher = Convert.FromBase64String(secret.Ciphertext);
        if (secret.Algorithm.Equals("AES-256-GCM", StringComparison.OrdinalIgnoreCase))
        {
            byte[] tag = Convert.FromBase64String(secret.Tag);
            byte[] gcmPlain = new byte[cipher.Length];
            try
            {
                using var gcm = new AesGcm(key, tag.Length);
                gcm.Decrypt(iv, cipher, tag, gcmPlain);
                return Encoding.UTF8.GetString(gcmPlain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(gcmPlain);
            }
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private byte[] GetOrCreateKey()
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows tool secrets use DPAPI and should not require a local AES key file.");

        if (File.Exists(_keyPath))
            return Convert.FromBase64String(File.ReadAllText(_keyPath));

        byte[] key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, Convert.ToBase64String(key));
        TryHideFile(_keyPath);
        return key;
    }

    private Dictionary<string, EncryptedSecret> LoadSecrets()
    {
        if (!File.Exists(_secretsPath))
            return new Dictionary<string, EncryptedSecret>(StringComparer.OrdinalIgnoreCase);

        string json = File.ReadAllText(_secretsPath);
        var values = JsonSerializer.Deserialize<Dictionary<string, EncryptedSecret>>(json);
        return values == null
            ? new Dictionary<string, EncryptedSecret>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, EncryptedSecret>(values, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSecrets(Dictionary<string, EncryptedSecret> secrets)
    {
        ClearHiddenAttribute(_secretsPath);
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(secrets, JsonOptions));
        TryHideFile(_secretsPath);
    }

    private static string NormalizeSecretId(string secretId)
    {
        if (string.IsNullOrWhiteSpace(secretId))
            throw new ArgumentException("Secret id is required.", nameof(secretId));
        return LlmToolRegistry.NormalizeId(secretId);
    }

    private static void TryHideDirectory(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }
    }

    private static void TryHideFile(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }
    }

    private static void ClearHiddenAttribute(string path)
    {
        try
        {
            if (File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        }
        catch
        {
        }
    }

    private sealed class EncryptedSecret
    {
        public string Algorithm { get; set; } = "";

        public string Iv { get; set; } = "";

        public string Ciphertext { get; set; } = "";

        public string Tag { get; set; } = "";

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
