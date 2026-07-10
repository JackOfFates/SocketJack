using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.SocketChat {
    public sealed class SocketChatDeviceIdentity : IDisposable {
        private readonly ECDsa _key;
        public string PublicKey { get; }
        public string Fingerprint { get; }

        private SocketChatDeviceIdentity(ECDsa key) {
            _key = key;
            PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
            using var sha = SHA256.Create();
            Fingerprint = string.Concat(sha.ComputeHash(Convert.FromBase64String(PublicKey)).Take(10).Select(b => b.ToString("X2")));
        }

        public static SocketChatDeviceIdentity Create() => new SocketChatDeviceIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        public static SocketChatDeviceIdentity LoadOrCreate(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(path)) key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(path)), out _);
            else {
                File.WriteAllText(path, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
                TryRestrictFile(path);
            }
            return new SocketChatDeviceIdentity(key);
        }

        public string Sign(string value) => Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(value ?? ""), HashAlgorithmName.SHA256));

        public static bool Verify(string publicKey, string value, string signature) {
            try {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                return key.VerifyData(Encoding.UTF8.GetBytes(value ?? ""), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
            } catch { return false; }
        }

        public static string FingerprintForPublicKey(string publicKey) {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Convert.FromBase64String(publicKey)).Take(10).Select(b => b.ToString("X2")));
        }

        public static string CreatePairingCode() {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] bytes = new byte[6];
            RandomNumberGenerator.Fill(bytes);
            return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
        }

        public void Dispose() => _key.Dispose();

        private static void TryRestrictFile(string path) {
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }
        }
    }

    public static class SocketChatCrypto {
        public static string Encrypt(byte[] keyMaterial, byte[] plaintext) {
            DeriveKeys(keyMaterial, out var encryptionKey, out var macKey);
            using var aes = Aes.Create();
            aes.Key = encryptionKey; aes.GenerateIV(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var output = new MemoryStream();
            output.Write(aes.IV, 0, aes.IV.Length);
            using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, true)) crypto.Write(plaintext, 0, plaintext.Length);
            byte[] body = output.ToArray();
            using var hmac = new HMACSHA256(macKey);
            return Convert.ToBase64String(body.Concat(hmac.ComputeHash(body)).ToArray());
        }

        public static byte[] Decrypt(byte[] keyMaterial, string encoded) {
            DeriveKeys(keyMaterial, out var encryptionKey, out var macKey);
            byte[] value = Convert.FromBase64String(encoded);
            if (value.Length < 49) throw new CryptographicException("SocketChat payload is truncated.");
            byte[] body = value.Take(value.Length - 32).ToArray();
            byte[] suppliedMac = value.Skip(value.Length - 32).ToArray();
            using var hmac = new HMACSHA256(macKey);
            if (!FixedTimeEquals(hmac.ComputeHash(body), suppliedMac)) throw new CryptographicException("SocketChat payload authentication failed.");
            using var aes = Aes.Create();
            aes.Key = encryptionKey; aes.IV = body.Take(16).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            using var input = new MemoryStream(body, 16, body.Length - 16);
            using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var output = new MemoryStream(); crypto.CopyTo(output); return output.ToArray();
        }

        private static void DeriveKeys(byte[] material, out byte[] encryptionKey, out byte[] macKey) {
            using var hmac = new HMACSHA512(material ?? throw new ArgumentNullException(nameof(material)));
            byte[] expanded = hmac.ComputeHash(Encoding.UTF8.GetBytes("SocketChat/v1/encrypt-then-mac"));
            encryptionKey = expanded.Take(32).ToArray(); macKey = expanded.Skip(32).Take(32).ToArray();
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right) {
            if (left.Length != right.Length) return false;
            int diff = 0; for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i]; return diff == 0;
        }
    }
}
