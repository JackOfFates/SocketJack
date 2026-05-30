using System.Text.Json;
using System.Text.Json.Serialization;

namespace JackONNX;

public enum JackOnnxExecutionProvider
{
    Auto,
    Cpu,
    DirectML,
    Cuda
}

public enum JackOnnxDevicePolicy
{
    PreferGpuThenCpu,
    RequirePreferredProvider,
    CpuOnly
}

public enum JackOnnxMediaKind
{
    Image,
    Audio,
    Video,
    Model,
    Log
}

public enum JackOnnxJobState
{
    Queued,
    LoadingModel,
    Running,
    StreamingPreview,
    Encoding,
    Completed,
    Cancelled,
    Failed
}

public sealed class JackOnnxOptions
{
    public JackOnnxExecutionProvider PreferredProvider { get; set; } = JackOnnxExecutionProvider.Auto;

    public JackOnnxDevicePolicy DevicePolicy { get; set; } = JackOnnxDevicePolicy.PreferGpuThenCpu;

    public string ModelCachePath { get; set; } = Path.Combine(Environment.CurrentDirectory, "Models", "JackONNX");

    public string ArtifactRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, "Artifacts", "JackONNX");

    public bool EnableMemoryPattern { get; set; } = true;

    public bool EnableIoBinding { get; set; } = true;

    public TimeSpan ProviderProbeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ImageGenerationTimeout { get; set; } = TimeSpan.FromHours(1);

    public string ImageGenerationPythonExecutable { get; set; } = Path.Combine(Environment.CurrentDirectory, "Python", "python.exe");

    public bool AllowPythonBootstrap { get; set; }

    public bool AllowPythonPackageInstall { get; set; }

    public List<string> ModelManifestPaths { get; set; } = new();
}

public sealed class JackOnnxDeviceInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public JackOnnxExecutionProvider Provider { get; set; }

    public bool IsAvailable { get; set; }

    public long? DedicatedMemoryBytes { get; set; }

    public string Detail { get; set; } = "";
}

public sealed class JackOnnxModelManifest
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public string Format { get; set; } = "onnx";

    public string Precision { get; set; } = "";

    public Dictionary<string, string> Components { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<JackOnnxExecutionProvider> RecommendedProviders { get; set; } = new();

    public int? DefaultWidth { get; set; }

    public int? DefaultHeight { get; set; }

    public long? RequiredMemoryBytes { get; set; }

    public string ManifestPath { get; set; } = "";

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<JackOnnxModelManifest> LoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<JackOnnxModelManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
            throw new InvalidOperationException("Model manifest could not be read: " + manifestPath);

        manifest.ManifestPath = Path.GetFullPath(manifestPath);
        return manifest;
    }

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class JackOnnxArtifact
{
    public string Id { get; set; } = "artifact_" + Guid.NewGuid().ToString("N");

    public JackOnnxMediaKind Kind { get; set; }

    public string MediaType { get; set; } = "";

    public string FilePath { get; set; } = "";

    public long LengthBytes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class JackOnnxArtifactContent
{
    public JackOnnxArtifact Artifact { get; set; } = new();

    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class JackOnnxProgress
{
    public string JobId { get; set; } = "";

    public JackOnnxJobState State { get; set; } = JackOnnxJobState.Queued;

    public double Percent { get; set; }

    public int? Step { get; set; }

    public int? TotalSteps { get; set; }

    public string Message { get; set; } = "";

    public JackOnnxArtifact? PreviewArtifact { get; set; }
}

public sealed class JackOnnxProviderCompatibility
{
    public string ProviderId { get; set; } = "";

    public JackOnnxExecutionProvider Provider { get; set; }

    public bool IsAvailable { get; set; }

    public bool CanCreateSessionOptions { get; set; }

    public string Detail { get; set; } = "";
}

public sealed class JackOnnxJobSnapshot
{
    public string Id { get; set; } = "job_" + Guid.NewGuid().ToString("N");

    public JackOnnxJobState State { get; set; } = JackOnnxJobState.Queued;

    public JackOnnxMediaKind Kind { get; set; }

    public string ModelId { get; set; } = "";

    public JackOnnxExecutionProvider Provider { get; set; } = JackOnnxExecutionProvider.Auto;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public double Percent { get; set; }

    public string Message { get; set; } = "";

    public List<JackOnnxArtifact> Artifacts { get; set; } = new();

    public string? LlmRuntimeSessionId { get; set; }

    public string? LlmRuntimeToolCallId { get; set; }

    public string? LlmRuntimeParentRunId { get; set; }
}

public sealed class JackOnnxGenerationContext
{
    public string? LlmRuntimeSessionId { get; set; }

    public string? LlmRuntimeToolCallId { get; set; }

    public string? LlmRuntimeParentRunId { get; set; }

    public string OwnerKey { get; set; } = "";

    public string DeviceId { get; set; } = "";

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public abstract class JackOnnxGenerationRequest
{
    public string ModelId { get; set; } = "";

    public string Prompt { get; set; } = "";

    public string NegativePrompt { get; set; } = "";

    public int? Seed { get; set; }

    public string SourcePath { get; set; } = "";

    public string SourceDataUrl { get; set; } = "";

    public string SourceMediaType { get; set; } = "";

    public string SourceName { get; set; } = "";

    public string SourceKind { get; set; } = "";

    public string GenerationMode { get; set; } = "";

    public JackOnnxGenerationContext Context { get; set; } = new();
}

public sealed class ImageGenerationRequest : JackOnnxGenerationRequest
{
    public int Width { get; set; } = 512;

    public int Height { get; set; } = 512;

    public int Steps { get; set; } = 30;

    public double GuidanceScale { get; set; } = 7.5;
}

public sealed class SpeechGenerationRequest : JackOnnxGenerationRequest
{
    public string Text { get; set; } = "";

    public string Voice { get; set; } = "default";

    public double Speed { get; set; } = 1.0;

    public int SampleRate { get; set; } = 24000;
}

public sealed class AudioGenerationRequest : JackOnnxGenerationRequest
{
    public double Seconds { get; set; } = 5;

    public int SampleRate { get; set; } = 44100;
}

public sealed class VideoGenerationRequest : JackOnnxGenerationRequest
{
    public int Width { get; set; } = 320;

    public int Height { get; set; } = 192;

    public double Seconds { get; set; } = 7;

    public int Fps { get; set; } = 4;

    public int? Frames { get; set; }

    public int Steps { get; set; } = 16;

    public double GuidanceScale { get; set; } = 7.5;
}

public sealed class JackOnnxGenerationResult
{
    public string JobId { get; set; } = "";

    public bool Success { get; set; }

    public string Message { get; set; } = "";

    public List<JackOnnxArtifact> Artifacts { get; set; } = new();
}

public interface IJackOnnxExecutionProvider
{
    string Id { get; }

    JackOnnxExecutionProvider Kind { get; }

    int Priority { get; }

    Task<IReadOnlyList<JackOnnxDeviceInfo>> ProbeDevicesAsync(CancellationToken cancellationToken = default);
}

public interface IJackOnnxSessionOptionsFactory
{
    JackOnnxProviderCompatibility CheckCompatibility(JackOnnxOptions? options = null);
}

public interface IJackOnnxRuntimeCatalog
{
    Task<IReadOnlyList<JackOnnxDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JackOnnxModelManifest>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JackOnnxJobSnapshot>> ListJobsAsync(CancellationToken cancellationToken = default);
}
