using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using heirowLLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class AgentBuilderSqlEndpointTests
{
    private const string Username = "feature-view-test";

    [TestMethod]
    public async Task AgentBuilderAndSqlViewsAreRegisteredAndPermissionGated()
    {
        string root = Path.Combine(Path.GetTempPath(), "heirowllm-feature-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var proxy = new HeirowLlm("127.0.0.1", NextPort(), NextPort(), NextPort(), root)
            {
                PublicAccessEnabled = false
            };

            ChatClientPermissionSnapshot permissions = proxy.GetChatClientPermissionsDiagnostics("webauth:" + Username);
            permissions.AgentBuilder = true;
            permissions.SqlAdmin = true;
            proxy.SaveChatClientPermissionsDiagnostics(permissions);
            Assert.IsTrue(proxy.ChatServer.Listen());

            using var client = new NetHttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxy.ChatServerPort}/"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            await Authenticate(proxy, client);

            string chat = await client.GetStringAsync("/");
            StringAssert.Contains(chat, "workspaceBuilderTab");
            StringAssert.Contains(chat, "workspaceSqlTab");

            using HttpResponseMessage builder = await client.GetAsync("/Builder");
            Assert.AreEqual(HttpStatusCode.OK, builder.StatusCode);
            StringAssert.Contains(await builder.Content.ReadAsStringAsync(), "New Builder Workflow");

            using HttpResponseMessage builderSession = await client.GetAsync("/api/agentbuilder/session");
            Assert.AreEqual(HttpStatusCode.OK, builderSession.StatusCode);
            StringAssert.Contains(await builderSession.Content.ReadAsStringAsync(), "\"authenticated\":true");

            using HttpResponseMessage sql = await client.GetAsync("/sql");
            Assert.AreEqual(HttpStatusCode.OK, sql.StatusCode);

            permissions.AgentBuilder = false;
            permissions.SqlAdmin = false;
            proxy.SaveChatClientPermissionsDiagnostics(permissions);

            using HttpResponseMessage blockedBuilder = await client.GetAsync("/Builder");
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedBuilder.StatusCode);
            using HttpResponseMessage blockedBuilderSession = await client.GetAsync("/api/agentbuilder/session");
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedBuilderSession.StatusCode);
            using HttpResponseMessage blockedSql = await client.GetAsync("/sql");
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedSql.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Authenticate(HeirowLlm proxy, NetHttpClient client)
    {
        const string password = "correct horse battery staple";
        MethodInfo requestRegistration = typeof(HeirowLlm).GetMethod(
            "HandleWebAuthRegistrationRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var request = new SocketJack.Net.HttpRequest
        {
            Method = "POST",
            Path = "/api/web-auth/registration-request",
            Body = JsonSerializer.Serialize(new { username = Username, password })
        };
        requestRegistration.Invoke(proxy, new object?[] { null, request });
        proxy.ApproveWebAuthRegistrationRequest(proxy.GetPendingWebAuthRegistrationRequests().Single().Id);
        using HttpResponseMessage login = await client.PostAsync(
            "/api/web-auth/login",
            new StringContent(JsonSerializer.Serialize(new { username = Username, password, remember = true }), Encoding.UTF8, "application/json"));
        login.EnsureSuccessStatusCode();
    }

    private static int NextPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
