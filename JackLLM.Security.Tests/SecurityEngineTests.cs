using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace JackLLM.Security.Tests;

[TestClass]
public sealed class SecurityEngineTests {
    private string _root = null!;
    private string _registryPath = null!;
    private SecurityStateStore _store = null!;
    private SecurityEngine _engine = null!;
    private CngKey _helloKey = null!;

    [TestInitialize]
    public void Initialize() {
        string id = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "JackLLM.Security.Tests", id);
        _registryPath = @"Software\SocketJack\JackLLM\Security\Tests\" + id;
        _store = new SecurityStateStore(true, _root, _registryPath);
        _engine = new SecurityEngine(_store, true);
        _helloKey = CngKey.Create(CngAlgorithm.Rsa, null, new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowExport });
    }

    [TestCleanup]
    public void Cleanup() {
        _helloKey.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        Registry.CurrentUser.DeleteSubKeyTree(_registryPath, false);
    }

    [TestMethod]
    public void EnrollUnlockAndChallengeReplayAreEnforced() {
        SecurityResponse enrolled = Enroll();
        Assert.IsTrue(enrolled.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(enrolled.RecoveryKey));
        Assert.AreEqual(SecurityStateKind.Locked, _engine.GetStatus().State);

        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
        var request = new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            Signature = Sign(begin.Challenge!)
        };
        SecurityResponse unlocked = _engine.Unlock(request);
        Assert.IsTrue(unlocked.Success);
        Assert.AreEqual(SecurityStateKind.Unlocked, unlocked.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unlocked.UnlockGrant));
        Assert.IsFalse(_engine.Unlock(request).Success, "A challenge must be single-use.");
        Assert.IsTrue(_engine.ConsumeGrant(unlocked.UnlockGrant), "A fresh unlock grant must be accepted once.");
        Assert.IsFalse(_engine.ConsumeGrant(unlocked.UnlockGrant), "An unlock grant must not be replayable.");
    }

    [TestMethod]
    public void ThreeFailuresPersistCooldown() {
        Enroll();
        for (int attempt = 0; attempt < 3; attempt++) {
            SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
            SecurityResponse failed = _engine.Unlock(new SecurityRequest {
                Operation = SecurityOperation.CompleteUnlock, ChallengeId = begin.ChallengeId,
                Password = "Wrong Password Value 92!", Signature = Sign(begin.Challenge!)
            });
            if (attempt < 2) Assert.AreEqual(SecurityStateKind.Locked, failed.State);
            else Assert.AreEqual(SecurityStateKind.Cooldown, failed.State);
        }
        SecurityEngine restarted = new(_store, true);
        Assert.AreEqual(SecurityStateKind.Cooldown, restarted.GetStatus().State);
    }

    [TestMethod]
    public void DeletedPrimaryCredentialFailsClosed() {
        Enroll();
        File.Delete(Path.Combine(_root, "workstation-access.bin"));
        Assert.AreEqual(SecurityStateKind.CredentialMissing, _engine.GetStatus().State);
    }

    [TestMethod]
    public void ModifiedHardwareBindingFailsClosed() {
        Enroll();
        WorkstationCredentialRecord record = _store.Load()!;
        record.HardwareId = new string('0', 64);
        _store.Save(record);
        Assert.AreEqual(SecurityStateKind.HardwareMismatch, _engine.GetStatus().State);
    }

    [TestMethod]
    public void BackwardClockMovementFailsClosed() {
        Enroll();
        WorkstationCredentialRecord record = _store.Load()!;
        record.LastObservedUtc = DateTimeOffset.UtcNow.AddHours(1);
        _store.Save(record);
        SecurityResponse status = _engine.GetStatus();
        Assert.AreEqual(SecurityStateKind.Cooldown, status.State);
        Assert.IsTrue(status.CooldownUntilUtc > DateTimeOffset.UtcNow.AddHours(23));
    }

    [TestMethod]
    public void PortableRecoveryEnrollsAReplacementMachineStore() {
        SecurityResponse originalEnrollment = Enroll();
        string replacementId = Guid.NewGuid().ToString("N");
        string replacementRoot = Path.Combine(Path.GetTempPath(), "JackLLM.Security.Tests", replacementId);
        string replacementRegistry = @"Software\SocketJack\JackLLM\Security\Tests\" + replacementId;
        using CngKey replacementKey = CngKey.Create(CngAlgorithm.Rsa, null, new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowExport });
        try {
            var replacementStore = new SecurityStateStore(true, replacementRoot, replacementRegistry);
            var replacementEngine = new SecurityEngine(replacementStore, true);
            SecurityResponse begin = replacementEngine.Begin(SecurityOperation.BeginUnlock);
            using var rsa = new RSACng(replacementKey);
            string signature = Convert.ToBase64String(rsa.SignData(Convert.FromBase64String(begin.Challenge!), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            SecurityResponse recovered = replacementEngine.Recover(new SecurityRequest {
                Operation = SecurityOperation.Recover,
                ChallengeId = begin.ChallengeId,
                RecoveryKey = originalEnrollment.RecoveryKey,
                RecoveryBackup = originalEnrollment.RecoveryBackup,
                NewPassword = "Replacement Quartz Harbor Lantern 73!",
                PublicKey = Convert.ToBase64String(replacementKey.Export(CngKeyBlobFormat.GenericPublicBlob)),
                Signature = signature,
                Attestation = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            });
            Assert.IsTrue(recovered.Success, recovered.Message);
            Assert.AreEqual(SecurityStateKind.Locked, replacementEngine.GetStatus().State);
        } finally {
            if (Directory.Exists(replacementRoot)) Directory.Delete(replacementRoot, true);
            Registry.CurrentUser.DeleteSubKeyTree(replacementRegistry, false);
        }
    }

    [TestMethod]
    public void PasswordChangeRequiresHelloAndCurrentPassword() {
        Enroll();
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
        SecurityResponse changed = _engine.ChangePassword(new SecurityRequest {
            Operation = SecurityOperation.ChangePassword,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            NewPassword = "Copper Meadow Lantern Harbor 84!",
            Signature = Sign(begin.Challenge!)
        });
        Assert.IsTrue(changed.Success, changed.Message);

        SecurityResponse unlockBegin = _engine.Begin(SecurityOperation.BeginUnlock);
        SecurityResponse unlocked = _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = unlockBegin.ChallengeId,
            Password = "Copper Meadow Lantern Harbor 84!",
            Signature = Sign(unlockBegin.Challenge!)
        });
        Assert.IsTrue(unlocked.Success, unlocked.Message);
    }

    private SecurityResponse Enroll() {
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginEnroll);
        string publicKey = Convert.ToBase64String(_helloKey.Export(CngKeyBlobFormat.GenericPublicBlob));
        return _engine.Enroll(new SecurityRequest {
            Operation = SecurityOperation.Enroll,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            PublicKey = publicKey,
            Signature = Sign(begin.Challenge!),
            Attestation = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        });
    }

    private string Sign(string challenge) {
        using var rsa = new RSACng(_helloKey);
        byte[] signature = rsa.SignData(Convert.FromBase64String(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Convert.ToBase64String(signature);
    }

    private const string StrongPassword = "Correct Horse Battery Staple 92!";
}
