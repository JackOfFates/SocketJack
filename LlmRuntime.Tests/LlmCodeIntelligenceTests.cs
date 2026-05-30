using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmCodeIntelligenceTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void CodeIntelligenceService_BuildsGraphsPlansAndLocalPrivacyArtifacts()
    {
        string root = CreateWorkspaceFixture();
        try
        {
            var options = new LlmRuntimeOptions
            {
                DefaultWorkspaceRoot = root,
                ModelRoot = Path.Combine(root, "Models"),
                AgentRoot = Path.Combine(root, "Agents"),
                ToolRoot = Path.Combine(root, "Tools"),
                LocalPrivacyMode = true
            };
            using var registry = new LlmModelRegistry(options);
            var service = new LlmCodeIntelligenceService(options, registry);

            var symbols = service.BuildSymbolGraph(root);
            var graph = service.BuildCodeGraph(root);
            var refactor = service.CreateRefactorPlan(root, "rename runner");
            var migration = service.CreateMigrationPlan(root, "net9.0");
            var tests = service.ExploreTests(root);
            var profiling = service.BuildProfilingPlan();
            var architecture = service.ReviewArchitecture(root);
            var docs = service.CreateDocumentationSyncPlan(root);
            var eval = service.CreateModelEvaluationHarness(root);
            var context = service.OptimizeContext(root, 12000);

            Assert.IsTrue(symbols.Symbols.Any(symbol => symbol.Name == "SampleRunner"));
            Assert.IsTrue(graph.Dependencies.Values.Any(values => values.Contains("Newtonsoft.Json")));
            Assert.IsTrue(refactor.Steps.Count >= 4);
            Assert.IsTrue(migration.Steps.Any(step => step.Detail.Contains("project", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(tests.TestProjects.Count > 0);
            Assert.IsTrue(profiling.Cpu.Count > 0);
            Assert.IsTrue(architecture.Recommendations.Count > 0);
            Assert.IsTrue(docs.DocumentationFiles.Any(file => file.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(eval.Tasks.Contains("Generate focused unit test"));
            Assert.AreEqual(12000, context.MaxTokens);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task CodeIntelligenceEndpoints_ReturnStableShapes()
    {
        string root = CreateWorkspaceFixture();
        int port = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions
            {
                DefaultWorkspaceRoot = root,
                ModelRoot = Path.Combine(root, "Models"),
                AgentRoot = Path.Combine(root, "Agents"),
                ToolRoot = Path.Combine(root, "Tools"),
                Port = port,
                LocalPrivacyMode = true
            });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string workspaceJson = "{\"workspace_root\":\"" + JsonEsc(root) + "\"}";
            string graph = await Post(client, port, "/api/v1/code-intelligence/symbol-graph", workspaceJson);
            string plan = await Post(client, port, "/api/v1/code-intelligence/refactor-plan", "{\"workspace_root\":\"" + JsonEsc(root) + "\",\"goal\":\"extract service\"}");
            string privacy = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/code-intelligence/privacy");

            using var graphDoc = JsonDocument.Parse(graph);
            using var planDoc = JsonDocument.Parse(plan);
            using var privacyDoc = JsonDocument.Parse(privacy);

            Assert.IsTrue(graphDoc.RootElement.GetProperty("symbols").GetArrayLength() > 0);
            Assert.AreEqual("Solution-wide refactor plan", planDoc.RootElement.GetProperty("title").GetString());
            Assert.IsTrue(privacyDoc.RootElement.GetProperty("localPrivacyMode").GetBoolean());
            Assert.IsFalse(privacyDoc.RootElement.GetProperty("codeLeavesMachine").GetBoolean());
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private static string CreateWorkspaceFixture()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "README.md"), "# Sample\n");
        File.WriteAllText(Path.Combine(root, "Sample.sln"), "\n");
        File.WriteAllText(Path.Combine(root, "src", "Sample.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(root, "src", "SampleRunner.cs"), """
using System;
using Newtonsoft.Json;

public sealed class SampleRunner
{
    public string Run()
    {
        Console.WriteLine("running");
        return JsonConvert.SerializeObject(new { ok = true });
    }
}
""");
        File.WriteAllText(Path.Combine(root, "tests", "Sample.Tests.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
  </ItemGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(root, "tests", "SampleRunnerTests.cs"), """
public sealed class SampleRunnerTests
{
    public void Run_ReturnsJson()
    {
    }
}
""");
        return root;
    }

    private static async Task<string> Post(HttpClient client, int port, string path, string body)
    {
        using var response = await client.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(body, Encoding.UTF8, "application/json"));
        return await response.Content.ReadAsStringAsync();
    }

    private static string JsonEsc(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
