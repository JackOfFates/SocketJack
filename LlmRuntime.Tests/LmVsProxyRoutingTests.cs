using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LmVsProxyRoutingTests
{
    [TestMethod]
    public void CopilotDuplicatorRemoteRouting_OverridesLocalRuntimeProvider()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081)
        {
            LocalModelRuntime = new HttpLmVsProxyModelRuntime("Embedded LlmRuntime", "http://127.0.0.1:1234")
        };

        proxy.ConfigureRemoteModelServerSelection(new LmVsProxyRemoteModelServerSelection
        {
            Enabled = true,
            OpenAiBaseUrl = "https://example.com/lmstudio/v1",
            SelectedModel = "remote-code-model"
        });

        Assert.IsTrue(proxy.IsCopilotDuplicatorRemoteRoutingActive);
        Assert.AreEqual("https://example.com/lmstudio", proxy.GetEffectiveCopilotDuplicatorUpstreamBaseUrl());

        proxy.ClearRemoteModelServerSelection();

        Assert.IsFalse(proxy.IsCopilotDuplicatorRemoteRoutingActive);
        Assert.AreEqual("http://127.0.0.1:1234", proxy.GetEffectiveCopilotDuplicatorUpstreamBaseUrl());
    }

    [TestMethod]
    public void PrepareModelRuntimeForwardBody_StripsLmStudioUnsupportedLoadFields()
    {
        string body = """
            {
              "model": "qwen3.5-2b",
              "backend": "auto",
              "context_length": 8192,
              "eval_batch_size": 512,
              "gpu_layer_count": -1,
              "flash_attention": false,
              "offload_kv_cache_to_gpu": false,
              "allow_backend_fallback": true,
              "echo_load_config": true
            }
            """;

        string sanitized = LmVsProxy.PrepareModelRuntimeForwardBody("/api/v1/models/load", body, "LM Studio");

        using JsonDocument document = JsonDocument.Parse(sanitized);
        JsonElement root = document.RootElement;
        Assert.AreEqual("qwen3.5-2b", root.GetProperty("model").GetString());
        Assert.AreEqual(8192, root.GetProperty("context_length").GetInt32());
        Assert.IsFalse(root.TryGetProperty("backend", out _));
        Assert.IsFalse(root.TryGetProperty("gpu_layer_count", out _));
        Assert.IsFalse(root.TryGetProperty("allow_backend_fallback", out _));
    }

    [TestMethod]
    public void PrepareModelRuntimeForwardBody_MapsLoadAliasWhenModelKeyIsMissing()
    {
        string body = """
            {
              "id": "qwen3.5-2b",
              "backend": "auto",
              "context_length": 8192
            }
            """;

        string sanitized = LmVsProxy.PrepareModelRuntimeForwardBody("/api/v1/models/load", body, "LM Studio");

        using JsonDocument document = JsonDocument.Parse(sanitized);
        JsonElement root = document.RootElement;
        Assert.AreEqual("qwen3.5-2b", root.GetProperty("model").GetString());
        Assert.IsFalse(root.TryGetProperty("backend", out _));
    }

    [TestMethod]
    public void PrepareModelRuntimeForwardBody_MapsUnloadAliasWhenModelKeyIsMissing()
    {
        string body = """
            {
              "id": "qwen3.5-2b",
              "unknown": "value"
            }
            """;

        string forwarded = LmVsProxy.PrepareModelRuntimeForwardBody("/api/v1/models/unload", body, "LM Studio");

        using JsonDocument document = JsonDocument.Parse(forwarded);
        JsonElement root = document.RootElement;
        Assert.AreEqual("qwen3.5-2b", root.GetProperty("model").GetString());
        Assert.AreEqual("value", root.GetProperty("unknown").GetString());
    }

    [TestMethod]
    public void PrepareModelRuntimeForwardBody_KeepsEmbeddedLlmRuntimeLoadFields()
    {
        string body = "{\"model\":\"qwen3.5-2b\",\"backend\":\"cuda12\",\"gpu_layer_count\":-1,\"allow_backend_fallback\":true}";

        string forwarded = LmVsProxy.PrepareModelRuntimeForwardBody("/api/v1/models/load", body, "LlmRuntime");

        Assert.AreEqual(body, forwarded);
    }

    [TestMethod]
    public void RuntimeModelIdMatches_TreatsRuntimePackagingSuffixAsAlias()
    {
        using var proxy = new LmVsProxy("localhost", 11435, 18080, 18081);
        var method = typeof(LmVsProxy).GetMethod("RuntimeModelIdMatches", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);

        bool matches = (bool)(method.Invoke(proxy, new object[] { "John6666-bond-nsfw-v50-sdxl-pytorch", "John6666-bond-nsfw-v50-sdxl" }) ?? false);

        Assert.IsTrue(matches);
    }
}
