using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmHiddenReasoningStreamFilterTests
{
    [TestMethod]
    public void Accept_SuppressesCompleteHiddenReasoningBlock()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        string first = filter.Accept("<think>hidden</think>\n\n");
        string second = filter.Accept("4");

        Assert.AreEqual("\n\n", first);
        Assert.AreEqual("4", second);
        Assert.AreEqual("", filter.Flush());
    }

    [TestMethod]
    public void Accept_SuppressesHiddenReasoningWhenTagsAreSplit()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        string first = filter.Accept("<thi");
        string second = filter.Accept("nk>hidden</thi");
        string third = filter.Accept("nk>\n\n4");

        Assert.AreEqual("", first);
        Assert.AreEqual("", second);
        Assert.AreEqual("\n\n4", third);
    }

    [TestMethod]
    public void Flush_DropsUnclosedHiddenReasoning()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        Assert.AreEqual("", filter.Accept("<think>still hidden"));
        Assert.AreEqual("", filter.Flush());
    }
}
