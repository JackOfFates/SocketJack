using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JackLLM.Security;

public sealed class PasswordProtectedRecoveryFile {
    public string Format { get; set; } = "JackLLM Password-Protected Recovery File";
    public int Version { get; set; } = 2;
    public string Protection { get; set; } = "Argon2id-65536-3-4/AES-256-GCM";
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Tag { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class RecoveryFileProtection {
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("JackLLM-Recovery-File-v2");

    public static string Protect(PortableRecoveryFile recovery, string password) {
        ArgumentNullException.ThrowIfNull(recovery);
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A recovery-file password is required.", nameof(password));

        byte[] salt = PasswordSecurity.CreateSalt();
        byte[] key = PasswordSecurity.Derive(password, salt, Array.Empty<byte>());
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(recovery, SecurityProtocol.Json);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        try {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData);
            return JsonSerializer.Serialize(new PasswordProtectedRecoveryFile {
                Salt = Convert.ToBase64String(salt),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipher),
                Tag = Convert.ToBase64String(tag)
            }, new JsonSerializerOptions(SecurityProtocol.Json) { WriteIndented = true });
        } finally {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static PortableRecoveryFile Unprotect(string protectedFile, string password) {
        if (string.IsNullOrWhiteSpace(protectedFile))
            throw new InvalidDataException("The recovery file is empty.");
        if (string.IsNullOrEmpty(password))
            throw new InvalidDataException("Enter the recovery-file password.");

        try {
            PasswordProtectedRecoveryFile envelope =
                JsonSerializer.Deserialize<PasswordProtectedRecoveryFile>(protectedFile, SecurityProtocol.Json)
                ?? throw new InvalidDataException("The recovery file is empty.");
            if (envelope.Version != 2 ||
                !string.Equals(envelope.Format, "JackLLM Password-Protected Recovery File", StringComparison.Ordinal) ||
                !string.Equals(envelope.Protection, "Argon2id-65536-3-4/AES-256-GCM", StringComparison.Ordinal))
                throw new InvalidDataException("This is not a supported password-protected JackLLM recovery file.");

            byte[] salt = Convert.FromBase64String(envelope.Salt);
            byte[] nonce = Convert.FromBase64String(envelope.Nonce);
            byte[] cipher = Convert.FromBase64String(envelope.Ciphertext);
            byte[] tag = Convert.FromBase64String(envelope.Tag);
            byte[] key = PasswordSecurity.Derive(password, salt, Array.Empty<byte>());
            byte[] plain = new byte[cipher.Length];
            try {
                using var aes = new AesGcm(key, tag.Length);
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
                PortableRecoveryFile recovery =
                    JsonSerializer.Deserialize<PortableRecoveryFile>(plain, SecurityProtocol.Json)
                    ?? throw new InvalidDataException("The decrypted recovery file is empty.");
                if (recovery.Version != 1 ||
                    !string.Equals(recovery.Format, "JackLLM Workstation Recovery Key", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(recovery.RecoveryKey) ||
                    string.IsNullOrWhiteSpace(recovery.RecoveryBackup))
                    throw new InvalidDataException("The decrypted recovery data is invalid.");
                return recovery;
            } finally {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
            }
        } catch (CryptographicException) {
            throw new InvalidDataException("The recovery-file password is incorrect, or the file was modified.");
        } catch (FormatException) {
            throw new InvalidDataException("The recovery file is damaged or incomplete.");
        } catch (JsonException) {
            throw new InvalidDataException("The recovery file is not valid JSON.");
        }
    }
}
