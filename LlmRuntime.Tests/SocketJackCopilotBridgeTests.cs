using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.CopilotMcpBridge;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class SocketJackCopilotBridgeTests
{
    [TestMethod]
    public void BridgeOptionsParseHttpProxyMode()
    {
        CopilotBridgeOptions options = CopilotBridgeOptions.Parse(new[]
        {
            "--http-proxy",
            "--server-endpoint",
            "https://socketjack.com/proxy/TitanX",
            "--server-id",
            "TitanX",
            "--server-name",
            "Titan X",
            "--model",
            "qwen-tools",
            "--listen-port",
            "11600",
            "--auth-token",
            "token-value",
            "--auth-user",
            "JACK"
        });

        Assert.AreEqual(CopilotBridgeTransport.HttpProxy, options.Transport);
        Assert.AreEqual("TitanX", options.ServerId);
        Assert.AreEqual("qwen-tools", options.ModelId);
        Assert.AreEqual(11600, options.ListenPort);
        Assert.AreEqual("token-value", options.AuthToken);
        Assert.AreEqual("JACK", options.AuthUserName);
        Assert.AreEqual("https://socketjack.com/proxy/TitanX/", options.ServerEndpoint.ToString());
    }

    [TestMethod]
    public void BridgeSafePathsRejectAbsoluteAndTraversal()
    {
        Assert.IsTrue(SocketJackSafePath.IsSafeGetPath("/api/models"));
        Assert.IsTrue(SocketJackSafePath.IsSafeGetPath("/v1/models"));
        Assert.IsTrue(SocketJackSafePath.IsSafeGetPath("/models"));
        Assert.IsTrue(SocketJackSafePath.IsSafeGetPath("/health"));
        Assert.IsTrue(SocketJackSafePath.IsSafeProxyPath("/chat/completions"));
        Assert.IsFalse(SocketJackSafePath.IsSafeGetPath("https://example.com/api/models"));
        Assert.IsFalse(SocketJackSafePath.IsSafeGetPath("/api/../secret"));
        Assert.IsFalse(SocketJackSafePath.IsSafeGetPath("//example.com"));
    }

    [TestMethod]
    public void BridgeNormalizesRootOpenAiPathsForUpstream()
    {
        Assert.AreEqual("/v1/chat/completions", SocketJackProxyPath.NormalizeForUpstream("/chat/completions"));
        Assert.AreEqual("/v1/models", SocketJackProxyPath.NormalizeForUpstream("/models"));
        Assert.AreEqual("/v1/responses", SocketJackProxyPath.NormalizeForUpstream("/responses"));
        Assert.AreEqual("/v1/chat/completions", SocketJackProxyPath.NormalizeForUpstream("/v1/chat/completions"));
        Assert.IsTrue(SocketJackProxyPath.IsOpenAiResponsesPath("/v1/responses"));
        Assert.IsTrue(SocketJackProxyPath.IsOllamaTagsPath("/api/tags"));
    }

    [TestMethod]
    public void BridgePrefersModelRuntimeOpenAiChatForwardPath()
    {
        FieldInfo? field = typeof(SocketJackModelProxyForwarder).GetField("OpenAiChatForwardPaths", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field);

        string[] paths = (string[])field!.GetValue(null)!;

        Assert.AreEqual("api/model-runtime/v1/chat/completions", paths[0]);
        Assert.AreEqual("v1/chat/completions", paths[1]);
    }

    [TestMethod]
    public void OllamaAdapterBuildsTagsForSelectedModel()
    {
        ProxyResponse response = SocketJackOllamaChatAdapter.BuildTagsResponse("qwen-tools");
        string json = Encoding.UTF8.GetString(response.Body);

        Assert.AreEqual(200, response.StatusCode);
        StringAssert.Contains(json, "\"models\"");
        StringAssert.Contains(json, "\"name\":\"qwen-tools\"");
    }

    [TestMethod]
    public void OpenAiAdapterBuildsChatStreamPayload()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "ping" }
          ],
          "max_completion_tokens": 12,
          "tools": []
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");

        Assert.AreEqual("qwen-tools", payload["model"]!.ToString());
        Assert.AreEqual("12", payload["max_tokens"]!.ToString());
        Assert.AreEqual("agent", payload["service"]!.ToString());
        Assert.AreEqual("user", payload["messages"]!.AsArray()[0]!["role"]!.ToString());
        StringAssert.StartsWith(payload["sessionId"]!.ToString(), "copilot_");
        StringAssert.StartsWith(payload["streamId"]!.ToString(), "copilot_");
    }

    [TestMethod]
    public void OpenAiAdapterFiltersChatStreamToolsForVisualStudioPlanState()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");

        JsonArray tools = payload["tools"]!.AsArray();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("get_files_in_project", tools[0]!["function"]!["name"]!.ToString());
        Assert.AreEqual("required", payload["tool_choice"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterBuildsDirectForwardPayloadForSelectedModel()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "gpt-4o",
          "stream": true,
          "messages": [
            {
              "role": "user",
              "content": "Use this file."
            },
            {
              "role": "system",
              "references": [
                {
                  "fileName": "Program.cs",
                  "relativePath": "src/Program.cs",
                  "content": "Console.WriteLine(\"hi\");"
                }
              ]
            }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        JsonArray messages = payload["messages"]!.AsArray();
        string combined = string.Join("\n", messages.Select(message => message?["content"]?.ToString() ?? ""));

        Assert.AreEqual("qwen-tools", payload["model"]!.ToString());
        StringAssert.Contains(combined, "Use this file.");
        StringAssert.Contains(combined, "Program.cs");
        StringAssert.Contains(combined, "Console.WriteLine");
    }

    [TestMethod]
    public void OpenAiAdapterFiltersModernizationToolsForGeneralPlanImplementation()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "start_modernization", "description": "Start .NET modernization." } },
            { "type": "function", "function": { "name": "get_state", "description": "Get modernization state." } },
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "run_command_in_terminal", "description": "Run terminal command." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update plan progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string forwardedTools = payload["tools"]!.ToJsonString();

        Assert.IsFalse(forwardedTools.Contains("start_modernization", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(forwardedTools.Contains("get_state", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(forwardedTools.Contains("run_command_in_terminal", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(forwardedTools, "get_projects_in_solution");
        Assert.IsFalse(forwardedTools.Contains("get_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(forwardedTools.Contains("replace_string_in_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(forwardedTools.Contains("update_plan_progress", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterKeepsModernizationToolsForUpgradeRequests()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Modernize this .NET project to SDK-style format." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "start_modernization", "description": "Start .NET modernization." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string forwardedTools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(forwardedTools, "start_modernization");
        StringAssert.Contains(forwardedTools, "get_file");
    }

    [TestMethod]
    public void OpenAiAdapterUploadsVisualStudioReferenceFiles()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            {
              "role": "user",
              "content": "Add XML docs to ServerScript."
            },
            {
              "role": "system",
              "references": [
                {
                  "fileName": "ServerScript.cs",
                  "relativePath": "Assets/Scripts/ServerScript.cs",
                  "startLine": 10,
                  "endLine": 14,
                  "content": "public static class ServerScript {\\n    public static void Tick() {}\\n}"
                }
              ]
            }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");
        JsonArray files = payload["files"]!.AsArray();
        JsonObject file = files[0]!.AsObject();

        Assert.AreEqual("agent", payload["service"]!.ToString());
        Assert.AreEqual("ServerScript.cs", file["name"]!.ToString());
        Assert.AreEqual("VisualStudio/Assets/Scripts/ServerScript.cs", file["relativePath"]!.ToString());
        Assert.AreEqual("10", file["startLine"]!.ToString());
        StringAssert.Contains(file["text"]!.ToString(), "ServerScript");
    }

    [TestMethod]
    public void OpenAiAdapterBuildsResponsesPayloadWithVisualStudioModes()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "instructions": "Use the open solution context.",
          "input": [
            {
              "role": "user",
              "content": [
                { "type": "input_text", "text": "Implement the pending FKKVS matrix control." }
              ]
            }
          ],
          "metadata": {
            "interactionMode": "AutoPilot",
            "summary": "ME7Tools.sln is loaded in Visual Studio 2026 Insiders."
          },
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "update_plan_progress",
                "description": "Update plan progress."
              }
            },
            {
              "type": "function",
              "function": {
                "name": "task_complete",
                "description": "Mark the task complete."
              }
            }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");
        JsonArray messages = payload["messages"]!.AsArray();

        Assert.AreEqual("agent", payload["service"]!.ToString());
        Assert.IsNotNull(payload["metadata"]);
        Assert.IsNotNull(payload["tools"]);
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "Visual Studio AI mode: AutoPilot");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "do not write hidden reasoning");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "Choose the next useful tool call directly");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "Use exact file and project paths");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "exact project path returned by get_projects_in_solution");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "do not retry the same tool");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "replace_string_in_file");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "record genuine progress");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "after each successful create_file");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "before calling task_complete");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "Never call task_complete immediately after a failed tool result");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "ME7Tools.sln");
        StringAssert.Contains(messages[1]!["content"]!.ToString(), "Use the open solution context.");
        Assert.AreEqual("user", messages[2]!["role"]!.ToString());
        StringAssert.Contains(messages[2]!["content"]!.ToString(), "FKKVS matrix control");
    }

    [TestMethod]
    public void OpenAiAdapterAddsVisualStudioToolRecoveryGuidance()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"ME7Tools\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "" },
            { "role": "assistant", "content": "The model attempted a tool call but returned malformed JSON. Please retry." },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "create", "type": "function", "function": { "name": "create_file", "arguments": "{\"filePath\":\"Maf Scale\\\\MatrixControl.Vb\",\"content\":\"...\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "create", "content": "File already exists at Maf Scale\\MatrixControl.Vb. This tool cannot be used to edit existing files." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update plan progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));

        StringAssert.Contains(combined, "Recent Visual Studio tool recovery guidance");
        StringAssert.Contains(combined, "exact project path returned by get_projects_in_solution: 'Maf Scale\\ME7Tools.vbproj'");
        StringAssert.Contains(combined, "Do not call create_file for that path again");
        StringAssert.Contains(combined, "replace_string_in_file");
        StringAssert.Contains(combined, "Do not call task_complete");
        StringAssert.Contains(combined, "strict function tool call only");
    }

    [TestMethod]
    public void OpenAiAdapterBlocksTaskCompleteAfterFailedVisualStudioEdit()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "replace", "type": "function", "function": { "name": "replace_string_in_file", "arguments": "{\"filePath\":\"Maf Scale\\\\fkkvs.xaml\",\"oldString\":\"old\",\"newString\":\"new\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "replace", "content": "Did not find a match for the given replace string." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "multi_replace_string_in_file", "description": "Edit files." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update plan progress." } },
            { "type": "function", "function": { "name": "run_build", "description": "Build." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Mark done." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "Do not call task_complete after a failed edit");
        StringAssert.Contains(combined, "use the returned text as the exact oldString");
        Assert.IsFalse(tools.Contains("task_complete", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("update_plan_progress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("run_build", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(tools, "get_file");
        StringAssert.Contains(tools, "replace_string_in_file");
    }

    [TestMethod]
    public void OpenAiAdapterDoesNotOfferCreateAfterVisualStudioFileAlreadyExists()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "# FILE CONTEXT\n```markdown Maf Scale\\plan-implementation-plan.md\n# Implementation Plan\n**Progress**: 0%\n```" },
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "create", "type": "function", "function": { "name": "create_file", "arguments": "{\"filePath\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"content\":\"content\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "create", "content": "File already exists at Maf Scale\\MatrixControl.xaml.vb. This tool cannot be used to edit existing files." },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"startLine\":1,\"endLine\":300}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read", "content": "Partial Public Class MatrixControl\r\nEnd Class" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "multi_replace_string_in_file", "description": "Edit files." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } },
            { "type": "function", "function": { "name": "run_build", "description": "Build." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Complete." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "Do not call create_file for that path again");
        StringAssert.Contains(tools, "replace_string_in_file");
        StringAssert.Contains(tools, "multi_replace_string_in_file");
        Assert.IsFalse(tools.Contains("\"name\":\"create_file\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"update_plan_progress\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"run_build\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"task_complete\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterRequiresToolChoiceForVisualStudioImplementationPlanWork()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "# IDESTATE CONTEXT\nThe user's current file: Maf Scale\\fkkvs.xaml" },
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));

        Assert.AreEqual("required", payload["tool_choice"]!.ToString());
        StringAssert.Contains(combined, "do not ask the user what the plan is");
        StringAssert.Contains(combined, "locate the implementation plan");
        StringAssert.Contains(combined, "Visual Basic WPF/XAML");
    }

    [TestMethod]
    public void OpenAiAdapterRaisesShortAutopilotCompletionBudget()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "max_completion_tokens": 512,
          "metadata": { "interactionMode": "AutoPilot" },
          "messages": [
            { "role": "user", "content": "Implement the plan." }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");
        JsonObject direct = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");

        Assert.AreEqual("8192", payload["max_tokens"]!.ToString());
        Assert.AreEqual("8192", direct["max_tokens"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRaisesVisualStudioToolRequestTimeoutFloor()
    {
        byte[] visualStudioRequest = Encoding.UTF8.GetBytes("""
        {
          "model": "qwen-tools",
          "messages": [
            { "role": "user", "content": "# IDESTATE CONTEXT\nImplement the plan" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution" } },
            { "type": "function", "function": { "name": "replace_string_in_file" } }
          ]
        }
        """);
        byte[] plainRequest = Encoding.UTF8.GetBytes("""{ "model": "qwen-tools", "messages": [{ "role": "user", "content": "hello" }] }""");

        Assert.AreEqual(300, SocketJackOpenAiChatAdapter.GetEffectiveOpenAiStreamTimeoutSeconds(visualStudioRequest, 120));
        Assert.AreEqual(120, SocketJackOpenAiChatAdapter.GetEffectiveOpenAiStreamTimeoutSeconds(plainRequest, 120));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsVisualStudioToolRequest(visualStudioRequest));
        Assert.IsFalse(SocketJackOpenAiChatAdapter.IsVisualStudioToolRequest(plainRequest));
    }

    [TestMethod]
    public void OpenAiAdapterBuildsStructuredToolCallsFromRawVsToolJson()
    {
        var availableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file" };

        bool parsed = SocketJackOpenAiChatAdapter.TryParseAssistantToolCalls("""
        {"tool":"read_file","path":"\\\\VisualStudio\\VisualStudio\\file_abc.md"}
        """, availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls);
        string sse = SocketJackOpenAiChatAdapter.BuildStreamingToolCallsSse("chatcmpl-test", 123, "qwen-tools", toolCalls);

        Assert.IsTrue(parsed);
        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("read_file", toolCalls[0].Name);
        StringAssert.Contains(toolCalls[0].ArgumentsJson, "file_abc.md");
        StringAssert.Contains(sse, "\"tool_calls\"");
        StringAssert.Contains(sse, "\"finish_reason\":\"tool_calls\"");
        Assert.IsFalse(sse.Contains("\"content\":\"{\\\"tool\\\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterExtractsNativeStreamingToolCallDelta()
    {
        var availableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file" };

        bool parsed = SocketJackOpenAiChatAdapter.TryParseOpenAiStreamingToolCalls("""
        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"C:/src/Program.cs\"}"}}]}}]}
        """, availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls);

        Assert.IsTrue(parsed);
        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("call_1", toolCalls[0].Id);
        Assert.AreEqual("read_file", toolCalls[0].Name);
        StringAssert.Contains(toolCalls[0].ArgumentsJson, "Program.cs");
    }

    [TestMethod]
    public void OpenAiAdapterExtractsPlainAssistantRequestedToolCalls()
    {
        var availableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "get_file", "replace_string_in_file" };

        bool parsed = SocketJackOpenAiChatAdapter.TryParseAssistantToolCalls("""
        Assistant requested tool call(s):
        tool_call id=call_3 name=get_file arguments={
          "filename": "Maf Scale\\MatrixControl.xaml.vb",
          "startLine": 1,
          "endLine": 50
        }

        Assistant requested tool call(s):
        tool_call id=call_4 name=run_build arguments={}

        Assistant requested tool call(s):
        tool_call id=call_5 name=replace_string_in_file arguments={
          "filePath": "Maf Scale\\fkkvs.xaml",
          "oldString": "<Grid />",
          "newString": "<local:MatrixControl />"
        }
        """, availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls);

        Assert.IsTrue(parsed);
        Assert.AreEqual(2, toolCalls.Count);
        Assert.AreEqual("call_3", toolCalls[0].Id);
        Assert.AreEqual("get_file", toolCalls[0].Name);
        Assert.AreEqual("replace_string_in_file", toolCalls[1].Name);
        Assert.IsFalse(toolCalls.Any(toolCall => toolCall.Name.Equals("run_build", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void OpenAiAdapterIgnoresSseKeepaliveComments()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        : proxy keepalive; phase=processing_prompt; progress=1%
        data: {"choices":[{"delta":{"content":"done"}}]}
        data: [DONE]
        """);

        Assert.AreEqual("done", text);
    }

    [TestMethod]
    public void OpenAiAdapterBuildsResponsesToolCallStream()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "read_file",
                ArgumentsJson = "{\"path\":\"Project.csproj\"}"
            }
        };

        string sse = SocketJackOpenAiChatAdapter.BuildResponseToolCallsSse("resp_test", 123, "qwen-tools", toolCalls);

        StringAssert.Contains(sse, "event: response.output_item.added");
        StringAssert.Contains(sse, "\"type\":\"function_call\"");
        StringAssert.Contains(sse, "\"call_id\":\"call_1\"");
        StringAssert.Contains(sse, "\"name\":\"read_file\"");
        StringAssert.Contains(sse, "event: response.function_call_arguments.delta");
        StringAssert.Contains(sse, "event: response.function_call_arguments.done");
        StringAssert.Contains(sse, "event: response.completed");
        StringAssert.Contains(sse, "data: [DONE]");
    }

    [TestMethod]
    public void OpenAiAdapterBuildsResponsesToolCallStreamWithoutBlankMessagePrelude()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_projects_in_solution",
                ArgumentsJson = "{}"
            }
        };

        string sse =
            SocketJackOpenAiChatAdapter.BuildResponseLifecycleStartSse("resp_test", 123, "qwen-tools") +
            SocketJackOpenAiChatAdapter.BuildResponseToolCallsSse("resp_test", 123, "qwen-tools", toolCalls, includeLifecycle: false);

        StringAssert.Contains(sse, "event: response.created");
        StringAssert.Contains(sse, "\"type\":\"function_call\"");
        StringAssert.Contains(sse, "\"call_id\":\"call_1\"");
        Assert.IsFalse(sse.Contains("\"type\":\"message\"", StringComparison.Ordinal), sse);
        Assert.IsFalse(sse.Contains("response.content_part.added", StringComparison.Ordinal), sse);
        Assert.IsFalse(sse.Contains("response.output_text.delta", StringComparison.Ordinal), sse);
    }

    [TestMethod]
    public void OpenAiAdapterAssignsUniqueVisualStudioToolCallIds()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"MatrixControl.xaml.vb\"}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "file_search",
                ArgumentsJson = "{\"queries\":[\"plan\"],\"maxResults\":5}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.AssignUniqueVisualStudioToolCallIds(toolCalls, "chatcmpl-test-turn");

        Assert.AreEqual(2, normalized.Count);
        Assert.AreNotEqual("call_1", normalized[0].Id);
        Assert.AreNotEqual(normalized[0].Id, normalized[1].Id);
        StringAssert.StartsWith(normalized[0].Id, "call_sj_");
        Assert.AreEqual("get_file", normalized[0].Name);
        Assert.AreEqual(toolCalls[0].ArgumentsJson, normalized[0].ArgumentsJson);
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesInvalidVsGetFileStartLine()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"Maf Scale\\\\fkkvs.xaml\",\"startLine\":0,\"endLine\":-1}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "\"startLine\":1");
        StringAssert.Contains(normalized[0].ArgumentsJson, "\"endLine\":-1");
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesRootAbsoluteVsFilePathIntoProjectFolder()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"C:\\\\Users\\\\Vin\\\\source\\\\repos\\\\Maf Scale\\\\MatrixControl.xaml.vb\",\"startLine\":1,\"endLine\":500}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\MatrixControl.xaml.vb");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("C:\\\\Users", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesShortVsProjectPath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\"ME7Tools\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\ME7Tools.vbproj");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("\"projectPath\":\"ME7Tools\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesVsSolutionFolderProjectPath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "folder",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\"Maf Scale\"}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "dot",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\".\"}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "folderAndStem",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\"Maf Scale\\\\ME7Tools\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\ME7Tools.vbproj");
        StringAssert.Contains(normalized[1].ArgumentsJson, "Maf Scale\\\\ME7Tools.vbproj");
        StringAssert.Contains(normalized[2].ArgumentsJson, "Maf Scale\\\\ME7Tools.vbproj");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("\"projectPath\":\"Maf Scale\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[1].ArgumentsJson.Contains("\"projectPath\":\".\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[2].ArgumentsJson.Contains("\"projectPath\":\"Maf Scale\\\\ME7Tools\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesParentRelativeVsProjectFolderPath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\"..\\\\..\\\\..\\\\Maf Scale\\\\ME7Tools\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        JsonObject arguments = JsonNode.Parse(normalized[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\ME7Tools.vbproj", arguments["projectPath"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesRootedVsSolutionFolderProjectPath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "root",
                Name = "get_files_in_project",
                ArgumentsJson = "{\"projectPath\":\"C:\\\\Users\\\\Vin\\\\source\\\\repos\\\\Maf Scale\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\ME7Tools.vbproj");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("C:\\\\Users", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterExpandsVisualBasicMacroFileSearchQueries()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "file_search",
                ArgumentsJson = "{\"queries\":[\"fkkvs.Vba\",\"MatrixControl.Vba\"],\"maxResults\":10}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "fkkvs.xaml.vb");
        StringAssert.Contains(normalized[0].ArgumentsJson, "fkkvs.xaml");
        StringAssert.Contains(normalized[0].ArgumentsJson, "MatrixControl.xaml.vb");
        StringAssert.Contains(normalized[0].ArgumentsJson, "MatrixControl.xaml");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("fkkvs.Vba", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("MatrixControl.Vba", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("fkkvs.vb", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterExpandsVisualStudioWildcardFileSearchQueries()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "file_search",
                ArgumentsJson = "{\"queries\":[\"*.xaml\",\"*.xaml.vb\"],\"maxResults\":20}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, ".xaml");
        StringAssert.Contains(normalized[0].ArgumentsJson, "xaml");
        StringAssert.Contains(normalized[0].ArgumentsJson, ".xaml.vb");
        StringAssert.Contains(normalized[0].ArgumentsJson, "xaml.vb");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("*.xaml", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesVisualStudioGrepIncludePatternArray()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "grep",
                Name = "grep_search",
                ArgumentsJson = "{\"query\":\"Matrix\",\"isRegexp\":false,\"includePattern\":[\"*.vb\",\"*.xaml\"],\"maxResults\":50}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "\"includePattern\":null");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("[\"*.vb\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesVisualStudioPlanStatusAndMacroFilePath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "create",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"Maf Scale\\\\MatrixControl.Vb\",\"startLine\":1}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "create",
                Name = "create_file",
                ArgumentsJson = "{\"filePath\":\"fkkvs.Vba\",\"content\":\"content\"}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "progress",
                Name = "update_plan_progress",
                ArgumentsJson = "{\"stepId\":\"step\",\"status\":\"In Progress\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "MatrixControl.xaml.vb");
        StringAssert.Contains(normalized[1].ArgumentsJson, "fkkvs.xaml.vb");
        StringAssert.Contains(normalized[2].ArgumentsJson, "in-progress");
    }

    [TestMethod]
    public void OpenAiAdapterPrefixesUnqualifiedVsFilePathsWithProjectDirectory()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "read",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"fkkvs.xaml.vb\",\"startLine\":1,\"endLine\":60}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "create",
                Name = "create_file",
                ArgumentsJson = "{\"filePath\":\"MatrixControl.xaml.vb\",\"content\":\"content\"}"
            },
            new SocketJackOpenAiToolCall
            {
                Id = "replace",
                Name = "multi_replace_string_in_file",
                ArgumentsJson = "{\"replacements\":[{\"filePath\":\"fkkvs.Vba\",\"oldString\":\"old\",\"newString\":\"new\",\"explanation\":\"test\"}],\"explanation\":\"test\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\fkkvs.xaml.vb");
        StringAssert.Contains(normalized[1].ArgumentsJson, "Maf Scale\\\\MatrixControl.xaml.vb");
        StringAssert.Contains(normalized[2].ArgumentsJson, "Maf Scale\\\\fkkvs.xaml.vb");
    }

    [TestMethod]
    public void OpenAiAdapterCanTreatLowercaseVbAsXamlCodeBehindForWpfPlans()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "read",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"MatrixControl.vb\",\"startLine\":1,\"endLine\":80}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(
            toolCalls,
            "Maf Scale\\ME7Tools.vbproj",
            preferVisualStudioXamlCodeBehindForVbFiles: true);

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\MatrixControl.xaml.vb");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("MatrixControl.vb", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterLeavesLowercaseVbAloneWithoutWpfPlanPreference()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "read",
                Name = "get_file",
                ArgumentsJson = "{\"filename\":\"MatrixControl.vb\",\"startLine\":1,\"endLine\":80}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj");

        StringAssert.Contains(normalized[0].ArgumentsJson, "Maf Scale\\\\MatrixControl.vb");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("MatrixControl.xaml.vb", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterExpandsUppercaseVbSearchQueriesToXamlCodeBehind()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "file_search",
                ArgumentsJson = "{\"queries\":[\"MatrixControl.Vb\",\"fkkvs.xaml.Vb\"],\"maxResults\":10}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "MatrixControl.xaml.vb");
        StringAssert.Contains(normalized[0].ArgumentsJson, "MatrixControl.xaml");
        StringAssert.Contains(normalized[0].ArgumentsJson, "fkkvs.xaml.vb");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("MatrixControl.Vb", StringComparison.Ordinal));
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("fkkvs.xaml.Vb", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterGuidesWpfPlansToXamlCodeBehindSearchResults()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "# FILE CONTEXT\n```markdown Maf Scale\\plan-implementation-plan.md\nCreate VBA file for MatrixControl.xaml.Vb and update fkkvs.xaml.\n```" },
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "search", "type": "function", "function": { "name": "file_search", "arguments": "{\"queries\":[\"MatrixControl\"],\"maxResults\":10}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "search", "content": "Maf Scale\\MatrixControl.vb\r\nMaf Scale\\MatrixControl.xaml\r\nMaf Scale\\MatrixControl.xaml.vb\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(Encoding.UTF8.GetBytes(request.ToJsonString())));
        StringAssert.Contains(combined, "WPF XAML/code-behind paths");
        StringAssert.Contains(combined, "Maf Scale\\MatrixControl.xaml.vb");
        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("replace_string_in_file", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterGuidesImplementationPlanReadsAfterProjectFileList()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "# FILE CONTEXT\n``` MatrixControl.Vb\n<VBA Project>\n```" },
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\App.config\r\nMaf Scale\\MatrixControl.xaml.vb\r\nMaf Scale\\plan-implementation-plan.md\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "implementation plan file");
        StringAssert.Contains(combined, "Maf Scale\\plan-implementation-plan.md");
        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("replace_string_in_file", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterInfersWpfPlanPreferenceFromToolResults()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "# FILE CONTEXT\n``` MatrixControl.Vb\n<VBA Project>\n```" },
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\MatrixControl.vb\r\nMaf Scale\\MatrixControl.xaml\r\nMaf Scale\\MatrixControl.xaml.vb\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(Encoding.UTF8.GetBytes(request.ToJsonString())));
    }

    [TestMethod]
    public void OpenAiAdapterKeepsDiscoveryToolsAfterEmptyVisualStudioFileSearch()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj\r\n" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "search", "type": "function", "function": { "name": "file_search", "arguments": "{\"queries\":[\"fkkvs.Vba\",\"MatrixControl.Vba\"],\"maxResults\":10}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "search", "content": "\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "file_search returned no results");
        StringAssert.Contains(combined, "macro-style file names");
        StringAssert.Contains(tools, "get_files_in_project");
        StringAssert.Contains(tools, "grep_search");
        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("create_file", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterRequiresReadAfterVisualStudioSearchResultsBeforeWrites()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "search", "type": "function", "function": { "name": "file_search", "arguments": "{\"queries\":[\"fkkvs.xaml\"],\"maxResults\":10}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "search", "content": "Maf Scale\\fkkvs.xaml\r\nMaf Scale\\fkkvs.xaml.vb\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "call get_file for the exact target file before creating or editing files");
        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("file_search", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("grep_search", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("create_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("replace_string_in_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("update_plan_progress", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterRequiresReadAfterVisualStudioProjectFileList()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\fkkvs.xaml\r\nMaf Scale\\fkkvs.xaml.vb\r\nMaf Scale\\MatrixControl.xaml.vb\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("file_search", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("grep_search", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("get_files_in_project", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("create_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("replace_string_in_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("update_plan_progress", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterRequiresProjectFileListAfterProjectsForImplementationPlan()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string[] toolNames = payload["tools"]!.AsArray()
            .Select(tool => tool?["function"]?["name"]?.ToString() ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        CollectionAssert.Contains(toolNames, "get_files_in_project");
        CollectionAssert.DoesNotContain(toolNames, "get_file");
        CollectionAssert.DoesNotContain(toolNames, "file_search");
        CollectionAssert.DoesNotContain(toolNames, "grep_search");
    }

    [TestMethod]
    public void OpenAiAdapterStopsRepeatedVisualStudioGetFileReads()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read1", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\MatrixControl.vb\",\"startLine\":1,\"endLine\":-1}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read1", "content": "Public Class MatrixControlWindow\r\nEnd Class" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read2", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\MatrixControl.vb\",\"startLine\":1,\"endLine\":-1}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read2", "content": "Public Class MatrixControlWindow\r\nEnd Class" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "multi_replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } },
            { "type": "function", "function": { "name": "run_build", "description": "Build." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Complete." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "already returned content");
        StringAssert.Contains(tools, "replace_string_in_file");
        Assert.IsFalse(tools.Contains("\"name\":\"get_file\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"create_file\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"update_plan_progress\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"run_build\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"task_complete\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterTreatsEmptyVisualStudioGetFileAsFailedRead()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj\r\n" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"fkkvs.xaml.vb\",\"startLine\":1,\"endLine\":100}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read", "content": "```visualbasic fkkvs.xaml.vb\r\n\r\n```" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "file_search", "description": "Search files." } },
            { "type": "function", "function": { "name": "grep_search", "description": "Search text." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "get_file returned no content");
        StringAssert.Contains(tools, "get_files_in_project");
        StringAssert.Contains(tools, "file_search");
        StringAssert.Contains(tools, "get_file");
        Assert.IsFalse(tools.Contains("create_file", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("update_plan_progress", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterRequiresVisualStudioPlanProgressAfterSuccessfulMutation()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "create", "type": "function", "function": { "name": "create_file", "arguments": "{\"filePath\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"content\":\"content\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "create", "content": "File created at Maf Scale\\MatrixControl.xaml.vb." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "multi_replace_string_in_file", "description": "Edit files." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } },
            { "type": "function", "function": { "name": "run_build", "description": "Build." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Complete." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(tools, "update_plan_progress");
        Assert.IsFalse(tools.Contains("\"name\":\"get_file\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"replace_string_in_file\"", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tools.Contains("\"name\":\"task_complete\"", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterBlocksTaskCompleteUntilImplementationWritesAFile()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "build", "type": "function", "function": { "name": "run_build", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "build", "content": "Build successful\r\n" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Mark done." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(tools, "get_file");
        StringAssert.Contains(tools, "replace_string_in_file");
        Assert.IsFalse(tools.Contains("task_complete", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterStripsLeakedReasoningFromVsFileWriteArguments()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "create_file",
                ArgumentsJson = "{\"filePath\":\"Maf Scale\\\\MatrixControl.Vb\",\"content\":\"Public Class MatrixControl\\n<think>\\nI need to read another file first.\\n</think>\\nAssistant requested tool call(s):\\ntool_call\",\"newString\":\"Public Class Replacement\\n<think>\\nThe previous tool call failed because I used the wrong file.\\n\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        StringAssert.Contains(normalized[0].ArgumentsJson, "Public Class MatrixControl");
        StringAssert.Contains(normalized[0].ArgumentsJson, "Public Class Replacement");
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("<think>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("Assistant requested tool", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(normalized[0].ArgumentsJson.Contains("previous tool call", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void OpenAiAdapterConvertsNoOpVisualStudioReplaceToRead()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "replace",
                Name = "replace_string_in_file",
                ArgumentsJson = "{\"filePath\":\"fkkvs.xaml.vb\",\"oldString\":\"\",\"newString\":\"\"}"
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls);

        Assert.AreEqual("replace", normalized[0].Id);
        Assert.AreEqual("get_file", normalized[0].Name);
        StringAssert.Contains(normalized[0].ArgumentsJson, "\"filename\":\"fkkvs.xaml.vb\"");
        StringAssert.Contains(normalized[0].ArgumentsJson, "\"startLine\":1");
    }

    [TestMethod]
    public void OpenAiAdapterDoesNotCountNoOpVisualStudioReplaceAsFileMutation()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "replace", "type": "function", "function": { "name": "replace_string_in_file", "arguments": "{\"filePath\":\"fkkvs.xaml.vb\",\"oldString\":\"\",\"newString\":\"\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "replace", "content": "Successfully replaced text in fkkvs.xaml.vb" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_file", "description": "Read a file." } },
            { "type": "function", "function": { "name": "replace_string_in_file", "description": "Edit a file." } },
            { "type": "function", "function": { "name": "multi_replace_string_in_file", "description": "Edit files." } },
            { "type": "function", "function": { "name": "update_plan_progress", "description": "Update progress." } },
            { "type": "function", "function": { "name": "run_build", "description": "Build." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Mark done." } }
          ]
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "qwen-tools");
        string combined = string.Join("\n", payload["messages"]!.AsArray().Select(message => message?["content"]?.ToString() ?? ""));
        string tools = payload["tools"]!.ToJsonString();

        StringAssert.Contains(combined, "was a no-op");
        StringAssert.Contains(combined, "Do not count that as a file edit");
        StringAssert.Contains(tools, "get_file");
        StringAssert.Contains(tools, "replace_string_in_file");
        Assert.IsFalse(tools.Contains("task_complete", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ToolCallAccumulatorBuffersSplitRawToolJson()
    {
        var accumulator = new SocketJackToolCallTextAccumulator(new[] { "read_file" });

        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, accumulator.Accept("{\"tool\":\"read_", out _, out _));
        SocketJackToolCallConsumeResult result = accumulator.Accept("file\",\"args\":{\"path\":\"C:/src/Program.cs\"}}", out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, out _);

        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, result);
        Assert.IsTrue(accumulator.TryComplete(out toolCalls));
        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("read_file", toolCalls[0].Name);
        StringAssert.Contains(toolCalls[0].ArgumentsJson, "Program.cs");
    }

    [TestMethod]
    public void ToolCallAccumulatorKeepsWholeRawToolBatchUntilDone()
    {
        var accumulator = new SocketJackToolCallTextAccumulator(new[] { "read_file" });

        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, accumulator.Accept("{\"tool\":\"read_file\",\"path\":\"C:/one.cs\"}", out _, out string passThrough));
        Assert.AreEqual("", passThrough);
        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, accumulator.Accept("\n{\"tool\":\"read_file\",\"path\":\"C:/two.cs\"}", out _, out passThrough));
        Assert.AreEqual("", passThrough);

        Assert.IsTrue(accumulator.TryComplete(out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));
        Assert.AreEqual(2, toolCalls.Count);
        StringAssert.Contains(toolCalls[0].ArgumentsJson, "one.cs");
        StringAssert.Contains(toolCalls[1].ArgumentsJson, "two.cs");
    }

    [TestMethod]
    public void ToolCallAccumulatorBuffersPlainTranscriptWithShortPreface()
    {
        var accumulator = new SocketJackToolCallTextAccumulator(new[] { "get_file" });

        SocketJackToolCallConsumeResult result = accumulator.Accept(
            "Next I will inspect the file.\nAssistant requested tool call(s):\ntool_call id=call_1 name=get_file arguments={\"filename\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"startLine\":1,\"endLine\":80}",
            out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls,
            out string passThrough);

        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, result);
        Assert.AreEqual("", passThrough);
        Assert.IsTrue(accumulator.TryComplete(out toolCalls));
        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_file", toolCalls[0].Name);
        StringAssert.Contains(toolCalls[0].ArgumentsJson, "MatrixControl.xaml.vb");
    }

    [TestMethod]
    public void ToolCallAccumulatorSuppressesUnavailablePlainToolTranscript()
    {
        var accumulator = new SocketJackToolCallTextAccumulator(new[] { "replace_string_in_file" });

        SocketJackToolCallConsumeResult result = accumulator.Accept(
            """

            Assistant requested tool call(s):
            tool_call id=call_7 name=get_file arguments={
              "filename": "Maf Scale\\MatrixControl.xaml",
              "startLine": 1,
              "endLine": 200
            }

            ```xaml
            <UserControl />
            ```
            """,
            out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls,
            out string passThrough);

        Assert.AreEqual(SocketJackToolCallConsumeResult.Buffered, result);
        Assert.AreEqual("", passThrough);
        Assert.IsFalse(accumulator.TryComplete(out toolCalls));
    }

    [TestMethod]
    public void OpenAiAdapterSuppressesOrphanToolJsonTailFragments()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        data: {"choices":[{"delta":{"content":"\"}\"}\"}\"}"}}]}
        data: [DONE]
        """);

        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void OpenAiAdapterConvertsChatStreamEventsToSse()
    {
        JsonObject request = JsonNode.Parse("""{ "model": "qwen-tools", "stream": true }""")!.AsObject();
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"progress","status":"loading"}
        {"type":"token","content":"pong"}
        {"type":"done"}
        """);

        ProxyResponse response = SocketJackOpenAiChatAdapter.BuildStreamingChatResponse(request, text, "fallback-model");
        string body = Encoding.UTF8.GetString(response.Body);

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.ContentType);
        StringAssert.Contains(body, "chat.completion.chunk");
        StringAssert.Contains(body, "pong");
        StringAssert.Contains(body, "[DONE]");
    }

    [TestMethod]
    public void BridgeChunkFrameFilterOnlyMatchesHexLengthMarkers()
    {
        MethodInfo method = typeof(SocketJackModelProxyForwarder).GetMethod("IsHttpChunkSizeLine", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("IsHttpChunkSizeLine helper was not found.");
        bool IsHttpChunkSizeLine(string text) => (bool)method.Invoke(null, new object?[] { text })!;

        Assert.IsTrue(IsHttpChunkSizeLine("4C4"));
        Assert.IsTrue(IsHttpChunkSizeLine("3c1"));
        Assert.IsTrue(IsHttpChunkSizeLine("4AF;chunk-extension=true"));
        Assert.IsFalse(IsHttpChunkSizeLine("100"));
        Assert.IsFalse(IsHttpChunkSizeLine("bad"));
        Assert.IsFalse(IsHttpChunkSizeLine("visible-ok"));
    }

    [TestMethod]
    public void OpenAiAdapterSuppressesReasoningWhenDeltaHasNoContent()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"delta","content":"","reasoning":"SOCKETJACK"}
        {"type":"delta","content":"","reasoning_content":" BRIDGE"}
        {"type":"delta","content":"","reasoningContent":" OK"}
        {"type":"done"}
        """);

        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void OpenAiAdapterPreservesStreamingDeltaBoundaryWhitespace()
    {
        Assert.AreEqual(" world", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"token","content":" world"}"""));
        Assert.AreEqual(" ", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"token","content":" "}"""));
        Assert.AreEqual("", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"delta","content":"","reasoning_content":" from reasoning"}"""));
        Assert.AreEqual("visible", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"delta","content":"visible</thought>"}"""));
    }

    [TestMethod]
    public void OpenAiAdapterPreservesOpenAiStreamingDeltaBoundaryWhitespace()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""
        data: {"choices":[{"delta":{"content":" world"}}]}
        """);

        Assert.AreEqual(" world", text);
    }

    [TestMethod]
    public void OpenAiAdapterDoesNotDuplicateNestedAssistantContent()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""
        {"type":"delta","message":{"content":"Implementation Progress Report"},"content":"Implementation Progress Report"}
        """);

        Assert.AreEqual("Implementation Progress Report", text);
    }

    [TestMethod]
    public void OpenAiAdapterCollapsesCumulativeResponseSnapshots()
    {
        string final = "Implementation Progress Report\nCurrent Status: READY TO CONTINUE\nPlease respond with your preferred option.";
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"delta","content":"Implementation Progress Report\n"}
        {"type":"delta","content":"Implementation Progress Report\nCurrent Status: READY TO CONTINUE\n"}
        {"type":"delta","content":"Implementation Progress Report\nCurrent Status: READY TO CONTINUE\nPlease respond with your preferred option.</markdown>"}
        {"type":"delta","content":"Implementation Progress Report\nCurrent Status: READY TO CONTINUE\nPlease respond with your preferred option.</markdown>"}
        {"type":"done"}
        """);

        Assert.AreEqual(final, text);
    }

    [TestMethod]
    public void AssistantTextAccumulatorStreamsOnlySnapshotSuffixes()
    {
        var accumulator = new SocketJackAssistantTextAccumulator();

        Assert.AreEqual("Implementation Progress Report\n", accumulator.AcceptDeltaOrSnapshot("Implementation Progress Report\n"));
        Assert.AreEqual("Current Status: READY TO CONTINUE\n", accumulator.AcceptDeltaOrSnapshot("Implementation Progress Report\nCurrent Status: READY TO CONTINUE\n"));
        Assert.AreEqual("Please respond with your preferred option.", accumulator.AcceptDeltaOrSnapshot("Implementation Progress Report\nCurrent Status: READY TO CONTINUE\nPlease respond with your preferred option."));
        Assert.AreEqual("", accumulator.AcceptDeltaOrSnapshot("Implementation Progress Report\nCurrent Status: READY TO CONTINUE\nPlease respond with your preferred option."));
    }

    [TestMethod]
    public void OpenAiAdapterExtractsFinalMarkdownWhenStreamUsesFinalTags()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"progress","status":"Processing prompt... Still connected after 2s."}
        {"type":"toolCall","toolName":"internet_search","content":"raw tool event"}
        {"type":"token","content":"<auto_final>Done in **Markdown**.</auto_final>"}
        {"type":"done"}
        """);

        Assert.AreEqual("Done in **Markdown**.", text);
    }

    [TestMethod]
    public void OpenAiAdapterExtractsOpenAiSseShape()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        data: {"choices":[{"delta":{"content":"Hello"}}]}

        data: {"choices":[{"delta":{"content":" world"}}]}

        data: [DONE]
        """);

        Assert.AreEqual("Hello world", text);
    }

    [TestMethod]
    public void OpenAiAdapterBuildsResponsesApiOutputShapes()
    {
        JsonObject request = JsonNode.Parse("""{ "model": "qwen-tools", "stream": true }""")!.AsObject();

        ProxyResponse response = SocketJackOpenAiChatAdapter.BuildResponseResponse(request, "done", "fallback-model");
        string json = Encoding.UTF8.GetString(response.Body);
        StringAssert.Contains(json, "\"object\":\"response\"");
        StringAssert.Contains(json, "\"output_text\":\"done\"");

        ProxyResponse stream = SocketJackOpenAiChatAdapter.BuildStreamingResponseResponse(request, "done", "fallback-model");
        string body = Encoding.UTF8.GetString(stream.Body);
        StringAssert.Contains(body, "event: response.created");
        StringAssert.Contains(body, "event: response.output_text.delta");
        StringAssert.Contains(body, "\"delta\":\"done\"");
        StringAssert.Contains(body, "[DONE]");
    }

    [TestMethod]
    public void OpenAiAdapterCarriesResponsesTextInTerminalEvents()
    {
        JsonObject request = JsonNode.Parse("""{ "model": "qwen-tools", "stream": true }""")!.AsObject();
        string marker = "unique-visible-response-text";

        ProxyResponse stream = SocketJackOpenAiChatAdapter.BuildStreamingResponseResponse(request, marker, "fallback-model");
        string body = Encoding.UTF8.GetString(stream.Body);
        int occurrences = 0;
        int index = -marker.Length;
        while ((index = body.IndexOf(marker, index + marker.Length, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
        }

        Assert.IsTrue(occurrences > 1, body);
        StringAssert.Contains(body, "event: response.output_text.delta");
        StringAssert.Contains(body, "event: response.output_text.done");
        StringAssert.Contains(body, "\"text\":\"unique-visible-response-text\"");
        StringAssert.Contains(body, "\"output_text\":\"unique-visible-response-text\"");
        StringAssert.Contains(body, "event: response.completed");
        StringAssert.Contains(body, "[DONE]");
    }

    [TestMethod]
    public void OpenAiAdapterBuildsFriendlyOfflineMessage()
    {
        Assert.IsTrue(SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(503, "JackLLM has not connected its reverse agent."));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(503, "The selected model endpoint returned HTTP 503 for v1/chat/completions."));

        string message = SocketJackOpenAiChatAdapter.BuildServerOfflineAssistantText("sable");

        StringAssert.StartsWith(message, "Sable server is offline, please choose a different server.");
        StringAssert.Contains(message, "Extensions > SocketJack > Copilot Servers");
        Assert.IsFalse(message.Contains("[SocketJack]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OpenAiAdapterSuppressesNoAssistantTextErrorEvents()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"progress","status":"Processing prompt..."}
        {"type":"error","content":"The selected model stream completed without returning assistant text."}
        """);

        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void OpenAiAdapterRecognizesDoneOnlyStreamsAsEmpty()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        data: {"choices":[{"delta":{"role":"assistant"}}]}

        data: [DONE]
        """);

        Assert.AreEqual("", text);
    }

    [TestMethod]
    public void OpenAiAdapterFallbackIsVisibleMarkdownText()
    {
        string fallback = SocketJackOpenAiChatAdapter.BuildNoVisibleAssistantTextFallback();
        JsonObject request = JsonNode.Parse("""{ "model": "qwen-tools", "stream": true }""")!.AsObject();
        ProxyResponse response = SocketJackOpenAiChatAdapter.BuildStreamingChatResponse(request, fallback, "fallback-model");
        string body = Encoding.UTF8.GetString(response.Body);

        StringAssert.Contains(fallback, "SocketJack bridge");
        Assert.IsFalse(fallback.Contains("The model finished without a visible reply", StringComparison.Ordinal));
        Assert.IsFalse(fallback.Contains("select another enabled chat model", StringComparison.Ordinal));
        Assert.IsFalse(fallback.TrimStart().StartsWith("{", StringComparison.Ordinal));
        StringAssert.Contains(body, "SocketJack bridge");
        StringAssert.Contains(body, "[DONE]");
    }

    [TestMethod]
    public void OpenAiAdapterRecognizesNoVisibleFallbackForVisualStudioRecovery()
    {
        string fallback = SocketJackOpenAiChatAdapter.BuildNoVisibleAssistantTextFallback();
        string legacyCopilotFallback = "The model finished without a visible reply. Please try again, or select another enabled chat model.";

        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(fallback));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(legacyCopilotFallback));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText("data: {\"choices\":[{\"delta\":{\"content\":\"" + fallback + "\"}}]}"));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText("data: {\"choices\":[{\"delta\":{\"content\":\"" + legacyCopilotFallback + "\"}}]}"));
        Assert.IsFalse(SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText("The model paused before writing a final answer."));
    }

    [TestMethod]
    public void OpenAiAdapterSuppressesInternalProgressAndToolUpdates()
    {
        JsonObject progress = JsonNode.Parse("""{"type":"progress","phase":"prompt_processing","status":"Processing prompt... Still connected after 32s."}""")!.AsObject();
        JsonObject tool = JsonNode.Parse("""{"type":"toolCall","toolName":"download_file","status":"saved README.md"}""")!.AsObject();
        JsonObject service = JsonNode.Parse("""{"type":"serviceAccess","service":"filesystem","status":"allowed"}""")!.AsObject();
        JsonObject rawJsonTool = JsonNode.Parse("""{"type":"toolCall","toolName":"download_file","body":"{\"url\":\"https://example.com\"}"}""")!.AsObject();
        JsonObject usage = JsonNode.Parse("""{"type":"usage","status":"Token usage"}""")!.AsObject();

        Assert.AreEqual("", SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(progress));
        Assert.AreEqual("", SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(tool));
        Assert.AreEqual("", SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(service));
        Assert.AreEqual("", SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(rawJsonTool));
        Assert.AreEqual("", SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(usage));
    }

    [TestMethod]
    public void OpenAiAdapterRecoversMalformedVisualStudioToolAttemptByReadingPlan()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\MatrixControl.xaml.vb\nMaf Scale\\plan-implementation-plan.md" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"startLine\":1,\"endLine\":50,\"includeLineNumbers\":true}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read", "content": "```visualbasic Maf Scale\\MatrixControl.xaml.vb (Lines 1-50 of 191 total. Call tools for more if needed.)\n1: Partial Public Class MatrixControl\n```" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText("The model attempted a tool call but returned malformed JSON. Please retry."));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText("Assistant requested tool call(s):\ntool_call id=call_1\n<tool_result>..."));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText("# Implementation Plan Clarification Needed\n\nThe plan was not specified."));
        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_file", toolCalls[0].Name);
        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\plan-implementation-plan.md", arguments["filename"]!.ToString());
        Assert.AreEqual("1", arguments["startLine"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioPlanByStartingProjectDiscovery()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "system", "content": "You are an AI programming assistant." },
            { "role": "user", "content": "Implement the plan" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
        Assert.AreEqual("{}", toolCalls[0].ArgumentsJson);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioPlanAfterNoVisibleFallbackLoop()
    {
        string socketJackFallback = SocketJackOpenAiChatAdapter.BuildNoVisibleAssistantTextFallback();
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "system", "content": "You are an AI programming assistant." },
            { "role": "user", "content": "# IDESTATE CONTEXT\nUser's solution file is:\nC:\\Users\\Vin\\source\\repos\\Maf Scale\\ME7Tools.sln" },
            { "role": "user", "content": "Implement the plan" },
            { "role": "assistant", "content": "__SOCKETJACK_FALLBACK__" },
            { "role": "user", "content": "You have not yet marked the task as complete using the task_complete tool. If you believe the task is done, call task_complete now. Otherwise, continue working on the task." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Finish." } }
          ],
          "tool_choice": "auto"
        }
        """.Replace("__SOCKETJACK_FALLBACK__", socketJackFallback.Replace("\\", "\\\\").Replace("\"", "\\\"")))!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversGenericVisualStudioToolRequestByStartingProjectDiscovery()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "system", "content": "You are an AI programming assistant." },
            { "role": "user", "content": "# IDESTATE CONTEXT\nUser's solution file is:\nC:\\Users\\Vin\\source\\repos\\Maf Scale\\ME7Tools.sln" },
            { "role": "user", "content": "Look at this solution and keep working." }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Finish." } }
          ],
          "tool_choice": "auto"
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
        Assert.AreEqual("{}", toolCalls[0].ArgumentsJson);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversGenericVisualStudioToolRequestAfterProjectList()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "system", "content": "You are an AI programming assistant." },
            { "role": "user", "content": "# IDESTATE CONTEXT\nUser's solution file is:\nC:\\Users\\Vin\\source\\repos\\Maf Scale\\ME7Tools.sln" },
            { "role": "user", "content": "Look at this solution and keep working." },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_files_in_project", toolCalls[0].Name);
        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\ME7Tools.vbproj", arguments["projectPath"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRequiresToolChoiceForVisualStudioReadmeSummary()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project and save it to readme.md" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create file." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Finish." } }
          ]
        }
        """)!.AsObject();

        JsonObject forward = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "fallback-model");

        Assert.AreEqual("required", forward["tool_choice"]!.ToString());
        JsonArray tools = forward["tools"]!.AsArray();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("get_projects_in_solution", tools[0]!["function"]!["name"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRequiresToolChoiceForVisualStudioSummaryMarkdown()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project to summary.md" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "create_file", "description": "Create file." } },
            { "type": "function", "function": { "name": "task_complete", "description": "Finish." } }
          ]
        }
        """)!.AsObject();

        JsonObject forward = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(request, "fallback-model");

        Assert.AreEqual("required", forward["tool_choice"]!.ToString());
        JsonArray tools = forward["tools"]!.AsArray();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("get_projects_in_solution", tools[0]!["function"]!["name"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioReadmeSummaryByStartingProjectDiscovery()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project and save it to readme.md" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioSummaryMarkdownByStartingProjectDiscovery()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project to summary.md" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioReadmeSummaryAfterProjectListByReadingProjectFiles()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project and save it to readme.md" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_files_in_project", toolCalls[0].Name);
        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\ME7Tools.vbproj", arguments["projectPath"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioReadmeSummaryAfterFileListByReadingProjectFile()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "summarize this project and save it to readme.md" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\fkkvs.xaml.vb\nMaf Scale\\readme.md\nMaf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_file", toolCalls[0].Name);
        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\ME7Tools.vbproj", arguments["filename"]!.ToString());
        Assert.AreEqual("1", arguments["startLine"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioPlanWhenOnlyProjectDiscoveryIsAllowed()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } }
          ],
          "tool_choice": "required"
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_projects_in_solution", toolCalls[0].Name);
    }

    [TestMethod]
    public void OpenAiAdapterRecoversVisualStudioPlanAfterProjectListByReadingProjectFiles()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "projects", "type": "function", "function": { "name": "get_projects_in_solution", "arguments": "{}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "projects", "content": "Maf Scale\\ME7Tools.vbproj" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        Assert.AreEqual(1, toolCalls.Count);
        Assert.AreEqual("get_files_in_project", toolCalls[0].Name);
        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\ME7Tools.vbproj", arguments["projectPath"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterRecoversMalformedVisualStudioToolAttemptWithFileContinuation()
    {
        JsonObject request = JsonNode.Parse("""
        {
          "model": "qwen-tools",
          "stream": true,
          "messages": [
            { "role": "user", "content": "Implement the plan" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "files", "type": "function", "function": { "name": "get_files_in_project", "arguments": "{\"projectPath\":\"Maf Scale\\\\ME7Tools.vbproj\"}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "files", "content": "Maf Scale\\MatrixControl.xaml.vb\nMaf Scale\\plan-implementation-plan.md" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "plan", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\plan-implementation-plan.md\",\"startLine\":1,\"endLine\":120}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "plan", "content": "```markdown Maf Scale\\plan-implementation-plan.md (Lines 1-30 of 30 total.)\n# Plan\n```" },
            {
              "role": "assistant",
              "tool_calls": [
                { "id": "read", "type": "function", "function": { "name": "get_file", "arguments": "{\"filename\":\"Maf Scale\\\\MatrixControl.xaml.vb\",\"startLine\":1,\"endLine\":50,\"includeLineNumbers\":true}" } }
              ]
            },
            { "role": "tool", "tool_call_id": "read", "content": "```visualbasic Maf Scale\\MatrixControl.xaml.vb (Lines 1-50 of 191 total. Call tools for more if needed.)\n1: Partial Public Class MatrixControl\n```" }
          ],
          "tools": [
            { "type": "function", "function": { "name": "get_projects_in_solution", "description": "List projects." } },
            { "type": "function", "function": { "name": "get_file", "description": "Read file." } },
            { "type": "function", "function": { "name": "get_files_in_project", "description": "List files." } }
          ]
        }
        """)!.AsObject();

        Assert.IsTrue(SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(Encoding.UTF8.GetBytes(request.ToJsonString()), out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls));

        JsonObject arguments = JsonNode.Parse(toolCalls[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\MatrixControl.xaml.vb", arguments["filename"]!.ToString());
        Assert.AreEqual("51", arguments["startLine"]!.ToString());
        Assert.AreEqual("130", arguments["endLine"]!.ToString());
    }

    [TestMethod]
    public void OpenAiAdapterNormalizesDuplicateVisualStudioProjectDirectoryFilePath()
    {
        var toolCalls = new[]
        {
            new SocketJackOpenAiToolCall
            {
                Id = "call_1",
                Name = "get_file",
                ArgumentsJson = """{"filename":"Maf Scale\\Maf Scale\\MatrixControl.xaml.vb","startLine":1,"endLine":50}"""
            }
        };

        IReadOnlyList<SocketJackOpenAiToolCall> normalized = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, "Maf Scale\\ME7Tools.vbproj", true);

        JsonObject arguments = JsonNode.Parse(normalized[0].ArgumentsJson)!.AsObject();
        Assert.AreEqual("Maf Scale\\MatrixControl.xaml.vb", arguments["filename"]!.ToString());
    }

    [TestMethod]
    public void WebSocketUriUsesSocketJackProxyPath()
    {
        Uri uri = SocketJackWebChatApiClient.BuildWebSocketUri(new Uri("https://socketjack.com/proxy/TitanX/"));

        Assert.AreEqual("wss://socketjack.com/proxy/TitanX/api/web-chat/ws", uri.ToString());
    }

    [TestMethod]
    public void SecretRedactorMasksTokens()
    {
        string redacted = SocketJackSecretRedactor.Redact("token=abc123&server=TitanX authorization: Bearer xyz");

        StringAssert.Contains(redacted, "token=[redacted]");
        StringAssert.Contains(redacted, "authorization: [redacted]");
        Assert.IsFalse(redacted.Contains("abc123"));
        Assert.IsFalse(redacted.Contains("xyz"));
    }

    [TestMethod]
    public void ToolCatalogContainsCopilotTools()
    {
        CollectionAssert.Contains(CopilotBridgeToolCatalog.ToolNames, "socketjack_copilot_server_status");
        CollectionAssert.Contains(CopilotBridgeToolCatalog.ToolNames, "socketjack_copilot_get_models");
        CollectionAssert.Contains(CopilotBridgeToolCatalog.ToolNames, "socketjack_copilot_get_path");
    }
}
