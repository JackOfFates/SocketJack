using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.SocketChat;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.SocketChat.Tests;

[TestClass]
public class SocketChatTests
{
    [TestMethod]
    public void CryptoRoundTripRejectsTampering()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string encrypted = SocketChatCrypto.Encrypt(key, Encoding.UTF8.GetBytes("private hello"));
        Assert.AreEqual("private hello", Encoding.UTF8.GetString(SocketChatCrypto.Decrypt(key, encrypted)));
        byte[] tampered = Convert.FromBase64String(encrypted); tampered[20] ^= 0x40;
        Assert.ThrowsException<CryptographicException>(() => SocketChatCrypto.Decrypt(key, Convert.ToBase64String(tampered)));
    }

    [TestMethod]
    public void IdentitySignsAndCreatesPairingCode()
    {
        using var identity = SocketChatDeviceIdentity.Create();
        string signature = identity.Sign("lease");
        Assert.IsTrue(SocketChatDeviceIdentity.Verify(identity.PublicKey, "lease", signature));
        Assert.IsFalse(SocketChatDeviceIdentity.Verify(identity.PublicKey, "changed", signature));
        Assert.AreEqual(6, SocketChatDeviceIdentity.CreatePairingCode().Length);
    }

    [TestMethod]
    public void HostElectionPrefersReachability()
    {
        var now = DateTimeOffset.UtcNow;
        var winner = SocketChatHostElection.SelectBest(new[] {
            new SocketChatHostCandidate { Fingerprint="BLOCKED", UpstreamMbps=1000, ObservedUtc=now },
            new SocketChatHostCandidate { Fingerprint="TUNNEL", TunnelReachable=true, UpstreamMbps=50, ObservedUtc=now },
            new SocketChatHostCandidate { Fingerprint="DIRECT", PubliclyReachable=true, UpstreamMbps=10, ObservedUtc=now }
        }, now, TimeSpan.FromMinutes(1));
        Assert.AreEqual("DIRECT", winner.Fingerprint);
    }

    [TestMethod]
    public void ManagedDatabasePersistsAndDeduplicates()
    {
        string root = Path.Combine(Path.GetTempPath(), "socketchat-tests-" + Guid.NewGuid().ToString("N"));
        try {
            var record = new SocketChatEnvelope { LobbyId="lobby", SenderFingerprint="sender", Sequence=1, CipherText="cipher" };
            var first = new SocketChatManagedDatabase(root); first.Append(record); first.Append(record);
            var second = new SocketChatManagedDatabase(root);
            Assert.AreEqual(1, second.Read("lobby").Count);
            Assert.AreEqual("cipher", second.Read("lobby").Single().CipherText);
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
