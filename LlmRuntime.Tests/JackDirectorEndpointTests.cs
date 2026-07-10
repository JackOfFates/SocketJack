using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class JackDirectorEndpointTests
{
    [TestMethod]
    public async Task LongShotSplitsIntoModelSizedChunksAndAssembles()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            var executor = new FakeExecutor(root);
            proxy.JackDirectorMediaExecutor = executor;
            using var client = CreateClient(proxy);
            await Post(client, "/api/jackdirector/projects/save", new
            {
                id = "project_chunks", revision = 0, title = "Chunk Film", width = 640, height = 360, fps = 24,
                shots = new[] { new { id = "shot_long", title = "Long", prompt = "A long tracking shot", durationSeconds = 10, renderMode = "text-to-video", keyframeApproved = true } }
            });
            await Post(client, "/api/jackdirector/render", new { projectId = "project_chunks", keyframesOnly = false });
            JsonDocument status = null!;
            for (int i = 0; i < 100; i++)
            {
                status?.Dispose();
                status = await Get(client, "/api/jackdirector/status?projectId=project_chunks");
                string state = status.RootElement.GetProperty("project").GetProperty("status").GetString() ?? "";
                if (state is "completed" or "failed") break;
                await Task.Delay(50);
            }
            using (status)
            {
                Assert.AreEqual("completed", status.RootElement.GetProperty("project").GetProperty("status").GetString());
                Assert.IsFalse(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("project").GetProperty("finalArtifactId").GetString()));
            }
            Assert.AreEqual(3, executor.VideoCalls, "240 frames should be split into 96, 96, and 48-frame jobs.");
            Assert.IsTrue(executor.AssemblyCalls >= 2, "Chunk assembly and final project assembly should both run.");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task RouteAndProjectCrudRoundTrip()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            using var client = CreateClient(proxy);
            string html = await client.GetStringAsync("/JackDirector");
            StringAssert.Contains(html, "<title>JackDirector</title>");
            StringAssert.Contains(html, "Render farm");
            string webChat = await client.GetStringAsync("/");
            StringAssert.Contains(webChat, "id=\"jackDirectorLauncher\"");
            StringAssert.Contains(webChat, "jackdirector-new-badge");
            StringAssert.Contains(webChat, "id=\"jackDirectorFeatureToast\"");

            JsonDocument saved = await Post(client, "/api/jackdirector/projects/save", new
            {
                id = "project_endpoint", revision = 0, title = "Endpoint Film", concept = "A short test film",
                width = 1280, height = 720, fps = 24,
                shots = new[] { new { id = "shot_one", title = "Opening", prompt = "A quiet sunrise", durationSeconds = 4, renderMode = "text-to-video" } }
            });
            Assert.AreEqual(1, saved.RootElement.GetProperty("project").GetProperty("revision").GetInt32());
            JsonDocument list = await Get(client, "/api/jackdirector/projects");
            Assert.AreEqual("Endpoint Film", list.RootElement.GetProperty("projects")[0].GetProperty("title").GetString());
            JsonDocument status = await Get(client, "/api/jackdirector/status?projectId=project_endpoint");
            Assert.AreEqual("project_endpoint", status.RootElement.GetProperty("project").GetProperty("id").GetString());
            await Post(client, "/api/jackdirector/projects/delete", new { projectId = "project_endpoint" });
            list = await Get(client, "/api/jackdirector/projects");
            Assert.AreEqual(0, list.RootElement.GetProperty("projects").GetArrayLength());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task RejectsRevisionConflictAndPublicHttpWorker()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            using var client = CreateClient(proxy);
            await Post(client, "/api/jackdirector/projects/save", new { id = "project_conflict", revision = 0, title = "One", shots = Array.Empty<object>() });
            using HttpResponseMessage conflict = await client.PostAsync("/api/jackdirector/projects/save", Json(new { id = "project_conflict", revision = 0, title = "Stale", shots = Array.Empty<object>() }));
            Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
            using HttpResponseMessage insecure = await client.PostAsync("/api/jackdirector/workers/discover", Json(new { url = "http://example.com:11436", source = "manual" }));
            Assert.AreEqual(HttpStatusCode.BadRequest, insecure.StatusCode);
            StringAssert.Contains(await insecure.Content.ReadAsStringAsync(), "HTTPS");
        }
        finally { TryDelete(root); }
    }

    private static LmVsProxy CreateProxy(string root) { var proxy = new LmVsProxy("127.0.0.1", NextPort(), NextPort(), NextPort(), root) { PublicAccessEnabled = false }; Assert.IsTrue(proxy.ChatServer.Listen()); return proxy; }
    private static NetHttpClient CreateClient(LmVsProxy proxy) => new() { BaseAddress = new Uri(proxy.ChatServerUrl), Timeout = TimeSpan.FromSeconds(15) };
    private static async Task<JsonDocument> Get(NetHttpClient client, string path) => JsonDocument.Parse(await client.GetStringAsync(path));
    private static async Task<JsonDocument> Post(NetHttpClient client, string path, object payload) { using HttpResponseMessage response = await client.PostAsync(path, Json(payload)); string body = await response.Content.ReadAsStringAsync(); response.EnsureSuccessStatusCode(); return JsonDocument.Parse(body); }
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static int NextPort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static string TempRoot() { string root = Path.Combine(Path.GetTempPath(), "JackDirectorTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static void TryDelete(string root) { try { Directory.Delete(root, true); } catch { } }

    private sealed class FakeExecutor : IJackDirectorMediaExecutor
    {
        private readonly string _root;
        public int VideoCalls;
        public int AssemblyCalls;
        public FakeExecutor(string root) => _root = root;
        public Task<JackDirectorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new JackDirectorCapabilities { FfmpegAvailable = true });
        public Task<JackDirectorMediaResult> GenerateImageAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default) => Result("image-" + Guid.NewGuid().ToString("N") + ".png", "image/png");
        public Task<JackDirectorMediaResult> GenerateVideoAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default) { Interlocked.Increment(ref VideoCalls); return Result("video-" + Guid.NewGuid().ToString("N") + ".mp4", "video/mp4"); }
        public Task<JackDirectorMediaResult> ExtractLastFrameAsync(string sourcePath, string outputPath, CancellationToken cancellationToken = default) { Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); File.WriteAllBytes(outputPath, new byte[] { 1, 2, 3 }); return Task.FromResult(new JackDirectorMediaResult { Success = true, ArtifactPath = outputPath, MediaType = "image/png" }); }
        public Task<JackDirectorMediaResult> AssembleAsync(JackDirectorAssemblyRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default) { Interlocked.Increment(ref AssemblyCalls); Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!); File.WriteAllBytes(request.OutputPath, new byte[] { 1, 2, 3, 4 }); return Task.FromResult(new JackDirectorMediaResult { Success = true, ArtifactPath = request.OutputPath, MediaType = "video/mp4" }); }
        private Task<JackDirectorMediaResult> Result(string name, string type) { string path = Path.Combine(_root, name); File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 }); return Task.FromResult(new JackDirectorMediaResult { Success = true, ArtifactPath = path, MediaType = type }); }
    }
}
