using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LlmRuntime;

public sealed class LlmModelRegistry : IDisposable
{
    private static readonly JsonSerializerOptions RuntimeConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly TimeSpan ModelCacheRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentlyWrittenModelGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GpuUsageCacheInterval = TimeSpan.FromSeconds(2);
    private const double BusyGpuUsagePercent = 75d;
    private static readonly string[] LmStudioHomeEnvironmentVariables =
    [
        "LMSTUDIO_HOME",
        "LM_STUDIO_HOME"
    ];

    private static readonly string[] LmStudioModelRootEnvironmentVariables =
    [
        "LMSTUDIO_MODELS_DIR",
        "LM_STUDIO_MODELS_DIR",
        "LMSTUDIO_MODEL_PATH",
        "LM_STUDIO_MODEL_PATH"
    ];

    private static readonly string[] LmStudioModelRootSettingsKeys =
    [
        "downloadsFolder",
        "modelsFolder",
        "modelFolder",
        "modelsDirectory",
        "modelDirectory"
    ];

    private static readonly string[] PartialModelPathMarkers =
    [
        ".partial",
        ".download",
        ".downloading",
        ".tmp",
        ".crdownload",
        ".part",
        ".incomplete"
    ];

    private static readonly EnumerationOptions ModelFileEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    private static readonly EnumerationOptions DirectorySizeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    private static readonly string[] ModelFileSearchPatterns =
    [
        "*.gguf",
        "*.jackonnx.json",
        "manifest.json",
        "model.safetensors.index.json",
        "config.json"
    ];

    private readonly LlmRuntimeOptions _options;
    private readonly ILlmBackendFactory _backendFactory;
    private readonly ConcurrentDictionary<string, ILlmBackend> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LlmLoadConfig> _loadedRuntimeModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelDownloadJob> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelDownloadService> _downloadServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly LlmRuntimeScheduler _scheduler = new();
    private readonly object _loadLock = new();
    private readonly object _runtimeConfigLock = new();
    private readonly object _modelCacheLock = new();
    private readonly object _gpuUsageLock = new();
    private readonly Dictionary<string, FileSystemWatcher> _modelWatchers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LlmModelInfo> _modelCache = [];
    private DateTimeOffset _modelCacheRefreshedAtUtc = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<string, GpuDeviceUsage> _gpuUsageByDevice = new Dictionary<string, GpuDeviceUsage>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _gpuUsageRefreshedAtUtc = DateTimeOffset.MinValue;
    private bool _modelCacheDirty = true;
    private bool _disposed;

    public LlmModelRegistry(LlmRuntimeOptions options, ILlmBackendFactory? backendFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _backendFactory = backendFactory ?? new LlmBackendFactory(_options);
        Directory.CreateDirectory(_options.ModelRoot);
        if (!string.IsNullOrWhiteSpace(_options.CompleteModelRoot))
            Directory.CreateDirectory(_options.CompleteModelRoot);
        EnsureModelWatchers();
        RefreshModelCache();
    }

    public event Action<LlmModelLoadProgress>? ModelLoadProgressChanged;

    public IReadOnlyList<LlmModelInfo> ListModels()
    {
        Directory.CreateDirectory(_options.ModelRoot);
        if (!string.IsNullOrWhiteSpace(_options.CompleteModelRoot))
            Directory.CreateDirectory(_options.CompleteModelRoot);

        EnsureModelWatchers();
        lock (_modelCacheLock)
        {
            if (!_modelCacheDirty && DateTimeOffset.UtcNow - _modelCacheRefreshedAtUtc < ModelCacheRefreshInterval)
                return _modelCache;
        }

        return RefreshModelCache();
    }

    public LlmModelInfo? FindModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        string requested = NormalizeModelIdentifier(model);
        return ListModels().FirstOrDefault(info => ModelMatches(info, requested));
    }

    public LlmLoadResult Load(string model, uint? contextLength = null, int? gpuLayerCount = null, int? evalBatchSize = null, bool? flashAttention = null, bool? offloadKvCacheToGpu = null, LlmBackendKind? backend = null, bool? allowBackendFallback = null, bool echoLoadConfig = false, string? deviceId = null, string? instanceId = null, int? concurrencyLimit = null, LlmParallelismMode? parallelismMode = null, LlmParallelismPlacement? parallelismPlacement = null, IReadOnlyList<string>? targetDeviceIds = null, IReadOnlyList<string>? networkNodeIds = null, int? maxGpuLoadPercent = null, int? maxVramUsagePercent = null, int? pipelineStageCount = null, int? dataParallelReplicaCount = null, string? videoDeviceMap = null, bool? videoAllowCpuOffload = null, string? videoOffloadFolder = null, bool? videoDisableCudaMemoryGuard = null, int? videoCudaMemoryReserveMb = null, string? videoCpuMaxMemory = null, bool? videoMemorySaving = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return LoadInternal(
                model,
                contextLength,
                gpuLayerCount,
                evalBatchSize,
                flashAttention,
                offloadKvCacheToGpu,
                backend,
                allowBackendFallback,
                echoLoadConfig,
                deviceId,
                instanceId,
                concurrencyLimit,
                parallelismMode,
                parallelismPlacement,
                targetDeviceIds,
                networkNodeIds,
                maxGpuLoadPercent,
                maxVramUsagePercent,
                pipelineStageCount,
                dataParallelReplicaCount,
                videoDeviceMap,
                videoAllowCpuOffload,
                videoOffloadFolder,
                videoDisableCudaMemoryGuard,
                videoCudaMemoryReserveMb,
                videoCpuMaxMemory,
                videoMemorySaving,
                persistLoadedState: true,
                emitProgress: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportModelLoadProgress(model, "", 100, "failed", "Model load failed: " + model + " (" + ex.Message + ").", false);
            throw;
        }
    }

    private LlmLoadResult LoadInternal(string model, uint? contextLength = null, int? gpuLayerCount = null, int? evalBatchSize = null, bool? flashAttention = null, bool? offloadKvCacheToGpu = null, LlmBackendKind? backend = null, bool? allowBackendFallback = null, bool echoLoadConfig = false, string? deviceId = null, string? instanceId = null, int? concurrencyLimit = null, LlmParallelismMode? parallelismMode = null, LlmParallelismPlacement? parallelismPlacement = null, IReadOnlyList<string>? targetDeviceIds = null, IReadOnlyList<string>? networkNodeIds = null, int? maxGpuLoadPercent = null, int? maxVramUsagePercent = null, int? pipelineStageCount = null, int? dataParallelReplicaCount = null, string? videoDeviceMap = null, bool? videoAllowCpuOffload = null, string? videoOffloadFolder = null, bool? videoDisableCudaMemoryGuard = null, int? videoCudaMemoryReserveMb = null, string? videoCpuMaxMemory = null, bool? videoMemorySaving = null, bool persistLoadedState = true, bool emitProgress = true, CancellationToken cancellationToken = default)
    {
        var info = FindModel(model);
        if (info == null)
            throw new FileNotFoundException($"Model not found: {model}");

        string resolvedDeviceId = NormalizeDeviceId(deviceId);
        IReadOnlyList<string> resolvedTargetDeviceIds = NormalizeDeviceIds(targetDeviceIds, resolvedDeviceId);
        IReadOnlyList<string> resolvedNetworkNodeIds = NormalizeStringList(networkNodeIds);
        LlmParallelismMode resolvedParallelismMode = ResolveParallelismMode(parallelismMode, resolvedTargetDeviceIds, resolvedNetworkNodeIds);
        ValidateTensorParallelTargets(resolvedParallelismMode, resolvedTargetDeviceIds);
        LlmParallelismPlacement resolvedParallelismPlacement = ResolveParallelismPlacement(parallelismPlacement, resolvedNetworkNodeIds);
        string resolvedInstanceId = ResolveLoadInstanceId(info.Key, resolvedDeviceId, instanceId);
        int resolvedConcurrencyLimit = Math.Max(1, concurrencyLimit ?? 1);
        var requestedBackend = backend ?? _options.DefaultBackend;
        var effectiveBackend = IsVllmChatLoadableModel(info)
            ? LlmBackendKind.Vllm
            : IsChatLoadableModel(info)
            ? LlmBackendAutoSelector.Resolve(requestedBackend, _options.RequireGpuForAutoBackend)
            : requestedBackend;
        bool effectiveBackendFallback = ResolveAllowBackendFallback(effectiveBackend, allowBackendFallback, _options.AllowBackendFallback, _options.PreventCpuBackendFallback);
        var config = new LlmLoadConfig
        {
            ModelKey = info.Key,
            InstanceId = resolvedInstanceId,
            DeviceId = resolvedDeviceId,
            ConcurrencyLimit = resolvedConcurrencyLimit,
            Backend = effectiveBackend,
            AllowBackendFallback = effectiveBackendFallback,
            ContextLength = contextLength ?? ResolveDefaultContextLength(info, _options.DefaultContextLength),
            EvalBatchSize = evalBatchSize ?? _options.DefaultEvalBatchSize,
            FlashAttention = flashAttention ?? _options.DefaultFlashAttention,
            OffloadKvCacheToGpu = ResolveOffloadKvCacheToGpu(effectiveBackend, offloadKvCacheToGpu, _options.DefaultOffloadKvCacheToGpu),
            GpuLayerCount = ResolveGpuLayerCount(effectiveBackend, gpuLayerCount, _options.DefaultGpuLayerCount),
            ParallelismMode = resolvedParallelismMode,
            ParallelismPlacement = resolvedParallelismPlacement,
            TargetDeviceIds = resolvedTargetDeviceIds,
            NetworkNodeIds = resolvedNetworkNodeIds,
            MaxGpuLoadPercent = ClampPercent(maxGpuLoadPercent ?? 100),
            MaxVramUsagePercent = ClampPercent(maxVramUsagePercent ?? 100),
            PipelineStageCount = Math.Max(1, pipelineStageCount ?? (resolvedParallelismMode == LlmParallelismMode.PipelineParallel ? Math.Max(1, resolvedTargetDeviceIds.Count) : 1)),
            DataParallelReplicaCount = Math.Max(1, dataParallelReplicaCount ?? (resolvedParallelismMode == LlmParallelismMode.DataParallel ? Math.Max(1, resolvedTargetDeviceIds.Count) : 1)),
            ParallelTensor = resolvedParallelismMode == LlmParallelismMode.TensorParallel,
            TensorParallelSize = ResolveTensorParallelSize(resolvedParallelismMode, resolvedTargetDeviceIds),
            VideoDeviceMap = ResolveDefaultMediaDeviceMap(info, videoDeviceMap),
            VideoAllowCpuOffload = ResolveDefaultMediaCpuOffload(info, videoAllowCpuOffload),
            VideoOffloadFolder = (videoOffloadFolder ?? "").Trim(),
            VideoDisableCudaMemoryGuard = videoDisableCudaMemoryGuard ?? false,
            VideoCudaMemoryReserveMb = Math.Max(0, videoCudaMemoryReserveMb ?? 1024),
            VideoCpuMaxMemory = (videoCpuMaxMemory ?? "").Trim(),
            VideoMemorySaving = ResolveDefaultMediaMemorySaving(info, videoMemorySaving)
        };
        config = NormalizeLinuxCudaLoadConfig(info, config, emitProgress);

        if (!string.IsNullOrWhiteSpace(info.LoadDisabledReason))
            throw new LlmRuntimeException(info.LoadDisabledReason, "unsupported_model_metadata", "unsupported_model_metadata");

        if (emitProgress)
            ReportModelLoadProgress(model, resolvedInstanceId, 0, "loading", "Loading model " + info.DisplayName + ".", false);

        if (!IsChatLoadableModel(info))
        {
            if (IsRuntimeLoadableModel(info))
            {
                _loadedRuntimeModels[resolvedInstanceId] = config;
                MarkModelCacheDirty();
                if (persistLoadedState)
                    SavePersistedLoadedModel(info.Key, resolvedInstanceId, config);
                if (emitProgress)
                    ReportModelLoadProgress(model, resolvedInstanceId, 100, "loaded", "Loaded model " + info.DisplayName + ".", false);
                return new LlmLoadResult
                {
                    InstanceId = resolvedInstanceId,
                    Type = info.Type,
                    ChatService = GetRuntimeServiceForModel(info),
                    LoadTimeSeconds = 0,
                    Status = "loaded",
                    LoadConfig = echoLoadConfig ? config : null
                };
            }

            if (!string.Equals(info.Format, "gguf", StringComparison.OrdinalIgnoreCase))
                throw new LlmRuntimeException($"Model format '{info.Format}' is listed but cannot be loaded by the GGUF chat backend yet: {model}", "unsupported_model_format", "unsupported_model_format");

            throw new LlmRuntimeException(
                GetRuntimeLoadDisabledReason(info) ??
                $"Model '{info.DisplayName}' is a {info.Type} model and cannot be loaded by the GGUF chat backend. Choose a text chat/instruct GGUF model for LlmRuntime chat, or route media models through the media runtime.",
                "unsupported_model_type",
                "unsupported_model_type");
        }

        lock (_loadLock)
        {
            if (_loaded.TryGetValue(resolvedInstanceId, out var existingBackend))
            {
                bool ready = WarmPromptPipelineAfterLoad(info, existingBackend, model, resolvedInstanceId, emitProgress, cancellationToken);
                MarkModelCacheDirty();
                if (persistLoadedState)
                    SavePersistedLoadedModel(info.Key, resolvedInstanceId, existingBackend.LoadConfig);
                if (emitProgress)
                    ReportModelLoadProgress(
                        model,
                        resolvedInstanceId,
                        100,
                        ready ? "ready" : "loaded",
                        ready
                            ? "Model " + info.DisplayName + " is already loaded and prompt-ready."
                            : "Model " + info.DisplayName + " is already loaded, but prompt pipeline warmup is not ready.",
                        false);
                return new LlmLoadResult
                {
                    InstanceId = resolvedInstanceId,
                    Type = info.Type == "embedding" ? "embedding" : "llm",
                    ChatService = GetRuntimeServiceForModel(info),
                    LoadTimeSeconds = 0,
                    Status = "loaded",
                    LoadConfig = echoLoadConfig ? existingBackend.LoadConfig : null
                };
            }

            var loadedBackend = _backendFactory.Create(resolvedInstanceId, info.FilePath, config);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                loadedBackend.LoadAsync(cancellationToken).GetAwaiter().GetResult();
                _loaded[resolvedInstanceId] = loadedBackend;
                MarkModelCacheDirty();
                if (persistLoadedState)
                    SavePersistedLoadedModel(info.Key, resolvedInstanceId, loadedBackend.LoadConfig);
                WarmPromptPipelineAfterLoad(info, loadedBackend, model, resolvedInstanceId, emitProgress, cancellationToken);
                MarkModelCacheDirty();
                stopwatch.Stop();
            }
            catch
            {
                _loaded.TryRemove(resolvedInstanceId, out _);
                loadedBackend.Dispose();
                throw;
            }

            return new LlmLoadResult
            {
                InstanceId = resolvedInstanceId,
                Type = info.Type == "embedding" ? "embedding" : "llm",
                ChatService = GetRuntimeServiceForModel(info),
                LoadTimeSeconds = stopwatch.Elapsed.TotalSeconds,
                Status = "loaded",
                LoadConfig = echoLoadConfig ? config : null
            };
        }
    }

    public async Task RestorePersistedLoadedModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.RestoreLoadedModelsOnStartup || !IsLoadedModelPersistenceEnabled())
            return;

        IReadOnlyList<PersistedLoadedModel> savedModels = ReadPersistedLoadedModels();
        if (savedModels.Count == 0)
            return;

        int loaded = 0;
        int total = savedModels.Count;
        ReportModelLoadProgress("", "", 0, "loading", "Restoring " + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + " saved loaded model(s).", true);

        for (int index = 0; index < savedModels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
                break;

            PersistedLoadedModel savedModel = savedModels[index];
            int startPercent = Percent(index, total);
            int endPercent = Percent(index + 1, total);
            string ordinal = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);

            ReportModelLoadProgress(
                savedModel.Model,
                "",
                startPercent,
                "loading",
                "Loading saved model " + ordinal + ": " + savedModel.Model + ".",
                true);

            try
            {
                LlmLoadResult result = await Task.Run(() => LoadInternal(
                    savedModel.Model,
                    savedModel.Config.ContextLength,
                    savedModel.Config.GpuLayerCount,
                    savedModel.Config.EvalBatchSize,
                    savedModel.Config.FlashAttention,
                    savedModel.Config.OffloadKvCacheToGpu,
                    savedModel.Config.Backend,
                    savedModel.Config.AllowBackendFallback,
                    echoLoadConfig: false,
                    deviceId: savedModel.Config.DeviceId,
                    instanceId: string.IsNullOrWhiteSpace(savedModel.Config.InstanceId) ? savedModel.InstanceId : savedModel.Config.InstanceId,
                    concurrencyLimit: savedModel.Config.ConcurrencyLimit,
                    parallelismMode: savedModel.Config.ParallelismMode,
                    parallelismPlacement: savedModel.Config.ParallelismPlacement,
                    targetDeviceIds: savedModel.Config.TargetDeviceIds,
                    networkNodeIds: savedModel.Config.NetworkNodeIds,
                    maxGpuLoadPercent: savedModel.Config.MaxGpuLoadPercent,
                    maxVramUsagePercent: savedModel.Config.MaxVramUsagePercent,
                    pipelineStageCount: savedModel.Config.PipelineStageCount,
                    dataParallelReplicaCount: savedModel.Config.DataParallelReplicaCount,
                    videoDeviceMap: savedModel.Config.VideoDeviceMap,
                    videoAllowCpuOffload: savedModel.Config.VideoAllowCpuOffload,
                    videoOffloadFolder: savedModel.Config.VideoOffloadFolder,
                    videoDisableCudaMemoryGuard: savedModel.Config.VideoDisableCudaMemoryGuard,
                    videoCudaMemoryReserveMb: savedModel.Config.VideoCudaMemoryReserveMb,
                    videoCpuMaxMemory: savedModel.Config.VideoCpuMaxMemory,
                    videoMemorySaving: savedModel.Config.VideoMemorySaving,
                    persistLoadedState: false,
                    emitProgress: false,
                    cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

                loaded++;
                ReportModelLoadProgress(
                    savedModel.Model,
                    result.InstanceId,
                    endPercent,
                    "loaded",
                    "Loaded saved model " + ordinal + ": " + result.InstanceId + ".",
                    true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportModelLoadProgress(
                    savedModel.Model,
                    "",
                    endPercent,
                    "failed",
                    "Saved model " + ordinal + " failed: " + savedModel.Model + " (" + ex.Message + ").",
                    true);
            }
        }

        ReportModelLoadProgress(
            "",
            "",
            100,
            "complete",
            "Saved model restore complete: " + loaded.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + " loaded.",
            true);
    }

    public bool Unload(string model)
    {
        string? modelKey = FindModel(model)?.Key;
        var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_loaded.ContainsKey(model) || _loadedRuntimeModels.ContainsKey(model))
            instanceIds.Add(model);
        if (!string.IsNullOrWhiteSpace(modelKey))
        {
            foreach (var pair in _loaded)
            {
                if (LoadedInstanceBelongsToModel(pair.Key, pair.Value.LoadConfig, modelKey))
                    instanceIds.Add(pair.Key);
            }

            foreach (var pair in _loadedRuntimeModels)
            {
                if (LoadedInstanceBelongsToModel(pair.Key, pair.Value, modelKey))
                    instanceIds.Add(pair.Key);
            }
        }

        if (instanceIds.Count == 0)
            return false;

        bool removed = false;
        foreach (string instanceId in instanceIds)
        {
            if (_loaded.TryRemove(instanceId, out var backend))
            {
                backend.Dispose();
                removed = true;
            }
            if (_loadedRuntimeModels.TryRemove(instanceId, out _))
                removed = true;

            if (removed)
                RemovePersistedLoadedModel(instanceId);
        }

        if (removed)
            MarkModelCacheDirty();
        return removed;
    }

    public async Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        var backend = ResolveLoadedBackend(request, out string model, autoLoad: true, cancellationToken: cancellationToken);
        request.Model = model;
        await EnsurePromptPipelineReadyForInferenceAsync(model, backend, cancellationToken).ConfigureAwait(false);
        using var job = _scheduler.BeginJob(model);
        return await backend.CompleteChatAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var backend = ResolveLoadedBackend(request, out string model, autoLoad: true, cancellationToken: cancellationToken);
        request.Model = model;
        await EnsurePromptPipelineReadyForInferenceAsync(model, backend, cancellationToken).ConfigureAwait(false);
        using var job = _scheduler.BeginJob(model);
        await foreach (var token in backend.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            yield return token;
    }

    public IReadOnlyList<LlmSchedulerInstanceStatus> GetSchedulerStatus() => _scheduler.BuildStatus(ListModels());

    public bool TryResolveLoadedModelKey(string? model, out string resolvedModel)
    {
        try
        {
            var request = new LlmChatRequest { Model = model ?? "" };
            ResolveLoadedBackend(request, out resolvedModel, autoLoad: false);
            return true;
        }
        catch (LlmRuntimeException)
        {
            resolvedModel = "";
            return false;
        }
    }

    public bool TryEnsureModelLoadedForInference(string? model, out string resolvedModel, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new LlmChatRequest { Model = model ?? "" };
            ILlmBackend backend = ResolveLoadedBackend(request, out resolvedModel, autoLoad: true, cancellationToken: cancellationToken);
            EnsurePromptPipelineReadyForInferenceAsync(resolvedModel, backend, cancellationToken).GetAwaiter().GetResult();
            return true;
        }
        catch (LlmRuntimeException)
        {
            resolvedModel = "";
            return false;
        }
    }

    private bool WarmPromptPipelineAfterLoad(LlmModelInfo info, ILlmBackend backend, string requestedModel, string instanceId, bool emitProgress, CancellationToken cancellationToken)
    {
        if (backend.IsPromptPipelineReady)
        {
            if (emitProgress)
            {
                ReportModelLoadProgress(
                    requestedModel,
                    instanceId,
                    100,
                    "ready",
                    "Loaded model " + info.DisplayName + " and prompt pipeline is ready.",
                    false);
            }

            return true;
        }

        if (emitProgress)
            ReportModelLoadProgress(requestedModel, instanceId, 92, "warming_prompt_pipeline", "Warming prompt pipeline for " + info.DisplayName + ".", false);

        try
        {
            backend.WarmPromptPipelineAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (emitProgress)
            {
                ReportModelLoadProgress(
                    requestedModel,
                    instanceId,
                    100,
                    "loaded",
                    "Loaded model " + info.DisplayName + ", but prompt pipeline warmup failed: " + TrimStatusMessage(ex.Message) + ".",
                    false);
            }
            return false;
        }

        bool ready = backend.IsPromptPipelineReady;
        if (emitProgress)
        {
            ReportModelLoadProgress(
                requestedModel,
                instanceId,
                100,
                ready ? "ready" : "loaded",
                ready
                    ? "Loaded model " + info.DisplayName + " and warmed the prompt pipeline."
                    : "Loaded model " + info.DisplayName + ", but prompt pipeline status is " + backend.PromptPipelineStatus + ".",
                false);
        }
        return ready;
    }

    private async Task EnsurePromptPipelineReadyForInferenceAsync(string model, ILlmBackend backend, CancellationToken cancellationToken)
    {
        if (backend.IsPromptPipelineReady)
            return;

        try
        {
            await backend.WarmPromptPipelineAsync(cancellationToken).ConfigureAwait(false);
            MarkModelCacheDirty();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Prompt pipeline warmup failed for " + model + ": " + ex.Message);
            MarkModelCacheDirty();
        }
    }

    private ILlmBackend ResolveLoadedBackend(LlmChatRequest request, out string model, bool autoLoad = true, CancellationToken cancellationToken = default)
    {
        string requestedModel = request.Model?.Trim() ?? "";
        bool requestedDefaultPlaceholder = IsDefaultModelPlaceholder(requestedModel);
        if (requestedDefaultPlaceholder)
            requestedModel = "";

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            if (_loaded.TryGetValue(requestedModel, out var exactBackend))
            {
                model = requestedModel;
                return exactBackend;
            }

            string? modelKey = FindModel(requestedModel)?.Key;
            if (modelKey != null && TrySelectLoadedBackend(modelKey, out string selectedInstanceId, out var requestedBackend))
            {
                model = selectedInstanceId;
                return requestedBackend;
            }

            if (modelKey != null && autoLoad)
            {
                Load(requestedModel, cancellationToken: cancellationToken);
                if (TrySelectLoadedBackend(modelKey, out selectedInstanceId, out requestedBackend))
                {
                    model = selectedInstanceId;
                    return requestedBackend;
                }
            }

            throw new LlmRuntimeException($"Model is not loaded: {requestedModel}", "model_not_loaded", "model_not_loaded");
        }

        if (_loaded.Count == 1)
        {
            var pair = _loaded.Single();
            model = pair.Key;
            return pair.Value;
        }

        string[] loadedModelKeys = _loaded
            .Select(pair => ResolveLoadedModelKey(pair.Key, pair.Value.LoadConfig))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (loadedModelKeys.Length == 1 && TrySelectLoadedBackend(loadedModelKeys[0], out string defaultInstanceId, out var defaultBackend))
        {
            model = defaultInstanceId;
            return defaultBackend;
        }

        if (requestedDefaultPlaceholder)
            throw new LlmRuntimeException("No loaded model is selected for inference. Select and load a specific LlmRuntime model instead of using the lm-studio placeholder.", "model_not_loaded", "model_not_loaded");

        throw new LlmRuntimeException("No model is loaded for inference. Use POST /api/v1/models/load first.", "model_not_loaded", "model_not_loaded");
    }

    private bool TrySelectLoadedBackend(string modelKey, out string instanceId, out ILlmBackend backend)
    {
        IReadOnlyDictionary<string, GpuDeviceUsage> gpuUsageByDevice = GetGpuUsageByDevice();
        var candidates = _loaded
            .Where(pair => LoadedInstanceBelongsToModel(pair.Key, pair.Value.LoadConfig, modelKey))
            .Select(pair =>
            {
                string deviceId = FirstNonEmpty(pair.Value.LoadConfig.DeviceId, pair.Value.LoadConfig.TargetDeviceIds.FirstOrDefault());
                GpuDeviceUsage usage = GetGpuDeviceUsage(gpuUsageByDevice, deviceId);
                return new
                {
                    InstanceId = pair.Key,
                    Backend = pair.Value,
                    DeviceId = deviceId,
                    GpuUsagePercent = usage.GpuUsagePercent,
                    VramUsagePercent = usage.VramUsagePercent,
                    MaxVramUsagePercent = pair.Value.LoadConfig.MaxVramUsagePercent,
                    ActiveJobs = _scheduler.GetActiveJobs(pair.Key),
                    QueuedJobs = _scheduler.GetQueuedJobs(pair.Key, pair.Value.LoadConfig.ConcurrencyLimit),
                    ConcurrencyLimit = Math.Max(1, pair.Value.LoadConfig.ConcurrencyLimit)
                };
            })
            .OrderBy(candidate => candidate.ActiveJobs >= candidate.ConcurrencyLimit)
            .ThenBy(candidate => candidate.VramUsagePercent >= candidate.MaxVramUsagePercent)
            .ThenBy(candidate => candidate.VramUsagePercent)
            .ThenBy(candidate => candidate.GpuUsagePercent >= BusyGpuUsagePercent)
            .ThenBy(candidate => candidate.GpuUsagePercent)
            .ThenBy(candidate => candidate.QueuedJobs)
            .ThenBy(candidate => candidate.ActiveJobs)
            .ThenBy(candidate => candidate.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        if (selected == null)
        {
            instanceId = "";
            backend = null!;
            return false;
        }

        instanceId = selected.InstanceId;
        backend = selected.Backend;
        return true;
    }

    private IReadOnlyDictionary<string, GpuDeviceUsage> GetGpuUsageByDevice()
    {
        lock (_gpuUsageLock)
        {
            if (DateTimeOffset.UtcNow - _gpuUsageRefreshedAtUtc < GpuUsageCacheInterval)
                return _gpuUsageByDevice;

            try
            {
                var probe = new DefaultLlmRuntimeCompatibilityProbe();
                _gpuUsageByDevice = probe.DetectNvidiaGpus(CancellationToken.None)
                    .Where(gpu => !string.IsNullOrWhiteSpace(gpu.DeviceId))
                    .ToDictionary(
                        gpu => NormalizeDeviceId(gpu.DeviceId),
                        gpu => new GpuDeviceUsage(
                            Math.Clamp(gpu.GpuUsagePercent.GetValueOrDefault(), 0, 100),
                            Math.Clamp(gpu.VramUsagePercent.GetValueOrDefault(), 0, 100)),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _gpuUsageByDevice = new Dictionary<string, GpuDeviceUsage>(StringComparer.OrdinalIgnoreCase);
            }

            _gpuUsageRefreshedAtUtc = DateTimeOffset.UtcNow;
            return _gpuUsageByDevice;
        }
    }

    private static GpuDeviceUsage GetGpuDeviceUsage(IReadOnlyDictionary<string, GpuDeviceUsage> usageByDevice, string deviceId)
    {
        string normalized = NormalizeDeviceId(deviceId);
        if (!string.IsNullOrWhiteSpace(normalized) && usageByDevice.TryGetValue(normalized, out GpuDeviceUsage usage))
            return usage;
        return GpuDeviceUsage.Empty;
    }

    private readonly record struct GpuDeviceUsage(double GpuUsagePercent, double VramUsagePercent)
    {
        public static GpuDeviceUsage Empty { get; } = new(0, 0);
    }

    private static bool LoadedInstanceBelongsToModel(string instanceId, LlmLoadConfig config, string modelKey)
    {
        string normalizedModelKey = NormalizeModelIdentifier(modelKey);
        string configModelKey = NormalizeModelIdentifier(config.ModelKey);
        if (!string.IsNullOrWhiteSpace(configModelKey))
            return string.Equals(configModelKey, normalizedModelKey, StringComparison.OrdinalIgnoreCase);

        string normalizedInstanceId = NormalizeModelIdentifier(instanceId);
        return string.Equals(normalizedInstanceId, normalizedModelKey, StringComparison.OrdinalIgnoreCase) ||
               normalizedInstanceId.StartsWith(normalizedModelKey + "@", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLoadedModelKey(string instanceId, LlmLoadConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ModelKey))
            return config.ModelKey;

        int suffix = instanceId.IndexOf('@');
        return suffix > 0 ? instanceId[..suffix] : instanceId;
    }

    private bool TryGetDefaultInferenceModel(out LlmModelInfo defaultModel)
    {
        IReadOnlyList<LlmModelInfo> models = ListModels();
        defaultModel = models.FirstOrDefault(IsPrimaryInferenceModel) ??
                       models.FirstOrDefault(IsFallbackInferenceModel)!;
        return defaultModel != null;
    }

    public static bool IsChatLoadableModel(LlmModelInfo? model) =>
        IsGgufChatLoadableModel(model) || IsVllmChatLoadableModel(model);

    private static bool IsGgufChatLoadableModel(LlmModelInfo? model) =>
        model != null &&
        string.Equals(model.Format, "gguf", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(model.LoadDisabledReason) &&
        !string.IsNullOrWhiteSpace(model.Architecture) &&
        IsChatCompletionModelType(model.Type) &&
        !IsAuxiliaryModelFile(model.FilePath);

    private static bool IsVllmChatLoadableModel(LlmModelInfo? model) =>
        model != null &&
        string.IsNullOrWhiteSpace(model.LoadDisabledReason) &&
        HasTag(model, "vllm") &&
        (string.Equals(model.Format, "safetensors", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(model.Format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(model.Format, "torch", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(model.Format, "gptq", StringComparison.OrdinalIgnoreCase)) &&
        IsChatCompletionModelType(model.Type);

    public static bool IsRuntimeLoadableModel(LlmModelInfo? model) =>
        model != null &&
        string.IsNullOrWhiteSpace(model.LoadDisabledReason) &&
        (IsChatLoadableModel(model) || IsGenerationModel(model) &&
            (!string.Equals(model.Format, "gguf", StringComparison.OrdinalIgnoreCase) || HasTag(model, "complete-model")));

    private static string ResolveLoadInstanceId(string modelKey, string deviceId, string? requestedInstanceId)
    {
        string normalizedInstanceId = NormalizeModelIdentifier(requestedInstanceId);
        if (!string.IsNullOrWhiteSpace(normalizedInstanceId))
            return normalizedInstanceId;

        if (string.IsNullOrWhiteSpace(deviceId))
            return modelKey;

        return modelKey + "@" + SanitizeInstanceSegment(deviceId);
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        string normalized = (deviceId ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        if (normalized.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase))
            return "cuda:" + normalized["cuda:".Length..].Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericDevice) && numericDevice >= 0)
            return "cuda:" + numericDevice.ToString(CultureInfo.InvariantCulture);
        return normalized;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static IReadOnlyList<string> NormalizeDeviceIds(IReadOnlyList<string>? deviceIds, string fallbackDeviceId)
    {
        var result = new List<string>();
        foreach (string value in deviceIds ?? [])
        {
            string normalized = NormalizeDeviceId(value);
            if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(fallbackDeviceId))
            result.Add(fallbackDeviceId);

        return result;
    }

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        foreach (string value in values ?? [])
        {
            string normalized = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }

    private static LlmParallelismMode ResolveParallelismMode(LlmParallelismMode? requested, IReadOnlyList<string> targetDeviceIds, IReadOnlyList<string> networkNodeIds)
    {
        if (requested.HasValue)
            return requested.Value;
        if (targetDeviceIds.Count > 1 || networkNodeIds.Count > 1)
            return LlmParallelismMode.DataParallel;
        return LlmParallelismMode.Single;
    }

    private static void ValidateTensorParallelTargets(LlmParallelismMode mode, IReadOnlyList<string> targetDeviceIds)
    {
        if (mode != LlmParallelismMode.TensorParallel)
            return;

        int targetCount = targetDeviceIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (targetCount < 2)
            throw new LlmRuntimeException(
                "Tensor parallelism requires at least two target GPUs.",
                "invalid_request_error",
                "unsupported_tensor_parallel_targets");
    }

    private static int ResolveTensorParallelSize(LlmParallelismMode mode, IReadOnlyList<string> targetDeviceIds) =>
        mode == LlmParallelismMode.TensorParallel
            ? Math.Max(2, targetDeviceIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            : 1;

    private static LlmParallelismPlacement ResolveParallelismPlacement(LlmParallelismPlacement? requested, IReadOnlyList<string> networkNodeIds)
    {
        if (requested.HasValue)
            return requested.Value;
        return networkNodeIds.Count == 0 ? LlmParallelismPlacement.Local : LlmParallelismPlacement.Network;
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private static string SanitizeInstanceSegment(string value)
    {
        string sanitized = Regex.Replace(value.Trim(), @"[^a-zA-Z0-9._-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "device" : sanitized;
    }

    private static uint ResolveDefaultContextLength(LlmModelInfo info, uint configuredDefault)
    {
        uint fallback = configuredDefault == 0 ? 8192u : configuredDefault;
        if (info.MaxContextLength.HasValue && info.MaxContextLength.Value > 0)
        {
            uint modelMax = info.MaxContextLength.Value;
            if (IsVllmChatLoadableModel(info))
                return Math.Clamp(modelMax, 512u, 131072u);

            return Math.Min(modelMax, fallback);
        }

        return fallback;
    }

    private LlmLoadConfig NormalizeLinuxCudaLoadConfig(LlmModelInfo info, LlmLoadConfig config, bool emitProgress)
    {
        if (!OperatingSystem.IsLinux())
            return config;

        if (config.Backend is not LlmBackendKind.Cuda and not LlmBackendKind.Cuda12)
            return config;

        uint maxContext = ResolveLinuxCudaMaxContextLength();
        int maxBatch = config.ContextLength > 32768 ? 128 : Math.Max(1, config.EvalBatchSize);
        uint contextLength = config.ContextLength > maxContext ? maxContext : config.ContextLength;
        int evalBatchSize = config.EvalBatchSize > maxBatch ? maxBatch : config.EvalBatchSize;
        if (contextLength == config.ContextLength && evalBatchSize == config.EvalBatchSize)
            return config;

        if (emitProgress)
        {
            ReportModelLoadProgress(
                config.ModelKey,
                config.InstanceId,
                5,
                "loading",
                "Adjusted Linux CUDA load settings for " + info.DisplayName + ": context " +
                config.ContextLength.ToString(CultureInfo.InvariantCulture) + " -> " +
                contextLength.ToString(CultureInfo.InvariantCulture) + ", eval batch " +
                config.EvalBatchSize.ToString(CultureInfo.InvariantCulture) + " -> " +
                evalBatchSize.ToString(CultureInfo.InvariantCulture) + ".",
                false);
        }

        return CopyLoadConfig(config, contextLength, evalBatchSize);
    }

    private static uint ResolveLinuxCudaMaxContextLength()
    {
        uint configured = ReadUIntEnvironment("LLMRUNTIME_LINUX_CUDA_MAX_CONTEXT_LENGTH");
        if (configured > 0)
            return Math.Clamp(configured, 4096u, 262144u);

        long smallestVramBytes = DetectSmallestNvidiaVramBytes();
        if (smallestVramBytes > 0 && smallestVramBytes <= 20L * 1024L * 1024L * 1024L)
            return 65536u;

        return 131072u;
    }

    private static uint ReadUIntEnvironment(string name)
    {
        string value = Environment.GetEnvironmentVariable(name) ?? "";
        return uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint result)
            ? result
            : 0u;
    }

    private static long DetectSmallestNvidiaVramBytes()
    {
        try
        {
            string nvidiaSmi = Environment.GetEnvironmentVariable("JACKLLM_NVIDIA_SMI") ??
                               Environment.GetEnvironmentVariable("NVIDIA_SMI_PATH") ??
                               "nvidia-smi";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = nvidiaSmi,
                    Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return 0;

            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return 0;
            }

            if (process.ExitCode != 0)
                return 0;

            long smallestMiB = long.MaxValue;
            foreach (string line in process.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib) && mib > 0)
                    smallestMiB = Math.Min(smallestMiB, mib);
            }

            return smallestMiB == long.MaxValue ? 0 : smallestMiB * 1024L * 1024L;
        }
        catch
        {
            return 0;
        }
    }

    private static LlmLoadConfig CopyLoadConfig(LlmLoadConfig source, uint contextLength, int evalBatchSize) => new()
    {
        ModelKey = source.ModelKey,
        InstanceId = source.InstanceId,
        DeviceId = source.DeviceId,
        ConcurrencyLimit = source.ConcurrencyLimit,
        Backend = source.Backend,
        AllowBackendFallback = source.AllowBackendFallback,
        ContextLength = contextLength,
        EvalBatchSize = Math.Max(1, evalBatchSize),
        FlashAttention = source.FlashAttention,
        OffloadKvCacheToGpu = source.OffloadKvCacheToGpu,
        GpuLayerCount = source.GpuLayerCount,
        ParallelismMode = source.ParallelismMode,
        ParallelismPlacement = source.ParallelismPlacement,
        TargetDeviceIds = source.TargetDeviceIds,
        NetworkNodeIds = source.NetworkNodeIds,
        MaxGpuLoadPercent = source.MaxGpuLoadPercent,
        MaxVramUsagePercent = source.MaxVramUsagePercent,
        PipelineStageCount = source.PipelineStageCount,
        DataParallelReplicaCount = source.DataParallelReplicaCount,
        ParallelTensor = source.ParallelTensor || source.ParallelismMode == LlmParallelismMode.TensorParallel,
        TensorParallelSize = source.ParallelismMode == LlmParallelismMode.TensorParallel
            ? Math.Max(source.TensorParallelSize, ResolveTensorParallelSize(source.ParallelismMode, source.TargetDeviceIds))
            : 1,
        VideoDeviceMap = source.VideoDeviceMap,
        VideoAllowCpuOffload = source.VideoAllowCpuOffload,
        VideoOffloadFolder = source.VideoOffloadFolder,
        VideoDisableCudaMemoryGuard = source.VideoDisableCudaMemoryGuard,
        VideoCudaMemoryReserveMb = source.VideoCudaMemoryReserveMb,
        VideoCpuMaxMemory = source.VideoCpuMaxMemory,
        VideoMemorySaving = source.VideoMemorySaving
    };

    public static bool IsGenerationModel(LlmModelInfo? model) =>
        model != null &&
        (IsGenerationModelType(model.Type) ||
         HasTag(model, "image") ||
         HasTag(model, "audio") ||
         HasTag(model, "video"));

    public static string GetRuntimeServiceForModel(LlmModelInfo? model)
    {
        if (model == null)
            return "";

        string type = (model.Type ?? "").Trim().ToLowerInvariant();
        if (type == "video" || HasTag(model, "video"))
            return "video_generation";
        if (type == "audio" || HasTag(model, "audio"))
            return "audio_generation";
        if (type == "image" || HasTag(model, "image"))
            return "image_generation";
        if (IsVllmChatLoadableModel(model))
            return "chat_completion";
        if (IsChatLoadableModel(model))
            return "chat_completion";
        return "";
    }

    private static IReadOnlyList<string> GetModalities(string modelType, IReadOnlyList<string> tags)
    {
        var modalities = new List<string>();
        string type = (modelType ?? "").Trim().ToLowerInvariant();
        if (type == "video" || HasTag(tags, "video"))
            modalities.Add("video");
        if (type == "audio" || HasTag(tags, "audio"))
            modalities.Add("audio");
        if (type == "image" || HasTag(tags, "image"))
            modalities.Add("image");
        if (modalities.Count == 0)
            modalities.Add("text");
        return modalities;
    }

    private static bool HasTag(IReadOnlyList<string> tags, string tag) =>
        tags.Any(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));

    private static string ToBackendName(LlmBackendKind backend) => backend switch
    {
        LlmBackendKind.Cpu => "cpu",
        LlmBackendKind.Cuda12 => "cuda",
        LlmBackendKind.Vulkan => "vulkan",
        LlmBackendKind.DirectML => "directml",
        LlmBackendKind.Vllm => "vllm",
        _ => "auto"
    };

    public static string? GetChatLoadDisabledReason(LlmModelInfo? model)
    {
        if (model == null)
            return "Model metadata is missing.";
        if (IsVllmChatLoadableModel(model))
            return null;
        if (HasTag(model, "vllm"))
            return "Hugging Face safetensors/GPTQ chat bundles require the vLLM backend.";
        if (!string.Equals(model.Format, "gguf", StringComparison.OrdinalIgnoreCase))
            return $"Model format '{model.Format}' is listed but cannot be loaded by the GGUF chat backend yet.";
        if (!string.IsNullOrWhiteSpace(model.LoadDisabledReason))
            return model.LoadDisabledReason;
        if (string.IsNullOrWhiteSpace(model.Architecture))
            return "The GGUF metadata is missing general.architecture, so LlmRuntime cannot identify a chat backend for this file.";
        if (!IsChatCompletionModelType(model.Type))
            return $"Model '{model.DisplayName}' is a {model.Type} model and cannot be loaded by the GGUF chat backend.";
        if (IsAuxiliaryModelFile(model.FilePath))
            return "This GGUF appears to be an auxiliary projection file, not a standalone chat model.";
        return null;
    }

    public static string? GetRuntimeLoadDisabledReason(LlmModelInfo? model)
    {
        if (model == null)
            return "Model metadata is missing.";
        if (IsRuntimeLoadableModel(model))
            return null;
        if (!string.IsNullOrWhiteSpace(model.LoadDisabledReason))
            return model.LoadDisabledReason;
        if (IsGenerationModel(model) && string.Equals(model.Format, "gguf", StringComparison.OrdinalIgnoreCase))
            return "Generation GGUF models load from CompleteModels; standard GGUF chat and vision models stay in Models.";
        return GetChatLoadDisabledReason(model);
    }

    private static bool IsPrimaryInferenceModel(LlmModelInfo model) =>
        IsChatLoadableModel(model) &&
        string.Equals(model.Type, "llm", StringComparison.OrdinalIgnoreCase);

    private static bool IsFallbackInferenceModel(LlmModelInfo model) =>
        IsChatLoadableModel(model);

    private static bool IsChatCompletionModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return true;

        return type.Equals("llm", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("vlm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenerationModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        return type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageOrVideoGenerationModel(LlmModelInfo? model)
    {
        if (model == null)
            return false;

        string type = (model.Type ?? "").Trim();
        return type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("video", StringComparison.OrdinalIgnoreCase) ||
               HasTag(model, "image") ||
               HasTag(model, "video");
    }

    private static string ResolveDefaultMediaDeviceMap(LlmModelInfo model, string? requestedDeviceMap)
    {
        string requested = NormalizeDiffusersDeviceMap(requestedDeviceMap);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        return IsImageOrVideoGenerationModel(model) ? "balanced" : "";
    }

    private static bool ResolveDefaultMediaCpuOffload(LlmModelInfo model, bool? requested) =>
        requested ?? IsImageOrVideoGenerationModel(model);

    private static bool ResolveDefaultMediaMemorySaving(LlmModelInfo model, bool? requested) =>
        requested ?? IsImageOrVideoGenerationModel(model);

    private static string NormalizeDiffusersDeviceMap(string? value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase);
        return normalized switch
        {
            "off" or "none" or "disabled" or "false" or "0" => "",
            "auto" or "balanced_low_0" or "balanced_low0" or "sequential" => "balanced",
            "balanced" or "cuda" or "cpu" => normalized,
            _ => (value ?? "").Trim()
        };
    }

    private static bool HasTag(LlmModelInfo model, string tag) =>
        model.Tags.Any(item => string.Equals(item, tag, StringComparison.OrdinalIgnoreCase));

    private static bool IsDefaultModelPlaceholder(string model) =>
        string.IsNullOrWhiteSpace(model) ||
        string.Equals(model.Trim(), "lm-studio", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model.Trim(), "lmstudio", StringComparison.OrdinalIgnoreCase);

    private static int ResolveGpuLayerCount(LlmBackendKind backend, int? requestedGpuLayerCount, int defaultGpuLayerCount) =>
        requestedGpuLayerCount ?? (backend is LlmBackendKind.Auto or LlmBackendKind.Cpu ? 0 : defaultGpuLayerCount);

    private static bool ResolveOffloadKvCacheToGpu(LlmBackendKind backend, bool? requestedOffloadKvCacheToGpu, bool defaultOffloadKvCacheToGpu) =>
        requestedOffloadKvCacheToGpu ?? (backend is LlmBackendKind.Auto or LlmBackendKind.Cpu ? defaultOffloadKvCacheToGpu : true);

    private static bool ResolveAllowBackendFallback(LlmBackendKind backend, bool? requestedAllowBackendFallback, bool defaultAllowBackendFallback, bool preventCpuFallback)
    {
        bool allowFallback = requestedAllowBackendFallback ?? defaultAllowBackendFallback;
        if (preventCpuFallback && backend is not LlmBackendKind.Auto and not LlmBackendKind.Cpu)
            return false;
        return allowFallback;
    }

    public ModelDownloadJob StartDownload(string modelOrUrl, string? quantization = null)
    {
        return StartDownload(modelOrUrl, quantization, null);
    }

    public ModelDownloadJob StartDownload(string modelOrUrl, string? quantization, string? bearerToken)
    {
        return StartDownload(modelOrUrl, quantization, bearerToken, null, null, null, null, null, null);
    }

    public ModelDownloadJob StartDownload(
        string modelOrUrl,
        string? quantization,
        string? bearerToken,
        string? repository,
        string? revision,
        string? sourcePath,
        string? task,
        string? targetRelativeDirectory,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (string.IsNullOrWhiteSpace(modelOrUrl))
            throw new ArgumentException("Model URL is required.", nameof(modelOrUrl));

        string url = modelOrUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new NotSupportedException("Catalog identifiers are not supported yet; pass an exact model file URL.");

        if (!ModelDownloadService.IsModelUrl(url))
            throw new NotSupportedException("The first milestone downloads exact .gguf, .safetensors, or .onnx file URLs.");

        bool useCompleteModelLayout = ShouldDownloadSingleFileToCompleteModels(url, repository, sourcePath, task);
        string downloadRoot = useCompleteModelLayout && !string.IsNullOrWhiteSpace(_options.CompleteModelRoot)
            ? _options.CompleteModelRoot
            : _options.ModelRoot;
        string? customFileName = useCompleteModelLayout
            ? BuildSingleFileCompleteModelRelativePath(url, repository, revision, sourcePath, targetRelativeDirectory)
            : null;
        string manifestFormat = ResolveSingleFileManifestFormat(url);
        string manifestTask = ResolveSingleFileManifestTask(url, repository, sourcePath, task);
        IReadOnlyList<string> manifestSourcePaths = string.IsNullOrWhiteSpace(sourcePath)
            ? [ModelDownloadService.ExtractFileName(url)]
            : [sourcePath!.Replace('\\', '/')];
        string? registeredCompleteManifestPath = null;

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..10];
        var job = new ModelDownloadJob
        {
            JobId = jobId,
            Model = modelOrUrl,
            Status = "downloading"
        };
        _downloads[jobId] = job;

        var service = new ModelDownloadService(downloadRoot, _options.DownloadTimeout);
        _downloadServices[jobId] = service;
        service.ProgressChanged += progress =>
        {
            job.DownloadedBytes = progress.DownloadedBytes;
            job.TotalSizeBytes = progress.TotalBytes > 0 ? progress.TotalBytes : job.TotalSizeBytes;
        };
        service.DownloadCompleted += path =>
        {
            job.FilePath = registeredCompleteManifestPath ?? path;
            job.Status = "completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
        };
        service.DownloadCompletedWithResult += result =>
        {
            string completedPath = result.FilePath;
            if (useCompleteModelLayout)
            {
                try
                {
                    registeredCompleteManifestPath = CompleteModelManifestWriter.WriteManifest(
                        Path.GetDirectoryName(result.FilePath) ?? downloadRoot,
                        repository,
                        revision,
                        manifestTask,
                        manifestFormat,
                        manifestSourcePaths,
                        metadata);
                    completedPath = registeredCompleteManifestPath;
                }
                catch (Exception ex)
                {
                    job.Error = "Downloaded file, but complete-model registration failed: " + ex.Message;
                }
            }

            job.FilePath = completedPath;
            job.DownloadedBytes = result.SizeBytes;
            job.TotalSizeBytes = result.SizeBytes;
            job.Sha256 = result.Sha256;
            job.Status = "completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _downloadServices.TryRemove(jobId, out _);
        };
        service.DownloadFailed += error =>
        {
            job.Error = error;
            job.Status = ResolveDownloadFailureStatus(error);
            job.CompletedAt = DateTimeOffset.UtcNow;
            _downloadServices.TryRemove(jobId, out _);
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await service.DownloadAsync(url, customFileName, bearerToken: bearerToken).ConfigureAwait(false);
            }
            finally
            {
                if (!IsActiveDownloadStatus(job.Status))
                    _downloadServices.TryRemove(jobId, out _);
            }
        });
        return job;
    }

    private static bool ShouldDownloadSingleFileToCompleteModels(string url, string? repository, string? sourcePath, string? task)
    {
        if (!ModelDownloadService.IsGgufUrl(url))
            return true;

        string signal = string.Join(" ", new[]
        {
            url ?? "",
            repository ?? "",
            sourcePath ?? "",
            task ?? ""
        });
        if (IsGenerationDownloadTask(task))
            return true;

        string type = ModelHeuristics.DetectModelType(null, signal);
        return string.Equals(type, "image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenerationDownloadTask(string? task)
    {
        string normalized = (task ?? "").Trim().ToLowerInvariant();
        return normalized == "image" ||
               normalized == "audio" ||
               normalized == "video" ||
               normalized.Contains("image", StringComparison.Ordinal) ||
               normalized.Contains("audio", StringComparison.Ordinal) ||
               normalized.Contains("video", StringComparison.Ordinal);
    }

    private static string ResolveSingleFileManifestFormat(string url)
    {
        if (ModelDownloadService.IsGgufUrl(url))
            return "gguf";
        if (ModelDownloadService.IsOnnxUrl(url))
            return "onnx";
        return "pytorch";
    }

    private static string ResolveSingleFileManifestTask(string url, string? repository, string? sourcePath, string? task)
    {
        if (!string.IsNullOrWhiteSpace(task))
            return task!.Trim();

        string signal = string.Join(" ", new[] { url ?? "", repository ?? "", sourcePath ?? "" });
        string type = ModelHeuristics.DetectModelType(null, signal);
        if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
            return "image.text-to-image";
        if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
            return "audio.generation";
        if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
            return "video.text-to-video";
        return "llm.text-generation";
    }

    private static string BuildSingleFileCompleteModelRelativePath(
        string url,
        string? repository,
        string? revision,
        string? sourcePath,
        string? targetRelativeDirectory)
    {
        string fileName = ModelDownloadService.ExtractFileName(url);
        string directory = !string.IsNullOrWhiteSpace(targetRelativeDirectory)
            ? SanitizeRelativePath(targetRelativeDirectory!)
            : !string.IsNullOrWhiteSpace(repository)
                ? BuildRepositoryTargetDirectory(repository!, revision)
                : "";
        string relativeFile = SanitizeRelativePath(string.IsNullOrWhiteSpace(sourcePath) ? fileName : sourcePath!);
        return string.IsNullOrWhiteSpace(directory) ? relativeFile : Path.Combine(directory, relativeFile);
    }

    private static string BuildRepositoryTargetDirectory(string repository, string? revision)
    {
        string repo = (repository ?? "").Replace('\\', '/').Trim('/');
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision!.Trim().Trim('/');
        string[] parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(rev)
            .Select(SanitizeFileName)
            .ToArray();
        return parts.Length == 0 ? Path.Combine("model", "main") : Path.Combine(parts);
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string[] parts = (relativePath ?? "")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .Where(part => !string.IsNullOrWhiteSpace(part) && part != "." && part != "..")
            .ToArray();
        return parts.Length == 0 ? "model.bin" : Path.Combine(parts);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string sanitized = new string((value ?? "").Trim().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "model" : sanitized;
    }

    public ModelDownloadJob StartBundleDownload(ModelBundleDownloadRequest request, string? bearerToken = null)
    {
        if (request == null)
            throw new ArgumentException("Bundle download request is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Repository))
            throw new ArgumentException("Bundle repository is required.", nameof(request));

        string jobId = "job_" + Guid.NewGuid().ToString("N")[..10];
        var job = new ModelDownloadJob
        {
            JobId = jobId,
            Model = request.Repository,
            Status = "downloading"
        };
        _downloads[jobId] = job;

        string downloadRoot = string.IsNullOrWhiteSpace(_options.CompleteModelRoot) ? _options.ModelRoot : _options.CompleteModelRoot;
        var service = new ModelDownloadService(downloadRoot, _options.DownloadTimeout);
        _downloadServices[jobId] = service;
        service.ProgressChanged += progress =>
        {
            job.DownloadedBytes = progress.DownloadedBytes;
            job.TotalSizeBytes = progress.TotalBytes > 0 ? progress.TotalBytes : job.TotalSizeBytes;
        };
        service.BundleDownloadCompletedWithResult += result =>
        {
            job.FilePath = result.ManifestPath;
            job.DownloadedBytes = result.SizeBytes;
            job.TotalSizeBytes = result.SizeBytes;
            job.Status = "completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _downloadServices.TryRemove(jobId, out _);
        };
        service.DownloadCompleted += path =>
        {
            job.FilePath = path;
            job.Status = "completed";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _downloadServices.TryRemove(jobId, out _);
        };
        service.DownloadFailed += error =>
        {
            job.Error = error;
            job.Status = ResolveDownloadFailureStatus(error);
            job.CompletedAt = DateTimeOffset.UtcNow;
            _downloadServices.TryRemove(jobId, out _);
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await service.DownloadBundleAsync(request, bearerToken: bearerToken).ConfigureAwait(false);
            }
            finally
            {
                if (!IsActiveDownloadStatus(job.Status))
                    _downloadServices.TryRemove(jobId, out _);
            }
        });
        return job;
    }

    public IReadOnlyList<ModelDownloadJob> GetDownloadJobs() =>
        _downloads.Values.OrderByDescending(job => job.StartedAt).ToList();

    public ModelDownloadJob? GetDownloadJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;
        return _downloads.TryGetValue(jobId, out var job) ? job : null;
    }

    public bool CancelDownload(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        if (!_downloads.TryGetValue(jobId, out var job))
            return false;

        if (!_downloadServices.TryGetValue(jobId, out var service))
            return false;

        job.Status = "cancelling";
        service.Cancel();
        return true;
    }

    public IReadOnlyList<ModelDownloadJob> CancelActiveDownloads()
    {
        var cancelled = new List<ModelDownloadJob>();
        foreach (var pair in _downloadServices)
        {
            if (!_downloads.TryGetValue(pair.Key, out var job))
                continue;

            job.Status = "cancelling";
            pair.Value.Cancel();
            cancelled.Add(job);
        }

        return cancelled;
    }

    private static string ResolveDownloadFailureStatus(string error)
    {
        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "already_downloaded";
        if (error.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("canceled", StringComparison.OrdinalIgnoreCase))
            return "cancelled";
        if (error.Contains("paused", StringComparison.OrdinalIgnoreCase))
            return "paused";
        return "failed";
    }

    private static bool IsActiveDownloadStatus(string status) =>
        status.Equals("downloading", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("cancelling", StringComparison.OrdinalIgnoreCase);

    public LlmModelConfig GetModelConfig(string model)
    {
        string key = FindModel(model)?.Key ?? NormalizeModelIdentifier(model);
        return ReadModelConfigs().TryGetValue(key, out var config) ? config : new LlmModelConfig();
    }

    public LlmModelConfig SaveModelConfig(string model, LlmModelConfig config)
    {
        string key = FindModel(model)?.Key ?? NormalizeModelIdentifier(model);
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Model identifier is required.", nameof(model));

        var configs = ReadModelConfigs();
        configs[key] = config ?? new LlmModelConfig();
        string path = GetModelConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _options.ModelRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(configs, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return configs[key];
    }

    public bool DeleteModel(string model)
    {
        var info = FindModel(model);
        if (info == null)
            return false;

        Unload(info.Key);
        string target = ResolveDeletableModelPath(info);
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Model path is not inside a managed model directory.");

        if (File.Exists(target))
            File.Delete(target);
        else if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        else
            return false;

        var configs = ReadModelConfigs();
        if (configs.Remove(info.Key))
            File.WriteAllText(GetModelConfigPath(), JsonSerializer.Serialize(configs, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        MarkModelCacheDirty();
        return true;
    }

    private IReadOnlyList<LlmModelInfo> RefreshModelCache()
    {
        IReadOnlyList<ModelDirectory> modelDirectories = DiscoverModelDirectories();
        EnsureModelWatchers(modelDirectories);

        var models = new List<LlmModelInfo>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedCompleteBundleDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelDirectory directory in modelDirectories)
        {
            foreach (string filePath in EnumerateModelFiles(directory.Path))
            {
                if (TryResolveHuggingFaceSafetensorsBundle(filePath, directory, out string bundleDirectory) &&
                    usedCompleteBundleDirectories.Add(bundleDirectory))
                {
                    try
                    {
                        models.Add(CreateHuggingFaceSafetensorsModelInfo(bundleDirectory, directory, usedKeys));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Failed to read Hugging Face safetensors metadata for " + bundleDirectory + ": " + ex.Message);
                    }

                    continue;
                }

                if (!IsCompleteModelFile(filePath))
                    continue;

                try
                {
                    models.Add(CreateModelInfo(filePath, directory, usedKeys));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to read model metadata for " + filePath + ": " + ex.Message);
                }
            }
        }

        IReadOnlyList<LlmModelInfo> ordered = models
            .OrderBy(model => string.Equals(model.Publisher, "local", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(model => model.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_modelCacheLock)
        {
            _modelCache = ordered;
            _modelCacheRefreshedAtUtc = DateTimeOffset.UtcNow;
            _modelCacheDirty = false;
            return _modelCache;
        }
    }

    private IReadOnlyList<ModelDirectory> DiscoverModelDirectories()
    {
        var directories = new List<ModelDirectory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddModelDirectory(NormalizeDirectory(_options.ModelRoot), "local", false, createIfMissing: true);
        if (!string.IsNullOrWhiteSpace(_options.CompleteModelRoot))
            AddModelDirectory(NormalizeDirectory(_options.CompleteModelRoot), "local", false, createIfMissing: true);
        if (_options.IncludeLmStudioModels)
        {
            foreach (string path in DiscoverLmStudioModelRoots())
                AddModelDirectory(NormalizeDirectory(path), "lmstudio", true, createIfMissing: false);
        }

        return directories;

        void AddModelDirectory(string path, string publisher, bool isLmStudio, bool createIfMissing)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (createIfMissing)
                    Directory.CreateDirectory(path);
                if (!Directory.Exists(path))
                    return;

                string normalized = NormalizeDirectory(path);
                if (seen.Add(normalized))
                    directories.Add(new ModelDirectory(normalized, publisher, isLmStudio));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Skipping model directory " + path + ": " + ex.Message);
            }
        }
    }

    private static IEnumerable<string> DiscoverLmStudioModelRoots()
    {
        foreach (string home in DiscoverLmStudioHomeDirectories())
        {
            if (string.IsNullOrWhiteSpace(home))
                continue;

            yield return Path.Combine(home, "models");
            yield return Path.Combine(home, ".internal", "bundled-models");

            foreach (string configuredRoot in ReadLmStudioConfiguredModelRoots(home))
                yield return configuredRoot;
        }

        foreach (string variable in LmStudioModelRootEnvironmentVariables)
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            foreach (string candidate in SplitPathList(value))
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    yield return candidate;
            }
        }
    }

    private static IEnumerable<string> DiscoverLmStudioHomeDirectories()
    {
        var explicitHomes = new List<string>();
        foreach (string variable in LmStudioHomeEnvironmentVariables)
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            foreach (string candidate in SplitPathList(value))
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    explicitHomes.Add(candidate);
            }
        }

        if (explicitHomes.Count > 0)
            return explicitHomes;

        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("HOME") ?? "";

        return string.IsNullOrWhiteSpace(userProfile)
            ? []
            : [Path.Combine(userProfile, ".lmstudio")];
    }

    private static IEnumerable<string> ReadLmStudioConfiguredModelRoots(string lmStudioHome)
    {
        if (string.IsNullOrWhiteSpace(lmStudioHome))
            yield break;

        string settingsPath = Path.Combine(lmStudioHome, "settings.json");
        if (!File.Exists(settingsPath))
            yield break;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (string key in LmStudioModelRootSettingsKeys)
            {
                string? configuredPath = TryReadStringProperty(document.RootElement, key);
                if (!string.IsNullOrWhiteSpace(configuredPath))
                    yield return ResolveLmStudioConfiguredPath(lmStudioHome, configuredPath);
            }
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static string? TryReadStringProperty(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ResolveLmStudioConfiguredPath(string lmStudioHome, string configuredPath)
    {
        string expanded = Environment.ExpandEnvironmentVariables((configuredPath ?? "").Trim());
        if (string.IsNullOrWhiteSpace(expanded))
            return "";

        return Path.IsPathFullyQualified(expanded)
            ? expanded
            : Path.Combine(lmStudioHome, expanded);
    }

    private static IEnumerable<string> SplitPathList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (string part in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return part;
    }

    private void EnsureModelWatchers(IEnumerable<ModelDirectory>? knownDirectories = null)
    {
        if (_disposed)
            return;

        IReadOnlyList<ModelDirectory> directories = knownDirectories?.ToList() ?? DiscoverModelDirectories();
        lock (_modelCacheLock)
        {
            foreach (ModelDirectory directory in directories)
            {
                if (_modelWatchers.ContainsKey(directory.Path) || !Directory.Exists(directory.Path))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(directory.Path)
                    {
                        IncludeSubdirectories = true,
                        Filter = "*.*",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    FileSystemEventHandler changed = (_, _) => MarkModelCacheDirty();
                    RenamedEventHandler renamed = (_, _) => MarkModelCacheDirty();
                    ErrorEventHandler errored = (_, _) => MarkModelCacheDirty();
                    watcher.Created += changed;
                    watcher.Changed += changed;
                    watcher.Deleted += changed;
                    watcher.Renamed += renamed;
                    watcher.Error += errored;
                    watcher.EnableRaisingEvents = true;
                    _modelWatchers[directory.Path] = watcher;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to watch model directory " + directory.Path + ": " + ex.Message);
                }
            }
        }
    }

    private void MarkModelCacheDirty()
    {
        lock (_modelCacheLock)
            _modelCacheDirty = true;
    }

    private static IEnumerable<string> EnumerateModelFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return ModelFileSearchPatterns
                .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, ModelFileEnumerationOptions))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to enumerate model directory " + directory + ": " + ex.Message);
            return [];
        }
    }

    private static bool IsCompleteModelFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;
        if (HasPartialModelPathMarker(filePath))
            return false;
        if (IsOnnxManifestFile(filePath))
            return IsCompleteOnnxManifestFile(filePath);
        if (!filePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsAuxiliaryModelFile(filePath))
            return false;

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length < 8)
                return false;

            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < RecentlyWrittenModelGracePeriod && !HasStableFileLength(filePath, fileInfo.Length))
                return false;

            Span<byte> header = stackalloc byte[4];
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            return stream.Read(header) == 4 &&
                   header[0] == (byte)'G' &&
                   header[1] == (byte)'G' &&
                   header[2] == (byte)'U' &&
                   header[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    private bool TryResolveHuggingFaceSafetensorsBundle(string filePath, ModelDirectory directory, out string bundleDirectory)
    {
        bundleDirectory = "";
        if (!IsCompleteModelDirectory(directory) || string.IsNullOrWhiteSpace(filePath))
            return false;

        string fileName = Path.GetFileName(filePath);
        if (!fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("model.safetensors.index.json", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrWhiteSpace(candidateDirectory))
            return false;

        string configPath = Path.Combine(candidateDirectory, "config.json");
        if (!File.Exists(configPath))
            return false;

        try
        {
            if (!Directory.EnumerateFiles(candidateDirectory, "*.safetensors", SearchOption.TopDirectoryOnly).Any())
                return false;
        }
        catch
        {
            return false;
        }

        if (!IsHuggingFaceChatSafetensorsConfig(configPath))
            return false;

        bundleDirectory = NormalizeDirectory(candidateDirectory);
        return true;
    }

    private static bool IsHuggingFaceChatSafetensorsConfig(string configPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string architectureText = ReadJsonStringArray(root, "architectures");
            string modelType = TryReadStringProperty(root, "model_type") ?? "";
            bool hasCausalLmArchitecture =
                architectureText.Contains("CausalLM", StringComparison.OrdinalIgnoreCase) ||
                architectureText.Contains("ForConditionalGeneration", StringComparison.OrdinalIgnoreCase);
            bool knownTextModelType = ContainsAny(
                modelType,
                "llama",
                "qwen",
                "mistral",
                "mixtral",
                "gemma",
                "phi",
                "deepseek",
                "starcoder",
                "falcon",
                "gpt");
            bool hasQuantization = root.TryGetProperty("quantization_config", out JsonElement quantization) &&
                                   quantization.ValueKind == JsonValueKind.Object;
            return hasCausalLmArchitecture || knownTextModelType && hasQuantization;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOnnxManifestFile(string filePath)
    {
        return filePath.EndsWith(".jackonnx.json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompleteOnnxManifestFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length < 2)
                return false;

            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < RecentlyWrittenModelGracePeriod && !HasStableFileLength(filePath, fileInfo.Length))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("format", out JsonElement formatElement) ||
                !IsRuntimeManifestFormat(formatElement.GetString()))
                return false;

            return root.TryGetProperty("components", out JsonElement componentsElement) &&
                   componentsElement.ValueKind == JsonValueKind.Object &&
                   componentsElement.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.String);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRuntimeManifestFormat(string? format)
    {
        return string.Equals(format, "onnx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, "torch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuxiliaryModelFile(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath ?? "");
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("-mmproj", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_mmproj", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".mmproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStableFileLength(string filePath, long expectedLength)
    {
        Thread.Sleep(75);
        try
        {
            return File.Exists(filePath) && new FileInfo(filePath).Length == expectedLength;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPartialModelPathMarker(string filePath)
    {
        foreach (string segment in SplitPathSegments(filePath))
        {
            string lower = segment.ToLowerInvariant();
            foreach (string marker in PartialModelPathMarkers)
            {
                if (lower.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool ModelMatches(LlmModelInfo info, string requested)
    {
        return ModelIdentifierMatches(info.Key, requested) ||
               ModelIdentifierMatches(info.DisplayName, requested) ||
               ModelIdentifierMatches(info.FileName, requested) ||
               ModelIdentifierMatches(Path.GetFileNameWithoutExtension(info.FileName), requested) ||
               ModelIdentifierMatches(info.FilePath, requested) ||
               info.Aliases.Any(alias => ModelIdentifierMatches(alias, requested));
    }

    private static bool ModelIdentifierMatches(string? candidate, string requested)
    {
        return !string.IsNullOrWhiteSpace(candidate) &&
               string.Equals(NormalizeModelIdentifier(candidate), requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelIdentifier(string? value)
    {
        return (value ?? "").Trim().Trim('"').Replace('\\', '/').Trim('/');
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static IReadOnlyList<string> BuildModelAliases(string filePath, ModelDirectory directory, string displayName, string fileName)
    {
        var aliases = new List<string>();
        AddAlias(aliases, displayName);
        AddAlias(aliases, fileName);
        AddAlias(aliases, Path.GetFileNameWithoutExtension(fileName));
        AddAlias(aliases, filePath);

        string relativePath = GetRelativeModelPath(directory.Path, filePath);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            string relativePathWithSlashes = relativePath.Replace('\\', '/');
            string relativeWithoutExtension = RemoveExtension(relativePathWithSlashes);
            AddAlias(aliases, relativePath);
            AddAlias(aliases, relativePathWithSlashes);
            AddAlias(aliases, relativeWithoutExtension);

            string relativeDirectory = Path.GetDirectoryName(relativePath) ?? "";
            string[] segments = SplitPathSegments(relativeDirectory).ToArray();
            if (segments.Length > 0)
                AddAlias(aliases, segments[^1]);
            if (directory.IsLmStudio && segments.Length > 1)
            {
                AddAlias(aliases, segments[1]);
                AddAlias(aliases, string.Join("/", segments.Skip(1)));
            }
        }

        return aliases;
    }

    private static string BuildModelKey(string filePath, ModelDirectory directory, string displayName, IReadOnlyList<string> aliases, HashSet<string> usedKeys)
    {
        var candidates = new List<string>();
        if (directory.IsLmStudio)
        {
            string lmStudioAlias = GetLmStudioModelAlias(directory.Path, filePath);
            AddAlias(candidates, lmStudioAlias);
        }

        AddAlias(candidates, displayName);
        AddAlias(candidates, Path.GetFileNameWithoutExtension(filePath));
        candidates.AddRange(aliases);

        foreach (string candidate in candidates)
        {
            string key = NormalizeModelIdentifier(candidate);
            if (!string.IsNullOrWhiteSpace(key) && usedKeys.Add(key))
                return key;
        }

        string fallbackBase = NormalizeModelIdentifier(displayName);
        if (string.IsNullOrWhiteSpace(fallbackBase))
            fallbackBase = "model";

        for (int suffix = 2; ; suffix++)
        {
            string key = fallbackBase + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (usedKeys.Add(key))
                return key;
        }
    }

    private static string GetLmStudioModelAlias(string rootPath, string filePath)
    {
        string relativePath = GetRelativeModelPath(rootPath, filePath);
        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? "";
        string[] segments = SplitPathSegments(relativeDirectory).ToArray();
        if (segments.Length > 1)
            return segments[1];
        return segments.Length == 1 ? segments[0] : "";
    }

    private static string GetRelativeModelPath(string rootPath, string filePath)
    {
        try
        {
            return Path.GetRelativePath(rootPath, filePath);
        }
        catch
        {
            return Path.GetFileName(filePath);
        }
    }

    private static string RemoveExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? path : path[..^extension.Length];
    }

    private static IEnumerable<string> SplitPathSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        return path
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));
    }

    private static void AddAlias(List<string> aliases, string? alias)
    {
        string normalized = NormalizeModelIdentifier(alias);
        if (!string.IsNullOrWhiteSpace(normalized) && !aliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            aliases.Add(normalized);
    }

    private LlmModelInfo CreateModelInfo(string filePath, ModelDirectory directory, HashSet<string> usedKeys)
    {
        if (IsOnnxManifestFile(filePath))
            return CreateOnnxModelInfo(filePath, directory, usedKeys);

        var fileInfo = new FileInfo(filePath);
        var metadata = GgufMetadataReader.Read(filePath);
        string fileName = Path.GetFileName(filePath);
        string displayName = Path.GetFileNameWithoutExtension(filePath);
        var aliases = BuildModelAliases(filePath, directory, metadata?.GetString("general.name") ?? displayName, fileName);
        string key = BuildModelKey(filePath, directory, displayName, aliases, usedKeys);
        string? arch = metadata?.GetString("general.architecture");
        uint? contextLength = !string.IsNullOrWhiteSpace(arch) ? metadata?.GetUInt32($"{arch}.context_length") : null;
        ulong? paramCount = metadata?.GetUInt64("general.parameter_count");
        if (!paramCount.HasValue && !string.IsNullOrWhiteSpace(arch))
            paramCount = metadata?.GetUInt64($"{arch}.parameter_count");

        string quant = ModelHeuristics.DetectQuantType(fileName);
        IReadOnlyList<string> tags = AddDirectoryTags(ModelHeuristics.DetectModelTags(fileName, metadata), directory);
        string type = ModelHeuristics.DetectModelType(metadata, filePath);
        string? loadDisabledReason = GetGgufLoadDisabledReason(metadata, arch, directory, type, tags);
        return new LlmModelInfo
        {
            Key = key,
            DisplayName = metadata?.GetString("general.name") ?? displayName,
            FilePath = filePath,
            FileName = fileName,
            Type = type,
            Publisher = directory.Publisher,
            Architecture = arch,
            QuantizationName = string.IsNullOrWhiteSpace(quant) ? null : quant,
            BitsPerWeight = ModelHeuristics.EstimateBitsPerWeight(quant),
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            ParamsString = ModelHeuristics.FormatParamCount(paramCount),
            MaxContextLength = contextLength,
            LoadDisabledReason = loadDisabledReason,
            Tags = tags,
            Aliases = aliases,
            LoadedInstances = GetLoadedInstances(key, type, tags)
        };
    }

    private LlmModelInfo CreateHuggingFaceSafetensorsModelInfo(string modelDirectory, ModelDirectory directory, HashSet<string> usedKeys)
    {
        string configPath = Path.Combine(modelDirectory, "config.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement root = document.RootElement;
        string directoryName = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string displayName = FirstNonEmpty(
            TryReadStringProperty(root, "_name_or_path"),
            directoryName);
        string architecture = FirstNonEmpty(
            ReadFirstJsonString(root, "architectures"),
            TryReadStringProperty(root, "model_type"));
        string quantization = ReadQuantizationName(root);
        uint? contextLength = ResolveHuggingFaceContextLength(root, modelDirectory);
        var aliases = BuildModelAliases(configPath, directory, displayName, directoryName).ToList();
        AddAlias(aliases, directoryName);
        AddAlias(aliases, modelDirectory);
        string key = BuildModelKey(configPath, directory, displayName, aliases, usedKeys);
        IReadOnlyList<string> tags = AddDirectoryTags(
            new[]
            {
                "chat",
                "text",
                "safetensors",
                "pytorch",
                "vllm"
            }
            .Concat(string.IsNullOrWhiteSpace(quantization) ? [] : new[] { quantization.ToLowerInvariant() })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
            directory);

        return new LlmModelInfo
        {
            Key = key,
            DisplayName = displayName,
            FilePath = modelDirectory,
            FileName = directoryName,
            Type = "llm",
            Publisher = directory.Publisher,
            Architecture = architecture,
            QuantizationName = string.IsNullOrWhiteSpace(quantization) ? "safetensors" : quantization,
            BitsPerWeight = ModelHeuristics.EstimateBitsPerWeight(quantization),
            SizeBytes = EstimateDirectoryBytes(modelDirectory),
            ParamsString = InferParameterSummary(directoryName),
            MaxContextLength = contextLength,
            Format = "safetensors",
            Tags = tags,
            Aliases = aliases,
            LoadedInstances = GetLoadedInstances(key, "llm", tags)
        };
    }

    private static uint? ResolveHuggingFaceContextLength(JsonElement root, string modelDirectory)
    {
        var values = new List<uint>();
        AddIfPositive(values, TryReadUInt32(root, "max_position_embeddings"));
        AddIfPositive(values, TryReadUInt32(root, "seq_length"));
        AddIfPositive(values, TryReadUInt32(root, "n_positions"));
        AddIfPositive(values, TryReadUInt32(root, "model_max_length"));
        AddIfPositive(values, TryReadUInt32(root, "sliding_window"));

        string tokenizerConfigPath = Path.Combine(modelDirectory, "tokenizer_config.json");
        if (File.Exists(tokenizerConfigPath))
        {
            try
            {
                using JsonDocument tokenizerDocument = JsonDocument.Parse(File.ReadAllText(tokenizerConfigPath));
                JsonElement tokenizerRoot = tokenizerDocument.RootElement;
                AddIfPositive(values, TryReadUInt32(tokenizerRoot, "model_max_length"));
                AddIfPositive(values, TryReadUInt32(tokenizerRoot, "max_position_embeddings"));
                AddIfPositive(values, TryReadUInt32(tokenizerRoot, "seq_length"));
                AddIfPositive(values, TryReadUInt32(tokenizerRoot, "n_positions"));
            }
            catch
            {
            }
        }

        return values.Count == 0 ? null : values.Max();
    }

    private static void AddIfPositive(List<uint> values, uint? value)
    {
        if (value.HasValue && value.Value > 0)
            values.Add(value.Value);
    }

    private string? GetGgufLoadDisabledReason(
        GgufMetadataReader? metadata,
        string? architecture,
        ModelDirectory directory,
        string type,
        IReadOnlyList<string> tags)
    {
        bool completeGenerationGguf = IsCompleteModelDirectory(directory) &&
                                      (IsGenerationModelType(type) ||
                                       HasTag(tags, "image") ||
                                       HasTag(tags, "audio") ||
                                       HasTag(tags, "video"));
        if (completeGenerationGguf)
            return null;

        if (metadata == null)
            return "The file has a .gguf extension, but its GGUF metadata could not be read. Redownload a standard chat or instruct GGUF model.";
        if (string.IsNullOrWhiteSpace(architecture))
            return "The GGUF metadata is missing general.architecture, so LlmRuntime cannot identify a chat backend for this file. Choose a standard chat or instruct GGUF model with complete metadata.";
        return null;
    }

    private LlmModelInfo CreateOnnxModelInfo(string manifestPath, ModelDirectory directory, HashSet<string> usedKeys)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        string id = TryReadStringProperty(root, "id") ?? Path.GetFileNameWithoutExtension(manifestPath);
        string name = TryReadStringProperty(root, "name") ?? id;
        string type = TryReadStringProperty(root, "type") ?? "onnx";
        string format = TryReadStringProperty(root, "format") ?? "onnx";
        string precision = TryReadStringProperty(root, "precision") ?? "";
        string fileName = Path.GetFileName(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Environment.CurrentDirectory;
        DiffusersModelConfigInfo? diffusersConfig = DiffusersModelConfigMetadata.FindBest(manifestDirectory);
        string effectiveType = DiffusersModelConfigMetadata.RefineTask(type, diffusersConfig);
        var aliases = BuildModelAliases(manifestPath, directory, id, fileName).ToList();
        AddAlias(aliases, name);
        string source = TryReadMetadataString(root, "source") ?? "";
        if (source.StartsWith("huggingface:", StringComparison.OrdinalIgnoreCase))
            AddAlias(aliases, source["huggingface:".Length..]);
        string key = BuildModelKey(manifestPath, directory, id, aliases, usedKeys);
        string? architecture = TryReadMetadataString(root, "architecture") ?? diffusersConfig?.ClassName;
        string adapterType = FirstNonEmpty(TryReadMetadataString(root, "adapterType"), TryReadMetadataString(root, "adapter_type"));
        string baseModel = FirstNonEmpty(TryReadMetadataString(root, "baseModel"), TryReadMetadataString(root, "base_model"), diffusersConfig?.InferredBaseModel);
        IReadOnlyList<string> baseModels = SplitMetadataValues(FirstNonEmpty(
                TryReadMetadataString(root, "baseModels"),
                TryReadMetadataString(root, "base_models"),
                baseModel,
                diffusersConfig?.InferredBaseModel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool adapterRequiresBaseModel = TryReadMetadataBool(root, "adapterRequiresBaseModel") ||
            TryReadMetadataBool(root, "adapter_requires_base_model") ||
            !string.IsNullOrWhiteSpace(adapterType) && adapterType.Contains("lora", StringComparison.OrdinalIgnoreCase);

        string normalizedType = NormalizeOnnxModelType(effectiveType);
        IReadOnlyList<string> tags = AddDirectoryTags(BuildManifestTags(format, effectiveType, precision), directory);
        uint? contextLength = TryParseUInt32(FirstNonEmpty(
            TryReadMetadataString(root, "contextLength"),
            TryReadMetadataString(root, "context_length"),
            diffusersConfig?.TextLen?.ToString(CultureInfo.InvariantCulture)));
        string parameterSummary = FirstNonEmpty(
            TryReadMetadataString(root, "paramsString"),
            TryReadMetadataString(root, "params_string"),
            TryReadMetadataString(root, "diffusersConfigSummary"),
            diffusersConfig?.ParameterSummary);

        return new LlmModelInfo
        {
            Key = key,
            DisplayName = name,
            FilePath = manifestPath,
            FileName = fileName,
            Type = normalizedType,
            Publisher = directory.Publisher,
            Architecture = architecture,
            QuantizationName = string.IsNullOrWhiteSpace(precision) ? null : precision,
            BitsPerWeight = ModelHeuristics.EstimateBitsPerWeight(precision),
            SizeBytes = EstimateOnnxManifestSize(manifestPath, root),
            ParamsString = string.IsNullOrWhiteSpace(parameterSummary) ? null : parameterSummary,
            MaxContextLength = contextLength,
            Format = NormalizeManifestFormat(format),
            AdapterType = adapterType,
            AdapterRequiresBaseModel = adapterRequiresBaseModel,
            BaseModel = baseModels.FirstOrDefault() ?? "",
            BaseModels = baseModels,
            Tags = tags,
            Aliases = aliases,
            LoadedInstances = GetLoadedInstances(key, normalizedType, tags)
        };
    }

    private IReadOnlyList<LoadedModelInstance> GetLoadedInstances(string key, string modelType, IReadOnlyList<string> tags)
    {
        IReadOnlyList<string> modalities = GetModalities(modelType, tags);
        var instances = new List<LoadedModelInstance>();
        foreach (var pair in _loaded)
        {
            if (LoadedInstanceBelongsToModel(pair.Key, pair.Value.LoadConfig, key))
                instances.Add(CreateLoadedInstance(pair.Key, pair.Value.LoadConfig, key, modalities, pair.Value));
        }

        foreach (var pair in _loadedRuntimeModels)
        {
            if (LoadedInstanceBelongsToModel(pair.Key, pair.Value, key))
                instances.Add(CreateLoadedInstance(pair.Key, pair.Value, key, modalities));
        }

        return instances
            .OrderBy(instance => instance.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private LoadedModelInstance CreateLoadedInstance(string instanceId, LlmLoadConfig config, string fallbackModelKey, IReadOnlyList<string> modalities, ILlmBackend? backend = null)
    {
        string modelKey = string.IsNullOrWhiteSpace(config.ModelKey) ? fallbackModelKey : config.ModelKey;
        int concurrencyLimit = Math.Max(1, config.ConcurrencyLimit);
        return new LoadedModelInstance
        {
            Id = instanceId,
            ModelKey = modelKey,
            DeviceId = config.DeviceId,
            Backend = ToBackendName(config.Backend),
            Modalities = modalities,
            ActiveJobs = _scheduler.GetActiveJobs(instanceId),
            QueuedJobs = _scheduler.GetQueuedJobs(instanceId, concurrencyLimit),
            ConcurrencyLimit = concurrencyLimit,
            Health = "healthy",
            Config = config,
            PromptPipelineReady = backend?.IsPromptPipelineReady ?? !IsChatModelModalitySet(modalities),
            PromptPipelineStatus = backend?.PromptPipelineStatus ?? (!IsChatModelModalitySet(modalities) ? "not_applicable" : "cold"),
            PromptPipelineDetail = backend?.PromptPipelineDetail ?? "",
            PromptPipelineReadyAtUtc = backend?.PromptPipelineReadyAtUtc,
            PromptPipelineWarmupSeconds = backend?.PromptPipelineWarmupSeconds ?? 0
        };
    }

    private static bool IsChatModelModalitySet(IReadOnlyList<string> modalities) =>
        modalities.Count == 0 ||
        modalities.Contains("text", StringComparer.OrdinalIgnoreCase) ||
        modalities.Contains("chat", StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> AddDirectoryTags(IReadOnlyList<string> tags, ModelDirectory directory)
    {
        if (!IsCompleteModelDirectory(directory))
            return tags;

        var result = tags.ToList();
        if (!result.Contains("complete-model", StringComparer.OrdinalIgnoreCase))
            result.Add("complete-model");
        return result;
    }

    private bool IsCompleteModelDirectory(ModelDirectory directory)
    {
        if (string.IsNullOrWhiteSpace(_options.CompleteModelRoot))
            return false;
        return string.Equals(NormalizeDirectory(_options.CompleteModelRoot), NormalizeDirectory(directory.Path), StringComparison.OrdinalIgnoreCase);
    }

    private static long EstimateOnnxManifestSize(string manifestPath, JsonElement root)
    {
        long total = 0;
        try { total += new FileInfo(manifestPath).Length; } catch { }

        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Environment.CurrentDirectory;
        if (!root.TryGetProperty("components", out JsonElement componentsElement) || componentsElement.ValueKind != JsonValueKind.Object)
            return total;

        foreach (JsonProperty component in componentsElement.EnumerateObject())
        {
            if (component.Value.ValueKind != JsonValueKind.String)
                continue;

            string relativePath = component.Value.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            string fullPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));
            try
            {
                if (File.Exists(fullPath))
                {
                    total += new FileInfo(fullPath).Length;
                }
                else if (Directory.Exists(fullPath))
                {
                    foreach (string file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                        total += new FileInfo(file).Length;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to estimate ONNX component size for " + fullPath + ": " + ex.Message);
            }
        }

        return total;
    }

    private static long EstimateDirectoryBytes(string directory)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", DirectorySizeEnumerationOptions))
            {
                try { total += GetFileLengthFollowingLinks(file); } catch { }
            }
        }
        catch
        {
        }

        return total;
    }

    private static long GetFileLengthFollowingLinks(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
            return 0;

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is FileInfo targetFile && targetFile.Exists)
                    return targetFile.Length;
            }
            catch
            {
            }
        }

        return info.Length;
    }

    private static IReadOnlyList<string> BuildManifestTags(string format, string type, string precision)
    {
        var tags = new List<string> { NormalizeManifestFormat(format) };
        if (!string.IsNullOrWhiteSpace(precision))
            tags.Add(precision.ToLowerInvariant());

        string lower = type.ToLowerInvariant();
        if (lower.Contains("text", StringComparison.Ordinal) || lower.Contains("llm", StringComparison.Ordinal))
            tags.Add("chat");
        if (lower.Contains("embed", StringComparison.Ordinal))
            tags.Add("embedding");
        if (lower.Contains("vision", StringComparison.Ordinal) || lower.Contains("vlm", StringComparison.Ordinal))
            tags.Add("vision");
        if (lower.Contains("image", StringComparison.Ordinal))
            tags.Add("image");
        if (lower.Contains("audio", StringComparison.Ordinal) || lower.Contains("speech", StringComparison.Ordinal))
            tags.Add("audio");
        if (lower.Contains("video", StringComparison.Ordinal))
            tags.Add("video");

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeManifestFormat(string format)
    {
        string lower = (format ?? "").Trim().ToLowerInvariant();
        if (lower == "torch")
            return "pytorch";
        return string.IsNullOrWhiteSpace(lower) ? "manifest" : lower;
    }

    private static string NormalizeOnnxModelType(string type)
    {
        string lower = type.ToLowerInvariant();
        if (lower.Contains("embed", StringComparison.Ordinal))
            return "embedding";
        if (lower.Contains("video", StringComparison.Ordinal))
            return "video";
        if (lower.Contains("audio", StringComparison.Ordinal) || lower.Contains("speech", StringComparison.Ordinal))
            return "audio";
        if (lower.Contains("vision", StringComparison.Ordinal) || lower.Contains("vlm", StringComparison.Ordinal))
            return "vlm";
        if (lower.Contains("image", StringComparison.Ordinal))
            return "image";
        return "llm";
    }

    private static string? TryReadMetadataString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("metadata", out JsonElement metadata) || metadata.ValueKind != JsonValueKind.Object)
            return null;

        return TryReadStringProperty(metadata, propertyName);
    }

    private static bool TryReadMetadataBool(JsonElement root, string propertyName)
    {
        string value = TryReadMetadataString(root, propertyName) ?? "";
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return values.Any(value => !string.IsNullOrWhiteSpace(value) && text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadJsonStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
            return "";
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join(" ", value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? ""));
    }

    private static string ReadFirstJsonString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value))
            return "";
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array)
            return "";
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                return item.GetString()!;
        }

        return "";
    }

    private static uint? TryReadUInt32(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetUInt32(out uint result) ? result : null;
    }

    private static string ReadQuantizationName(JsonElement root)
    {
        if (!root.TryGetProperty("quantization_config", out JsonElement quantization) ||
            quantization.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        return FirstNonEmpty(
            TryReadStringProperty(quantization, "quant_method"),
            TryReadStringProperty(quantization, "quantization_method"),
            TryReadStringProperty(quantization, "load_in"),
            TryReadStringProperty(quantization, "bits"));
    }

    private static string InferParameterSummary(string text)
    {
        Match match = Regex.Match(text ?? "", @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>[bm])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["value"].Value + match.Groups["unit"].Value.ToUpperInvariant()
            : "";
    }

    private static IEnumerable<string> SplitMetadataValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (string part in value.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = part.Trim().Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    public bool IsBaseModelAvailable(LlmModelInfo model)
    {
        if (model == null || model.BaseModels.Count == 0 && string.IsNullOrWhiteSpace(model.BaseModel))
            return true;

        foreach (string baseModel in model.BaseModels.Count == 0 ? [model.BaseModel] : model.BaseModels)
        {
            if (!string.IsNullOrWhiteSpace(baseModel) && FindModel(baseModel) != null)
                return true;
        }

        return false;
    }

    private string GetModelConfigPath()
    {
        string root = string.IsNullOrWhiteSpace(_options.CompleteModelRoot) ? _options.ModelRoot : _options.CompleteModelRoot;
        return Path.Combine(root, ".model-config.json");
    }

    private Dictionary<string, LlmModelConfig> ReadModelConfigs()
    {
        string path = GetModelConfigPath();
        if (!File.Exists(path))
            return new Dictionary<string, LlmModelConfig>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, LlmModelConfig>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
                   new Dictionary<string, LlmModelConfig>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, LlmModelConfig>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool IsLoadedModelPersistenceEnabled() =>
        !string.IsNullOrWhiteSpace(_options.RuntimeConfigPath);

    private IReadOnlyList<PersistedLoadedModel> ReadPersistedLoadedModels()
    {
        string path = _options.RuntimeConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];

        try
        {
            LlmRuntimePersistedConfig? config = JsonSerializer.Deserialize<LlmRuntimePersistedConfig>(File.ReadAllText(path), RuntimeConfigJsonOptions);
            return config?.LoadedModels?
                .Where(model => !string.IsNullOrWhiteSpace(model.Model) || !string.IsNullOrWhiteSpace(model.InstanceId) || !string.IsNullOrWhiteSpace(model.Config.InstanceId))
                .GroupBy(GetPersistedLoadedModelKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(GetPersistedLoadedModelKey, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SavePersistedLoadedModel(string model, string instanceId, LlmLoadConfig config)
    {
        if (!IsLoadedModelPersistenceEnabled() || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(instanceId))
            return;

        lock (_runtimeConfigLock)
        {
            var models = ReadPersistedLoadedModels()
                .ToDictionary(GetPersistedLoadedModelKey, StringComparer.OrdinalIgnoreCase);
            models[instanceId] = new PersistedLoadedModel
            {
                Model = model,
                InstanceId = instanceId,
                Config = EnsureLoadConfigIdentity(config, model, instanceId),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            WritePersistedLoadedModels(models.Values);
        }
    }

    private void RemovePersistedLoadedModel(string instanceId)
    {
        if (!IsLoadedModelPersistenceEnabled() || string.IsNullOrWhiteSpace(instanceId))
            return;

        lock (_runtimeConfigLock)
        {
            var models = ReadPersistedLoadedModels()
                .ToDictionary(GetPersistedLoadedModelKey, StringComparer.OrdinalIgnoreCase);
            if (models.Remove(instanceId))
                WritePersistedLoadedModels(models.Values);
        }
    }

    private static string GetPersistedLoadedModelKey(PersistedLoadedModel model)
    {
        if (model == null)
            return "";
        if (!string.IsNullOrWhiteSpace(model.InstanceId))
            return model.InstanceId;
        return string.IsNullOrWhiteSpace(model.Config.InstanceId) ? model.Model : model.Config.InstanceId;
    }

    private static LlmLoadConfig EnsureLoadConfigIdentity(LlmLoadConfig config, string modelKey, string instanceId)
    {
        bool tensorMode = config.ParallelismMode == LlmParallelismMode.TensorParallel;
        int tensorParallelSize = tensorMode
            ? Math.Max(config.TensorParallelSize, ResolveTensorParallelSize(config.ParallelismMode, config.TargetDeviceIds))
            : 1;
        if (string.Equals(config.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(config.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) &&
            config.ConcurrencyLimit > 0 &&
            (!tensorMode || config.ParallelTensor && config.TensorParallelSize == tensorParallelSize))
            return config;

        return new LlmLoadConfig
        {
            ModelKey = string.IsNullOrWhiteSpace(config.ModelKey) ? modelKey : config.ModelKey,
            InstanceId = string.IsNullOrWhiteSpace(config.InstanceId) ? instanceId : config.InstanceId,
            DeviceId = config.DeviceId,
            ConcurrencyLimit = Math.Max(1, config.ConcurrencyLimit),
            Backend = config.Backend,
            AllowBackendFallback = config.AllowBackendFallback,
            ContextLength = config.ContextLength,
            EvalBatchSize = config.EvalBatchSize,
            FlashAttention = config.FlashAttention,
            OffloadKvCacheToGpu = config.OffloadKvCacheToGpu,
            GpuLayerCount = config.GpuLayerCount,
            ParallelismMode = config.ParallelismMode,
            ParallelismPlacement = config.ParallelismPlacement,
            TargetDeviceIds = config.TargetDeviceIds,
            NetworkNodeIds = config.NetworkNodeIds,
            MaxGpuLoadPercent = config.MaxGpuLoadPercent,
            MaxVramUsagePercent = config.MaxVramUsagePercent,
            PipelineStageCount = config.PipelineStageCount,
            DataParallelReplicaCount = config.DataParallelReplicaCount,
            ParallelTensor = config.ParallelTensor || tensorMode,
            TensorParallelSize = tensorParallelSize,
            VideoDeviceMap = config.VideoDeviceMap,
            VideoAllowCpuOffload = config.VideoAllowCpuOffload,
            VideoOffloadFolder = config.VideoOffloadFolder,
            VideoDisableCudaMemoryGuard = config.VideoDisableCudaMemoryGuard,
            VideoCudaMemoryReserveMb = config.VideoCudaMemoryReserveMb,
            VideoCpuMaxMemory = config.VideoCpuMaxMemory,
            VideoMemorySaving = config.VideoMemorySaving
        };
    }

    private void WritePersistedLoadedModels(IEnumerable<PersistedLoadedModel> models)
    {
        string path = _options.RuntimeConfigPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _options.ModelRoot);
        var config = new LlmRuntimePersistedConfig
        {
            LoadedModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model.Model))
                .OrderBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config, RuntimeConfigJsonOptions));
    }

    private void ReportModelLoadProgress(string model, string instanceId, int percent, string status, string message, bool isStartupRestore)
    {
        percent = Math.Clamp(percent, 0, 100);
        ModelLoadProgressChanged?.Invoke(new LlmModelLoadProgress
        {
            Model = model ?? "",
            InstanceId = instanceId ?? "",
            Percent = percent,
            Status = status ?? "",
            Message = message ?? "",
            IsStartupRestore = isStartupRestore
        });
    }

    private static int Percent(int completed, int total) =>
        total <= 0 ? 100 : Math.Clamp((int)Math.Round(completed * 100d / total), 0, 100);

    private static string TrimStatusMessage(string message)
    {
        message = (message ?? "").Trim();
        if (message.Length <= 200)
            return message;
        return message[..200] + "...";
    }

    private string ResolveDeletableModelPath(LlmModelInfo info)
    {
        string filePath = Path.GetFullPath(info.FilePath);
        string target = IsOnnxManifestFile(filePath)
            ? Path.GetDirectoryName(filePath) ?? filePath
            : filePath;

        string fullTarget = Path.GetFullPath(target);
        foreach (ModelDirectory directory in DiscoverModelDirectories())
        {
            string root = Path.GetFullPath(directory.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullTarget.Equals(root, StringComparison.OrdinalIgnoreCase))
                return "";
            if (fullTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullTarget.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fullTarget;
        }

        return "";
    }

    private static uint? TryParseUInt32(string? value)
    {
        return uint.TryParse(value, out uint parsed) ? parsed : null;
    }

    private sealed record ModelDirectory(string Path, string Publisher, bool IsLmStudio);

    private sealed class LlmRuntimePersistedConfig
    {
        public int Version { get; set; } = 1;

        public List<PersistedLoadedModel> LoadedModels { get; set; } = [];
    }

    private sealed class PersistedLoadedModel
    {
        public string Model { get; set; } = "";

        public string InstanceId { get; set; } = "";

        public LlmLoadConfig Config { get; set; } = new();

        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var service in _downloadServices.Values)
            service.Cancel();
        _downloadServices.Clear();
        foreach (var backend in _loaded.Values)
            backend.Dispose();
        _loaded.Clear();
        _loadedRuntimeModels.Clear();
    }
}
