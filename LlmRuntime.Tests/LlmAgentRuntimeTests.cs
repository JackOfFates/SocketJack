using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmAgentRuntimeTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void AgentRuntime_CreatesDurableSessionAndPlan()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        string workspace = Path.Combine(root, "workspace");
        try
        {
            var runtime = new LlmAgentRuntime(new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = workspace });
            var session = runtime.CreateSession("Build a useful thing");
            runtime.SavePlan(session.Id, [new LlmAgentPlanStep { Title = "Inspect", Status = "completed" }, new LlmAgentPlanStep { Title = "Patch" }]);

            var reloaded = new LlmAgentRuntime(new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = workspace });
            var saved = reloaded.GetSession(session.Id);

            Assert.AreEqual("Build a useful thing", saved.Goal);
            Assert.AreEqual(2, saved.Plan.Count);
            Assert.IsTrue(saved.Events.Any(e => e.Kind == "plan.saved"));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void AgentRuntime_FilePreviewAndApprovedWriteRespectSandbox()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        string workspace = Path.Combine(root, "workspace");
        try
        {
            var runtime = new LlmAgentRuntime(new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = workspace });
            var session = runtime.CreateSession("Edit files", sandbox: LlmAgentSandboxProfile.WorkspaceWrite);

            var preview = runtime.PreviewWriteFile(session.Id, "notes.txt", "hello");
            Assert.IsFalse(preview.Applied);
            StringAssert.Contains(preview.Diff, "+hello");

            Assert.ThrowsException<InvalidOperationException>(() => runtime.WriteFile(session.Id, "notes.txt", "hello", approved: false));

            var written = runtime.WriteFile(session.Id, "notes.txt", "hello", approved: true);
            Assert.IsTrue(written.Applied);
            Assert.AreEqual("hello", runtime.ReadFile(session.Id, "notes.txt").Content);
            Assert.ThrowsException<InvalidOperationException>(() => runtime.PreviewWriteFile(session.Id, "..\\outside.txt", "nope"));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task AgentRuntime_TerminalRequiresApprovalAndCapturesLogs()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        string workspace = Path.Combine(root, "workspace");
        try
        {
            var runtime = new LlmAgentRuntime(new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = workspace });
            var session = runtime.CreateSession("Run command");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runtime.RunCommandAsync(session.Id, "Write-Output blocked", approved: false));

            var result = await runtime.RunCommandAsync(session.Id, "Write-Output agent-ok", approved: true);
            Assert.AreEqual(0, result.ExitCode);
            StringAssert.Contains(result.Output, "agent-ok");
            Assert.AreEqual("Command succeeded.", result.Diagnosis);

            var checks = await runtime.RunCheckLoopAsync(session.Id, ["Write-Output check-ok"], approved: true);
            Assert.IsTrue(checks.Success);
            Assert.AreEqual("All checks passed.", checks.Diagnosis);

            var correction = runtime.CreateSelfCorrectionPlan(session.Id, "Compiler error");
            Assert.AreEqual(4, correction.Count);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void AgentRuntime_DiscoversRepoInstructionsAndAutomationHooks()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "instructions");
            File.WriteAllText(Path.Combine(root, "SocketJack.sln"), "");
            Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "demo"));
            File.WriteAllText(Path.Combine(root, ".agents", "skills", "demo", "SKILL.md"), "skill");

            var runtime = new LlmAgentRuntime(new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = root });
            var context = runtime.DiscoverRepoContext();
            var hook = runtime.UpsertAutomationHook(new LlmAgentAutomationHook { Name = "Nightly", Prompt = "Run checks", Schedule = "daily" });

            Assert.AreEqual(1, context.InstructionFiles.Count);
            Assert.AreEqual(1, context.SolutionFiles.Count);
            Assert.AreEqual(1, context.SkillFiles.Count);
            Assert.AreEqual(hook.Id, runtime.ListAutomationHooks()[0].Id);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task AgentEndpoints_CreatePlanPreviewAndWriteFile()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        string workspace = Path.Combine(root, "workspace");
        int port = NextPort();
        try
        {
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions
            {
                ModelRoot = Path.Combine(root, "models"),
                ToolRoot = Path.Combine(root, "tools"),
                AgentRoot = Path.Combine(root, "agents"),
                DefaultWorkspaceRoot = workspace,
                Port = port
            });
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/agent/sessions",
                new StringContent("{\"goal\":\"Endpoint task\",\"sandbox\":\"workspaceWrite\"}", Encoding.UTF8, "application/json"));
            string createBody = await createResponse.Content.ReadAsStringAsync();
            using var createDocument = JsonDocument.Parse(createBody);
            string sessionId = createDocument.RootElement.GetProperty("session").GetProperty("id").GetString()!;

            var planResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/agent/plan",
                new StringContent("{\"session_id\":\"" + sessionId + "\",\"steps\":[\"Inspect\",\"Patch\"]}", Encoding.UTF8, "application/json"));
            planResponse.EnsureSuccessStatusCode();

            var previewResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/agent/files/preview",
                new StringContent("{\"session_id\":\"" + sessionId + "\",\"path\":\"endpoint.txt\",\"content\":\"from endpoint\"}", Encoding.UTF8, "application/json"));
            string previewBody = await previewResponse.Content.ReadAsStringAsync();
            StringAssert.Contains(previewBody, "from endpoint");

            var writeResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/agent/files/write",
                new StringContent("{\"session_id\":\"" + sessionId + "\",\"path\":\"endpoint.txt\",\"content\":\"from endpoint\",\"approved\":true}", Encoding.UTF8, "application/json"));
            writeResponse.EnsureSuccessStatusCode();

            Assert.AreEqual("from endpoint", File.ReadAllText(Path.Combine(workspace, "endpoint.txt")));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }
}
