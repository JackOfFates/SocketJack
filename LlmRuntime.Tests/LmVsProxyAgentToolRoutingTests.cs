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
    public void JackhammerUsesReasoningDependentToolRoundBudget()
    {
        Assert.AreEqual(36, GetJackhammerToolRoundBudget("""{"messages":[]}"""));
        Assert.AreEqual(2, GetJackhammerToolRoundBudget("""{"messages":[{"role":"system","content":"[Jackhammer work mode]\nJackhammer turn budget: 2"}]}"""));
        Assert.AreEqual(100, GetJackhammerToolRoundBudget("""{"messages":[{"role":"system","content":"[Jackhammer work mode]\nJackhammer turn budget: 100"}]}"""));
        Assert.AreEqual(200, GetJackhammerToolRoundBudget("""{"messages":[{"role":"system","content":"[Jackhammer work mode]\nJackhammer turn budget: 999"}]}"""));
    }

    [TestMethod]
    public void PlanModeNeverAdvertisesWriteCapableAgentTools()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("IsChatAgentServiceSelected", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { """{"service":"agent","interactionMode":"chat"}""" })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { """{"service":"agent","interactionMode":"plan"}""" })!);
    }

    [TestMethod]
    public void JackhammerRepromptsDirectAnswerBeforeCheckpoint()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("ShouldContinueJackhammerAfterDirectCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[Jackhammer work mode]\nJackhammer turn budget: 10"},{"role":"user","content":"Do the work."}],"tools":[{"type":"function","function":{"name":"goal_checkpoint","parameters":{"type":"object"}}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        StringAssert.Contains((string)arguments[1]!, "Call goal_checkpoint now");
    }

    [TestMethod]
    public void JackhammerCreatesServerOwnedCheckpointBeforeModelSelection()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildInitialJackhammerCheckpointToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[Jackhammer work mode]\nJackhammer turn budget: 10"},{"role":"user","content":"Inspect, implement, and test the fix."}],"tools":[{"type":"function","function":{"name":"goal_checkpoint","parameters":{"type":"object"}}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object checkpoint = arguments[1]!;
        Assert.AreEqual("goal_checkpoint", checkpoint.GetType().GetProperty("Name")!.GetValue(checkpoint));
        string json = (string)checkpoint.GetType().GetProperty("ArgumentsJson")!.GetValue(checkpoint)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("in_progress", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(3, document.RootElement.GetProperty("steps").GetArrayLength());
        StringAssert.StartsWith(document.RootElement.GetProperty("steps")[0].GetString(), "in_progress|");
    }

    [TestMethod]
    public void JackhammerDirectReadOnlyPromptBypassesSlowToolSelectorAfterCheckpoint()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("JackhammerCanAnswerDirectlyWithoutProxyTools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string directRequest = """{"messages":[{"role":"system","content":"[Jackhammer work mode]"},{"role":"user","content":"Use JackHammer to perform exactly two read-only steps: first state today's date, second compute 2 + 2. Show ordered plan progress. Do not modify files or system state."}]}""";
        const string toolRequest = """{"messages":[{"role":"system","content":"[Jackhammer work mode]"},{"role":"user","content":"Run the project tests in PowerShell and report the output."}]}""";

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { directRequest })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { toolRequest })!);
    }

    [TestMethod]
    public void JackhammerCompletesLocalDateAndArithmeticMicrotaskWithoutModelInference()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildDeterministicJackhammerReadOnlyCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[Jackhammer work mode]"},{"role":"user","content":"Use JackHammer to perform exactly two read-only steps: first state today's date, second compute 2 + 2. Show ordered plan progress. Do not modify files or system state."}],"tool_choice":"none"}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        string content = (string)arguments[1]!.GetType().GetProperty("Content")!.GetValue(arguments[1])!;
        StringAssert.StartsWith(content, "- Today's date is ");
        StringAssert.Contains(content, "\n- 2 + 2 = 4.");
        Assert.AreEqual(2, content.Split('\n').Length);
    }

    [TestMethod]
    public void ExplicitTwoLineSandboxFilePromptPreloadsWriteTool()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildExplicitRequiredProxyFileWriteToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"user","content":"Create a sandboxed Project Files file named live-file-actions-probe.txt with exactly two lines: alpha and beta. Use vs_write_file. Do not only describe the change."}],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_write_file", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("live-file-actions-probe.txt", document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("alpha\nbeta", document.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public void ExplicitSandboxReplacePromptPreloadsReplaceTool()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildExplicitRequiredProxyFileMutationToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"user","content":"In live-file-actions-probe.txt, replace exactly \"alpha\" with \"ALPHA\" using vs_replace_in_file."}],"tools":[{"type":"function","function":{"name":"vs_replace_in_file"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_replace_in_file", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("live-file-actions-probe.txt", document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("alpha", document.RootElement.GetProperty("oldString").GetString());
        Assert.AreEqual("ALPHA", document.RootElement.GetProperty("newString").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("replaceAll").GetBoolean());
    }

    [TestMethod]
    public void ExplicitSandboxDeletePromptPreloadsDeleteTool()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildExplicitRequiredProxyFileMutationToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"user","content":"Delete live-file-actions-probe.txt using vs_delete_file."}],"tools":[{"type":"function","function":{"name":"vs_delete_file"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_delete_file", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("live-file-actions-probe.txt", document.RootElement.GetProperty("path").GetString());
    }

    [TestMethod]
    public void CompletedExplicitFileToolReturnsWithoutAnotherModelPass()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(LmVsProxy).GetMethod("TryBuildDirectExplicitFileToolCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"vs_delete_file","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"vs_delete_file deleted \\live-file-actions-probe.txt."}]}""",
            "vs_delete_file",
            null,
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object completion = arguments[3]!;
        Assert.AreEqual("Done. vs_delete_file deleted \\live-file-actions-probe.txt.", completion.GetType().GetProperty("Content")!.GetValue(completion));
        Assert.AreEqual("stop", completion.GetType().GetProperty("FinishReason")!.GetValue(completion));
    }

    [TestMethod]
    public void JackhammerToolSelectionUsesTheStandardRuntimeBudget()
    {
        MethodInfo method = typeof(LlmRuntimeHost).GetMethod("ApplyToolSelectionBudget", BindingFlags.Static | BindingFlags.NonPublic)!;
        var jackhammer = new LlmChatRequest
        {
            MaxTokens = 4096,
            Messages = [new LlmChatMessage("system", "[Jackhammer work mode]")]
        };
        var ordinary = new LlmChatRequest
        {
            MaxTokens = 4096,
            Messages = [new LlmChatMessage("user", "Use a tool.")]
        };

        method.Invoke(null, new object[] { jackhammer });
        method.Invoke(null, new object[] { ordinary });

        Assert.AreEqual(512, jackhammer.MaxTokens);
        Assert.AreEqual(512, ordinary.MaxTokens);
        Assert.IsTrue(jackhammer.MaxTokensSpecified);
    }

    [TestMethod]
    public void ModelPromptHttpClientHasNoElapsedTimeout()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        FieldInfo field = typeof(LmVsProxy).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var client = (System.Net.Http.HttpClient)field.GetValue(proxy)!;

        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
        proxy.PromptTimeout = TimeSpan.FromSeconds(1);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout,
            "Long GPU inference must continue until user cancellation or runtime completion.");
    }

    [TestMethod]
    public void JackhammerGoalCheckpointPreservesOrderedPlanSteps()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(LmVsProxy).GetMethod("BuildProxyCoordinationToolResult", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        object result = method!.Invoke(proxy, new object[]
        {
            "goal_checkpoint",
            "{\"goal\":\"Ship it\",\"status\":\"in_progress\",\"steps\":[\"completed|Inspect\",\"in_progress|Implement\",\"pending|Verify\"],\"progressPercent\":40}",
            "{\"messages\":[{\"role\":\"user\",\"content\":\"Ship it\"}]}"
        })!;
        string json = (string)(result.GetType().GetProperty("Result")?.GetValue(result) ?? "");
        using JsonDocument document = JsonDocument.Parse(json);
        string[] steps = document.RootElement.GetProperty("steps").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();

        CollectionAssert.AreEqual(new[] { "completed|Inspect", "in_progress|Implement", "pending|Verify" }, steps);
    }

    [TestMethod]
    public void ExactFinalAnswerInstructionDoesNotSuppressRequiredFileTools()
    {
        Assert.IsTrue(PromptLikelyNeedsProxyTools(
            "Create C:\\Users\\Vin\\project\\socketjack.md with a summary. When finished, final answer exactly DONE."));
    }

    private static int GetJackhammerToolRoundBudget(string requestBody)
    {
        MethodInfo method = typeof(LmVsProxy).GetMethod("GetJackhammerToolRoundBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, new object[] { requestBody })!;
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
    public void ChatUiSteeringBeforeStreamRegistrationIsRejected()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        const string ownerKey = "owner-for-steering-test";
        const string streamId = "stream_pending_steering_test";
        const string sessionId = "session-pending-steering-test";
        const string steering = "Use the already opened browser context.";

        Assert.IsFalse(AddActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, steering));

        object active = RegisterActiveChatStreamCancellation(proxy, ownerKey, streamId, sessionId);
        Assert.AreEqual("", ConsumeActiveChatStreamSteering(proxy, active));
    }

    [TestMethod]
    public void ChatUiSteeringAcceptsOrdinaryChatStreams()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        const string ownerKey = "owner-for-non-jackhammer-steering-test";
        const string streamId = "stream_non_jackhammer_steering_test";
        const string sessionId = "session-non-jackhammer-steering-test";
        _ = RegisterActiveChatStreamCancellation(proxy, ownerKey, streamId, sessionId, jackhammerEnabled: false);

        Assert.IsTrue(AcceptActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "refine this answer", "steer_chat", out string state));
        Assert.AreEqual("accepted", state);
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
    public void ChatUiSteeringIdIsDeduplicatedAndClosedStreamIsRejected()
    {
        using var proxy = new LmVsProxy("127.0.0.1", 11434, 11435);
        const string ownerKey = "owner-for-steering-id-test";
        const string streamId = "stream_steering_id_test";
        const string sessionId = "session-steering-id-test";
        object active = RegisterActiveChatStreamCancellation(proxy, ownerKey, streamId, sessionId);

        Assert.IsTrue(AcceptActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "apply once", "steer_same", out string firstState));
        Assert.IsTrue(AcceptActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "apply once", "steer_same", out string duplicateState));
        Assert.AreEqual("accepted", firstState);
        Assert.AreEqual("accepted", duplicateState);
        Assert.AreEqual("apply once", ConsumeActiveChatStreamSteering(proxy, active));

        UnregisterActiveChatStreamCancellation(proxy, active);
        Assert.IsFalse(AcceptActiveChatStreamSteering(proxy, ownerKey, streamId, sessionId, "too late", "steer_late", out string closedState));
        Assert.AreEqual("stream_closed", closedState);
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

    private static object RegisterActiveChatStreamCancellation(LmVsProxy proxy, string ownerKey, string streamId, string sessionId, bool jackhammerEnabled = true)
    {
        var method = typeof(LmVsProxy).GetMethod(
            "RegisterActiveChatStreamCancellation",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "RegisterActiveChatStreamCancellation should remain available for steering tests.");
        string requestBody = jackhammerEnabled
            ? "{\"jackhammer\":{\"enabled\":true,\"runId\":\"test_run\"}}"
            : "{\"jackhammer\":{\"enabled\":false}}";
        return method!.Invoke(proxy, new object[] { ownerKey, streamId, sessionId, requestBody })!;
    }

    private static bool AcceptActiveChatStreamSteering(LmVsProxy proxy, string ownerKey, string streamId, string sessionId, string steering, string steeringId, out string state)
    {
        var method = typeof(LmVsProxy).GetMethod("AcceptActiveChatStreamSteering", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        object[] args = { ownerKey, streamId, sessionId, steering, steeringId, string.Empty };
        bool accepted = (bool)method!.Invoke(proxy, args)!;
        state = (string)args[5];
        return accepted;
    }

    private static void UnregisterActiveChatStreamCancellation(LmVsProxy proxy, object activeStreamCancellation)
    {
        var method = typeof(LmVsProxy).GetMethod("UnregisterActiveChatStreamCancellation", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        method!.Invoke(proxy, new[] { activeStreamCancellation });
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
