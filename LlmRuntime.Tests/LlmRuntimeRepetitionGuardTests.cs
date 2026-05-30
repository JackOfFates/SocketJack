using System.Text;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmRuntimeRepetitionGuardTests
{
    [TestMethod]
    public void Accept_AllowsNormalStructuredComparison()
    {
        const string text = """
        | GPU | VRAM | Local model fit |
        | --- | --- | --- |
        | NVIDIA RTX 4090 | 24 GB | Strong for 7B and 14B quantized chat models. |
        | NVIDIA RTX 6000 Ada | 48 GB | Better for larger context windows and bigger local models. |
        | NVIDIA RTX 5090 | 32 GB | Fast generation with less room than workstation cards. |
        """;
        var guard = new LlmRuntimeRepetitionGuard();
        var output = new StringBuilder();

        foreach (string chunk in Chunk(text, 19))
        {
            LlmRuntimeRepetitionGuardDecision decision = guard.Accept(chunk);
            Assert.IsFalse(decision.ShouldStop);
            output.Append(decision.Text);
        }

        Assert.AreEqual(text, output.ToString());
    }

    [TestMethod]
    public void Accept_StopsRepeatedParagraphLoop()
    {
        const string paragraph = "The NVIDIA RTX 6000 Ada remains the best workstation option because its larger memory buffer keeps the local model from falling back to slower paging behavior.";
        var guard = new LlmRuntimeRepetitionGuard();
        var output = new StringBuilder();
        bool stopped = false;

        for (int i = 0; i < 12; i++)
        {
            LlmRuntimeRepetitionGuardDecision decision = guard.Accept(paragraph + Environment.NewLine);
            output.Append(decision.Text);
            if (decision.ShouldStop)
            {
                stopped = true;
                break;
            }
        }

        Assert.IsTrue(stopped);
        Assert.IsTrue(CountOccurrences(output.ToString(), "RTX 6000 Ada") < 12);
    }

    [TestMethod]
    public void TrimRepeatingTail_RemovesRepeatedTableRowRunaway()
    {
        const string row = "| NVIDIA GeForce GTX 1660 Super | 6 GB | Not enough room for this model without aggressive quantization and slow fallback. |";
        string runaway = "Summary before the table." + Environment.NewLine + string.Concat(Enumerable.Repeat(row + Environment.NewLine, 16)) + "This should be cut.";

        string trimmed = LlmRuntimeRepetitionGuard.TrimRepeatingTail(runaway);

        Assert.IsTrue(trimmed.Length < runaway.Length);
        Assert.IsFalse(trimmed.Contains("This should be cut.", StringComparison.Ordinal));
        Assert.IsTrue(CountOccurrences(trimmed, "GTX 1660 Super") < 16);
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (int index = 0; index < text.Length; index += size)
            yield return text.Substring(index, Math.Min(size, text.Length - index));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
