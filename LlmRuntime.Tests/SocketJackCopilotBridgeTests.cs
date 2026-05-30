using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.CopilotMcpBridge;
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
          "tools": []
        }
        """)!.AsObject();

        JsonObject payload = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(request, "fallback-model");
        JsonArray messages = payload["messages"]!.AsArray();

        Assert.AreEqual("agent", payload["service"]!.ToString());
        Assert.IsNotNull(payload["metadata"]);
        Assert.IsNotNull(payload["tools"]);
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "Visual Studio AI mode: AutoPilot");
        StringAssert.Contains(messages[0]!["content"]!.ToString(), "ME7Tools.sln");
        StringAssert.Contains(messages[1]!["content"]!.ToString(), "Use the open solution context.");
        Assert.AreEqual("user", messages[2]!["role"]!.ToString());
        StringAssert.Contains(messages[2]!["content"]!.ToString(), "FKKVS matrix control");
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
    public void OpenAiAdapterUsesReasoningWhenDeltaHasNoContent()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"delta","content":"","reasoning":"SOCKETJACK"}
        {"type":"delta","content":"","reasoning_content":" BRIDGE"}
        {"type":"delta","content":"","reasoningContent":" OK"}
        {"type":"done"}
        """);

        Assert.AreEqual("SOCKETJACK BRIDGE OK", text);
    }

    [TestMethod]
    public void OpenAiAdapterPreservesStreamingDeltaBoundaryWhitespace()
    {
        Assert.AreEqual(" world", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"token","content":" world"}"""));
        Assert.AreEqual(" ", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"token","content":" "}"""));
        Assert.AreEqual(" from reasoning", SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText("""{"type":"delta","content":"","reasoning_content":" from reasoning"}"""));
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
    public void OpenAiAdapterSuppressesNoAssistantTextErrorEvents()
    {
        string text = SocketJackOpenAiChatAdapter.ExtractAssistantText("""
        {"type":"progress","status":"Processing prompt..."}
        {"type":"error","content":"The selected model stream completed without returning assistant text."}
        """);

        Assert.AreEqual("", text);
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
