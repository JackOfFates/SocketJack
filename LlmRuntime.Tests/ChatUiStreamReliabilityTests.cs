using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ChatUiStreamReliabilityTests
{
    [TestMethod]
    public void ExtractNovelChatUiStreamDelta_ConvertsCumulativeFrameToSuffix()
    {
        Assert.AreEqual(" world", ExtractNovel("Hello", "Hello world"));
    }

    [TestMethod]
    public void ExtractNovelChatUiStreamDelta_DropsLongReplayedFrame()
    {
        const string answer = "This is a sufficiently long completed Mythos response.";
        Assert.AreEqual("", ExtractNovel(answer, answer));
    }

    [TestMethod]
    public void ExtractNovelChatUiStreamDelta_TrimsLongSuffixPrefixOverlap()
    {
        string existing = "Answer prefix and a long shared transition into the next frame";
        string incoming = "shared transition into the next frame plus new text";
        Assert.AreEqual(" plus new text", ExtractNovel(existing, incoming));
    }

    [TestMethod]
    public void ExtractNovelChatUiStreamDelta_PreservesShortIntentionalRepetition()
    {
        Assert.AreEqual("ha", ExtractNovel("ha", "ha"));
    }

    [TestMethod]
    public void PlainChatPrompt_BlocksUnrelatedCodeAndFtpContextBleed()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "BuildPlainChatModeSystemHint",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        string prompt = (string)(method.Invoke(proxy, null) ?? "");

        StringAssert.Contains(prompt, "do not surface unrelated code");
        StringAssert.Contains(prompt, "FTP configuration");
        StringAssert.Contains(prompt, "Never expose passwords, tokens, or connection credentials");
    }

    private static string ExtractNovel(string existing, string incoming)
    {
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "ExtractNovelChatUiStreamDelta",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)(method.Invoke(null, new object[] { existing, incoming }) ?? "");
    }
}
