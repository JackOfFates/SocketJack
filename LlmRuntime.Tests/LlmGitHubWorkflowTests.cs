using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmGitHubWorkflowTests
{
    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public void GitHubWorkflow_InspectsRepositoryCreatesBranchAndCommit()
    {
        string root = CreateGitRepository();
        try
        {
            var options = new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = root };
            var agent = new LlmAgentRuntime(options);
            var workflows = new LlmGitHubWorkflowService(options, agent);

            var info = workflows.InspectRepository(root);
            Assert.IsTrue(info.IsGitRepository);
            Assert.AreEqual("master", info.CurrentBranch);

            var denied = workflows.CreateOrSwitchBranch("feature", root, approved: false);
            Assert.IsFalse(denied.Success);
            StringAssert.Contains(denied.Error, "approval");

            var branch = workflows.CreateOrSwitchBranch("feature", root, approved: true);
            Assert.IsTrue(branch.Success, branch.Error);
            Assert.AreEqual("codex/feature", branch.Branch);

            File.WriteAllText(Path.Combine(root, "change.txt"), "change");
            var commit = workflows.Commit("Add change", ["change.txt"], root, approved: true);
            Assert.IsTrue(commit.Success, commit.Error);
            StringAssert.Contains(commit.Summary, "Add change");
            Assert.IsTrue(workflows.ListAudit().Any(entry => entry.Action == "git.commit" && entry.Outcome == "success"));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void GitHubWorkflow_ReviewSecurityDependencyAndPolicyWork()
    {
        string root = CreateGitRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, "Program.cs"), "class C { string token = \"abc123-secret-token\"; }");
            File.WriteAllText(Path.Combine(root, "Todo.cs"), "// TODO: fix this");
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" /></ItemGroup></Project>");

            var options = new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = root };
            var workflows = new LlmGitHubWorkflowService(options, new LlmAgentRuntime(options));

            var review = workflows.ReviewDiff(root);
            Assert.IsTrue(review.Findings.Any(finding => finding.Title.Contains("TODO", StringComparison.OrdinalIgnoreCase)));

            var security = workflows.SecurityReview(root);
            Assert.IsTrue(security.Findings.Any(finding => finding.Title.Contains("secret", StringComparison.OrdinalIgnoreCase)));

            var dependencies = workflows.DependencyUpdatePlan(root);
            Assert.IsTrue(dependencies.PackageFiles.Any(path => path.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase)));

            var policy = workflows.SavePolicy(new LlmAgentPolicy { AllowGitHubWrites = true, AllowPullRequests = true, RequireApprovalForGitWrites = false });
            Assert.IsTrue(policy.AllowGitHubWrites);
            Assert.IsFalse(workflows.GetPolicy().RequireApprovalForGitWrites);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void GitHubWorkflow_StartsTaskAndHandlesMissingGitHubCli()
    {
        string root = CreateGitRepository();
        try
        {
            var options = new LlmRuntimeOptions { AgentRoot = Path.Combine(root, "agents"), DefaultWorkspaceRoot = root };
            var workflows = new LlmGitHubWorkflowService(options, new LlmAgentRuntime(options));

            var item = workflows.FetchIssueOrPullRequest("issue", 123, root);
            Assert.AreEqual(123, item.Number);
            if (!string.IsNullOrWhiteSpace(item.UnavailableReason))
                StringAssert.Contains(item.UnavailableReason, "GitHub CLI");

            var task = workflows.StartAgentTaskFromItem("issue", 123, root);
            Assert.IsNotNull(task.Session);
            Assert.AreEqual("ISSUE #123", task.Session.Goal.Split(':')[0]);

            var actions = workflows.DebugFailedActions(root);
            if (!actions.Available)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(actions.Diagnosis));
                Assert.IsFalse(string.IsNullOrWhiteSpace(actions.Error));
            }

            var pr = workflows.CreateDraftPullRequest("Title", "Body", root, approved: true);
            Assert.IsTrue(pr.Unavailable || !pr.Success);
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task GitHubEndpoints_ReturnRepositoryReviewPolicyAndAudit()
    {
        string root = CreateGitRepository();
        int port = NextPort();
        try
        {
            File.WriteAllText(Path.Combine(root, "Todo.cs"), "// TODO: endpoint");
            using var host = new LlmRuntimeHost(new LlmRuntimeOptions
            {
                ModelRoot = Path.Combine(root, "models"),
                ToolRoot = Path.Combine(root, "tools"),
                AgentRoot = Path.Combine(root, "agents"),
                DefaultWorkspaceRoot = root,
                Port = port
            });
            Assert.IsTrue(host.Start());

            using var client = new System.Net.Http.HttpClient();
            string repoBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/github/repository?workspace_root={Uri.EscapeDataString(root)}");
            using var repoDocument = JsonDocument.Parse(repoBody);
            Assert.IsTrue(repoDocument.RootElement.GetProperty("repository").GetProperty("isGitRepository").GetBoolean());

            var reviewResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/github/review",
                new StringContent("{\"workspace_root\":\"" + JsonEscape(root) + "\"}", Encoding.UTF8, "application/json"));
            string reviewBody = await reviewResponse.Content.ReadAsStringAsync();
            StringAssert.Contains(reviewBody, "TODO");

            var policyResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/github/policy",
                new StringContent("{\"allowGitHubWrites\":true,\"requireApprovalForGitWrites\":false}", Encoding.UTF8, "application/json"));
            policyResponse.EnsureSuccessStatusCode();

            string auditBody = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/github/audit");
            StringAssert.Contains(auditBody, "github.review");
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private static string CreateGitRepository()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        try
        {
            Run("git", "init", root);
            Run("git", "config user.email test@example.com", root);
            Run("git", "config user.name Test User", root);
            File.WriteAllText(Path.Combine(root, "README.md"), "hello");
            Run("git", "add README.md", root);
            Run("git", "commit -m initial", root);
            return root;
        }
        catch (AssertInconclusiveException)
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
            throw;
        }
    }

    private static void Run(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (fileName.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("git.exe is not available in this test environment: " + ex.Message);
            throw;
        }

        using (process)
        {
            if (process == null)
                throw new InvalidOperationException("Process failed to start.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
