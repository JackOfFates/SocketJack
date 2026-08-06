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
        Assert.AreEqual("hidden", filter.TakeReasoningDelta());
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
        Assert.AreEqual("hidden", filter.TakeReasoningDelta());
    }

    [TestMethod]
    public void Flush_DropsUnclosedHiddenReasoning()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        Assert.AreEqual("", filter.Accept("<think>still hidden"));
        Assert.AreEqual("", filter.Flush());
        Assert.AreEqual("still hidden", filter.TakeReasoningDelta());
    }

    [TestMethod]
    public void Accept_ConsumesSplitEndOfThoughtMarkerWithoutLeakingIt()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        Assert.AreEqual("", filter.Accept("provided above.\n</end_of_"));
        Assert.AreEqual("\nFinal answer", filter.Accept("thought>\nFinal answer"));
        Assert.AreEqual("provided above.\n", filter.TakeReasoningDelta());
        Assert.AreEqual("", filter.Flush());
    }

    [TestMethod]
    public void Accept_ConsumesTokenFormEndOfThoughtMarker()
    {
        var filter = new LlmHiddenReasoningStreamFilter();

        Assert.AreEqual("Answer", filter.Accept("hidden<|end_of_thought|>Answer"));
        Assert.AreEqual("hidden", filter.TakeReasoningDelta());
    }
}
