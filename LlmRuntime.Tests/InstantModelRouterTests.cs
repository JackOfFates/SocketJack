using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class InstantModelRouterTests
{
    [TestMethod]
    public void Route_PrefersLoadedFastModelForSimplePrompt()
    {
        var router = new InstantModelRouter();
        var small = Model("small-2B-instruct", 2L << 30, loaded: true, tokensPerSecond: 80);
        var large = Model("large-14B-instruct", 8L << 30, loaded: false);

        ModelRouteDecision result = router.Route([large, small], new ModelRouteRequest("hello", Reasoning: RouterReasoningLevel.Minimal, AvailableVramBytes: 24L << 30));

        Assert.AreEqual(small.Key, result.SelectedModel);
        Assert.AreEqual("best_loaded_match", result.ReasonCode);
    }

    [TestMethod]
    public void Route_HighReasoningCanSelectStrongerLoadableModel()
    {
        var router = new InstantModelRouter();
        var small = Model("small-2B-instruct", 2L << 30, loaded: true, tokensPerSecond: 25);
        var large = Model("large-14B-reason-instruct", 8L << 30, loaded: false);

        ModelRouteDecision result = router.Route([small, large], new ModelRouteRequest("Analyze this architecture and compare trade-offs", Reasoning: RouterReasoningLevel.High, AvailableVramBytes: 16L << 30));

        Assert.AreEqual(large.Key, result.SelectedModel);
        Assert.IsTrue(result.RequiresLoading);
    }

    [TestMethod]
    public void Route_RejectsUnloadedCandidateBeyondOneHundredFiftyPercentVram()
    {
        var router = new InstantModelRouter();
        var loaded = Model("loaded-3B-instruct", 2L << 30, loaded: true);
        var oversized = Model("oversized-30B-reason-instruct", 20L << 30, loaded: false);

        ModelRouteDecision result = router.Route([oversized, loaded], new ModelRouteRequest("Prove a difficult theorem", Reasoning: RouterReasoningLevel.High, AvailableVramBytes: 8L << 30));

        Assert.AreEqual(loaded.Key, result.SelectedModel);
        Assert.AreEqual("vram_limit", result.Candidates.Single(candidate => candidate.Model == oversized.Key).RejectionReason);
    }

    [TestMethod]
    public void Route_AllowsKnownGoodNineBClaudeModelWithHybridMemoryHeadroom()
    {
        var router = new InstantModelRouter();
        var claude = Model("Qwythos-9B-Claude-Mythos-5-1M-Q6_K", 7_359_259_296, loaded: false);

        ModelRouteDecision result = router.Route([claude], new ModelRouteRequest(
            "Assess a completed Dream and return JSON.",
            Reasoning: RouterReasoningLevel.Minimal,
            RequiredContextTokens: 2048,
            AvailableVramBytes: 6_009_389_056));

        Assert.AreEqual(claude.Key, result.SelectedModel);
        Assert.IsTrue(result.Candidates.Single().FitsVram);
    }

    [TestMethod]
    public void Route_DoesNotLoadWhenVramTelemetryIsUnknown()
    {
        var router = new InstantModelRouter();
        var loaded = Model("loaded-2B-instruct", 2L << 30, loaded: true);
        var unloaded = Model("unloaded-14B-reason-instruct", 8L << 30, loaded: false);

        ModelRouteDecision result = router.Route([unloaded, loaded], new ModelRouteRequest("Analyze a complex proof", Reasoning: RouterReasoningLevel.High));

        Assert.AreEqual(loaded.Key, result.SelectedModel);
        Assert.AreEqual("vram_unknown", result.Candidates.Single(candidate => candidate.Model == unloaded.Key).RejectionReason);
    }

    [TestMethod]
    public void Route_EnforcesVisionCapabilityBeforeScoring()
    {
        var router = new InstantModelRouter();
        var text = Model("fast-7B-instruct", 4L << 30, loaded: true, tokensPerSecond: 100);
        var vision = Model("vision-7B-vlm-instruct", 4L << 30, loaded: true, tokensPerSecond: 20);

        ModelRouteDecision result = router.Route([text, vision], new ModelRouteRequest("Describe this image", RequiresVision: true, AvailableVramBytes: 12L << 30));

        Assert.AreEqual(vision.Key, result.SelectedModel);
        Assert.AreEqual("vision_required", result.Candidates.Single(candidate => candidate.Model == text.Key).RejectionReason);
    }

    [TestMethod]
    public void Route_ClassifiesCSharpParserAsCoding()
    {
        var router = new InstantModelRouter();
        ModelRouteDecision result = router.Route([Model("coder-3B-instruct", 2L << 30, loaded: true)], new ModelRouteRequest("write a C# parser"));
        Assert.AreEqual("coding", result.Classification);
        Assert.AreEqual(RouterReasoningLevel.Low, result.EffectiveReasoning);
    }

    [TestMethod]
    public void OptionalTools_DoNotRequireToolCapabilityButRequiredChoiceDoes()
    {
        using var optionalDocument = JsonDocument.Parse("""
        {"model":"auto","messages":[{"role":"system","content":"Reflect on saved messages."}],"tools":[{"type":"function","function":{"name":"internet_search"}}]}
        """);
        using var requiredDocument = JsonDocument.Parse("""
        {"model":"auto","messages":[{"role":"user","content":"Search now."}],"tools":[{"type":"function","function":{"name":"internet_search"}}],"tool_choice":"required"}
        """);

        Assert.IsFalse(LlmRuntimeHost.RequestRequiresToolCapableModel(LlmChatRequest.FromJson(optionalDocument.RootElement)));
        Assert.IsTrue(LlmRuntimeHost.RequestRequiresToolCapableModel(LlmChatRequest.FromJson(requiredDocument.RootElement)));
    }

    private static LlmModelInfo Model(string key, long size, bool loaded, double tokensPerSecond = 0) => new()
    {
        Key = key,
        DisplayName = key,
        FilePath = key + ".gguf",
        FileName = key + ".gguf",
        SizeBytes = size,
        Format = "gguf",
        Architecture = "llama",
        Type = "llm",
        LoadedInstances = loaded ? [new LoadedModelInstance { Id = key + "-instance", ModelKey = key, Health = "healthy", TokensPerSecond = tokensPerSecond }] : []
    };
}
