using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public class DirectMlRunnerProgramTests
{
    [TestMethod]
    public void DetermineFinishReason_TreatsUnclosedHiddenReasoningAsLength()
    {
        string finishReason = LlmRuntime.DirectMlRunner.LlamaRunnerEngine.DetermineFinishReason(
            stoppedByGuard: false,
            generatedTokens: 14,
            maxTokens: 16,
            completionTokenEstimate: 14,
            stoppedInsideHiddenReasoning: true);

        Assert.AreEqual("length", finishReason);
    }

    [TestMethod]
    public void DetermineFinishReason_PreservesGuardStop()
    {
        string finishReason = LlmRuntime.DirectMlRunner.LlamaRunnerEngine.DetermineFinishReason(
            stoppedByGuard: true,
            generatedTokens: 16,
            maxTokens: 16,
            completionTokenEstimate: 16,
            stoppedInsideHiddenReasoning: true);

        Assert.AreEqual("stop", finishReason);
    }

    [TestMethod]
    public void EndsInsideHiddenReasoning_DetectsIncompleteThinkBlock()
    {
        Assert.IsTrue(LlmRuntime.DirectMlRunner.LlamaRunnerEngine.EndsInsideHiddenReasoning("<think>still reasoning"));
        Assert.IsFalse(LlmRuntime.DirectMlRunner.LlamaRunnerEngine.EndsInsideHiddenReasoning("<think>hidden</think>visible"));
    }

    [TestMethod]
    public void IsHiddenReasoningOnly_DetectsClosedHiddenBlockWithoutVisibleAnswer()
    {
        Assert.IsTrue(LlmRuntime.DirectMlRunner.LlamaRunnerEngine.IsHiddenReasoningOnly("\n<think>hidden</think>\n"));
        Assert.IsFalse(LlmRuntime.DirectMlRunner.LlamaRunnerEngine.IsHiddenReasoningOnly("<think>hidden</think>visible"));
    }
}
