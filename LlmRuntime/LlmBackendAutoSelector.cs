using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LlmRuntime;

internal static class LlmBackendAutoSelector
{
    public const string EnvironmentVariable = "LLMRUNTIME_AUTO_BACKEND";

    public static LlmBackendKind Resolve(LlmBackendKind requested, bool requireGpu = true)
    {
        if (requested == LlmBackendKind.Cuda)
            return LlmBackendKind.Cuda12;

        if (requested != LlmBackendKind.Auto)
            return requested;

        if (TryReadForcedBackend(out var forcedBackend))
        {
            if (requireGpu && forcedBackend == LlmBackendKind.Cpu)
                throw BuildGpuRequiredException("LLMRUNTIME_AUTO_BACKEND is set to CPU.");
            return forcedBackend;
        }

        if (HasNvidiaCudaDriver() && HasBackendAsset(LlmBackendKind.Cuda12))
            return LlmBackendKind.Cuda12;

        if (HasVulkanLoader() && HasBackendAsset(LlmBackendKind.Vulkan))
            return LlmBackendKind.Vulkan;

        if (requireGpu)
            throw BuildGpuRequiredException("No CUDA or Vulkan LLamaSharp GPU backend asset is available.");

        return LlmBackendKind.Cpu;
    }

    private static LlmRuntimeException BuildGpuRequiredException(string reason) =>
        new(
            "LlmRuntime auto backend could not select a GPU backend. CPU fallback is disabled for LlmRuntime and agent modes. " +
            reason + " Install/configure the CUDA runtime assets under the build-local Runtimes folder, repair PyTorch/CUDA from Workstation, or explicitly choose a GPU backend.",
            "backend_error",
            "gpu_backend_required");

    private static bool TryReadForcedBackend(out LlmBackendKind backend)
    {
        string value = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "";
        string normalized = value.Trim().Replace("-", "", StringComparison.OrdinalIgnoreCase).Replace("_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        backend = normalized switch
        {
            "cpu" => LlmBackendKind.Cpu,
            "cuda" => LlmBackendKind.Cuda12,
            "cuda12" => LlmBackendKind.Cuda12,
            "vulkan" => LlmBackendKind.Vulkan,
            "directml" => LlmBackendKind.DirectML,
            _ => LlmBackendKind.Auto
        };

        return backend != LlmBackendKind.Auto;
    }

    private static bool HasNvidiaCudaDriver() =>
        TryLoadNativeLibrary(GetCudaDriverLibraryNames()) || NvidiaSmiReportsGpu();

    private static bool HasVulkanLoader() =>
        TryLoadNativeLibrary(GetVulkanLoaderLibraryNames());

    private static bool TryLoadNativeLibrary(IEnumerable<string> libraryNames)
    {
        foreach (string name in libraryNames)
        {
            if (NativeLibrary.TryLoad(name, out nint handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    private static bool NvidiaSmiReportsGpu()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return false;

            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasBackendAsset(LlmBackendKind backend)
    {
        string assetName = GetBackendAssetName(backend);
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        foreach (string directory in LlmNativeRuntimePathConfigurator.EnumerateBackendSearchDirectories(backend))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, assetName)))
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static string GetBackendAssetName(LlmBackendKind backend)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return backend switch
            {
                LlmBackendKind.Cuda or LlmBackendKind.Cuda12 => "ggml-cuda.dll",
                LlmBackendKind.Vulkan => "ggml-vulkan.dll",
                _ => ""
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return backend switch
            {
                LlmBackendKind.Cuda or LlmBackendKind.Cuda12 => "libggml-cuda.so",
                LlmBackendKind.Vulkan => "libggml-vulkan.so",
                _ => ""
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return backend switch
            {
                LlmBackendKind.Vulkan => "libggml-vulkan.dylib",
                _ => ""
            };
        }

        return "";
    }

    private static IReadOnlyList<string> GetCudaDriverLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["nvcuda.dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["libcuda.so.1", "libcuda.so"];
        return [];
    }

    private static IReadOnlyList<string> GetVulkanLoaderLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["vulkan-1.dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["libvulkan.so.1", "libvulkan.so"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["libvulkan.1.dylib", "libvulkan.dylib"];
        return [];
    }

}
