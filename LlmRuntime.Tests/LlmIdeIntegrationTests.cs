using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmIdeIntegrationTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void IdeService_ProvidesCompletionPlanIndexCheckpointAndRollback()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "Sample.cs"), "public class Sample\n{\n    // TODO: finish\n}\n");
            Directory.CreateDirectory(Path.Combine(root, ".github", "prompts"));
            File.WriteAllText(Path.Combine(root, ".github", "copilot-instructions.md"), "Prefer focused changes.");
            File.WriteAllText(Path.Combine(root, ".github", "prompts", "fix.prompt.md"), "Fix the selected code.");

            var options = new LlmRuntimeOptions
            {
                DefaultWorkspaceRoot = root,
                AgentRoot = Path.Combine(root, "Agents"),
                ToolRoot = Path.Combine(root, "Tools")
            };
            var agents = new LlmAgentRuntime(options);
            var tools = new LlmToolRegistry(options);
            var service = new LlmIdeIntegrationService(options, agents, tools);

            var request = new LlmIdeContextRequest
            {
                WorkspaceRoot = root,
                ActiveFilePath = Path.Combine(root, "Sample.cs"),
                ActiveFileText = File.ReadAllText(Path.Combine(root, "Sample.cs")),
                SelectionText = "// TODO: finish",
                Prompt = "Explain and fix TODO",
                CursorLine = 1
            };

            Assert.IsTrue(service.GetCapabilities().InlineCompletion);
            Assert.IsFalse(string.IsNullOrWhiteSpace(service.CompleteInline(request).Text));
            Assert.IsTrue(service.SuggestNextEdits(request).Count > 0);
            Assert.AreEqual("ask", service.Ask(request).Mode);
            Assert.IsTrue(service.PreviewEdit(request).Diff.Contains("+", StringComparison.Ordinal));
            Assert.AreEqual(4, service.CreatePlan("Fix TODO").Steps.Count);
            Assert.IsTrue(service.BuildReferences(request).CustomInstructionFiles.Count > 0);
            Assert.IsTrue(service.ReadPromptFiles(root).Count > 0);
            Assert.IsTrue(service.BuildWorkspaceIndex(root).Documents.Count > 0);
            Assert.IsTrue(service.Search(root, "Sample").Count > 0);

            var checkpoint = service.CreateCheckpoint(root, ["Sample.cs"]);
            File.WriteAllText(Path.Combine(root, "Sample.cs"), "changed");
            var rollback = service.Rollback(checkpoint.Id, root);
            Assert.AreEqual(1, rollback.RestoredFiles);
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "Sample.cs")), "TODO");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task IdeEndpoints_ReturnCopilotParityShapes()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            File.WriteAllText(Path.Combine(root, "EndpointSample.cs"), "public class EndpointSample\n{\n    // TODO: finish\n}\n");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions
            {
                DefaultWorkspaceRoot = root,
                AgentRoot = Path.Combine(root, "Agents"),
                ToolRoot = Path.Combine(root, "Tools"),
                ModelRoot = Path.Combine(root, "Models"),
                Port = port
            });
            Assert.IsTrue(host.Start());

            using var client = new System.Net.Http.HttpClient();
            string body = JsonSerializer.Serialize(new LlmIdeContextRequest
            {
                WorkspaceRoot = root,
                ActiveFilePath = Path.Combine(root, "EndpointSample.cs"),
                ActiveFileText = File.ReadAllText(Path.Combine(root, "EndpointSample.cs")),
                SelectionText = "// TODO: finish",
                Prompt = "Fix TODO",
                CursorLine = 1
            });

            string capabilities = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/ide/capabilities");
            string completion = await Post(client, port, "/api/v1/ide/completions/inline", body);
            string plan = await Post(client, port, "/api/v1/ide/plan", body);
            string search = await Post(client, port, "/api/v1/ide/search", "{\"workspace_root\":\"" + JsonEsc(root) + "\",\"query\":\"EndpointSample\"}");

            using var capDoc = JsonDocument.Parse(capabilities);
            using var completionDoc = JsonDocument.Parse(completion);
            using var planDoc = JsonDocument.Parse(plan);
            using var searchDoc = JsonDocument.Parse(search);

            Assert.IsTrue(capDoc.RootElement.GetProperty("inlineCompletion").GetBoolean());
            Assert.IsTrue(completionDoc.RootElement.TryGetProperty("text", out _));
            Assert.AreEqual(4, planDoc.RootElement.GetProperty("steps").GetArrayLength());
            Assert.IsTrue(searchDoc.RootElement.GetProperty("results").GetArrayLength() > 0);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private static async Task<string> Post(System.Net.Http.HttpClient client, int port, string path, string body)
    {
        using var response = await client.PostAsync($"http://127.0.0.1:{port}{path}", new StringContent(body, Encoding.UTF8, "application/json"));
        return await response.Content.ReadAsStringAsync();
    }

    private static string JsonEsc(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
