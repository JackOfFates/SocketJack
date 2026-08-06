using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
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
