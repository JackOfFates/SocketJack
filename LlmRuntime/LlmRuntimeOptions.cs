namespace LlmRuntime;

public enum LlmBackendKind
{
    Auto,
    Cpu,
    Cuda,
    Cuda12,
    Vulkan,
    DirectML,
    Vllm
}

public sealed class LlmRuntimeOptions
{
    public string ModelRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, "Models");

    public string CompleteModelRoot { get; set; } = "";

    public bool IncludeLmStudioModels { get; set; } = false;

    public string ToolRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, "Tools");

    public string AgentRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, "Agents");

    public string DefaultWorkspaceRoot { get; set; } = Environment.CurrentDirectory;

    public string RuntimeConfigPath { get; set; } = "";

    public string CompatibilityConfigPath { get; set; } = "";

    public bool RestoreLoadedModelsOnStartup { get; set; } = false;

    public int Port { get; set; } = 1234;

    public uint DefaultContextLength { get; set; } = 8192;

    public int DefaultGpuLayerCount { get; set; } = -1;

    public int DefaultEvalBatchSize { get; set; } = 512;

    public bool DefaultFlashAttention { get; set; } = false;

    public bool DefaultOffloadKvCacheToGpu { get; set; } = false;

    public LlmBackendKind DefaultBackend { get; set; } = LlmBackendKind.Auto;

    public bool AllowBackendFallback { get; set; } = false;

    public bool RequireGpuForAutoBackend { get; set; } = true;

    public bool PreventCpuBackendFallback { get; set; } = true;

    public string DirectMlGgufRunnerPath { get; set; } = "";

    public string DirectMlGgufRunnerArguments { get; set; } = "";

    public string VllmPythonPath { get; set; } =
        Environment.GetEnvironmentVariable("JACKLLM_VLLM_PYTHON") ??
        Environment.GetEnvironmentVariable("LLMRUNTIME_VLLM_PYTHON") ??
        "";

    public string VllmBaseUrl { get; set; } =
        Environment.GetEnvironmentVariable("JACKLLM_VLLM_BASE_URL") ??
        Environment.GetEnvironmentVariable("LLMRUNTIME_VLLM_BASE_URL") ??
        "http://127.0.0.1:8000";

    public string VllmExtraArguments { get; set; } =
        Environment.GetEnvironmentVariable("JACKLLM_VLLM_ARGS") ??
        Environment.GetEnvironmentVariable("LLMRUNTIME_VLLM_ARGS") ??
        "--dtype auto --enforce-eager";

    public List<RemoteVllmProfile> RemoteVllmProfiles { get; set; } = [];

    public string SelectedRemoteVllmProfileId { get; set; } = "";

    internal RemoteVllmManager? RemoteVllmManager { get; set; }

    public TimeSpan VllmStartupTimeout { get; set; } =
        TimeSpan.FromSeconds(Math.Clamp(
            int.TryParse(
                Environment.GetEnvironmentVariable("JACKLLM_VLLM_STARTUP_TIMEOUT_SECONDS") ??
                Environment.GetEnvironmentVariable("LLMRUNTIME_VLLM_STARTUP_TIMEOUT_SECONDS"),
                out int seconds)
                ? seconds
                : 600,
            30,
            3600));

    public bool LocalPrivacyMode { get; set; } = true;

    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromHours(4);

    public string OnnxConversionWorkerPath { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER_PATH") ?? "";

    public string OnnxConversionWorkerArguments { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER_ARGS") ?? "";

    public TimeSpan OnnxConversionTimeout { get; set; } = TimeSpan.FromHours(6);

    public string ServerName { get; set; } = "LlmRuntime";

    public string ControlAuthToken { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_CONTROL_TOKEN") ?? "";
}
