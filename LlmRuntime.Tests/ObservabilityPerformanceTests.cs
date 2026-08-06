using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class ObservabilityPerformanceTests
{
    [TestMethod]
    [TestCategory("Performance")]
    public async Task SeventyMegabyteDatabaseHasFastReadOnlyCachedObservability()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-observability-" + Guid.NewGuid().ToString("N"));
        try
        {
            using LmVsProxy proxy = CreateProxy(root);
            using var client = new NetHttpClient { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.ConnectionClose = true;
            CreateAdministrator(proxy);
            await Post(client, "/api/web-auth/login", new { username = "perf-admin", password = "correct horse battery staple", remember = true });
            string largeContent = new('x', 34 * 1024 * 1024); // Protected V2/base64 storage expands this to roughly 70 MB.
            await Post(client, "/api/chat-session", new
            {
                id = "large_session",
                title = "Large telemetry fixture",
                messages = new[] { new { role = "user", content = largeContent } },
                files = new[] { new { name = "fixture.bin", type = "application/octet-stream", path = "fixture.bin" } }
            });
            largeContent = "";
            ForcePendingSave(proxy);

            string databasePath = Path.Combine(root, "SocketJack", "JackLLMChat", "SocketJackDatabase.json");
            FileInfo database = new(databasePath);
            Assert.IsTrue(database.Exists);
            Assert.IsTrue(database.Length >= 60L * 1024 * 1024, $"Expected an approximately 70 MB fixture; actual size was {database.Length} bytes.");

            JsonElement warm = await WaitForSessionCount(client, 1);
            Assert.AreEqual(1, ReadInt(warm, "ChatSessions", "chatSessions"));
            Assert.IsTrue(HasProperty(warm, "SnapshotAgeMs", "snapshotAgeMs"));
            Assert.IsTrue(HasProperty(warm, "Refreshing", "refreshing"));
            Assert.IsTrue(HasProperty(warm, "DegradedSections", "degradedSections"));

            DateTime writeBefore = File.GetLastWriteTimeUtc(databasePath);
            var elapsed = new List<long>(30);
            MethodInfo cachedSnapshot = typeof(LmVsProxy).GetMethod(
                "GetCachedObservabilityDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int poll = 0; poll < 30; poll++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                object snapshot = cachedSnapshot.Invoke(proxy, null)!;
                using JsonDocument response = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
                Assert.AreEqual(1, ReadInt(response.RootElement, "ChatSessions", "chatSessions"));
                elapsed.Add(stopwatch.ElapsedMilliseconds);
            }
            DateTime writeAfter = File.GetLastWriteTimeUtc(databasePath);

            elapsed.Sort();
            long p95 = elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1];
            Assert.IsTrue(p95 < 250, $"Warm observability p95 was {p95} ms.");
            Assert.AreEqual(writeBefore, writeAfter, "Read-only observability polls must not dirty or rewrite the database.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<JsonElement> WaitForSessionCount(NetHttpClient client, int expected)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            using JsonDocument response = await Get(client, "/api/observability");
            JsonElement root = response.RootElement;
            if (ReadInt(root, "ChatSessions", "chatSessions") == expected)
                return root.Clone();
            await Task.Delay(100);
        }
        Assert.Fail("Observability cache did not refresh after the session mutation.");
        return default;
    }

    private static int ReadInt(JsonElement root, params string[] names)
    {
        foreach (string name in names)
            if (root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result))
                return result;
        return -1;
    }

    private static bool HasProperty(JsonElement root, params string[] names) =>
        names.Any(name => root.TryGetProperty(name, out _));

    private static void ForcePendingSave(LmVsProxy proxy)
    {
        FieldInfo field = typeof(LmVsProxy).GetField("_chatSessionData", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object dataServer = field.GetValue(proxy)!;
        dataServer.GetType().GetMethod("SaveIfDirty", BindingFlags.Instance | BindingFlags.Public)!.Invoke(dataServer, null);
    }

    private static void CreateAdministrator(LmVsProxy proxy)
    {
        MethodInfo requestRegistration = typeof(LmVsProxy).GetMethod(
            "HandleWebAuthRegistrationRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var request = new HttpRequest
        {
            Method = "POST",
            Path = "/api/web-auth/registration-request",
            Body = """{"username":"perf-admin","password":"correct horse battery staple"}"""
        };
        requestRegistration.Invoke(proxy, new object?[] { null, request });
        var pending = proxy.GetPendingWebAuthRegistrationRequests().Single();
        proxy.ApproveWebAuthRegistrationRequest(pending.Id);
    }

    private static async Task<JsonDocument> Get(NetHttpClient client, string path) =>
        JsonDocument.Parse(await client.GetStringAsync(path));

    private static async Task Post(NetHttpClient client, string path, object payload)
    {
        using var response = await client.PostAsync(path, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
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

    private static int NextPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
