using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ChatUiDisplayNormalizationTests
{
    [TestMethod]
    public void CleanAssistantVisibleContent_RemovesSerializedControlEvents()
    {
        string cleaned = CleanVisibleContent("""
        Before
        {"type":"toolCall","id":"call_1","name":"internet_search","summary":"Looking up current external information."}
        After
        """);

        StringAssert.Contains(cleaned, "Before");
        StringAssert.Contains(cleaned, "After");
        Assert.IsFalse(cleaned.Contains("\"type\":\"toolCall\""), cleaned);
        Assert.IsFalse(cleaned.Contains("Looking up current external information."), cleaned);
    }

    [TestMethod]
    public void CleanAssistantVisibleContent_RepairsCommonRuntimeSpacingArtifacts()
    {
        string cleaned = CleanVisibleContent("**EC U Part Numbers - 2 0 0 8 Audi A3 3 . 2 L VR 6 D SG**");

        StringAssert.Contains(cleaned, "ECU");
        StringAssert.Contains(cleaned, "2008");
        StringAssert.Contains(cleaned, "3.2L");
        StringAssert.Contains(cleaned, "VR6");
        StringAssert.Contains(cleaned, "DSG");
    }

    [TestMethod]
    public void CleanAssistantVisibleContent_RemovesDanglingToolParameterLeak()
    {
        string cleaned = CleanVisibleContent("""
        I can open that search.

        <parameter>{"url":"https://duckduckgo.com/?q=2001+audi+s4+black+lowered+BBS"
        """);

        StringAssert.Contains(cleaned, "I can open that search.");
        Assert.IsFalse(cleaned.Contains("<parameter>", StringComparison.OrdinalIgnoreCase), cleaned);
        Assert.IsFalse(cleaned.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase), cleaned);
    }

    [TestMethod]
    public void CleanAssistantVisibleContent_RemovesModelMetadataAndDanglingThinkingFragment()
    {
        string cleaned = CleanVisibleContent("""
        OK_1
        <model_name>Qwen3.5-9B-Claude-4.6-Opus-Reasoning-Distilled-v2-GGUF</model_name>
        <model_id>qwen3.5-9b-claude-4
        ing
        """);

        Assert.AreEqual("OK_1", cleaned);
        Assert.IsFalse(cleaned.Contains("model_name", StringComparison.OrdinalIgnoreCase), cleaned);
        Assert.IsFalse(cleaned.Contains("model_id", StringComparison.OrdinalIgnoreCase), cleaned);
        Assert.IsFalse(cleaned.EndsWith("ing", StringComparison.OrdinalIgnoreCase), cleaned);
    }

    private static string CleanVisibleContent(string content)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("CleanAssistantVisibleContent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "CleanAssistantVisibleContent was not found.");
        return (string)method!.Invoke(proxy, new object[] { content })!;
    }
}
