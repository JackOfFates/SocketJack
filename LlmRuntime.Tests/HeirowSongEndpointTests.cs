using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using NetHttpClient = System.Net.Http.HttpClient;
using NetHttpClientHandler = System.Net.Http.HttpClientHandler;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class HeirowSongEndpointTests
{
    [TestMethod]
    public async Task CreatorRouteCrudAndLocalGenerationRoundTrip()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            proxy.HeirowSongMediaExecutor = new FakeSongExecutor(root);
            bool installerCalled = false;
            proxy.HeirowSongInstallRequestedAsync = _ => { installerCalled = true; return Task.FromResult("heirowSong install queued for test."); };
            using var client = CreateClient(proxy);
            await Authenticate(proxy, client, "song-owner");

            string html = await GetText(client, "/heirowSong");
            StringAssert.Contains(html, "<title>heirowSong</title>");
            StringAssert.Contains(html, "What will you create?");
            StringAssert.Contains(html, "12-stem extraction");
            StringAssert.Contains(html, "placeholder=\"[Verse]&#10;Write or paste your lyrics…\"");
            Assert.IsFalse(html.Contains("[Verse]\\nWrite or paste your lyrics", StringComparison.Ordinal));
            StringAssert.Contains(html, "id=\"installHeirowSong\"");
            StringAssert.Contains(html, "id=\"installCardTitle\"");
            StringAssert.Contains(html, "id=\"modelInstallState\"");
            StringAssert.Contains(html, "Models installed · runtime unavailable");
            StringAssert.Contains(html, "installButton.hidden=modelsInstalled||ready");
            StringAssert.Contains(html, "ACE-Step runtime needs attention");
            using JsonDocument install = await Post(client, "/api/heirowsong/install", new { });
            Assert.IsTrue(installerCalled);
            StringAssert.Contains(install.RootElement.GetProperty("message").GetString(), "install queued");
            string webChat = await GetText(client, "/");
            StringAssert.Contains(webChat, "id=\"heirowSongLauncher\"");
            StringAssert.Contains(webChat, "id=\"heirowSongFeatureToast\"");

            using JsonDocument saved = await Post(client, "/api/heirowsong/projects/save", new { title = "Endpoint Song", concept = "Warm synthwave", revision = 0, tracks = Array.Empty<object>() });
            string projectId = saved.RootElement.GetProperty("project").GetProperty("id").GetString()!;
            Assert.AreEqual(1, saved.RootElement.GetProperty("project").GetProperty("revision").GetInt32());

            using JsonDocument started = await Post(client, "/api/heirowsong/jobs", new { projectId, operation = "text2music", prompt = "Warm synthwave", durationSeconds = 60, batchSize = 2, outputFormat = "wav" });
            string jobId = started.RootElement.GetProperty("job").GetProperty("id").GetString()!;
            using JsonDocument finished = await WaitForJob(client, jobId);
            Assert.AreEqual("completed", finished.RootElement.GetProperty("job").GetProperty("state").GetString());
            Assert.AreEqual(2, finished.RootElement.GetProperty("job").GetProperty("artifactIds").GetArrayLength());

            using JsonDocument project = await Get(client, "/api/heirowsong/projects/" + projectId);
            Assert.AreEqual(1, project.RootElement.GetProperty("project").GetProperty("versions").GetArrayLength());
            Assert.AreEqual("", project.RootElement.GetProperty("project").GetProperty("artifacts")[0].GetProperty("filePath").GetString(), "Raw artifact paths must never cross the owner API boundary.");
            string artifactId = project.RootElement.GetProperty("project").GetProperty("artifacts")[0].GetProperty("id").GetString()!;
            using HttpResponseMessage audio = await client.GetAsync("/api/heirowsong/artifacts/" + artifactId);
            Assert.AreEqual(HttpStatusCode.OK, audio.StatusCode);
            Assert.AreEqual("audio/wav", audio.Content.Headers.ContentType?.MediaType);
            Assert.IsTrue((await audio.Content.ReadAsByteArrayAsync()).Length > 16);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AuthenticationOwnerIsolationRevisionAndUploadLimitsAreEnforced()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            proxy.HeirowSongMediaExecutor = new FakeSongExecutor(root);
            using var anonymous = CreateClient(proxy);
            using HttpResponseMessage blockedPage = await anonymous.GetAsync("/heirowSong");
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedPage.StatusCode);
            StringAssert.Contains(await blockedPage.Content.ReadAsStringAsync(), "Workstation sign-in required");
            using HttpResponseMessage blockedApi = await anonymous.GetAsync("/api/heirowsong/projects");
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedApi.StatusCode);
            using HttpResponseMessage blockedInstall = await anonymous.PostAsync("/api/heirowsong/install", Json(new { }));
            Assert.AreEqual(HttpStatusCode.Forbidden, blockedInstall.StatusCode);

            using var owner = CreateClient(proxy);
            using var other = CreateClient(proxy);
            await Authenticate(proxy, owner, "song-owner-a");
            await Authenticate(proxy, other, "song-owner-b");
            using JsonDocument saved = await Post(owner, "/api/heirowsong/projects/save", new { id = "songproject_private", title = "Private", revision = 0, tracks = Array.Empty<object>() });

            using HttpResponseMessage foreign = await other.GetAsync("/api/heirowsong/projects/songproject_private");
            Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
            using JsonDocument shared = await Post(owner, "/api/heirowsong/shares", new { projectId = "songproject_private", recipientUserName = "song-owner-b", canRead = true, canDownload = false, canRemix = false });
            string shareId = shared.RootElement.GetProperty("share").GetProperty("id").GetString()!;
            using HttpResponseMessage granted = await other.GetAsync("/api/heirowsong/projects/songproject_private");
            Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode);
            await Post(owner, "/api/heirowsong/shares/revoke", new { shareId });
            using HttpResponseMessage revoked = await other.GetAsync("/api/heirowsong/projects/songproject_private");
            Assert.AreEqual(HttpStatusCode.Forbidden, revoked.StatusCode);
            using HttpResponseMessage stale = await owner.PostAsync("/api/heirowsong/projects/save", Json(new { id = "songproject_private", title = "Stale", revision = 0, tracks = Array.Empty<object>() }));
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

            using HttpResponseMessage badType = await owner.PostAsync("/api/heirowsong/uploads", Json(new { projectId = "songproject_private", fileName = "payload.exe", mediaType = "application/octet-stream", base64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }));
            Assert.AreEqual(HttpStatusCode.BadRequest, badType.StatusCode);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task JobCancellationAndStudioTimelinePersistThroughApi()
    {
        string root = TempRoot();
        try
        {
            using var proxy = CreateProxy(root);
            var executor = new FakeSongExecutor(root) { WaitForCancellation = true };
            proxy.HeirowSongMediaExecutor = executor;
            using var client = CreateClient(proxy);
            await Authenticate(proxy, client, "song-cancel");
            using JsonDocument saved = await Post(client, "/api/heirowsong/projects/save", new
            {
                id = "songproject_studio", title = "Studio", revision = 0,
                tracks = new[] { new { name = "Vocals", gainDb = -2.5, pan = .2, pitchSemitones = 1, tempo = 1.05, regions = new[] { new { timelineStartSeconds = 2, sourceStartSeconds = 1, durationSeconds = 8, fadeInSeconds = .1, fadeOutSeconds = .2, takeLane = 2 } } } }
            });
            Assert.AreEqual(1, saved.RootElement.GetProperty("project").GetProperty("tracks").GetArrayLength());
            using JsonDocument started = await Post(client, "/api/heirowsong/jobs", new { projectId = "songproject_studio", operation = "text2music", prompt = "Cancel me" });
            string jobId = started.RootElement.GetProperty("job").GetProperty("id").GetString()!;
            await Post(client, "/api/heirowsong/jobs/cancel", new { jobId });
            using JsonDocument finished = await WaitForJob(client, jobId);
            Assert.AreEqual("cancelled", finished.RootElement.GetProperty("job").GetProperty("state").GetString());
        }
        finally { TryDelete(root); }
    }

    private static HeirowLlm CreateProxy(string root) { var proxy = new HeirowLlm("127.0.0.1", NextPort(), NextPort(), NextPort(), root) { PublicAccessEnabled = false }; Assert.IsTrue(proxy.ChatServer.Listen()); return proxy; }
    private static NetHttpClient CreateClient(HeirowLlm proxy) => new(new NetHttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{proxy.ChatServerPort}/"), Timeout = TimeSpan.FromSeconds(20) };
    private static async Task<string> GetText(NetHttpClient client, string path) { using HttpResponseMessage response = await client.GetAsync(path); string body = await response.Content.ReadAsStringAsync(); if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GET {path} failed with HTTP {(int)response.StatusCode}: {body}"); return body; }
    private static async Task<JsonDocument> Get(NetHttpClient client, string path) { using HttpResponseMessage response = await client.GetAsync(path); string body = await response.Content.ReadAsStringAsync(); if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GET {path} failed with HTTP {(int)response.StatusCode}: {body}"); return JsonDocument.Parse(body); }
    private static async Task<JsonDocument> Post(NetHttpClient client, string path, object payload) { using HttpResponseMessage response = await client.PostAsync(path, Json(payload)); string body = await response.Content.ReadAsStringAsync(); if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"POST {path} failed with HTTP {(int)response.StatusCode}: {body}"); return JsonDocument.Parse(body); }
    private static async Task<JsonDocument> WaitForJob(NetHttpClient client, string jobId) { for (int attempt = 0; attempt < 100; attempt++) { JsonDocument status = await Get(client, "/api/heirowsong/jobs/" + jobId); string state = status.RootElement.GetProperty("job").GetProperty("state").GetString() ?? ""; if (state is "completed" or "failed" or "cancelled" or "interrupted") return status; status.Dispose(); await Task.Delay(30); } throw new TimeoutException("Song job did not reach a terminal state."); }
    private static async Task Authenticate(HeirowLlm proxy, NetHttpClient client, string username)
    {
        const string password = "correct horse battery staple";
        MethodInfo registration = typeof(HeirowLlm).GetMethod("HandleWebAuthRegistrationRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
        registration.Invoke(proxy, new object?[] { null, new HttpRequest { Method = "POST", Path = "/api/web-auth/registration-request", Body = JsonSerializer.Serialize(new { username, password }) } });
        var pending = proxy.GetPendingWebAuthRegistrationRequests().Single(value => value.UserName == username);
        proxy.ApproveWebAuthRegistrationRequest(pending.Id);
        using JsonDocument login = await Post(client, "/api/web-auth/login", new { username, password, remember = true });
        Assert.IsTrue(login.RootElement.GetProperty("ok").GetBoolean());
    }
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static int NextPort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static string TempRoot() { string root = Path.Combine(Path.GetTempPath(), "HeirowSongTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static void TryDelete(string root) { try { Directory.Delete(root, true); } catch { } }

    private sealed class FakeSongExecutor : IHeirowSongMediaExecutor
    {
        private readonly string _root;
        public bool WaitForCancellation { get; set; }
        public FakeSongExecutor(string root) => _root = root;
        public Task<HeirowSongCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HeirowSongCapabilities { Installed = true, Ready = true, Backend = "test", LocalWorkerCount = 1 });
        public async Task<HeirowSongMediaResult> GenerateAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            var artifacts = new List<HeirowSongMediaArtifact>();
            for (int index = 0; index < request.BatchSize; index++)
            {
                string path = Path.Combine(_root, "fake-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("RIFF\u0018\0\0\0WAVEfmt fake heirowSong audio " + index));
                artifacts.Add(new HeirowSongMediaArtifact { FilePath = path, MediaType = "audio/wav", Kind = "song" });
            }
            return new HeirowSongMediaResult { Success = true, JobId = request.JobId, Artifacts = artifacts };
        }
        public Task<HeirowSongMediaResult> EditAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => GenerateAsync(request, progress, cancellationToken);
        public Task<HeirowSongMediaResult> ExtractStemAsync(HeirowSongMediaRequest request, string stem, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => GenerateAsync(request, progress, cancellationToken);
        public Task<HeirowSongMediaResult> CompleteTrackAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => GenerateAsync(request, progress, cancellationToken);
        public Task<HeirowSongMediaResult> TranscribeMidiAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => GenerateAsync(request, progress, cancellationToken);
        public Task<HeirowSongAdapterResult> TrainAdapterAsync(HeirowSongAdapterRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => Task.FromResult(new HeirowSongAdapterResult());
        public Task<HeirowSongVoiceVerificationResult> VerifyVoiceAsync(HeirowSongVoiceVerificationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new HeirowSongVoiceVerificationResult());
    }
}
