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
    public void DetermineFinishReason_TreatsGuardedHiddenReasoningAsLength()
    {
        string finishReason = LlamaSharpBackend.DetermineFinishReason(
            stoppedByGuard: true,
            generatedTokens: 32,
            maxTokens: 512,
            completionTokenEstimate: 32,
            stoppedInsideHiddenReasoning: true);

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
    public void ShouldSuppressReasoningByDefault_DoesNotHideQwenThinking()
    {
        var backend = new LlamaSharpBackend(
            "Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF",
            "C:\\Models\\Qwen3.5-2B.Q5_K_S.gguf",
            new LlmLoadConfig());

        Assert.IsFalse(backend.ShouldSuppressReasoningByDefault());
    }

    [TestMethod]
    public void ContainsNoThinkControl_DetectsSlashNoThink()
    {
        Assert.IsTrue(LlamaSharpBackend.ContainsNoThinkControl("Answer briefly. /no_think"));
        Assert.IsTrue(LlamaSharpBackend.ContainsNoThinkControl("Answer briefly. /no-think"));
    }

    [TestMethod]
    public void ResolveMultimodalProjectorPath_FindsSiblingMmproj()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            string modelPath = Path.Combine(root, "VisionModel-Q4_K_M.gguf");
            string projectorPath = Path.Combine(root, "mmproj-BF16.gguf");
            File.WriteAllBytes(modelPath, [1]);
            File.WriteAllBytes(projectorPath, [2]);

            Assert.AreEqual(projectorPath, LlamaSharpBackend.ResolveMultimodalProjectorPath(modelPath));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void TryDecodeImageDataUrl_DecodesBase64ImageBytes()
    {
        bool decoded = LlamaSharpBackend.TryDecodeImageDataUrl(
            "data:image/png;base64,iVBORw0KGgo=",
            out byte[] bytes);

        Assert.IsTrue(decoded);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
            bytes);
    }

    [TestMethod]
    public void ShouldUseGpuForMultimodal_TreatsNegativeOneAsAllGpuLayers()
    {
        Assert.IsTrue(LlamaSharpBackend.ShouldUseGpuForMultimodal(new LlmLoadConfig
        {
            Backend = LlmBackendKind.Cuda12,
            GpuLayerCount = -1
        }));
        Assert.IsFalse(LlamaSharpBackend.ShouldUseGpuForMultimodal(new LlmLoadConfig
        {
            Backend = LlmBackendKind.Cpu,
            GpuLayerCount = 0
        }));
    }

    [TestMethod]
    public void ResolveMultimodalImageMaxTokens_LeavesRoomForPromptAndCompletion()
    {
        Assert.AreEqual(1024, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(2048));
        Assert.AreEqual(2048, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(4096));
        Assert.AreEqual(2048, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(8192));
        Assert.AreEqual(256, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(256));
        Assert.AreEqual(512, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(2048, 2));
        Assert.AreEqual(256, LlamaSharpBackend.ResolveMultimodalImageMaxTokens(2048, 4));
    }
}
