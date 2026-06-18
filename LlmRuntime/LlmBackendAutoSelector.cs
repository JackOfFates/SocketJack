using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LlmRuntime;

internal static class LlmBackendAutoSelector
{
    public const string EnvironmentVariable = "LLMRUNTIME_AUTO_BACKEND";
    private const double MinimumCuda12AutoComputeCapability = 6.0d;

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

        bool hasCudaAsset = HasBackendAsset(LlmBackendKind.Cuda12);
        bool hasVulkanBackend = HasVulkanLoader() && HasBackendAsset(LlmBackendKind.Vulkan);
        return ResolveAutoBackend(ProbeCudaForAutoBackend(), hasCudaAsset, hasVulkanBackend, requireGpu);
    }

    internal static LlmBackendKind ResolveAutoBackend(CudaAutoBackendProbe cuda, bool hasCudaAsset, bool hasVulkanBackend, bool requireGpu)
    {
        if (cuda.DriverAvailable && hasCudaAsset)
            return LlmBackendKind.Cuda12;

        if (hasVulkanBackend)
            return LlmBackendKind.Vulkan;

        if (requireGpu)
        {
            string reason = cuda.DriverAvailable && !cuda.SupportedForAuto
                ? cuda.UnsupportedReason
                : "No CUDA or Vulkan LLamaSharp GPU backend asset is available.";
            throw BuildGpuRequiredException(reason);
        }

        return LlmBackendKind.Cpu;
    }

    public static bool ShouldUseLegacyCudaCompatibilityProfile(LlmBackendKind backend)
    {
        if (backend is not LlmBackendKind.Cuda and not LlmBackendKind.Cuda12)
            return false;

        CudaAutoBackendProbe probe = ProbeCudaForAutoBackend();
        return probe.DriverAvailable && !probe.SupportedForAuto;
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
            "vllm" => LlmBackendKind.Vllm,
            _ => LlmBackendKind.Auto
        };

        return backend != LlmBackendKind.Auto;
    }

    private static bool HasVulkanLoader() =>
        TryLoadNativeLibrary(GetVulkanLoaderLibraryNames());

    private static CudaAutoBackendProbe ProbeCudaForAutoBackend()
    {
        CudaAutoBackendProbe nvidiaSmiProbe = ProbeCudaWithNvidiaSmi();
        if (nvidiaSmiProbe.DriverAvailable)
            return nvidiaSmiProbe;

        bool driverAvailable = TryLoadNativeLibrary(GetCudaDriverLibraryNames()) || NvidiaSmiReportsGpu();
        return new CudaAutoBackendProbe(driverAvailable, driverAvailable, "");
    }

    private static CudaAutoBackendProbe ProbeCudaWithNvidiaSmi()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,compute_cap --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return CudaAutoBackendProbe.Unavailable;

            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return CudaAutoBackendProbe.Unavailable;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return CudaAutoBackendProbe.Unavailable;

            return BuildCudaProbeFromNvidiaSmiOutput(output);
        }
        catch
        {
            return CudaAutoBackendProbe.Unavailable;
        }
    }

    internal static CudaAutoBackendProbe BuildCudaProbeFromNvidiaSmiOutput(string output)
    {
        IReadOnlyList<NvidiaCudaDeviceInfo> devices = ParseNvidiaSmiComputeCapabilityOutput(output);
        if (devices.Count == 0)
            return CudaAutoBackendProbe.Unavailable;

        if (devices.Any(device => !device.ComputeCapability.HasValue || device.ComputeCapability.Value >= MinimumCuda12AutoComputeCapability))
            return new CudaAutoBackendProbe(true, true, "");

        string summary = string.Join(
            ", ",
            devices.Select(device => string.IsNullOrWhiteSpace(device.Name)
                ? device.ComputeCapability!.Value.ToString("0.0", CultureInfo.InvariantCulture)
                : device.Name + " compute " + device.ComputeCapability!.Value.ToString("0.0", CultureInfo.InvariantCulture)));
        return new CudaAutoBackendProbe(
            true,
            false,
            "NVIDIA CUDA GPU(s) were detected, but their compute capability is below the CUDA12 auto minimum " +
            MinimumCuda12AutoComputeCapability.ToString("0.0", CultureInfo.InvariantCulture) +
            ": " + summary + ".");
    }

    internal static IReadOnlyList<NvidiaCudaDeviceInfo> ParseNvidiaSmiComputeCapabilityOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var devices = new List<NvidiaCudaDeviceInfo>();
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int separator = line.LastIndexOf(',');
            if (separator < 0)
                continue;

            string name = line[..separator].Trim();
            string capabilityText = line[(separator + 1)..].Trim();
            double? computeCapability = double.TryParse(capabilityText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : null;
            devices.Add(new NvidiaCudaDeviceInfo(name, computeCapability));
        }

        return devices;
    }

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

    internal sealed record CudaAutoBackendProbe(bool DriverAvailable, bool SupportedForAuto, string UnsupportedReason)
    {
        public static CudaAutoBackendProbe Unavailable { get; } = new(false, false, "");
    }

    internal sealed record NvidiaCudaDeviceInfo(string Name, double? ComputeCapability);
}
