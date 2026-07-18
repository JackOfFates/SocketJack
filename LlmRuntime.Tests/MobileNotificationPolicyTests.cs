using JackLLM.Mobile.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobileNotificationPolicyTests
{
    [TestMethod]
    public void VisibleAnswerMilestone_FiresExactlyOnce()
    {
        var policy = new MobileResponseMilestonePolicy();

        Assert.IsFalse(policy.TryReachVisibleAnswer(false));
        Assert.IsTrue(policy.TryReachVisibleAnswer(true));
        Assert.IsFalse(policy.TryReachVisibleAnswer(true));
    }

    [TestMethod]
    public void VisibleAnswerMilestone_Reset_AllowsNextMobilePrompt()
    {
        var policy = new MobileResponseMilestonePolicy();
        Assert.IsTrue(policy.TryReachVisibleAnswer(true));

        policy.Reset();

        Assert.IsTrue(policy.TryReachVisibleAnswer(true));
    }

    [TestMethod]
    public void UnreadText_UsesExactAggregateWording()
    {
        int unread = MobileNotificationText.IncrementUnread(0);

        Assert.AreEqual(1, unread);
        Assert.AreEqual("JackLLM has 1 new notifications.", MobileNotificationText.FormatUnread(unread));
        Assert.AreEqual(2, MobileNotificationText.IncrementUnread(unread));
    }
}
