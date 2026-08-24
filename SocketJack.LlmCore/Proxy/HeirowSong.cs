using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocketJack.Net;

namespace SocketJack.Net;

public interface IHeirowSongMediaExecutor
{
    Task<HeirowSongCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<HeirowSongMediaResult> GenerateAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongMediaResult> EditAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongMediaResult> ExtractStemAsync(HeirowSongMediaRequest request, string stem, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongMediaResult> CompleteTrackAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongMediaResult> TranscribeMidiAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongAdapterResult> TrainAdapterAsync(HeirowSongAdapterRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default);
    Task<HeirowSongVoiceVerificationResult> VerifyVoiceAsync(HeirowSongVoiceVerificationRequest request, CancellationToken cancellationToken = default);
}

public sealed class HeirowSongCapabilities
{
    public bool Installed { get; set; }
    public bool Ready { get; set; }
    public string RuntimeVersion { get; set; } = "ACE-Step 1.5 v0.1.8";
    public string RuntimeCommit { get; set; } = "dce621408bee8c31b4fcf4811682eb9359e1bc94";
    public string Backend { get; set; } = "not-installed";
    public string CompatibilityMessage { get; set; } = "Install the heirowSong model and signed runtime pack in Models.";
    public int LocalWorkerCount { get; set; }
    public int MaxBatchSize { get; set; } = 2;
    public long ModelPayloadBytes { get; set; } = 12_501_168_402;
    public List<string> Devices { get; set; } = new();
    public List<string> Operations { get; set; } = new() { "text2music", "cover", "repaint", "extend", "crop", "fade", "speed", "extract", "lego", "complete", "midi", "lora" };
    public List<string> ExportFormats { get; set; } = new() { "wav", "mp3", "flac", "lrc", "mp4", "midi" };
}

public sealed class HeirowSongMediaRequest
{
    public string JobId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Operation { get; set; } = "text2music";
    public string Prompt { get; set; } = "";
    public string Lyrics { get; set; } = "";
    public bool Instrumental { get; set; }
    public string VocalLanguage { get; set; } = "auto";
    public double DurationSeconds { get; set; } = 60;
    public int? Bpm { get; set; }
    public string KeyScale { get; set; } = "";
    public string TimeSignature { get; set; } = "";
    public long? Seed { get; set; }
    public int BatchSize { get; set; } = 2;
    public string OutputFormat { get; set; } = "wav";
    public string SourceArtifactId { get; set; } = "";
    public string ReferenceArtifactId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ReferencePath { get; set; } = "";
    public string AdapterPath { get; set; } = "";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double Speed { get; set; } = 1;
    public string Stem { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
}

public sealed class HeirowSongMediaResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public string JobId { get; set; } = "";
    public List<HeirowSongMediaArtifact> Artifacts { get; set; } = new();
}

public sealed class HeirowSongMediaArtifact
{
    public string FilePath { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public string Kind { get; set; } = "song";
    public string Stem { get; set; } = "";
}

public sealed class HeirowSongProgress
{
    public double Percent { get; set; }
    public string Message { get; set; } = "";
}

public sealed class HeirowSongAdapterRequest
{
    public string ProjectId { get; set; } = "";
    public string PersonaId { get; set; } = "";
    public List<string> SourcePaths { get; set; } = new();
    public string OutputDirectory { get; set; } = "";
}

public sealed class HeirowSongAdapterResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public string AdapterPath { get; set; } = "";
}

public sealed class HeirowSongVoiceVerificationRequest
{
    public string EnrollmentPath { get; set; } = "";
    public string ChallengePath { get; set; } = "";
    public string ExpectedChallenge { get; set; } = "";
}

public sealed class HeirowSongVoiceVerificationResult
{
    public bool Success { get; set; }
    public bool ChallengeMatched { get; set; }
    public double SpeakerSimilarity { get; set; }
    public string Transcript { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class HeirowSongProject
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "songproject_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string Workspace { get; set; } = "My songs";
    public string Title { get; set; } = "Untitled song";
    public string Concept { get; set; } = "";
    public int Revision { get; set; }
    public string CurrentVersionId { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<HeirowSongVersion> Versions { get; set; } = new();
    public List<HeirowSongTrack> Tracks { get; set; } = new();
    public List<HeirowSongArtifact> Artifacts { get; set; } = new();
    public List<HeirowSongAuditEvent> Audit { get; set; } = new();
}

public sealed class HeirowSongVersion
{
    public string Id { get; set; } = "songversion_" + Guid.NewGuid().ToString("N");
    public string ParentVersionId { get; set; } = "";
    public string Operation { get; set; } = "text2music";
    public string Prompt { get; set; } = "";
    public string Lyrics { get; set; } = "";
    public bool Instrumental { get; set; }
    public double DurationSeconds { get; set; }
    public int? Bpm { get; set; }
    public string KeyScale { get; set; } = "";
    public string TimeSignature { get; set; } = "";
    public long? Seed { get; set; }
    public string Model { get; set; } = "ACE-Step 1.5";
    public List<string> ArtifactIds { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HeirowSongTrack
{
    public string Id { get; set; } = "track_" + Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Track";
    public string Kind { get; set; } = "audio";
    public double GainDb { get; set; }
    public double Pan { get; set; }
    public double PitchSemitones { get; set; }
    public double Tempo { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public List<HeirowSongRegion> Regions { get; set; } = new();
}

public sealed class HeirowSongRegion
{
    public string Id { get; set; } = "region_" + Guid.NewGuid().ToString("N");
    public string ArtifactId { get; set; } = "";
    public double TimelineStartSeconds { get; set; }
    public double SourceStartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public int TakeLane { get; set; }
}

public sealed class HeirowSongArtifact
{
    public string Id { get; set; } = "songartifact_" + Guid.NewGuid().ToString("N");
    public string VersionId { get; set; } = "";
    public string Kind { get; set; } = "song";
    public string Stem { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HeirowSongJob
{
    public string Id { get; set; } = "songjob_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Operation { get; set; } = "text2music";
    public string State { get; set; } = "queued";
    public double Progress { get; set; }
    public string Message { get; set; } = "Queued";
    public string AssignedWorker { get; set; } = "local";
    public HeirowSongMediaRequest Request { get; set; } = new();
    public List<string> ArtifactIds { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HeirowSongPersona
{
    public string Id { get; set; } = "persona_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "New persona";
    public string Description { get; set; } = "";
    public string AdapterPath { get; set; } = "";
    public string State { get; set; } = "draft";
}

public sealed class HeirowSongVoiceProfile
{
    public string Id { get; set; } = "voice_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "My voice";
    public string Challenge { get; set; } = "";
    public string EnrollmentPath { get; set; } = "";
    public string ChallengePath { get; set; } = "";
    public bool OwnershipAttested { get; set; }
    public bool Verified { get; set; }
    public double SpeakerSimilarity { get; set; }
    public string State { get; set; } = "challenge-required";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HeirowSongShareGrant
{
    public string Id { get; set; } = "songshare_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string RecipientUserName { get; set; } = "";
    public string RecipientOwnerId { get; set; } = "";
    public bool CanRead { get; set; } = true;
    public bool CanDownload { get; set; }
    public bool CanRemix { get; set; }
    public bool IncludeVoiceMaterial { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool Revoked { get; set; }
}

public sealed class HeirowSongAuditEvent
{
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Category { get; set; } = "";
    public string Detail { get; set; } = "";
}

internal sealed class HeirowSongService : IDisposable
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase) { "text2music", "cover", "repaint", "replace", "extend", "crop", "fade", "speed", "extract", "lego", "complete", "midi", "export" };
    private static readonly HashSet<string> AllowedStems = new(StringComparer.OrdinalIgnoreCase) { "vocals", "backing_vocals", "drums", "bass", "guitar", "keyboard", "strings", "brass", "woodwinds", "synth", "percussion", "fx" };
    private static readonly HashSet<string> AllowedAudioTypes = new(StringComparer.OrdinalIgnoreCase) { "audio/wav", "audio/x-wav", "audio/mpeg", "audio/flac", "audio/ogg", "audio/mp4" };
    private readonly HeirowLlm _proxy;
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, HeirowSongProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HeirowSongJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HeirowSongPersona> _personas = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HeirowSongVoiceProfile> _voices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HeirowSongShareGrant> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    public HeirowSongService(HeirowLlm proxy, string root)
    {
        _proxy = proxy;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(ProjectRoot);
        Directory.CreateDirectory(ArtifactRoot);
        Directory.CreateDirectory(JobRoot);
        Directory.CreateDirectory(ProfileRoot);
        LoadState();
        ResumeQueuedJobs();
    }

    private string ProjectRoot => Path.Combine(_root, "Projects");
    private string ArtifactRoot => Path.Combine(_root, "Artifacts");
    private string JobRoot => Path.Combine(_root, "Jobs");
    private string ProfileRoot => Path.Combine(_root, "Profiles");

    public async Task<object> CapabilitiesAsync(CancellationToken cancellationToken)
    {
        HeirowSongCapabilities capabilities = _proxy.HeirowSongMediaExecutor == null
            ? new HeirowSongCapabilities()
            : await _proxy.HeirowSongMediaExecutor.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            ok = true,
            capabilities,
            modelBundle = HeirowSongModelBundleDescriptor.Create(),
            privacy = new { localOnly = true, privateByDefault = true, publicFeed = false },
            voicePolicy = new { ownerOnly = true, verificationRequired = true, publicFigurePresets = false, experimental = true }
        };
    }

    public object ListProjects(string ownerKey)
    {
        string ownerId = OwnerId(ownerKey);
        var owned = _projects.Values.Where(project => project.OwnerId == ownerId);
        var granted = _shares.Values.Where(grant => grant.RecipientOwnerId == ownerId && IsGrantActive(grant) && grant.CanRead)
            .Select(grant => _projects.TryGetValue(grant.ProjectId, out HeirowSongProject project) ? project : null).Where(project => project != null);
        return new { ok = true, projects = owned.Concat(granted).GroupBy(project => project.Id, StringComparer.OrdinalIgnoreCase).Select(group => RedactProject(group.First())).OrderByDescending(project => project.UpdatedUtc).ToArray() };
    }

    public HeirowSongProject GetProject(string projectId, string ownerKey, string required = "read")
    {
        if (!_projects.TryGetValue(SafeId(projectId, "songproject_"), out HeirowSongProject project)) throw new FileNotFoundException("Song project was not found.");
        string ownerId = OwnerId(ownerKey);
        if (project.OwnerId == ownerId) return project;
        HeirowSongShareGrant grant = _shares.Values.FirstOrDefault(value => value.ProjectId == project.Id && value.RecipientOwnerId == ownerId && IsGrantActive(value));
        bool allowed = required switch { "download" => grant?.CanDownload == true, "remix" => grant?.CanRemix == true, _ => grant?.CanRead == true };
        if (!allowed) throw new UnauthorizedAccessException("This song project belongs to another Workstation account.");
        return project;
    }

    public HeirowSongProject SaveProject(HeirowSongProject incoming, string ownerKey)
    {
        if (incoming == null) throw new ArgumentException("Project payload is required.");
        string ownerId = OwnerId(ownerKey);
        incoming.Id = SafeId(incoming.Id, "songproject_");
        incoming.Title = Limit(First(incoming.Title, "Untitled song"), 160);
        incoming.Concept = Limit(incoming.Concept, 4000);
        incoming.Workspace = Limit(First(incoming.Workspace, "My songs"), 100);
        incoming.Versions ??= new(); incoming.Tracks ??= new(); incoming.Artifacts ??= new(); incoming.Audit ??= new();
        if (_projects.TryGetValue(incoming.Id, out HeirowSongProject existing))
        {
            if (existing.OwnerId != ownerId) throw new UnauthorizedAccessException("This song project belongs to another Workstation account.");
            if (incoming.Revision != existing.Revision) throw new InvalidOperationException("Song project revision conflict. Reload before saving.");
            incoming.CreatedUtc = existing.CreatedUtc;
            incoming.Versions = existing.Versions;
            incoming.Artifacts = existing.Artifacts;
            incoming.CurrentVersionId = existing.CurrentVersionId;
            incoming.Revision++;
        }
        else
        {
            incoming.OwnerId = ownerId;
            incoming.Versions = new();
            incoming.Artifacts = new();
            incoming.CurrentVersionId = "";
            incoming.Revision = 1;
            incoming.CreatedUtc = DateTimeOffset.UtcNow;
        }
        incoming.OwnerId = ownerId;
        incoming.UpdatedUtc = DateTimeOffset.UtcNow;
        NormalizeTimeline(incoming);
        _projects[incoming.Id] = incoming;
        SaveProjectFile(incoming);
        return incoming;
    }

    public void DeleteProject(string projectId, string ownerKey)
    {
        HeirowSongProject project = GetProject(projectId, ownerKey);
        if (project.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("Only the owner can delete this song project.");
        CancelProjectJobs(project.Id);
        _projects.TryRemove(project.Id, out _);
        string projectFile = ProjectPath(project.OwnerId, project.Id);
        if (File.Exists(projectFile)) File.Delete(projectFile);
        string artifactDirectory = ProjectArtifactDirectory(project.OwnerId, project.Id);
        if (Directory.Exists(artifactDirectory)) Directory.Delete(artifactDirectory, true);
        foreach (HeirowSongShareGrant grant in _shares.Values.Where(value => value.ProjectId == project.Id).ToArray()) _shares.TryRemove(grant.Id, out _);
        SaveProfiles();
    }

    public HeirowSongJob StartJob(HeirowSongMediaRequest request, string ownerKey)
    {
        if (_proxy.HeirowSongMediaExecutor == null) throw new InvalidOperationException("heirowSong media runtime is not configured.");
        request ??= new();
        request.ProjectId = SafeId(request.ProjectId, "songproject_");
        HeirowSongProject project = GetProject(request.ProjectId, ownerKey, request.Operation is "cover" or "repaint" or "replace" or "extend" ? "remix" : "read");
        string ownerId = OwnerId(ownerKey);
        HeirowSongProject sourceProject = project;
        if (project.OwnerId != ownerId)
        {
            if (request.Operation is not ("cover" or "repaint" or "replace" or "extend")) throw new UnauthorizedAccessException("Shared projects may only be remixed when the grant allows it.");
            project = new HeirowSongProject { OwnerId = ownerId, Title = Limit(project.Title + " (Remix)", 160), Concept = project.Concept, Workspace = "My songs", Revision = 1, Audit = new List<HeirowSongAuditEvent> { new() { Category = "remix", Detail = "Created from an explicitly shared project." } } };
            _projects[project.Id] = project;
            SaveProjectFile(project);
            request.ProjectId = project.Id;
        }
        request.Operation = First(request.Operation, "text2music").ToLowerInvariant();
        if (!AllowedOperations.Contains(request.Operation)) throw new ArgumentException("Unsupported song operation: " + request.Operation);
        request.DurationSeconds = Math.Clamp(request.DurationSeconds, 10, 600);
        request.BatchSize = Math.Clamp(request.BatchSize, 1, 2);
        request.Bpm = request.Bpm.HasValue ? Math.Clamp(request.Bpm.Value, 30, 300) : null;
        request.OutputFormat = NormalizeExportFormat(request.OutputFormat);
        request.Prompt = Limit(request.Prompt, 8000);
        request.Lyrics = Limit(request.Lyrics, 40_000);
        if (request.Operation == "extract" && !AllowedStems.Contains(request.Stem)) throw new ArgumentException("Unsupported stem. Choose one of the twelve heirowSong stems.");
        if (!string.IsNullOrWhiteSpace(request.SourceArtifactId)) request.SourcePath = ResolveArtifactPath(sourceProject, request.SourceArtifactId);
        if (!string.IsNullOrWhiteSpace(request.ReferenceArtifactId)) request.ReferencePath = ResolveArtifactPath(sourceProject, request.ReferenceArtifactId);
        request.JobId = "songjob_" + Guid.NewGuid().ToString("N");
        request.OutputDirectory = ProjectArtifactDirectory(project.OwnerId, project.Id);
        var job = new HeirowSongJob { Id = request.JobId, OwnerId = ownerId, ProjectId = project.Id, Operation = request.Operation, Request = request };
        _jobs[job.Id] = job;
        SaveJob(job);
        StartJobRun(job);
        return job;
    }

    public HeirowSongJob GetJob(string id, string ownerKey)
    {
        if (!_jobs.TryGetValue(id ?? "", out HeirowSongJob job)) throw new FileNotFoundException("Song job was not found.");
        if (job.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("This job belongs to another Workstation account.");
        return job;
    }

    public void CancelJob(string id, string ownerKey)
    {
        HeirowSongJob job = GetJob(id, ownerKey);
        if (_runs.TryGetValue(job.Id, out CancellationTokenSource cancellation)) cancellation.Cancel();
        else { job.State = "cancelled"; job.Message = "Job cancelled before it started."; job.UpdatedUtc = DateTimeOffset.UtcNow; SaveJob(job); }
    }

    public HeirowSongJob RetryJob(string id, string ownerKey)
    {
        HeirowSongJob existing = GetJob(id, ownerKey);
        if (existing.State is "queued" or "running") throw new InvalidOperationException("This job is already active.");
        var request = JsonSerializer.Deserialize<HeirowSongMediaRequest>(JsonSerializer.Serialize(existing.Request, _json), _json) ?? new();
        return StartJob(request, ownerKey);
    }

    public object Artifact(string artifactId, string ownerKey, bool download)
    {
        foreach (HeirowSongProject project in _projects.Values)
        {
            HeirowSongArtifact artifact = project.Artifacts.FirstOrDefault(value => value.Id.Equals(artifactId ?? "", StringComparison.OrdinalIgnoreCase));
            if (artifact == null) continue;
            GetProject(project.Id, ownerKey, download ? "download" : "read");
            if (!IsUnderRoot(artifact.FilePath, ArtifactRoot) || !File.Exists(artifact.FilePath)) break;
            return new FileResponse(File.ReadAllBytes(artifact.FilePath), artifact.MediaType, Path.GetFileName(artifact.FilePath));
        }
        throw new FileNotFoundException("Song artifact was not found.");
    }

    public object Upload(string body, string ownerKey)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        JsonElement root = document.RootElement;
        string projectId = ReadString(root, "projectId");
        HeirowSongProject project = GetProject(projectId, ownerKey);
        if (project.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("Only the owner can upload source audio.");
        string fileName = SafeFileName(ReadString(root, "fileName"));
        string mediaType = ReadString(root, "mediaType").ToLowerInvariant();
        if (!AllowedAudioTypes.Contains(mediaType)) throw new ArgumentException("Upload must be WAV, MP3, FLAC, OGG, or M4A audio.");
        string data = ReadString(root, "base64");
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) data = data[(data.IndexOf(',') + 1)..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); } catch { throw new ArgumentException("Upload payload is not valid base64 audio."); }
        if (bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024) throw new ArgumentException("Audio uploads must be between 1 byte and 25 MB.");
        string target = Path.Combine(ProjectArtifactDirectory(project.OwnerId, project.Id), "upload-" + Guid.NewGuid().ToString("N")[..10] + Path.GetExtension(fileName));
        File.WriteAllBytes(target, bytes);
        HeirowSongArtifact artifact = AddArtifact(project, "", "source", "", target, mediaType);
        project.Audit.Add(new HeirowSongAuditEvent { Category = "upload", Detail = "Owner uploaded " + fileName + "." });
        SaveProjectFile(project);
        return new { ok = true, artifact = PublicArtifact(artifact) };
    }

    public object ListPersonas(string ownerKey)
    {
        string ownerId = OwnerId(ownerKey);
        return new { ok = true, personas = _personas.Values.Where(value => value.OwnerId == ownerId).Select(RedactPersona).ToArray(), voices = _voices.Values.Where(value => value.OwnerId == ownerId).Select(PublicVoice).ToArray() };
    }

    public HeirowSongPersona SavePersona(HeirowSongPersona persona, string ownerKey)
    {
        persona ??= new(); persona.Id = SafeId(persona.Id, "persona_"); persona.OwnerId = OwnerId(ownerKey); persona.Name = Limit(First(persona.Name, "New persona"), 100); persona.Description = Limit(persona.Description, 2000);
        if (_personas.TryGetValue(persona.Id, out HeirowSongPersona existing) && existing.OwnerId != persona.OwnerId) throw new UnauthorizedAccessException("This persona belongs to another Workstation account.");
        _personas[persona.Id] = persona; SaveProfiles(); return persona;
    }

    public HeirowSongVoiceProfile BeginVoiceEnrollment(string name, bool attested, string ownerKey)
    {
        if (!attested) throw new InvalidOperationException("You must attest that this is your own voice and that you consent to local processing.");
        var voice = new HeirowSongVoiceProfile { OwnerId = OwnerId(ownerKey), Name = Limit(First(name, "My voice"), 100), OwnershipAttested = true, Challenge = BuildChallenge() };
        _voices[voice.Id] = voice; SaveProfiles(); return voice;
    }

    public HeirowSongShareGrant SaveShare(HeirowSongShareGrant incoming, string ownerKey)
    {
        incoming ??= new(); HeirowSongProject project = GetProject(incoming.ProjectId, ownerKey);
        string ownerId = OwnerId(ownerKey); if (project.OwnerId != ownerId) throw new UnauthorizedAccessException("Only the owner can share this project.");
        incoming.Id = SafeId(incoming.Id, "songshare_"); incoming.OwnerId = ownerId; incoming.ProjectId = project.Id;
        incoming.RecipientOwnerId = HashExternalOwnerId(First(incoming.RecipientUserName, incoming.RecipientOwnerId));
        incoming.RecipientUserName = "";
        if (string.IsNullOrWhiteSpace(incoming.RecipientOwnerId) || incoming.RecipientOwnerId == ownerId) throw new ArgumentException("A different recipient Workstation account is required.");
        if (!incoming.CanRead && !incoming.CanDownload && !incoming.CanRemix) throw new ArgumentException("Select at least one sharing permission.");
        if (incoming.ExpiresUtc.HasValue && incoming.ExpiresUtc <= DateTimeOffset.UtcNow) throw new ArgumentException("Share expiry must be in the future.");
        _shares[incoming.Id] = incoming; SaveProfiles(); return incoming;
    }

    public void RevokeShare(string shareId, string ownerKey)
    {
        if (!_shares.TryGetValue(shareId ?? "", out HeirowSongShareGrant grant)) throw new FileNotFoundException("Share grant was not found.");
        if (grant.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("Only the owner can revoke this share.");
        grant.Revoked = true; SaveProfiles();
    }

    private void StartJobRun(HeirowSongJob job)
    {
        if (_runs.ContainsKey(job.Id) || _lifetime.IsCancellationRequested) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (!_runs.TryAdd(job.Id, cancellation)) { cancellation.Dispose(); return; }
        _ = Task.Run(() => RunJobAsync(job, cancellation.Token));
    }

    private async Task RunJobAsync(HeirowSongJob job, CancellationToken cancellationToken)
    {
        try
        {
            job.State = "running"; job.Message = "Preparing local music worker."; Touch(job);
            var progress = new Progress<HeirowSongProgress>(value => { job.Progress = Math.Clamp(value.Percent, 0, 100); job.Message = Limit(value.Message, 500); Touch(job); });
            IHeirowSongMediaExecutor executor = _proxy.HeirowSongMediaExecutor ?? throw new InvalidOperationException("heirowSong media runtime is not configured.");
            HeirowSongMediaResult result = job.Operation switch
            {
                "text2music" => await executor.GenerateAsync(job.Request, progress, cancellationToken).ConfigureAwait(false),
                "extract" => await executor.ExtractStemAsync(job.Request, job.Request.Stem, progress, cancellationToken).ConfigureAwait(false),
                "lego" or "complete" => await executor.CompleteTrackAsync(job.Request, progress, cancellationToken).ConfigureAwait(false),
                "midi" => await executor.TranscribeMidiAsync(job.Request, progress, cancellationToken).ConfigureAwait(false),
                _ => await executor.EditAsync(job.Request, progress, cancellationToken).ConfigureAwait(false)
            };
            if (!result.Success) throw new InvalidOperationException(First(result.Error, "Music generation returned no output."));
            HeirowSongProject project = _projects[job.ProjectId];
            string versionId = "songversion_" + Guid.NewGuid().ToString("N");
            foreach (HeirowSongMediaArtifact output in result.Artifacts)
            {
                if (!File.Exists(output.FilePath)) continue;
                string copied = CopyIntoProject(project, output.FilePath, output.Kind);
                HeirowSongArtifact artifact = AddArtifact(project, versionId, output.Kind, output.Stem, copied, output.MediaType);
                job.ArtifactIds.Add(artifact.Id);
            }
            if (job.ArtifactIds.Count == 0) throw new InvalidOperationException("Music worker completed without a readable artifact.");
            var version = new HeirowSongVersion { Id = versionId, ParentVersionId = project.CurrentVersionId, Operation = job.Operation, Prompt = job.Request.Prompt, Lyrics = job.Request.Lyrics, Instrumental = job.Request.Instrumental, DurationSeconds = job.Request.DurationSeconds, Bpm = job.Request.Bpm, KeyScale = job.Request.KeyScale, TimeSignature = job.Request.TimeSignature, Seed = job.Request.Seed, ArtifactIds = job.ArtifactIds.ToList() };
            project.Versions.Add(version); project.CurrentVersionId = version.Id; project.Revision++; project.UpdatedUtc = DateTimeOffset.UtcNow;
            project.Audit.Add(new HeirowSongAuditEvent { Category = "generation", Detail = job.Operation + " completed locally." });
            SaveProjectFile(project);
            job.State = "completed"; job.Progress = 100; job.Message = "Music is ready."; Touch(job);
        }
        catch (OperationCanceledException) { job.State = "cancelled"; job.Message = "Job cancelled; completed artifacts were preserved."; Touch(job); }
        catch (Exception ex) { job.State = "failed"; job.Message = Limit(ex.Message, 1000); Touch(job); }
        finally { if (_runs.TryRemove(job.Id, out CancellationTokenSource cancellation)) cancellation.Dispose(); }
    }

    private void LoadState()
    {
        foreach (string file in Directory.EnumerateFiles(ProjectRoot, "*.json", SearchOption.AllDirectories))
        {
            try { HeirowSongProject project = JsonSerializer.Deserialize<HeirowSongProject>(File.ReadAllText(file), _json); if (project != null) _projects[project.Id] = project; } catch { }
        }
        foreach (string file in Directory.EnumerateFiles(JobRoot, "*.json", SearchOption.AllDirectories))
        {
            try { HeirowSongJob job = JsonSerializer.Deserialize<HeirowSongJob>(File.ReadAllText(file), _json); if (job != null) { if (job.State == "running") { job.State = "interrupted"; job.Message = "Generation was interrupted by a Workstation restart."; SaveJob(job); } _jobs[job.Id] = job; } } catch { }
        }
        try
        {
            string path = Path.Combine(ProfileRoot, "profiles.json"); if (!File.Exists(path)) return;
            HeirowSongProfiles state = JsonSerializer.Deserialize<HeirowSongProfiles>(File.ReadAllText(path), _json) ?? new();
            foreach (HeirowSongPersona value in state.Personas) _personas[value.Id] = value;
            foreach (HeirowSongVoiceProfile value in state.Voices) _voices[value.Id] = value;
            foreach (HeirowSongShareGrant value in state.Shares) _shares[value.Id] = value;
        }
        catch { }
    }

    private void ResumeQueuedJobs() { foreach (HeirowSongJob job in _jobs.Values.Where(value => value.State == "queued").OrderBy(value => value.CreatedUtc)) StartJobRun(job); }
    private void Touch(HeirowSongJob job) { job.UpdatedUtc = DateTimeOffset.UtcNow; SaveJob(job); }
    private void CancelProjectJobs(string projectId) { foreach (HeirowSongJob job in _jobs.Values.Where(value => value.ProjectId == projectId && value.State is "queued" or "running")) if (_runs.TryGetValue(job.Id, out CancellationTokenSource cancellation)) cancellation.Cancel(); }
    private void SaveProjectFile(HeirowSongProject project) { lock (_saveLock) AtomicWrite(ProjectPath(project.OwnerId, project.Id), JsonSerializer.Serialize(project, _json)); }
    private void SaveJob(HeirowSongJob job) { lock (_saveLock) AtomicWrite(Path.Combine(JobRoot, job.OwnerId, SafeId(job.Id, "songjob_") + ".json"), JsonSerializer.Serialize(job, _json)); }
    private void SaveProfiles() { lock (_saveLock) AtomicWrite(Path.Combine(ProfileRoot, "profiles.json"), JsonSerializer.Serialize(new HeirowSongProfiles { Personas = _personas.Values.ToList(), Voices = _voices.Values.ToList(), Shares = _shares.Values.ToList() }, _json)); }
    private string ProjectPath(string ownerId, string projectId) => Path.Combine(ProjectRoot, ownerId, SafeId(projectId, "songproject_") + ".json");
    private string ProjectArtifactDirectory(string ownerId, string projectId) { string path = Path.Combine(ArtifactRoot, ownerId, SafeId(projectId, "songproject_")); Directory.CreateDirectory(path); return path; }
    private string CopyIntoProject(HeirowSongProject project, string source, string kind) { string extension = Path.GetExtension(source); if (string.IsNullOrWhiteSpace(extension)) extension = kind == "midi" ? ".mid" : ".wav"; string target = Path.Combine(ProjectArtifactDirectory(project.OwnerId, project.Id), SafeId(kind, "song") + "-" + Guid.NewGuid().ToString("N")[..10] + extension); if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true); return target; }
    private string ResolveArtifactPath(HeirowSongProject project, string artifactId) { HeirowSongArtifact artifact = project.Artifacts.FirstOrDefault(value => value.Id.Equals(artifactId ?? "", StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("Source song artifact was not found."); if (!IsUnderRoot(artifact.FilePath, ArtifactRoot) || !File.Exists(artifact.FilePath)) throw new FileNotFoundException("Source song artifact file is unavailable."); return artifact.FilePath; }
    private HeirowSongArtifact AddArtifact(HeirowSongProject project, string versionId, string kind, string stem, string path, string mediaType) { var artifact = new HeirowSongArtifact { VersionId = versionId, Kind = Limit(First(kind, "song"), 40), Stem = Limit(stem, 40), FilePath = Path.GetFullPath(path), MediaType = First(mediaType, "application/octet-stream"), SizeBytes = new FileInfo(path).Length, Sha256 = Sha256File(path) }; project.Artifacts.Add(artifact); return artifact; }
    private static object PublicArtifact(HeirowSongArtifact artifact) => new { artifact.Id, artifact.VersionId, artifact.Kind, artifact.Stem, artifact.MediaType, artifact.SizeBytes, artifact.Sha256, artifact.CreatedUtc, url = "/api/heirowsong/artifacts/" + Uri.EscapeDataString(artifact.Id) };
    private static object PublicVoice(HeirowSongVoiceProfile voice) => new { voice.Id, voice.Name, voice.OwnershipAttested, voice.Verified, voice.SpeakerSimilarity, voice.State, voice.CreatedUtc };
    public static HeirowSongProject RedactProject(HeirowSongProject project) { var copy = JsonSerializer.Deserialize<HeirowSongProject>(JsonSerializer.Serialize(project, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); copy.OwnerId = ""; foreach (HeirowSongArtifact artifact in copy.Artifacts) artifact.FilePath = ""; return copy; }
    public static HeirowSongJob RedactJob(HeirowSongJob job) { var copy = JsonSerializer.Deserialize<HeirowSongJob>(JsonSerializer.Serialize(job, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); copy.OwnerId = ""; copy.Request.SourcePath = ""; copy.Request.ReferencePath = ""; copy.Request.AdapterPath = ""; copy.Request.OutputDirectory = ""; return copy; }
    public static object RedactPersona(HeirowSongPersona persona) => new { persona.Id, persona.Name, persona.Description, persona.State, adapterInstalled = !string.IsNullOrWhiteSpace(persona.AdapterPath) };
    private static void NormalizeTimeline(HeirowSongProject project) { project.Tracks = project.Tracks.Take(12).ToList(); foreach (HeirowSongTrack track in project.Tracks) { track.GainDb = Math.Clamp(track.GainDb, -60, 18); track.Pan = Math.Clamp(track.Pan, -1, 1); track.PitchSemitones = Math.Clamp(track.PitchSemitones, -24, 24); track.Tempo = Math.Clamp(track.Tempo, .25, 4); track.Regions ??= new(); foreach (HeirowSongRegion region in track.Regions) { region.TimelineStartSeconds = Math.Max(0, region.TimelineStartSeconds); region.SourceStartSeconds = Math.Max(0, region.SourceStartSeconds); region.DurationSeconds = Math.Max(0, region.DurationSeconds); region.FadeInSeconds = Math.Max(0, region.FadeInSeconds); region.FadeOutSeconds = Math.Max(0, region.FadeOutSeconds); region.TakeLane = Math.Clamp(region.TakeLane, 0, 64); } } }
    private static bool IsGrantActive(HeirowSongShareGrant grant) => !grant.Revoked && (!grant.ExpiresUtc.HasValue || grant.ExpiresUtc > DateTimeOffset.UtcNow);
    private static string BuildChallenge() { string[] words = { "amber", "river", "seven", "violet", "echo", "harbor", "silver", "canvas", "orbit", "winter" }; Span<byte> bytes = stackalloc byte[4]; RandomNumberGenerator.Fill(bytes); return "I confirm this is my voice: " + words[bytes[0] % words.Length] + " " + words[bytes[1] % words.Length] + " " + words[bytes[2] % words.Length] + " " + (100 + bytes[3] % 900).ToString(CultureInfo.InvariantCulture) + "."; }
    private static string OwnerId(string ownerKey) { if (string.IsNullOrWhiteSpace(ownerKey)) throw new UnauthorizedAccessException("A signed-in Workstation account is required."); return Sha256Text(ownerKey.Trim().ToLowerInvariant())[..32]; }
    private static string HashExternalOwnerId(string value) { value = (value ?? "").Trim(); if (value.Length == 32 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant(); if (string.IsNullOrWhiteSpace(value)) return ""; string ownerKey = value.StartsWith("webauth:", StringComparison.OrdinalIgnoreCase) ? value.ToLowerInvariant() : "webauth:" + value.ToLowerInvariant(); return Sha256Text(ownerKey)[..32]; }
    private static string SafeId(string value, string prefix) { string result = new((value ?? "").Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').Take(96).ToArray()); return string.IsNullOrWhiteSpace(result) ? prefix + Guid.NewGuid().ToString("N") : result; }
    private static string SafeFileName(string value) { string name = Path.GetFileName(First(value, "upload.wav")); foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_'); return Limit(name, 160); }
    private static string Limit(string value, int max) { value ??= ""; return value.Length <= max ? value : value[..max]; }
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static string ReadString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string NormalizeExportFormat(string value) { value = First(value, "wav").Trim('.').ToLowerInvariant(); return value is "wav" or "mp3" or "flac" or "mid" or "midi" ? value : "wav"; }
    private static bool IsUnderRoot(string path, string root) { string full = Path.GetFullPath(path); string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase); }
    private static string Sha256File(string path) { using SHA256 sha = SHA256.Create(); using FileStream stream = File.OpenRead(path); return ToHex(sha.ComputeHash(stream)); }
    private static string Sha256Text(string value) { using SHA256 sha = SHA256.Create(); return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))); }
    private static string ToHex(byte[] bytes) { var builder = new StringBuilder(bytes.Length * 2); foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return builder.ToString(); }
    private static void AtomicWrite(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temporary, content, Encoding.UTF8); if (File.Exists(path)) File.Delete(path); File.Move(temporary, path); }
    public void Dispose() { _lifetime.Cancel(); foreach (CancellationTokenSource cancellation in _runs.Values) cancellation.Cancel(); _lifetime.Dispose(); }

    private sealed class HeirowSongProfiles
    {
        public List<HeirowSongPersona> Personas { get; set; } = new();
        public List<HeirowSongVoiceProfile> Voices { get; set; } = new();
        public List<HeirowSongShareGrant> Shares { get; set; } = new();
    }
}

public static class HeirowSongModelBundleDescriptor
{
    public static object Create() => new
    {
        id = "heirowsong-ace-step-1.5-v0.1.8",
        displayName = "heirowSong — ACE-Step 1.5",
        task = "text-to-audio",
        payloadBytes = 12_501_168_402L,
        payloadGiB = 11.64,
        license = "MIT",
        runtime = new { version = "v0.1.8", commit = "dce621408bee8c31b4fcf4811682eb9359e1bc94", signedPackRequired = true },
        repositories = new object[]
        {
            new { repository = "ACE-Step/Ace-Step1.5", revision = "19671f406d603126926c1b7e2adc169acbcade22", includePrefixes = new[] { "acestep-v15-turbo/", "vae/", "Qwen3-Embedding-0.6B/" }, sizeBytes = 6_336_680_772L },
            new { repository = "ACE-Step/acestep-5Hz-lm-0.6B", revision = "148d8ea0225bdab342ee1ae3a354275ccd60ca80", includePrefixes = new[] { "*" }, sizeBytes = 1_372_702_240L },
            new { repository = "ACE-Step/acestep-v15-base", revision = "e432212fec32b8965a14ffa57ae653438d6abd14", includePrefixes = new[] { "*" }, exclude = new[] { "README.md", ".gitattributes" }, sizeBytes = 4_791_785_390L }
        }
    };
}

public partial class HeirowLlm
{
    private HeirowSongService _heirowSong;
    public IHeirowSongMediaExecutor HeirowSongMediaExecutor { get; set; }
    public Func<CancellationToken, Task<string>> HeirowSongInstallRequestedAsync { get; set; }

    private void RegisterHeirowSongRoutes(HttpServer server)
    {
        _heirowSong ??= new HeirowSongService(this, Path.Combine(_chatSessionRoot, "HeirowSong"));
        string html = HtmlPageResources.GetHtml("HeirowSong.html");
        server.Map("GET", "/heirowSong", (_, request, _) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/heirowSong/", (_, request, _) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/api/heirowsong/capabilities", (_, request, token) => HeirowSongJson(() => _heirowSong.CapabilitiesAsync(token).GetAwaiter().GetResult(), request));
        server.Map("POST", "/api/heirowsong/install", (connection, request, token) => HeirowSongJson(() =>
        {
            _ = GetChatSessionOwnerKey(connection, request);
            Func<CancellationToken, Task<string>> installer = HeirowSongInstallRequestedAsync ?? throw new InvalidOperationException("The heirowSong installer is unavailable in this host.");
            return new { ok = true, message = installer(token).GetAwaiter().GetResult() };
        }, request));
        server.Map("GET", "/api/heirowsong/projects", (connection, request, _) => HeirowSongJson(() => _heirowSong.ListProjects(GetChatSessionOwnerKey(connection, request)), request));
        server.Map("GET", "/api/heirowsong/songs", (connection, request, _) => HeirowSongJson(() => _heirowSong.ListProjects(GetChatSessionOwnerKey(connection, request)), request));
        server.Map("GET", "/api/heirowsong/projects/*", (connection, request, _) => HeirowSongJson(() => new { ok = true, project = HeirowSongService.RedactProject(_heirowSong.GetProject(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("GET", "/api/heirowsong/songs/*", (connection, request, _) => HeirowSongJson(() => new { ok = true, project = HeirowSongService.RedactProject(_heirowSong.GetProject(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("POST", "/api/heirowsong/projects/save", (connection, request, _) => HeirowSongJson(() => new { ok = true, project = HeirowSongService.RedactProject(_heirowSong.SaveProject(JsonSerializer.Deserialize<HeirowSongProject>(request.Body ?? "{}", HeirowSongJsonOptions), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("POST", "/api/heirowsong/projects/delete", (connection, request, _) => HeirowSongJson(() => { using JsonDocument document = JsonDocument.Parse(request.Body ?? "{}"); _heirowSong.DeleteProject(HsString(document.RootElement, "projectId"), GetChatSessionOwnerKey(connection, request)); return new { ok = true }; }, request));
        server.Map("POST", "/api/heirowsong/uploads", (connection, request, _) => HeirowSongJson(() => _heirowSong.Upload(request.Body, GetChatSessionOwnerKey(connection, request)), request));
        server.Map("POST", "/api/heirowsong/jobs", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(_heirowSong.StartJob(JsonSerializer.Deserialize<HeirowSongMediaRequest>(request.Body ?? "{}", HeirowSongJsonOptions), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("POST", "/api/heirowsong/edits", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(StartHeirowSongOperation(connection, request, "")) }, request));
        server.Map("POST", "/api/heirowsong/stems", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(StartHeirowSongOperation(connection, request, "extract")) }, request));
        server.Map("POST", "/api/heirowsong/midi", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(StartHeirowSongOperation(connection, request, "midi")) }, request));
        server.Map("POST", "/api/heirowsong/exports", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(StartHeirowSongOperation(connection, request, "export")) }, request));
        server.Map("GET", "/api/heirowsong/jobs/*", (connection, request, _) => HeirowSongJson(() => new { ok = true, job = HeirowSongService.RedactJob(_heirowSong.GetJob(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("POST", "/api/heirowsong/jobs/cancel", (connection, request, _) => HeirowSongJson(() => { using JsonDocument document = JsonDocument.Parse(request.Body ?? "{}"); _heirowSong.CancelJob(HsString(document.RootElement, "jobId"), GetChatSessionOwnerKey(connection, request)); return new { ok = true }; }, request));
        server.Map("POST", "/api/heirowsong/jobs/retry", (connection, request, _) => HeirowSongJson(() => { using JsonDocument document = JsonDocument.Parse(request.Body ?? "{}"); return new { ok = true, job = HeirowSongService.RedactJob(_heirowSong.RetryJob(HsString(document.RootElement, "jobId"), GetChatSessionOwnerKey(connection, request))) }; }, request));
        server.MapStream("GET", "/api/heirowsong/events", (connection, request, stream, token) => StreamHeirowSongEventsAsync(connection, request, stream, token));
        server.Map("GET", "/api/heirowsong/artifacts/*", (connection, request, _) => HeirowSongFile(() => _heirowSong.Artifact(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request), false), request));
        server.Map("GET", "/api/heirowsong/exports/*", (connection, request, _) => HeirowSongFile(() => _heirowSong.Artifact(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request), true), request));
        server.Map("GET", "/api/heirowsong/personas", (connection, request, _) => HeirowSongJson(() => _heirowSong.ListPersonas(GetChatSessionOwnerKey(connection, request)), request));
        server.Map("POST", "/api/heirowsong/personas", (connection, request, _) => HeirowSongJson(() => new { ok = true, persona = HeirowSongService.RedactPersona(_heirowSong.SavePersona(JsonSerializer.Deserialize<HeirowSongPersona>(request.Body ?? "{}", HeirowSongJsonOptions), GetChatSessionOwnerKey(connection, request))) }, request));
        server.Map("POST", "/api/heirowsong/voices/enroll", (connection, request, _) => HeirowSongJson(() => { using JsonDocument document = JsonDocument.Parse(request.Body ?? "{}"); return new { ok = true, voice = _heirowSong.BeginVoiceEnrollment(HsString(document.RootElement, "name"), HsBool(document.RootElement, "ownershipAttested"), GetChatSessionOwnerKey(connection, request)) }; }, request));
        server.Map("POST", "/api/heirowsong/shares", (connection, request, _) => HeirowSongJson(() => new { ok = true, share = _heirowSong.SaveShare(JsonSerializer.Deserialize<HeirowSongShareGrant>(request.Body ?? "{}", HeirowSongJsonOptions), GetChatSessionOwnerKey(connection, request)) }, request));
        server.Map("POST", "/api/heirowsong/shares/revoke", (connection, request, _) => HeirowSongJson(() => { using JsonDocument document = JsonDocument.Parse(request.Body ?? "{}"); _heirowSong.RevokeShare(HsString(document.RootElement, "shareId"), GetChatSessionOwnerKey(connection, request)); return new { ok = true }; }, request));
        foreach (string path in new[] { "/api/heirowsong/capabilities", "/api/heirowsong/install", "/api/heirowsong/projects", "/api/heirowsong/songs", "/api/heirowsong/projects/save", "/api/heirowsong/projects/delete", "/api/heirowsong/uploads", "/api/heirowsong/jobs", "/api/heirowsong/edits", "/api/heirowsong/stems", "/api/heirowsong/midi", "/api/heirowsong/exports", "/api/heirowsong/jobs/cancel", "/api/heirowsong/jobs/retry", "/api/heirowsong/events", "/api/heirowsong/personas", "/api/heirowsong/voices/enroll", "/api/heirowsong/shares", "/api/heirowsong/shares/revoke" }) server.Map("OPTIONS", path, (_, request, _) => HandleWebAuthCorsPreflight(request));
    }

    private HeirowSongJob StartHeirowSongOperation(NetworkConnection connection, HttpRequest request, string operation)
    {
        HeirowSongMediaRequest media = JsonSerializer.Deserialize<HeirowSongMediaRequest>(request.Body ?? "{}", HeirowSongJsonOptions) ?? new();
        if (!string.IsNullOrWhiteSpace(operation)) media.Operation = operation;
        return _heirowSong.StartJob(media, GetChatSessionOwnerKey(connection, request));
    }

    private async Task StreamHeirowSongEventsAsync(NetworkConnection connection, HttpRequest request, ChunkedStream stream, CancellationToken cancellationToken)
    {
        stream.ContentType = "text/event-stream";
        string jobId = GetQueryParameter(request, "jobId");
        string ownerKey = GetChatSessionOwnerKey(connection, request);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HeirowSongJob job = _heirowSong.GetJob(jobId, ownerKey);
                stream.WriteLine("event: status"); stream.WriteLine("data: " + JsonSerializer.Serialize(new { ok = true, job = HeirowSongService.RedactJob(job) }, HeirowSongJsonOptions)); stream.WriteLine("");
                if (job.State is "completed" or "failed" or "cancelled" or "interrupted") break;
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { stream.WriteLine("event: error"); stream.WriteLine("data: " + JsonSerializer.Serialize(new { error = ex.Message })); stream.WriteLine(""); break; }
        }
    }

    private static readonly JsonSerializerOptions HeirowSongJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private object HeirowSongJson(Func<object> action, HttpRequest request) { AddWebAuthCorsHeaders(request); try { request.Context.Response.ContentType = "application/json; charset=utf-8"; return JsonSerializer.Serialize(action(), HeirowSongJsonOptions); } catch (UnauthorizedAccessException ex) { return BuildJsonError(request, 403, "Forbidden", ex.Message); } catch (FileNotFoundException ex) { return BuildJsonError(request, 404, "Not Found", ex.Message); } catch (InvalidOperationException ex) { return BuildJsonError(request, 409, "Conflict", ex.Message); } catch (Exception ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); } }
    private object HeirowSongFile(Func<object> action, HttpRequest request) { try { return action(); } catch (UnauthorizedAccessException ex) { return BuildJsonError(request, 403, "Forbidden", ex.Message); } catch (FileNotFoundException ex) { return BuildJsonError(request, 404, "Not Found", ex.Message); } }
    private static string HsString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool HsBool(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
}
