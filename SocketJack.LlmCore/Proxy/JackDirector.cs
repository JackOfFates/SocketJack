using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocketJack.Net;

namespace SocketJack.Net;

public interface IJackDirectorMediaExecutor
{
    Task<JackDirectorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<JackDirectorMediaResult> GenerateImageAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default);
    Task<JackDirectorMediaResult> GenerateVideoAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default);
    Task<JackDirectorMediaResult> ExtractLastFrameAsync(string sourcePath, string outputPath, CancellationToken cancellationToken = default);
    Task<JackDirectorMediaResult> AssembleAsync(JackDirectorAssemblyRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default);
}

public sealed class JackDirectorCapabilities
{
    public string MachineName { get; set; } = Environment.MachineName;
    public string OperatingSystem { get; set; } = Environment.OSVersion.ToString();
    public List<string> Providers { get; set; } = new();
    public List<string> ImageModels { get; set; } = new();
    public List<string> VideoModels { get; set; } = new();
    public long DedicatedMemoryBytes { get; set; }
    public long FreeMemoryBytes { get; set; }
    public bool FfmpegAvailable { get; set; }
    public int ActiveJobs { get; set; }
    public int MaxConcurrentVideoJobs { get; set; } = 1;
}

public sealed class JackDirectorMediaRequest
{
    public string JobId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ShotId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceDataUrl { get; set; } = "";
    public string GenerationMode { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;
    public int Frames { get; set; } = 96;
    public double Seconds { get; set; } = 4;
    public int Steps { get; set; } = 20;
    public int? Seed { get; set; }
}

public sealed class JackDirectorAssemblyRequest
{
    public string ProjectId { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public List<string> ClipPaths { get; set; } = new();
    public string AudioPath { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;
}

public sealed class JackDirectorMediaResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public string ArtifactPath { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public string JobId { get; set; } = "";
}

public sealed class JackDirectorProgress
{
    public double Percent { get; set; }
    public string Message { get; set; } = "";
}

public sealed class JackDirectorProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "project_" + Guid.NewGuid().ToString("N");
    public string OwnerKey { get; set; } = "";
    public string Title { get; set; } = "Untitled production";
    public string Concept { get; set; } = "";
    public int Revision { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;
    public string DefaultImageModel { get; set; } = "";
    public string DefaultVideoModel { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string Status { get; set; } = "draft";
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public string FinalArtifactId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<JackDirectorShot> Shots { get; set; } = new();
    public List<JackDirectorArtifact> Artifacts { get; set; } = new();
    public List<JackDirectorAuditEvent> Audit { get; set; } = new();
}

public sealed class JackDirectorShot
{
    public string Id { get; set; } = "shot_" + Guid.NewGuid().ToString("N");
    public int Order { get; set; }
    public string Title { get; set; } = "Shot";
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public double DurationSeconds { get; set; } = 4;
    public string ContinuityGroupId { get; set; } = "";
    public string RenderMode { get; set; } = "keyframe-to-video";
    public string ImageModel { get; set; } = "";
    public string VideoModel { get; set; } = "";
    public string PreferredWorkerId { get; set; } = "";
    public int? Seed { get; set; }
    public string ReferencePath { get; set; } = "";
    public string KeyframeArtifactId { get; set; } = "";
    public bool KeyframeApproved { get; set; }
    public string ClipArtifactId { get; set; } = "";
    public string EndingFrameArtifactId { get; set; } = "";
    public string Status { get; set; } = "draft";
    public double Progress { get; set; }
    public string Message { get; set; } = "";
}

public sealed class JackDirectorArtifact
{
    public string Id { get; set; } = "artifact_" + Guid.NewGuid().ToString("N");
    public string ShotId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JackDirectorAuditEvent
{
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class JackDirectorWorker
{
    public string Id { get; set; } = "worker_" + Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Source { get; set; } = "manual";
    public string TrustState { get; set; } = "candidate";
    public bool Rental { get; set; }
    public bool LeaseActive { get; set; }
    public bool Quarantined { get; set; }
    public string Secret { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public int RecentFailures { get; set; }
    public double LatencyMs { get; set; }
    public JackDirectorCapabilities Capabilities { get; set; } = new();
}

public sealed class JackDirectorWorkerJob
{
    public string Id { get; set; } = "remotejob_" + Guid.NewGuid().ToString("N");
    public string State { get; set; } = "queued";
    public string Kind { get; set; } = "video";
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public JackDirectorMediaResult Result { get; set; }
}

public sealed class JackDirectorRenderTask
{
    public string Id { get; set; } = "task_" + Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string ShotId { get; set; } = "";
    public string Kind { get; set; } = "video";
    public string WorkerId { get; set; } = "local";
    public string State { get; set; } = "queued";
    public int Attempt { get; set; }
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    public string ArtifactId { get; set; } = "";
}

internal sealed class JackDirectorService : IDisposable
{
    private readonly LmVsProxy _proxy;
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, JackDirectorProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JackDirectorWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JackDirectorRenderTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PairRequest> _pairRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JackDirectorWorkerJob> _workerJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _workerJobCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(35) };
    private readonly object _saveLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private System.Net.Sockets.UdpClient _lanBeacon;
    private const int LanDiscoveryPort = 11437;

    public JackDirectorService(LmVsProxy proxy, string root)
    {
        _proxy = proxy;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(ProjectRoot);
        Directory.CreateDirectory(ArtifactRoot);
        LoadState();
        StartLanBeacon();
        StartHeartbeatLoop();
    }

    private string ProjectRoot => Path.Combine(_root, "Projects");
    private string ArtifactRoot => Path.Combine(_root, "Artifacts");
    private string WorkersPath => Path.Combine(_root, "workers.json");

    public object ListProjects(string ownerKey) => new { ok = true, projects = _projects.Values.Where(p => OwnerMatches(ownerKey, p.OwnerKey)).OrderByDescending(p => p.UpdatedUtc).ToArray() };

    public JackDirectorProject SaveProject(JackDirectorProject incoming, string ownerKey)
    {
        if (incoming == null) throw new ArgumentException("Project payload is required.");
        if (string.IsNullOrWhiteSpace(incoming.Id)) incoming.Id = "project_" + Guid.NewGuid().ToString("N");
        incoming.Id = SafeId(incoming.Id, "project_");
        incoming.OwnerKey = ownerKey;
        incoming.Width = Math.Clamp(incoming.Width, 256, 4096);
        incoming.Height = Math.Clamp(incoming.Height, 256, 4096);
        incoming.Fps = Math.Clamp(incoming.Fps, 1, 60);
        incoming.Shots ??= new();
        incoming.Artifacts ??= new();
        incoming.Audit ??= new();
        if (_projects.TryGetValue(incoming.Id, out JackDirectorProject existing))
        {
            if (!OwnerMatches(ownerKey, existing.OwnerKey)) throw new UnauthorizedAccessException("Project belongs to another owner.");
            if (incoming.Revision != existing.Revision) throw new InvalidOperationException("Project revision conflict. Refresh before saving.");
            incoming.CreatedUtc = existing.CreatedUtc;
            incoming.Revision++;
        }
        else incoming.Revision = 1;
        int order = 0;
        foreach (JackDirectorShot shot in incoming.Shots)
        {
            shot.Id = SafeId(shot.Id, "shot_");
            shot.Order = order++;
            shot.DurationSeconds = Math.Clamp(shot.DurationSeconds, 0.25, 600);
        }
        incoming.UpdatedUtc = DateTimeOffset.UtcNow;
        _projects[incoming.Id] = incoming;
        SaveProjectFile(incoming);
        return incoming;
    }

    public void DeleteProject(string projectId, string ownerKey)
    {
        JackDirectorProject project = GetProject(projectId, ownerKey);
        Cancel(project.Id);
        _projects.TryRemove(project.Id, out _);
        string path = ProjectPath(project.Id);
        if (File.Exists(path)) File.Delete(path);
    }

    public JackDirectorProject GetProject(string projectId, string ownerKey)
    {
        if (!_projects.TryGetValue(SafeId(projectId, "project_"), out JackDirectorProject project)) throw new FileNotFoundException("JackDirector project was not found.");
        if (!OwnerMatches(ownerKey, project.OwnerKey)) throw new UnauthorizedAccessException("Project belongs to another owner.");
        return project;
    }

    public object Status(string projectId, string ownerKey)
    {
        JackDirectorProject project = GetProject(projectId, ownerKey);
        return new { ok = true, project, tasks = _tasks.Values.Where(t => t.ProjectId == project.Id).ToArray() };
    }

    public void StartRender(string projectId, string ownerKey, bool keyframesOnly)
    {
        JackDirectorProject project = GetProject(projectId, ownerKey);
        if (_runs.ContainsKey(project.Id)) throw new InvalidOperationException("This project is already rendering.");
        if (_proxy.JackDirectorMediaExecutor == null) throw new InvalidOperationException("JackDirector media execution is unavailable. Select LlmRuntime and ensure JackONNX is ready.");
        var cts = new CancellationTokenSource();
        if (!_runs.TryAdd(project.Id, cts)) throw new InvalidOperationException("This project is already rendering.");
        project.Status = keyframesOnly ? "rendering-keyframes" : "rendering";
        project.Message = "Render queued.";
        SaveProjectFile(project);
        _ = Task.Run(() => RenderProjectAsync(project, keyframesOnly, cts.Token));
    }

    public void Cancel(string projectId)
    {
        if (_runs.TryGetValue(projectId ?? "", out CancellationTokenSource cts)) cts.Cancel();
    }

    private async Task RenderProjectAsync(JackDirectorProject project, bool keyframesOnly, CancellationToken cancellationToken)
    {
        try
        {
            List<IGrouping<string, JackDirectorShot>> groups = project.Shots.OrderBy(s => s.Order)
                .GroupBy(s => string.IsNullOrWhiteSpace(s.ContinuityGroupId) ? s.Id : s.ContinuityGroupId, StringComparer.OrdinalIgnoreCase).ToList();
            int completed = 0;
            var groupTasks = groups.Select(async group =>
            {
                string inheritedReference = "";
                foreach (JackDirectorShot shot in group.OrderBy(s => s.Order))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(shot.ReferencePath)) shot.ReferencePath = inheritedReference;
                    if (keyframesOnly || (shot.RenderMode == "keyframe-to-video" && !shot.KeyframeApproved))
                        await RenderShotMediaAsync(project, shot, true, cancellationToken).ConfigureAwait(false);
                    if (!keyframesOnly && shot.RenderMode != "still")
                    {
                        if (shot.RenderMode == "keyframe-to-video" && !shot.KeyframeApproved)
                        {
                            shot.Status = "awaiting-keyframe-approval";
                            shot.Message = "Approve the keyframe before rendering video.";
                            continue;
                        }
                        await RenderShotMediaAsync(project, shot, false, cancellationToken).ConfigureAwait(false);
                        JackDirectorArtifact boundary = FindArtifact(project, shot.EndingFrameArtifactId);
                        JackDirectorArtifact clip = FindArtifact(project, shot.ClipArtifactId);
                        if (boundary != null) inheritedReference = boundary.FilePath;
                        else if (clip != null) inheritedReference = clip.FilePath;
                    }
                    int done = Interlocked.Increment(ref completed);
                    project.Progress = groups.Count == 0 ? 0 : done * 100d / Math.Max(1, project.Shots.Count);
                    SaveProjectFile(project);
                }
            });
            await Task.WhenAll(groupTasks).ConfigureAwait(false);
            if (keyframesOnly)
            {
                project.Status = "awaiting-keyframe-approval";
                project.Message = "Keyframes are ready for review.";
            }
            else
            {
                List<string> clips = project.Shots.OrderBy(s => s.Order).Select(s => FindArtifact(project, s.ClipArtifactId)?.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
                if (clips.Count == 0) throw new InvalidOperationException("No completed clips are available for assembly.");
                project.Status = "assembling";
                string output = Path.Combine(ProjectArtifactDirectory(project.Id), "final-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".mp4");
                JackDirectorMediaResult assembled = await _proxy.JackDirectorMediaExecutor.AssembleAsync(new JackDirectorAssemblyRequest
                {
                    ProjectId = project.Id, OutputPath = output, ClipPaths = clips, AudioPath = project.AudioPath,
                    Width = project.Width, Height = project.Height, Fps = project.Fps
                }, new Progress<JackDirectorProgress>(p => { project.Progress = p.Percent; project.Message = p.Message; }), cancellationToken).ConfigureAwait(false);
                if (!assembled.Success) throw new InvalidOperationException(assembled.Error);
                JackDirectorArtifact artifact = AddArtifact(project, "", "final", assembled.ArtifactPath, assembled.MediaType);
                project.FinalArtifactId = artifact.Id;
                project.Status = "completed";
                project.Progress = 100;
                project.Message = "Finished video is ready.";
            }
        }
        catch (OperationCanceledException)
        {
            project.Status = "cancelled";
            project.Message = "Render cancelled; completed artifacts were preserved.";
        }
        catch (Exception ex)
        {
            project.Status = ex.Message.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) ? "assembly-blocked" : "failed";
            project.Message = ex.Message;
            project.Audit.Add(new JackDirectorAuditEvent { Category = "render", Status = "failed", Detail = ex.Message });
        }
        finally
        {
            project.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveProjectFile(project);
            if (_runs.TryRemove(project.Id, out CancellationTokenSource run)) run.Dispose();
        }
    }

    private async Task RenderShotMediaAsync(JackDirectorProject project, JackDirectorShot shot, bool image, CancellationToken cancellationToken)
    {
        string kind = image ? "keyframe" : "video";
        var task = new JackDirectorRenderTask { ProjectId = project.Id, ShotId = shot.Id, Kind = kind, State = "running" };
        _tasks[task.Id] = task;
        shot.Status = "rendering-" + kind;
        string model = image ? First(shot.ImageModel, project.DefaultImageModel) : First(shot.VideoModel, project.DefaultVideoModel);
        string source = image ? shot.ReferencePath : First(FindArtifact(project, shot.KeyframeArtifactId)?.FilePath, shot.ReferencePath);
        var request = new JackDirectorMediaRequest
        {
            JobId = task.Id, ProjectId = project.Id, ShotId = shot.Id, ModelId = model, Prompt = shot.Prompt,
            NegativePrompt = shot.NegativePrompt, SourcePath = source, GenerationMode = string.IsNullOrWhiteSpace(source) ? (image ? "text-to-image" : "text-to-video") : (image ? "image-to-image" : "image-to-video"),
            Width = project.Width, Height = project.Height, Fps = project.Fps, Seconds = shot.DurationSeconds,
            Frames = Math.Min(96, Math.Max(1, (int)Math.Ceiling(shot.DurationSeconds * project.Fps))), Seed = shot.Seed
        };
        JackDirectorMediaResult result;
        if (image)
        {
            result = await ExecuteMediaRequestAsync(model, shot.PreferredWorkerId, request, true, project, shot, task, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int totalFrames = Math.Max(1, (int)Math.Ceiling(shot.DurationSeconds * project.Fps));
            int chunkCount = Math.Max(1, (int)Math.Ceiling(totalFrames / 96d));
            var chunkPaths = new List<string>();
            string chunkSource = source;
            result = null;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                int frames = Math.Min(96, totalFrames - chunkIndex * 96);
                request.JobId = task.Id + "_chunk_" + (chunkIndex + 1).ToString(CultureInfo.InvariantCulture);
                request.Frames = frames;
                request.Seconds = frames / (double)project.Fps;
                request.SourcePath = chunkSource;
                request.SourceDataUrl = "";
                request.GenerationMode = string.IsNullOrWhiteSpace(chunkSource) ? "text-to-video" : (chunkIndex == 0 ? "image-to-video" : "video-to-video");
                task.Message = "Rendering chunk " + (chunkIndex + 1).ToString(CultureInfo.InvariantCulture) + " of " + chunkCount.ToString(CultureInfo.InvariantCulture) + ".";
                result = await ExecuteMediaRequestAsync(model, shot.PreferredWorkerId, request, false, project, shot, task, cancellationToken).ConfigureAwait(false);
                if (!result.Success) break;
                string chunkPath = CopyIntoProject(project.Id, shot.Id, "chunk-" + (chunkIndex + 1).ToString(CultureInfo.InvariantCulture), result.ArtifactPath);
                AddArtifact(project, shot.Id, "chunk", chunkPath, result.MediaType);
                chunkPaths.Add(chunkPath);
                chunkSource = chunkPath;
            }
            if (result?.Success == true && chunkPaths.Count > 1)
            {
                string assembledPath = Path.Combine(ProjectArtifactDirectory(project.Id), SafeId(shot.Id, "shot_") + "-assembled.mp4");
                result = await _proxy.JackDirectorMediaExecutor.AssembleAsync(new JackDirectorAssemblyRequest
                {
                    ProjectId = project.Id, OutputPath = assembledPath, ClipPaths = chunkPaths,
                    Width = project.Width, Height = project.Height, Fps = project.Fps
                }, ProgressFor(project, shot, task), cancellationToken).ConfigureAwait(false);
            }
            else if (result?.Success == true && chunkPaths.Count == 1) result.ArtifactPath = chunkPaths[0];
        }
        if (!result.Success)
        {
            task.State = "failed"; task.Message = result.Error; shot.Status = "failed"; shot.Message = result.Error;
            throw new InvalidOperationException(result.Error);
        }
        string copied = CopyIntoProject(project.Id, shot.Id, kind, result.ArtifactPath);
        JackDirectorArtifact artifact = AddArtifact(project, shot.Id, kind, copied, result.MediaType);
        if (image) { shot.KeyframeArtifactId = artifact.Id; shot.KeyframeApproved = false; shot.Status = "keyframe-ready"; }
        else
        {
            shot.ClipArtifactId = artifact.Id;
            string endingFramePath = Path.Combine(ProjectArtifactDirectory(project.Id), SafeId(shot.Id, "shot_") + "-ending-frame.png");
            JackDirectorMediaResult endingFrame = await _proxy.JackDirectorMediaExecutor.ExtractLastFrameAsync(copied, endingFramePath, cancellationToken).ConfigureAwait(false);
            if (endingFrame.Success)
            {
                JackDirectorArtifact boundary = AddArtifact(project, shot.Id, "ending-frame", endingFrame.ArtifactPath, endingFrame.MediaType);
                shot.EndingFrameArtifactId = boundary.Id;
            }
            shot.Status = "completed";
        }
        shot.Progress = 100; shot.Message = image ? "Keyframe ready for approval." : "Clip complete.";
        task.State = "completed"; task.Progress = 100; task.ArtifactId = artifact.Id;
        SaveProjectFile(project);
    }

    private async Task<JackDirectorMediaResult> ExecuteMediaRequestAsync(string model, string preferredWorkerId, JackDirectorMediaRequest request, bool image, JackDirectorProject project, JackDirectorShot shot, JackDirectorRenderTask task, CancellationToken cancellationToken)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception lastRemoteError = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            JackDirectorWorker remote = SelectWorker(model, image, attempt == 1 ? preferredWorkerId : "", attempted);
            if (remote == null) break;
            attempted.Add(remote.Id); task.Attempt = attempt; task.WorkerId = remote.Id;
            try { return await ExecuteRemoteAsync(remote, request, image, ProgressFor(project, shot, task), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastRemoteError = ex; task.Message = "Worker " + remote.Name + " failed; retrying elsewhere. " + ex.Message; }
        }
        task.WorkerId = "local"; task.Attempt = Math.Max(1, attempted.Count + 1);
        JackDirectorMediaResult local = image
            ? await _proxy.JackDirectorMediaExecutor.GenerateImageAsync(request, ProgressFor(project, shot, task), cancellationToken).ConfigureAwait(false)
            : await _proxy.JackDirectorMediaExecutor.GenerateVideoAsync(request, ProgressFor(project, shot, task), cancellationToken).ConfigureAwait(false);
        if (!local.Success && lastRemoteError != null) local.Error = "Remote workers failed, then local fallback failed. Remote: " + lastRemoteError.Message + "; local: " + local.Error;
        return local;
    }

    private IProgress<JackDirectorProgress> ProgressFor(JackDirectorProject project, JackDirectorShot shot, JackDirectorRenderTask task) =>
        new Progress<JackDirectorProgress>(p => { task.Progress = p.Percent; task.Message = p.Message; shot.Progress = p.Percent; shot.Message = p.Message; project.Message = shot.Title + ": " + p.Message; });

    public object ListWorkers() => new { ok = true, workers = _workers.Values.Select(RedactWorker).ToArray(), masterList = new { status = "optional", degraded = false } };

    public JackDirectorWorker AddCandidate(string url, string source, bool rental)
    {
        Uri uri = ValidateWorkerUri(url);
        var worker = new JackDirectorWorker { BaseUrl = uri.GetLeftPart(UriPartial.Authority), Name = uri.Host, Source = First(source, "manual"), Rental = rental, LeaseActive = !rental };
        _workers[worker.Id] = worker; SaveWorkers(); return worker;
    }

    public async Task<object> DiscoverAsync(string url, string source, bool rental, CancellationToken cancellationToken)
    {
        source = First(source, "manual").ToLowerInvariant();
        var added = new List<object>();
        string warning = "";
        if (!string.IsNullOrWhiteSpace(url)) added.Add(RedactWorker(AddCandidate(url, source, rental)));
        else if (source == "lan")
        {
            foreach (string discovered in await DiscoverLanAsync(cancellationToken).ConfigureAwait(false))
                try { added.Add(RedactWorker(AddCandidate(discovered, "lan", false))); } catch { }
            if (added.Count == 0) warning = "No JackDirector LAN beacons answered. Manual URL pairing remains available.";
        }
        else if (source == "master-list")
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync("http://127.0.0.1:" + _proxy.ChatServerPort.ToString(CultureInfo.InvariantCulture) + "/api/jackllm/servers?fresh=1", cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
                using JsonDocument document = JsonDocument.Parse(body);
                foreach (string endpoint in FindEndpoints(document.RootElement).Distinct(StringComparer.OrdinalIgnoreCase))
                    try { added.Add(RedactWorker(AddCandidate(endpoint, "master-list", true))); } catch { }
            }
            catch (Exception ex) { warning = "Master List unavailable: " + ex.Message + ". Manual and LAN workers remain available."; }
        }
        else if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A Workstation URL is required for manual discovery.");
        return new { ok = true, workers = added, source, degraded = !string.IsNullOrWhiteSpace(warning), warning };
    }

    private void StartLanBeacon()
    {
        try
        {
            _lanBeacon = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, LanDiscoveryPort));
            _ = Task.Run(async () =>
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        System.Net.Sockets.UdpReceiveResult packet = await _lanBeacon.ReceiveAsync().ConfigureAwait(false);
                        if (Encoding.UTF8.GetString(packet.Buffer) != "JACKDIRECTOR_DISCOVER_V1") continue;
                        byte[] response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { service = "JackDirector", url = BuildLanUrl() }));
                        await _lanBeacon.SendAsync(response, response.Length, packet.RemoteEndPoint).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch when (_lifetime.IsCancellationRequested) { break; }
                    catch { }
                }
            });
        }
        catch { _lanBeacon = null; }
    }

    private async Task<IReadOnlyList<string>> DiscoverLanAsync(CancellationToken cancellationToken)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var client = new System.Net.Sockets.UdpClient(0) { EnableBroadcast = true };
        byte[] probe = Encoding.UTF8.GetBytes("JACKDIRECTOR_DISCOVER_V1");
        await client.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, LanDiscoveryPort)).ConfigureAwait(false);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(900);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            Task<System.Net.Sockets.UdpReceiveResult> receive = client.ReceiveAsync();
            Task completed = await Task.WhenAny(receive, Task.Delay(150, cancellationToken)).ConfigureAwait(false);
            if (completed != receive) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(receive.Result.Buffer));
                string endpoint = document.RootElement.TryGetProperty("url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(endpoint)) results.Add(endpoint);
            }
            catch { }
        }
        return results.ToArray();
    }

    private string BuildLanUrl()
    {
        string host = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))?.ToString() ?? "127.0.0.1";
        return "http://" + host + ":" + _proxy.ChatServerPort.ToString(CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> FindEndpoints(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Name is "endpoint" or "publicEndpoint" or "proxyEndpoint" or "baseUrl" or "url")
                {
                    string value = property.Value.GetString() ?? "";
                    if (Uri.TryCreate(value, UriKind.Absolute, out Uri parsed)) yield return parsed.GetLeftPart(UriPartial.Authority);
                }
                foreach (string nested in FindEndpoints(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement item in element.EnumerateArray()) foreach (string nested in FindEndpoints(item)) yield return nested;
    }

    public PairRequest CreatePairRequest(string coordinatorName)
    {
        CleanupPairs();
        var pair = new PairRequest { CoordinatorName = First(coordinatorName, "JackDirector"), Code = RandomNumberGenerator.GetInt32(100000, 999999).ToString(CultureInfo.InvariantCulture) };
        _pairRequests[pair.Id] = pair;
        return pair;
    }

    public object ListPairRequests() { CleanupPairs(); return new { ok = true, requests = _pairRequests.Values.Select(p => new { p.Id, p.CoordinatorName, p.Code, p.ExpiresUtc }).ToArray() }; }

    public object ApprovePair(string requestId, string code)
    {
        CleanupPairs();
        if (!_pairRequests.TryRemove(requestId ?? "", out PairRequest pair) || !FixedEquals(pair.Code, code)) throw new UnauthorizedAccessException("Pairing request or code is invalid or expired.");
        string secret = Convert.ToBase64String(RandomBytes(32));
        string signingKey = Sha256(secret);
        string workerId = "worker_" + Guid.NewGuid().ToString("N");
        _workers[workerId] = new JackDirectorWorker { Id = workerId, CredentialId = workerId, Name = pair.CoordinatorName, Source = "paired-inbound", TrustState = "approved", Secret = signingKey, LeaseActive = true };
        SaveWorkers();
        return new { ok = true, workerId, secret, machineName = Environment.MachineName };
    }

    public async Task<object> CompleteRemotePairAsync(string workerId, string requestId, string code, CancellationToken cancellationToken)
    {
        if (!_workers.TryGetValue(workerId ?? "", out JackDirectorWorker worker)) throw new FileNotFoundException("Worker candidate was not found.");
        using var response = await _http.PostAsync(worker.BaseUrl + "/api/jackdirector/worker/pair/approve", JsonContent(new { requestId, code }), cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Remote pairing failed: " + body);
        using JsonDocument document = JsonDocument.Parse(body);
        worker.Secret = Sha256(document.RootElement.GetProperty("secret").GetString() ?? "");
        worker.CredentialId = document.RootElement.GetProperty("workerId").GetString() ?? worker.Id;
        worker.TrustState = "approved";
        worker.Name = document.RootElement.TryGetProperty("machineName", out JsonElement machine) ? machine.GetString() ?? worker.Name : worker.Name;
        try
        {
            using HttpResponseMessage capabilities = await SendSignedAsync(worker, HttpMethod.Get, "/api/jackdirector/worker/capabilities", "", cancellationToken).ConfigureAwait(false);
            string capabilitiesBody = await capabilities.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (capabilities.IsSuccessStatusCode)
            {
                using JsonDocument capabilityDocument = JsonDocument.Parse(capabilitiesBody);
                if (capabilityDocument.RootElement.TryGetProperty("capabilities", out JsonElement value)) worker.Capabilities = JsonSerializer.Deserialize<JackDirectorCapabilities>(value.GetRawText(), _json) ?? new();
            }
        }
        catch { }
        SaveWorkers();
        return new { ok = true, worker = RedactWorker(worker) };
    }

    public async Task<object> BeginRemotePairAsync(string workerId, CancellationToken cancellationToken)
    {
        if (!_workers.TryGetValue(workerId ?? "", out JackDirectorWorker worker)) throw new FileNotFoundException("Worker candidate was not found.");
        using HttpResponseMessage response = await _http.PostAsync(worker.BaseUrl + "/api/jackdirector/worker/pair/request", JsonContent(new { coordinatorName = Environment.MachineName }), cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Remote pairing request failed: " + body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement pairing = document.RootElement.GetProperty("pairing");
        return new { ok = true, workerId, requestId = pairing.GetProperty("id").GetString(), expiresUtc = pairing.GetProperty("expiresUtc").GetString() };
    }

    public JackDirectorWorkerJob SubmitWorkerJob(JackDirectorMediaRequest request, string kind)
    {
        if (_proxy.JackDirectorMediaExecutor == null) throw new InvalidOperationException("JackONNX media execution is unavailable on this worker.");
        var job = new JackDirectorWorkerJob { Kind = kind == "image" ? "image" : "video", State = "queued" };
        var cts = new CancellationTokenSource();
        _workerJobs[job.Id] = job; _workerJobCancellations[job.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                job.State = "running";
                var progress = new Progress<JackDirectorProgress>(p => { job.Progress = p.Percent; job.Message = p.Message; });
                job.Result = job.Kind == "image"
                    ? await _proxy.JackDirectorMediaExecutor.GenerateImageAsync(request, progress, cts.Token).ConfigureAwait(false)
                    : await _proxy.JackDirectorMediaExecutor.GenerateVideoAsync(request, progress, cts.Token).ConfigureAwait(false);
                job.State = job.Result.Success ? "completed" : "failed"; job.Progress = job.Result.Success ? 100 : job.Progress; job.Message = job.Result.Success ? "Artifact ready." : job.Result.Error;
            }
            catch (OperationCanceledException) { job.State = "cancelled"; job.Message = "Job cancelled."; }
            catch (Exception ex) { job.State = "failed"; job.Message = ex.Message; job.Result = new JackDirectorMediaResult { Error = ex.Message }; }
            finally { if (_workerJobCancellations.TryRemove(job.Id, out CancellationTokenSource source)) source.Dispose(); }
        });
        return job;
    }

    public JackDirectorWorkerJob GetWorkerJob(string id) => _workerJobs.TryGetValue(id ?? "", out JackDirectorWorkerJob job) ? job : throw new FileNotFoundException("Worker job was not found.");
    public void CancelWorkerJob(string id) { GetWorkerJob(id); if (_workerJobCancellations.TryGetValue(id ?? "", out CancellationTokenSource cts)) cts.Cancel(); }
    public object WorkerJobArtifact(string id)
    {
        JackDirectorWorkerJob job = GetWorkerJob(id);
        if (job.Result == null || !job.Result.Success || !File.Exists(job.Result.ArtifactPath)) throw new FileNotFoundException("Worker artifact is not ready.");
        return new FileResponse(File.ReadAllBytes(job.Result.ArtifactPath), job.Result.MediaType, Path.GetFileName(job.Result.ArtifactPath));
    }

    public void RemoveWorker(string workerId) { _workers.TryRemove(workerId ?? "", out _); SaveWorkers(); }

    private JackDirectorWorker SelectWorker(string model, bool image, string preferredId, ISet<string> excluded = null)
    {
        IEnumerable<JackDirectorWorker> eligible = _workers.Values.Where(w => !string.IsNullOrWhiteSpace(w.BaseUrl) && w.TrustState == "approved" && !w.Quarantined && (!w.Rental || w.LeaseActive));
        eligible = eligible.Where(w => excluded == null || !excluded.Contains(w.Id)).Where(w =>
        {
            List<string> models = image ? w.Capabilities.ImageModels : w.Capabilities.VideoModels;
            return string.IsNullOrWhiteSpace(model) || models == null || models.Count == 0 || models.Contains(model, StringComparer.OrdinalIgnoreCase);
        });
        if (!string.IsNullOrWhiteSpace(preferredId)) return eligible.FirstOrDefault(w => string.Equals(w.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        return eligible.OrderBy(w => w.Capabilities.ActiveJobs).ThenBy(w => w.RecentFailures).ThenByDescending(w => w.Capabilities.FreeMemoryBytes).ThenBy(w => w.LatencyMs).FirstOrDefault();
    }

    private void StartHeartbeatLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                foreach (JackDirectorWorker worker in _workers.Values.Where(w => !string.IsNullOrWhiteSpace(w.BaseUrl) && w.TrustState == "approved" && !w.Quarantined).ToArray())
                {
                    DateTimeOffset started = DateTimeOffset.UtcNow;
                    try
                    {
                        using HttpResponseMessage response = await SendSignedAsync(worker, HttpMethod.Get, "/api/jackdirector/worker/capabilities", "", _lifetime.Token).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
                        using JsonDocument document = JsonDocument.Parse(body);
                        if (document.RootElement.TryGetProperty("capabilities", out JsonElement capabilities)) worker.Capabilities = JsonSerializer.Deserialize<JackDirectorCapabilities>(capabilities.GetRawText(), _json) ?? new();
                        worker.LastHeartbeatUtc = DateTimeOffset.UtcNow; worker.LatencyMs = Math.Max(1, (worker.LastHeartbeatUtc - started).TotalMilliseconds); worker.RecentFailures = Math.Max(0, worker.RecentFailures - 1);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                    catch { worker.RecentFailures++; }
                }
                SaveWorkers();
                try { await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        });
    }

    private async Task<JackDirectorMediaResult> ExecuteRemoteAsync(JackDirectorWorker worker, JackDirectorMediaRequest request, bool image, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SourcePath) && File.Exists(request.SourcePath))
        {
            string mime = image ? "image/" + First(Path.GetExtension(request.SourcePath).TrimStart('.'), "png") : "video/mp4";
            request.SourceDataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(request.SourcePath));
            request.SourcePath = "";
        }
        string body = JsonSerializer.Serialize(new { kind = image ? "image" : "video", request }, _json);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage submit = await SendSignedAsync(worker, HttpMethod.Post, "/api/jackdirector/worker/jobs", body, cancellationToken).ConfigureAwait(false);
            string submitBody = await submit.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!submit.IsSuccessStatusCode) throw new InvalidOperationException("Worker rejected the job: " + submitBody);
            using JsonDocument submitted = JsonDocument.Parse(submitBody);
            string jobId = submitted.RootElement.GetProperty("job").GetProperty("id").GetString() ?? "";
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                using HttpResponseMessage statusResponse = await SendSignedAsync(worker, HttpMethod.Get, "/api/jackdirector/worker/jobs/" + Uri.EscapeDataString(jobId), "", cancellationToken).ConfigureAwait(false);
                string statusBody = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!statusResponse.IsSuccessStatusCode) throw new InvalidOperationException("Worker status failed: " + statusBody);
                using JsonDocument statusDocument = JsonDocument.Parse(statusBody);
                JsonElement job = statusDocument.RootElement.GetProperty("job");
                string state = job.GetProperty("state").GetString() ?? "";
                double percent = job.TryGetProperty("progress", out JsonElement percentElement) && percentElement.TryGetDouble(out double number) ? number : 0;
                string message = job.TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() ?? state : state;
                progress?.Report(new JackDirectorProgress { Percent = percent, Message = worker.Name + ": " + message });
                if (state == "completed")
                {
                    using HttpResponseMessage artifact = await SendSignedAsync(worker, HttpMethod.Get, "/api/jackdirector/worker/artifacts/" + Uri.EscapeDataString(jobId), "", cancellationToken).ConfigureAwait(false);
                    if (!artifact.IsSuccessStatusCode) throw new InvalidOperationException("Worker artifact download failed: " + await artifact.Content.ReadAsStringAsync().ConfigureAwait(false));
                    string extension = image ? ".png" : ".mp4";
                    string temp = Path.Combine(_root, "Remote", jobId + extension); Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    File.WriteAllBytes(temp, await artifact.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                    worker.LastHeartbeatUtc = DateTimeOffset.UtcNow; worker.LatencyMs = Math.Max(1, (DateTimeOffset.UtcNow - started).TotalMilliseconds); SaveWorkers();
                    return new JackDirectorMediaResult { Success = true, ArtifactPath = temp, MediaType = artifact.Content.Headers.ContentType?.MediaType ?? (image ? "image/png" : "video/mp4"), JobId = jobId };
                }
                if (state is "failed" or "cancelled") throw new InvalidOperationException(message);
            }
        }
        catch
        {
            worker.RecentFailures++; worker.LastHeartbeatUtc = DateTimeOffset.UtcNow; SaveWorkers(); throw;
        }
    }

    private async Task<HttpResponseMessage> SendSignedAsync(JackDirectorWorker worker, HttpMethod method, string path, string body, CancellationToken cancellationToken)
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = Guid.NewGuid().ToString("N");
        string canonical = timestamp + "\n" + nonce + "\n" + Sha256(body ?? "");
        var request = new HttpRequestMessage(method, worker.BaseUrl.TrimEnd('/') + path);
        request.Headers.TryAddWithoutValidation("X-JackDirector-Worker", First(worker.CredentialId, worker.Id));
        request.Headers.TryAddWithoutValidation("X-JackDirector-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-JackDirector-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-JackDirector-Signature", Hmac(worker.Secret, canonical));
        if (method != HttpMethod.Get) request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    public bool ValidateWorkerRequest(HttpRequest request, string body, out string error)
    {
        error = "";
        string workerId = Header(request, "X-JackDirector-Worker");
        string timestamp = Header(request, "X-JackDirector-Timestamp");
        string nonce = Header(request, "X-JackDirector-Nonce");
        string signature = Header(request, "X-JackDirector-Signature");
        if (!_workers.TryGetValue(workerId, out JackDirectorWorker worker) || worker.TrustState != "approved" || worker.Quarantined || string.IsNullOrWhiteSpace(worker.Secret)) { error = "Worker is not approved."; return false; }
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300) { error = "Signature timestamp is invalid."; return false; }
        if (string.IsNullOrWhiteSpace(nonce) || !_nonces.TryAdd(nonce, DateTimeOffset.UtcNow)) { error = "Request nonce was already used."; return false; }
        string canonical = timestamp + "\n" + nonce + "\n" + Sha256(body ?? "");
        string expected = Hmac(worker.Secret, canonical);
        if (!FixedEquals(expected, signature)) { error = "Request signature is invalid."; return false; }
        return true;
    }

    public object Artifact(string artifactId, string ownerKey)
    {
        foreach (JackDirectorProject project in _projects.Values.Where(p => OwnerMatches(ownerKey, p.OwnerKey)))
        {
            JackDirectorArtifact artifact = FindArtifact(project, artifactId);
            if (artifact != null && IsUnderRoot(artifact.FilePath, ArtifactRoot) && File.Exists(artifact.FilePath))
                return new FileResponse(File.ReadAllBytes(artifact.FilePath), artifact.MediaType, Path.GetFileName(artifact.FilePath));
        }
        throw new FileNotFoundException("Artifact was not found.");
    }

    private void LoadState()
    {
        foreach (string file in Directory.EnumerateFiles(ProjectRoot, "*.json"))
        {
            try
            {
                JackDirectorProject project = JsonSerializer.Deserialize<JackDirectorProject>(File.ReadAllText(file), _json);
                if (project != null) { if (project.Status.StartsWith("rendering", StringComparison.OrdinalIgnoreCase) || project.Status == "assembling") { project.Status = "interrupted"; project.Message = "Rendering was interrupted by a restart."; } _projects[project.Id] = project; }
            }
            catch { }
        }
        try
        {
            if (File.Exists(WorkersPath)) foreach (JackDirectorWorker worker in JsonSerializer.Deserialize<List<JackDirectorWorker>>(File.ReadAllText(WorkersPath), _json) ?? new()) _workers[worker.Id] = worker;
        }
        catch { }
    }

    private void SaveProjectFile(JackDirectorProject project) { lock (_saveLock) AtomicWrite(ProjectPath(project.Id), JsonSerializer.Serialize(project, _json)); }
    private void SaveWorkers() { lock (_saveLock) AtomicWrite(WorkersPath, JsonSerializer.Serialize(_workers.Values.ToList(), _json)); }
    private string ProjectPath(string id) => Path.Combine(ProjectRoot, SafeId(id, "project_") + ".json");
    private string ProjectArtifactDirectory(string id) { string path = Path.Combine(ArtifactRoot, SafeId(id, "project_")); Directory.CreateDirectory(path); return path; }
    private string CopyIntoProject(string projectId, string shotId, string kind, string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) throw new FileNotFoundException("Generated artifact file was not found.");
        string extension = Path.GetExtension(source); if (extension.Length > 10) extension = "";
        string target = Path.Combine(ProjectArtifactDirectory(projectId), SafeId(shotId, "shot_") + "-" + kind + "-" + Guid.NewGuid().ToString("N")[..8] + extension);
        File.Copy(source, target, true); return target;
    }
    private JackDirectorArtifact AddArtifact(JackDirectorProject project, string shotId, string kind, string path, string mediaType) { var artifact = new JackDirectorArtifact { ShotId = shotId, Kind = kind, FilePath = Path.GetFullPath(path), MediaType = mediaType }; project.Artifacts.Add(artifact); return artifact; }
    private static JackDirectorArtifact FindArtifact(JackDirectorProject project, string id) => project?.Artifacts?.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
    private static bool OwnerMatches(string left, string right) => string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
    private static string SafeId(string value, string prefix) { value = new string((value ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(96).ToArray()); return string.IsNullOrWhiteSpace(value) ? prefix + Guid.NewGuid().ToString("N") : value; }
    private static string First(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    private static void AtomicWrite(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, content, Encoding.UTF8); if (File.Exists(path)) File.Delete(path); File.Move(temp, path); }
    private static bool IsUnderRoot(string path, string root) { string full = Path.GetFullPath(path); string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase); }
    private static Uri ValidateWorkerUri(string value) { if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http")) throw new ArgumentException("A valid HTTP or HTTPS Workstation URL is required."); if (uri.Scheme == "http" && !IsPrivateHost(uri.Host)) throw new ArgumentException("Internet workers must use HTTPS."); return uri; }
    private static bool IsPrivateHost(string host) { if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true; if (!IPAddress.TryParse(host, out IPAddress ip)) return false; byte[] b = ip.GetAddressBytes(); return IPAddress.IsLoopback(ip) || b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31); }
    private static object RedactWorker(JackDirectorWorker w) => new { w.Id, w.Name, w.BaseUrl, w.Source, w.TrustState, w.Rental, w.LeaseActive, w.Quarantined, online = string.IsNullOrWhiteSpace(w.BaseUrl) || w.LastHeartbeatUtc > DateTimeOffset.UtcNow.AddSeconds(-15), w.LastHeartbeatUtc, w.RecentFailures, w.LatencyMs, w.Capabilities };
    private static byte[] RandomBytes(int count) { byte[] bytes = new byte[count]; using RandomNumberGenerator rng = RandomNumberGenerator.Create(); rng.GetBytes(bytes); return bytes; }
    private static string Sha256(string value) { using SHA256 sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))); }
    private static string Hmac(string secret, string value) { using var hmac = new HMACSHA256(Convert.FromBase64String(secret)); return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))); }
    private static bool FixedEquals(string a, string b) { byte[] x = Encoding.UTF8.GetBytes(a ?? ""), y = Encoding.UTF8.GetBytes(b ?? ""); return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y); }
    private static string Header(HttpRequest request, string name) => request?.Headers != null && request.Headers.TryGetValue(name, out string value) ? value ?? "" : "";
    private static System.Net.Http.StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private void CleanupPairs() { foreach (var pair in _pairRequests.Where(p => p.Value.ExpiresUtc < DateTimeOffset.UtcNow).ToArray()) _pairRequests.TryRemove(pair.Key, out _); }
    public void Dispose() { _lifetime.Cancel(); try { _lanBeacon?.Close(); } catch { } foreach (CancellationTokenSource cts in _runs.Values) cts.Cancel(); _http.Dispose(); _lifetime.Dispose(); }

    public sealed class PairRequest
    {
        public string Id { get; set; } = "pair_" + Guid.NewGuid().ToString("N");
        public string CoordinatorName { get; set; } = "";
        public string Code { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);
    }
}

public partial class LmVsProxy
{
    private JackDirectorService _jackDirector;
    public IJackDirectorMediaExecutor JackDirectorMediaExecutor { get; set; }

    private void RegisterJackDirectorRoutes(HttpServer server)
    {
        _jackDirector ??= new JackDirectorService(this, Path.Combine(_chatSessionRoot, "JackDirector"));
        string html = HtmlPageResources.GetHtml("JackDirector.html");
        server.Map("GET", "/JackDirector", (_, request, _) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/JackDirector/", (_, request, _) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/api/jackdirector/projects", (connection, request, _) => JackDirectorJson(() => _jackDirector.ListProjects(GetChatSessionOwnerKey(connection, request)), request));
        server.Map("POST", "/api/jackdirector/projects/save", (connection, request, _) => JackDirectorJson(() => new { ok = true, project = _jackDirector.SaveProject(JsonSerializer.Deserialize<JackDirectorProject>(request.Body ?? "{}", JackDirectorJsonOptions), GetChatSessionOwnerKey(connection, request)) }, request));
        server.Map("POST", "/api/jackdirector/projects/delete", (connection, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); _jackDirector.DeleteProject(JdString(d.RootElement, "projectId"), GetChatSessionOwnerKey(connection, request)); return new { ok = true }; }, request));
        server.Map("POST", "/api/jackdirector/storyboard", (connection, request, token) => JackDirectorJson(() => HandleJackDirectorStoryboardAsync(connection, request, token).GetAwaiter().GetResult(), request));
        server.Map("POST", "/api/jackdirector/render", (connection, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); _jackDirector.StartRender(JdString(d.RootElement, "projectId"), GetChatSessionOwnerKey(connection, request), JdBool(d.RootElement, "keyframesOnly")); return new { ok = true, status = "queued" }; }, request));
        server.Map("POST", "/api/jackdirector/control", (connection, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); string id = JdString(d.RootElement, "projectId"); string action = JdString(d.RootElement, "action"); if (action == "cancel") _jackDirector.Cancel(id); else throw new ArgumentException("Supported action: cancel."); return new { ok = true }; }, request));
        server.Map("GET", "/api/jackdirector/status", (connection, request, _) => JackDirectorJson(() => _jackDirector.Status(GetQueryParameter(request, "projectId"), GetChatSessionOwnerKey(connection, request)), request));
        server.MapStream("GET", "/api/jackdirector/events", (connection, request, stream, token) => StreamJackDirectorEventsAsync(connection, request, stream, token));
        server.Map("GET", "/api/jackdirector/artifacts/*", (connection, request, _) => JackDirectorFile(() => _jackDirector.Artifact(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request)), request));
        server.Map("GET", "/api/jackdirector/workers", (_, request, _) => JackDirectorJson(() => _jackDirector.ListWorkers(), request));
        server.Map("POST", "/api/jackdirector/workers/discover", (_, request, token) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); return _jackDirector.DiscoverAsync(JdString(d.RootElement, "url"), JdString(d.RootElement, "source"), JdBool(d.RootElement, "rental"), token).GetAwaiter().GetResult(); }, request));
        server.Map("POST", "/api/jackdirector/workers/pair", (_, request, token) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); string workerId = JdString(d.RootElement, "workerId"); if (!string.IsNullOrWhiteSpace(JdString(d.RootElement, "requestId"))) return _jackDirector.CompleteRemotePairAsync(workerId, JdString(d.RootElement, "requestId"), JdString(d.RootElement, "code"), token).GetAwaiter().GetResult(); return _jackDirector.BeginRemotePairAsync(workerId, token).GetAwaiter().GetResult(); }, request));
        server.Map("POST", "/api/jackdirector/workers/approve", (_, request, token) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); return _jackDirector.CompleteRemotePairAsync(JdString(d.RootElement, "workerId"), JdString(d.RootElement, "requestId"), JdString(d.RootElement, "code"), token).GetAwaiter().GetResult(); }, request));
        server.Map("POST", "/api/jackdirector/workers/remove", (_, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); _jackDirector.RemoveWorker(JdString(d.RootElement, "workerId")); return new { ok = true }; }, request));
        server.Map("GET", "/api/jackdirector/worker/capabilities", (_, request, token) => JackDirectorJson(() => new { ok = true, capabilities = JackDirectorMediaExecutor?.GetCapabilitiesAsync(token).GetAwaiter().GetResult() ?? new JackDirectorCapabilities() }, request));
        server.Map("POST", "/api/jackdirector/worker/pair/request", (_, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); return new { ok = true, pairing = _jackDirector.CreatePairRequest(JdString(d.RootElement, "coordinatorName")) }; }, request));
        server.Map("GET", "/api/jackdirector/worker/pair/requests", (connection, request, _) => JackDirectorJson(() => { if (!IsLoopbackWorkstationClient(connection)) throw new UnauthorizedAccessException("Pairing codes are visible only on the worker's loopback UI."); return _jackDirector.ListPairRequests(); }, request));
        server.Map("POST", "/api/jackdirector/worker/pair/approve", (_, request, _) => JackDirectorJson(() => { using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); return _jackDirector.ApprovePair(JdString(d.RootElement, "requestId"), JdString(d.RootElement, "code")); }, request));
        server.Map("POST", "/api/jackdirector/worker/jobs", (_, request, _) => JackDirectorJson(() => { RequireJackDirectorWorker(request); using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); string kind = JdString(d.RootElement, "kind"); JackDirectorMediaRequest media = d.RootElement.TryGetProperty("request", out JsonElement element) ? JsonSerializer.Deserialize<JackDirectorMediaRequest>(element.GetRawText(), JackDirectorJsonOptions) : null; if (media == null) throw new ArgumentException("A media request is required."); return new { ok = true, job = _jackDirector.SubmitWorkerJob(media, kind) }; }, request));
        server.Map("GET", "/api/jackdirector/worker/jobs/*", (_, request, _) => JackDirectorJson(() => { RequireJackDirectorWorker(request); return new { ok = true, job = _jackDirector.GetWorkerJob(request.PathVariables?.FirstOrDefault()) }; }, request));
        server.Map("POST", "/api/jackdirector/worker/jobs/cancel", (_, request, _) => JackDirectorJson(() => { RequireJackDirectorWorker(request); using JsonDocument d = JsonDocument.Parse(request.Body ?? "{}"); _jackDirector.CancelWorkerJob(JdString(d.RootElement, "jobId")); return new { ok = true }; }, request));
        server.Map("GET", "/api/jackdirector/worker/artifacts/*", (_, request, _) => JackDirectorFile(() => { RequireJackDirectorWorker(request); return _jackDirector.WorkerJobArtifact(request.PathVariables?.FirstOrDefault()); }, request));
        foreach (string path in new[] { "/api/jackdirector/projects", "/api/jackdirector/projects/save", "/api/jackdirector/projects/delete", "/api/jackdirector/storyboard", "/api/jackdirector/render", "/api/jackdirector/control", "/api/jackdirector/status", "/api/jackdirector/events", "/api/jackdirector/workers", "/api/jackdirector/workers/discover", "/api/jackdirector/workers/pair", "/api/jackdirector/workers/approve", "/api/jackdirector/workers/remove", "/api/jackdirector/worker/capabilities", "/api/jackdirector/worker/pair/request", "/api/jackdirector/worker/pair/requests", "/api/jackdirector/worker/pair/approve", "/api/jackdirector/worker/jobs", "/api/jackdirector/worker/jobs/cancel" })
            server.Map("OPTIONS", path, (_, request, _) => HandleWebAuthCorsPreflight(request));
    }

    private async Task StreamJackDirectorEventsAsync(NetworkConnection connection, HttpRequest request, ChunkedStream stream, CancellationToken cancellationToken)
    {
        stream.ContentType = "text/event-stream";
        string projectId = GetQueryParameter(request, "projectId");
        string ownerKey = GetChatSessionOwnerKey(connection, request);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                object snapshot = _jackDirector.Status(projectId, ownerKey);
                string json = JsonSerializer.Serialize(snapshot, JackDirectorJsonOptions);
                stream.WriteLine("event: status");
                stream.WriteLine("data: " + json);
                stream.WriteLine("");
                JackDirectorProject project = _jackDirector.GetProject(projectId, ownerKey);
                if (project.Status is "completed" or "failed" or "cancelled" or "assembly-blocked" or "awaiting-keyframe-approval") break;
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { stream.WriteLine("event: error"); stream.WriteLine("data: " + JsonSerializer.Serialize(new { error = ex.Message })); stream.WriteLine(""); break; }
        }
    }

    private async Task<object> HandleJackDirectorStoryboardAsync(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
    {
        using JsonDocument input = JsonDocument.Parse(request.Body ?? "{}");
        string concept = JdString(input.RootElement, "concept");
        int count = Math.Clamp(JdInt(input.RootElement, "shotCount", 6), 1, 24);
        if (string.IsNullOrWhiteSpace(concept)) throw new ArgumentException("A production concept is required.");
        await LocalModelRuntime.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        string prompt = "Return only JSON with a shots array. Each shot must have title, prompt, negativePrompt, durationSeconds, continuityGroupId, and renderMode. Create " + count + " cinematic shots for: " + concept;
        string payload = JsonSerializer.Serialize(new { model = FirstNonEmpty(JdString(input.RootElement, "model"), ChatModel), messages = new[] { new { role = "system", content = "You are JackDirector. Produce practical, visually continuous shot plans as strict JSON." }, new { role = "user", content = prompt } }, temperature = 0.5, stream = false });
        using HttpRequestMessage upstream = new(HttpMethod.Post, LocalModelRuntime.OpenAiBaseUrl.TrimEnd('/') + "/v1/chat/completions") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await _httpClient.SendAsync(upstream, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Storyboard model failed: HTTP " + (int)response.StatusCode + ". " + body);
        using JsonDocument result = JsonDocument.Parse(body);
        string content = result.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        int start = content.IndexOf('{'), end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Storyboard model did not return valid JSON.");
        using JsonDocument storyboard = JsonDocument.Parse(content.Substring(start, end - start + 1));
        if (!storyboard.RootElement.TryGetProperty("shots", out JsonElement shots) || shots.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Storyboard JSON is missing its shots array.");
        return new { ok = true, storyboard = storyboard.RootElement.Clone() };
    }

    private static readonly JsonSerializerOptions JackDirectorJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private object JackDirectorJson(Func<object> action, HttpRequest request)
    {
        AddWebAuthCorsHeaders(request);
        try { request.Context.Response.ContentType = "application/json; charset=utf-8"; return JsonSerializer.Serialize(action(), JackDirectorJsonOptions); }
        catch (UnauthorizedAccessException ex) { return BuildJsonError(request, 403, "Forbidden", ex.Message); }
        catch (FileNotFoundException ex) { return BuildJsonError(request, 404, "Not Found", ex.Message); }
        catch (InvalidOperationException ex) { return BuildJsonError(request, 409, "Conflict", ex.Message); }
        catch (Exception ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
    }
    private object JackDirectorFile(Func<object> action, HttpRequest request) { try { return action(); } catch (FileNotFoundException ex) { return BuildJsonError(request, 404, "Not Found", ex.Message); } catch (UnauthorizedAccessException ex) { return BuildJsonError(request, 403, "Forbidden", ex.Message); } }
    private void RequireJackDirectorWorker(HttpRequest request) { if (!_jackDirector.ValidateWorkerRequest(request, request?.Body ?? "", out string error)) throw new UnauthorizedAccessException(error); }
    private static string JdString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool JdBool(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    private static int JdInt(JsonElement root, string name, int fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : fallback;
}
