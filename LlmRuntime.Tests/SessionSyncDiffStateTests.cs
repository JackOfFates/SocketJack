using LlmRuntime.VisualStudio2026;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class SessionSyncDiffStateTests
{
    [TestMethod]
    public void RemoteOnlyFile_IsPendingPullCandidateNotDeleteCandidate()
    {
        bool remoteOnly = SessionSyncDiffState.IsRemoteOnly(
            DateTimeOffset.MinValue,
            0,
            "",
            DateTimeOffset.UtcNow,
            12,
            "");

        Assert.IsTrue(remoteOnly);
    }

    [TestMethod]
    public void LocalBaselinePreventsRemoteOnlyClassification()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool remoteOnly = SessionSyncDiffState.IsRemoteOnly(
            now.AddMinutes(-2),
            12,
            "abc",
            now,
            12,
            "abc");

        Assert.IsFalse(remoteOnly);
    }

    [TestMethod]
    public void KnownHashesEqualRequiresBothSides()
    {
        Assert.IsTrue(SessionSyncDiffState.KnownHashesEqual(" ABC ", "abc"));
        Assert.IsFalse(SessionSyncDiffState.KnownHashesEqual("", "abc"));
        Assert.IsFalse(SessionSyncDiffState.KnownHashesEqual("abc", ""));
    }

    [TestMethod]
    public void RemoteMetadataChangedIgnoresSubsecondClockSkew()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.IsFalse(SessionSyncDiffState.RemoteMetadataChanged(now, now.AddMilliseconds(500), 12, 12));
        Assert.IsTrue(SessionSyncDiffState.RemoteMetadataChanged(now, now.AddSeconds(2), 12, 12));
        Assert.IsTrue(SessionSyncDiffState.RemoteMetadataChanged(now, now, 12, 13));
    }
}
