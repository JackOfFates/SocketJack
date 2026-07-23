using Microsoft.VisualStudio.TestTools.UnitTesting;

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
}
