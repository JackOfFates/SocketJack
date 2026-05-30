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

    private static string CleanVisibleContent(string content)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("CleanAssistantVisibleContent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "CleanAssistantVisibleContent was not found.");
        return (string)method!.Invoke(proxy, new object[] { content })!;
    }
}
