using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
using LmVs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;
using NetHttpResponseMessage = System.Net.Http.HttpResponseMessage;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ChatProjectEndpointTests
{
    [TestMethod]
    public async Task ProjectsGroupPinMoveArchiveAndPersistSessions()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-chat-projects-" + Guid.NewGuid().ToString("N"));
        try
        {
            string projectId;
            using (LmVsProxy proxy = CreateProxy(root))
            using (var client = new NetHttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) })
            {
                using JsonDocument created = await Post(client, "/api/chat-project", new { action = "create", name = "SocketJack" });
                projectId = created.RootElement.GetProperty("project").GetProperty("id").GetString()!;
                await Post(client, "/api/chat-session", new { id = "session_a", title = "First task", projectId, messages = new[] { new { role = "user", content = "one" } } });
                await Post(client, "/api/chat-session", new { id = "session_b", title = "Second task", messages = new[] { new { role = "user", content = "two" } } });
                await Post(client, "/api/chat-session-action", new { id = "session_b", action = "assign-project", projectId });
                await Post(client, "/api/chat-session-action", new { id = "session_b", action = "pin" });

                using JsonDocument sessions = await Get(client, "/api/chat-sessions?projectId=" + Uri.EscapeDataString(projectId) + "&take=all");
                JsonElement list = sessions.RootElement.GetProperty("sessions");
                Assert.AreEqual(250, sessions.RootElement.GetProperty("take").GetInt32(),
                    "Unbounded session-list requests must be capped to protect Workstation memory.");
                Assert.AreEqual(2, list.GetArrayLength());
                Assert.AreEqual("session_b", list[0].GetProperty("id").GetString(), "Pinned chats must sort first inside a project.");
                Assert.IsTrue(list[0].GetProperty("pinned").GetBoolean());
                Assert.AreEqual("SocketJack", list[0].GetProperty("projectName").GetString());

                await Post(client, "/api/chat-project", new { action = "pin", projectId });
                await Post(client, "/api/chat-project", new { action = "archive", projectId });
                using JsonDocument visible = await Get(client, "/api/chat-projects");
                Assert.IsFalse(visible.RootElement.GetProperty("projects").EnumerateArray().Any(item => item.GetProperty("id").GetString() == projectId));
                using JsonDocument archived = await Get(client, "/api/chat-projects?includeArchived=true");
                Assert.IsTrue(archived.RootElement.GetProperty("projects").EnumerateArray().Any(item => item.GetProperty("id").GetString() == projectId && item.GetProperty("archived").GetBoolean()));
            }

            using (LmVsProxy reloaded = CreateProxy(root))
            using (var client = new NetHttpClient { BaseAddress = new Uri(reloaded.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) })
            {
                using JsonDocument sessions = await Get(client, "/api/chat-sessions?projectId=" + Uri.EscapeDataString(projectId) + "&take=all");
                Assert.AreEqual(2, sessions.RootElement.GetProperty("sessions").GetArrayLength(), "Project assignments must survive a Workstation restart.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProjectFilesAreSharedDeleteableAndRevertibleThroughVersions()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-project-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            using LmVsProxy proxy = CreateProxy(root);
            using var client = new NetHttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) };
            using JsonDocument created = await Post(client, "/api/chat-project", new { action = "create", name = "Shared Files" });
            string projectId = created.RootElement.GetProperty("project").GetProperty("id").GetString()!;
            await Post(client, "/api/chat-session", new { id = "session_a", title = "A", projectId, messages = Array.Empty<object>() });
            await Post(client, "/api/chat-session", new { id = "session_b", title = "B", projectId, messages = Array.Empty<object>() });

            MethodInfo getFilesRoot = typeof(LmVsProxy).GetMethod("GetChatSessionFilesDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string filesRoot = (string)getFilesRoot.Invoke(proxy, new object[] { "session_a" })!;
            MethodInfo storeFile = typeof(LmVsProxy).GetMethod("StoreChatUploadedSessionFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            storeFile.Invoke(proxy, new object[] { "session_a", "unauthenticated", filesRoot, "note.txt", "", "text/plain", Encoding.UTF8.GetBytes("hello") });

            JsonElement sharedFile = await GetOnlyProjectFile(client, "session_b");
            Assert.AreEqual("note.txt", sharedFile.GetProperty("name").GetString(), "Generated file_ ids must stay hidden from Project Files display names.");

            await Post(client, "/api/project-file-versions", new { action = "create", sessionId = "session_b", name = "Working copy" });
            string virtualPath = sharedFile.GetProperty("path").GetString()!;
            using NetHttpResponseMessage deleted = await client.DeleteAsync("/api/chat-file?sessionId=session_b&kind=session&path=" + Uri.EscapeDataString(virtualPath));
            deleted.EnsureSuccessStatusCode();
            using JsonDocument empty = await Get(client, "/api/chat-solution-explorer?sessionId=session_a&kind=session&path=%5C");
            Assert.AreEqual(0, empty.RootElement.GetProperty("children").GetArrayLength());

            using JsonDocument versions = await Get(client, "/api/project-file-versions?sessionId=session_a");
            string versionId = versions.RootElement.GetProperty("versions")[0].GetProperty("id").GetString()!;
            await Post(client, "/api/project-file-versions", new { action = "restore", sessionId = "session_a", versionId });
            JsonElement restored = await GetOnlyProjectFile(client, "session_b");
            Assert.AreEqual("note.txt", restored.GetProperty("name").GetString());

            string folderRoot = Path.Combine(filesRoot, "docs");
            Directory.CreateDirectory(folderRoot);
            storeFile.Invoke(proxy, new object[] { "session_a", "unauthenticated", folderRoot, "nested.txt", "", "text/plain", Encoding.UTF8.GetBytes("nested") });
            using JsonDocument withFolder = await Get(client, "/api/chat-solution-explorer?sessionId=session_b&kind=session&path=%5C");
            JsonElement sharedFolder = withFolder.RootElement.GetProperty("children").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "directory" && item.GetProperty("name").GetString() == "docs");
            string folderVirtualPath = sharedFolder.GetProperty("path").GetString()!;

            using NetHttpResponseMessage folderDeleted = await client.DeleteAsync(
                "/api/chat-file?sessionId=session_b&kind=session&type=directory&path=" + Uri.EscapeDataString(folderVirtualPath));
            folderDeleted.EnsureSuccessStatusCode();
            Assert.IsFalse(Directory.Exists(folderRoot));
            using JsonDocument afterFolderDelete = await Get(client, "/api/chat-solution-explorer?sessionId=session_a&kind=session&path=%5C");
            Assert.IsFalse(afterFolderDelete.RootElement.GetProperty("children").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "docs"));

            using NetHttpResponseMessage rootDeleteBlocked = await client.DeleteAsync(
                "/api/chat-file?sessionId=session_a&kind=session&type=directory&path=%5C");
            Assert.AreEqual(HttpStatusCode.BadRequest, rootDeleteBlocked.StatusCode);
            Assert.IsTrue(Directory.Exists(filesRoot), "Folder deletion must never remove the Project Files root.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WorkspaceExplorerWritesAndDeletesOnlyInsideReadWriteRoots()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-workspace-explorer-" + Guid.NewGuid().ToString("N"));
        string attachedRoot = Path.Combine(root, "attached");
        Directory.CreateDirectory(attachedRoot);
        try
        {
            using LmVsProxy proxy = CreateProxy(root);
            using var client = new NetHttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) };
            await Post(client, "/api/chat-session", new { id = "workspace_session", title = "Workspace", messages = Array.Empty<object>() });
            ChatWorkspaceRootSnapshot attached = proxy.SaveChatWorkspaceRootDiagnostics(
                "unauthenticated", "workspace_session", "", "attached", "Attached", attachedRoot, "read-write");

            using JsonDocument workspacePayload = await Get(client, "/api/chat-workspaces?sessionId=workspace_session");
            JsonElement attachedPayload = workspacePayload.RootElement.GetProperty("effectiveRoots").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == attached.Id);
            Assert.AreEqual("attached", attachedPayload.GetProperty("role").GetString());
            Assert.AreEqual("read-write", attachedPayload.GetProperty("accessMode").GetString());
            Assert.AreEqual("Attached", attachedPayload.GetProperty("displayName").GetString());

            string writableFile = Path.Combine(attachedRoot, "delete-me.txt");
            File.WriteAllText(writableFile, "writable");
            using NetHttpResponseMessage deleted = await client.DeleteAsync(
                "/api/chat-file?sessionId=workspace_session&kind=workspace&path=" + Uri.EscapeDataString(writableFile));
            deleted.EnsureSuccessStatusCode();
            Assert.IsFalse(File.Exists(writableFile));

            MethodInfo resolveUpload = typeof(LmVsProxy).GetMethod(
                "TryResolveChatUploadDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] writableArgs = { "unauthenticated", "workspace_session", attachedRoot, "workspace", null!, null! };
            Assert.IsTrue((bool)resolveUpload.Invoke(proxy, writableArgs)!);
            Assert.AreEqual(Path.GetFullPath(attachedRoot), writableArgs[4]);

            proxy.SaveChatWorkspaceRootDiagnostics(
                "unauthenticated", "workspace_session", attached.Id, "attached", "Attached", attachedRoot, "read-only");
            string protectedFile = Path.Combine(attachedRoot, "keep-me.txt");
            File.WriteAllText(protectedFile, "read only");
            using NetHttpResponseMessage blocked = await client.DeleteAsync(
                "/api/chat-file?sessionId=workspace_session&kind=workspace&path=" + Uri.EscapeDataString(protectedFile));
            Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.IsTrue(File.Exists(protectedFile));

            object[] readOnlyArgs = { "unauthenticated", "workspace_session", attachedRoot, "workspace", null!, null! };
            Assert.IsFalse((bool)resolveUpload.Invoke(proxy, readOnlyArgs)!);
            StringAssert.Contains((string)readOnlyArgs[5], "read-only");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task VersionControlGroupsResponseChangesRestoresSafelyAndPersistsSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-version-control-" + Guid.NewGuid().ToString("N"));
        string writableRoot = Path.Combine(root, "writable");
        string readOnlyRoot = Path.Combine(root, "readonly");
        Directory.CreateDirectory(writableRoot);
        Directory.CreateDirectory(readOnlyRoot);
        try
        {
            using (LmVsProxy proxy = CreateProxy(root))
            using (var client = new NetHttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) })
            {
                await Post(client, "/api/chat-session", new { id = "version_session", title = "Protected", messages = Array.Empty<object>() });
                proxy.SaveChatWorkspaceRootDiagnostics("unauthenticated", "version_session", "", "attached", "Writable", writableRoot, "read-write");
                proxy.SaveChatWorkspaceRootDiagnostics("unauthenticated", "version_session", "", "attached", "Read only", readOnlyRoot, "read-only");
                string writableFile = Path.Combine(writableRoot, "one.txt");
                string secondFile = Path.Combine(writableRoot, "two.txt");
                string ignoredGitFile = Path.Combine(writableRoot, ".git", "index");
                Directory.CreateDirectory(Path.GetDirectoryName(ignoredGitFile)!);
                File.WriteAllText(writableFile, "before");
                File.WriteAllText(ignoredGitFile, "before");

                ProjectVersionControlDiagnosticsSnapshot defaults = proxy.GetProjectVersionControlDiagnostics("unauthenticated", "version_session");
                Assert.IsTrue(defaults.ProjectAutomaticEnabled);
                Assert.IsTrue(defaults.SessionAutomaticEnabled);
                Assert.IsTrue(defaults.EffectiveAutomaticEnabled);
                Assert.IsTrue(defaults.ProtectedRoots.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(writableRoot), StringComparison.OrdinalIgnoreCase)));
                Assert.IsFalse(defaults.ProtectedRoots.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(readOnlyRoot), StringComparison.OrdinalIgnoreCase)));
	            using JsonDocument apiDefaults = await Get(client, "/api/project-version-control?sessionId=version_session&scope=session&take=25&skip=0");
	            Assert.IsTrue(apiDefaults.RootElement.GetProperty("settings").GetProperty("effectiveAutomaticEnabled").GetBoolean());
	            Assert.IsTrue(apiDefaults.RootElement.GetProperty("protectedRoots").EnumerateArray().Any(path => string.Equals(Path.GetFullPath(path.GetString()!), Path.GetFullPath(writableRoot), StringComparison.OrdinalIgnoreCase)));
	            Assert.IsFalse(apiDefaults.RootElement.GetProperty("protectedRoots").EnumerateArray().Any(path => string.Equals(Path.GetFullPath(path.GetString()!), Path.GetFullPath(readOnlyRoot), StringComparison.OrdinalIgnoreCase)));

                MethodInfo begin = typeof(LmVsProxy).GetMethod("BeginAutomaticVersionControlRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo complete = typeof(LmVsProxy).GetMethod("CompleteAutomaticVersionControlRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
                begin.Invoke(proxy, new object[] { "prompt_test", "unauthenticated", "version_session" });
                File.WriteAllText(writableFile, "after");
                File.WriteAllText(secondFile, "created");
                File.WriteAllText(ignoredGitFile, "after");
                string versionId = (string)complete.Invoke(proxy, new object[] { "prompt_test", "Completed" })!;
                Assert.IsTrue(versionId.StartsWith("version_", StringComparison.Ordinal));

                ProjectVersionControlDiagnosticsSnapshot history = proxy.GetProjectVersionControlDiagnostics("unauthenticated", "version_session");
                Assert.AreEqual(1, history.Versions.Count(version => version.Type == "automatic"));
                ProjectVersionControlVersionSnapshot automatic = history.Versions.Single(version => version.Id == versionId);
                Assert.AreEqual(2, automatic.AffectedFileCount, ".git metadata must not be included in the response restore point.");
                Assert.AreEqual("1.0.1", automatic.SemanticVersion);
                Assert.AreEqual(2, automatic.Additions);
                Assert.AreEqual(1, automatic.Deletions);

                ProjectVersionControlRestoreSnapshot restored = proxy.RestoreProjectVersionControlDiagnostics("unauthenticated", "version_session", versionId);
                Assert.AreEqual("before", File.ReadAllText(writableFile));
                Assert.IsFalse(File.Exists(secondFile));
                Assert.AreEqual("after", File.ReadAllText(ignoredGitFile));
                Assert.AreEqual(2, restored.RestoredFileCount);

                File.WriteAllText(writableFile, "newer work");
                ProjectVersionControlRestoreSnapshot conflict = proxy.RestoreProjectVersionControlDiagnostics("unauthenticated", "version_session", versionId);
                Assert.AreEqual("newer work", File.ReadAllText(writableFile));
                Assert.IsTrue(conflict.Conflicts.Contains(writableFile));

                begin.Invoke(proxy, new object[] { "prompt_no_change", "unauthenticated", "version_session" });
                Assert.AreEqual("", (string)complete.Invoke(proxy, new object[] { "prompt_no_change", "Completed" })!);

	            using JsonDocument branch = await Post(client, "/api/project-version-control", new { action = "create-branch", sessionId = "version_session", name = "feature-test", scope = "project" });
	            Assert.AreEqual("feature-test", branch.RootElement.GetProperty("activeBranch").GetString());
	            using JsonDocument branchPoint = await Post(client, "/api/project-version-control", new { action = "create-restore-point", sessionId = "version_session", name = "Feature ready", scope = "project" });
	            Assert.AreEqual("1.0.4", branchPoint.RootElement.GetProperty("currentVersion").GetString());
	            using JsonDocument merged = await Post(client, "/api/project-version-control", new { action = "merge-branch", sessionId = "version_session", scope = "project" });
	            Assert.AreEqual("1.1.0", merged.RootElement.GetProperty("currentVersion").GetString());
	            Assert.AreEqual("", merged.RootElement.GetProperty("activeBranch").GetString());
	            using JsonDocument major = await Post(client, "/api/project-version-control", new { action = "increment-major", sessionId = "version_session", scope = "project" });
	            Assert.AreEqual("2.0.0", major.RootElement.GetProperty("currentVersion").GetString());

                proxy.SaveProjectVersionControlDiagnosticsSettings("unauthenticated", "version_session", false, false);
	            using JsonDocument saved = await Post(client, "/api/project-version-control", new { action = "save-settings", sessionId = "version_session", projectAutomaticEnabled = false, sessionAutomaticEnabled = false, scope = "session" });
	            Assert.IsFalse(saved.RootElement.GetProperty("settings").GetProperty("effectiveAutomaticEnabled").GetBoolean());
            }

            using (LmVsProxy reloaded = CreateProxy(root))
            {
                ProjectVersionControlDiagnosticsSnapshot persisted = reloaded.GetProjectVersionControlDiagnostics("unauthenticated", "version_session");
                Assert.IsFalse(persisted.ProjectAutomaticEnabled);
                Assert.IsFalse(persisted.SessionAutomaticEnabled);
                Assert.IsFalse(persisted.EffectiveAutomaticEnabled);
                Assert.AreEqual("2.0.0", persisted.CurrentVersion);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static LmVsProxy CreateProxy(string root)
    {
        var proxy = new LmVsProxy("127.0.0.1", NextPort(), NextPort(), NextPort(), root)
        {
            PublicAccessEnabled = false,
            RequireWorkstationUserAuthentication = false
        };
        Assert.IsTrue(proxy.ChatServer.Listen());
        return proxy;
    }

    private static async Task<JsonDocument> Get(NetHttpClient client, string path) => JsonDocument.Parse(await client.GetStringAsync(path));
    private static async Task<JsonDocument> Post(NetHttpClient client, string path, object payload)
    {
        using NetHttpResponseMessage response = await client.PostAsync(path, new System.Net.Http.StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }
    private static async Task<JsonElement> GetOnlyProjectFile(NetHttpClient client, string sessionId)
    {
        using JsonDocument files = await Get(client, "/api/chat-solution-explorer?sessionId=" + Uri.EscapeDataString(sessionId) + "&kind=session&path=%5C");
        JsonElement children = files.RootElement.GetProperty("children");
        Assert.AreEqual(1, children.GetArrayLength());
        return children[0].Clone();
    }
    private static int NextPort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
