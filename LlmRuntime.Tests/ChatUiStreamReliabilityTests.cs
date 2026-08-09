using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack;
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
        StringAssert.Contains(prompt, "dedicated reasoning channel");
    }

    [TestMethod]
    public void PlainChatReasoning_IsVisibleUnlessNoThinkWasRequested()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "ShouldSuppressChatUiReasoningForRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        Assert.IsFalse((bool)(method.Invoke(proxy, new object[] { "{\"service\":\"\",\"messages\":[{\"role\":\"user\",\"content\":\"Explain this\"}]}" }) ?? true));
        Assert.IsTrue((bool)(method.Invoke(proxy, new object[] { "{\"service\":\"\",\"messages\":[{\"role\":\"user\",\"content\":\"Explain this /no_think\"}]}" }) ?? false));
    }

    [TestMethod]
    public void NoVisibleAnswerDiagnostic_TriggersAnswerOnlyRecovery()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        const string diagnostic = "The model used the response budget without producing visible assistant text. Retry with a higher `max_tokens` value or choose a non-reasoning model.";

        MethodInfo? shouldContinue = typeof(LmVsProxy).GetMethod(
            "ShouldAutoContinueChatUiCompletion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(shouldContinue);
        object?[] continuationArgs = { "stop", diagnostic, "", 0, -1, "ip:127.0.0.1", null, null };
        Assert.IsTrue((bool)(shouldContinue.Invoke(proxy, continuationArgs) ?? false));

        MethodInfo? buildRequest = typeof(LmVsProxy).GetMethod(
            "BuildChatUiAutoContinuationRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(buildRequest);
        const string request = "{\"model\":\"reasoning-model\",\"messages\":[{\"role\":\"user\",\"content\":\"Give me the answer\"}]}";
        string recovery = (string)(buildRequest.Invoke(proxy, new object[] { request, diagnostic, "", "stop", 1 }) ?? "");
        using JsonDocument document = JsonDocument.Parse(recovery);
        JsonElement messages = document.RootElement.GetProperty("messages");
        JsonElement last = messages[messages.GetArrayLength() - 1];

        Assert.AreEqual("user", last.GetProperty("role").GetString());
        StringAssert.Contains(last.GetProperty("content").GetString(), "visible answer recovery");
        StringAssert.Contains(last.GetProperty("content").GetString(), "/no_think");
        Assert.IsFalse(messages.EnumerateArray().Any(message =>
            message.GetProperty("content").GetString()?.StartsWith("The model used the response budget", StringComparison.OrdinalIgnoreCase) == true));
    }

    [TestMethod]
    public void WebChatPlainMode_DoesNotDiscardReasoningEvents()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "const suppressReasoning = noThinkRequested;");
        Assert.IsFalse(html.Contains("noThinkRequested || !selectedService()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebChatSessionUx_UsesPanelsOptimisticRollbackAndEnterSteering()
    {
        string html = HtmlPageResources.GetHtml("JackLLMWebChat.html");

        StringAssert.Contains(html, "sessionMoveBackdrop");
        StringAssert.Contains(html, "sessionDeletePhrase.value !== 'DELETE ALL'");
        StringAssert.Contains(html, "pendingSessionMutationIds");
        StringAssert.Contains(html, "restoreSessionSnapshot(snapshot)");
        StringAssert.Contains(html, "steerComposerImmediately()");
        StringAssert.Contains(html, "event.isComposing");
        StringAssert.Contains(html, "steeringId");
        StringAssert.Contains(html, "streamState.connected && streamState.jackhammerEnabled");
        StringAssert.Contains(html, "JackHammer steps");
        StringAssert.Contains(html, "jackhammerPlanStepsFromCheckpoints");
        StringAssert.Contains(html, "Errors / Diagnosis");
        StringAssert.Contains(html, ">File</button>");
        StringAssert.Contains(html, ">Edit</button>");
        StringAssert.Contains(html, ">Tools</button>");
        StringAssert.Contains(html, ">Options</button>");
        StringAssert.Contains(html, ">Help</button>");
    }

    [TestMethod]
    public void SplitThinkTags_MovesEndOfThoughtPrefixToReasoning()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("SplitThinkTags", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        object completion = method.Invoke(proxy, new object[] { "provided above.\n</end_of_thought>\nFinal answer", "", false })!;
        string content = (string)(completion.GetType().GetProperty("Content")?.GetValue(completion) ?? "");
        string reasoning = (string)(completion.GetType().GetProperty("Reasoning")?.GetValue(completion) ?? "");

        Assert.AreEqual("Final answer", content);
        StringAssert.Contains(reasoning, "provided above.");
        Assert.IsFalse(content.Contains("end_of_thought", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void AbruptStopContinuationCandidate_DetectsClippedVisibleSentence()
    {
        const string clipped = "I notice you've mentioned attached screenshots (Screenshot_00014.jpg, Screenshot_00006";

        Assert.IsTrue(IsAbruptStopContinuationCandidate("stop", clipped));
    }

    [TestMethod]
    public void AbruptStopContinuationCandidate_IgnoresCompleteOrShortAnswers()
    {
        Assert.IsFalse(IsAbruptStopContinuationCandidate("stop", "The screenshots show the Web Chat interface."));
        Assert.IsFalse(IsAbruptStopContinuationCandidate("stop", "The button says Send"));
        Assert.IsFalse(IsAbruptStopContinuationCandidate("length", "This answer is clipped"));
    }

    [TestMethod]
    public void AutoContinuationRequest_AppendsPartialAssistantThenUserInstruction()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "BuildChatUiAutoContinuationRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        const string request = """
        {"model":"vision-model","messages":[{"role":"user","content":"Describe both screenshots."}]}
        """;

        string continuation = (string)(method.Invoke(proxy, new object[] { request, "Partial answer", "", "stop", 1 }) ?? "");
        using JsonDocument document = JsonDocument.Parse(continuation);
        JsonElement messages = document.RootElement.GetProperty("messages");

        Assert.AreEqual("assistant", messages[messages.GetArrayLength() - 2].GetProperty("role").GetString());
        Assert.AreEqual("Partial answer", messages[messages.GetArrayLength() - 2].GetProperty("content").GetString());
        Assert.AreEqual("user", messages[messages.GetArrayLength() - 1].GetProperty("role").GetString());
        StringAssert.Contains(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString(), "Continue the same answer");
    }

    private static string ExtractNovel(string existing, string incoming)
    {
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "ExtractNovelChatUiStreamDelta",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)(method.Invoke(null, new object[] { existing, incoming }) ?? "");
    }

    private static bool IsAbruptStopContinuationCandidate(string finishReason, string content)
    {
        MethodInfo? method = typeof(LmVsProxy).GetMethod(
            "IsChatUiAbruptStopContinuationCandidate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (bool)(method.Invoke(null, new object[] { finishReason, content }) ?? false);
    }
}
