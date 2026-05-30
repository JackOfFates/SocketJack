using LLama.Native;

namespace LlmRuntime;

internal static class LlamaSharpBackendSelector
{
    private static readonly object ConfigureLock = new();
    private static LlmBackendKind? _configuredBackend;
    private static bool? _configuredFallback;

    public static void Configure(LlmLoadConfig config)
    {
        var backend = LlmBackendAutoSelector.Resolve(config.Backend);
        if (backend == LlmBackendKind.DirectML)
            throw new NotSupportedException("DirectML is not available for LLamaSharp GGUF inference. Use CPU, CUDA12, or Vulkan.");

        LlmNativeRuntimePathConfigurator.ConfigureProcessPath(backend);
        var pinnedLibraries = ResolvePinnedNativeLibraries(backend);

        lock (ConfigureLock)
        {
            if (_configuredBackend.HasValue)
            {
                if (_configuredBackend.Value != backend || _configuredFallback != config.AllowBackendFallback)
                {
                    throw new InvalidOperationException(
                        $"LLamaSharp native backend was already configured as {Format(_configuredBackend.Value)}. Restart the runtime to switch to {Format(backend)}.");
                }

                return;
            }

            if (pinnedLibraries.HasValue)
            {
                NativeLibraryConfig.All
                    .WithSearchDirectories(LlmNativeRuntimePathConfigurator.EnumerateBackendSearchDirectories(backend))
                    .WithLibrary(pinnedLibraries.Value.LlamaPath, pinnedLibraries.Value.MtmdPath);
            }
            else
            {
                NativeLibraryConfig.All
                    .WithSearchDirectories(LlmNativeRuntimePathConfigurator.EnumerateBackendSearchDirectories(backend))
                    .WithAutoFallback(config.AllowBackendFallback)
                    .WithCuda(UsesCuda(backend))
                    .WithVulkan(backend is LlmBackendKind.Vulkan);
            }

            _configuredBackend = backend;
            _configuredFallback = config.AllowBackendFallback;
        }
    }

    public static void ValidateLoadedBackend(LlmBackendKind backend)
    {
        backend = LlmBackendAutoSelector.Resolve(backend);
        if (!UsesCuda(backend) && backend is not LlmBackendKind.Vulkan)
            return;

        string requiredModule = backend is LlmBackendKind.Vulkan
            ? GetNativeLibraryName("ggml-vulkan")
            : GetNativeLibraryName("ggml-cuda");
        string requiredDirectory = GetBackendDirectoryName(backend);
        bool loaded = System.Diagnostics.Process.GetCurrentProcess().Modules
            .Cast<System.Diagnostics.ProcessModule>()
            .Any(module =>
            {
                string fileName = Path.GetFileName(module.FileName);
                if (!string.Equals(fileName, requiredModule, StringComparison.OrdinalIgnoreCase))
                    return false;

                string directoryName = Path.GetFileName(Path.GetDirectoryName(module.FileName) ?? "");
                return string.Equals(directoryName, requiredDirectory, StringComparison.OrdinalIgnoreCase);
            });

        if (!loaded)
        {
            throw new LlmRuntimeException(
                "LLamaSharp was configured for " + Format(backend) + ", but the loaded native modules do not include " + requiredModule + " from the " + requiredDirectory + " backend. Restart JackLLM and verify the GPU backend assets are loadable.",
                "backend_error",
                "gpu_backend_not_loaded");
        }
    }

    public static string Format(LlmBackendKind backend) =>
        backend switch
        {
            LlmBackendKind.Cuda => "cuda12",
            LlmBackendKind.Cuda12 => "cuda12",
            LlmBackendKind.DirectML => "directml",
            _ => backend.ToString().ToLowerInvariant()
        };

    private static bool UsesCuda(LlmBackendKind backend) =>
        backend is LlmBackendKind.Cuda or LlmBackendKind.Cuda12;

    private static string GetBackendDirectoryName(LlmBackendKind backend) =>
        backend is LlmBackendKind.Vulkan ? "vulkan" : "cuda12";

    private static (string LlamaPath, string MtmdPath)? ResolvePinnedNativeLibraries(LlmBackendKind backend)
    {
        if (!UsesCuda(backend) && backend is not LlmBackendKind.Vulkan)
            return null;

        foreach (string directory in LlmNativeRuntimePathConfigurator.EnumerateBackendSearchDirectories(backend))
        {
            string llamaPath = Path.Combine(directory, GetNativeLibraryName("llama"));
            string mtmdPath = Path.Combine(directory, GetNativeLibraryName("mtmd"));
            string accelerationPath = Path.Combine(directory, UsesCuda(backend) ? GetNativeLibraryName("ggml-cuda") : GetNativeLibraryName("ggml-vulkan"));
            if (File.Exists(llamaPath) && File.Exists(mtmdPath) && File.Exists(accelerationPath))
                return (llamaPath, mtmdPath);
        }

        throw new LlmRuntimeException(
            "The requested " + Format(backend) + " LLamaSharp backend assets were not found under the runtime search directories.",
            "backend_error",
            "gpu_backend_assets_missing");
    }

    private static string GetNativeLibraryName(string baseName)
    {
        if (OperatingSystem.IsWindows())
            return baseName + ".dll";
        if (OperatingSystem.IsMacOS())
            return "lib" + baseName + ".dylib";
        return "lib" + baseName + ".so";
    }
}
