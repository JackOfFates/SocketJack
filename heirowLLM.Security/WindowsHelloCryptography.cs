using System.Security.Cryptography;

namespace heirowLLM.Security;

public static class WindowsHelloCryptography {
    public static bool VerifySignature(string? subjectPublicKeyInfo, byte[] challenge, string? signature) {
        try {
            byte[] publicKeyBytes = Convert.FromBase64String(subjectPublicKeyInfo ?? "");
            byte[] signatureBytes = Convert.FromBase64String(signature ?? "");
            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out int bytesRead);
            return bytesRead == publicKeyBytes.Length &&
                   rsa.VerifyData(challenge, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        } catch {
            return false;
        }
    }
}
