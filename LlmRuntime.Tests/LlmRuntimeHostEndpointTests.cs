using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmRuntimeHostEndpointTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void ChatRequestFromJson_PreservesToolCallOnlyAssistantAndEmptyToolResult()
    {
        using var document = JsonDocument.Parse("""
        {
          "model": "ToolChat-Q4_0",
          "messages": [
            { "role": "user", "content": "inspect the project" },
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_vs_read",
                "type": "function",
                "function": {
                  "name": "vs_read_file",
                  "arguments": "{\"path\":\"App.xaml.cs\"}"
                }
              }]
            },
            { "role": "tool", "tool_call_id": "call_vs_read", "content": "" }
          ],
          "tools": []
        }
        """);

        LlmChatRequest request = LlmChatRequest.FromJson(document.RootElement);

        Assert.AreEqual(3, request.Messages.Count);
        StringAssert.Contains(request.Messages[1].Content, "vs_read_file");
        StringAssert.Contains(request.Messages[1].Content, "App.xaml.cs");
        StringAssert.Contains(request.Messages[2].Content, "call_vs_read");
        StringAssert.Contains(request.Messages[2].Content, "(empty tool result)");
    }

    [TestMethod]
    public void ChatRequestFromJson_PreservesNonEmptyToolResultCallId()
    {
        using var document = JsonDocument.Parse("""
        {
          "model": "ToolChat-Q4_0",
          "messages": [
            { "role": "user", "content": "inspect the project" },
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_vs_read",
                "type": "function",
                "function": {
                  "name": "vs_read_file",
                  "arguments": "{\"path\":\"App.xaml.cs\"}"
                }
              }]
            },
            { "role": "tool", "tool_call_id": "call_vs_read", "content": "class App {}" }
          ],
          "tools": []
        }
        """);

        LlmChatRequest request = LlmChatRequest.FromJson(document.RootElement);

        StringAssert.Contains(request.Messages[2].Content, "call_vs_read");
        StringAssert.Contains(request.Messages[2].Content, "class App {}");
    }

    [TestMethod]
    public void ChatRequestFromJson_UsesLongAutoBudgetAndAcceptsMaxOutputTokens()
    {
        using var autoDocument = JsonDocument.Parse("""
        {
          "model": "LongAnswer-Q4_0",
          "messages": [{ "role": "user", "content": "explain a complicated topic" }]
        }
        """);
        using var outputDocument = JsonDocument.Parse("""
        {
          "model": "LongAnswer-Q4_0",
          "max_output_tokens": 1234,
          "messages": [{ "role": "user", "content": "short" }]
        }
        """);

        LlmChatRequest autoRequest = LlmChatRequest.FromJson(autoDocument.RootElement);
        LlmChatRequest outputRequest = LlmChatRequest.FromJson(outputDocument.RootElement);

        Assert.AreEqual(LlmChatRequest.DefaultMaxCompletionTokens, autoRequest.MaxTokens);
        Assert.IsFalse(autoRequest.MaxTokensSpecified);
        Assert.AreEqual(1234, outputRequest.MaxTokens);
        Assert.IsTrue(outputRequest.MaxTokensSpecified);
    }

    [TestMethod]
    public void ContextCompression_PreservesLatestUserMessageAndAddsSummary()
    {
        var request = new LlmChatRequest
        {
            MaxTokens = 256,
            Messages = Enumerable.Range(0, 24)
                .SelectMany(index => new[]
                {
                    new LlmChatMessage("user", "older user turn " + index + " " + new string('u', 700)),
                    new LlmChatMessage("assistant", "older assistant turn " + index + " " + new string('a', 700))
                })
                .Append(new LlmChatMessage("user", "latest request: keep this exact task"))
                .ToArray()
        };

        LlmRuntimeHost.ApplyContextCompressionForInference(request, contextLength: 1024);

        Assert.IsTrue(request.Messages.Count < 49);
        StringAssert.Contains(request.Messages[0].Content, "Compressed conversation context");
        Assert.AreEqual("user", request.Messages[^1].Role);
        Assert.AreEqual("latest request: keep this exact task", request.Messages[^1].Content);
    }

    [TestMethod]
    public async Task OpenAiModelsEndpoint_ReturnsList()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Endpoint-Q4_0.gguf"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string body = await client.GetStringAsync($"http://127.0.0.1:{port}/v1/models");
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("list", document.RootElement.GetProperty("object").GetString());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetArrayLength());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task OpenAiModelsEndpoint_FiltersNonChatMediaModels()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Chat-Q4_0.gguf"));
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf"), "wan2");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string openAiBody = await client.GetStringAsync($"http://127.0.0.1:{port}/v1/models");
            using var openAiDocument = JsonDocument.Parse(openAiBody);
            JsonElement openAiModels = openAiDocument.RootElement.GetProperty("data");

            Assert.AreEqual(1, openAiModels.GetArrayLength());
            Assert.AreEqual("Chat-Q4_0", openAiModels[0].GetProperty("id").GetString());

            string nativeBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/models");
            using var nativeDocument = JsonDocument.Parse(nativeBody);
            JsonElement nativeModels = nativeDocument.RootElement.GetProperty("models");
            JsonElement mediaModel = nativeModels.EnumerateArray().Single(model => model.GetProperty("type").GetString() == "video");

            Assert.IsFalse(mediaModel.GetProperty("capabilities").GetProperty("chat_completion").GetBoolean());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task NativeModelsEndpoint_ReturnsModelMetadata()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Native-Q4_0.gguf"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string body = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/models");
            using var document = JsonDocument.Parse(body);

            var model = document.RootElement.GetProperty("models")[0];
            Assert.AreEqual("Native-Q4_0", model.GetProperty("key").GetString());
            Assert.AreEqual("gguf", model.GetProperty("format").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadMissingModel_ReturnsStructuredError()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent("{\"model\":\"missing\"}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("load_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_AcceptsGpuBackendSelection()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Gpu-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("loaded"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent("{\"model\":\"Gpu-Q4_0\",\"backend\":\"vulkan\",\"allow_backend_fallback\":false,\"echo_load_config\":true}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var config = document.RootElement.GetProperty("load_config");
            Assert.AreEqual("vulkan", config.GetProperty("backend").GetString());
            Assert.IsFalse(config.GetProperty("allow_backend_fallback").GetBoolean());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_DirectMlRequiresConfiguredRunner()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "DirectMl-Q4_0.gguf"));
            string isolatedToolRoot = Path.Combine(root, "empty-tools");
            var options = new LlmRuntimeOptions { ModelRoot = root, ToolRoot = isolatedToolRoot, Port = port };
            using var registry = new LlmModelRegistry(options);
            using var host = new LlmRuntimeHost(options, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent("{\"model\":\"DirectMl-Q4_0\",\"backend\":\"directml\"}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("directml_runner_not_configured", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_RejectsMediaGgufWithStructuredError()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf"), "wan2");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent("{\"model\":\"Wan2.2-T2V-A14B-HighNoise-Q2_K\"}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("unsupported_model_type", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task InferenceEndpoint_ReturnsModelNotLoadedError()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"missing\",\"messages\":[]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("model_not_loaded", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_ReturnsOpenAiChatCompletion()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Chat-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("hello from runtime"));
            registry.Load("Chat-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"Chat-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("chat.completion", document.RootElement.GetProperty("object").GetString());
            Assert.AreEqual("hello from runtime", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            Assert.AreEqual("stop", document.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
            Assert.IsTrue(document.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_AppliesContextAwareAutoCompletionBudget()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "AutoBudget-Q4_0.gguf"));
            var factory = new FakeBackendFactory("long enough");
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, factory);
            registry.Load("AutoBudget-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"AutoBudget-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"give me a detailed explanation\"}]}", Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();

            Assert.AreEqual(1, factory.SeenMaxTokens.Count);
            Assert.IsTrue(factory.SeenMaxTokens[0] > 512, "Auto max token budget should not use the old short 512-token cap.");
            Assert.IsTrue(factory.SeenMaxTokens[0] <= LlmChatRequest.DefaultMaxCompletionTokens);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_DoesNotShortcutExactReplyPrompts()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "NoShortcut-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("backend response"));
            registry.Load("NoShortcut-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"NoShortcut-Q4_0\",\"max_tokens\":5,\"messages\":[{\"role\":\"user\",\"content\":\"Say exactly: hello world\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("backend response", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_HidesReasoningTagsFromMessageContent()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "HideThink-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("<think>hidden reasoning</think>visible answer"));
            registry.Load("HideThink-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"HideThink-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("visible answer", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_DropsWhitespaceOnlyReasoningResidue()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "HideWhitespace-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("\n<think>hidden reasoning"));
            registry.Load("HideWhitespace-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"HideWhitespace-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_AutoLoadsDiscoveredModel()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "AutoLoad-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("auto loaded"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"AutoLoad-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("auto loaded", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
            Assert.IsTrue(registry.ListModels().Any(model => model.Key == "AutoLoad-Q4_0" && model.IsLoaded));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_DoesNotGuessDefaultModelForLmStudioPlaceholder()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "PlaceholderDefault-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("placeholder auto loaded"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"lm-studio\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual((int)HttpStatusCode.NotFound, (int)response.StatusCode);
            Assert.AreEqual("model_not_loaded", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.IsFalse(registry.ListModels().Any(model => model.Key == "PlaceholderDefault-Q4_0" && model.IsLoaded));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_UsesSingleLoadedModelForLmStudioPlaceholder()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "LoadedDefault-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("loaded placeholder"));
            registry.Load("LoadedDefault-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"lm-studio\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("loaded placeholder", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_StreamsOpenAiSseChunks()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Stream-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("streamed text"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent("{\"model\":\"Stream-Q4_0\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(body, "data:");
            StringAssert.Contains(body, "\n\n");
            StringAssert.Contains(body, "streamed");
            StringAssert.Contains(body, "data: [DONE]\n\n");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletionsEndpoint_StreamsToolCallDeltasForExternalRunner()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "StreamTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "{\"tool\":\"vs_write_file\",\"input\":{\"path\":\"hello.txt\",\"content\":\"hello\"}}"));
            registry.Load("StreamTool-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "StreamTool-Q4_0",
              "stream": true,
              "messages": [{ "role": "user", "content": "create hello.txt" }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "vs_write_file",
                  "description": "Writes a file.",
                  "parameters": { "type": "object" }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(body, "\"tool_calls\"");
            StringAssert.Contains(body, "\"finish_reason\":\"tool_calls\"");
            StringAssert.Contains(body, "\"name\":\"vs_write_file\"");
            StringAssert.Contains(body, "hello.txt");
            StringAssert.Contains(body, "data: [DONE]\n\n");
            Assert.IsFalse(body.Contains("\"content\":\"{\\\"tool\\\"", StringComparison.Ordinal), body);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task NativeChatEndpoint_StreamsOpenAiSseChunksWithFrameBoundaries()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "NativeStream-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("native streamed text"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/chat",
                new StringContent("{\"model\":\"NativeStream-Q4_0\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(body, "data:");
            StringAssert.Contains(body, "\n\n");
            StringAssert.Contains(body, "native");
            StringAssert.Contains(body, "data: [DONE]\n\n");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task NativeChatEndpoint_ReturnsOpenAiCompatibleCompletion()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "NativeChat-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("native chat"));
            registry.Load("NativeChat-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/chat",
                new StringContent("{\"model\":\"NativeChat-Q4_0\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("native chat", document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task SchedulerStatus_ReturnsLoadedInstanceDeviceAndConcurrencyMetadata()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Scheduler-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("scheduled"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var loadResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent(
                    "{\"model\":\"Scheduler-Q4_0\",\"device_id\":\"cuda:1\",\"concurrency_limit\":4,\"echo_load_config\":true}",
                    Encoding.UTF8,
                    "application/json"));
            loadResponse.EnsureSuccessStatusCode();

            string loadBody = await loadResponse.Content.ReadAsStringAsync();
            using (JsonDocument loadDocument = JsonDocument.Parse(loadBody))
            {
                JsonElement rootElement = loadDocument.RootElement;
                StringAssert.Contains(rootElement.GetProperty("instance_id").GetString() ?? "", "@cuda-1");
                Assert.AreEqual("cuda:1", rootElement.GetProperty("load_config").GetProperty("device_id").GetString());
                Assert.AreEqual(4, rootElement.GetProperty("load_config").GetProperty("concurrency_limit").GetInt32());
            }

            string statusBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/scheduler/status");
            using JsonDocument statusDocument = JsonDocument.Parse(statusBody);
            JsonElement instance = statusDocument.RootElement.GetProperty("instances")[0];
            Assert.AreEqual("cuda:1", instance.GetProperty("device_id").GetString());
            Assert.AreEqual(4, instance.GetProperty("concurrency_limit").GetInt32());
            Assert.AreEqual(0, instance.GetProperty("active_jobs").GetInt32());
            Assert.AreEqual(0, instance.GetProperty("queue_count").GetInt32());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_DataParallelTargetsLoadMultipleGpuInstances()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "DataParallel-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("parallel"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var loadResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent(
                    "{\"model\":\"DataParallel-Q4_0\",\"parallelism_mode\":\"data_parallel\",\"parallelism_placement\":\"local\",\"target_device_ids\":[\"cuda:0\",\"cuda:1\"],\"gpu_load_threshold_percent\":70,\"max_vram_usage_percent\":82,\"echo_load_config\":true}",
                    Encoding.UTF8,
                    "application/json"));
            loadResponse.EnsureSuccessStatusCode();

            string loadBody = await loadResponse.Content.ReadAsStringAsync();
            using JsonDocument loadDocument = JsonDocument.Parse(loadBody);
            JsonElement rootElement = loadDocument.RootElement;
            Assert.AreEqual("local-instance-pool", rootElement.GetProperty("parallelism").GetProperty("execution").GetString());
            Assert.AreEqual(2, rootElement.GetProperty("instances").GetArrayLength());
            Assert.AreEqual("data_parallel", rootElement.GetProperty("load_config").GetProperty("parallelism_mode").GetString());
            Assert.AreEqual(70, rootElement.GetProperty("load_config").GetProperty("gpu_load_threshold_percent").GetInt32());
            Assert.AreEqual(82, rootElement.GetProperty("load_config").GetProperty("max_vram_usage_percent").GetInt32());

            string statusBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/scheduler/status");
            using JsonDocument statusDocument = JsonDocument.Parse(statusBody);
            JsonElement instances = statusDocument.RootElement.GetProperty("instances");
            Assert.AreEqual(2, instances.GetArrayLength());
            CollectionAssert.AreEquivalent(
                new[] { "cuda:0", "cuda:1" },
                instances.EnumerateArray().Select(instance => instance.GetProperty("device_id").GetString()).ToArray());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_ParallelTensorTargetsLoadSingleShardedGpuInstance()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "TensorParallel-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("tensor"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var loadResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent(
                    "{\"model\":\"TensorParallel-Q4_0\",\"parallel_tensor\":true,\"target_device_ids\":[\"cuda:0\",\"cuda:1\"],\"echo_load_config\":true}",
                    Encoding.UTF8,
                    "application/json"));
            loadResponse.EnsureSuccessStatusCode();

            string loadBody = await loadResponse.Content.ReadAsStringAsync();
            using JsonDocument loadDocument = JsonDocument.Parse(loadBody);
            JsonElement rootElement = loadDocument.RootElement;
            Assert.AreEqual("tensor-sharded-runtime-instance", rootElement.GetProperty("parallelism").GetProperty("execution").GetString());
            Assert.IsTrue(rootElement.GetProperty("parallelism").GetProperty("parallel_tensor").GetBoolean());
            Assert.AreEqual(2, rootElement.GetProperty("parallelism").GetProperty("tensor_parallel_size").GetInt32());
            Assert.AreEqual(1, rootElement.GetProperty("instances").GetArrayLength());
            Assert.AreEqual("tensor_parallel", rootElement.GetProperty("load_config").GetProperty("parallelism_mode").GetString());
            Assert.IsTrue(rootElement.GetProperty("load_config").GetProperty("parallel_tensor").GetBoolean());
            Assert.AreEqual(2, rootElement.GetProperty("load_config").GetProperty("tensor_parallel_size").GetInt32());
            CollectionAssert.AreEquivalent(
                new[] { "cuda:0", "cuda:1" },
                rootElement.GetProperty("load_config").GetProperty("target_device_ids").EnumerateArray().Select(device => device.GetString()).ToArray());

            string statusBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/scheduler/status");
            using JsonDocument statusDocument = JsonDocument.Parse(statusBody);
            JsonElement instances = statusDocument.RootElement.GetProperty("instances");
            Assert.AreEqual(1, instances.GetArrayLength());
            Assert.IsTrue(instances[0].GetProperty("parallel_tensor").GetBoolean());
            Assert.AreEqual(2, instances[0].GetProperty("tensor_parallel_size").GetInt32());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_ParallelTensorRequiresAtLeastTwoGpuTargets()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "TensorParallelInvalid-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("tensor"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var loadResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent(
                    "{\"model\":\"TensorParallelInvalid-Q4_0\",\"parallelism_mode\":\"tensor_parallel\",\"target_device_ids\":[\"cuda:0\"],\"echo_load_config\":true}",
                    Encoding.UTF8,
                    "application/json"));

            string body = await loadResponse.Content.ReadAsStringAsync();
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.AreEqual(HttpStatusCode.BadRequest, loadResponse.StatusCode);
            Assert.AreEqual("unsupported_tensor_parallel_targets", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task LoadModel_DataParallelReturnsPartialSuccessWhenOneGpuInstanceLoads()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "PartialParallel-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new PartiallyFailingBackendFactory("cuda-1"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var loadResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/models/load",
                new StringContent(
                    "{\"model\":\"PartialParallel-Q4_0\",\"parallelism_mode\":\"data_parallel\",\"target_device_ids\":[\"cuda:0\",\"cuda:1\"],\"echo_load_config\":true}",
                    Encoding.UTF8,
                    "application/json"));
            loadResponse.EnsureSuccessStatusCode();

            string loadBody = await loadResponse.Content.ReadAsStringAsync();
            using JsonDocument loadDocument = JsonDocument.Parse(loadBody);
            JsonElement rootElement = loadDocument.RootElement;
            Assert.AreEqual("partial_loaded", rootElement.GetProperty("status").GetString());
            Assert.IsTrue(rootElement.GetProperty("partial").GetBoolean());
            Assert.AreEqual(1, rootElement.GetProperty("instances").GetArrayLength());
            Assert.AreEqual("cuda:0", rootElement.GetProperty("instances")[0].GetProperty("load_config").GetProperty("device_id").GetString());
            Assert.AreEqual(1, rootElement.GetProperty("instance_errors").GetArrayLength());
            Assert.AreEqual("cuda:1", rootElement.GetProperty("instance_errors")[0].GetProperty("device_id").GetString());

            string statusBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/scheduler/status");
            using JsonDocument statusDocument = JsonDocument.Parse(statusBody);
            JsonElement instances = statusDocument.RootElement.GetProperty("instances");
            Assert.AreEqual(1, instances.GetArrayLength());
            Assert.AreEqual("cuda:0", instances[0].GetProperty("device_id").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_ReturnsNativeToolCallsForExternalRunner()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "ToolChat-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "{\"tool_calls\":[{\"id\":\"call_lookup\",\"name\":\"vendor_lookup\",\"arguments\":{\"id\":\"abc\"}}]}"));
            registry.Load("ToolChat-Q4_0");

            var options = new LlmRuntimeOptions { ModelRoot = root, ToolRoot = Path.Combine(root, "Tools"), Port = port };
            using var host = new LlmRuntimeHost(options, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "ToolChat-Q4_0",
              "messages": [{ "role": "user", "content": "look up abc" }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "vendor_lookup",
                  "description": "Looks up a proprietary record.",
                  "parameters": { "type": "object", "properties": { "id": { "type": "string" } } }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            Assert.AreEqual("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.AreEqual("", message.GetProperty("content").GetString());
            Assert.AreEqual("vendor_lookup", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("{\"id\":\"abc\"}", message.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_DoesNotRepeatCompletedToolCallWithMultilineArguments()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "RepeatTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "{\"tool_calls\":[{\"id\":\"call_repeat\",\"name\":\"get_state\",\"arguments\":{\"path\":\"fkkvs.Vb\"}}]}",
                "The state was already read; continuing from the existing result."));
            registry.Load("RepeatTool-Q4_0");

            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "RepeatTool-Q4_0",
              "messages": [
                { "role": "user", "content": "Implement the plan" },
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "call_done",
                    "type": "function",
                    "function": {
                      "name": "get_state",
                      "arguments": "{\n  \"path\": \"fkkvs.Vb\"\n}"
                    }
                  }]
                },
                {
                  "role": "tool",
                  "tool_call_id": "call_done",
                  "content": ""
                }
              ],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "get_state",
                  "description": "Gets state.",
                  "parameters": { "type": "object", "properties": { "path": { "type": "string" } } }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            Assert.AreEqual("stop", choice.GetProperty("finish_reason").GetString());
            Assert.IsFalse(message.TryGetProperty("tool_calls", out _));
            StringAssert.Contains(message.GetProperty("content").GetString(), "already read");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_ConvertsXmlStyleToolCallToNativeToolCall()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "XmlTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "<tool_call id=\"call_2\" name=\"vs_write_file\"><arguments>{\"path\":\"xml.html\",\"content\":\"ok\"}</arguments></tool_call>"));
            registry.Load("XmlTool-Q4_0");

            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "XmlTool-Q4_0",
              "messages": [{ "role": "user", "content": "create xml.html" }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "vs_write_file",
                  "description": "Writes a file.",
                  "parameters": { "type": "object" }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var toolCall = message.GetProperty("tool_calls")[0];
            Assert.AreEqual("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.AreEqual("", message.GetProperty("content").GetString());
            Assert.AreEqual("vs_write_file", toolCall.GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("{\"path\":\"xml.html\",\"content\":\"ok\"}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_RepairsMalformedToolCallAttempt()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "RepairTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "I'll write it now.\n{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"vs_write_file\",\"arguments\":{\"path\":\"broken.html\",\"content\":\"<h1>L",
                "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"vs_write_file\",\"arguments\":{\"path\":\"fixed.html\",\"content\":\"<h1>ok</h1>\"}}]}"));
            registry.Load("RepairTool-Q4_0");

            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "RepairTool-Q4_0",
              "messages": [{ "role": "user", "content": "create fixed.html" }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "vs_write_file",
                  "description": "Writes a file.",
                  "parameters": { "type": "object" }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
            Assert.AreEqual("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.AreEqual("vs_write_file", toolCall.GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("{\"path\":\"fixed.html\",\"content\":\"<h1>ok</h1>\"}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_RescuesWriteFileIntentWhenToolJsonRepairFails()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "RescueTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "I will use vs_write_file but the JSON is malformed.",
                "Still malformed tool_calls text."));
            registry.Load("RescueTool-Q4_0");

            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "RescueTool-Q4_0",
              "messages": [{ "role": "user", "content": "Create a current-session file named rescued.html with exactly this content: tool rescue ok. Use the file-writing tool." }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "vs_write_file",
                  "description": "Writes a file.",
                  "parameters": { "type": "object" }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
            Assert.AreEqual("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.AreEqual("vs_write_file", toolCall.GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("{\"path\":\"rescued.html\",\"content\":\"tool rescue ok\",\"overwrite\":false}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChatCompletion_RejectsRepairPlaceholderAndRescuesNamedReadFileIntent()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "ReadRescueTool-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory(
                "I will use read_file with {\"path\":\"Project.csproj\" but the JSON is malformed.",
                "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"tool_name\",\"arguments\":{}}]}"));
            registry.Load("ReadRescueTool-Q4_0");

            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string request = """
            {
              "model": "ReadRescueTool-Q4_0",
              "messages": [{ "role": "user", "content": "Inspect the project file before editing." }],
              "tools": [{
                "type": "function",
                "function": {
                  "name": "read_file",
                  "description": "Reads a file.",
                  "parameters": {
                    "type": "object",
                    "properties": { "path": { "type": "string" } },
                    "required": ["path"]
                  }
                }
              }]
            }
            """;
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(request, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var choice = document.RootElement.GetProperty("choices")[0];
            var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
            Assert.AreEqual("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.AreEqual("read_file", toolCall.GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("{\"path\":\"Project.csproj\"}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task AutonomousAgentRun_UsesLoadedModelToApplyFileEdit()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Agent-Q4_0.gguf"));
            string workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new FakeBackendFactory("""
            {
              "summary": "create file",
              "steps": [{ "title": "Write file", "detail": "Create the requested output." }],
              "files": [{ "path": "result.txt", "content": "local gguf powered edit" }],
              "commands": []
            }
            """));
            registry.Load("Agent-Q4_0");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = workspace, Port = port }, registry);
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/agent/autonomous/run",
                new StringContent(JsonSerializer.Serialize(new
                {
                    goal = "Create result.txt",
                    model = "Agent-Q4_0",
                    workspaceRoot = workspace,
                    approved = true
                }), Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("completed", document.RootElement.GetProperty("session").GetProperty("status").GetString());
            Assert.AreEqual("local gguf powered edit", File.ReadAllText(Path.Combine(workspace, "result.txt")));
            Assert.IsTrue(document.RootElement.GetProperty("applied").GetBoolean());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task GitHubDraftPullRequestJobEndpoint_QueuesBackgroundWorkflow()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = Path.Combine(root, "models"), AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/github/pull-request/job",
                new StringContent(JsonSerializer.Serialize(new
                {
                    title = "Draft PR",
                    body = "Created by test",
                    workspaceRoot = root,
                    approved = true,
                    push = false
                }), Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.AreEqual("draft_pull_request", document.RootElement.GetProperty("job").GetProperty("kind").GetString());
            string jobs = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/github/jobs");
            StringAssert.Contains(jobs, "draft_pull_request");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private sealed class FakeBackendFactory : ILlmBackendFactory
    {
        private readonly Queue<string> _texts;
        private readonly string _fallbackText;
        private readonly object _seenLock = new();

        public FakeBackendFactory(params string[] text)
        {
            _texts = new Queue<string>(text);
            _fallbackText = text.LastOrDefault() ?? "";
        }

        public List<int> SeenMaxTokens { get; } = new();

        public void AddSeenMaxTokens(int maxTokens)
        {
            lock (_seenLock)
                SeenMaxTokens.Add(maxTokens);
        }

        public ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig) =>
            new FakeBackend(instanceId, modelPath, loadConfig, _texts, _fallbackText, AddSeenMaxTokens);
    }

    private sealed class FakeBackend : ILlmBackend
    {
        private readonly Queue<string> _texts;
        private readonly string _fallbackText;
        private readonly Action<int> _recordMaxTokens;

        public FakeBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig, Queue<string> texts, string fallbackText, Action<int> recordMaxTokens)
        {
            InstanceId = instanceId;
            ModelPath = modelPath;
            LoadConfig = loadConfig;
            _texts = texts;
            _fallbackText = fallbackText;
            _recordMaxTokens = recordMaxTokens;
        }

        public string InstanceId { get; }

        public string ModelPath { get; }

        public LlmLoadConfig LoadConfig { get; }

        public bool IsPromptPipelineReady => true;

        public string PromptPipelineStatus => "ready";

        public string PromptPipelineDetail => "";

        public DateTimeOffset? PromptPipelineReadyAtUtc => DateTimeOffset.UtcNow;

        public double PromptPipelineWarmupSeconds => 0;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
        {
            _recordMaxTokens(request.MaxTokens);
            string text;
            lock (_texts)
                text = _texts.Count == 0 ? _fallbackText : _texts.Dequeue();

            return Task.FromResult(new LlmChatResult
            {
                Model = request.Model,
                Content = text,
                FinishReason = "stop",
                Metrics = LlmInferenceMetrics.FromText(request, text, TimeSpan.FromMilliseconds(25))
            });
        }

        public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _recordMaxTokens(request.MaxTokens);
            string text;
            lock (_texts)
                text = _texts.Count == 0 ? _fallbackText : _texts.Dequeue();

            foreach (string part in text.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new LlmChatToken(part + " ");
                await Task.Yield();
            }
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PartiallyFailingBackendFactory : ILlmBackendFactory
    {
        private readonly string _failingInstanceSegment;

        public PartiallyFailingBackendFactory(string failingInstanceSegment)
        {
            _failingInstanceSegment = failingInstanceSegment;
        }

        public ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig)
        {
            if (!string.IsNullOrWhiteSpace(_failingInstanceSegment) &&
                instanceId.Contains(_failingInstanceSegment, StringComparison.OrdinalIgnoreCase))
            {
                return new FailingLoadBackend(instanceId, modelPath, loadConfig);
            }

            return new FakeBackend(instanceId, modelPath, loadConfig, new Queue<string>(["partial"]), "partial", _ => { });
        }
    }

    private sealed class FailingLoadBackend : ILlmBackend
    {
        public FailingLoadBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig)
        {
            InstanceId = instanceId;
            ModelPath = modelPath;
            LoadConfig = loadConfig;
        }

        public string InstanceId { get; }

        public string ModelPath { get; }

        public LlmLoadConfig LoadConfig { get; }

        public bool IsPromptPipelineReady => false;

        public string PromptPipelineStatus => "failed";

        public string PromptPipelineDetail => "Simulated load failure.";

        public DateTimeOffset? PromptPipelineReadyAtUtc => null;

        public double PromptPipelineWarmupSeconds => 0;

        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated replica load failure.");

        public Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmChatResult { Model = request.Model, Content = "" });

        public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
