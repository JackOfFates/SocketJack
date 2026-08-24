using System.Reflection;
using System.Text.Json;
using heirowLLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class HeirowLlmAgentToolRoutingTests
{
    [TestMethod]
    public void ChatRequest_PreservesLatestPriorUserTaskBeforeTranscriptTruncation()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("BuildChatUiCompletionRequestJson", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var messages = new List<object>();
        for (int index = 0; index < 30; index++)
        {
            messages.Add(new { role = "user", content = "old request " + index + " " + new string('u', 1300) });
            messages.Add(new { role = "assistant", content = "old response " + index + " " + new string('a', 1300) });
        }
        messages.Add(new { role = "user", content = "Fix SENTINEL-PRIOR-TASK-9137 and validate it in Release." });
        messages.Add(new { role = "assistant", content = "I could not finish that task." });
        messages.Add(new { role = "user", content = "try again" });
        messages.Add(new { role = "assistant", content = "I still do not have enough context." });
        messages.Add(new { role = "user", content = "try again" });
        string request = JsonSerializer.Serialize(new { model = "test-model", service = "agent", messages });

        string rebuilt = (string)method.Invoke(proxy, new object?[] { request, false, null, null, "test:context", false })!;

        StringAssert.Contains(rebuilt, "[Current task continuity]");
        StringAssert.Contains(rebuilt, "SENTINEL-PRIOR-TASK-9137");
    }

    [TestMethod]
    public void ToolMediatorIsPlacedBeforeUserAndRequiresContextGrounding()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo getPermissions = typeof(HeirowLlm).GetMethod("GetChatPermissions", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)!;
        object permissions = getPermissions.Invoke(proxy, new object[] { "test:tool-mediation" })!;
        MethodInfo addTools = typeof(HeirowLlm).GetMethod("AddProxyResearchTools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
        {
          "model":"test-model",
          "messages":[
            {"role":"system","content":"[heirowLLM prior conversation]\nThe current project is C:\\work\\MusicApp and the failing file is Player.cs."},
            {"role":"user","content":"Fix that file with the tools."}
          ]
        }
        """;

        string mediated = (string)addTools.Invoke(proxy, new object[] { request, permissions, true, true, true, "test:tool-mediation" })!;
        using JsonDocument document = JsonDocument.Parse(mediated);
        JsonElement messages = document.RootElement.GetProperty("messages");
        int userIndex = -1;
        int mediatorIndex = -1;
        string mediator = "";
        for (int index = 0; index < messages.GetArrayLength(); index++)
        {
            string role = messages[index].GetProperty("role").GetString()!;
            string content = messages[index].GetProperty("content").GetString()!;
            if (role == "user") userIndex = index;
            if (content.Contains("[heirowLLM tool mediation]", StringComparison.Ordinal))
            {
                mediatorIndex = index;
                mediator = content;
            }
        }

        Assert.IsTrue(mediatorIndex >= 0);
        Assert.IsTrue(mediatorIndex < userIndex);
        Assert.AreEqual("Fix that file with the tools.", messages[userIndex].GetProperty("content").GetString());
        StringAssert.Contains(mediator, "complete current context");
        StringAssert.Contains(mediator, "Interpret references such as 'it', 'that', 'this file', and 'the current project'");
        StringAssert.Contains(mediator, "instead of forwarding the raw user sentence");
        StringAssert.Contains(mediator, "After every tool result, treat it as new context");
        Assert.AreEqual("auto", document.RootElement.GetProperty("tool_choice").GetString());
    }

    [TestMethod]
    public void HeirowForgeCompactsCodingToolsButKeepsResearchTurnsBroad()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("ShouldUseCompactCodingAgentTools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string codingRequest = """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Improve the existing C# website, build it, start the server process, and verify the UI."}]}""";
        const string researchRequest = """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Research the web for current sources about C# website hosting."}]}""";

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { codingRequest, true, true, true })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { researchRequest, true, true, true })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { codingRequest, false, true, true })!);
    }

    [TestMethod]
    public void HeirowForgeBuildsNoSchemaActionRecoveryAfterToolResults()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildHeirowForgeActionDraftRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
        {
          "model":"test-model",
          "stream":true,
          "messages":[
            {"role":"system","content":"[heirowForge work mode]"},
            {"role":"user","content":"Improve and build the C# website."},
            {"role":"assistant","tool_calls":[{"id":"call_read","type":"function","function":{"name":"vs_read_file","arguments":"{\"path\":\"Program.cs\"}"}}]},
            {"role":"tool","tool_call_id":"call_read","content":"vs_read_file result:\n1: Console.WriteLine(\"old\");"}
          ],
          "tools":[
            {"type":"function","function":{"name":"vs_replace_in_file","parameters":{"type":"object"}}},
            {"type":"function","function":{"name":"run_command_in_terminal","parameters":{"type":"object"}}}
          ],
          "tool_choice":"auto"
        }
        """;
        object?[] arguments = [request, null];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        using JsonDocument draft = JsonDocument.Parse((string)arguments[1]!);
        Assert.IsFalse(draft.RootElement.TryGetProperty("tools", out _));
        Assert.AreEqual("none", draft.RootElement.GetProperty("tool_choice").GetString());
        Assert.AreEqual(2, draft.RootElement.GetProperty("messages").GetArrayLength());
        StringAssert.Contains(draft.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!, "vs_replace_in_file");
        StringAssert.Contains(draft.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!, "Console.WriteLine");
    }

    [TestMethod]
    public void InspectedExistingFileWriteDefaultsToOverwrite()
    {
        const string request = """
        {"messages":[
          {"role":"user","content":"Improve the existing website."},
          {"role":"assistant","tool_calls":[{"id":"call_read","type":"function","function":{"name":"vs_read_file","arguments":"{\"path\":\"wwwroot/index.html\"}"}}]},
          {"role":"tool","tool_call_id":"call_read","content":"```html wwwroot/index.html (Lines 1-1)\n<h1>Old</h1>\n```"}
        ],"tools":[{"type":"function","function":{"name":"vs_read_file"}},{"type":"function","function":{"name":"vs_write_file"}}]}
        """;
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_write", Name = "vs_write_file", ArgumentsJson = "{\"path\":\"wwwroot/index.html\",\"content\":\"<h1>New</h1>\"}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        using JsonDocument arguments = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.IsTrue(arguments.RootElement.GetProperty("overwrite").GetBoolean());
    }

    [TestMethod]
    public void UninspectedExistingFileWriteDoesNotGainOverwrite()
    {
        const string request = """{"messages":[{"role":"user","content":"Improve the existing website."}],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}""";
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_write", Name = "vs_write_file", ArgumentsJson = "{\"path\":\"wwwroot/index.html\",\"content\":\"<h1>New</h1>\"}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        using JsonDocument arguments = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.IsFalse(arguments.RootElement.TryGetProperty("overwrite", out _));
    }

    [TestMethod]
    public void ExplicitFalseOverwriteRemainsFalseAfterRead()
    {
        const string request = """
        {"messages":[
          {"role":"user","content":"Improve the existing website."},
          {"role":"assistant","tool_calls":[{"id":"call_read","type":"function","function":{"name":"vs_read_file","arguments":"{\"path\":\"wwwroot/index.html\"}"}}]},
          {"role":"tool","tool_call_id":"call_read","content":"```html wwwroot/index.html (Lines 1-1)\n<h1>Old</h1>\n```"}
        ],"tools":[{"type":"function","function":{"name":"vs_read_file"}},{"type":"function","function":{"name":"vs_write_file"}}]}
        """;
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_write", Name = "vs_write_file", ArgumentsJson = "{\"path\":\"wwwroot/index.html\",\"content\":\"<h1>New</h1>\",\"overwrite\":false}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        using JsonDocument arguments = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.IsFalse(arguments.RootElement.GetProperty("overwrite").GetBoolean());
    }

    [TestMethod]
    public void HeirowForgeSuppressesDuplicateRootDiscoveryAndExactRead()
    {
        const string request = """
        {"messages":[
          {"role":"system","content":"[heirowForge work mode]"},
          {"role":"user","content":"Improve and build this website."},
          {"role":"assistant","tool_calls":[{"id":"call_list","type":"function","function":{"name":"vs_list_files","arguments":"{\"path\":\".\",\"recursive\":true}"}}]},
          {"role":"tool","tool_call_id":"call_list","content":"vs_list_files:\nProgram.cs\nwwwroot/index.html"},
          {"role":"assistant","tool_calls":[{"id":"call_read","type":"function","function":{"name":"vs_read_file","arguments":"{\"path\":\"Program.cs\"}"}}]},
          {"role":"tool","tool_call_id":"call_read","content":"```csharp Program.cs (Lines 1-1)\nConsole.WriteLine();\n```"}
        ],"tools":[{"type":"function","function":{"name":"vs_list_files"}},{"type":"function","function":{"name":"vs_read_file"}}]}
        """;
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_list_again", Name = "vs_list_files", ArgumentsJson = "{\"path\":\".\"}" },
            new() { Id = "call_read_again", Name = "vs_read_file", ArgumentsJson = "{\"path\":\"Program.cs\"}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void HeirowForgeSuppressesAnExactFailedReadRetry()
    {
        const string request = """
        {"messages":[
          {"role":"system","content":"[heirowForge work mode]"},
          {"role":"user","content":"Improve and build this website."},
          {"role":"assistant","tool_calls":[{"id":"call_missing","type":"function","function":{"name":"vs_read_file","arguments":"{\"path\":\"/app/styles.css\"}"}}]},
          {"role":"tool","tool_call_id":"call_missing","content":"vs_read_file error: file could not be found: /app/styles.css"}
        ],"tools":[{"type":"function","function":{"name":"vs_read_file"}}]}
        """;
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_missing_again", Name = "vs_read_file", ArgumentsJson = "{\"path\":\"/app/styles.css\"}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void EmptyFileListingDoesNotSatisfyProjectGrounding()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("RequestHasSuccessfulProxyToolResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string emptyRequest = """
        {"messages":[
          {"role":"assistant","tool_calls":[{"id":"call_list","type":"function","function":{"name":"vs_list_files","arguments":"{\"path\":\"/app\"}"}}]},
          {"role":"tool","tool_call_id":"call_list","content":"vs_list_files found no files."}
        ]}
        """;
        const string populatedRequest = """
        {"messages":[
          {"role":"assistant","tool_calls":[{"id":"call_list","type":"function","function":{"name":"vs_list_files","arguments":"{\"path\":\".\"}"}}]},
          {"role":"tool","tool_call_id":"call_list","content":"vs_list_files:\nProgram.cs\nwwwroot/index.html"}
        ]}
        """;

        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { emptyRequest, "vs_list_files" })!);
        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { populatedRequest, "vs_list_files" })!);
    }

    [TestMethod]
    public void IncompleteVsReplaceIsSuppressedBeforeExecution()
    {
        const string request = """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Improve the website."}],"tools":[{"type":"function","function":{"name":"vs_replace_in_file"}}]}""";
        var calls = new List<ToolCallData>
        {
            new() { Id = "call_incomplete", Name = "vs_replace_in_file", ArgumentsJson = "{\"path\":\"Program.cs\",\"newString\":\"var ready = true;\"}" }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void JackhammerRequiresRecordedRuntimeVerificationForApplicationChanges()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("PromptRequiresApplicationVerification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Fix and improve this web application."}],"tools":[{"type":"function","function":{"name":"record_project_verification"}}]}""";

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { request })!);
    }

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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("IsChatAgentServiceSelected", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { """{"service":"agent","interactionMode":"chat"}""" })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { """{"service":"agent","interactionMode":"plan"}""" })!);
    }

    [TestMethod]
    public void JackhammerRepromptsDirectAnswerBeforeCheckpoint()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("ShouldContinueJackhammerAfterDirectCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildInitialJackhammerCheckpointToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        Assert.AreEqual(5, document.RootElement.GetProperty("steps").GetArrayLength());
        StringAssert.StartsWith(document.RootElement.GetProperty("steps")[0].GetString(), "in_progress|");
        StringAssert.Contains(document.RootElement.GetProperty("steps")[0].GetString()!, "Inspect, implement, and test the fix");
        Assert.AreEqual(3, document.RootElement.GetProperty("steps")[0].GetString()!.Split('|').Length, "Each step should include a status, short title, and useful summary.");
        StringAssert.Contains(document.RootElement.GetProperty("steps")[3].GetString()!, "final affected content");
        Assert.IsFalse(document.RootElement.GetProperty("steps")[1].GetString()!.Contains("Perform the requested work", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HeirowForgeForcesProjectDiscoveryWhenOnlyCheckpointWorkExists()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildHeirowForgeProjectGroundingToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[heirowForge work mode]\n[HeirowLlm Agent Filesystem Context]\nCurrent project root: C:\\\\work."},{"role":"user","content":"Improve the existing C# website, build it, and verify it in a browser."},{"role":"assistant","tool_calls":[{"id":"checkpoint_1","type":"function","function":{"name":"goal_checkpoint","arguments":"{}"}}]},{"role":"tool","tool_call_id":"checkpoint_1","content":"goal_checkpoint result: {\"ok\":true,\"coordination\":true,\"tool\":\"goal_checkpoint\",\"status\":\"in_progress\",\"progressPercent\":5}"}],"tools":[{"type":"function","function":{"name":"goal_checkpoint"}},{"type":"function","function":{"name":"vs_list_files"}},{"type":"function","function":{"name":"vs_read_file"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_list_files", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        using JsonDocument document = JsonDocument.Parse((string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!);
        Assert.IsFalse(document.RootElement.GetProperty("recursive").GetBoolean());
        Assert.AreEqual(100, document.RootElement.GetProperty("take").GetInt32());
    }

    [TestMethod]
    public void HeirowForgeRecursivelyDiscoversFilesAfterShallowDirectoryListing()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildHeirowForgeRecursiveProjectDiscoveryToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Improve the website."},{"role":"assistant","tool_calls":[{"id":"list_1","type":"function","function":{"name":"vs_list_files","arguments":"{}"}}]},{"role":"tool","tool_call_id":"list_1","content":"vs_list_files:\nSJWeb"}],"tools":[{"type":"function","function":{"name":"vs_list_files","parameters":{"type":"object"}}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_list_files", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        using JsonDocument document = JsonDocument.Parse((string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!);
        Assert.IsTrue(document.RootElement.GetProperty("recursive").GetBoolean());
        Assert.AreEqual(200, document.RootElement.GetProperty("take").GetInt32());
    }

    [TestMethod]
    public void HeirowForgeReadsRelevantSourceAfterRecursiveDiscovery()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildHeirowForgeProjectFileInspectionToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"system","content":"[heirowForge work mode]"},{"role":"user","content":"Improve the website."},{"role":"assistant","tool_calls":[{"id":"list_1","type":"function","function":{"name":"vs_list_files","arguments":"{\"recursive\":true,\"reason\":\"[heirowForge recursive project discovery]\"}"}}]},{"role":"tool","tool_call_id":"list_1","content":"vs_list_files:\n\\Program.cs\n\\App.csproj\n\\wwwroot\\index.html"}],"tools":[{"type":"function","function":{"name":"vs_read_file","parameters":{"type":"object"}}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("vs_read_file", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        using JsonDocument document = JsonDocument.Parse((string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!);
        Assert.AreEqual("\\Program.cs", document.RootElement.GetProperty("path").GetString());
        StringAssert.Contains(document.RootElement.GetProperty("reason").GetString(), "Program.cs");
    }

    [TestMethod]
    public void JackhammerDirectReadOnlyPromptBypassesSlowToolSelectorAfterCheckpoint()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("JackhammerCanAnswerDirectlyWithoutProxyTools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string directRequest = """{"messages":[{"role":"system","content":"[Jackhammer work mode]"},{"role":"user","content":"Use JackHammer to perform exactly two read-only steps: first state today's date, second compute 2 + 2. Show ordered plan progress. Do not modify files or system state."}]}""";
        const string toolRequest = """{"messages":[{"role":"system","content":"[Jackhammer work mode]"},{"role":"user","content":"Run the project tests in PowerShell and report the output."}]}""";

        Assert.IsTrue((bool)method.Invoke(proxy, new object[] { directRequest })!);
        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { toolRequest })!);
    }

    [TestMethod]
    public void JackhammerCompletesLocalDateAndArithmeticMicrotaskWithoutModelInference()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildDeterministicJackhammerReadOnlyCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitRequiredProxyFileWriteToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitRequiredProxyFileMutationToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitRequiredProxyFileMutationToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildDirectExplicitFileToolCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
    public void CompletedExplicitTerminalToolReturnsCleanResultWithoutAnotherModelPass()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildDirectExplicitTerminalToolCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"run_command_in_terminal","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"{\"ok\":true,\"service\":\"terminal\",\"command\":\"dotnet build App.csproj\",\"workingDirectory\":\"C:\\\\work\",\"exitCode\":0,\"timedOut\":false,\"canceled\":false,\"stdout\":\"Build succeeded.\\n0 Warning(s)\\n0 Error(s)\",\"stderr\":\"\"}"}]}""",
            "run_command_in_terminal",
            null,
            null,
            false
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object completion = arguments[3]!;
        Assert.AreEqual(true, arguments[4]);
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Command completed successfully.");
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Exit code: 0");
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Build succeeded.");
        Assert.AreEqual("stop", completion.GetType().GetProperty("FinishReason")!.GetValue(completion));
    }

    [TestMethod]
    public void FailedExplicitTerminalToolReturnsFailureWithoutCompletingGoal()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildDirectExplicitTerminalToolCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"run_command_in_terminal","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"{\"ok\":false,\"service\":\"terminal\",\"command\":\"dotnet build Missing.csproj\",\"workingDirectory\":\"C:\\\\work\",\"exitCode\":1,\"timedOut\":false,\"canceled\":false,\"stdout\":\"\",\"stderr\":\"Project file does not exist.\"}"}]}""",
            "run_command_in_terminal",
            null,
            null,
            true
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object completion = arguments[3]!;
        Assert.AreEqual(false, arguments[4]);
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Command failed.");
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Exit code: 1");
        StringAssert.Contains((string)completion.GetType().GetProperty("Content")!.GetValue(completion)!, "Project file does not exist.");
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        FieldInfo field = typeof(HeirowLlm).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var client = (System.Net.Http.HttpClient)field.GetValue(proxy)!;

        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
        proxy.PromptTimeout = TimeSpan.FromSeconds(1);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout,
            "Long GPU inference must continue until user cancellation or runtime completion.");
    }

    [TestMethod]
    public void JackhammerGoalCheckpointPreservesOrderedPlanSteps()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(HeirowLlm).GetMethod("BuildProxyCoordinationToolResult", BindingFlags.Instance | BindingFlags.NonPublic);
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
        MethodInfo method = typeof(HeirowLlm).GetMethod("GetJackhammerToolRoundBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
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
    public void RelativeProjectFileTargetExtractionPreservesDirectories()
    {
        const string prompt = "Use vs_write_file now to create SJWeb/App.csproj with this exact content: <Project Sdk=\"Microsoft.NET.Sdk\"></Project>.";

        Assert.AreEqual("SJWeb/App.csproj", ExtractLikelyRequestedFileTarget(prompt));
        Assert.IsTrue(TryExtractExactRequestedFileContent(prompt, out string content));
        Assert.AreEqual("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", content);
    }

    [TestMethod]
    public void BrowserVerificationUrlIsNotTreatedAsAFileTarget()
    {
        const string prompt = "Update all required source files, then open http://127.0.0.1:18741/ in the browser and verify it.";

        Assert.AreEqual(string.Empty, ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void ExistingDllProcessAndRecentOutputDoNotRequireAFileWrite()
    {
        const string prompt = "Start the already-built SJWeb site as a managed tracked background process. Run dotnet \"C:\\Users\\Vin\\SJWeb\\bin\\Release\\net8.0\\App.dll\" with working directory \"C:\\Users\\Vin\\SJWeb\". Then inspect its tracked status and recent output, open http://127.0.0.1:18741/ in the browser, and leave the process running.";

        Assert.IsFalse(PromptRequestsProxyFileWrite(prompt));
        Assert.IsTrue(PromptLikelyNeedsProxyTools(prompt));
    }

    [TestMethod]
    public void ExplicitRelativeProjectWritePreloadsExactToolCall()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitRequiredProxyFileWriteToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"user","content":"Use vs_write_file now to create SJWeb/App.csproj with this exact content: <Project Sdk=\"Microsoft.NET.Sdk\"></Project>."}],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("SJWeb/App.csproj", document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", document.RootElement.GetProperty("content").GetString());
    }

    [TestMethod]
    public void MultilineExactContentPreloadsProgramFileToolCall()
    {
        const string prompt = """
            SJWeb Acceptance Step 2. Use vs_write_file now to create SJWeb/Program.cs with this exact content:
            using System.Net;
            using SocketJack.Net;

            var options = new NetworkOptions { BindAddress = IPAddress.Loopback };
            Make the tool call and report the exact path.
            """;

        Assert.AreEqual("SJWeb/Program.cs", ExtractLikelyRequestedFileTarget(prompt));
        Assert.IsTrue(TryExtractExactRequestedFileContent(prompt, out string content));
        Assert.AreEqual("using System.Net;\nusing SocketJack.Net;\n\nvar options = new NetworkOptions { BindAddress = IPAddress.Loopback };", content.Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void StreamedProgramWriteIsRepairedToExactRequestedContent()
    {
        const string request = """
            {"messages":[{"role":"user","content":"Use vs_write_file now to create SJWeb/Program.cs with this exact content:\nusing System.Net;\nusing SocketJack.Net;\n\nvar ready = true;\nMake the tool call and report the exact path."}],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}
            """;
        var calls = new List<ToolCallData>
        {
            new()
            {
                Id = "call_bad_program",
                Name = "vs_write_file",
                ArgumentsJson = "{\"path\":\"now to create SJWeb/Program.cs\",\"content\":\"using System.Net;\",\"overwrite\":true}"
            }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(1, calls.Count);
        using JsonDocument document = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.AreEqual("SJWeb/Program.cs", document.RootElement.GetProperty("path").GetString());
        Assert.AreEqual("using System.Net;\nusing SocketJack.Net;\n\nvar ready = true;", document.RootElement.GetProperty("content").GetString()!.Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void ExplicitTerminalCommandPreloadsExactToolCall()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitTerminalCommandToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            """{"messages":[{"role":"user","content":"Call run_command_in_terminal now with command dotnet build SJWeb/App.csproj -c Release and summary Build SJWeb Release. Do not edit files. After the tool result, report the exit code."}],"tools":[{"type":"function","function":{"name":"run_command_in_terminal"}}]}""",
            null
        ];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("dotnet build SJWeb/App.csproj -c Release", document.RootElement.GetProperty("command").GetString());
        Assert.AreEqual("Build SJWeb Release", document.RootElement.GetProperty("summary").GetString());
    }

    [TestMethod]
    public void NaturalManagedProcessRequestPreloadsTrackedStartToolCall()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitManagedProcessStartToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string prompt = "Launch the existing SJWeb Release app as a managed background process and keep it running. The command is dotnet \"C:\\Users\\Vin\\SJWeb\\bin\\Release\\net8.0\\App.dll\" and its working directory is \"C:\\Users\\Vin\\SJWeb\". After launch, inspect its tracked status and recent output.";
        string request = JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = prompt } },
            tools = new[] { new { type = "function", function = new { name = "terminal_start_process" } } }
        });
        object?[] arguments = [request, null];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("terminal_start_process", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("dotnet \"C:\\Users\\Vin\\SJWeb\\bin\\Release\\net8.0\\App.dll\"", document.RootElement.GetProperty("command").GetString());
        Assert.AreEqual("C:\\Users\\Vin\\SJWeb", document.RootElement.GetProperty("workingDirectory").GetString());
    }

    [TestMethod]
    public void ManagedProcessStartResultPreloadsStatusCheck()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildManagedProcessStatusToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
            {"messages":[{"role":"user","content":"Launch this as a managed background process, then inspect its tracked status and recent output."},{"role":"assistant","tool_calls":[{"id":"call_start","type":"function","function":{"name":"terminal_start_process","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_start","name":"terminal_start_process","content":"{\\n  \"ok\": true,\\n  \"service\": \"terminal_processes\",\\n  \"action\": \"start\",\\n  \"process\": {\\n    \"ProcessId\": 24680,\\n    \"Running\": true\\n  }\\n}"}],"tools":[{"type":"function","function":{"name":"terminal_process_status"}}]}
            """;
        object?[] arguments = [request, null];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("terminal_process_status", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(24680, document.RootElement.GetProperty("processId").GetInt32());
    }

    [TestMethod]
    public void NaturalBrowserVerificationRunsAgainForANewUserTurn()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildExplicitBrowserOpenToolCall", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
            {"messages":[{"role":"user","content":"Open http://127.0.0.1:18741/ in the browser."},{"role":"assistant","tool_calls":[{"id":"call_old_browser","type":"function","function":{"name":"browser_open","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_old_browser","content":"Browser Skill browser_open result: online"},{"role":"user","content":"Verify the website in the browser at http://127.0.0.1:18741/ again."}],"tools":[{"type":"function","function":{"name":"browser_open"}}]}
            """;
        object?[] arguments = [request, null];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object toolCall = arguments[1]!;
        Assert.AreEqual("browser_open", toolCall.GetType().GetProperty("Name")!.GetValue(toolCall));
        string json = (string)toolCall.GetType().GetProperty("ArgumentsJson")!.GetValue(toolCall)!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual("http://127.0.0.1:18741/", document.RootElement.GetProperty("url").GetString());
    }

    [TestMethod]
    public void PriorTurnProcessStatusDoesNotSatisfyANewLaunchRequest()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("RequestHasSuccessfulProxyToolResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
            {"messages":[{"role":"user","content":"Start the app."},{"role":"assistant","tool_calls":[{"id":"call_old_status","type":"function","function":{"name":"terminal_process_status","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_old_status","content":"{\"ok\":true}"},{"role":"user","content":"Launch it again and inspect the new process status."}]}
            """;

        Assert.IsFalse((bool)method.Invoke(proxy, new object[] { request, "terminal_process_status" })!);
    }

    [TestMethod]
    public void ManagedProcessBrowserVerificationReturnsConcreteFinalReport()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo method = typeof(HeirowLlm).GetMethod("TryBuildDirectManagedProcessVerificationCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string request = """
            {"messages":[{"role":"user","content":"Launch the website as a managed process, inspect its tracked status, open http://127.0.0.1:18741/ in the browser, and leave it running."}]}
            """;
        const string continuation = """
            {"messages":[{"role":"user","content":"Launch the website as a managed process, inspect its tracked status, open http://127.0.0.1:18741/ in the browser, and leave it running."},{"role":"assistant","tool_calls":[{"id":"call_status","type":"function","function":{"name":"terminal_process_status","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_status","content":"{\\n  \"ok\": true,\\n  \"process\": {\\n    \"ProcessId\": 24680,\\n    \"Running\": true,\\n    \"Output\": \"Listening on 127.0.0.1:18741\"\\n  }\\n}"},{"role":"assistant","tool_calls":[{"id":"call_browser","type":"function","function":{"name":"browser_open","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_browser","content":"[Browser Skill browser_open]\r\nurl: http://127.0.0.1:18741/\r\ntitle: SJWeb · SocketJack Tool Lab\r\n\r\n[Visible text]\r\nonline"}]}
            """;
        object?[] arguments = [request, continuation, null, null];

        Assert.IsTrue((bool)method.Invoke(proxy, arguments)!);
        object completion = arguments[3]!;
        string content = (string)completion.GetType().GetProperty("Content")!.GetValue(completion)!;
        StringAssert.Contains(content, "24680");
        StringAssert.Contains(content, "Running: **yes**");
        StringAssert.Contains(content, "Listening on 127.0.0.1:18741");
        StringAssert.Contains(content, "SJWeb · SocketJack Tool Lab");
        Assert.IsFalse(content.Contains("SocketJack Tool Lab\\r", StringComparison.Ordinal));
        StringAssert.Contains(content, "left running");
    }

    [TestMethod]
    public void ReplaceTargetPrecedesHtmlClosingTagFragments()
    {
        const string prompt = "Use vs_replace_in_file on SJWeb/wwwroot/index.html. Replace exactly `<div class=\"eyebrow\">old</div>` with `<div class=\"eyebrow\">new</div>`.";

        Assert.AreEqual("SJWeb/wwwroot/index.html", ExtractLikelyRequestedFileTarget(prompt));
    }

    [TestMethod]
    public void StreamedWriteTargetDropsPromptProsePrefix()
    {
        const string request = """{"messages":[{"role":"user","content":"Use vs_write_file now to create SJWeb/App.csproj with generated content."}],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}""";
        var calls = new List<ToolCallData>
        {
            new()
            {
                Id = "call_bad_path",
                Name = "vs_write_file",
                ArgumentsJson = "{\"path\":\"now to create SJWeb/App.csproj\",\"content\":\"project body\",\"overwrite\":true}"
            }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(1, calls.Count);
        using JsonDocument document = JsonDocument.Parse(calls[0].ArgumentsJson);
        Assert.AreEqual("SJWeb/App.csproj", document.RootElement.GetProperty("path").GetString());
    }

    [TestMethod]
    public void RegressiveRepeatedStreamedWriteIsSuppressedWithinSameUserTurn()
    {
        const string request = """
            {"messages":[
              {"role":"user","content":"Use vs_write_file now to create SJWeb/App.csproj with generated content."},
              {"role":"assistant","tool_calls":[{"id":"call_first","type":"function","function":{"name":"vs_write_file","arguments":"{\"path\":\"SJWeb/App.csproj\",\"content\":\"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789\",\"overwrite\":true}"}}]},
              {"role":"tool","tool_call_id":"call_first","content":"vs_write_file wrote 62 chars to \\SJWeb\\App.csproj."}
            ],"tools":[{"type":"function","function":{"name":"vs_write_file"}}]}
            """;
        var calls = new List<ToolCallData>
        {
            new()
            {
                Id = "call_regressive",
                Name = "vs_write_file",
                ArgumentsJson = "{\"path\":\"now to create SJWeb/App.csproj\",\"content\":\"abc\",\"overwrite\":true}"
            }
        };

        NormalizeProxyToolCallsForRequest(calls, request);

        Assert.AreEqual(0, calls.Count);
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

        string rewritten = ApplyChatUiSteeringToRequest(requestBody, "Focus the answer on the heirowLLM web UI.");

        using JsonDocument document = JsonDocument.Parse(rewritten);
        JsonElement root = document.RootElement;
        Assert.AreEqual("qwen-tools", root.GetProperty("model").GetString());
        Assert.IsTrue(root.TryGetProperty("tools", out _));

        List<JsonElement> messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.AreEqual(4, messages.Count);
        Assert.AreEqual("system", messages[2].GetProperty("role").GetString());
        StringAssert.Contains(messages[2].GetProperty("content").GetString(), "heirowLLM conversation steering");
        Assert.AreEqual("user", messages[3].GetProperty("role").GetString());
        StringAssert.Contains(messages[3].GetProperty("content").GetString(), "Focus the answer on the heirowLLM web UI.");
    }

    [TestMethod]
    public void ChatUiSteeringBeforeStreamRegistrationIsRejected()
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
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
        var method = typeof(HeirowLlm).GetMethod(
            "PromptLikelyNeedsProxyTools",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptLikelyNeedsProxyTools should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool PromptRequestsProxyFileWrite(string prompt)
    {
        var method = typeof(HeirowLlm).GetMethod(
            "PromptRequestsProxyFileWrite",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "PromptRequestsProxyFileWrite should remain available for routing tests.");
        return (bool)method!.Invoke(null, new object[] { prompt })!;
    }

    private static string ExtractLikelyRequestedFileTarget(string prompt)
    {
        var method = typeof(HeirowLlm).GetMethod(
            "ExtractLikelyRequestedFileTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method, "ExtractLikelyRequestedFileTarget should remain available for routing tests.");
        return (string)method!.Invoke(null, new object[] { prompt })!;
    }

    private static bool TryExtractExactRequestedFileContent(string prompt, out string content)
    {
        var method = typeof(HeirowLlm).GetMethod(
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
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        var method = typeof(HeirowLlm).GetMethod(
            "ApplyChatUiSteeringToRequest",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ApplyChatUiSteeringToRequest should remain available for steering tests.");
        return (string)method!.Invoke(proxy, new object[] { requestBody, steering })!;
    }

    private static List<ToolCallData> ExtractLooseProxyToolCalls(string content)
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        var method = typeof(HeirowLlm).GetMethod(
            "ExtractLooseProxyToolCalls",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ExtractLooseProxyToolCalls should remain available for tool-call steering tests.");
        return (List<ToolCallData>)method!.Invoke(proxy, new object[] { content })!;
    }

    private static void NormalizeProxyToolCallsForRequest(List<ToolCallData> calls, string requestBody)
    {
        using var proxy = new HeirowLlm("127.0.0.1", 11434, 11435);
        MethodInfo? method = typeof(HeirowLlm).GetMethod("NormalizeProxyToolCallsForRequest", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        method!.Invoke(proxy, new object[] { calls, requestBody });
    }

    private static bool AddActiveChatStreamSteering(HeirowLlm proxy, string ownerKey, string streamId, string sessionId, string steering)
    {
        var method = typeof(HeirowLlm).GetMethod(
            "AddActiveChatStreamSteering",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "AddActiveChatStreamSteering should remain available for steering tests.");
        return (bool)method!.Invoke(proxy, new object[] { ownerKey, streamId, sessionId, steering })!;
    }

    private static object RegisterActiveChatStreamCancellation(HeirowLlm proxy, string ownerKey, string streamId, string sessionId, bool jackhammerEnabled = true)
    {
        var method = typeof(HeirowLlm).GetMethod(
            "RegisterActiveChatStreamCancellation",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "RegisterActiveChatStreamCancellation should remain available for steering tests.");
        string requestBody = jackhammerEnabled
            ? "{\"jackhammer\":{\"enabled\":true,\"runId\":\"test_run\"}}"
            : "{\"jackhammer\":{\"enabled\":false}}";
        return method!.Invoke(proxy, new object[] { ownerKey, streamId, sessionId, requestBody })!;
    }

    private static bool AcceptActiveChatStreamSteering(HeirowLlm proxy, string ownerKey, string streamId, string sessionId, string steering, string steeringId, out string state)
    {
        var method = typeof(HeirowLlm).GetMethod("AcceptActiveChatStreamSteering", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        object[] args = { ownerKey, streamId, sessionId, steering, steeringId, string.Empty };
        bool accepted = (bool)method!.Invoke(proxy, args)!;
        state = (string)args[5];
        return accepted;
    }

    private static void UnregisterActiveChatStreamCancellation(HeirowLlm proxy, object activeStreamCancellation)
    {
        var method = typeof(HeirowLlm).GetMethod("UnregisterActiveChatStreamCancellation", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        method!.Invoke(proxy, new[] { activeStreamCancellation });
    }

    private static string ConsumeActiveChatStreamSteering(HeirowLlm proxy, object activeStreamCancellation)
    {
        var method = typeof(HeirowLlm).GetMethod(
            "ConsumeActiveChatStreamSteering",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.IsNotNull(method, "ConsumeActiveChatStreamSteering should remain available for steering tests.");
        return (string)method!.Invoke(proxy, new[] { activeStreamCancellation })!;
    }
}
