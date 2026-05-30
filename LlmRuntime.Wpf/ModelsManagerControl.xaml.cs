using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LlmRuntime;

namespace LlmRuntime.Wpf;

public sealed class ModelManagerBenchmarkInfo
{
    public string ModelId { get; set; } = "";
    public string Status { get; set; } = "";
    public double TokensPerSecond { get; set; }
    public double TokensPerHour { get; set; }
    public int Rating { get; set; }
    public string BenchmarkedUtc { get; set; } = "";
    public string BenchmarkKind { get; set; } = "";
    public string BenchmarkPrompt { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool CanBenchmark { get; set; } = true;
}

public sealed class ModelManagerBenchmarkRequest
{
    public string ModelId { get; set; } = "";
    public string SuggestedChatService { get; set; } = "";
    public string ModelType { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public IReadOnlyList<string> Tags { get; set; } = [];
}

public partial class ModelsManagerControl : UserControl, IDisposable
{
    private const int DefaultIdleUnloadMinutes = 0;
    private const int MaxResourceHistorySamples = 34;
    private const double BusyGpuUsagePercent = 75d;

    private static readonly HttpClient ApiClient = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ObservableCollection<ModelManagerModelItem> _models = [];
    private readonly ObservableCollection<GpuConfigurationItem> _gpuConfigurations = [];
    private readonly ObservableCollection<GpuModelAssignmentItem> _gpuModelAssignments = [];
    private readonly Dictionary<string, ModelManagerLoadSettings> _settingsByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDownloadedModelEnableTokens = new(StringComparer.OrdinalIgnoreCase);
    private GpuGlobalSettings _globalGpuSettings = new();
    private readonly Dictionary<string, ModelManagerBenchmarkInfo> _benchmarksByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SystemResourceUsageSnapshot>> _resourceHistoryByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _resourceUsageTimer;
    private readonly ICollectionView _modelView;
    private LlmModelRegistry? _registry;
    private string _modelsDirectory = Path.Combine(Environment.CurrentDirectory, "Models");
    private string _completeModelsDirectory = Path.Combine(Environment.CurrentDirectory, "CompleteModels");
    private bool _disposed;
    private bool _loadingSelectedSettings;
    private bool _savedSettingsLoaded;
    private bool _viewReady;
    private bool _refreshingModels;
    private bool _suppressSelectionChanged;
    private bool _benchmarkControlsEnabled = true;
    private bool _refreshingGpuAssignments;
    private bool _applyingGpuConfiguration;
    private bool _refreshingResourceUsage;
    private string _preferredSelectionKey = "";
    private ulong _lastCpuIdleTime;
    private ulong _lastCpuKernelTime;
    private ulong _lastCpuUserTime;
    private bool _hasLastCpuSample;

    public ModelsManagerControl()
    {
        InitializeComponent();
        _modelView = CollectionViewSource.GetDefaultView(_models);
        _modelView.Filter = FilterModel;
        _modelView.SortDescriptions.Add(new SortDescription(nameof(ModelManagerModelItem.SortRank), ListSortDirection.Ascending));
        _modelView.SortDescriptions.Add(new SortDescription(nameof(ModelManagerModelItem.DisplayName), ListSortDirection.Ascending));
        ModelsListBox.ItemsSource = _modelView;
        GpuSelectorComboBox.ItemsSource = _gpuConfigurations;
        GpuModelAssignmentsListView.ItemsSource = _gpuModelAssignments;
        _viewReady = true;
        ModelBrowserControl.ModelDownloaded += path =>
        {
            TrackDownloadedModelForDefaultEnable(path);
            ModelDownloaded?.Invoke(path);
            _ = RefreshModelsAsync();
        };
        ModelBrowserControl.ModelLoadRequested += path => _ = LoadModelAsync(path);
        ModelBrowserControl.StatusChanged += SetStatus;
        LoadSavedSettings();
        LoadSavedGpuSettings();
        ApplyGlobalGpuSettingsToPanel(_globalGpuSettings);
        _resourceUsageTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _resourceUsageTimer.Tick += async (_, _) => await RefreshResourceUsageAsync().ConfigureAwait(true);
        Loaded += async (_, _) =>
        {
            await RefreshModelsAsync().ConfigureAwait(true);
            _resourceUsageTimer.Start();
        };
        Unloaded += (_, _) => _resourceUsageTimer.Stop();
    }

    private bool TryBeginOnUi(Action action)
    {
        if (action == null || _disposed)
            return false;

        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return false;

            if (Dispatcher.CheckAccess())
            {
                action();
                return true;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try { action(); } catch { }
            }), DispatcherPriority.Background);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string ModelsDirectory
    {
        get => _modelsDirectory;
        set
        {
            string next = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.CurrentDirectory, "Models")
                : value;
            if (string.Equals(_modelsDirectory, next, StringComparison.OrdinalIgnoreCase))
                return;

            _modelsDirectory = next;
            ModelBrowserControl.ModelsDirectory = next;
            ResetRegistry();
        }
    }

    public string CompleteModelsDirectory
    {
        get => _completeModelsDirectory;
        set
        {
            string next = string.IsNullOrWhiteSpace(value)
                ? Path.Combine(Environment.CurrentDirectory, "CompleteModels")
                : value;
            if (string.Equals(_completeModelsDirectory, next, StringComparison.OrdinalIgnoreCase))
                return;

            _completeModelsDirectory = next;
            ModelBrowserControl.CompleteModelsDirectory = next;
            ResetRegistry();
        }
    }

    public Func<Task>? EnsureRuntimeStartedAsync { get; set; }

    public Func<string>? RuntimeBaseUrlProvider { get; set; }

    public event Action<string>? ModelDownloaded;

    public event Action<string>? ChatServiceSuggested;

    public event Action<string>? StatusChanged;

    public event Action<string>? ModelBenchmarkRequested;

    public event Action<ModelManagerBenchmarkRequest>? ModelBenchmarkRequestedWithMetadata;

    public event Action? BenchmarkAllModelsRequested;

    public void ShowModelsTab()
    {
        ManagerTabs.SelectedItem = ModelsTabItem;
        Focus();
    }

    public void ShowGpuTab()
    {
        ManagerTabs.SelectedItem = GpuTabItem;
        Focus();
    }

    public void SetModelBenchmarks(IEnumerable<ModelManagerBenchmarkInfo> benchmarks, bool canBenchmark)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(() => SetModelBenchmarks(benchmarks, canBenchmark));
            return;
        }

        _benchmarkControlsEnabled = canBenchmark;
        _benchmarksByModel.Clear();
        foreach (ModelManagerBenchmarkInfo benchmark in benchmarks ?? [])
        {
            if (benchmark == null || string.IsNullOrWhiteSpace(benchmark.ModelId))
                continue;

            string key = NormalizeBenchmarkKey(benchmark.ModelId);
            if (!_benchmarksByModel.ContainsKey(key))
                _benchmarksByModel.Add(key, benchmark);
        }

        ApplyBenchmarksToModels();
    }

    public void UpdateModelBenchmark(ModelManagerBenchmarkInfo benchmark, bool canBenchmark)
    {
        if (benchmark == null || string.IsNullOrWhiteSpace(benchmark.ModelId))
            return;

        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(() => UpdateModelBenchmark(benchmark, canBenchmark));
            return;
        }

        _benchmarkControlsEnabled = canBenchmark;
        _benchmarksByModel[NormalizeBenchmarkKey(benchmark.ModelId)] = benchmark;
        ApplyBenchmarksToModels();
    }

    private async Task RefreshModelsAsync(string preferredSelectionKey = "")
    {
        if (_disposed)
            return;

        string selectionKey = FirstNonEmpty(preferredSelectionKey, GetSelectedModelKey(), _preferredSelectionKey);
        SetStatus("Refreshing model inventory...");
        RefreshModelsButton.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(CompleteModelsDirectory);
            LlmModelRegistry registry = GetRegistry();
            IReadOnlyList<LlmModelInfo> localModels = registry.ListModels();
            IReadOnlyDictionary<string, RuntimeModelSnapshot> runtimeModels = await TryFetchRuntimeModelsAsync().ConfigureAwait(true);
            RuntimeHardwareSnapshot hardware = await TryFetchRuntimeHardwareSnapshotAsync().ConfigureAwait(true);

            var merged = new Dictionary<string, ModelManagerModelItem>(StringComparer.OrdinalIgnoreCase);
            foreach (LlmModelInfo model in localModels)
            {
                RuntimeModelSnapshot? runtime = TryFindRuntimeModel(runtimeModels, model);
                var item = ModelManagerModelItem.FromLocalModel(model, runtime, registry.IsBaseModelAvailable(model));
                item.HardwareWarning = hardware.BuildModelWarning(item);
                merged[item.Key] = item;
            }

            foreach (RuntimeModelSnapshot runtime in runtimeModels.Values)
            {
                if (string.IsNullOrWhiteSpace(runtime.Key) || merged.ContainsKey(runtime.Key))
                    continue;

                var item = ModelManagerModelItem.FromRuntimeModel(runtime);
                item.HardwareWarning = hardware.BuildModelWarning(item);
                merged[item.Key] = item;
            }

            bool modelSettingsChanged = false;
            foreach (ModelManagerModelItem item in merged.Values)
            {
                if (_settingsByModel.TryGetValue(item.Key, out ModelManagerLoadSettings? settings))
                {
                    item.ApplyLoadSettings(settings);
                }
                else if (TryCreateDownloadedDefaultSettings(item, out settings))
                {
                    _settingsByModel[item.Key] = settings;
                    item.ApplyLoadSettings(settings);
                    modelSettingsChanged = true;
                }
            }

            if (modelSettingsChanged)
                SaveSavedSettings();

            _refreshingModels = true;
            try
            {
                _models.Clear();
                foreach (ModelManagerModelItem item in merged.Values.OrderBy(item => item.SortRank).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
                    _models.Add(item);
            }
            finally
            {
                _refreshingModels = false;
            }

            ApplyBenchmarksToModels();
            _modelView.Refresh();
            UpdateCounts();
            RestoreSelection(selectionKey);
            UpdateEmptyState();
            RefreshGpuConfigurationItems(hardware);

            int running = _models.Count(item => item.IsRunning);
            SetStatus("Model inventory refreshed: " + _models.Count.ToString("N0") + " model(s), " + running.ToString("N0") + " running.");
            if (!string.IsNullOrWhiteSpace(hardware.GlobalWarning))
                SetStatus(hardware.GlobalWarning);
        }
        catch (Exception ex)
        {
            SetStatus("Model inventory refresh failed: " + ex.Message);
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private LlmModelRegistry GetRegistry()
    {
        if (_registry != null)
            return _registry;

        _registry = new LlmModelRegistry(new LlmRuntimeOptions
        {
            ModelRoot = ModelsDirectory,
            CompleteModelRoot = CompleteModelsDirectory,
            IncludeLmStudioModels = true
        });
        return _registry;
    }

    private void ResetRegistry()
    {
        try { _registry?.Dispose(); } catch { }
        _registry = null;
    }

    private async Task<IReadOnlyDictionary<string, RuntimeModelSnapshot>> TryFetchRuntimeModelsAsync()
    {
        var result = new Dictionary<string, RuntimeModelSnapshot>(StringComparer.OrdinalIgnoreCase);
        string baseUrl = GetRuntimeBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return result;

        try
        {
            if (EnsureRuntimeStartedAsync != null)
                await EnsureRuntimeStartedAsync().ConfigureAwait(true);

            using var response = await ApiClient.GetAsync(baseUrl.TrimEnd('/') + "/api/v1/models").ConfigureAwait(true);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                SetStatus("LlmRuntime model state unavailable: HTTP " + (int)response.StatusCode + ".");
                return result;
            }

            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement model in models.EnumerateArray())
            {
                RuntimeModelSnapshot snapshot = RuntimeModelSnapshot.FromJson(model);
                if (!string.IsNullOrWhiteSpace(snapshot.Key) && !result.ContainsKey(snapshot.Key))
                    result.Add(snapshot.Key, snapshot);
            }
        }
        catch (Exception ex)
        {
            SetStatus("LlmRuntime model state unavailable: " + ex.Message);
        }

        return result;
    }

    private async Task<RuntimeHardwareSnapshot> TryFetchRuntimeHardwareSnapshotAsync()
    {
        string baseUrl = GetRuntimeBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return RuntimeHardwareSnapshot.Empty;

        try
        {
            using var response = await ApiClient.GetAsync(baseUrl.TrimEnd('/') + "/api/v1/runtime/compatibility").ConfigureAwait(true);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                return RuntimeHardwareSnapshot.Empty;

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            return RuntimeHardwareSnapshot.FromCompatibilityJson(root);
        }
        catch
        {
            return RuntimeHardwareSnapshot.Empty;
        }
    }

    private async Task RefreshGpuConfigurationAsync()
    {
        RuntimeHardwareSnapshot hardware = await TryFetchRuntimeHardwareSnapshotAsync().ConfigureAwait(true);
        RefreshGpuConfigurationItems(hardware);
    }

    private async Task RefreshResourceUsageAsync()
    {
        if (_disposed || _refreshingResourceUsage)
            return;

        _refreshingResourceUsage = true;
        try
        {
            RuntimeHardwareSnapshot hardware = await TryFetchRuntimeHardwareSnapshotAsync().ConfigureAwait(true);
            RefreshGpuUsageReadings(hardware);
        }
        finally
        {
            _refreshingResourceUsage = false;
        }
    }

    private void RefreshGpuUsageReadings(RuntimeHardwareSnapshot hardware)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(() => RefreshGpuUsageReadings(hardware));
            return;
        }

        bool needsFullRefresh = hardware.Gpus.Count != _gpuConfigurations.Count;
        for (int index = 0; !needsFullRefresh && index < hardware.Gpus.Count; index++)
        {
            RuntimeGpuSnapshot gpu = hardware.Gpus[index];
            string deviceId = FirstNonEmpty(gpu.DeviceId, "cuda:" + index.ToString(CultureInfo.InvariantCulture));
            needsFullRefresh = !_gpuConfigurations.Any(item => DeviceMatches(item.DeviceId, deviceId));
        }

        if (needsFullRefresh)
        {
            RefreshGpuConfigurationItems(hardware);
            return;
        }

        for (int index = 0; index < hardware.Gpus.Count; index++)
        {
            RuntimeGpuSnapshot gpu = hardware.Gpus[index];
            string deviceId = FirstNonEmpty(gpu.DeviceId, "cuda:" + index.ToString(CultureInfo.InvariantCulture));
            GpuConfigurationItem? item = _gpuConfigurations.FirstOrDefault(candidate => DeviceMatches(candidate.DeviceId, deviceId));
            item?.ApplyRuntimeGpu(gpu, index);
        }

        ApplySystemResourceSnapshot(hardware);
    }

    private void RefreshGpuConfigurationItems(RuntimeHardwareSnapshot hardware)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(() => RefreshGpuConfigurationItems(hardware));
            return;
        }

        string selectedDeviceId = GpuSelectorComboBox.SelectedItem is GpuConfigurationItem selectedGpu
            ? selectedGpu.DeviceId
            : "";
        IReadOnlyList<RuntimeGpuSnapshot> detected = hardware.Gpus;
        var existing = _gpuConfigurations.ToDictionary(item => item.DeviceId, StringComparer.OrdinalIgnoreCase);
        var next = new List<GpuConfigurationItem>();

        for (int index = 0; index < detected.Count; index++)
        {
            RuntimeGpuSnapshot gpu = detected[index];
            string deviceId = FirstNonEmpty(gpu.DeviceId, "cuda:" + index.ToString(CultureInfo.InvariantCulture));
            if (!existing.TryGetValue(deviceId, out GpuConfigurationItem? item))
            {
                item = new GpuConfigurationItem
                {
                    DeviceId = deviceId,
                    Enabled = true,
                    MaxGpuLoadPercent = _globalGpuSettings.MaxGpuLoadPercent,
                    MaxVramUsagePercent = _globalGpuSettings.MaxVramUsagePercent,
                    ParallelismMode = _globalGpuSettings.ParallelismMode,
                    ParallelismPlacement = _globalGpuSettings.ParallelismPlacement
                };
            }

            AttachGpuConfigurationItem(item);
            item.ApplyRuntimeGpu(gpu, index);
            item.ActiveModelsText = BuildActiveModelsForGpu(deviceId);
            next.Add(item);
        }

        foreach (GpuConfigurationItem item in existing.Values.Where(item => !next.Any(nextItem => string.Equals(nextItem.DeviceId, item.DeviceId, StringComparison.OrdinalIgnoreCase))))
        {
            AttachGpuConfigurationItem(item);
            item.ActiveModelsText = BuildActiveModelsForGpu(item.DeviceId);
            next.Add(item);
        }

        _applyingGpuConfiguration = true;
        _gpuConfigurations.Clear();
        foreach (GpuConfigurationItem item in next.OrderBy(item => item.DeviceSortKey, StringComparer.OrdinalIgnoreCase))
            _gpuConfigurations.Add(item);
        _applyingGpuConfiguration = false;

        RefreshGpuAssignmentsFromModels();
        GpuSelectorComboBox.SelectedItem = _gpuConfigurations.FirstOrDefault(item => DeviceMatches(item.DeviceId, selectedDeviceId)) ??
                                           _gpuConfigurations.FirstOrDefault();
        UpdateSelectedGpuConfiguration();
        GpuConfigurationStatusText.Text = _gpuConfigurations.Count == 0
            ? "No GPUs reported by the active LlmRuntime endpoint yet."
            : _gpuConfigurations.Count.ToString("N0") + " GPU(s) available for local/network placement.";
        ApplySystemResourceSnapshot(hardware);
    }

    private void ApplySystemResourceSnapshot(RuntimeHardwareSnapshot hardware)
    {
        SystemResourceUsageSnapshot systemSnapshot = BuildSystemResourceUsageSnapshot(hardware);
        int runningModelCount = Math.Max(1, _models.Count(model => model.IsRunning));
        int totalActiveJobs = _models.Sum(model => model.ActiveJobCount);
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ModelManagerModelItem model in _models)
        {
            string key = ModelSelectionKey(model);
            if (string.IsNullOrWhiteSpace(key))
                key = model.GetHashCode().ToString(CultureInfo.InvariantCulture);
            currentKeys.Add(key);

            SystemResourceUsageSnapshot snapshot = BuildModelResourceUsageSnapshot(model, hardware, systemSnapshot, runningModelCount, totalActiveJobs);
            if (!_resourceHistoryByModel.TryGetValue(key, out List<SystemResourceUsageSnapshot>? history))
            {
                history = [];
                _resourceHistoryByModel[key] = history;
            }

            history.Add(snapshot);
            if (history.Count > MaxResourceHistorySamples)
                history.RemoveRange(0, history.Count - MaxResourceHistorySamples);

            model.ApplyResourceUsage(snapshot, history.ToArray());
        }

        foreach (string staleKey in _resourceHistoryByModel.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
            _resourceHistoryByModel.Remove(staleKey);
    }

    private SystemResourceUsageSnapshot BuildSystemResourceUsageSnapshot(RuntimeHardwareSnapshot hardware)
    {
        double cpu = SampleCpuUsagePercent();
        (double ramPercent, long ramUsedBytes, long ramTotalBytes) = ReadMemoryUsage();
        double gpu = AveragePercent(hardware.Gpus.Select(gpu => gpu.GpuUsagePercent));
        double vram = AveragePercent(hardware.Gpus.Select(gpu => (double?)gpu.EffectiveVramUsagePercent));
        long vramUsed = hardware.Gpus.Sum(gpu => gpu.EffectiveMemoryUsedBytes);
        long vramTotal = hardware.Gpus.Sum(gpu => gpu.EffectiveMemoryTotalBytes);
        return new SystemResourceUsageSnapshot(DateTimeOffset.UtcNow, cpu, ramPercent, gpu, vram, ramUsedBytes, ramTotalBytes, vramUsed, vramTotal, "whole system", "System");
    }

    private SystemResourceUsageSnapshot BuildModelResourceUsageSnapshot(
        ModelManagerModelItem model,
        RuntimeHardwareSnapshot hardware,
        SystemResourceUsageSnapshot systemSnapshot,
        int runningModelCount,
        int totalActiveJobs)
    {
        IReadOnlyList<string> runtimeDeviceIds = GetModelRuntimeDeviceIds(model);
        IReadOnlyList<string> assignedDeviceIds = runtimeDeviceIds.Count > 0 ? runtimeDeviceIds : GetModelAssignedDeviceIds(model);
        IReadOnlyList<string> usageDeviceIds = model.IsRunning ? assignedDeviceIds : [];
        RuntimeGpuSnapshot[] scopedGpus = FindRuntimeGpus(hardware, usageDeviceIds).ToArray();

        double gpuUsage = scopedGpus.Length == 0 ? 0 : AveragePercent(scopedGpus.Select(gpu => gpu.GpuUsagePercent));
        double vramUsage = scopedGpus.Length == 0 ? 0 : AveragePercent(scopedGpus.Select(gpu => (double?)gpu.EffectiveVramUsagePercent));
        long vramUsed = scopedGpus.Sum(gpu => gpu.EffectiveMemoryUsedBytes);
        long vramTotal = scopedGpus.Sum(gpu => gpu.EffectiveMemoryTotalBytes);

        double cpuUsage = 0;
        double ramUsage = 0;
        long ramUsed = 0;
        long ramTotal = systemSnapshot.RamTotalBytes;
        if (model.IsRunning)
        {
            int activeJobs = model.ActiveJobCount;
            if (totalActiveJobs > 0 && activeJobs > 0)
                cpuUsage = ClampPercent(systemSnapshot.CpuUsagePercent * activeJobs / totalActiveJobs);
            ramUsage = ClampPercent(systemSnapshot.RamUsagePercent / runningModelCount);
            ramUsed = runningModelCount <= 0 ? 0 : systemSnapshot.RamUsedBytes / runningModelCount;
        }

        string scope = BuildModelResourceScopeText(model, runtimeDeviceIds, assignedDeviceIds, scopedGpus.Length > 0 || usageDeviceIds.Count == 0);
        return new SystemResourceUsageSnapshot(DateTimeOffset.UtcNow, cpuUsage, ramUsage, gpuUsage, vramUsage, ramUsed, ramTotal, vramUsed, vramTotal, scope, "Model/GPU");
    }

    private IReadOnlyList<string> GetModelRuntimeDeviceIds(ModelManagerModelItem model)
    {
        var ids = new List<string>();
        void Add(string? value)
        {
            string normalized = NormalizeDeviceIdForDisplay(value);
            if (!string.IsNullOrWhiteSpace(normalized) && !ids.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                ids.Add(normalized);
        }

        foreach (RuntimeLoadedInstance instance in model.LoadedInstances)
        {
            Add(instance.DeviceId);
            if (instance.Config != null)
            {
                foreach (string target in instance.Config.TargetDeviceIds)
                    Add(target);
            }
        }

        if (ids.Count == 0 && _settingsByModel.TryGetValue(model.Key, out ModelManagerLoadSettings? settings))
        {
            foreach (string target in settings.TargetDeviceIds)
                Add(target);
        }

        return ids;
    }

    private IReadOnlyList<string> GetModelAssignedDeviceIds(ModelManagerModelItem model) =>
        _gpuConfigurations
            .Where(gpu => IsModelAssignedToGpu(model, gpu))
            .Select(gpu => NormalizeDeviceIdForDisplay(gpu.DeviceId))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<RuntimeGpuSnapshot> FindRuntimeGpus(RuntimeHardwareSnapshot hardware, IReadOnlyList<string> deviceIds)
    {
        if (deviceIds.Count == 0)
            yield break;

        for (int index = 0; index < hardware.Gpus.Count; index++)
        {
            RuntimeGpuSnapshot gpu = hardware.Gpus[index];
            string fallbackId = "cuda:" + index.ToString(CultureInfo.InvariantCulture);
            if (deviceIds.Any(deviceId => DeviceMatches(deviceId, gpu.DeviceId) || DeviceMatches(deviceId, fallbackId)))
                yield return gpu;
        }
    }

    private static string BuildModelResourceScopeText(
        ModelManagerModelItem model,
        IReadOnlyList<string> runtimeDeviceIds,
        IReadOnlyList<string> assignedDeviceIds,
        bool telemetryMatched)
    {
        string devices = assignedDeviceIds.Count == 0 ? "" : string.Join(", ", assignedDeviceIds);
        if (model.IsRunning)
        {
            if (!string.IsNullOrWhiteSpace(devices))
                return "running on " + devices + (telemetryMatched ? "" : " (waiting for GPU telemetry)");
            return "running CPU/local";
        }

        if (!string.IsNullOrWhiteSpace(devices))
            return "idle; assigned to " + devices;
        if (runtimeDeviceIds.Count > 0)
            return "idle; configured for " + string.Join(", ", runtimeDeviceIds);
        return "idle; no GPU assigned";
    }

    private static double AveragePercent(IEnumerable<double?> values)
    {
        double[] percents = values
            .Where(value => value.HasValue)
            .Select(value => ClampPercent(value.GetValueOrDefault()))
            .ToArray();
        return percents.Length == 0 ? 0 : percents.Average();
    }

    private double SampleCpuUsagePercent()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            return 0;

        ulong idleTime = idle.ToUInt64();
        ulong kernelTime = kernel.ToUInt64();
        ulong userTime = user.ToUInt64();
        if (!_hasLastCpuSample)
        {
            _lastCpuIdleTime = idleTime;
            _lastCpuKernelTime = kernelTime;
            _lastCpuUserTime = userTime;
            _hasLastCpuSample = true;
            return 0;
        }

        ulong idleDelta = idleTime - _lastCpuIdleTime;
        ulong kernelDelta = kernelTime - _lastCpuKernelTime;
        ulong userDelta = userTime - _lastCpuUserTime;
        ulong total = kernelDelta + userDelta;
        _lastCpuIdleTime = idleTime;
        _lastCpuKernelTime = kernelTime;
        _lastCpuUserTime = userTime;
        return total == 0 ? 0 : ClampPercent((total - idleDelta) * 100.0 / total);
    }

    private static (double Percent, long UsedBytes, long TotalBytes) ReadMemoryUsage()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status) || status.ullTotalPhys == 0)
            return (0, 0, 0);

        ulong used = status.ullTotalPhys - status.ullAvailPhys;
        return (ClampPercent(used * 100.0 / status.ullTotalPhys), (long)Math.Min(used, long.MaxValue), (long)Math.Min(status.ullTotalPhys, long.MaxValue));
    }

    private void AttachGpuConfigurationItem(GpuConfigurationItem item)
    {
        item.PropertyChanged -= GpuConfigurationItem_PropertyChanged;
        item.PropertyChanged += GpuConfigurationItem_PropertyChanged;
    }

    private void GpuConfigurationItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingGpuConfiguration || sender is not GpuConfigurationItem)
            return;

        string property = e.PropertyName ?? "";
        if (property is not (nameof(GpuConfigurationItem.Enabled) or
                             nameof(GpuConfigurationItem.MaxGpuLoadPercent) or
                             nameof(GpuConfigurationItem.MaxVramUsagePercent) or
                             nameof(GpuConfigurationItem.ParallelismMode) or
                             nameof(GpuConfigurationItem.ParallelismPlacement) or
                             nameof(GpuConfigurationItem.AssignedModelsText) or
                             nameof(GpuConfigurationItem.DisabledModelsText)))
            return;

        ApplyGpuConfigurationToModelSettings(showStatus: false);
        if (property == nameof(GpuConfigurationItem.Enabled))
            RefreshSelectedGpuAssignments();
        SetStatus("GPU configuration saved.");
    }

    private void UpdateSelectedGpuConfiguration()
    {
        GpuConfigurationItem? selected = GpuSelectorComboBox.SelectedItem as GpuConfigurationItem ?? _gpuConfigurations.FirstOrDefault();
        GpuSelectedConfigurationPanel.DataContext = selected;
        RefreshSelectedGpuAssignments();
    }

    private void RefreshSelectedGpuAssignments()
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(RefreshSelectedGpuAssignments);
            return;
        }

        _refreshingGpuAssignments = true;
        try
        {
            foreach (GpuModelAssignmentItem assignment in _gpuModelAssignments)
                assignment.PropertyChanged -= GpuModelAssignment_PropertyChanged;
            _gpuModelAssignments.Clear();

            if (GpuSelectorComboBox.SelectedItem is not GpuConfigurationItem gpu)
            {
                GpuAssignmentEmptyText.Text = "Select a GPU to edit model placement. Models enabled on the Models tab are checked by default unless this GPU has an override.";
                return;
            }

            foreach (ModelManagerModelItem model in _models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var assignment = GpuModelAssignmentItem.FromModel(model, IsModelAssignedToGpu(model, gpu));
                assignment.PropertyChanged += GpuModelAssignment_PropertyChanged;
                _gpuModelAssignments.Add(assignment);
            }

            GpuAssignmentEmptyText.Text = _gpuModelAssignments.Count == 0
                ? "No models are available yet. Refresh the Models tab after scanning or downloading models."
                : "Assignments save instantly. Enabled Models-tab entries are checked by default for this GPU unless you uncheck them here.";
        }
        finally
        {
            _refreshingGpuAssignments = false;
        }
    }

    private void GpuModelAssignment_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_refreshingGpuAssignments || _applyingGpuConfiguration || e.PropertyName != nameof(GpuModelAssignmentItem.IsAssigned))
            return;

        SaveSelectedGpuAssignmentsFromList();
    }

    private string BuildActiveModelsForGpu(string deviceId)
    {
        var active = _models
            .Where(model => model.LoadedInstances.Any(instance => DeviceMatches(instance.DeviceId, deviceId) || instance.Config?.TargetDeviceIds.Any(target => DeviceMatches(target, deviceId)) == true))
            .Select(model => model.Key)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return active.Length == 0 ? "" : string.Join(", ", active);
    }

    private string BuildConfiguredModelsForGpu(string deviceId)
    {
        var assigned = _settingsByModel
            .Where(pair => pair.Value.TargetDeviceIds.Any(target => DeviceMatches(target, deviceId)))
            .Select(pair => pair.Key)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return assigned.Length == 0 ? "" : string.Join(", ", assigned);
    }

    private void RefreshGpuAssignmentsFromModels()
    {
        foreach (GpuConfigurationItem gpu in _gpuConfigurations)
        {
            gpu.ActiveModelsText = BuildActiveModelsForGpu(gpu.DeviceId);
        }

        RefreshSelectedGpuAssignments();
    }

    private void ApplyGlobalGpuSettingsToPanel(GpuGlobalSettings settings)
    {
        if (!_viewReady)
            return;

        SetComboByTag(GlobalParallelismModeBox, NormalizeParallelismMode(settings.ParallelismMode));
        SetComboByTag(GlobalParallelismPlacementBox, NormalizeParallelismPlacement(settings.ParallelismPlacement));
        GlobalTargetDeviceIdsBox.Text = string.Join(", ", settings.TargetDeviceIds ?? []);
        GlobalMaxGpuLoadPercentBox.Text = ClampPercent(settings.MaxGpuLoadPercent).ToString(CultureInfo.InvariantCulture);
        GlobalMaxVramUsagePercentBox.Text = ClampPercent(settings.MaxVramUsagePercent).ToString(CultureInfo.InvariantCulture);
        SetComboByTag(GlobalVideoDeviceMapBox, NormalizeVideoDeviceMap(settings.VideoDeviceMap));
        GlobalVideoAllowCpuOffloadBox.IsChecked = settings.VideoAllowCpuOffload;
        GlobalVideoOffloadFolderBox.Text = settings.VideoOffloadFolder;
        GlobalVideoDisableCudaGuardBox.IsChecked = settings.VideoDisableCudaMemoryGuard;
        GlobalVideoCudaReserveMbBox.Text = Math.Max(0, settings.VideoCudaMemoryReserveMb).ToString(CultureInfo.InvariantCulture);
        GlobalVideoCpuMaxMemoryBox.Text = settings.VideoCpuMaxMemory;
        GlobalVideoMemorySavingBox.IsChecked = settings.VideoMemorySaving;
    }

    private GpuGlobalSettings ReadGlobalGpuSettingsFromPanel() => new()
    {
        ParallelismMode = NormalizeParallelismMode(GlobalParallelismModeBox.SelectedItem is ComboBoxItem mode ? mode.Tag?.ToString() ?? "single" : "single"),
        ParallelismPlacement = NormalizeParallelismPlacement(GlobalParallelismPlacementBox.SelectedItem is ComboBoxItem placement ? placement.Tag?.ToString() ?? "local" : "local"),
        TargetDeviceIds = ParseDelimitedList(GlobalTargetDeviceIdsBox.Text),
        MaxGpuLoadPercent = ClampPercent(ParseInt(GlobalMaxGpuLoadPercentBox.Text, 100)),
        MaxVramUsagePercent = ClampPercent(ParseInt(GlobalMaxVramUsagePercentBox.Text, 100)),
        VideoDeviceMap = NormalizeVideoDeviceMap(GlobalVideoDeviceMapBox.SelectedItem is ComboBoxItem map ? map.Tag?.ToString() ?? "" : ""),
        VideoAllowCpuOffload = GlobalVideoAllowCpuOffloadBox.IsChecked.GetValueOrDefault(),
        VideoOffloadFolder = (GlobalVideoOffloadFolderBox.Text ?? "").Trim(),
        VideoDisableCudaMemoryGuard = GlobalVideoDisableCudaGuardBox.IsChecked.GetValueOrDefault(),
        VideoCudaMemoryReserveMb = Math.Max(0, ParseInt(GlobalVideoCudaReserveMbBox.Text, 1024)),
        VideoCpuMaxMemory = (GlobalVideoCpuMaxMemoryBox.Text ?? "").Trim(),
        VideoMemorySaving = GlobalVideoMemorySavingBox.IsChecked.GetValueOrDefault()
    };

    private void SaveGlobalGpuDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        _globalGpuSettings = ReadGlobalGpuSettingsFromPanel();
        SaveSavedGpuSettings();
        SetStatus("Saved GPU defaults.");
    }

    private void SaveGlobalGpuOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        _globalGpuSettings = ReadGlobalGpuSettingsFromPanel();
        ApplyGlobalGpuSettingsOverride(_globalGpuSettings);
        SaveSavedGpuSettings();
        SaveSavedSettings();
        RefreshGpuAssignmentsFromModels();
        if (ModelsListBox.SelectedItem is ModelManagerModelItem selected)
        {
            _loadingSelectedSettings = true;
            try
            {
                ApplySettingsToPanel(GetSettings(selected));
            }
            finally
            {
                _loadingSelectedSettings = false;
            }
        }
        SetStatus("Applied GPU override to all model load settings.");
    }

    private void ApplyGlobalGpuSettingsOverride(GpuGlobalSettings global)
    {
        _applyingGpuConfiguration = true;
        try
        {
            foreach (GpuConfigurationItem gpu in _gpuConfigurations)
            {
                gpu.MaxGpuLoadPercent = global.MaxGpuLoadPercent;
                gpu.MaxVramUsagePercent = global.MaxVramUsagePercent;
                gpu.ParallelismMode = global.ParallelismMode;
                gpu.ParallelismPlacement = global.ParallelismPlacement;
            }
        }
        finally
        {
            _applyingGpuConfiguration = false;
        }

        IReadOnlyList<string> targetDevices = ResolveGlobalTargetDeviceIds(global);
        foreach (ModelManagerModelItem model in _models)
        {
            ModelManagerLoadSettings settings = GetSettings(model);
            ApplyGlobalModelPlacement(settings, global, targetDevices, overwrite: true);
            model.ApplyLoadSettings(settings);
            _settingsByModel[model.Key] = settings;
        }

        foreach (KeyValuePair<string, ModelManagerLoadSettings> pair in _settingsByModel.ToArray())
        {
            if (_models.Any(model => string.Equals(model.Key, pair.Key, StringComparison.OrdinalIgnoreCase)))
                continue;
            ApplyGlobalModelPlacement(pair.Value, global, targetDevices, overwrite: true);
        }
    }

    private IReadOnlyList<string> ResolveGlobalTargetDeviceIds(GpuGlobalSettings global)
    {
        IReadOnlyList<string> configured = global.TargetDeviceIds ?? [];
        if (configured.Count > 0)
            return configured;
        return _gpuConfigurations
            .Where(gpu => gpu.Enabled)
            .Select(gpu => gpu.DeviceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ApplyGlobalModelPlacement(ModelManagerLoadSettings settings, GpuGlobalSettings global, IReadOnlyList<string> targetDevices, bool overwrite)
    {
        if (settings == null || global == null)
            return;

        if (overwrite || settings.TargetDeviceIds.Count == 0)
            settings.TargetDeviceIds = targetDevices.ToArray();
        if (overwrite || string.IsNullOrWhiteSpace(settings.ParallelismMode) || settings.ParallelismMode == "single")
            settings.ParallelismMode = NormalizeParallelismMode(global.ParallelismMode);
        if (overwrite || string.IsNullOrWhiteSpace(settings.ParallelismPlacement) || settings.ParallelismPlacement == "local")
            settings.ParallelismPlacement = NormalizeParallelismPlacement(global.ParallelismPlacement);
        if (overwrite || settings.MaxGpuLoadPercent == 100)
            settings.MaxGpuLoadPercent = ClampPercent(global.MaxGpuLoadPercent);
        if (overwrite || settings.MaxVramUsagePercent == 100)
            settings.MaxVramUsagePercent = ClampPercent(global.MaxVramUsagePercent);
        settings.DataParallelReplicaCount = settings.ParallelismMode == "data_parallel" ? Math.Max(1, settings.TargetDeviceIds.Count) : 1;
        settings.PipelineStageCount = settings.ParallelismMode == "pipeline_parallel" ? Math.Max(1, settings.TargetDeviceIds.Count) : 1;
        ApplyGlobalVideoSettings(settings, global, overwrite);
    }

    private static void ApplyGlobalVideoSettings(ModelManagerLoadSettings settings, GpuGlobalSettings global, bool overwrite)
    {
        if (settings == null || global == null)
            return;

        if (overwrite || string.IsNullOrWhiteSpace(settings.VideoDeviceMap))
            settings.VideoDeviceMap = NormalizeVideoDeviceMap(global.VideoDeviceMap);
        if (overwrite || !settings.VideoAllowCpuOffload)
            settings.VideoAllowCpuOffload = global.VideoAllowCpuOffload;
        if (overwrite || string.IsNullOrWhiteSpace(settings.VideoOffloadFolder))
            settings.VideoOffloadFolder = global.VideoOffloadFolder ?? "";
        if (overwrite || !settings.VideoDisableCudaMemoryGuard)
            settings.VideoDisableCudaMemoryGuard = global.VideoDisableCudaMemoryGuard;
        if (overwrite || settings.VideoCudaMemoryReserveMb == 1024)
            settings.VideoCudaMemoryReserveMb = Math.Max(0, global.VideoCudaMemoryReserveMb);
        if (overwrite || string.IsNullOrWhiteSpace(settings.VideoCpuMaxMemory))
            settings.VideoCpuMaxMemory = global.VideoCpuMaxMemory ?? "";
        if (overwrite || !settings.VideoMemorySaving)
            settings.VideoMemorySaving = global.VideoMemorySaving;
    }

    private bool IsModelAssignedToGpu(ModelManagerModelItem model, GpuConfigurationItem gpu)
    {
        if (model == null || gpu == null || string.IsNullOrWhiteSpace(gpu.DeviceId))
            return false;

        if (ModelKeyInList(gpu.DisabledModelsText, model))
            return false;
        if (ModelKeyInList(gpu.AssignedModelsText, model))
            return true;
        if (_settingsByModel.TryGetValue(model.Key, out ModelManagerLoadSettings? settings))
        {
            if (settings.TargetDeviceIds.Any(target => DeviceMatches(target, gpu.DeviceId)))
                return true;
            if (settings.Enabled && settings.TargetDeviceIds.Count == 0 && gpu.Enabled)
                return true;
        }

        return model.LoadedInstances.Any(instance =>
            DeviceMatches(instance.DeviceId, gpu.DeviceId) ||
            instance.Config?.TargetDeviceIds.Any(target => DeviceMatches(target, gpu.DeviceId)) == true);
    }

    private bool IsModelDefaultAssignedToGpu(ModelManagerModelItem model, GpuConfigurationItem gpu)
    {
        if (!_settingsByModel.TryGetValue(model.Key, out ModelManagerLoadSettings? settings))
            return false;
        return settings.Enabled &&
               settings.TargetDeviceIds.Count == 0 &&
               gpu.Enabled &&
               !ModelKeyInList(gpu.DisabledModelsText, model);
    }

    private static bool ModelKeyInList(string text, ModelManagerModelItem model) =>
        ParseDelimitedList(text).Any(key => ModelSelectionMatches(model, key));

    private IReadOnlyList<string> GetAssignedModelKeysForGpu(GpuConfigurationItem gpu)
    {
        return _models
            .Where(model => IsModelAssignedToGpu(model, gpu))
            .Select(model => model.Key)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SaveSelectedGpuAssignmentsFromList()
    {
        if (_refreshingGpuAssignments || GpuSelectorComboBox.SelectedItem is not GpuConfigurationItem gpu)
            return;

        var assigned = new List<string>();
        var disabled = new List<string>();
        foreach (GpuModelAssignmentItem assignment in _gpuModelAssignments)
        {
            ModelManagerModelItem? model = _models.FirstOrDefault(item => ModelSelectionMatches(item, assignment.ModelKey));
            if (model == null || string.IsNullOrWhiteSpace(model.Key))
                continue;

            if (assignment.IsAssigned)
            {
                assigned.Add(model.Key);
                continue;
            }

            if (IsModelDefaultAssignedToGpu(model, gpu) ||
                ModelKeyInList(gpu.AssignedModelsText, model) ||
                _settingsByModel.TryGetValue(model.Key, out ModelManagerLoadSettings? settings) &&
                settings.TargetDeviceIds.Any(target => DeviceMatches(target, gpu.DeviceId)))
            {
                disabled.Add(model.Key);
            }
        }

        _applyingGpuConfiguration = true;
        try
        {
            gpu.AssignedModelsText = JoinDelimited(assigned);
            gpu.DisabledModelsText = JoinDelimited(disabled);
        }
        finally
        {
            _applyingGpuConfiguration = false;
        }

        ApplyGpuConfigurationToModelSettings(showStatus: false);
        SetStatus("Saved model assignments for " + gpu.DisplayName + ".");
    }

    private static string JoinDelimited(IEnumerable<string> values) =>
        string.Join(", ", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<GpuLoadPolicyPayload> BuildGpuLoadPolicies(ModelManagerLoadSettings settings)
    {
        var policies = new List<GpuLoadPolicyPayload>();
        foreach (string target in settings.TargetDeviceIds)
        {
            GpuConfigurationItem? gpu = _gpuConfigurations.FirstOrDefault(item => DeviceMatches(item.DeviceId, target));
            policies.Add(new GpuLoadPolicyPayload
            {
                device_id = target,
                enabled = gpu?.Enabled ?? true,
                max_gpu_load_percent = gpu?.MaxGpuLoadPercent ?? settings.MaxGpuLoadPercent,
                max_vram_usage_percent = gpu?.MaxVramUsagePercent ?? settings.MaxVramUsagePercent,
                parallelism_mode = NormalizeParallelismMode(gpu?.ParallelismMode ?? settings.ParallelismMode),
                parallelism_placement = NormalizeParallelismPlacement(gpu?.ParallelismPlacement ?? settings.ParallelismPlacement),
                assigned_models = gpu == null ? [] : GetAssignedModelKeysForGpu(gpu)
            });
        }

        return policies;
    }

    private void ApplyGpuConfigurationToModelSettings(bool showStatus = true)
    {
        if (_applyingGpuConfiguration)
            return;

        _applyingGpuConfiguration = true;
        var configuredGpuIds = _gpuConfigurations
            .Select(item => item.DeviceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        try
        {
            foreach (ModelManagerLoadSettings settings in _settingsByModel.Values)
            {
                settings.TargetDeviceIds = settings.TargetDeviceIds
                    .Where(target => !configuredGpuIds.Any(gpu => DeviceMatches(gpu, target)))
                    .ToArray();
            }

            var assignedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuConfigurationItem gpu in _gpuConfigurations)
            {
                if (!gpu.Enabled)
                    continue;

                IReadOnlyList<string> assignedModels = GetAssignedModelKeysForGpu(gpu);
                foreach (string modelKey in assignedModels)
                {
                    ModelManagerModelItem? model = _models.FirstOrDefault(item => ModelSelectionMatches(item, modelKey));
                    string key = model?.Key ?? modelKey;
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    ModelManagerLoadSettings settings = model == null
                        ? (_settingsByModel.TryGetValue(key, out ModelManagerLoadSettings? existing) ? existing : new ModelManagerLoadSettings())
                        : GetSettings(model);
                    settings.Enabled = true;
                    settings.ParallelismMode = NormalizeParallelismMode(gpu.ParallelismMode);
                    settings.ParallelismPlacement = NormalizeParallelismPlacement(gpu.ParallelismPlacement);
                    settings.MaxGpuLoadPercent = ClampPercent(gpu.MaxGpuLoadPercent);
                    settings.MaxVramUsagePercent = ClampPercent(gpu.MaxVramUsagePercent);
                    settings.TargetDeviceIds = settings.TargetDeviceIds
                        .Concat([gpu.DeviceId])
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    settings.DataParallelReplicaCount = settings.ParallelismMode == "data_parallel" ? Math.Max(1, settings.TargetDeviceIds.Count) : settings.DataParallelReplicaCount;
                    settings.PipelineStageCount = settings.ParallelismMode == "pipeline_parallel" ? Math.Max(1, settings.TargetDeviceIds.Count) : settings.PipelineStageCount;
                    _settingsByModel[key] = settings;
                    assignedCounts[key] = assignedCounts.TryGetValue(key, out int count) ? count + 1 : 1;
                    model?.ApplyLoadSettings(settings);
                }
            }

            foreach (ModelManagerModelItem model in _models)
            {
                if (!_settingsByModel.TryGetValue(model.Key, out ModelManagerLoadSettings? settings))
                    continue;
                if (configuredGpuIds.Length > 0 && settings.Enabled && !assignedCounts.ContainsKey(model.Key) && IsModelDisabledOnEveryEnabledGpu(model))
                    settings.Enabled = false;
                model.ApplyLoadSettings(settings);
            }

            if (ModelsListBox.SelectedItem is ModelManagerModelItem selected)
            {
                _loadingSelectedSettings = true;
                try
                {
                    ApplySettingsToPanel(GetSettings(selected));
                }
                finally
                {
                    _loadingSelectedSettings = false;
                }
            }

            SaveSavedSettings();
            SaveSavedGpuSettings();
            RefreshGpuAssignmentsFromModels();
            if (showStatus)
                SetStatus("GPU configuration applied to model load settings.");
        }
        finally
        {
            _applyingGpuConfiguration = false;
        }
    }

    private bool IsModelDisabledOnEveryEnabledGpu(ModelManagerModelItem model)
    {
        var enabledGpus = _gpuConfigurations.Where(gpu => gpu.Enabled).ToArray();
        return enabledGpus.Length > 0 && enabledGpus.All(gpu => ModelKeyInList(gpu.DisabledModelsText, model));
    }

    private static bool DeviceMatches(string? left, string? right)
    {
        string normalizedLeft = NormalizeDeviceIdForDisplay(left);
        string normalizedRight = NormalizeDeviceIdForDisplay(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDeviceIdForDisplay(string? value)
    {
        string normalized = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericDevice))
            return "cuda:" + numericDevice.ToString(CultureInfo.InvariantCulture);
        return normalized.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
    }

    private string GetRuntimeBaseUrl()
    {
        try
        {
            string? value = RuntimeBaseUrlProvider?.Invoke();
            return value?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static RuntimeModelSnapshot? TryFindRuntimeModel(IReadOnlyDictionary<string, RuntimeModelSnapshot> runtimeModels, LlmModelInfo model)
    {
        if (runtimeModels.TryGetValue(model.Key, out RuntimeModelSnapshot? exact))
            return exact;

        foreach (string alias in model.Aliases)
        {
            if (runtimeModels.TryGetValue(alias, out RuntimeModelSnapshot? byAlias))
                return byAlias;
        }

        return null;
    }

    private bool FilterModel(object item)
    {
        if (item is not ModelManagerModelItem model)
            return false;

        string filter = SelectedFilter();
        bool filterMatch = MatchesRuntimeFilter(model, filter);
        if (!filterMatch)
            return false;

        string query = (ModelSearchBox?.Text ?? "").Trim();
        return string.IsNullOrWhiteSpace(query) ||
               model.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRuntimeFilter(ModelManagerModelItem model, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) ||
            filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        string[] parts = filter.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return true;

        bool sourceMatches = parts[0].Equals("llmruntime", StringComparison.OrdinalIgnoreCase)
            ? model.IsLlmRuntime
            : parts[0].Equals("lmstudio", StringComparison.OrdinalIgnoreCase) && model.IsLmStudio;
        if (!sourceMatches)
            return false;

        return parts[1].ToLowerInvariant() switch
        {
            "running" => model.IsRunning,
            "idle" => !model.IsRunning,
            _ => true
        };
    }

    private string SelectedFilter()
    {
        if (ModelFilterBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return "all";
    }

    private void UpdateCounts()
    {
        RunningCountText.Text = _models.Count(item => item.IsRunning).ToString("N0");
        DownloadedCountText.Text = _models.Count(item => item.IsDownloaded).ToString("N0");
        LmStudioCountText.Text = _models.Count(item => item.IsLmStudio).ToString("N0");
        TotalCountText.Text = _models.Count.ToString("N0");
    }

    private void RestoreSelection(string preferredSelectionKey = "")
    {
        string key = FirstNonEmpty(preferredSelectionKey, _preferredSelectionKey, GetSelectedModelKey());
        if (!string.IsNullOrWhiteSpace(key))
        {
            ModelManagerModelItem? visibleMatch = _modelView.Cast<ModelManagerModelItem>().FirstOrDefault(item => ModelSelectionMatches(item, key));
            if (visibleMatch != null)
            {
                SelectModelItem(visibleMatch);
                return;
            }

            ModelManagerModelItem? hiddenMatch = _models.FirstOrDefault(item => ModelSelectionMatches(item, key));
            if (hiddenMatch != null)
            {
                _preferredSelectionKey = ModelSelectionKey(hiddenMatch);
                _suppressSelectionChanged = true;
                try
                {
                    ModelsListBox.SelectedItem = null;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
                ShowSelectedModel(hiddenMatch);
                return;
            }
        }

        ModelManagerModelItem? first = _modelView.Cast<ModelManagerModelItem>().FirstOrDefault();
        if (first != null)
            SelectModelItem(first);
        else
            ShowSelectedModel(null);
    }

    private void SelectModelItem(ModelManagerModelItem item)
    {
        _preferredSelectionKey = ModelSelectionKey(item);
        if (ReferenceEquals(ModelsListBox.SelectedItem, item))
        {
            ShowSelectedModel(item);
            return;
        }

        ModelsListBox.SelectedItem = item;
    }

    private string GetSelectedModelKey()
    {
        return ModelsListBox.SelectedItem is ModelManagerModelItem item ? ModelSelectionKey(item) : "";
    }

    private static string ModelSelectionKey(ModelManagerModelItem? item)
    {
        if (item == null)
            return "";
        return FirstNonEmpty(item.Key, item.LoadIdentifier, item.DisplayName, item.FilePath);
    }

    private static bool ModelSelectionMatches(ModelManagerModelItem item, string key)
    {
        key = (key ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(item.LoadIdentifier, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(item.DisplayName, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(item.FilePath, key, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(item.FilePath), key, StringComparison.OrdinalIgnoreCase);
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

    private void UpdateEmptyState()
    {
        bool anyVisible = _modelView.Cast<object>().Any();
        ModelsEmptyText.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSelectedModel(ModelManagerModelItem? item)
    {
        _loadingSelectedSettings = true;
        try
        {
            SaveModelSettingsButton.IsEnabled = item != null;
            ResetModelSettingsButton.IsEnabled = item != null;
            EnabledBox.IsEnabled = item?.CanRequestLoad == true;
            LoadModelButton.IsEnabled = item?.CanRequestLoad == true;
            UnloadModelButton.IsEnabled = item?.IsRunning == true;
            DeleteModelButton.IsEnabled = item?.IsDownloaded == true;
            DownloadBaseModelButton.IsEnabled = item?.RequiresMissingBaseModel == true;

            if (item == null)
            {
                SaveModelSettingsButton.IsEnabled = false;
                ResetModelSettingsButton.IsEnabled = false;
                LoadModelButton.IsEnabled = false;
                UnloadModelButton.IsEnabled = false;
                DeleteModelButton.IsEnabled = false;
                SelectedModelTitleText.Text = "Select a model";
                SelectedModelSubtitleText.Text = "Properties and load parameters appear here.";
                SelectedModelStatusText.Text = "-";
                SelectedModelSourceText.Text = "-";
                SelectedModelBaseModelPanel.Visibility = Visibility.Collapsed;
                SelectedModelHardwareWarningPanel.Visibility = Visibility.Collapsed;
                SelectedModelHardwareWarningText.Text = "";
                SelectedModelCredentialStatusText.Text = "No model credentials saved.";
                HuggingFaceTokenBox.Password = "";
                ModelApiKeyBox.Password = "";
                EnabledBox.IsChecked = false;
                IdleUnloadMinutesBox.Text = DefaultIdleUnloadMinutes.ToString();
                SelectedModelDetails.ItemsSource = Array.Empty<ModelManagerDetailRow>();
                return;
            }

            SelectedModelTitleText.Text = item.DisplayName;
            SelectedModelSubtitleText.Text = item.Key;
            SelectedModelStatusText.Text = item.StatusLabel;
            SelectedModelSourceText.Text = item.SourceLabel;
            SelectedModelDetails.ItemsSource = item.DetailRows;
            SelectedModelBaseModelPanel.Visibility = item.RequiresMissingBaseModel ? Visibility.Visible : Visibility.Collapsed;
            SelectedModelBaseModelText.Text = item.RequiresMissingBaseModel
                ? "This model requires " + (item.BaseModels.Count == 0 ? "a base model" : string.Join(", ", item.BaseModels)) + " before it can run."
                : "";
            SelectedModelHardwareWarningPanel.Visibility = item.HasHardwareWarning ? Visibility.Visible : Visibility.Collapsed;
            SelectedModelHardwareWarningText.Text = item.HasHardwareWarning ? item.HardwareWarningDetail : "";
            LlmModelConfig config = GetRegistry().GetModelConfig(item.Key);
            HuggingFaceTokenBox.Password = "";
            ModelApiKeyBox.Password = "";
            SelectedModelCredentialStatusText.Text = BuildCredentialStatus(config);
            ApplySettingsToPanel(GetSettings(item));
        }
        finally
        {
            _loadingSelectedSettings = false;
        }
    }

    private static string BuildCredentialStatus(LlmModelConfig config)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.HuggingFaceToken))
            parts.Add("Hugging Face token saved");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            parts.Add("API key saved");
        return parts.Count == 0 ? "No model credentials saved." : string.Join(" | ", parts) + ". Leave a field blank to keep the saved value.";
    }

    private ModelManagerLoadSettings GetSettings(ModelManagerModelItem item)
    {
        if (!_settingsByModel.TryGetValue(item.Key, out ModelManagerLoadSettings? settings))
        {
            settings = CreateDefaultModelSettings(item, enableIfDownloaded: false);
            _settingsByModel[item.Key] = settings;
        }

        return settings;
    }

    private ModelManagerLoadSettings CreateDefaultModelSettings(ModelManagerModelItem item, bool enableIfDownloaded)
    {
        ModelManagerLoadSettings settings = ModelManagerLoadSettings.CreateDefault(item);
        settings.Enabled = enableIfDownloaded && item.IsDownloaded && item.CanRequestLoad;
        ApplyGlobalModelPlacement(settings, _globalGpuSettings, ResolveGlobalTargetDeviceIds(_globalGpuSettings), overwrite: false);
        return settings;
    }

    private void TrackDownloadedModelForDefaultEnable(string path)
    {
        foreach (string token in BuildDownloadedModelTokens(path))
            _pendingDownloadedModelEnableTokens.Add(token);
    }

    private bool TryCreateDownloadedDefaultSettings(ModelManagerModelItem item, out ModelManagerLoadSettings settings)
    {
        settings = null!;
        if (_pendingDownloadedModelEnableTokens.Count == 0 || !item.IsDownloaded || !item.CanRequestLoad)
            return false;

        string[] candidates = BuildModelIdentityTokens(item).ToArray();
        if (!candidates.Any(candidate => _pendingDownloadedModelEnableTokens.Any(token => ModelIdentityTokenMatches(token, candidate))))
            return false;

        settings = CreateDefaultModelSettings(item, enableIfDownloaded: true);
        foreach (string candidate in candidates)
            _pendingDownloadedModelEnableTokens.RemoveWhere(token => ModelIdentityTokenMatches(token, candidate));
        return true;
    }

    private static IEnumerable<string> BuildDownloadedModelTokens(string path)
    {
        foreach (string value in BuildPathTokens(path))
            yield return value;
    }

    private static IEnumerable<string> BuildModelIdentityTokens(ModelManagerModelItem item)
    {
        foreach (string value in BuildPathTokens(item.Key))
            yield return value;
        foreach (string value in BuildPathTokens(item.LoadIdentifier))
            yield return value;
        foreach (string value in BuildPathTokens(item.FilePath))
            yield return value;
        foreach (string value in BuildPathTokens(item.DisplayName))
            yield return value;
    }

    private static IEnumerable<string> BuildPathTokens(string value)
    {
        value = (value ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var tokens = new List<string>
        {
            NormalizeModelIdentityToken(value)
        };

        try
        {
            string fullPath = Path.GetFullPath(value);
            tokens.Add(NormalizeModelIdentityToken(fullPath));
            tokens.Add(NormalizeModelIdentityToken(Path.GetFileName(fullPath)));
            tokens.Add(NormalizeModelIdentityToken(Path.GetFileNameWithoutExtension(fullPath)));
            string? parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
                tokens.Add(NormalizeModelIdentityToken(parent));
        }
        catch
        {
            tokens.Add(NormalizeModelIdentityToken(Path.GetFileName(value)));
            tokens.Add(NormalizeModelIdentityToken(Path.GetFileNameWithoutExtension(value)));
        }

        return tokens.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeModelIdentityToken(string value)
    {
        value = (value ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
    }

    private static bool ModelIdentityTokenMatches(string left, string right)
    {
        left = NormalizeModelIdentityToken(left);
        right = NormalizeModelIdentityToken(right);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        return left.Length > 8 &&
               right.Length > 8 &&
               (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
                right.Contains(left, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySettingsToPanel(ModelManagerLoadSettings settings)
    {
        SetComboByTag(BackendBox, settings.Backend);
        SetComboByTag(ParallelismModeBox, NormalizeParallelismMode(settings.ParallelismMode));
        SetComboByTag(ParallelismPlacementBox, NormalizeParallelismPlacement(settings.ParallelismPlacement));
        ContextLengthBox.Text = settings.ContextLength.ToString();
        EvalBatchBox.Text = settings.EvalBatchSize.ToString();
        GpuLayersBox.Text = settings.GpuLayerCount.ToString();
        TargetDeviceIdsBox.Text = string.Join(", ", settings.TargetDeviceIds);
        NetworkNodeIdsBox.Text = string.Join(", ", settings.NetworkNodeIds);
        MaxGpuLoadPercentBox.Text = settings.MaxGpuLoadPercent.ToString();
        MaxVramUsagePercentBox.Text = settings.MaxVramUsagePercent.ToString();
        IdleUnloadMinutesBox.Text = settings.IdleUnloadMinutes.ToString();
        EnabledBox.IsChecked = settings.Enabled;
        FlashAttentionBox.IsChecked = settings.FlashAttention;
        KvCacheGpuBox.IsChecked = settings.OffloadKvCacheToGpu;
        AllowFallbackBox.IsChecked = settings.AllowBackendFallback;
        SetComboByTag(VideoDeviceMapBox, NormalizeVideoDeviceMap(settings.VideoDeviceMap));
        VideoAllowCpuOffloadBox.IsChecked = settings.VideoAllowCpuOffload;
        VideoOffloadFolderBox.Text = settings.VideoOffloadFolder;
        VideoDisableCudaGuardBox.IsChecked = settings.VideoDisableCudaMemoryGuard;
        VideoCudaReserveMbBox.Text = settings.VideoCudaMemoryReserveMb.ToString(CultureInfo.InvariantCulture);
        VideoCpuMaxMemoryBox.Text = settings.VideoCpuMaxMemory;
        VideoMemorySavingBox.IsChecked = settings.VideoMemorySaving;
    }

    private static void SetComboByTag(ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void CaptureSettingsFromPanel()
    {
        if (_loadingSelectedSettings || ModelsListBox.SelectedItem is not ModelManagerModelItem item)
            return;

        ModelManagerLoadSettings settings = ReadPanelSettings(item);
        _settingsByModel[item.Key] = settings;
        item.ApplyLoadSettings(settings);
        RefreshGpuAssignmentsFromModels();
    }

    private ModelManagerLoadSettings ReadPanelSettings(ModelManagerModelItem item)
    {
        return new ModelManagerLoadSettings
        {
            Backend = BackendBox.SelectedItem is ComboBoxItem backendItem ? backendItem.Tag?.ToString() ?? "auto" : "auto",
            ParallelismMode = NormalizeParallelismMode(ParallelismModeBox.SelectedItem is ComboBoxItem modeItem ? modeItem.Tag?.ToString() ?? "single" : "single"),
            ParallelismPlacement = NormalizeParallelismPlacement(ParallelismPlacementBox.SelectedItem is ComboBoxItem placementItem ? placementItem.Tag?.ToString() ?? "local" : "local"),
            ContextLength = ParseUInt(ContextLengthBox.Text, item.MaxContextLength > 0 ? item.MaxContextLength : 8192),
            EvalBatchSize = ParseInt(EvalBatchBox.Text, 512),
            GpuLayerCount = ParseInt(GpuLayersBox.Text, -1),
            TargetDeviceIds = ParseDelimitedList(TargetDeviceIdsBox.Text),
            NetworkNodeIds = ParseDelimitedList(NetworkNodeIdsBox.Text),
            MaxGpuLoadPercent = ClampPercent(ParseInt(MaxGpuLoadPercentBox.Text, 100)),
            MaxVramUsagePercent = ClampPercent(ParseInt(MaxVramUsagePercentBox.Text, 100)),
            PipelineStageCount = NormalizeParallelismMode(ParallelismModeBox.SelectedItem is ComboBoxItem pipelineModeItem ? pipelineModeItem.Tag?.ToString() ?? "single" : "single") == "pipeline_parallel"
                ? Math.Max(1, ParseDelimitedList(TargetDeviceIdsBox.Text).Count)
                : 1,
            DataParallelReplicaCount = NormalizeParallelismMode(ParallelismModeBox.SelectedItem is ComboBoxItem replicaModeItem ? replicaModeItem.Tag?.ToString() ?? "single" : "single") == "data_parallel"
                ? Math.Max(1, ParseDelimitedList(TargetDeviceIdsBox.Text).Count)
                : 1,
            IdleUnloadMinutes = Math.Max(0, ParseInt(IdleUnloadMinutesBox.Text, DefaultIdleUnloadMinutes)),
            Enabled = EnabledBox.IsChecked.GetValueOrDefault(false),
            FlashAttention = FlashAttentionBox.IsChecked.GetValueOrDefault(),
            OffloadKvCacheToGpu = KvCacheGpuBox.IsChecked.GetValueOrDefault(),
            AllowBackendFallback = AllowFallbackBox.IsChecked.GetValueOrDefault(false),
            VideoDeviceMap = NormalizeVideoDeviceMap(VideoDeviceMapBox.SelectedItem is ComboBoxItem videoDeviceMapItem ? videoDeviceMapItem.Tag?.ToString() ?? "" : ""),
            VideoAllowCpuOffload = VideoAllowCpuOffloadBox.IsChecked.GetValueOrDefault(),
            VideoOffloadFolder = (VideoOffloadFolderBox.Text ?? "").Trim(),
            VideoDisableCudaMemoryGuard = VideoDisableCudaGuardBox.IsChecked.GetValueOrDefault(),
            VideoCudaMemoryReserveMb = Math.Max(0, ParseInt(VideoCudaReserveMbBox.Text, 1024)),
            VideoCpuMaxMemory = (VideoCpuMaxMemoryBox.Text ?? "").Trim(),
            VideoMemorySaving = VideoMemorySavingBox.IsChecked.GetValueOrDefault()
        };
    }

    private static uint ParseUInt(string text, uint fallback)
    {
        return uint.TryParse((text ?? "").Trim(), out uint value) && value > 0 ? value : fallback;
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse((text ?? "").Trim(), out int value) ? value : fallback;
    }

    private static IReadOnlyList<string> ParseDelimitedList(string text)
    {
        return (text ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private static double ClampPercent(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;

    private ModelManagerLoadSettings ApplyDefaultGpuTargets(ModelManagerModelItem item, ModelManagerLoadSettings settings)
    {
        if (!settings.Enabled || settings.TargetDeviceIds.Count > 0 || _gpuConfigurations.Count == 0)
            return settings;

        IReadOnlyList<string> globalTargetDeviceIds = _globalGpuSettings.TargetDeviceIds ?? [];
        GpuConfigurationItem[] eligibleGpus = _gpuConfigurations
            .Where(gpu => gpu.Enabled && !ModelKeyInList(gpu.DisabledModelsText, item))
            .Where(gpu => globalTargetDeviceIds.Count == 0 || globalTargetDeviceIds.Any(target => DeviceMatches(target, gpu.DeviceId)))
            .OrderBy(gpu => gpu.DeviceSortKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eligibleGpus.Length == 0)
            return settings;

        ModelManagerLoadSettings next = settings.Clone();
        next.TargetDeviceIds = eligibleGpus
            .Select(gpu => gpu.DeviceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        GpuConfigurationItem primary = eligibleGpus[0];
        next.ParallelismMode = NormalizeParallelismMode(FirstNonEmpty(_globalGpuSettings.ParallelismMode, primary.ParallelismMode));
        next.ParallelismPlacement = NormalizeParallelismPlacement(FirstNonEmpty(_globalGpuSettings.ParallelismPlacement, primary.ParallelismPlacement));
        next.MaxGpuLoadPercent = ClampPercent(_globalGpuSettings.MaxGpuLoadPercent);
        next.MaxVramUsagePercent = ClampPercent(_globalGpuSettings.MaxVramUsagePercent);
        ApplyGlobalVideoSettings(next, _globalGpuSettings, overwrite: false);
        next.DataParallelReplicaCount = next.ParallelismMode == "data_parallel" ? Math.Max(1, next.TargetDeviceIds.Count) : next.DataParallelReplicaCount;
        next.PipelineStageCount = next.ParallelismMode == "pipeline_parallel" ? Math.Max(1, next.TargetDeviceIds.Count) : next.PipelineStageCount;
        return next;
    }

    private async Task LoadModelAsync(string? modelOverride = null)
    {
        ModelManagerModelItem? item = ModelsListBox.SelectedItem as ModelManagerModelItem;
        string model = string.IsNullOrWhiteSpace(modelOverride) ? item?.LoadIdentifier ?? "" : modelOverride.Trim();
        string selectionKey = FirstNonEmpty(item == null ? "" : ModelSelectionKey(item), model, Path.GetFileName(model));
        if (string.IsNullOrWhiteSpace(model))
        {
            SetStatus("Select a model before loading.");
            return;
        }
        if (item != null && !item.CanRequestLoad)
        {
            string reason = item.RequiresMissingBaseModel
                ? "This model is missing its base model."
                : string.IsNullOrWhiteSpace(item.LoadDisabledReason)
                    ? "This model is not loadable by LlmRuntime."
                    : item.LoadDisabledReason;
            SetStatus("Load blocked: " + reason);
            return;
        }

        ModelManagerLoadSettings settings = item == null ? new ModelManagerLoadSettings() : ApplyDefaultGpuTargets(item, ReadPanelSettings(item));
        if (item != null)
            _settingsByModel[item.Key] = settings;
        SaveSavedSettings();

        BeginSelectedModelAction(item, "Loading");
        SetStatus("Loading model into LlmRuntime: " + model);
        bool loaded = false;
        try
        {
            if (EnsureRuntimeStartedAsync != null)
                await EnsureRuntimeStartedAsync().ConfigureAwait(true);

            string baseUrl = GetRuntimeBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("No LlmRuntime endpoint is configured.");

            string responseBody = await PostLoadRequestAsync(baseUrl, model, settings).ConfigureAwait(true);
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException(ReadErrorMessage(error));

            SetStatus("Loaded model into LlmRuntime: " + model);
            string service = ReadString(document.RootElement, "chat_service") ??
                             ReadString(document.RootElement, "chatService") ??
                             ReadString(document.RootElement, "service") ??
                             item?.SuggestedChatService ??
                             "";
            if (!string.IsNullOrWhiteSpace(service))
                ChatServiceSuggested?.Invoke(service);

            loaded = true;
            ApplySelectedModelOptimisticState(item, isRunning: true);
            QueueModelInventoryRefresh(selectionKey);
        }
        catch (Exception ex)
        {
            SetStatus("Load failed: " + ex.Message);
        }
        finally
        {
            if (!loaded)
                EndSelectedModelAction(item);
        }
    }

    private async Task<string> PostLoadRequestAsync(string baseUrl, string model, ModelManagerLoadSettings settings)
    {
        IReadOnlyList<GpuLoadPolicyPayload> gpuPolicies = BuildGpuLoadPolicies(settings);
        object payload = new
        {
            model,
            backend = settings.Backend,
            context_length = settings.ContextLength,
            eval_batch_size = settings.EvalBatchSize,
            gpu_layer_count = settings.GpuLayerCount,
            flash_attention = settings.FlashAttention,
            offload_kv_cache_to_gpu = settings.OffloadKvCacheToGpu,
            allow_backend_fallback = settings.AllowBackendFallback,
            parallelism_mode = settings.ParallelismMode,
            parallelism_placement = settings.ParallelismPlacement,
            target_device_ids = settings.TargetDeviceIds,
            gpu_device_ids = settings.TargetDeviceIds,
            network_node_ids = settings.NetworkNodeIds,
            gpu_load_threshold_percent = settings.MaxGpuLoadPercent,
            max_gpu_load_percent = settings.MaxGpuLoadPercent,
            max_vram_usage_percent = settings.MaxVramUsagePercent,
            pipeline_stage_count = settings.PipelineStageCount,
            data_parallel_replicas = settings.DataParallelReplicaCount,
            video_device_map = settings.VideoDeviceMap,
            video_allow_cpu_offload = settings.VideoAllowCpuOffload,
            video_offload_folder = settings.VideoOffloadFolder,
            video_disable_cuda_memory_guard = settings.VideoDisableCudaMemoryGuard,
            video_cuda_memory_reserve_mb = settings.VideoCudaMemoryReserveMb,
            video_cpu_max_memory = settings.VideoCpuMaxMemory,
            video_memory_saving = settings.VideoMemorySaving,
            gpu_load_policies = gpuPolicies,
            echo_load_config = true
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await ApiClient.PostAsync(baseUrl.TrimEnd('/') + "/api/v1/models/load", content).ConfigureAwait(true);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + (string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body));
        return body;
    }

    private async Task UnloadSelectedModelAsync()
    {
        if (ModelsListBox.SelectedItem is not ModelManagerModelItem item)
            return;

        string selectionKey = ModelSelectionKey(item);
        BeginSelectedModelAction(item, "Unloading");
        string model = string.IsNullOrWhiteSpace(item.Key) ? item.LoadIdentifier : item.Key;
        SetStatus("Unloading model from LlmRuntime: " + model);
        bool unloaded = false;
        try
        {
            if (EnsureRuntimeStartedAsync != null)
                await EnsureRuntimeStartedAsync().ConfigureAwait(true);

            string baseUrl = GetRuntimeBaseUrl();
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("No LlmRuntime endpoint is configured.");

            var payload = new { model };
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await ApiClient.PostAsync(baseUrl.TrimEnd('/') + "/api/v1/models/unload", content).ConfigureAwait(true);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + (string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body));

            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException(ReadErrorMessage(error));

            SetStatus("Unloaded model from LlmRuntime: " + model);
            unloaded = true;
            ApplySelectedModelOptimisticState(item, isRunning: false);
            QueueModelInventoryRefresh(selectionKey);
        }
        catch (Exception ex)
        {
            SetStatus("Unload failed: " + ex.Message);
        }
        finally
        {
            if (!unloaded)
                EndSelectedModelAction(item);
        }
    }

    private void BeginSelectedModelAction(ModelManagerModelItem? item, string action)
    {
        SaveModelSettingsButton.IsEnabled = false;
        ResetModelSettingsButton.IsEnabled = false;
        LoadModelButton.IsEnabled = false;
        UnloadModelButton.IsEnabled = false;
        DeleteModelButton.IsEnabled = false;
        DownloadBaseModelButton.IsEnabled = false;
        if (action.Equals("Loading", StringComparison.OrdinalIgnoreCase))
            LoadModelButton.Content = "Loading...";
        else if (action.Equals("Unloading", StringComparison.OrdinalIgnoreCase))
            UnloadModelButton.Content = "Unloading...";
        if (item != null)
            SelectedModelStatusText.Text = action + "...";
    }

    private void EndSelectedModelAction(ModelManagerModelItem? item)
    {
        LoadModelButton.Content = "Load";
        UnloadModelButton.Content = "Unload";
        ShowSelectedModel(item ?? (ModelsListBox.SelectedItem as ModelManagerModelItem));
    }

    private void ApplySelectedModelOptimisticState(ModelManagerModelItem? item, bool isRunning)
    {
        LoadModelButton.Content = "Load";
        UnloadModelButton.Content = "Unload";
        SaveModelSettingsButton.IsEnabled = item != null;
        ResetModelSettingsButton.IsEnabled = item != null;
        EnabledBox.IsEnabled = item?.CanRequestLoad == true;
        LoadModelButton.IsEnabled = item?.CanRequestLoad == true && !isRunning;
        UnloadModelButton.IsEnabled = isRunning;
        DeleteModelButton.IsEnabled = item?.IsDownloaded == true;
        DownloadBaseModelButton.IsEnabled = item?.RequiresMissingBaseModel == true;
        if (item != null)
        {
            SelectedModelStatusText.Text = isRunning ? "Running" : item.IsDownloaded ? "Downloaded" : item.CanRequestLoad ? "Available" : "Unsupported";
            SelectedModelDetails.ItemsSource = item.DetailRows;
        }
    }

    private void QueueModelInventoryRefresh(string selectionKey)
    {
        _preferredSelectionKey = FirstNonEmpty(selectionKey, _preferredSelectionKey);
        _ = RefreshModelsAsync(_preferredSelectionKey);
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "Unknown error.";
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out JsonElement message) &&
            message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "Unknown error.";
        return error.ToString();
    }

    private void LoadSavedSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return;

            var settings = JsonSerializer.Deserialize<Dictionary<string, ModelManagerLoadSettings>>(File.ReadAllText(path), JsonOptions);
            if (settings == null)
                return;

            _settingsByModel.Clear();
            foreach (KeyValuePair<string, ModelManagerLoadSettings> pair in settings)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                    _settingsByModel[pair.Key] = pair.Value;
            }
        }
        catch
        {
        }
        finally
        {
            _savedSettingsLoaded = true;
        }
    }

    private void SaveSavedSettings()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(_settingsByModel, JsonOptions));
        }
        catch (Exception ex)
        {
            SetStatus("Could not save model settings: " + ex.Message);
        }
    }

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "model-manager-settings.json");

    private void LoadSavedGpuSettings()
    {
        try
        {
            string path = GpuSettingsPath;
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            GpuConfigurationItem[]? settings = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    (document.RootElement.TryGetProperty("gpus", out _) || document.RootElement.TryGetProperty("global", out _)))
                {
                    GpuSettingsFile? file = JsonSerializer.Deserialize<GpuSettingsFile>(json, JsonOptions);
                    _globalGpuSettings = file?.Global ?? new GpuGlobalSettings();
                    settings = file?.Gpus;
                }
                else
                {
                    settings = JsonSerializer.Deserialize<GpuConfigurationItem[]>(json, JsonOptions);
                }
            }
            catch
            {
                settings = JsonSerializer.Deserialize<GpuConfigurationItem[]>(json, JsonOptions);
            }

            if (settings == null)
                return;

            _gpuConfigurations.Clear();
            foreach (GpuConfigurationItem item in settings.Where(item => item != null && !string.IsNullOrWhiteSpace(item.DeviceId)))
            {
                AttachGpuConfigurationItem(item);
                _gpuConfigurations.Add(item);
            }
        }
        catch
        {
        }
    }

    private void SaveSavedGpuSettings()
    {
        try
        {
            string path = GpuSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new GpuSettingsFile
            {
                Global = _globalGpuSettings,
                Gpus = _gpuConfigurations.ToArray()
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            SetStatus("Could not save GPU settings: " + ex.Message);
        }
    }

    private static string GpuSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "model-manager-gpu-settings.json");

    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            TryBeginOnUi(() => SetStatus(text));
            return;
        }

        ManagerStatusText.Text = string.IsNullOrWhiteSpace(text) ? "Ready" : text;
        ManagerStatusText.Foreground = IsWarningStatus(ManagerStatusText.Text)
            ? new SolidColorBrush(Color.FromRgb(254, 205, 211))
            : (FindResource("ManagerSubtleTextBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(170, 180, 195)));
        StatusChanged?.Invoke(ManagerStatusText.Text);
    }

    private static bool IsWarningStatus(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        return text.Contains("warning") ||
               text.Contains("failed") ||
               text.Contains("disabled") ||
               text.Contains("cuda") ||
               text.Contains("pytorch") ||
               text.Contains("torch") ||
               text.Contains("python");
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserTabItem.Visibility = Visibility.Visible;
        ManagerTabs.SelectedItem = BrowserTabItem;
        OpenBrowserButton.IsEnabled = false;
    }

    private void CloseBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        ModelBrowserControl.SaveCurrentPage();
        ManagerTabs.SelectedItem = ModelsTabItem;
        BrowserTabItem.Visibility = Visibility.Collapsed;
        OpenBrowserButton.IsEnabled = true;
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync().ConfigureAwait(true);

    private void BenchmarkAllModelsButton_Click(object sender, RoutedEventArgs e) => BenchmarkAllModelsRequested?.Invoke();

    private async void RefreshGpuConfigButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshGpuConfigurationAsync().ConfigureAwait(true);
        SetStatus("GPU configuration refreshed.");
    }

    private void ApplyGpuConfigButton_Click(object sender, RoutedEventArgs e) => ApplyGpuConfigurationToModelSettings();

    private void GpuSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedGpuConfiguration();

    private void ResetSelectedGpuButton_Click(object sender, RoutedEventArgs e)
    {
        if (GpuSelectorComboBox.SelectedItem is not GpuConfigurationItem gpu)
        {
            SetStatus("Select a GPU before resetting settings.");
            return;
        }

        _applyingGpuConfiguration = true;
        try
        {
            gpu.Enabled = true;
            gpu.MaxGpuLoadPercent = ClampPercent(_globalGpuSettings.MaxGpuLoadPercent);
            gpu.MaxVramUsagePercent = ClampPercent(_globalGpuSettings.MaxVramUsagePercent);
            gpu.ParallelismMode = NormalizeParallelismMode(_globalGpuSettings.ParallelismMode);
            gpu.ParallelismPlacement = NormalizeParallelismPlacement(_globalGpuSettings.ParallelismPlacement);
            gpu.AssignedModelsText = "";
            gpu.DisabledModelsText = "";
        }
        finally
        {
            _applyingGpuConfiguration = false;
        }

        ApplyGpuConfigurationToModelSettings(showStatus: false);
        RefreshSelectedGpuAssignments();
        SetStatus("Reset GPU settings for " + gpu.DisplayName + ".");
    }

    private void GpuModelAssignmentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingGpuAssignments || _applyingGpuConfiguration)
            return;

        SaveSelectedGpuAssignmentsFromList();
    }

    private void BenchmarkModelButton_Click(object sender, RoutedEventArgs e)
    {
        string model = "";
        ModelManagerModelItem? modelItem = null;
        if (sender is Button button)
        {
            model = button.CommandParameter?.ToString() ?? "";
            if (button.DataContext is ModelManagerModelItem item)
            {
                modelItem = item;
                if (string.IsNullOrWhiteSpace(model))
                    model = item.LoadIdentifier;
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            SetStatus("Select a model before benchmarking.");
            return;
        }

        SetStatus("Benchmark requested for " + model.Trim() + ".");
        if (ModelBenchmarkRequestedWithMetadata != null)
        {
            ModelBenchmarkRequestedWithMetadata.Invoke(new ModelManagerBenchmarkRequest
            {
                ModelId = model.Trim(),
                SuggestedChatService = modelItem?.SuggestedChatService ?? "",
                ModelType = modelItem?.Type ?? "",
                SourceLabel = modelItem?.SourceLabel ?? "",
                Tags = modelItem?.Tags ?? []
            });
            return;
        }

        ModelBenchmarkRequested?.Invoke(model.Trim());
    }

    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_viewReady)
            return;

        _modelView.Refresh();
        UpdateEmptyState();
    }

    private void ModelFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady)
            return;

        _modelView.Refresh();
        RestoreSelection();
        UpdateEmptyState();
    }

    private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged)
            return;
        if (_refreshingModels && ModelsListBox.SelectedItem == null)
            return;

        if (ModelsListBox.SelectedItem is ModelManagerModelItem item)
            _preferredSelectionKey = ModelSelectionKey(item);

        ShowSelectedModel(ModelsListBox.SelectedItem as ModelManagerModelItem);
    }

    private void LoadSetting_Changed(object sender, RoutedEventArgs e)
    {
        CaptureSettingsFromPanel();
        if (_savedSettingsLoaded && !_loadingSelectedSettings && (ReferenceEquals(sender, EnabledBox) || ReferenceEquals(sender, IdleUnloadMinutesBox)))
        {
            SaveSavedSettings();
            if (ReferenceEquals(sender, EnabledBox))
            {
                SetStatus(EnabledBox.IsChecked.GetValueOrDefault(false)
                    ? "Model enabled for dynamic web chat loading."
                    : "Model disabled for dynamic web chat loading.");
            }
            else
            {
                int minutes = Math.Max(0, ParseInt(IdleUnloadMinutesBox.Text, 0));
                SetStatus(minutes > 0
                    ? "Model idle unload set to " + minutes.ToString() + " minute(s)."
                    : "Model idle unload disabled.");
            }
        }
    }

    private void SaveModelSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureSettingsFromPanel();
        SaveSelectedModelConfig();
        SaveSavedSettings();
        SetStatus("Saved model settings.");
    }

    private void ResetModelSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsListBox.SelectedItem is not ModelManagerModelItem item)
        {
            SetStatus("Select a model before resetting settings.");
            return;
        }

        ModelManagerLoadSettings settings = CreateDefaultModelSettings(item, enableIfDownloaded: true);
        _settingsByModel[item.Key] = settings;
        _loadingSelectedSettings = true;
        try
        {
            ApplySettingsToPanel(settings);
        }
        finally
        {
            _loadingSelectedSettings = false;
        }
        item.ApplyLoadSettings(settings);
        SaveSavedSettings();
        RefreshGpuAssignmentsFromModels();
        SetStatus("Reset model settings for " + item.DisplayName + ".");
    }

    private async void LoadModelButton_Click(object sender, RoutedEventArgs e) => await LoadModelAsync().ConfigureAwait(true);

    private async void UnloadModelButton_Click(object sender, RoutedEventArgs e) => await UnloadSelectedModelAsync().ConfigureAwait(true);

    private async void DownloadBaseModelButton_Click(object sender, RoutedEventArgs e) => await DownloadSelectedBaseModelAsync().ConfigureAwait(true);

    private async void DeleteModelButton_Click(object sender, RoutedEventArgs e) => await DeleteSelectedModelAsync().ConfigureAwait(true);

    private void SaveSelectedModelConfig()
    {
        if (ModelsListBox.SelectedItem is not ModelManagerModelItem item)
            return;

        LlmModelRegistry registry = GetRegistry();
        LlmModelConfig existing = registry.GetModelConfig(item.Key);
        LlmModelConfig saved = registry.SaveModelConfig(item.Key, new LlmModelConfig
        {
            HuggingFaceToken = string.IsNullOrWhiteSpace(HuggingFaceTokenBox.Password) ? existing.HuggingFaceToken : HuggingFaceTokenBox.Password,
            ApiKey = string.IsNullOrWhiteSpace(ModelApiKeyBox.Password) ? existing.ApiKey : ModelApiKeyBox.Password,
            Notes = existing.Notes
        });
        HuggingFaceTokenBox.Password = "";
        ModelApiKeyBox.Password = "";
        SelectedModelCredentialStatusText.Text = BuildCredentialStatus(saved);
    }

    private async Task DownloadSelectedBaseModelAsync()
    {
        if (ModelsListBox.SelectedItem is not ModelManagerModelItem item || !item.RequiresMissingBaseModel)
            return;

        string baseModel = item.BaseModels.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            SetStatus("No base model id was found in this model's metadata.");
            return;
        }

        SaveSelectedModelConfig();
        DownloadBaseModelButton.IsEnabled = false;
        SetStatus("Scanning base model " + baseModel + "...");
        try
        {
            if (!TryParseHuggingFaceRepoReference(baseModel, out string owner, out string repo, out string revision))
                throw new InvalidOperationException("Base model is not a Hugging Face repo id: " + baseModel);

            LlmModelConfig config = GetRegistry().GetModelConfig(item.Key);
            using var scannerClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var scanner = new ModelRepositoryScanner(scannerClient);
            ModelRepositoryScanResult scan = await scanner.ScanHuggingFaceAsync(owner, repo, revision, bearerToken: config.HuggingFaceToken).ConfigureAwait(true);
            ModelFileCandidate? candidate = scan.Candidates
                .Where(candidate => candidate.CanDownload)
                .OrderByDescending(candidate => string.Equals(candidate.Action, "download_pytorch_bundle", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => candidate.UsesCompleteModelLayout())
                .ThenBy(candidate => candidate.SizeBytes)
                .FirstOrDefault();
            if (candidate == null)
                throw new InvalidOperationException("No downloadable base model files were found for " + baseModel + ".");

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["baseModelFor"] = item.Key,
                ["baseModelDisplayName"] = item.DisplayName
            };

            BrowserTabItem.Visibility = Visibility.Visible;
            ManagerTabs.SelectedItem = BrowserTabItem;
            await ModelBrowserControl.InitializeBrowserAsync().ConfigureAwait(true);
            ModelBrowserControl.QueueModelCandidate(candidate, config.HuggingFaceToken, metadata, item.Type);
            SetStatus("Queued base model download in the browser list: " + baseModel);
            DownloadBaseModelButton.IsEnabled = item.RequiresMissingBaseModel;
        }
        catch (Exception ex)
        {
            SetStatus("Base model download failed: " + ex.Message);
            DownloadBaseModelButton.IsEnabled = item.RequiresMissingBaseModel;
        }
    }

    private async Task DeleteSelectedModelAsync()
    {
        if (ModelsListBox.SelectedItem is not ModelManagerModelItem item || !item.IsDownloaded)
            return;

        MessageBoxResult result = MessageBox.Show(
            "Delete this model from disk?\n\n" + item.DisplayName + "\n" + item.FilePath,
            "Delete model",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        DeleteModelButton.IsEnabled = false;
        SetStatus("Deleting model: " + item.DisplayName);
        try
        {
            bool deleted = await Task.Run(() => GetRegistry().DeleteModel(item.Key)).ConfigureAwait(true);
            SetStatus(deleted ? "Deleted model: " + item.DisplayName : "Model was not found on disk: " + item.DisplayName);
            await RefreshModelsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus("Delete failed: " + ex.Message);
        }
        finally
        {
            DeleteModelButton.IsEnabled = ModelsListBox.SelectedItem is ModelManagerModelItem selected && selected.IsDownloaded;
        }
    }

    private static bool TryParseHuggingFaceRepoReference(string value, out string owner, out string repo, out string revision)
    {
        owner = "";
        repo = "";
        revision = "main";
        value = (value ?? "").Trim().Trim('"', '\'').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        const string httpsPrefix = "https://huggingface.co/";
        const string httpPrefix = "http://huggingface.co/";
        if (value.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[httpsPrefix.Length..];
        else if (value.StartsWith(httpPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[httpPrefix.Length..];

        int queryIndex = value.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
            value = value[..queryIndex];

        int atIndex = value.LastIndexOf('@');
        if (atIndex > 0 && atIndex < value.Length - 1)
        {
            revision = value[(atIndex + 1)..].Trim('/');
            value = value[..atIndex];
        }

        string[] parts = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        if (parts.Length >= 4 && parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
            revision = parts[3];
        if (string.IsNullOrWhiteSpace(revision))
            revision = "main";

        return !owner.Equals("models", StringComparison.OrdinalIgnoreCase) &&
               !owner.Equals("datasets", StringComparison.OrdinalIgnoreCase) &&
               !owner.Equals("spaces", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _resourceUsageTimer.Stop();
        ResetRegistry();
        try { ModelBrowserControl.Dispose(); } catch { }
    }

    private void ApplyBenchmarksToModels()
    {
        foreach (ModelManagerModelItem item in _models)
            item.ApplyBenchmark(FindBenchmarkForModel(item), _benchmarkControlsEnabled);
    }

    private ModelManagerBenchmarkInfo? FindBenchmarkForModel(ModelManagerModelItem item)
    {
        foreach (string candidate in BuildBenchmarkCandidates(item))
        {
            string key = NormalizeBenchmarkKey(candidate);
            if (!string.IsNullOrWhiteSpace(key) && _benchmarksByModel.TryGetValue(key, out ModelManagerBenchmarkInfo? benchmark))
                return benchmark;
        }

        return null;
    }

    private static IEnumerable<string> BuildBenchmarkCandidates(ModelManagerModelItem item)
    {
        if (item == null)
            yield break;

        yield return item.Key;
        yield return item.DisplayName;
        yield return item.LoadIdentifier;
        yield return item.FilePath;
        if (!string.IsNullOrWhiteSpace(item.FilePath))
            yield return Path.GetFileName(item.FilePath);
    }

    private static string NormalizeBenchmarkKey(string value)
    {
        return (value ?? "").Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
    }

    private sealed record SystemResourceUsageSnapshot(
        DateTimeOffset CapturedAtUtc,
        double CpuUsagePercent,
        double RamUsagePercent,
        double GpuUsagePercent,
        double VramUsagePercent,
        long RamUsedBytes,
        long RamTotalBytes,
        long VramUsedBytes,
        long VramTotalBytes,
        string ScopeText,
        string SummaryPrefix)
    {
        public static SystemResourceUsageSnapshot Empty { get; } = new(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, "idle; no GPU assigned", "Model/GPU");

        public double CompositePercent => ClampPercent((CpuUsagePercent + RamUsagePercent + GpuUsagePercent + VramUsagePercent) / 4d);

        public string SummaryText =>
            SummaryPrefix + " now CPU " + CpuUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "% | RAM " +
            RamUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "% | GPU " +
            GpuUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "% | VRAM " +
            VramUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "% | " + ScopeText;
    }

    private sealed record ResourceHistoryBar(double Value, string ToolTip);

    private sealed class GpuLoadPolicyPayload
    {
        public string device_id { get; init; } = "";
        public bool enabled { get; init; } = true;
        public int max_gpu_load_percent { get; init; } = 100;
        public int max_vram_usage_percent { get; init; } = 100;
        public string parallelism_mode { get; init; } = "single";
        public string parallelism_placement { get; init; } = "local";
        public IReadOnlyList<string> assigned_models { get; init; } = [];
    }

    private sealed class GpuSettingsFile
    {
        public GpuGlobalSettings Global { get; set; } = new();

        public GpuConfigurationItem[] Gpus { get; set; } = [];
    }

    private sealed class GpuGlobalSettings
    {
        public string ParallelismMode { get; set; } = "single";

        public string ParallelismPlacement { get; set; } = "local";

        public IReadOnlyList<string> TargetDeviceIds { get; set; } = [];

        public int MaxGpuLoadPercent { get; set; } = 100;

        public int MaxVramUsagePercent { get; set; } = 100;

        public string VideoDeviceMap { get; set; } = "balanced";

        public bool VideoAllowCpuOffload { get; set; } = true;

        public string VideoOffloadFolder { get; set; } = "";

        public bool VideoDisableCudaMemoryGuard { get; set; }

        public int VideoCudaMemoryReserveMb { get; set; } = 1024;

        public string VideoCpuMaxMemory { get; set; } = "";

        public bool VideoMemorySaving { get; set; } = true;
    }

    private sealed class GpuModelAssignmentItem : INotifyPropertyChanged
    {
        private bool _isAssigned;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ModelKey { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ModelId { get; init; } = "";
        public string SourceLabel { get; init; } = "";
        public string TypeLabel { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsSelectable { get; init; }

        public bool IsAssigned
        {
            get => _isAssigned;
            set
            {
                if (_isAssigned == value)
                    return;
                _isAssigned = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAssigned)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssignmentToolTip)));
            }
        }

        public string AssignmentToolTip =>
            IsSelectable
                ? IsAssigned
                    ? "This model is assigned to the selected GPU. Uncheck to create a per-GPU override that blocks this GPU."
                    : "Assign this model to the selected GPU. The GPU thresholds and placement values above will override the model defaults."
                : "This model cannot be loaded by LlmRuntime until its missing dependency or unsupported metadata is fixed.";

        public static GpuModelAssignmentItem FromModel(ModelManagerModelItem model, bool isAssigned) =>
            new()
            {
                ModelKey = model.Key,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Key : model.DisplayName,
                ModelId = model.LoadIdentifier,
                SourceLabel = model.SourceLabel,
                TypeLabel = FormatModelType(model),
                StatusLabel = model.StatusLabel,
                Description = FirstNonEmpty(model.SummaryLine, model.LoadDisabledReason, model.PathLine),
                IsSelectable = model.CanRequestLoad,
                IsAssigned = isAssigned
            };

        private static string FormatModelType(ModelManagerModelItem model)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(model.Type))
                parts.Add(model.Type);
            if (!string.IsNullOrWhiteSpace(model.Format))
                parts.Add(model.Format);
            if (!string.IsNullOrWhiteSpace(model.Quantization))
                parts.Add(model.Quantization);
            return parts.Count == 0 ? "model" : string.Join(" / ", parts);
        }

        public override string ToString() => FirstNonEmpty(DisplayName, ModelId, ModelKey, base.ToString());
    }

    private sealed class GpuConfigurationItem : INotifyPropertyChanged
    {
        private string _deviceId = "";
        private string _name = "";
        private string _computeCapability = "";
        private long _dedicatedMemoryBytes;
        private long _memoryUsedBytes;
        private long _memoryTotalBytes;
        private double _gpuUsagePercent;
        private double _vramUsagePercent;
        private bool _isCudaSupported = true;
        private bool _enabled = true;
        private int _maxGpuLoadPercent = 100;
        private int _maxVramUsagePercent = 100;
        private string _parallelismMode = "single";
        private string _parallelismPlacement = "local";
        private string _assignedModelsText = "";
        private string _disabledModelsText = "";
        private string _activeModelsText = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DeviceId
        {
            get => _deviceId;
            set => Set(ref _deviceId, NormalizeDeviceIdForDisplay(value));
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value ?? "");
        }

        public string ComputeCapability
        {
            get => _computeCapability;
            set => Set(ref _computeCapability, value ?? "");
        }

        public long DedicatedMemoryBytes
        {
            get => _dedicatedMemoryBytes;
            set => Set(ref _dedicatedMemoryBytes, Math.Max(0, value));
        }

        public long MemoryUsedBytes
        {
            get => _memoryUsedBytes;
            set => Set(ref _memoryUsedBytes, Math.Max(0, value));
        }

        public long MemoryTotalBytes
        {
            get => _memoryTotalBytes;
            set => Set(ref _memoryTotalBytes, Math.Max(0, value));
        }

        public double GpuUsagePercent
        {
            get => _gpuUsagePercent;
            set => Set(ref _gpuUsagePercent, ClampPercent(value));
        }

        public double VramUsagePercent
        {
            get => _vramUsagePercent;
            set => Set(ref _vramUsagePercent, ClampPercent(value));
        }

        public bool IsCudaSupported
        {
            get => _isCudaSupported;
            set => Set(ref _isCudaSupported, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public int MaxGpuLoadPercent
        {
            get => _maxGpuLoadPercent;
            set => Set(ref _maxGpuLoadPercent, ClampPercent(value));
        }

        public int MaxVramUsagePercent
        {
            get => _maxVramUsagePercent;
            set => Set(ref _maxVramUsagePercent, ClampPercent(value));
        }

        public string ParallelismMode
        {
            get => _parallelismMode;
            set => Set(ref _parallelismMode, NormalizeParallelismMode(value));
        }

        public string ParallelismPlacement
        {
            get => _parallelismPlacement;
            set => Set(ref _parallelismPlacement, NormalizeParallelismPlacement(value));
        }

        public string AssignedModelsText
        {
            get => _assignedModelsText;
            set => Set(ref _assignedModelsText, value ?? "");
        }

        public string DisabledModelsText
        {
            get => _disabledModelsText;
            set => Set(ref _disabledModelsText, value ?? "");
        }

        public string ActiveModelsText
        {
            get => _activeModelsText;
            set => Set(ref _activeModelsText, value ?? "");
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DeviceId : Name + " (" + DeviceId + ")";

        public bool IsBusy => GpuUsagePercent >= BusyGpuUsagePercent;

        public string BusyLabel => IsBusy ? "Busy" : "Available";

        public string GpuUsageText => "GPU " + GpuUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

        public string VramUsageText => MemoryTotalBytes > 0
            ? "VRAM " + FormatBytes(MemoryUsedBytes) + " / " + FormatBytes(MemoryTotalBytes) + " (" + VramUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%)"
            : "VRAM " + VramUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";

        public string UsageLine => GpuUsageText + " | " + VramUsageText + (IsBusy ? " | over 75% busy threshold" : "");

        public string DetailLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(ComputeCapability))
                    parts.Add("compute " + ComputeCapability);
                if (DedicatedMemoryBytes > 0)
                    parts.Add(FormatBytes(DedicatedMemoryBytes) + " VRAM");
                parts.Add(IsCudaSupported ? "CUDA supported" : "CUDA unsupported");
                return string.Join(" | ", parts);
            }
        }

        public string StatusLine => (Enabled ? "Enabled" : "Disabled") + " | " +
                                    FormatParallelism(ParallelismMode, ParallelismPlacement) +
                                    " | thresholds GPU " + MaxGpuLoadPercent.ToString(CultureInfo.InvariantCulture) +
                                    "%, VRAM " + MaxVramUsagePercent.ToString(CultureInfo.InvariantCulture) + "%";

        public string DeviceSortKey => DeviceId;

        public void ApplyRuntimeGpu(RuntimeGpuSnapshot gpu, int index)
        {
            DeviceId = FirstNonEmpty(gpu.DeviceId, "cuda:" + index.ToString(CultureInfo.InvariantCulture));
            Name = gpu.Name;
            ComputeCapability = gpu.ComputeCapability;
            IsCudaSupported = gpu.IsCudaSupported;
            DedicatedMemoryBytes = gpu.DedicatedMemoryBytes ?? 0;
            MemoryTotalBytes = gpu.EffectiveMemoryTotalBytes;
            MemoryUsedBytes = gpu.EffectiveMemoryUsedBytes;
            GpuUsagePercent = gpu.GpuUsagePercent.GetValueOrDefault();
            VramUsagePercent = gpu.EffectiveVramUsagePercent;
            NotifyComputed();
        }

        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnChanged(propertyName);
            NotifyComputed();
            return true;
        }

        private void NotifyComputed()
        {
            OnChanged(nameof(DisplayName));
            OnChanged(nameof(IsBusy));
            OnChanged(nameof(BusyLabel));
            OnChanged(nameof(GpuUsageText));
            OnChanged(nameof(VramUsageText));
            OnChanged(nameof(UsageLine));
            OnChanged(nameof(DetailLine));
            OnChanged(nameof(StatusLine));
            OnChanged(nameof(DeviceSortKey));
        }

        private void OnChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public override string ToString() => DisplayName;
    }

    private sealed class RuntimeHardwareSnapshot
    {
        public static RuntimeHardwareSnapshot Empty { get; } = new();

        public bool HasStatus { get; init; }
        public bool GenerationDisabled { get; init; }
        public bool RequiresPytorchRepair { get; init; }
        public bool IsGpuGenerationEnabled { get; init; }
        public string Message { get; init; } = "";
        public string CudaToolkitDownloadUrl { get; init; } = LlmRuntimeCompatibilityService.CudaToolkitDownloadUrl;
        public IReadOnlyList<RuntimeGpuSnapshot> Gpus { get; init; } = [];

        public string GlobalWarning
        {
            get
            {
                if (!GenerationDisabled && !RequiresPytorchRepair)
                    return "";

                return "CUDA/PyTorch warning: " + (string.IsNullOrWhiteSpace(Message) ? "GPU generation is disabled." : Message) +
                       " Install CUDA: " + (string.IsNullOrWhiteSpace(CudaToolkitDownloadUrl) ? LlmRuntimeCompatibilityService.CudaToolkitDownloadUrl : CudaToolkitDownloadUrl) +
                       ". Use Repair PyTorch from the Workstation image warning.";
            }
        }

        public RuntimeGpuSnapshot? BestGpu =>
            Gpus
                .OrderByDescending(gpu => gpu.IsCudaSupported)
                .ThenByDescending(gpu => gpu.ComputeCapabilityScore)
                .ThenByDescending(gpu => gpu.DedicatedMemoryBytes ?? 0)
                .FirstOrDefault();

        public static RuntimeHardwareSnapshot FromCompatibilityJson(JsonElement root)
        {
            string cudaUrl = ReadString(root, "cudaToolkitDownloadUrl") ?? ReadString(root, "cuda_toolkit_download_url") ?? LlmRuntimeCompatibilityService.CudaToolkitDownloadUrl;
            var gpus = new List<RuntimeGpuSnapshot>();

            if (root.TryGetProperty("diagnostics", out JsonElement diagnostics) && diagnostics.ValueKind == JsonValueKind.Object &&
                diagnostics.TryGetProperty("gpus", out JsonElement gpuElements) && gpuElements.ValueKind == JsonValueKind.Array)
            {
                int gpuIndex = 0;
                foreach (JsonElement gpu in gpuElements.EnumerateArray())
                {
                    gpus.Add(new RuntimeGpuSnapshot
                    {
                        DeviceId = ReadString(gpu, "deviceId") ?? ReadString(gpu, "device_id") ?? "cuda:" + gpuIndex.ToString(CultureInfo.InvariantCulture),
                        Name = ReadString(gpu, "name") ?? "",
                        ComputeCapability = ReadString(gpu, "computeCapability") ?? ReadString(gpu, "compute_capability") ?? "",
                        CatalogName = ReadString(gpu, "catalogName") ?? ReadString(gpu, "catalog_name") ?? "",
                        IsCudaSupported = ReadBool(gpu, "isCudaSupported") ?? ReadBool(gpu, "is_cuda_supported") ?? false,
                        DedicatedMemoryBytes = ReadInt64(gpu, "dedicatedMemoryBytes") ?? ReadInt64(gpu, "dedicated_memory_bytes"),
                        MemoryUsedBytes = ReadInt64(gpu, "memoryUsedBytes") ?? ReadInt64(gpu, "memory_used_bytes") ?? ReadInt64(gpu, "vramUsedBytes") ?? ReadInt64(gpu, "vram_used_bytes"),
                        MemoryTotalBytes = ReadInt64(gpu, "memoryTotalBytes") ?? ReadInt64(gpu, "memory_total_bytes") ?? ReadInt64(gpu, "vramTotalBytes") ?? ReadInt64(gpu, "vram_total_bytes") ?? ReadInt64(gpu, "dedicatedMemoryBytes") ?? ReadInt64(gpu, "dedicated_memory_bytes"),
                        GpuUsagePercent = ReadDouble(gpu, "gpuUsagePercent") ?? ReadDouble(gpu, "gpu_usage_percent") ?? ReadDouble(gpu, "utilizationGpu") ?? ReadDouble(gpu, "utilization_gpu"),
                        VramUsagePercent = ReadDouble(gpu, "vramUsagePercent") ?? ReadDouble(gpu, "vram_usage_percent") ?? ReadDouble(gpu, "memoryUsagePercent") ?? ReadDouble(gpu, "memory_usage_percent")
                    });
                    gpuIndex++;
                }
            }

            return new RuntimeHardwareSnapshot
            {
                HasStatus = true,
                GenerationDisabled = ReadBool(root, "generationDisabled") == true || ReadBool(root, "generation_disabled") == true,
                RequiresPytorchRepair = ReadBool(root, "requiresPytorchRepair") == true || ReadBool(root, "requires_pytorch_repair") == true,
                IsGpuGenerationEnabled = ReadBool(root, "isGpuGenerationEnabled") == true || ReadBool(root, "is_gpu_generation_enabled") == true,
                Message = ReadString(root, "message") ?? "",
                CudaToolkitDownloadUrl = cudaUrl,
                Gpus = gpus
            };
        }

        public ModelHardwareWarning BuildModelWarning(ModelManagerModelItem item)
        {
            if (item == null)
                return ModelHardwareWarning.None;
            if (!HasStatus)
                return ModelHardwareWarning.None;

            RuntimeGpuSnapshot? gpu = BestGpu;
            ModelHardwareProfile? profile = ModelHardwareProfile.Match(item);
            bool gpuSensitive = IsGpuSensitiveModel(item) || profile != null;
            var reasons = new List<string>();

            if (gpuSensitive && gpu == null)
            {
                reasons.Add("No NVIDIA CUDA GPU was reported by the runtime compatibility layer.");
            }
            else if (gpuSensitive && gpu != null && !gpu.IsCudaSupported)
            {
                reasons.Add("The detected GPU was not marked CUDA-supported by the runtime compatibility layer.");
            }

            if (gpuSensitive && (GenerationDisabled || RequiresPytorchRepair))
            {
                reasons.Add(string.IsNullOrWhiteSpace(Message)
                    ? "CUDA/PyTorch is not ready for GPU generation."
                    : Message);
            }

            if (profile != null && gpu != null)
                reasons.AddRange(profile.Evaluate(gpu));

            if (profile == null && gpu != null && IsChatOrGgufModel(item) &&
                item.SizeBytes > 0 && gpu.DedicatedMemoryBytes.GetValueOrDefault() > 0 &&
                item.SizeBytes > gpu.DedicatedMemoryBytes.GetValueOrDefault() * 0.90)
            {
                reasons.Add("The model file is larger than 90% of detected VRAM (" +
                    FormatBytes(item.SizeBytes) + " model vs " + FormatBytes(gpu.DedicatedMemoryBytes.GetValueOrDefault()) +
                    " VRAM), so full GPU offload is unlikely to fit.");
            }

            if (reasons.Count == 0)
                return ModelHardwareWarning.None;

            string gpuText = gpu == null ? "no CUDA GPU detected" : gpu.DisplayText;
            string family = profile?.DisplayName ?? "this model";
            string summary = "Hardware warning: " + family + " is a poor fit for " + gpuText + ".";
            string detail = summary + " " + string.Join(" ", reasons.Distinct(StringComparer.OrdinalIgnoreCase)) +
                            " Workstation will still allow loading; this warning explains why GPU execution may fail, run on CPU, run extremely slowly, or hit out-of-memory for this model.";
            return new ModelHardwareWarning(summary, detail);
        }

        private static bool IsGpuSensitiveModel(ModelManagerModelItem item)
        {
            string text = item.SearchText.ToLowerInvariant();
            return string.Equals(item.Type, "image", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type, "video", StringComparison.OrdinalIgnoreCase) ||
                   item.Tags.Any(tag => string.Equals(tag, "image", StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "video", StringComparison.OrdinalIgnoreCase)) ||
                   ContainsAny(text, "diffusers", "safetensors", "stable diffusion", "stable-diffusion", "sdxl", "sd3", "flux", "qwen image", "qwen-image");
        }

        private static bool IsChatOrGgufModel(ModelManagerModelItem item)
        {
            return string.Equals(item.Format, "gguf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type, "llm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.Type, "vlm", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class RuntimeGpuSnapshot
    {
        public string DeviceId { get; init; } = "";
        public string Name { get; init; } = "";
        public string CatalogName { get; init; } = "";
        public string ComputeCapability { get; init; } = "";
        public bool IsCudaSupported { get; init; }
        public long? DedicatedMemoryBytes { get; init; }
        public long? MemoryUsedBytes { get; init; }
        public long? MemoryTotalBytes { get; init; }
        public double? GpuUsagePercent { get; init; }
        public double? VramUsagePercent { get; init; }

        public long EffectiveMemoryTotalBytes => MemoryTotalBytes ?? DedicatedMemoryBytes ?? 0;
        public long EffectiveMemoryUsedBytes => MemoryUsedBytes ?? 0;
        public double EffectiveVramUsagePercent =>
            VramUsagePercent.HasValue
                ? Math.Clamp(VramUsagePercent.GetValueOrDefault(), 0, 100)
                : EffectiveMemoryTotalBytes > 0
                    ? Math.Clamp(EffectiveMemoryUsedBytes * 100.0 / EffectiveMemoryTotalBytes, 0, 100)
                    : 0;

        public double ComputeCapabilityScore => TryParseCapability(ComputeCapability, out double value) ? value : 0;

        public string DisplayText
        {
            get
            {
                var parts = new List<string> { string.IsNullOrWhiteSpace(Name) ? "detected GPU" : Name };
                if (!string.IsNullOrWhiteSpace(DeviceId))
                    parts.Add(DeviceId);
                if (!string.IsNullOrWhiteSpace(ComputeCapability))
                    parts.Add("compute " + ComputeCapability);
                if (DedicatedMemoryBytes.GetValueOrDefault() > 0)
                    parts.Add(FormatBytes(DedicatedMemoryBytes.GetValueOrDefault()) + " VRAM");
                return string.Join(", ", parts);
            }
        }
    }

    private sealed class ModelHardwareWarning
    {
        public static ModelHardwareWarning None { get; } = new("", "");

        public ModelHardwareWarning(string summary, string detail)
        {
            Summary = summary ?? "";
            Detail = detail ?? "";
        }

        public string Summary { get; }
        public string Detail { get; }
        public bool HasWarning => !string.IsNullOrWhiteSpace(Summary) || !string.IsNullOrWhiteSpace(Detail);
    }

    private sealed class ModelHardwareProfile
    {
        private ModelHardwareProfile(string displayName, string minimumComputeCapability, long minimumDedicatedMemoryBytes, bool prefersModernBfloat16, params string[] keywords)
        {
            DisplayName = displayName;
            MinimumComputeCapability = minimumComputeCapability;
            MinimumDedicatedMemoryBytes = minimumDedicatedMemoryBytes;
            PrefersModernBfloat16 = prefersModernBfloat16;
            Keywords = keywords;
        }

        public string DisplayName { get; }
        public string MinimumComputeCapability { get; }
        public long MinimumDedicatedMemoryBytes { get; }
        public bool PrefersModernBfloat16 { get; }
        public IReadOnlyList<string> Keywords { get; }

        private static IReadOnlyList<ModelHardwareProfile> Profiles { get; } =
        [
            new("FLUX.1-dev", "8.0", 24L * 1024 * 1024 * 1024, true, "flux.1-dev", "flux1.dev", "flux-dev", "flux dev", "black-forest-labs/flux.1-dev", "flux"),
            new("Qwen-Image", "8.0", 24L * 1024 * 1024 * 1024, true, "qwen-image", "qwen image", "qwen_image", "qwenimage"),
            new("Stable Diffusion 3", "7.0", 16L * 1024 * 1024 * 1024, true, "stable-diffusion-3", "stable diffusion 3", "sd3", "sd 3"),
            new("SDXL", "5.0", 8L * 1024 * 1024 * 1024, false, "sdxl", "stable-diffusion-xl", "stable diffusion xl"),
            new("Stable Diffusion 1.x", "5.0", 4L * 1024 * 1024 * 1024, false, "stable-diffusion-v1", "stable diffusion v1", "sd15", "sd1.5", "sd 1.5")
        ];

        public static ModelHardwareProfile? Match(ModelManagerModelItem item)
        {
            string text = item.SearchText.ToLowerInvariant().Replace('_', ' ');
            return Profiles.FirstOrDefault(profile => profile.Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        public IEnumerable<string> Evaluate(RuntimeGpuSnapshot gpu)
        {
            if (!string.IsNullOrWhiteSpace(MinimumComputeCapability) &&
                CompareCapability(gpu.ComputeCapability, MinimumComputeCapability) < 0)
            {
                yield return DisplayName + " expects compute capability " + MinimumComputeCapability +
                             "+ for practical GPU generation; detected " + (string.IsNullOrWhiteSpace(gpu.ComputeCapability) ? "unknown" : gpu.ComputeCapability) + ".";
            }

            if (MinimumDedicatedMemoryBytes > 0 && gpu.DedicatedMemoryBytes.GetValueOrDefault() > 0 &&
                gpu.DedicatedMemoryBytes.GetValueOrDefault() < MinimumDedicatedMemoryBytes)
            {
                yield return DisplayName + " expects about " + FormatBytes(MinimumDedicatedMemoryBytes) +
                             " of VRAM for the local pipeline; detected " + FormatBytes(gpu.DedicatedMemoryBytes.GetValueOrDefault()) + ".";
            }

            if (PrefersModernBfloat16 && CompareCapability(gpu.ComputeCapability, "8.0") < 0)
                yield return DisplayName + " commonly relies on modern BF16/tensor-core friendly hardware; this GPU is older than that class.";
        }
    }

    private sealed class RuntimeModelSnapshot
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Type { get; init; } = "";
        public string Publisher { get; init; } = "";
        public string Architecture { get; init; } = "";
        public string Format { get; init; } = "";
        public string Quantization { get; init; } = "";
        public string ParamsString { get; init; } = "";
        public long SizeBytes { get; init; }
        public uint MaxContextLength { get; init; }
        public string LoadDisabledReason { get; init; } = "";
        public IReadOnlyList<string> Tags { get; init; } = [];
        public IReadOnlyList<RuntimeLoadedInstance> LoadedInstances { get; init; } = [];
        public bool ChatLoadable { get; init; }
        public bool RuntimeLoadable { get; init; }
        public string SuggestedChatService { get; init; } = "";
        public string AdapterType { get; init; } = "";
        public bool AdapterRequiresBaseModel { get; init; }
        public string BaseModel { get; init; } = "";
        public IReadOnlyList<string> BaseModels { get; init; } = [];
        public bool BaseModelAvailable { get; init; } = true;

        public static RuntimeModelSnapshot FromJson(JsonElement root)
        {
            var loaded = new List<RuntimeLoadedInstance>();
            if (root.TryGetProperty("loaded_instances", out JsonElement loadedInstances) && loadedInstances.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement instance in loadedInstances.EnumerateArray())
                    loaded.Add(RuntimeLoadedInstance.FromJson(instance));
            }

            return new RuntimeModelSnapshot
            {
                Key = ReadString(root, "key") ?? ReadString(root, "id") ?? ReadString(root, "model") ?? "",
                DisplayName = ReadString(root, "display_name") ?? ReadString(root, "displayName") ?? ReadString(root, "name") ?? "",
                Type = ReadString(root, "type") ?? "",
                Publisher = ReadString(root, "publisher") ?? "",
                Architecture = ReadString(root, "architecture") ?? "",
                Format = ReadString(root, "format") ?? "",
                Quantization = ReadQuantization(root),
                ParamsString = ReadString(root, "params_string") ?? ReadString(root, "paramsString") ?? "",
                SizeBytes = ReadInt64(root, "size_bytes") ?? ReadInt64(root, "sizeBytes") ?? 0,
                MaxContextLength = (uint)Math.Max(0, ReadInt64(root, "max_context_length") ?? ReadInt64(root, "maxContextLength") ?? 0),
                LoadDisabledReason = ReadString(root, "load_disabled_reason") ?? ReadString(root, "loadDisabledReason") ?? ReadString(root, "description") ?? "",
                Tags = ReadStringArray(root, "tags"),
                LoadedInstances = loaded,
                ChatLoadable = ReadCapability(root, "chat_completion") ?? true,
                RuntimeLoadable = ReadCapability(root, "runtime_load") ?? ReadCapability(root, "runtimeLoad") ?? ReadCapability(root, "load") ?? true,
                AdapterType = ReadString(root, "adapter_type") ?? ReadString(root, "adapterType") ?? "",
                AdapterRequiresBaseModel = ReadBool(root, "adapter_requires_base_model") ?? ReadBool(root, "adapterRequiresBaseModel") ?? false,
                BaseModel = ReadString(root, "base_model") ?? ReadString(root, "baseModel") ?? "",
                BaseModels = ReadStringArray(root, "base_models").Concat(ReadStringArray(root, "baseModels")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BaseModelAvailable = ReadBool(root, "base_model_available") ?? ReadBool(root, "baseModelAvailable") ?? true,
                SuggestedChatService = ReadString(root, "chat_service") ??
                                       ReadString(root, "chatService") ??
                                       ReadString(root, "service") ??
                                       ReadCapabilityString(root, "chat_service") ??
                                       ReadCapabilityString(root, "chatService") ??
                                       ""
            };
        }

        private static string ReadQuantization(JsonElement root)
        {
            if (!root.TryGetProperty("quantization", out JsonElement quantization))
                return "";
            if (quantization.ValueKind == JsonValueKind.String)
                return quantization.GetString() ?? "";
            if (quantization.ValueKind == JsonValueKind.Object)
                return ReadString(quantization, "name") ?? "";
            return "";
        }

        private static bool? ReadCapability(JsonElement root, string name)
        {
            if (!root.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Object)
                return null;
            if (!capabilities.TryGetProperty(name, out JsonElement value))
                return null;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            return null;
        }

        private static string? ReadCapabilityString(JsonElement root, string name)
        {
            if (!root.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Object)
                return null;
            return ReadString(capabilities, name);
        }
    }

    private sealed class RuntimeLoadedInstance
    {
        public string Id { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public int ActiveJobs { get; init; }
        public int QueuedJobs { get; init; }
        public int ConcurrencyLimit { get; init; } = 1;
        public ModelManagerLoadSettings? Config { get; init; }

        public static RuntimeLoadedInstance FromJson(JsonElement root)
        {
            ModelManagerLoadSettings? config = null;
            if (root.TryGetProperty("config", out JsonElement configElement) && configElement.ValueKind == JsonValueKind.Object)
                config = ModelManagerLoadSettings.FromRuntimeConfig(configElement);

            return new RuntimeLoadedInstance
            {
                Id = ReadString(root, "id") ?? "",
                DeviceId = ReadString(root, "device_id") ?? ReadString(root, "deviceId") ?? config?.TargetDeviceIds.FirstOrDefault() ?? "",
                ActiveJobs = (int)Math.Max(0, ReadInt64(root, "active_jobs") ?? ReadInt64(root, "activeJobs") ?? 0),
                QueuedJobs = (int)Math.Max(0, ReadInt64(root, "queued_jobs") ?? ReadInt64(root, "queuedJobs") ?? ReadInt64(root, "queue_count") ?? ReadInt64(root, "queueCount") ?? 0),
                ConcurrencyLimit = (int)Math.Max(1, ReadInt64(root, "concurrency_limit") ?? ReadInt64(root, "concurrencyLimit") ?? 1),
                Config = config
            };
        }
    }

    private sealed class ModelManagerModelItem : INotifyPropertyChanged
    {
        private ModelManagerBenchmarkInfo? _benchmark;
        private bool _benchmarkCanRun = true;
        private string _configuredParallelismMode = "single";
        private string _configuredParallelismPlacement = "local";
        private SystemResourceUsageSnapshot _resourceUsage = SystemResourceUsageSnapshot.Empty;
        private IReadOnlyList<SystemResourceUsageSnapshot> _resourceHistory = [];
        private IReadOnlyList<ResourceHistoryBar> _resourceHistoryBars = [];

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string FilePath { get; init; } = "";
        public string SourceLabel { get; init; } = "";
        public string Format { get; init; } = "";
        public string Type { get; init; } = "";
        public string Architecture { get; init; } = "";
        public string Quantization { get; init; } = "";
        public string ParamsString { get; init; } = "";
        public long SizeBytes { get; init; }
        public uint MaxContextLength { get; init; }
        public string LoadDisabledReason { get; init; } = "";
        public IReadOnlyList<string> Tags { get; init; } = [];
        public IReadOnlyList<RuntimeLoadedInstance> LoadedInstances { get; init; } = [];
        public int ActiveJobCount => LoadedInstances.Sum(instance => Math.Max(0, instance.ActiveJobs));
        public int QueuedJobCount => LoadedInstances.Sum(instance => Math.Max(0, instance.QueuedJobs));
        public bool RuntimeChatLoadable { get; init; }
        public bool RuntimeLoadable { get; init; }
        public string SuggestedChatService { get; init; } = "";
        public string AdapterType { get; init; } = "";
        public bool AdapterRequiresBaseModel { get; init; }
        public string BaseModel { get; init; } = "";
        public IReadOnlyList<string> BaseModels { get; init; } = [];
        public bool BaseModelAvailable { get; init; } = true;
        public ModelHardwareWarning HardwareWarning { get; set; } = ModelHardwareWarning.None;
        public bool RequiresMissingBaseModel => (AdapterRequiresBaseModel || BaseModels.Count > 0 || !string.IsNullOrWhiteSpace(BaseModel)) && !BaseModelAvailable;
        public bool IsDownloaded => !string.IsNullOrWhiteSpace(FilePath) && (File.Exists(FilePath) || Directory.Exists(Path.GetDirectoryName(FilePath) ?? ""));
        public bool IsRunning => LoadedInstances.Count > 0;
        public bool IsLmStudio => SourceLabel.Contains("LM Studio", StringComparison.OrdinalIgnoreCase);
        public bool IsLlmRuntime => !IsLmStudio && SourceLabel.Contains("LlmRuntime", StringComparison.OrdinalIgnoreCase);
        public bool CanLoad => RuntimeLoadable && !RequiresMissingBaseModel;
        public bool CanRequestLoad => !RequiresMissingBaseModel &&
                                      string.IsNullOrWhiteSpace(LoadDisabledReason) &&
                                      (RuntimeLoadable || IsDownloaded || IsRunning);
        public string LoadIdentifier => !string.IsNullOrWhiteSpace(Key) ? Key : FilePath;
        public int SortRank => IsRunning ? 0 : IsLmStudio ? 1 : IsDownloaded ? 2 : 3;
        public string StatusLabel => IsRunning ? "Running" : IsDownloaded ? "Downloaded" : CanRequestLoad ? "Available" : "Unsupported";
        public bool HasHardwareWarning => HardwareWarning.HasWarning;
        public Visibility HardwareWarningVisibility => HasHardwareWarning ? Visibility.Visible : Visibility.Collapsed;
        public string HardwareWarningSummary => HardwareWarning.Summary;
        public string HardwareWarningDetail => HardwareWarning.Detail;
        public Brush BorderBrush => HasHardwareWarning ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : IsRunning ? Brushes.MediumAquamarine : new SolidColorBrush(Color.FromRgb(45, 52, 64));
        public Brush StatusBackgroundBrush => IsRunning ? new SolidColorBrush(Color.FromRgb(20, 74, 62)) : new SolidColorBrush(Color.FromRgb(29, 38, 48));
        public Brush StatusBorderBrush => IsRunning ? new SolidColorBrush(Color.FromRgb(64, 201, 162)) : new SolidColorBrush(Color.FromRgb(59, 70, 84));
        public Brush StatusTextBrush => IsRunning ? new SolidColorBrush(Color.FromRgb(210, 255, 240)) : new SolidColorBrush(Color.FromRgb(242, 244, 248));
        public bool BenchmarkCanRun => _benchmarkCanRun && (_benchmark?.IsRunning != true);
        public string BenchmarkScoreText => _benchmark != null && _benchmark.TokensPerSecond > 0
            ? Math.Max(0, Math.Min(100, _benchmark.Rating)).ToString(CultureInfo.InvariantCulture) + "/100"
            : "--/100";
        public string BenchmarkLine
        {
            get
            {
                if (_benchmark == null)
                    return "Benchmark: not run";
                if (_benchmark.TokensPerSecond > 0)
                    return "Benchmark: " + FormatBenchmarkRate(_benchmark) + " | " +
                           (_benchmark.Status ?? "");
                return "Benchmark: " + (string.IsNullOrWhiteSpace(_benchmark.Status) ? "not run" : _benchmark.Status);
            }
        }
        public string BenchmarkToolTip => _benchmark == null
            ? "Run a throughput benchmark for this model."
            : BenchmarkLine + (string.IsNullOrWhiteSpace(_benchmark.BenchmarkedUtc) ? "" : "\n\n" + _benchmark.BenchmarkedUtc);
        public Brush BenchmarkBackgroundBrush
        {
            get
            {
                if (_benchmark?.IsRunning == true)
                    return new SolidColorBrush(Color.FromRgb(28, 71, 78));
                if (_benchmark != null && (_benchmark.Status ?? "").StartsWith("Benchmark failed", StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(74, 22, 38));
                if (_benchmark != null && _benchmark.TokensPerSecond > 0)
                    return RatingBrush(_benchmark.Rating);
                return new SolidColorBrush(Color.FromRgb(29, 38, 48));
            }
        }
        public Brush BenchmarkBorderBrush
        {
            get
            {
                if (_benchmark?.IsRunning == true)
                    return new SolidColorBrush(Color.FromRgb(64, 201, 162));
                if (_benchmark != null && (_benchmark.Status ?? "").StartsWith("Benchmark failed", StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(255, 92, 122));
                if (_benchmark != null && _benchmark.TokensPerSecond > 0)
                    return RatingBrush(_benchmark.Rating);
                return new SolidColorBrush(Color.FromRgb(59, 70, 84));
            }
        }
        public Brush BenchmarkTextBrush => _benchmark != null && _benchmark.TokensPerSecond > 0 && _benchmark.Rating >= 45
            ? new SolidColorBrush(Color.FromRgb(5, 16, 22))
            : new SolidColorBrush(Color.FromRgb(242, 244, 248));
        public double CpuUsagePercent => _resourceUsage.CpuUsagePercent;
        public double RamUsagePercent => _resourceUsage.RamUsagePercent;
        public double GpuUsagePercent => _resourceUsage.GpuUsagePercent;
        public double VramUsagePercent => _resourceUsage.VramUsagePercent;
        public string CpuUsageText => CpuUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        public string RamUsageText => RamUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        public string GpuUsageText => GpuUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        public string VramUsageText => VramUsagePercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        public IReadOnlyList<ResourceHistoryBar> ResourceHistoryBars => _resourceHistoryBars;
        public string ResourceUsageSummary => BuildResourceUsageSummary();
        public string ResourceCostText => BuildResourceCostText();
        public string ParallelismLabel
        {
            get
            {
                ModelManagerLoadSettings? running = LoadedInstances.FirstOrDefault(instance => instance.Config != null)?.Config;
                string mode = running?.ParallelismMode ?? _configuredParallelismMode;
                string placement = running?.ParallelismPlacement ?? _configuredParallelismPlacement;
                return FormatParallelism(mode, placement);
            }
        }
        public string ParallelismToolTip
        {
            get
            {
                var parts = new List<string> { "Parallelism: " + ParallelismLabel };
                ModelManagerLoadSettings? running = LoadedInstances.FirstOrDefault(instance => instance.Config != null)?.Config;
                IReadOnlyList<string> targets = running?.TargetDeviceIds ?? [];
                IReadOnlyList<string> nodes = running?.NetworkNodeIds ?? [];
                if (targets.Count > 0)
                    parts.Add("GPU targets: " + string.Join(", ", targets));
                if (nodes.Count > 0)
                    parts.Add("Network nodes: " + string.Join(", ", nodes));
                return string.Join("\n", parts);
            }
        }
        public string SummaryLine => BuildSummaryLine();
        public string PathLine => string.IsNullOrWhiteSpace(FilePath) ? Key : FilePath;
        public string SearchText => string.Join(" ", new[] { Key, DisplayName, FilePath, SourceLabel, Format, Type, Architecture, Quantization, ParamsString, AdapterType, BaseModel }.Concat(BaseModels).Concat(Tags));
        public IReadOnlyList<ModelManagerDetailRow> DetailRows => BuildDetailRows();

        public void ApplyBenchmark(ModelManagerBenchmarkInfo? benchmark, bool canBenchmark)
        {
            _benchmark = benchmark;
            _benchmarkCanRun = canBenchmark && (benchmark?.CanBenchmark ?? true);
            OnChanged(nameof(BenchmarkCanRun));
            OnChanged(nameof(BenchmarkScoreText));
            OnChanged(nameof(BenchmarkLine));
            OnChanged(nameof(BenchmarkToolTip));
            OnChanged(nameof(BenchmarkBackgroundBrush));
            OnChanged(nameof(BenchmarkBorderBrush));
            OnChanged(nameof(BenchmarkTextBrush));
            OnChanged(nameof(ResourceCostText));
        }

        public void ApplyResourceUsage(SystemResourceUsageSnapshot usage, IReadOnlyList<SystemResourceUsageSnapshot> history)
        {
            _resourceUsage = usage ?? SystemResourceUsageSnapshot.Empty;
            _resourceHistory = history ?? [];
            _resourceHistoryBars = _resourceHistory
                .Select(sample => new ResourceHistoryBar(
                    sample.CompositePercent,
                    sample.CapturedAtUtc.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture) +
                    " | avg " + sample.CompositePercent.ToString("0", CultureInfo.InvariantCulture) + "%"))
                .ToArray();
            OnChanged(nameof(CpuUsagePercent));
            OnChanged(nameof(RamUsagePercent));
            OnChanged(nameof(GpuUsagePercent));
            OnChanged(nameof(VramUsagePercent));
            OnChanged(nameof(CpuUsageText));
            OnChanged(nameof(RamUsageText));
            OnChanged(nameof(GpuUsageText));
            OnChanged(nameof(VramUsageText));
            OnChanged(nameof(ResourceHistoryBars));
            OnChanged(nameof(ResourceUsageSummary));
            OnChanged(nameof(ResourceCostText));
        }

        public void ApplyLoadSettings(ModelManagerLoadSettings settings)
        {
            _configuredParallelismMode = string.IsNullOrWhiteSpace(settings.ParallelismMode) ? "single" : settings.ParallelismMode;
            _configuredParallelismPlacement = string.IsNullOrWhiteSpace(settings.ParallelismPlacement) ? "local" : settings.ParallelismPlacement;
            OnChanged(nameof(ParallelismLabel));
            OnChanged(nameof(ParallelismToolTip));
            OnChanged(nameof(DetailRows));
        }

        public static ModelManagerModelItem FromLocalModel(LlmModelInfo model, RuntimeModelSnapshot? runtime, bool localBaseModelAvailable)
        {
            return new ModelManagerModelItem
            {
                Key = model.Key,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Key : model.DisplayName,
                FilePath = model.FilePath,
                SourceLabel = string.Equals(model.Publisher, "lmstudio", StringComparison.OrdinalIgnoreCase) ? "LM Studio" : "LlmRuntime",
                Format = model.Format,
                Type = model.Type,
                Architecture = model.Architecture ?? "",
                Quantization = model.QuantizationName ?? "",
                ParamsString = model.ParamsString ?? "",
                SizeBytes = model.SizeBytes,
                MaxContextLength = model.MaxContextLength ?? runtime?.MaxContextLength ?? 0,
                LoadDisabledReason = runtime?.LoadDisabledReason ?? model.LoadDisabledReason ?? "",
                Tags = model.Tags,
                AdapterType = runtime?.AdapterType ?? model.AdapterType,
                AdapterRequiresBaseModel = runtime?.AdapterRequiresBaseModel ?? model.AdapterRequiresBaseModel,
                BaseModel = runtime?.BaseModel ?? model.BaseModel,
                BaseModels = MergeBaseModels(runtime?.BaseModels, model.BaseModels, model.BaseModel),
                BaseModelAvailable = runtime?.BaseModelAvailable ?? localBaseModelAvailable,
                LoadedInstances = runtime?.LoadedInstances ?? model.LoadedInstances.Select(instance => new RuntimeLoadedInstance
                {
                    Id = instance.Id,
                    DeviceId = instance.DeviceId,
                    ActiveJobs = instance.ActiveJobs,
                    QueuedJobs = instance.QueuedJobs,
                    ConcurrencyLimit = instance.ConcurrencyLimit,
                    Config = ModelManagerLoadSettings.FromLoadConfig(instance.Config)
                }).ToArray(),
                RuntimeChatLoadable = runtime?.ChatLoadable ?? LlmModelRegistry.IsChatLoadableModel(model),
                RuntimeLoadable = runtime?.RuntimeLoadable ?? LlmModelRegistry.IsRuntimeLoadableModel(model),
                SuggestedChatService = runtime?.SuggestedChatService ?? LlmModelRegistry.GetRuntimeServiceForModel(model)
            };
        }

        public static ModelManagerModelItem FromRuntimeModel(RuntimeModelSnapshot runtime)
        {
            return new ModelManagerModelItem
            {
                Key = runtime.Key,
                DisplayName = string.IsNullOrWhiteSpace(runtime.DisplayName) ? runtime.Key : runtime.DisplayName,
                SourceLabel = string.Equals(runtime.Publisher, "lmstudio", StringComparison.OrdinalIgnoreCase) ? "LM Studio" : "LlmRuntime",
                Format = string.IsNullOrWhiteSpace(runtime.Format) ? "unknown" : runtime.Format,
                Type = runtime.Type,
                Architecture = runtime.Architecture,
                Quantization = runtime.Quantization,
                ParamsString = runtime.ParamsString,
                SizeBytes = runtime.SizeBytes,
                MaxContextLength = runtime.MaxContextLength,
                LoadDisabledReason = runtime.LoadDisabledReason,
                Tags = runtime.Tags,
                AdapterType = runtime.AdapterType,
                AdapterRequiresBaseModel = runtime.AdapterRequiresBaseModel,
                BaseModel = runtime.BaseModel,
                BaseModels = MergeBaseModels(runtime.BaseModels, [], runtime.BaseModel),
                BaseModelAvailable = runtime.BaseModelAvailable,
                LoadedInstances = runtime.LoadedInstances,
                RuntimeChatLoadable = runtime.ChatLoadable,
                RuntimeLoadable = runtime.RuntimeLoadable,
                SuggestedChatService = runtime.SuggestedChatService
            };
        }

        private string BuildSummaryLine()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Type))
                parts.Add(Type);
            if (!string.IsNullOrWhiteSpace(Format))
                parts.Add(Format);
            if (!string.IsNullOrWhiteSpace(Architecture))
                parts.Add(Architecture);
            if (!string.IsNullOrWhiteSpace(Quantization))
                parts.Add(Quantization);
            if (!string.IsNullOrWhiteSpace(ParamsString))
                parts.Add(ParamsString);
            if (MaxContextLength > 0)
                parts.Add(MaxContextLength.ToString("N0") + " ctx");
            if (SizeBytes > 0)
                parts.Add(FormatBytes(SizeBytes));
            if (RequiresMissingBaseModel)
                parts.Add("missing base model");
            return parts.Count == 0 ? Key : string.Join(" | ", parts);
        }

        private IReadOnlyList<ModelManagerDetailRow> BuildDetailRows()
        {
            var rows = new List<ModelManagerDetailRow>
            {
                new("Key", Key),
                new("Loadable", CanRequestLoad ? "Yes" : "No"),
                new("Chat service", string.IsNullOrWhiteSpace(SuggestedChatService) ? "Assistant" : SuggestedChatService),
                new("Source", SourceLabel),
                new("Format", string.IsNullOrWhiteSpace(Format) ? "unknown" : Format),
                new("Type", string.IsNullOrWhiteSpace(Type) ? "unknown" : Type),
                new("Architecture", string.IsNullOrWhiteSpace(Architecture) ? "unknown" : Architecture),
                new("Quantization", string.IsNullOrWhiteSpace(Quantization) ? "unknown" : Quantization),
                new("Parameters", string.IsNullOrWhiteSpace(ParamsString) ? "unknown" : ParamsString),
                new("Size", SizeBytes > 0 ? FormatBytes(SizeBytes) : "unknown"),
                new("Context", MaxContextLength > 0 ? MaxContextLength.ToString("N0") : "unknown"),
                new("Load status", string.IsNullOrWhiteSpace(LoadDisabledReason) ? "ready" : LoadDisabledReason),
                new("Parallelism", ParallelismLabel),
                new("Hardware warning", HasHardwareWarning ? HardwareWarningDetail : "none"),
                new("Adapter", string.IsNullOrWhiteSpace(AdapterType) ? "none" : AdapterType),
                new("Base model", BaseModels.Count == 0 ? "none" : string.Join(", ", BaseModels)),
                new("Base model local", BaseModelAvailable ? "yes" : "missing"),
                new("Tags", Tags.Count == 0 ? "none" : string.Join(", ", Tags)),
                new("Path", string.IsNullOrWhiteSpace(FilePath) ? "not exposed by runtime" : FilePath)
            };

            foreach (RuntimeLoadedInstance instance in LoadedInstances)
            {
                if (instance.Config == null)
                {
                    rows.Add(new ModelManagerDetailRow("Running", string.IsNullOrWhiteSpace(instance.Id) ? Key : instance.Id));
                    continue;
                }

                rows.Add(new ModelManagerDetailRow("Running", (string.IsNullOrWhiteSpace(instance.Id) ? Key : instance.Id) +
                    " | " + instance.Config.Backend +
                    " | " + FormatParallelism(instance.Config.ParallelismMode, instance.Config.ParallelismPlacement) +
                    " | ctx " + instance.Config.ContextLength.ToString("N0") +
                    " | gpu layers " + instance.Config.GpuLayerCount.ToString()));
            }

            return rows;
        }

        private string BuildResourceUsageSummary()
        {
            if (_resourceHistory.Count == 0)
                return "Model/GPU resources: waiting for live sample.";

            double avg = _resourceHistory.Average(sample => sample.CompositePercent);
            TimeSpan span = _resourceHistory.Count > 1
                ? _resourceHistory[^1].CapturedAtUtc - _resourceHistory[0].CapturedAtUtc
                : TimeSpan.Zero;
            string window = span.TotalSeconds < 30
                ? Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s"
                : Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "m";
            return _resourceUsage.SummaryText + " | avg " + avg.ToString("0", CultureInfo.InvariantCulture) + "% over " + window;
        }

        private string BuildResourceCostText()
        {
            double avg = _resourceHistory.Count == 0 ? _resourceUsage.CompositePercent : _resourceHistory.Average(sample => sample.CompositePercent);
            string costIndex = "cost index " + (avg / 100d).ToString("0.00", CultureInfo.InvariantCulture) + "x";
            if (_benchmark != null && _benchmark.TokensPerHour > 0)
            {
                double hoursForMillion = 1_000_000d / _benchmark.TokensPerHour;
                return "Estimate: " + costIndex + " | avg model/GPU " + avg.ToString("0", CultureInfo.InvariantCulture) +
                       "% | 1M tokens in " + FormatDurationEstimate(TimeSpan.FromHours(hoursForMillion));
            }

            return "Estimate: " + costIndex + " | avg model/GPU " + avg.ToString("0", CultureInfo.InvariantCulture) +
                   "% | run benchmark for token/time projection";
        }

        private static string FormatDurationEstimate(TimeSpan span)
        {
            if (span.TotalMinutes < 1)
                return Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
            if (span.TotalHours < 1)
                return Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "m";
            if (span.TotalDays < 1)
                return span.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + "h";
            return span.TotalDays.ToString("0.0", CultureInfo.InvariantCulture) + "d";
        }

        private static IReadOnlyList<string> MergeBaseModels(IReadOnlyList<string>? left, IReadOnlyList<string>? right, string fallback)
        {
            return (left ?? [])
                .Concat(right ?? [])
                .Concat(string.IsNullOrWhiteSpace(fallback) ? [] : [fallback])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static Brush RatingBrush(int rating)
        {
            rating = Math.Max(0, Math.Min(100, rating));
            byte red = (byte)Math.Round(229 * (100 - rating) / 100.0);
            byte green = (byte)Math.Round(72 + (154 * rating / 100.0));
            byte blue = (byte)Math.Round(72 - (38 * rating / 100.0));
            return new SolidColorBrush(Color.FromRgb(red, green, blue));
        }

        private static string FormatBenchmarkRate(ModelManagerBenchmarkInfo benchmark)
        {
            string kind = (benchmark.BenchmarkKind ?? "").Trim().ToLowerInvariant();
            if (kind == "image")
                return benchmark.TokensPerSecond.ToString("0.000", CultureInfo.InvariantCulture) + " img/s";
            if (kind == "audio")
                return benchmark.TokensPerSecond.ToString("0.000", CultureInfo.InvariantCulture) + " audio/s";
            if (kind == "video")
                return benchmark.TokensPerSecond.ToString("0.000", CultureInfo.InvariantCulture) + " video/s";
            return benchmark.TokensPerSecond.ToString("0.0", CultureInfo.InvariantCulture) +
                   " tok/s | " + benchmark.TokensPerHour.ToString("N0", CultureInfo.CurrentCulture) + " tok/hr";
        }

        private void OnChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed record ModelManagerDetailRow(string Name, string Value)
    {
        public string ToolTip => BuildToolTip(Name, Value);

        private static string BuildToolTip(string name, string value)
        {
            string label = string.IsNullOrWhiteSpace(name) ? "Property" : name.Trim();
            string detail = label switch
            {
                "Key" => "Runtime identifier used by Workstation, LlmRuntime, and web chat requests.",
                "Loadable" => "Whether LlmRuntime can load this model with the current metadata and local files.",
                "Chat service" => "Suggested chat mode or generation service to select after this model is loaded.",
                "Source" => "Where Workstation discovered the model.",
                "Format" => "Model file or bundle format reported by discovery.",
                "Type" => "Model family such as llm, vlm, image, audio, or video.",
                "Architecture" => "Architecture name read from model metadata when available.",
                "Quantization" => "Quantization profile. Lower-bit quantization usually uses less memory with some quality tradeoff.",
                "Parameters" => "Approximate model parameter scale.",
                "Size" => "Model file or bundle size on disk when known.",
                "Context" => "Maximum context length reported by the model metadata.",
                "Load status" => "Runtime readiness or the reason loading is currently blocked.",
                "Parallelism" => "Configured or active model placement mode: single instance, data parallelism, model offloading, or pipeline parallelism.",
                "Hardware warning" => "Non-blocking model-specific mismatch detected from runtime GPU compatibility, compute capability, VRAM, and model-family requirements.",
                "Adapter" => "Adapter type if this model is an adapter rather than a standalone base model.",
                "Base model" => "Base model required by this adapter, if any.",
                "Base model local" => "Whether the required base model is present locally.",
                "Tags" => "Discovery tags and capability hints associated with this model.",
                "Path" => "Local file path or runtime-provided location for the model.",
                "Running" => "Loaded runtime instance and the load settings currently in effect.",
                _ => "Model property."
            };

            return detail + (string.IsNullOrWhiteSpace(value) ? "" : "\n\n" + value);
        }
    }

    private static string FormatParallelism(string mode, string placement) =>
        FormatParallelismMode(mode) + " / " + FormatParallelismPlacement(placement);

    private static string FormatParallelismMode(string mode)
    {
        return NormalizeParallelismMode(mode) switch
        {
            "data_parallel" => "Data Parallelism",
            "model_offload" => "Model Offloading",
            "pipeline_parallel" => "Pipeline Parallelism",
            _ => "Single"
        };
    }

    private static string FormatParallelismPlacement(string placement)
    {
        return NormalizeParallelismPlacement(placement) switch
        {
            "network" => "Network",
            "hybrid" => "Hybrid",
            _ => "Local"
        };
    }

    private static string NormalizeParallelismMode(string mode)
    {
        string value = (mode ?? "").Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase);
        return value switch
        {
            "data" or "dataparallel" or "data_parallelism" => "data_parallel",
            "offload" or "modeloffload" or "model_offloading" => "model_offload",
            "pipeline" or "pipelineparallel" or "pipeline_parallelism" => "pipeline_parallel",
            _ => string.IsNullOrWhiteSpace(value) ? "single" : value
        };
    }

    private static string NormalizeParallelismPlacement(string placement)
    {
        string value = (placement ?? "").Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase);
        return value switch
        {
            "remote" or "distributed" => "network",
            "localnetwork" or "local_network" => "hybrid",
            _ => string.IsNullOrWhiteSpace(value) ? "local" : value
        };
    }

    private static string NormalizeVideoDeviceMap(string value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase);
        return normalized switch
        {
            "off" or "none" or "disabled" or "false" or "0" => "",
            "balanced_low_0" or "balanced_low0" => "balanced",
            "sequential" => "balanced",
            "auto" => "balanced",
            "balanced" => "balanced",
            _ => normalized
        };
    }

    private static string ToParallelismModeName(LlmParallelismMode mode) =>
        mode switch
        {
            LlmParallelismMode.DataParallel => "data_parallel",
            LlmParallelismMode.ModelOffload => "model_offload",
            LlmParallelismMode.PipelineParallel => "pipeline_parallel",
            _ => "single"
        };

    private static string ToParallelismPlacementName(LlmParallelismPlacement placement) =>
        placement switch
        {
            LlmParallelismPlacement.Network => "network",
            LlmParallelismPlacement.Hybrid => "hybrid",
            _ => "local"
        };

    private sealed class ModelManagerLoadSettings
    {
        public string Backend { get; set; } = "auto";
        public string ParallelismMode { get; set; } = "single";
        public string ParallelismPlacement { get; set; } = "local";
        public uint ContextLength { get; set; } = 8192;
        public int EvalBatchSize { get; set; } = 512;
        public int GpuLayerCount { get; set; } = -1;
        public IReadOnlyList<string> TargetDeviceIds { get; set; } = [];
        public IReadOnlyList<string> NetworkNodeIds { get; set; } = [];
        public int MaxGpuLoadPercent { get; set; } = 100;
        public int MaxVramUsagePercent { get; set; } = 100;
        public int PipelineStageCount { get; set; } = 1;
        public int DataParallelReplicaCount { get; set; } = 1;
        public int IdleUnloadMinutes { get; set; } = DefaultIdleUnloadMinutes;
        public bool Enabled { get; set; }
        public bool FlashAttention { get; set; }
        public bool OffloadKvCacheToGpu { get; set; }
        public bool AllowBackendFallback { get; set; }
        public string VideoDeviceMap { get; set; } = "balanced";
        public bool VideoAllowCpuOffload { get; set; } = true;
        public string VideoOffloadFolder { get; set; } = "";
        public bool VideoDisableCudaMemoryGuard { get; set; }
        public int VideoCudaMemoryReserveMb { get; set; } = 1024;
        public string VideoCpuMaxMemory { get; set; } = "";
        public bool VideoMemorySaving { get; set; } = true;

        public static ModelManagerLoadSettings CreateDefault(ModelManagerModelItem item)
        {
            RuntimeLoadedInstance? running = item.LoadedInstances.FirstOrDefault(instance => instance.Config != null);
            if (running?.Config != null)
                return running.Config.Clone();

            return new ModelManagerLoadSettings
            {
                ContextLength = item.MaxContextLength > 0 ? item.MaxContextLength : 8192,
                IdleUnloadMinutes = DefaultIdleUnloadMinutes
            };
        }

        public static ModelManagerLoadSettings FromRuntimeConfig(JsonElement root)
        {
            return new ModelManagerLoadSettings
            {
                Backend = ReadString(root, "backend") ?? "auto",
                ParallelismMode = NormalizeParallelismMode(ReadString(root, "parallelism_mode") ?? ReadString(root, "parallelismMode") ?? "single"),
                ParallelismPlacement = NormalizeParallelismPlacement(ReadString(root, "parallelism_placement") ?? ReadString(root, "parallelismPlacement") ?? "local"),
                ContextLength = (uint)Math.Max(1, ReadInt64(root, "context_length") ?? ReadInt64(root, "contextLength") ?? 8192),
                EvalBatchSize = (int)Math.Max(1, ReadInt64(root, "eval_batch_size") ?? ReadInt64(root, "evalBatchSize") ?? 512),
                GpuLayerCount = (int)(ReadInt64(root, "gpu_layer_count") ?? ReadInt64(root, "gpuLayerCount") ?? -1),
                TargetDeviceIds = ReadStringArray(root, "target_device_ids").Concat(ReadStringArray(root, "targetDeviceIds")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                NetworkNodeIds = ReadStringArray(root, "network_node_ids").Concat(ReadStringArray(root, "networkNodeIds")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                MaxGpuLoadPercent = ClampPercent((int)(ReadInt64(root, "gpu_load_threshold_percent") ?? ReadInt64(root, "gpuLoadThresholdPercent") ?? ReadInt64(root, "max_gpu_load_percent") ?? ReadInt64(root, "maxGpuLoadPercent") ?? 100)),
                MaxVramUsagePercent = ClampPercent((int)(ReadInt64(root, "max_vram_usage_percent") ?? ReadInt64(root, "maxVramUsagePercent") ?? 100)),
                PipelineStageCount = Math.Max(1, (int)(ReadInt64(root, "pipeline_stage_count") ?? ReadInt64(root, "pipelineStageCount") ?? 1)),
                DataParallelReplicaCount = Math.Max(1, (int)(ReadInt64(root, "data_parallel_replicas") ?? ReadInt64(root, "dataParallelReplicas") ?? 1)),
                IdleUnloadMinutes = (int)Math.Max(0, ReadInt64(root, "idle_unload_minutes") ?? ReadInt64(root, "idleUnloadMinutes") ?? DefaultIdleUnloadMinutes),
                Enabled = ReadBool(root, "enabled") ?? false,
                FlashAttention = ReadBool(root, "flash_attention") ?? ReadBool(root, "flashAttention") ?? false,
                OffloadKvCacheToGpu = ReadBool(root, "offload_kv_cache_to_gpu") ?? ReadBool(root, "offloadKvCacheToGpu") ?? false,
                AllowBackendFallback = ReadBool(root, "allow_backend_fallback") ?? ReadBool(root, "allowBackendFallback") ?? false,
                VideoDeviceMap = NormalizeVideoDeviceMap(FirstNonEmpty(ReadString(root, "video_device_map"), ReadString(root, "videoDeviceMap"), ReadString(root, "device_map"), ReadString(root, "deviceMap"), "balanced")),
                VideoAllowCpuOffload = ReadBool(root, "video_allow_cpu_offload") ?? ReadBool(root, "videoAllowCpuOffload") ?? ReadBool(root, "allow_cpu_offload") ?? ReadBool(root, "allowCpuOffload") ?? true,
                VideoOffloadFolder = ReadString(root, "video_offload_folder") ?? ReadString(root, "videoOffloadFolder") ?? "",
                VideoDisableCudaMemoryGuard = ReadBool(root, "video_disable_cuda_memory_guard") ?? ReadBool(root, "videoDisableCudaMemoryGuard") ?? false,
                VideoCudaMemoryReserveMb = Math.Max(0, (int)(ReadInt64(root, "video_cuda_memory_reserve_mb") ?? ReadInt64(root, "videoCudaMemoryReserveMb") ?? 1024)),
                VideoCpuMaxMemory = ReadString(root, "video_cpu_max_memory") ?? ReadString(root, "videoCpuMaxMemory") ?? "",
                VideoMemorySaving = ReadBool(root, "video_memory_saving") ?? ReadBool(root, "videoMemorySaving") ?? ReadBool(root, "memory_saving") ?? ReadBool(root, "memorySaving") ?? true
            };
        }

        public static ModelManagerLoadSettings FromLoadConfig(LlmLoadConfig config)
        {
            return new ModelManagerLoadSettings
            {
                Backend = config.Backend.ToString().ToLowerInvariant(),
                ParallelismMode = ToParallelismModeName(config.ParallelismMode),
                ParallelismPlacement = ToParallelismPlacementName(config.ParallelismPlacement),
                ContextLength = config.ContextLength,
                EvalBatchSize = config.EvalBatchSize,
                GpuLayerCount = config.GpuLayerCount,
                TargetDeviceIds = config.TargetDeviceIds,
                NetworkNodeIds = config.NetworkNodeIds,
                MaxGpuLoadPercent = config.MaxGpuLoadPercent,
                MaxVramUsagePercent = config.MaxVramUsagePercent,
                PipelineStageCount = config.PipelineStageCount,
                DataParallelReplicaCount = config.DataParallelReplicaCount,
                IdleUnloadMinutes = DefaultIdleUnloadMinutes,
                Enabled = false,
                FlashAttention = config.FlashAttention,
                OffloadKvCacheToGpu = config.OffloadKvCacheToGpu,
                AllowBackendFallback = config.AllowBackendFallback,
                VideoDeviceMap = NormalizeVideoDeviceMap(FirstNonEmpty(config.VideoDeviceMap, "balanced")),
                VideoAllowCpuOffload = config.VideoAllowCpuOffload,
                VideoOffloadFolder = config.VideoOffloadFolder,
                VideoDisableCudaMemoryGuard = config.VideoDisableCudaMemoryGuard,
                VideoCudaMemoryReserveMb = config.VideoCudaMemoryReserveMb,
                VideoCpuMaxMemory = config.VideoCpuMaxMemory,
                VideoMemorySaving = config.VideoMemorySaving
            };
        }

        public ModelManagerLoadSettings Clone()
        {
            return new ModelManagerLoadSettings
            {
                Backend = Backend,
                ParallelismMode = ParallelismMode,
                ParallelismPlacement = ParallelismPlacement,
                ContextLength = ContextLength,
                EvalBatchSize = EvalBatchSize,
                GpuLayerCount = GpuLayerCount,
                TargetDeviceIds = TargetDeviceIds.ToArray(),
                NetworkNodeIds = NetworkNodeIds.ToArray(),
                MaxGpuLoadPercent = MaxGpuLoadPercent,
                MaxVramUsagePercent = MaxVramUsagePercent,
                PipelineStageCount = PipelineStageCount,
                DataParallelReplicaCount = DataParallelReplicaCount,
                IdleUnloadMinutes = IdleUnloadMinutes,
                Enabled = Enabled,
                FlashAttention = FlashAttention,
                OffloadKvCacheToGpu = OffloadKvCacheToGpu,
                AllowBackendFallback = AllowBackendFallback,
                VideoDeviceMap = VideoDeviceMap,
                VideoAllowCpuOffload = VideoAllowCpuOffload,
                VideoOffloadFolder = VideoOffloadFolder,
                VideoDisableCudaMemoryGuard = VideoDisableCudaMemoryGuard,
                VideoCudaMemoryReserveMb = VideoCudaMemoryReserveMb,
                VideoCpuMaxMemory = VideoCpuMaxMemory,
                VideoMemorySaving = VideoMemorySaving
            };
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return values.Any(value => !string.IsNullOrWhiteSpace(value) && text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static int CompareCapability(string left, string right)
    {
        bool hasLeft = TryParseCapability(left, out double leftValue);
        bool hasRight = TryParseCapability(right, out double rightValue);
        if (!hasLeft && !hasRight)
            return 0;
        if (!hasLeft)
            return -1;
        if (!hasRight)
            return 1;
        return leftValue.CompareTo(rightValue);
    }

    private static bool TryParseCapability(string value, out double parsed)
    {
        parsed = 0;
        string[] parts = (value ?? "").Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out int major))
            return false;

        int minor = 0;
        if (parts.Length > 1)
            int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out minor);

        parsed = major + Math.Min(99, Math.Max(0, minor)) / 100.0;
        return true;
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static long? ReadInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt64(out long parsed) ? parsed : null;
    }

    private static double? ReadDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out double parsed) ? parsed : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            return [];

        if (value.ValueKind == JsonValueKind.String)
            return ParseDelimitedList(value.GetString() ?? "").ToArray();

        if (value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "unknown";
        if (bytes < 1024)
            return bytes.ToString("N0") + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
    }
}
