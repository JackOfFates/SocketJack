using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;

namespace JackLLM.Security.Tests;

[TestClass]
public sealed class PortableRecoveryTests {
    [TestMethod]
    public void PortableBackupRequiresItsRandomRecoveryKey() {
        string key = PasswordSecurity.CreateRecoveryKey();
        string backup = PortableRecovery.Create(key);
        Assert.IsTrue(PortableRecovery.Validate(key, backup));
        Assert.IsFalse(PortableRecovery.Validate(PasswordSecurity.CreateRecoveryKey(), backup));
        Assert.IsFalse(PortableRecovery.Validate(key, backup + "tampered"));
    }

    [TestMethod]
    public void RecoveryFileIsPasswordProtectedAndAuthenticated() {
        string recoveryKey = PasswordSecurity.CreateRecoveryKey();
        string recoveryBackup = PortableRecovery.Create(recoveryKey);
        var payload = new PortableRecoveryFile {
            RecoveryKey = recoveryKey,
            RecoveryBackup = recoveryBackup
        };
        const string password = "Violet-River-Quartz-Engine-47";

        string protectedFile = RecoveryFileProtection.Protect(payload, password);

        Assert.IsFalse(protectedFile.Contains(recoveryKey, StringComparison.Ordinal));
        Assert.IsFalse(protectedFile.Contains(recoveryBackup, StringComparison.Ordinal));
        PortableRecoveryFile restored = RecoveryFileProtection.Unprotect(protectedFile, password);
        Assert.AreEqual(recoveryKey, restored.RecoveryKey);
        Assert.AreEqual(recoveryBackup, restored.RecoveryBackup);
        Assert.ThrowsException<InvalidDataException>(() =>
            RecoveryFileProtection.Unprotect(protectedFile, "Wrong-Recovery-File-Password-97!"));

        PasswordProtectedRecoveryFile envelope =
            System.Text.Json.JsonSerializer.Deserialize<PasswordProtectedRecoveryFile>(protectedFile, SecurityProtocol.Json)!;
        envelope.Ciphertext = Convert.ToBase64String(Convert.FromBase64String(envelope.Ciphertext)
            .Select((value, index) => index == 0 ? (byte)(value ^ 1) : value).ToArray());
        string tampered = System.Text.Json.JsonSerializer.Serialize(envelope, SecurityProtocol.Json);
        Assert.ThrowsException<InvalidDataException>(() =>
            RecoveryFileProtection.Unprotect(tampered, password));
    }

    [TestMethod]
    public void WindowsHelloSubjectPublicKeyInfoAndPkcs1SignatureVerify() {
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        using RSA rsa = RSA.Create(2048);
        string publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        string signature = Convert.ToBase64String(
            rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        Assert.IsTrue(WindowsHelloCryptography.VerifySignature(publicKey, challenge, signature));
        Assert.IsFalse(WindowsHelloCryptography.VerifySignature(
            publicKey, RandomNumberGenerator.GetBytes(32), signature));
        string pssSignature = Convert.ToBase64String(
            rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        Assert.IsFalse(WindowsHelloCryptography.VerifySignature(publicKey, challenge, pssSignature));
    }
}
