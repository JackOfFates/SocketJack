using Microsoft.ML.OnnxRuntime;
using JackONNX;
using JackONNX.Runtime;

namespace JackONNX.DirectML;

public sealed class DirectMlExecutionProvider : IJackOnnxExecutionProvider, IJackOnnxSessionOptionsFactory
{
    public string Id => "onnxruntime.directml";

    public JackOnnxExecutionProvider Kind => JackOnnxExecutionProvider.DirectML;

    public int Priority => 200;

    public Task<IReadOnlyList<JackOnnxDeviceInfo>> ProbeDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool windows = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
        IReadOnlyList<JackOnnxDeviceInfo> devices =
        [
            new JackOnnxDeviceInfo
            {
                Id = "directml:0",
                Name = windows ? "Default DirectML adapter" : "DirectML unavailable",
                Provider = JackOnnxExecutionProvider.DirectML,
                IsAvailable = windows,
                Detail = windows
                    ? "DirectML provider package is installed and this Windows version can host DirectML."
                    : "DirectML requires Windows 10 version 1809 or newer."
            }
        ];
        return Task.FromResult(devices);
    }

    public SessionOptions CreateSessionOptions(JackOnnxOptions? options = null, int deviceId = 0)
    {
        options ??= new JackOnnxOptions();
        if (!OnnxRuntimeNativeProviderResolver.TryEnsureProvider(JackOnnxExecutionProvider.DirectML, out string nativeDetail))
            throw new InvalidOperationException(nativeDetail);

        var sessionOptions = new SessionOptions
        {
            EnableMemoryPattern = false
        };
        sessionOptions.AppendExecutionProvider_DML(deviceId);
        return sessionOptions;
    }

    public JackOnnxProviderCompatibility CheckCompatibility(JackOnnxOptions? options = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = false,
                CanCreateSessionOptions = false,
                Detail = "DirectML requires Windows 10 version 1809 or newer."
            };
        }

        string selectedProvider = OnnxRuntimeNativeProviderResolver.SelectedProvider;
        if (IsDifferentSelectedProvider(selectedProvider))
            return BuildSkippedForActiveProviderCompatibility(selectedProvider);

        try
        {
            using var sessionOptions = CreateSessionOptions(options);
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = true,
                CanCreateSessionOptions = true,
                Detail = "DirectML SessionOptions created successfully."
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
            Detail = selectedProvider + " is the active ONNX Runtime native provider for this process; DirectML compatibility probing was skipped."
        };
}
