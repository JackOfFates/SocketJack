using System.Net;
using System.Text.Json.Nodes;
using LlmRuntime.VisualStudio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class SocketJackCopilotServicesTests
{
    [TestMethod]
    public void MasterListParserNormalizesToolsCapableServer()
    {
        string json = """
        {
          "servers": [
            {
              "id": "TitanX",
              "title": "Titan X",
              "endpoint": "https://socketjack.com/proxy/TitanX",
              "online": true,
              "hostResponding": true,
              "toolsAllowed": "VS_tools, VS bridge, filesystem",
              "availableModels": ["Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF"]
            }
          ]
        }
        """;

        IReadOnlyList<SocketJackServerCandidate> servers = SocketJackMasterListClient.ParseServers(json);

        Assert.AreEqual(1, servers.Count);
        Assert.AreEqual("TitanX", servers[0].Id);
        Assert.AreEqual("https://socketjack.com/proxy/TitanX", servers[0].EffectiveEndpoint);
        Assert.IsTrue(servers[0].CanUseForCopilot);
        Assert.AreEqual("Qwen/Qwen3-Coder-30B-A3B-Instruct-GGUF", servers[0].AvailableModels[0]);
    }

    [TestMethod]
    public void ModelDiscoveryUsesMasterListCapabilityArrayStringForToolsSupport()
    {
        string capabilities = """
        [
          {
            "id": "qwen-tools",
            "type": "text-generation",
            "supportsTools": true,
            "isLoaded": true,
            "enabled": true,
            "disabled": false,
            "dynamicLoadEnabled": true
          },
          {
            "id": "video-model",
            "type": "video-generation",
            "supportsTools": false,
            "isLoaded": false,
            "enabled": true,
            "disabled": false,
            "dynamicLoadEnabled": true
          }
        ]
        """;
        string json = new JsonObject
        {
            ["servers"] = new JsonArray(new JsonObject
            {
                ["id"] = "TitanX",
                ["title"] = "TitanX",
                ["endpoint"] = "https://socketjack.com/proxy/TitanX",
                ["hostResponding"] = true,
                ["toolsAllowed"] = "LlmRuntime chat, VS_tools, VS bridge",
                ["availableModels"] = "qwen-tools, video-model",
                ["modelCapabilitiesJson"] = capabilities
            })
        }.ToJsonString();

        SocketJackServerCandidate server = SocketJackMasterListClient.ParseServers(json).Single();
        IReadOnlyList<SocketJackModelCandidate> models = SocketJackModelDiscoveryService.ParseAndMergeModels(null, null, server);

        SocketJackModelCandidate qwen = models.Single(model => model.Id == "qwen-tools");
        SocketJackModelCandidate video = models.Single(model => model.Id == "video-model");
        Assert.IsTrue(qwen.SupportsTools);
        Assert.IsTrue(qwen.IsSelectable, qwen.EligibilityReason);
        Assert.IsFalse(video.SupportsTools);
        Assert.IsFalse(video.IsSelectable);
    }

    [TestMethod]
    public void ModelDiscoveryMergesCompactAndRuntimeEligibility()
    {
        string compact = """
        {
          "models": [
            {
              "id": "qwen-tools",
              "displayName": "Qwen Tools",
              "supportsTools": true,
              "supportsVision": false
            },
            {
              "id": "embed-only",
              "displayName": "Embed Only",
              "supportsTools": false
            }
          ]
        }
        """;
        string runtime = """
        {
          "models": [
            {
              "key": "qwen-tools",
              "type": "text-generation",
              "loaded_instances": [],
              "capabilities": {
                "chat_completion": true,
                "runtime_load": true,
                "web_chat_dynamic_load": true,
                "trained_for_tool_use": true
              }
            },
            {
              "key": "embed-only",
              "type": "embedding",
              "load_disabled_reason": "embeddings only",
              "capabilities": {
                "chat_completion": false,
                "runtime_load": false
              }
            }
          ]
        }
        """;

        IReadOnlyList<SocketJackModelCandidate> models = SocketJackModelDiscoveryService.ParseAndMergeModels(compact, runtime);

        SocketJackModelCandidate qwen = models.Single(model => model.Id == "qwen-tools");
        SocketJackModelCandidate embed = models.Single(model => model.Id == "embed-only");
        Assert.IsTrue(qwen.IsSelectable, qwen.EligibilityReason);
        Assert.IsFalse(embed.IsSelectable);
        Assert.AreEqual("embeddings only", embed.DisabledReason);
    }

    [TestMethod]
    public void ModelDiscoveryKeepsLoadedPolicyDisabledModelSelectable()
    {
        string compact = """
        {
          "models": [
            {
              "id": "qwen-loaded",
              "supportsTools": true,
              "supportsVision": true,
              "isLoaded": true,
              "enabled": false,
              "disabled": false,
              "status": "loaded-disabled",
              "loadDisabledReason": "Enable this model in Workstation's Models tab, or enable the global web chat model-load API in Diagnostics."
            }
          ]
        }
        """;
        string runtime = """
        {
          "models": [
            {
              "key": "qwen-loaded",
              "type": "text-generation",
              "loaded_instances": [
                { "id": "active" }
              ],
              "enabled": false,
              "isEnabled": false,
              "disabled": false,
              "load_disabled_reason": "Enable this model in Workstation's Models tab, or enable the global web chat model-load API in Diagnostics.",
              "capabilities": {
                "chat_completion": true,
                "runtime_load": true,
                "trained_for_tool_use": true,
                "web_chat_dynamic_load": false
              }
            }
          ]
        }
        """;

        IReadOnlyList<SocketJackModelCandidate> models = SocketJackModelDiscoveryService.ParseAndMergeModels(compact, runtime);

        SocketJackModelCandidate qwen = models.Single(model => model.Id == "qwen-loaded");
        Assert.IsTrue(qwen.IsLoaded);
        Assert.IsTrue(qwen.Enabled);
        Assert.AreEqual("", qwen.DisabledReason);
        Assert.IsTrue(qwen.IsSelectable, qwen.EligibilityReason);
    }

    [TestMethod]
    public void ModelDiscoveryPrefersLoadedModelOverDisabledStaleSelection()
    {
        string compact = """
        {
          "models": [
            {
              "id": "Qwen3.5-9B-Claude-4.6-Opus-Reasoning-Distilled-v2-GGUF",
              "supportsTools": true,
              "supportsVision": true,
              "isLoaded": true,
              "enabled": true,
              "disabled": false,
              "dynamicLoadEnabled": true
            },
            {
              "id": "Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF",
              "supportsTools": true,
              "supportsVision": true,
              "isLoaded": false,
              "enabled": false,
              "disabled": true,
              "loadDisabledReason": "Enable this model in Workstation's Models tab, or enable the global web chat model-load API in Diagnostics."
            }
          ]
        }
        """;

        IReadOnlyList<SocketJackModelCandidate> models = SocketJackModelDiscoveryService.ParseAndMergeModels(compact, null);

        Assert.AreEqual("Qwen3.5-9B-Claude-4.6-Opus-Reasoning-Distilled-v2-GGUF", models.First(model => model.IsSelectable).Id);
        Assert.IsFalse(models.Single(model => model.Id == "Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF").IsSelectable);
    }

    [TestMethod]
    public void BrowserCacheRoundTripsServersAndModels()
    {
        string temp = Path.Combine(Path.GetTempPath(), "socketjack-browser-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var server = new SocketJackServerCandidate
            {
                Id = "sable",
                DisplayName = "sable",
                Endpoint = "https://socketjack.com/proxy/sable",
                Online = true,
                HostResponding = true,
                ToolsAdvertised = true,
                ToolsAllowed = "VS_tools, VS bridge",
                Hardware = "RTX"
            };
            server.AvailableModels.Add("qwen-tools");

            var model = new SocketJackModelCandidate
            {
                Id = "qwen-tools",
                DisplayName = "Qwen Tools",
                ChatCapable = true,
                SupportsTools = true,
                SupportsVision = true,
                IsLoaded = true,
                Enabled = true,
                MaxInputTokens = 32768,
                MaxOutputTokens = 4096
            };

            var cache = new SocketJackCopilotBrowserCache(temp);
            cache.SaveServers(new[] { server });
            cache.SaveModels(server, new SocketJackModelDiscoveryResult(new[] { model }, Array.Empty<string>()));

            IReadOnlyList<SocketJackServerCandidate> cachedServers = cache.LoadServers();
            Assert.AreEqual(1, cachedServers.Count);
            Assert.AreEqual("sable", cachedServers[0].Id);
            Assert.AreEqual("https://socketjack.com/proxy/sable", cachedServers[0].EffectiveEndpoint);
            Assert.AreEqual("qwen-tools", cachedServers[0].AvailableModels.Single());
            Assert.IsTrue(cachedServers[0].CanUseForCopilot);

            Assert.IsTrue(cache.TryLoadModels(cachedServers[0], out SocketJackModelDiscoveryResult cachedModels));
            SocketJackModelCandidate cachedModel = cachedModels.Models.Single();
            Assert.AreEqual("qwen-tools", cachedModel.Id);
            Assert.IsTrue(cachedModel.IsSelectable, cachedModel.EligibilityReason);
            Assert.AreEqual(32768, cachedModel.MaxInputTokens);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [TestMethod]
    public void McpWriterPreservesOtherServersAndReplacesSocketJackEntry()
    {
        string temp = Path.Combine(Path.GetTempPath(), "socketjack-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, ".vs"));
        string configPath = Path.Combine(temp, ".vs", "mcp.json");
        File.WriteAllText(configPath, """
        {
          "servers": {
            "github": { "url": "https://api.githubcopilot.com/mcp/" },
            "socketjack-old": { "command": "old" }
          }
        }
        """);

        var server = new SocketJackServerCandidate
        {
            Id = "TitanX",
            DisplayName = "Titan X",
            Endpoint = "https://socketjack.com/proxy/TitanX",
            Online = true,
            HostResponding = true,
            ToolsAdvertised = true
        };
        var model = new SocketJackModelCandidate
        {
            Id = "qwen-tools",
            DisplayName = "Qwen Tools",
            ChatCapable = true,
            SupportsTools = true,
            RuntimeLoadable = true
        };

        McpConfigWriteResult result = new VisualStudioMcpConfigWriter().Write(
            temp,
            server,
            model,
            SocketJackBridgeLaunchBuilder.CreateStdioLaunch("C:\\bridge\\SocketJack.CopilotMcpBridge.csproj", server, model));

        JsonObject root = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        JsonObject servers = root["servers"]!.AsObject();
        Assert.AreEqual("socketjack-titanx", result.ServerKey);
        Assert.IsNotNull(servers["github"]);
        Assert.IsNull(servers["socketjack-old"]);
        Assert.AreEqual("dotnet", servers["socketjack-titanx"]!["command"]!.ToString());
    }

    [TestMethod]
    public void BridgeLaunchBuilderIncludesRememberedAuthUser()
    {
        var server = new SocketJackServerCandidate
        {
            Id = "TitanX",
            DisplayName = "Titan X",
            Endpoint = "https://socketjack.com/proxy/TitanX"
        };
        var model = new SocketJackModelCandidate
        {
            Id = "qwen-tools",
            DisplayName = "Qwen Tools"
        };

        BridgeLaunchInfo launch = SocketJackBridgeLaunchBuilder.CreateStdioLaunchFromDll(
            "C:\\bridge\\SocketJack.CopilotMcpBridge.dll",
            server,
            model,
            "token-value",
            "JACK");

        CollectionAssert.Contains(launch.Arguments.ToList(), "--auth-token");
        CollectionAssert.Contains(launch.Arguments.ToList(), "token-value");
        CollectionAssert.Contains(launch.Arguments.ToList(), "--auth-user");
        CollectionAssert.Contains(launch.Arguments.ToList(), "JACK");
    }

    [TestMethod]
    public void HttpProxyLaunchDisablesLocalWebChatForRemoteSocketJackServers()
    {
        var server = new SocketJackServerCandidate
        {
            Id = "sable",
            DisplayName = "sable",
            Endpoint = "https://socketjack.com/proxy/sable"
        };
        var model = new SocketJackModelCandidate
        {
            Id = "qwen-tools",
            DisplayName = "Qwen Tools"
        };

        BridgeLaunchInfo launch = SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromDll(
            "C:\\bridge\\SocketJack.CopilotMcpBridge.dll",
            server,
            model,
            11574);

        CollectionAssert.Contains(launch.Arguments.ToList(), "--disable-local-webchat");
    }

    [TestMethod]
    public void HttpProxyLaunchKeepsLocalWebChatForLoopbackServers()
    {
        var server = new SocketJackServerCandidate
        {
            Id = "local",
            DisplayName = "local",
            Endpoint = "http://127.0.0.1:11436"
        };
        var model = new SocketJackModelCandidate
        {
            Id = "local-model",
            DisplayName = "Local Model"
        };

        BridgeLaunchInfo launch = SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromDll(
            "C:\\bridge\\SocketJack.CopilotMcpBridge.dll",
            server,
            model,
            11574);

        CollectionAssert.DoesNotContain(launch.Arguments.ToList(), "--disable-local-webchat");
    }

    [TestMethod]
    public void OllamaByomWriterUpdatesSelectedModelToSocketJackEndpoint()
    {
        string temp = Path.Combine(Path.GetTempPath(), "socketjack-byom-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(temp, """
        [
          {
            "Name": "Ollama",
            "IsApiKeyAvailable": true,
            "Models": [
              {
                "ProviderName": "Ollama",
                "IsSelected": true,
                "CustomURL": "http://localhost:11434",
                "Id": "old-model",
                "DisplayName": "Old Model"
              }
            ],
            "Endpoint": 10
          }
        ]
        """);

        var server = new SocketJackServerCandidate { Id = "TitanX", DisplayName = "Titan X" };
        var model = new SocketJackModelCandidate
        {
            Id = "qwen-tools",
            DisplayName = "Qwen Tools",
            SupportsTools = true,
            SupportsVision = true,
            MaxInputTokens = 32768,
            MaxOutputTokens = 4096
        };

        OllamaByomWriteResult result = new VisualStudioOllamaByomConfigWriter().Write(
            server,
            model,
            "https://socketjack.com/proxy/TitanX",
            temp);

        JsonArray providers = JsonNode.Parse(File.ReadAllText(temp))!.AsArray();
        JsonObject selected = providers[0]!["Models"]!.AsArray()[0]!.AsObject();
        Assert.AreEqual("https://socketjack.com/proxy/TitanX", result.CustomUrl);
        Assert.AreEqual("qwen-tools", selected["Id"]!.ToString());
        Assert.AreEqual("https://socketjack.com/proxy/TitanX", selected["CustomURL"]!.ToString());
        Assert.AreEqual("true", selected["IsToolCallingEnabled"]!.ToString());
        Assert.AreEqual("true", selected["IsVisionEnabled"]!.ToString());
    }

    [TestMethod]
    public async Task EndpointAccessProberDoesNotTreatModelsRouteAsChatRoute()
    {
        using var client = new HttpClient(new RouteResponseHandler(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            return path.EndsWith("/api/models", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var prober = new SocketJackEndpointAccessProber(client);

        SocketJackEndpointAccessResult modelRoute = await prober.ProbeAsync("https://socketjack.com/proxy/TitanX");
        SocketJackEndpointAccessResult chatRoute = await prober.ProbeChatAsync("https://socketjack.com/proxy/TitanX", "qwen-tools");

        Assert.IsTrue(modelRoute.CanUseDirectEndpoint);
        Assert.IsFalse(chatRoute.CanUseDirectEndpoint);
        StringAssert.Contains(chatRoute.Message, "local WebSocket proxy");
    }

    [TestMethod]
    public async Task EndpointAccessProberReportsUnavailableSocketJackFallback()
    {
        using var client = new HttpClient(new RouteResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"ok":false,"error":"JackLLM has not connected its reverse agent."}""")
            }));
        var prober = new SocketJackEndpointAccessProber(client);

        SocketJackEndpointAccessResult fallback = await prober.ProbeSocketJackFallbackAsync("https://socketjack.com/proxy/TitanX");

        Assert.IsFalse(fallback.CanUseDirectEndpoint);
        StringAssert.Contains(fallback.Message, "503");
        StringAssert.Contains(fallback.Message, "reverse agent");
    }

    private sealed class RouteResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public RouteResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(this.responder(request));
        }
    }
}
