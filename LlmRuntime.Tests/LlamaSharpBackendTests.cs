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

    [TestMethod]
    public void CreateSamplingPipeline_UsesGreedyForZeroTemperature()
    {
        var request = new LlmChatRequest
        {
            Temperature = 0
        };

        Assert.IsInstanceOfType(LlamaSharpBackend.CreateSamplingPipeline(request), typeof(LLama.Sampling.GreedySamplingPipeline));
    }

    [TestMethod]
    public void ShouldSuppressReasoningByDefault_DetectsQwenReasoningDistilledModels()
    {
        var backend = new LlamaSharpBackend(
            "Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF",
            "C:\\Models\\Qwen3.5-2B.Q5_K_S.gguf",
            new LlmLoadConfig());

        Assert.IsTrue(backend.ShouldSuppressReasoningByDefault());
    }

    [TestMethod]
    public void ContainsNoThinkControl_DetectsSlashNoThink()
    {
        Assert.IsTrue(LlamaSharpBackend.ContainsNoThinkControl("Answer briefly. /no_think"));
        Assert.IsTrue(LlamaSharpBackend.ContainsNoThinkControl("Answer briefly. /no-think"));
    }
}
