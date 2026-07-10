using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobilePairingSecurityTests
{
    [TestMethod]
    public void HashSecret_IsStableAndDoesNotStorePlaintext()
    {
        string first = LmVsProxy.HashSecret("123456");
        string second = LmVsProxy.HashSecret("123456");

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Length);
        Assert.IsFalse(first.Contains("123456", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FixedEquals_RejectsDifferentOrMalformedValues()
    {
        string expected = LmVsProxy.HashSecret("pairing-token");

        Assert.IsTrue(LmVsProxy.FixedEquals(expected, expected));
        Assert.IsFalse(LmVsProxy.FixedEquals(expected, LmVsProxy.HashSecret("other-token")));
        Assert.IsFalse(LmVsProxy.FixedEquals(expected, "short"));
    }
}
