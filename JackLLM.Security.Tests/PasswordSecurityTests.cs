using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace JackLLM.Security.Tests;

[TestClass]
public sealed class PasswordSecurityTests {
    [DataTestMethod]
    [DataRow("short")]
    [DataRow("password-password-password")]
    [DataRow("aaaaaaaaaaaaaaaaAAAA1111")]
    [DataRow("Abcdef123456!Abcdef")]
    public void WeakPasswordsAreRejected(string password) {
        Assert.IsNotNull(PasswordSecurity.Validate(password));
    }

    [DataTestMethod]
    [DataRow("Correct Horse Battery Staple 92!")]
    [DataRow("Violet-River-Quartz-Engine-47")]
    public void StrongPassphrasesAreAccepted(string password) {
        Assert.IsNull(PasswordSecurity.Validate(password));
    }

    [TestMethod]
    public void Argon2VerifierUsesSaltAndPepper() {
        byte[] salt = PasswordSecurity.CreateSalt();
        byte[] pepper = PasswordSecurity.CreatePepper();
        byte[] verifier = PasswordSecurity.Derive("Correct Horse Battery Staple 92!", salt, pepper);
        Assert.IsTrue(PasswordSecurity.Verify("Correct Horse Battery Staple 92!", salt, pepper, verifier));
        Assert.IsFalse(PasswordSecurity.Verify("Correct Horse Battery Staple 93!", salt, pepper, verifier));
        byte[] differentPepper = PasswordSecurity.CreatePepper();
        Assert.IsFalse(PasswordSecurity.Verify("Correct Horse Battery Staple 92!", salt, differentPepper, verifier));
    }

    [TestMethod]
    public void CooldownScheduleMatchesPolicy() {
        Assert.AreEqual(TimeSpan.Zero, PasswordSecurity.GetCooldown(2));
        Assert.AreEqual(TimeSpan.FromSeconds(10), PasswordSecurity.GetCooldown(3));
        Assert.AreEqual(TimeSpan.FromMinutes(1), PasswordSecurity.GetCooldown(5));
        Assert.AreEqual(TimeSpan.FromMinutes(15), PasswordSecurity.GetCooldown(7));
        Assert.AreEqual(TimeSpan.FromHours(24), PasswordSecurity.GetCooldown(10));
    }

    [TestMethod]
    public void RecoveryKeysAreRandomAndNormalized() {
        string first = PasswordSecurity.CreateRecoveryKey();
        string second = PasswordSecurity.CreateRecoveryKey();
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(48, PasswordSecurity.NormalizeRecoveryKey(first).Length);
        Assert.AreEqual(PasswordSecurity.NormalizeRecoveryKey(first), PasswordSecurity.NormalizeRecoveryKey(first.ToLowerInvariant().Replace("-", " ")));
    }
}
