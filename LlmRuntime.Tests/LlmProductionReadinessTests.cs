using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmProductionReadinessTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void ProductionReadiness_BuildsOnboardingDiagnosticsAndAnalytics()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Ready-Q4_0.gguf"));
            var options = new LlmRuntimeOptions
            {
                ModelRoot = root,
                ToolRoot = Path.Combine(root, "Tools"),
                AgentRoot = Path.Combine(root, "Agents"),
                Port = NextPort()
            };
            using var registry = new LlmModelRegistry(options);
            var tools = new LlmToolRegistry(options);
            var agents = new LlmAgentRuntime(options);
            var github = new LlmGitHubWorkflowService(options, agents);
            var service = new LlmProductionReadinessService(options, registry, tools, agents, github, DateTimeOffset.UtcNow.AddSeconds(-5));

            var onboarding = service.BuildOnboardingChecklist();
            var diagnostics = service.BuildDiagnosticsReport(options.Port, isListening: false);
            var analytics = service.BuildLocalAnalyticsDashboard();

            Assert.IsTrue(onboarding.PercentComplete > 0);
            Assert.IsTrue(onboarding.Items.Any(item => item.Id == "local-model" && item.Complete));
            Assert.IsTrue(diagnostics.Checks.Any(check => check.Id == "models" && check.Severity == "ok"));
            Assert.AreEqual("local-only", analytics.Telemetry);
            Assert.AreEqual(1, analytics.ModelCount);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task ProductionEndpoints_ReturnStableJson()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "EndpointReady-Q4_0.gguf"));
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions { ModelRoot = root, Port = port });
            Assert.IsTrue(host.Start());

            using var client = new System.Net.Http.HttpClient();
            string onboarding = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/production/onboarding");
            string diagnostics = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/production/diagnostics");
            string analytics = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/production/analytics/local");
            string goldenPath = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/production/golden-path");

            using var onboardingDoc = JsonDocument.Parse(onboarding);
            using var diagnosticsDoc = JsonDocument.Parse(diagnostics);
            using var analyticsDoc = JsonDocument.Parse(analytics);
            using var goldenPathDoc = JsonDocument.Parse(goldenPath);

            Assert.IsTrue(onboardingDoc.RootElement.GetProperty("percentComplete").GetInt32() > 0);
            Assert.IsTrue(diagnosticsDoc.RootElement.GetProperty("checks").GetArrayLength() > 0);
            Assert.AreEqual("local-only", analyticsDoc.RootElement.GetProperty("telemetry").GetString());
            Assert.IsTrue(goldenPathDoc.RootElement.GetProperty("steps").GetArrayLength() > 3);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }
}
