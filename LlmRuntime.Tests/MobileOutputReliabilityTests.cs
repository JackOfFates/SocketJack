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
}
