using JackLLM.Mobile.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class MobileOutputReliabilityTests
{
    [TestMethod]
    public void MergeStreamDelta_ReplacesCumulativeClaudeFrame()
    {
        string first = "Here is the requested data:\n| Item | Value |\n| A | 10 |";
        string cumulative = first + "\n| B | 20 |";

        Assert.AreEqual(cumulative, MobileOutputReliability.MergeStreamDelta(first, cumulative));
    }

    [TestMethod]
    public void MergeStreamDelta_IgnoresReplayedLongChunk()
    {
        string chunk = "{\"items\":[{\"name\":\"alpha\",\"value\":10},{\"name\":\"beta\",\"value\":20}]}";

        Assert.AreEqual(chunk, MobileOutputReliability.MergeStreamDelta(chunk, chunk));
    }

    [TestMethod]
    public void CollapseExactAdjacentBlocks_RemovesRepeatedMarkdownTable()
    {
        string table = "| Name | Cost | Status |\n|---|---:|---|\n| Alpha | $120 | Ready |\n| Beta | $240 | Waiting |";
        string repeated = "Summary:\n" + table + "\n\n" + table + "\nEnd.";

        Assert.AreEqual("Summary:\n" + table + "\n\nEnd.", MobileOutputReliability.CollapseExactAdjacentBlocks(repeated));
    }

    [TestMethod]
    public void MergeStreamDelta_PreservesShortIntentionalRepetition()
    {
        Assert.AreEqual("ha ha ha", MobileOutputReliability.MergeStreamDelta("ha ha ", "ha"));
        Assert.AreEqual("yesyes", MobileOutputReliability.MergeStreamDelta("yes", "yes"));
    }

    [TestMethod]
    public void StreamAccumulator_ShowsThoughtsBeforeIncrementalAnswer()
    {
        var stream = new MobileStreamTextAccumulator();

        stream.Append("First thought. ", reasoning: true);
        Assert.AreEqual("First thought. ", stream.Reasoning);
        Assert.AreEqual("", stream.Content);

        stream.Append("Answer ", reasoning: false);
        stream.Append("arrives live.", reasoning: false);
        Assert.AreEqual("Answer arrives live.", stream.Content);
        Assert.AreEqual("First thought. ", stream.Reasoning);
    }

    [TestMethod]
    public void StreamAccumulator_HandlesReasoningTagsSplitAcrossFrames()
    {
        var stream = new MobileStreamTextAccumulator();

        stream.Append("<thi", reasoning: false);
        Assert.AreEqual("", stream.Content);
        stream.Append("nk>Checking facts", reasoning: false);
        Assert.AreEqual("Checking facts", stream.Reasoning);
        Assert.AreEqual("", stream.Content);
        stream.Append("</thi", reasoning: false);
        stream.Append("nk>Final ", reasoning: false);
        stream.Append("answer", reasoning: false);

        Assert.AreEqual("Checking facts", stream.Reasoning);
        Assert.AreEqual("Final answer", stream.Content);
    }

    [TestMethod]
    public void StreamAccumulator_ReplacesCumulativeAnswerFrames()
    {
        var stream = new MobileStreamTextAccumulator();
        const string first = "This is a sufficiently long streamed answer prefix.";
        const string cumulative = first + " It now includes the next sentence.";

        stream.Append(first, reasoning: false);
        stream.Append(cumulative, reasoning: false);

        Assert.AreEqual(cumulative, stream.Content);
    }
}
