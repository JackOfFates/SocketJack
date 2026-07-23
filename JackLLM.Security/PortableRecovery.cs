using System.Security.Cryptography;
using System.Text.Json;

namespace JackLLM.Security;

public static class PortableRecovery {
    private sealed class RecoveryEnvelope {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }

    private sealed class RecoveryPayload {
        public int Version { get; set; } = 1;
        public string RecoveryId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset IssuedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string AuthorizationSecret { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static string Create(string recoveryKey) {
        byte[] salt = PasswordSecurity.CreateSalt();
        byte[] key = PasswordSecurity.Derive(PasswordSecurity.NormalizeRecoveryKey(recoveryKey), salt, Array.Empty<byte>());
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new RecoveryPayload(), SecurityProtocol.Json);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        try {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, cipher, tag, "JackLLM-Recovery-v1"u8);
            return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new RecoveryEnvelope {
                Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag)
            }, SecurityProtocol.Json));
        } finally {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static bool Validate(string? recoveryKey, string? encodedEnvelope) {
        try {
            byte[] envelopeBytes = Convert.FromBase64String(encodedEnvelope ?? "");
            RecoveryEnvelope envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(envelopeBytes, SecurityProtocol.Json) ?? throw new InvalidDataException();
            byte[] key = PasswordSecurity.Derive(PasswordSecurity.NormalizeRecoveryKey(recoveryKey), Convert.FromBase64String(envelope.Salt), Array.Empty<byte>());
            byte[] cipher = Convert.FromBase64String(envelope.Ciphertext);
            byte[] plain = new byte[cipher.Length];
            try {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(Convert.FromBase64String(envelope.Nonce), cipher, Convert.FromBase64String(envelope.Tag), plain, "JackLLM-Recovery-v1"u8);
                RecoveryPayload? payload = JsonSerializer.Deserialize<RecoveryPayload>(plain, SecurityProtocol.Json);
                return payload?.Version == 1 && !string.IsNullOrWhiteSpace(payload.AuthorizationSecret);
            } finally {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plain);
            }
        } catch { return false; }
    }
}
