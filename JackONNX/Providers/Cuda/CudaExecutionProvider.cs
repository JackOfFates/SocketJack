using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using JackONNX;
using JackONNX.Runtime;

namespace JackONNX.Cuda;

public sealed class CudaExecutionProvider : IJackOnnxExecutionProvider, IJackOnnxSessionOptionsFactory
{
    public string Id => "onnxruntime.cuda";

    public JackOnnxExecutionProvider Kind => JackOnnxExecutionProvider.Cuda;

    public int Priority => 100;

    public Task<IReadOnlyList<JackOnnxDeviceInfo>> ProbeDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JackOnnxDeviceInfo> devices = TryQueryNvidiaSmi(cancellationToken);
        return Task.FromResult(devices);
    }

    public SessionOptions CreateSessionOptions(JackOnnxOptions? options = null, int deviceId = 0)
    {
        options ??= new JackOnnxOptions();
        if (!OnnxRuntimeNativeProviderResolver.TryEnsureProvider(JackOnnxExecutionProvider.Cuda, out string nativeDetail))
            throw new InvalidOperationException(nativeDetail);

        var sessionOptions = new SessionOptions
        {
            EnableMemoryPattern = options.EnableMemoryPattern
        };
        sessionOptions.AppendExecutionProvider_CUDA(deviceId);
        return sessionOptions;
    }

    public JackOnnxProviderCompatibility CheckCompatibility(JackOnnxOptions? options = null)
    {
        string selectedProvider = OnnxRuntimeNativeProviderResolver.SelectedProvider;
        if (IsDifferentSelectedProvider(selectedProvider))
            return BuildSkippedForActiveProviderCompatibility(selectedProvider);

        OnnxRuntimeNativeProviderResolver.TryEnsureCudaDependencyPaths();
        IReadOnlyList<string> missingDependencies = CudaNativeDependencyProbe.FindMissingDependencies();
        if (missingDependencies.Count > 0)
        {
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = false,
                CanCreateSessionOptions = false,
                Detail = CudaNativeDependencyProbe.BuildMissingDependencyDetail(missingDependencies)
            };
        }

        try
        {
            using var sessionOptions = CreateSessionOptions(options);
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = true,
                CanCreateSessionOptions = true,
                Detail = "CUDA SessionOptions created successfully."
            };
        }
        catch (Exception ex)
        {
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = false,
                CanCreateSessionOptions = false,
                Detail = ex.Message
            };
        }
    }

    private bool IsDifferentSelectedProvider(string selectedProvider) =>
        !string.IsNullOrWhiteSpace(selectedProvider) &&
        !string.Equals(selectedProvider, Kind.ToString(), StringComparison.OrdinalIgnoreCase);

    private JackOnnxProviderCompatibility BuildSkippedForActiveProviderCompatibility(string selectedProvider) =>
        new()
        {
            ProviderId = Id,
            Provider = Kind,
            IsAvailable = true,
            CanCreateSessionOptions = false,
            Detail = selectedProvider + " is the active ONNX Runtime native provider for this process; CUDA compatibility probing was skipped."
        };

    private static IReadOnlyList<JackOnnxDeviceInfo> TryQueryNvidiaSmi(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
                return Unavailable("nvidia-smi could not be started.");

            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return Unavailable("nvidia-smi did not respond before the probe timeout.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return Unavailable(string.IsNullOrWhiteSpace(error) ? "nvidia-smi returned no CUDA devices." : error.Trim());

            var devices = new List<JackOnnxDeviceInfo>();
            int index = 0;
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split(',', 2);
                string name = parts[0].Trim();
                long? memoryBytes = null;
                if (parts.Length > 1 && long.TryParse(parts[1].Trim(), out long memoryMiB))
                    memoryBytes = memoryMiB * 1024L * 1024L;

                devices.Add(new JackOnnxDeviceInfo
                {
                    Id = "cuda:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = string.IsNullOrWhiteSpace(name) ? "CUDA device " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : name,
                    Provider = JackOnnxExecutionProvider.Cuda,
                    IsAvailable = true,
                    DedicatedMemoryBytes = memoryBytes,
                    Detail = "NVIDIA GPU reported by nvidia-smi."
                });
                index++;
            }

            return devices.Count == 0 ? Unavailable("nvidia-smi returned no CUDA devices.") : devices;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable("CUDA probe failed: " + ex.Message);
        }
    }

    private static IReadOnlyList<JackOnnxDeviceInfo> Unavailable(string detail)
    {
        return
        [
            new JackOnnxDeviceInfo
            {
                Id = "cuda:0",
                Name = "CUDA unavailable",
                Provider = JackOnnxExecutionProvider.Cuda,
                IsAvailable = false,
                Detail = detail
            }
        ];
    }
}
