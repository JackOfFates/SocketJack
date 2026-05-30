namespace LlmRuntime;

public enum LlmBackendKind
{
    Auto,
    Cpu,
    Cuda,
    Cuda12,
    Vulkan,
    DirectML
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

    public bool RestoreLoadedModelsOnStartup { get; set; } = true;

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

    public bool LocalPrivacyMode { get; set; } = true;

    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromHours(4);

    public string OnnxConversionWorkerPath { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER_PATH") ?? "";

    public string OnnxConversionWorkerArguments { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER_ARGS") ?? "";

    public TimeSpan OnnxConversionTimeout { get; set; } = TimeSpan.FromHours(6);

    public string ServerName { get; set; } = "LlmRuntime";

    public string ControlAuthToken { get; set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_CONTROL_TOKEN") ?? "";
}
