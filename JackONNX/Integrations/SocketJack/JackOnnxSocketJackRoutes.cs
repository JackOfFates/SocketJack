using System.Text.Json;
using JackONNX.Audio;
using JackONNX.Image;
using JackONNX.Runtime;
using JackONNX.Video;
using SocketJack.Net;

namespace JackONNX.SocketJack;

public static class JackOnnxSocketJackRoutes
{
    public const string Status = "/api/jackonnx/status";
    public const string Devices = "/api/jackonnx/devices";
    public const string Models = "/api/jackonnx/models";
    public const string ImageGenerate = "/api/jackonnx/image/generate";
    public const string AudioGenerate = "/api/jackonnx/audio/generate";
    public const string VideoGenerate = "/api/jackonnx/video/generate";
    public const string VideoPresentation = "/api/jackonnx/video/presentation";
    public const string Job = "/api/jackonnx/jobs/{id}";
    public const string JobCancel = "/api/jackonnx/jobs/{id}/cancel";
    public const string JobWildcard = "/api/jackonnx/jobs/*";
    public const string JobCancelByBody = "/api/jackonnx/jobs/cancel";
    public const string JobCancelWildcard = "/api/jackonnx/jobs/cancel/*";
    public const string Artifact = "/api/jackonnx/artifacts/{id}";
    public const string ArtifactWildcard = "/api/jackonnx/artifacts/*";
    public const string JobStream = "/api/jackonnx/jobs/{id}/stream";
    public const string JobStreamWildcard = "/api/jackonnx/jobs/stream/*";

    public static IReadOnlyList<string> All { get; } =
    [
        Status,
        Devices,
        Models,
        ImageGenerate,
        AudioGenerate,
        VideoGenerate,
        VideoPresentation,
        Job,
        JobCancel,
        JobWildcard,
        JobCancelByBody,
        JobCancelWildcard,
        Artifact,
        ArtifactWildcard,
        JobStream,
        JobStreamWildcard
    ];
}

public sealed class JackOnnxSocketJackOptions
{
    public string BasePath { get; set; } = "/api/jackonnx";
}

public static class JackOnnxSocketJackRouteMapper
{
    public static void MapJackOnnxRoutes(this HttpServer server, JackOnnxRuntimeEngine runtime, JackOnnxSocketJackOptions? options = null)
    {
        if (server == null)
            throw new ArgumentNullException(nameof(server));
        if (runtime == null)
            throw new ArgumentNullException(nameof(runtime));

        options ??= new JackOnnxSocketJackOptions();
        var handlers = new JackOnnxSocketJackHandlers(runtime);
        string basePath = NormalizeBasePath(options.BasePath);

        server.Map("GET", basePath + "/status", (_, _, cancellationToken) => handlers.StatusAsync(cancellationToken).GetAwaiter().GetResult());
        server.Map("GET", basePath + "/devices", (_, _, cancellationToken) => handlers.DevicesAsync(cancellationToken).GetAwaiter().GetResult());
        server.Map("GET", basePath + "/models", (_, _, cancellationToken) => handlers.ModelsAsync(cancellationToken).GetAwaiter().GetResult());
        server.Map("GET", basePath + "/jobs", (_, _, cancellationToken) => handlers.JobsAsync(cancellationToken).GetAwaiter().GetResult());
        server.Map("GET", basePath + "/jobs/*", (_, request, cancellationToken) => handlers.JobAsync(FirstPathVariable(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("POST", basePath + "/jobs/cancel", (_, request, cancellationToken) => handlers.CancelJobAsync(ReadJobId(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("POST", basePath + "/jobs/cancel/*", (_, request, cancellationToken) => handlers.CancelJobAsync(FirstPathVariable(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("GET", basePath + "/artifacts/*", (_, request, cancellationToken) => handlers.ArtifactResponseAsync(FirstPathVariable(request), cancellationToken).GetAwaiter().GetResult());
        server.MapStream("GET", basePath + "/jobs/stream/*", (_, request, stream, cancellationToken) => handlers.StreamJobAsync(FirstPathVariable(request), stream, cancellationToken));
        server.Map("POST", basePath + "/image/generate", (_, request, cancellationToken) => handlers.GenerateImageAsync(ReadJson<ImageGenerationRequest>(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("POST", basePath + "/audio/generate", (_, request, cancellationToken) => handlers.GenerateAudioAsync(ReadJson<AudioGenerationRequest>(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("POST", basePath + "/video/generate", (_, request, cancellationToken) => handlers.GenerateVideoAsync(ReadJson<VideoGenerationRequest>(request), cancellationToken).GetAwaiter().GetResult());
        server.Map("POST", basePath + "/video/presentation", (_, request, cancellationToken) => handlers.ConvertVideoPresentationAsync(ReadJson<VideoPresentationRequest>(request), cancellationToken).GetAwaiter().GetResult());
    }

    private static string NormalizeBasePath(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return "";
        basePath = "/" + basePath.Trim().Trim('/');
        return basePath == "/" ? "" : basePath;
    }

    private static string FirstPathVariable(HttpRequest request)
    {
        return request?.PathVariables != null && request.PathVariables.Count > 0
            ? request.PathVariables[0]
            : "";
    }

    private static string ReadJobId(HttpRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Body))
            return "";

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("jobId", out var jobId) &&
                jobId.ValueKind == JsonValueKind.String)
            {
                return jobId.GetString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static T ReadJson<T>(HttpRequest request) where T : new()
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Body))
            return new T();

        try
        {
            return JsonSerializer.Deserialize<T>(request.Body, JackOnnxSocketJackHandlers.JsonOptions) ?? new T();
        }
        catch
        {
            return new T();
        }
    }
}

public sealed class JackOnnxSocketJackHandlers
{
    internal static readonly JsonSerializerOptions JsonOptions = JackOnnxModelManifest.CreateJsonOptions();

    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly JackOnnxImagePipeline _images;
    private readonly JackOnnxAudioPipeline _audio;
    private readonly JackOnnxVideoPipeline _video;

    public JackOnnxSocketJackHandlers(JackOnnxRuntimeEngine runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _images = new JackOnnxImagePipeline(_runtime);
        _audio = new JackOnnxAudioPipeline(_runtime);
        _video = new JackOnnxVideoPipeline(_runtime);
    }

    public async Task<string> StatusAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _runtime.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        var compatibility = await _runtime.CheckProviderCompatibilityAsync(cancellationToken).ConfigureAwait(false);
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);

        return Json(new
        {
            service = "JackONNX",
            status = "ready",
            provider_count = _runtime.Providers.Count,
            device_count = devices.Count,
            available_device_count = devices.Count(device => device.IsAvailable),
            providers = compatibility,
            model_count = models.Count,
            job_count = jobs.Count
        });
    }

    public async Task<string> DevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _runtime.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            devices
        });
    }

    public async Task<string> ModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            models
        });
    }

    public async Task<string> JobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        return Json(new
        {
            jobs
        });
    }

    public async Task<string> JobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonError("jobId is required.");

        var job = await _runtime.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job == null ? JsonError("Job was not found: " + jobId) : Json(new { job });
    }

    public async Task<string> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return JsonError("jobId is required.");

        var job = await _runtime.CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job == null ? JsonError("Job was not found: " + jobId) : Json(new { job });
    }

    public async Task<object> ArtifactResponseAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            return JsonError("artifactId is required.");

        var content = await _runtime.ReadArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (content == null)
            return JsonError("Artifact was not found: " + artifactId);

        return new FileResponse(
            content.Data,
            string.IsNullOrWhiteSpace(content.Artifact.MediaType) ? "application/octet-stream" : content.Artifact.MediaType,
            Path.GetFileName(content.Artifact.FilePath));
    }

    public async Task<string> ArtifactAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            return JsonError("artifactId is required.");

        var jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        var artifact = jobs.SelectMany(job => job.Artifacts).FirstOrDefault(item => string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        return artifact == null ? JsonError("Artifact was not found: " + artifactId) : Json(new { artifact });
    }

    public async Task StreamJobAsync(string jobId, ChunkedStream stream, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            stream.ContentType = "application/json";
            stream.WriteLine(JsonError("jobId is required."));
            return;
        }

        stream.ContentType = "text/event-stream";
        foreach (var progress in _runtime.GetProgressHistory(jobId))
            WriteSse(stream, "progress", progress);

        var job = await _runtime.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            WriteSse(stream, "error", new { error = "Job was not found: " + jobId });
            return;
        }

        if (job.State is JackOnnxJobState.Completed or JackOnnxJobState.Cancelled or JackOnnxJobState.Failed)
        {
            WriteSse(stream, "done", job);
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completed.TrySetCanceled(cancellationToken));
        using var subscription = _runtime.SubscribeProgress(jobId, progress =>
        {
            WriteSse(stream, "progress", progress);
            if (progress.State is JackOnnxJobState.Completed or JackOnnxJobState.Cancelled or JackOnnxJobState.Failed)
                completed.TrySetResult();
        });

        try
        {
            await completed.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _images.GenerateAsync(request ?? new ImageGenerationRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return Json(result);
    }

    public async Task<string> GenerateSpeechAsync(SpeechGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _audio.GenerateSpeechAsync(request ?? new SpeechGenerationRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return Json(result);
    }

    public async Task<string> GenerateAudioAsync(AudioGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _audio.GenerateAsync(request ?? new AudioGenerationRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return Json(result);
    }

    public async Task<string> GenerateVideoAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _video.GenerateAsync(request ?? new VideoGenerationRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return Json(result);
    }

    public async Task<string> ConvertVideoPresentationAsync(VideoPresentationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ArtifactId))
            return JsonError("artifactId is required.");

        var artifact = await _video.CreatePresentationArtifactAsync(request.ArtifactId, cancellationToken).ConfigureAwait(false);
        if (artifact == null)
            return JsonError("Could not prepare a browser-playable presentation video for artifact: " + request.ArtifactId);

        return Json(new
        {
            success = true,
            message = "Browser-playable presentation video prepared.",
            artifactId = artifact.Id,
            artifact
        });
    }

    private static string Json<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static void WriteSse<T>(ChunkedStream stream, string eventName, T payload)
    {
        stream.WriteLine("event: " + eventName);
        stream.WriteLine("data: " + Json(payload));
        stream.WriteLine("");
    }

    private static string JsonError(string message)
    {
        return Json(new
        {
            success = false,
            error = message
        });
    }
}
