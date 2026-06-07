namespace LlmRuntime;

public sealed class LlmModelInfo
{
    public string Key { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public string FilePath { get; init; } = "";

    public string FileName { get; init; } = "";

    public string Type { get; init; } = "llm";

    public string Publisher { get; init; } = "local";

    public string? Architecture { get; init; }

    public string? QuantizationName { get; init; }

    public double? BitsPerWeight { get; init; }

    public long SizeBytes { get; init; }

    public string? ParamsString { get; init; }

    public uint? MaxContextLength { get; init; }

    public string Format { get; init; } = "gguf";

    public string? LoadDisabledReason { get; init; }

    public string AdapterType { get; init; } = "";

    public bool AdapterRequiresBaseModel { get; init; }

    public string BaseModel { get; init; } = "";

    public IReadOnlyList<string> BaseModels { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<LoadedModelInstance> LoadedInstances { get; init; } = [];

    public bool IsLoaded => LoadedInstances.Count > 0;
}

public sealed class LlmModelConfig
{
    public string HuggingFaceToken { get; init; } = "";

    public string ApiKey { get; init; } = "";

    public string Notes { get; init; } = "";
}

public sealed class LoadedModelInstance
{
    public string Id { get; init; } = "";

    public string ModelKey { get; init; } = "";

    public string DeviceId { get; init; } = "";

    public string Backend { get; init; } = "";

    public IReadOnlyList<string> Modalities { get; init; } = [];

    public int QueuedJobs { get; init; }

    public int ActiveJobs { get; init; }

    public int ConcurrencyLimit { get; init; } = 1;

    public string Health { get; init; } = "healthy";

    public LlmLoadConfig Config { get; init; } = new();

    public DateTimeOffset LoadedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool PromptPipelineReady { get; init; }

    public string PromptPipelineStatus { get; init; } = "cold";

    public string PromptPipelineDetail { get; init; } = "";

    public DateTimeOffset? PromptPipelineReadyAtUtc { get; init; }

    public double PromptPipelineWarmupSeconds { get; init; }
}

public sealed class LlmLoadConfig
{
    public string ModelKey { get; init; } = "";

    public string InstanceId { get; init; } = "";

    public string DeviceId { get; init; } = "";

    public int ConcurrencyLimit { get; init; } = 1;

    public LlmBackendKind Backend { get; init; } = LlmBackendKind.Auto;

    public bool AllowBackendFallback { get; init; } = true;

    public uint ContextLength { get; init; }

    public int EvalBatchSize { get; init; }

    public bool FlashAttention { get; init; }

    public bool OffloadKvCacheToGpu { get; init; }

    public int GpuLayerCount { get; init; }

    public LlmParallelismMode ParallelismMode { get; init; } = LlmParallelismMode.Single;

    public LlmParallelismPlacement ParallelismPlacement { get; init; } = LlmParallelismPlacement.Local;

    public IReadOnlyList<string> TargetDeviceIds { get; init; } = [];

    public IReadOnlyList<string> NetworkNodeIds { get; init; } = [];

    public int MaxGpuLoadPercent { get; init; } = 100;

    public int MaxVramUsagePercent { get; init; } = 100;

    public int PipelineStageCount { get; init; } = 1;

    public int DataParallelReplicaCount { get; init; } = 1;

    public bool ParallelTensor { get; init; }

    public int TensorParallelSize { get; init; } = 1;

    public string VideoDeviceMap { get; init; } = "";

    public bool VideoAllowCpuOffload { get; init; }

    public string VideoOffloadFolder { get; init; } = "";

    public bool VideoDisableCudaMemoryGuard { get; init; }

    public int VideoCudaMemoryReserveMb { get; init; } = 1024;

    public string VideoCpuMaxMemory { get; init; } = "";

    public bool VideoMemorySaving { get; init; }
}

public enum LlmParallelismMode
{
    Single,
    DataParallel,
    ModelOffload,
    PipelineParallel,
    TensorParallel
}

public enum LlmParallelismPlacement
{
    Local,
    Network,
    Hybrid
}

public sealed class LlmLoadResult
{
    public string Type { get; init; } = "llm";

    public string ChatService { get; init; } = "";

    public string InstanceId { get; init; } = "";

    public double LoadTimeSeconds { get; init; }

    public string Status { get; init; } = "loaded";

    public LlmLoadConfig? LoadConfig { get; init; }
}

public sealed class LlmModelLoadProgress
{
    public string Model { get; init; } = "";

    public string InstanceId { get; init; } = "";

    public int Percent { get; init; }

    public string Status { get; init; } = "";

    public string Message { get; init; } = "";

    public bool IsStartupRestore { get; init; }
}

public sealed class ModelDownloadJob
{
    public string JobId { get; init; } = "";

    public string Status { get; set; } = "downloading";

    public string Model { get; init; } = "";

    public string? FilePath { get; set; }

    public string? Error { get; set; }

    public long TotalSizeBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public string? Sha256 { get; set; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
