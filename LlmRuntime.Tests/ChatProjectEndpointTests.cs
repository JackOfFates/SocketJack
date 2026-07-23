using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    private static LmVsProxy CreateProxy(string root)
    {
        var proxy = new LmVsProxy("127.0.0.1", NextPort(), NextPort(), NextPort(), root) { PublicAccessEnabled = false };
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
    private static int NextPort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
