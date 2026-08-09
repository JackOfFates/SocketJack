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
    private RSA _helloKey = null!;
    private FakeNetworkBindingProvider _network = null!;

    [TestInitialize]
    public void Initialize() {
        string id = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "JackLLM.Security.Tests", id);
        _registryPath = @"Software\SocketJack\JackLLM\Security\Tests\" + id;
        _store = new SecurityStateStore(true, _root, _registryPath);
        _network = new FakeNetworkBindingProvider("203.0.113.10", "192.168.1.20");
        _engine = new SecurityEngine(_store, true, _network);
        _helloKey = RSA.Create(2048);
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
        Assert.IsTrue(enrolled.UnlockGrantExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(9));
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
            Assert.AreEqual("Windows Hello verified, but the workstation password was not accepted.", failed.Message);
            if (attempt < 2) Assert.AreEqual(SecurityStateKind.Locked, failed.State);
            else Assert.AreEqual(SecurityStateKind.Cooldown, failed.State);
        }
        SecurityEngine restarted = new(_store, true);
        Assert.AreEqual(SecurityStateKind.Cooldown, restarted.GetStatus().State);
    }

    [TestMethod]
    public void HelloFailureDoesNotConsumePasswordAttempts() {
        Enroll();
        using RSA differentHelloKey = RSA.Create(2048);

        for (int attempt = 0; attempt < 6; attempt++) {
            SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
            string wrongHelloSignature = Convert.ToBase64String(differentHelloKey.SignData(
                Convert.FromBase64String(begin.Challenge!), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            SecurityResponse rejected = _engine.Unlock(new SecurityRequest {
                Operation = SecurityOperation.CompleteUnlock,
                ChallengeId = begin.ChallengeId,
                Password = StrongPassword,
                Signature = wrongHelloSignature
            });

            Assert.AreEqual(SecurityStateKind.Locked, rejected.State);
            StringAssert.StartsWith(rejected.Message, "Windows Hello did not match");
        }

        WorkstationCredentialRecord record = _store.Load()!;
        Assert.AreEqual(0, record.FailedAttempts);
        Assert.IsNull(record.CooldownUntilUtc);

        SecurityResponse validBegin = _engine.Begin(SecurityOperation.BeginUnlock);
        SecurityResponse unlocked = _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = validBegin.ChallengeId,
            Password = StrongPassword,
            Signature = Sign(validBegin.Challenge!)
        });
        Assert.IsTrue(unlocked.Success, unlocked.Message);
    }

    [TestMethod]
    public void ValidHelloClearsPersistedPasswordCooldownAndMigratesToHelloOnly() {
        Enroll();
        for (int attempt = 0; attempt < 3; attempt++) {
            SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
            _engine.Unlock(new SecurityRequest {
                Operation = SecurityOperation.CompleteUnlock,
                ChallengeId = begin.ChallengeId,
                Password = "Wrong Password Value 92!",
                Signature = Sign(begin.Challenge!)
            });
        }
        Assert.AreEqual(SecurityStateKind.Cooldown, _engine.GetStatus().State);

        SecurityResponse helloBegin = _engine.Begin(SecurityOperation.BeginUnlock);
        Assert.IsTrue(helloBegin.Success, "Windows Hello repair must remain available during a password cooldown.");
        SecurityResponse unlocked = _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = helloBegin.ChallengeId,
            Signature = Sign(helloBegin.Challenge!),
            UseWindowsHelloOnly = true,
            RememberDevice = true
        });

        Assert.IsTrue(unlocked.Success, unlocked.Message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unlocked.RememberedDeviceToken));
        WorkstationCredentialRecord record = _store.Load()!;
        Assert.IsTrue(record.WindowsHelloOnly);
        Assert.AreEqual(0, record.FailedAttempts);
        Assert.IsNull(record.CooldownUntilUtc);
    }

    [TestMethod]
    public void InvalidHelloCannotClearPersistedPasswordCooldown() {
        Enroll();
        for (int attempt = 0; attempt < 3; attempt++) {
            SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
            _engine.Unlock(new SecurityRequest {
                Operation = SecurityOperation.CompleteUnlock,
                ChallengeId = begin.ChallengeId,
                Password = "Wrong Password Value 92!",
                Signature = Sign(begin.Challenge!)
            });
        }
        using RSA differentHelloKey = RSA.Create(2048);
        SecurityResponse helloBegin = _engine.Begin(SecurityOperation.BeginUnlock);
        string wrongSignature = Convert.ToBase64String(differentHelloKey.SignData(
            Convert.FromBase64String(helloBegin.Challenge!), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        SecurityResponse rejected = _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = helloBegin.ChallengeId,
            Signature = wrongSignature,
            UseWindowsHelloOnly = true
        });

        Assert.IsFalse(rejected.Success);
        WorkstationCredentialRecord record = _store.Load()!;
        Assert.IsFalse(record.WindowsHelloOnly);
        Assert.AreEqual(3, record.FailedAttempts);
        Assert.IsTrue(record.CooldownUntilUtc > DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void HelloOnlyEnrollmentNeverRequiresTheWorkstationPasswordForUnlock() {
        Enroll(helloOnly: true);
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
        SecurityResponse unlocked = _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = begin.ChallengeId,
            Signature = Sign(begin.Challenge!)
        });
        Assert.IsTrue(unlocked.Success, unlocked.Message);
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
        using RSA replacementKey = RSA.Create(2048);
        try {
            var replacementStore = new SecurityStateStore(true, replacementRoot, replacementRegistry);
            var replacementEngine = new SecurityEngine(replacementStore, true);
            SecurityResponse begin = replacementEngine.Begin(SecurityOperation.BeginUnlock);
            string signature = Convert.ToBase64String(replacementKey.SignData(
                Convert.FromBase64String(begin.Challenge!), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            SecurityResponse recovered = replacementEngine.Recover(new SecurityRequest {
                Operation = SecurityOperation.Recover,
                ChallengeId = begin.ChallengeId,
                RecoveryKey = originalEnrollment.RecoveryKey,
                RecoveryBackup = originalEnrollment.RecoveryBackup,
                NewPassword = "Replacement Quartz Harbor Lantern 73!",
                PublicKey = Convert.ToBase64String(replacementKey.ExportSubjectPublicKeyInfo()),
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

    [TestMethod]
    public void RememberedTokenSurvivesRestartAndDoesNotSlide() {
        SecurityResponse enrolled = Enroll(rememberDevice: true);
        Assert.IsFalse(string.IsNullOrWhiteSpace(enrolled.RememberedDeviceToken));
        DateTimeOffset expectedExpiration = enrolled.RememberedDeviceExpiresUtc!.Value;
        Assert.IsTrue(expectedExpiration > DateTimeOffset.UtcNow.AddDays(29.9));

        var restarted = new SecurityEngine(_store, true, _network);
        SecurityResponse remembered = restarted.RememberedUnlock(enrolled.RememberedDeviceToken);
        Assert.IsTrue(remembered.Success, remembered.Message);
        Assert.AreEqual(expectedExpiration, remembered.RememberedDeviceExpiresUtc);
        Assert.IsTrue(restarted.ConsumeGrant(remembered.UnlockGrant));
        Assert.IsFalse(restarted.ConsumeGrant(remembered.UnlockGrant), "The startup grant must remain single-use.");
    }

    [TestMethod]
    public void RememberedTokenInvalidatesForTokenOrNetworkChanges() {
        SecurityResponse enrolled = Enroll(rememberDevice: true);
        SecurityResponse altered = _engine.RememberedUnlock(enrolled.RememberedDeviceToken + "altered");
        Assert.IsFalse(altered.Success);
        Assert.IsNull(_store.LoadRememberedDevice(), "A rejected token must be deleted broker-side.");

        enrolled = UnlockRemembered();
        _network.Result = new(true, "203.0.113.11", "192.168.1.20");
        Assert.IsFalse(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);

        enrolled = UnlockRemembered();
        _network.Result = new(true, "203.0.113.10", "192.168.1.21");
        Assert.IsFalse(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);
    }

    [TestMethod]
    public void RememberedTokenUsesPublicIpCacheForOnlyOneHour() {
        SecurityResponse enrolled = Enroll(rememberDevice: true);
        _network.Result = new(false, null, "192.168.1.20", "offline");
        Assert.IsTrue(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);

        RememberedDeviceRecord record = _store.LoadRememberedDevice()!;
        record.LastPublicIpVerificationUtc = DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(-1);
        _store.SaveRememberedDevice(record);
        Assert.IsFalse(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);
    }

    [TestMethod]
    public void PasswordChangeAndExpirationInvalidateRememberedToken() {
        SecurityResponse enrolled = Enroll(rememberDevice: true);
        RememberedDeviceRecord remembered = _store.LoadRememberedDevice()!;
        remembered.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        _store.SaveRememberedDevice(remembered);
        Assert.IsFalse(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);

        enrolled = UnlockRemembered();
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
        SecurityResponse changed = _engine.ChangePassword(new SecurityRequest {
            Operation = SecurityOperation.ChangePassword,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            NewPassword = "Copper Meadow Lantern Harbor 84!",
            Signature = Sign(begin.Challenge!)
        });
        Assert.IsTrue(changed.Success, changed.Message);
        Assert.IsNull(_store.LoadRememberedDevice());
        Assert.IsFalse(_engine.RememberedUnlock(enrolled.RememberedDeviceToken).Success);
    }

    [TestMethod]
    public void ClientRememberedTokenIsDpapiProtected() {
        string path = Path.Combine(_root, "client-token.bin");
        var tokenStore = new RememberedDeviceTokenStore(path);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        tokenStore.Save(token, DateTimeOffset.UtcNow.AddDays(30));
        Assert.IsFalse(File.ReadAllText(path).Contains(token, StringComparison.Ordinal));
        Assert.AreEqual(token, tokenStore.Load()!.Token);
    }

    private SecurityResponse Enroll(bool rememberDevice = false, bool helloOnly = false) {
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginEnroll);
        string publicKey = Convert.ToBase64String(_helloKey.ExportSubjectPublicKeyInfo());
        return _engine.Enroll(new SecurityRequest {
            Operation = SecurityOperation.Enroll,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            PublicKey = publicKey,
            Signature = Sign(begin.Challenge!),
            Attestation = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            RememberDevice = rememberDevice,
            UseWindowsHelloOnly = helloOnly
        });
    }

    private SecurityResponse UnlockRemembered() {
        if (!_store.StateExists)
            Enroll();
        SecurityResponse begin = _engine.Begin(SecurityOperation.BeginUnlock);
        return _engine.Unlock(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            ChallengeId = begin.ChallengeId,
            Password = StrongPassword,
            Signature = Sign(begin.Challenge!),
            RememberDevice = true
        });
    }

    private string Sign(string challenge) {
        byte[] signature = _helloKey.SignData(
            Convert.FromBase64String(challenge), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    private const string StrongPassword = "Correct Horse Battery Staple 92!";

    private sealed class FakeNetworkBindingProvider : INetworkBindingProvider {
        public FakeNetworkBindingProvider(string publicIp, string localIp) =>
            Result = new(true, publicIp, localIp);

        public NetworkBindingResult Result { get; set; }

        public Task<NetworkBindingResult> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }
}
