using System.Diagnostics;
using System.Globalization;
using System.Text;
using JackONNX.Image;
using JackONNX.Runtime;
using JackONNX.Video;
using SocketJack.Net;

namespace JackONNX.JackDirector;

public sealed class JackOnnxJackDirectorMediaExecutor : IJackDirectorMediaExecutor
{
    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly JackOnnxImagePipeline _images;
    private readonly JackOnnxVideoPipeline _videos;

    public JackOnnxJackDirectorMediaExecutor(JackOnnxRuntimeEngine runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _images = new JackOnnxImagePipeline(runtime);
        _videos = new JackOnnxVideoPipeline(runtime);
    }

    public async Task<JackDirectorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JackOnnxDeviceInfo> devices = await _runtime.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JackOnnxProviderCompatibility> providers = await _runtime.CheckProviderCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JackOnnxModelManifest> models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JackOnnxJobSnapshot> jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        return new JackDirectorCapabilities
        {
            MachineName = Environment.MachineName,
            OperatingSystem = Environment.OSVersion.ToString(),
            Providers = providers.Where(p => p.IsAvailable && p.CanCreateSessionOptions).Select(p => p.Provider.ToString()).Distinct().ToList(),
            ImageModels = models.Where(IsImage).Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            VideoModels = models.Where(IsVideo).Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DedicatedMemoryBytes = devices.Where(d => d.IsAvailable).Sum(d => d.DedicatedMemoryBytes ?? 0),
            FreeMemoryBytes = devices.Where(d => d.IsAvailable).Sum(d => d.DedicatedMemoryBytes ?? 0),
            FfmpegAvailable = !string.IsNullOrWhiteSpace(JackOnnxVideoPresentationConverter.ResolveFfmpegPath()),
            ActiveJobs = jobs.Count(j => j.State is not JackOnnxJobState.Completed and not JackOnnxJobState.Cancelled and not JackOnnxJobState.Failed),
            MaxConcurrentVideoJobs = Math.Max(1, devices.Count(d => d.IsAvailable && d.Provider is JackOnnxExecutionProvider.Cuda or JackOnnxExecutionProvider.DirectML))
        };
    }

    public async Task<JackDirectorMediaResult> GenerateImageAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default)
    {
        try
        {
            JackOnnxGenerationResult result = await _images.GenerateAsync(new ImageGenerationRequest
            {
                ModelId = request.ModelId,
                Prompt = request.Prompt,
                NegativePrompt = request.NegativePrompt,
                SourcePath = request.SourcePath,
                SourceDataUrl = request.SourceDataUrl,
                SourceKind = string.IsNullOrWhiteSpace(request.SourcePath) && string.IsNullOrWhiteSpace(request.SourceDataUrl) ? "" : (request.GenerationMode.Contains("video-to-video", StringComparison.OrdinalIgnoreCase) ? "video" : "image"),
                GenerationMode = request.GenerationMode,
                Width = request.Width,
                Height = request.Height,
                Steps = request.Steps,
                Seed = request.Seed,
                Context = Context(request)
            }, Map(progress), cancellationToken).ConfigureAwait(false);
            return Convert(result, "image/png");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new JackDirectorMediaResult { Error = ex.Message }; }
    }

    public async Task<JackDirectorMediaResult> GenerateVideoAsync(JackDirectorMediaRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default)
    {
        try
        {
            JackOnnxGenerationResult result = await _videos.GenerateAsync(new VideoGenerationRequest
            {
                ModelId = request.ModelId,
                Prompt = request.Prompt,
                NegativePrompt = request.NegativePrompt,
                SourcePath = request.SourcePath,
                SourceDataUrl = request.SourceDataUrl,
                SourceKind = string.IsNullOrWhiteSpace(request.SourcePath) && string.IsNullOrWhiteSpace(request.SourceDataUrl) ? "" : (request.GenerationMode.Contains("video-to-video", StringComparison.OrdinalIgnoreCase) ? "video" : "image"),
                GenerationMode = request.GenerationMode,
                Width = request.Width,
                Height = request.Height,
                Fps = request.Fps,
                Frames = Math.Clamp(request.Frames, 1, 96),
                Seconds = request.Seconds,
                Steps = request.Steps,
                Seed = request.Seed,
                Context = Context(request)
            }, Map(progress), cancellationToken).ConfigureAwait(false);
            return Convert(result, "video/mp4");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new JackDirectorMediaResult { Error = ex.Message }; }
    }

    public async Task<JackDirectorMediaResult> AssembleAsync(JackDirectorAssemblyRequest request, IProgress<JackDirectorProgress> progress, CancellationToken cancellationToken = default)
    {
        string ffmpeg = JackOnnxVideoPresentationConverter.ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg)) return new JackDirectorMediaResult { Error = "FFmpeg was not found. Set JACKONNX_FFMPEG, SOCKETJACK_FFMPEG, or FFMPEG_PATH." };
        if (request.ClipPaths.Count == 0) return new JackDirectorMediaResult { Error = "No clips were supplied for assembly." };
        foreach (string clip in request.ClipPaths) if (!File.Exists(clip)) return new JackDirectorMediaResult { Error = "Assembly clip was not found: " + clip };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
        string listPath = request.OutputPath + ".concat-" + Guid.NewGuid().ToString("N") + ".txt";
        string tempPath = request.OutputPath + ".tmp-" + Guid.NewGuid().ToString("N") + ".mp4";
        try
        {
            await File.WriteAllLinesAsync(listPath, request.ClipPaths.Select(p => "file '" + Path.GetFullPath(p).Replace("'", "'\\''") + "'"), cancellationToken).ConfigureAwait(false);
            progress?.Report(new JackDirectorProgress { Percent = 5, Message = "Normalizing and assembling clips with FFmpeg." });
            var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            Add(start, "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listPath);
            bool audio = !string.IsNullOrWhiteSpace(request.AudioPath) && File.Exists(request.AudioPath);
            if (audio) Add(start, "-i", request.AudioPath);
            Add(start, "-map", "0:v:0");
            if (audio) Add(start, "-map", "1:a:0", "-shortest"); else Add(start, "-map", "0:a?");
            Add(start, "-vf", "scale=" + request.Width.ToString(CultureInfo.InvariantCulture) + ":" + request.Height.ToString(CultureInfo.InvariantCulture) + ":force_original_aspect_ratio=decrease,pad=" + request.Width.ToString(CultureInfo.InvariantCulture) + ":" + request.Height.ToString(CultureInfo.InvariantCulture) + ":(ow-iw)/2:(oh-ih)/2,fps=" + request.Fps.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-c:a", "aac", "-b:a", "160k", tempPath);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new JackDirectorMediaResult { Error = "FFmpeg did not start." };
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempPath)) return new JackDirectorMediaResult { Error = "FFmpeg assembly failed: " + error };
            File.Move(tempPath, request.OutputPath, true);
            progress?.Report(new JackDirectorProgress { Percent = 100, Message = "Finished MP4 assembled." });
            return new JackDirectorMediaResult { Success = true, ArtifactPath = request.OutputPath, MediaType = "video/mp4" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new JackDirectorMediaResult { Error = "FFmpeg assembly failed: " + ex.Message }; }
        finally { TryDelete(listPath); TryDelete(tempPath); }
    }

    public async Task<JackDirectorMediaResult> ExtractLastFrameAsync(string sourcePath, string outputPath, CancellationToken cancellationToken = default)
    {
        string ffmpeg = JackOnnxVideoPresentationConverter.ResolveFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpeg)) return new JackDirectorMediaResult { Error = "FFmpeg was not found for continuity-frame extraction." };
        if (!File.Exists(sourcePath)) return new JackDirectorMediaResult { Error = "Continuity source clip was not found." };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            Add(start, "-y", "-hide_banner", "-loglevel", "error", "-sseof", "-0.12", "-i", sourcePath, "-frames:v", "1", outputPath);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new JackDirectorMediaResult { Error = "FFmpeg did not start for continuity-frame extraction." };
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string detail = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath)) return new JackDirectorMediaResult { Error = "Continuity-frame extraction failed: " + detail };
            return new JackDirectorMediaResult { Success = true, ArtifactPath = outputPath, MediaType = "image/png" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new JackDirectorMediaResult { Error = "Continuity-frame extraction failed: " + ex.Message }; }
    }

    private static JackOnnxGenerationContext Context(JackDirectorMediaRequest request) => new()
    {
        LlmRuntimeSessionId = request.ProjectId,
        LlmRuntimeToolCallId = request.JobId,
        LlmRuntimeParentRunId = request.ShotId,
        OwnerKey = "jackdirector",
        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["projectId"] = request.ProjectId, ["shotId"] = request.ShotId }
    };

    private static IProgress<JackOnnxProgress> Map(IProgress<JackDirectorProgress> progress) => new Progress<JackOnnxProgress>(p => progress?.Report(new JackDirectorProgress { Percent = p.Percent, Message = p.Message }));
    private static JackDirectorMediaResult Convert(JackOnnxGenerationResult result, string fallbackType)
    {
        JackOnnxArtifact? artifact = result.Artifacts.FirstOrDefault(a => File.Exists(a.FilePath));
        return new JackDirectorMediaResult { Success = result.Success && artifact != null, Error = result.Success ? (artifact == null ? "Generation returned no artifact file." : "") : result.Message, ArtifactPath = artifact?.FilePath ?? "", MediaType = string.IsNullOrWhiteSpace(artifact?.MediaType) ? fallbackType : artifact.MediaType, JobId = result.JobId };
    }
    private static bool IsVideo(JackOnnxModelManifest m) { string t = (m.Type + " " + m.Id).ToLowerInvariant(); return t.Contains("video") || t.Contains("t2v") || t.Contains("i2v") || t.Contains("wan2") || t.Contains("ltx"); }
    private static bool IsImage(JackOnnxModelManifest m) { string t = (m.Type + " " + m.Id).ToLowerInvariant(); return !IsVideo(m) && (t.Contains("image") || t.Contains("diffusion") || t.Contains("flux") || t.Contains("sdxl")); }
    private static void Add(ProcessStartInfo start, params string[] args) { foreach (string arg in args) start.ArgumentList.Add(arg); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
