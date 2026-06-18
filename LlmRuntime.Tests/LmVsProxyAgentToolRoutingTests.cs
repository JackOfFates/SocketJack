using System.Reflection;
using System.Text.Json;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LmVsProxyAgentToolRoutingTests
{
    [TestMethod]
    public void ExactFinalAnswerInstructionDoesNotSuppressRequiredFileTools()
    {
        Assert.IsTrue(PromptLikelyNeedsProxyTools(
            "Create C:\\Users\\Vin\\project\\socketjack.md with a summary. When finished, final answer exactly DONE."));
    }

    [TestMethod]
    public void ExactReplyWithoutToolNeedStillBypassesTools()
    {
        Assert.IsFalse(PromptLikelyNeedsProxyTools("Return exactly TEXT_MODE_EXACT_OK and nothing else."));
    }

    [TestMethod]
    public void SummaryPromptToNamedMarkdownFileRequiresFileWrite()
    {
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Summarize the SocketJack C# library to a file called socketjack.md."));
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Summarize this project to a project_summary.md file."));
    }

    [TestMethod]
    public void SummaryPromptWithExplicitVsWriteFileRequiresFileWrite()
    {
        const string prompt = """
            Summarize the SocketJack C# library to a file called socketjack.md.

            Use the available Visual Studio/file tools. Inspect the C# library under C:\Users\Vin\Documents\GitHub\SocketJack\SocketJack with vs_list_files, vs_search_files, and vs_read_file as needed. Then create or overwrite this exact file with vs_write_file:
            C:\Users\Vin\Documents\GitHub\SocketJack\SocketJack\socketjack.md

            Do not claim success until vs_write_file completes. After vs_write_file succeeds, reply exactly SOCKETJACK_SUMMARY_DONE.
            """;

        Assert.IsTrue(PromptRequestsProxyFileWrite(prompt));
        Assert.IsTrue(PromptLikelyNeedsProxyTools(prompt));
        Assert.AreEqual(
            "C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\socketjack.md",
            ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void AbsolutePathExactContentPromptRequiresFileWrite()
    {
        Assert.IsTrue(PromptRequestsProxyFileWrite(
            "Use available tools. Create C:\\Users\\Vin\\project\\probe.txt containing exactly this single line: Generated-by-test. After the tool succeeds, reply exactly TOOL_WRITE_OK."));
    }

    [TestMethod]
    public void AbsolutePathExactContentExtractionStopsAtInstructionText()
    {
        const string prompt = "Use available tools. Create C:\\Users\\Vin\\project\\probe.txt containing exactly this single line: Generated-by-test. After the tool succeeds, reply exactly TOOL_WRITE_OK.";

        Assert.AreEqual("C:\\Users\\Vin\\project\\probe.txt", ExtractLikelyRequestedFileTarget(prompt));
        Assert.IsTrue(TryExtractExactRequestedFileContent(prompt, out var content));
        Assert.AreEqual("Generated-by-test.", content);
    }

    [TestMethod]
    public void FileTargetExtractionIgnoresSlashFragmentBeforeWindowsPath()
    {
        const string prompt = "Use the available Visual Studio/file tools to create the file C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\public_proxy_stream_probe.txt containing exactly this single line: Generated-by-test";

        Assert.AreEqual(
            "C:\\Users\\Vin\\Documents\\GitHub\\SocketJack\\SocketJack\\public_proxy_stream_probe.txt",
            ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void FileTargetExtractionPrefersNamedOutputOverSourceMention()
    {
        const string prompt = "Summarize socketjack.md to a file called project_summary.md.";

        Assert.AreEqual("project_summary.md", ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void ChatOnlySummaryDoesNotRequireFileWrite()
    {
        Assert.IsFalse(PromptRequestsProxyFileWrite("Summarize socketjack.md in chat only."));
    }

    [TestMethod]
    public void ChatUiSteeringIsAddedAsLateUserDirection()
    {
        const string requestBody = """
            {"model":"qwen-tools","messages":[{"role":"system","content":"Base rules."},{"role":"user","content":"Start a long agent task."}],"tools":[]}
            """;

        string rewritten = ApplyChatUiSteeringToRequest(requestBody, "Focus the answer on the JackLLM web UI.");

        using JsonDocument document = JsonDocument.Parse(rewritten);
        JsonElement root = document.RootElement;
        Assert.AreEqual("qwen-tools", root.GetProperty("model").GetString());
        Assert.IsTrue(root.TryGetProperty("tools", out _));

        List<JsonElement> messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.AreEqual(4, messages.Count);
        Assert.AreEqual("system", messages[2].GetProperty("role").GetString());
        StringAssert.Contains(messages[2].GetProperty("content").GetString(), "JackLLM conversation steering");
        Assert.AreEqual("user", messages[3].GetProperty("role").GetString());
        StringAssert.Contains(messages[3].GetProperty("content").GetString(), "Focus the answer on the JackLLM web UI.");
    }

    [TestMethod]
    public void ChatUiSteeringBeforeStreamRegistrationIsBuffered()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        const string ownerKey = "owner-for-steering-test";
        const string streamId = "stream_pending_steering_test";
        const string sessionId = "session-pending-steering-test";
        const string steering = "Use the already opened browser context.";

        Assert.IsTrue(AddActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, steering));

        object active = RegisterActiveChatStreamCancellation(proxy, ownerKey, streamId, sessionId);
        Assert.AreEqual(steering, ConsumeActiveChatStreamSteering(proxy, active));
    }

    [TestMethod]
    public void ChatUiSteeringMultipleUpdatesPreserveOrder()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        const string ownerKey = "owner-for-steering-multi-test";
        const string streamId = "stream_multi_steering_test";
        const string sessionId = "session-multi-steering-test";

        object active = RegisterActiveChatStreamCancellation(proxy, ownerKey, streamId, sessionId);
        Assert.IsTrue(AddActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "respond with OK_2 if you get this"));
        Assert.IsTrue(AddActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "respond with OK_3 if you get this"));

        string steering = ConsumeActiveChatStreamSteering(proxy, active);

        StringAssert.Contains(steering, "[Steering update 1]");
        StringAssert.Contains(steering, "respond with OK_2 if you get this");
        StringAssert.Contains(steering, "[Steering update 2]");
        StringAssert.Contains(steering, "respond with OK_3 if you get this");
        Assert.IsTrue(steering.IndexOf("OK_2", StringComparison.Ordinal) < steering.IndexOf("OK_3", StringComparison.Ordinal), steering);
    }

    [TestMethod]
    public void ChatUiSteeringInstructionRequiresEveryUpdate()
    {
        const string requestBody = """
            {"model":"qwen-tools","messages":[{"role":"user","content":"Start a long agent task."}],"tools":[]}
            """;

        string rewritten = ApplyChatUiSteeringToRequest(requestBody, "[Steering update 1]\nrespond with OK_2\n\n[Steering update 2]\nrespond with OK_3");

        using JsonDocument document = JsonDocument.Parse(rewritten);
        List<JsonElement> messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();
        StringAssert.Contains(messages[^2].GetProperty("content").GetString(), "apply every update in order");
        StringAssert.Contains(messages[^1].GetProperty("content").GetString(), "respond with OK_2");
        StringAssert.Contains(messages[^1].GetProperty("content").GetString(), "respond with OK_3");
    }

    [TestMethod]
    public void ExtractLooseProxyToolCallsParsesAttributedBrowserOpenParameterTag()
    {
        const string leakedToolText = """
            Assistant requested tool call(s):
            <tool_call id="call_browser" name="browser_open"><parameter>{"url":"https://duckduckgo.com/?q=2001+audi+s4+black+lowered+BBS"}</parameter></tool_call>
            """;

        List<ToolCallData> calls = ExtractLooseProxyToolCalls(leakedToolText);

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("call_browser", calls[0].Id);
        Assert.AreEqual("browser_open", calls[0].Name);
        Assert.AreEqual("{\"url\":\"https://duckduckgo.com/?q=2001+audi+s4+black+lowered+BBS\"}", calls[0].ArgumentsJson);
        Assert.IsTrue(calls[0].ArgumentsWereMalformed);
    }

    private static bool PromptLikelyNeedsProxyTools(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "PromptLikelyNeedsProxyTools",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptLikelyNeedsProxyTools should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool PromptRequestsProxyFileWrite(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "PromptRequestsProxyFileWrite",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptRequestsProxyFileWrite should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static string ExtractLikelyRequestedFileTarget(string prompt)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "ExtractLikelyRequestedFileTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "ExtractLikelyRequestedFileTarget should remain available for routing tests.");
        return (string)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool TryExtractExactRequestedFileContent(string prompt, out string content)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "TryExtractExactRequestedFileContent",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "TryExtractExactRequestedFileContent should remain available for routing tests.");
        object[] args = { prompt, string.Empty };
        bool result = (bool)method!.Invoke(null, args)!;
        content = (string)args[1];
        return result;
    }

    private static string ApplyChatUiSteeringToRequest(string requestBody, string steering)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        var method = typeof(LmVsProxy).GetMethod(
            "ApplyChatUiSteeringToRequest",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ApplyChatUiSteeringToRequest should remain available for steering tests.");
        return (string)method!.Invoke(proxy, new object[] { requestBody, steering })!;
    }

    private static List<ToolCallData> ExtractLooseProxyToolCalls(string content)
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        var method = typeof(LmVsProxy).GetMethod(
            "ExtractLooseProxyToolCalls",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ExtractLooseProxyToolCalls should remain available for tool-call steering tests.");
        return (List<ToolCallData>)method!.Invoke(proxy, new object[] { content })!;
    }

    private static bool AddActiveChatStreamSteering(LmVsProxy proxy, string ownerKey, string streamId, string sessionId, string steering)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "AddActiveChatStreamSteering",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "AddActiveChatStreamSteering should remain available for steering tests.");
        return (bool)method!.Invoke(proxy, new object[] { ownerKey, streamId, sessionId, steering })!;
    }

    private static object RegisterActiveChatStreamCancellation(LmVsProxy proxy, string ownerKey, string streamId, string sessionId)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "RegisterActiveChatStreamCancellation",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "RegisterActiveChatStreamCancellation should remain available for steering tests.");
        return method!.Invoke(proxy, new object[] { ownerKey, streamId, sessionId })!;
    }

    private static string ConsumeActiveChatStreamSteering(LmVsProxy proxy, object activeStreamCancellation)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "ConsumeActiveChatStreamSteering",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ConsumeActiveChatStreamSteering should remain available for steering tests.");
        return (string)method!.Invoke(proxy, new[] { activeStreamCancellation })!;
    }
}
