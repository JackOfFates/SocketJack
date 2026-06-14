using Microsoft.VisualStudio.TestTools.UnitTesting;
using LLama.Common;

namespace LlmRuntime.Tests;

[TestClass]
public class LlamaSharpBackendTests
{
    [TestMethod]
    public void DetermineFinishReason_TreatsHiddenReasoningOnlyAsLength()
    {
        string finishReason = LlamaSharpBackend.DetermineFinishReason(
            stoppedByGuard: false,
            generatedTokens: 14,
            maxTokens: 16,
            completionTokenEstimate: 14,
            stoppedInsideHiddenReasoning: LlamaSharpBackend.IsHiddenReasoningOnly("<think>hidden reasoning</think>\n"));

        Assert.AreEqual("length", finishReason);
    }

    [TestMethod]
    public void DetermineFinishReason_UsesGeneratedTokenLimit()
    {
        string finishReason = LlamaSharpBackend.DetermineFinishReason(
            stoppedByGuard: false,
            generatedTokens: 16,
            maxTokens: 16,
            completionTokenEstimate: 12,
            stoppedInsideHiddenReasoning: false);

        Assert.AreEqual("length", finishReason);
    }

    [TestMethod]
    public void CreateInferenceParams_UsesApplicationManagedContextOverflow()
    {
        var request = new LlmChatRequest
        {
            MaxTokens = 128,
            Stop = ["</s>"]
        };

        InferenceParams parameters = LlamaSharpBackend.CreateInferenceParams(request);

        Assert.AreEqual(ContextOverflowStrategy.ThrowException, parameters.OverflowStrategy);
    }
}
