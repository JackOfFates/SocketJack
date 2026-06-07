using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SocketJack.Net;

namespace LlmRuntime;

public sealed class LlmRuntimeModelRuntimeAdapter : ILmVsProxyModelRuntime, IDisposable
{
    private readonly object _sync = new();
    private readonly LlmRuntimeOptions _options;
    private readonly IReadOnlyList<LlmToolDefinition> _builtInToolDefinitions;
    private readonly IReadOnlyList<ILlmTool> _builtInTools;
    private LlmRuntimeHost? _host;
    private bool _disposed;

    public LlmRuntimeModelRuntimeAdapter(
        LlmRuntimeOptions options,
        IEnumerable<LlmToolDefinition>? builtInToolDefinitions = null,
        IEnumerable<ILlmTool>? builtInTools = null)
    {
        _options = CopyOptions(options ?? throw new ArgumentNullException(nameof(options)));
        _builtInToolDefinitions = (builtInToolDefinitions ?? []).Where(definition => definition != null).ToList();
        _builtInTools = (builtInTools ?? []).Where(tool => tool != null).ToList();
        CurrentPort = _options.Port;
    }

    public string DisplayName => "LlmRuntime";

    public int CurrentPort { get; private set; }

    public string OpenAiBaseUrl => "http://127.0.0.1:" + CurrentPort.ToString(CultureInfo.InvariantCulture);

    public bool IsListening
    {
        get
        {
            lock (_sync)
                return _host?.IsListening == true;
        }
    }

    public event Action<string>? ServiceLog;

    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_host != null && _host.IsListening)
                return Task.CompletedTask;

            StartHost(_options.Port, allowFreePortFallback: true);
            return Task.CompletedTask;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _host?.Stop();
        }
    }

    private void StartHost(int port, bool allowFreePortFallback)
    {
        DisposeHost();
        if (allowFreePortFallback && !IsLoopbackPortAvailable(port))
        {
            int fallbackPort = FindFreeLoopbackPort();
            ServiceLog?.Invoke("Port " + port.ToString(CultureInfo.InvariantCulture) + " is already bound on loopback; starting LlmRuntime on " + fallbackPort.ToString(CultureInfo.InvariantCulture) + " instead.");
            port = fallbackPort;
        }

        var firstOptions = CopyOptions(_options);
        firstOptions.Port = port;
        _host = CreateConfiguredHost(firstOptions);
        _host.ServiceLog += OnHostServiceLog;
        CurrentPort = firstOptions.Port;
        if (_host.Start())
            return;

        if (!allowFreePortFallback)
            throw new InvalidOperationException("Unable to start LlmRuntime on port " + port.ToString(CultureInfo.InvariantCulture) + ".");

        DisposeHost();
        var fallbackOptions = CopyOptions(_options);
        fallbackOptions.Port = FindFreeLoopbackPort();
        _host = CreateConfiguredHost(fallbackOptions);
        _host.ServiceLog += OnHostServiceLog;
        CurrentPort = fallbackOptions.Port;
        if (!_host.Start())
            throw new InvalidOperationException("Unable to start LlmRuntime on a free loopback port.");
    }

    private static bool IsLoopbackPortAvailable(int port)
    {
        if (port <= 0 || port > 65535)
            return false;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    private LlmRuntimeHost CreateConfiguredHost(LlmRuntimeOptions options)
    {
        if (_builtInToolDefinitions.Count == 0 && _builtInTools.Count == 0)
            return new LlmRuntimeHost(options);

        var toolRegistry = new LlmToolRegistry(options);
        foreach (LlmToolDefinition definition in _builtInToolDefinitions)
            toolRegistry.UpsertDefinition(definition);

        var builtInTools = new List<ILlmTool> { new WindowsDesktopAutomationTool() };
        builtInTools.AddRange(_builtInTools);
        var toolInvoker = new LlmToolInvoker(toolRegistry, builtInTools: builtInTools);
        return new LlmRuntimeHost(options, toolRegistry: toolRegistry, toolInvoker: toolInvoker);
    }

    private void DisposeHost()
    {
        if (_host == null)
            return;

        _host.ServiceLog -= OnHostServiceLog;
        _host.Dispose();
        _host = null;
    }

    private void OnHostServiceLog(string message)
    {
        ServiceLog?.Invoke(message);
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static LlmRuntimeOptions CopyOptions(LlmRuntimeOptions options)
    {
        return new LlmRuntimeOptions
        {
            ModelRoot = options.ModelRoot,
            CompleteModelRoot = options.CompleteModelRoot,
            IncludeLmStudioModels = options.IncludeLmStudioModels,
            ToolRoot = options.ToolRoot,
            AgentRoot = options.AgentRoot,
            DefaultWorkspaceRoot = options.DefaultWorkspaceRoot,
            RuntimeConfigPath = options.RuntimeConfigPath,
            CompatibilityConfigPath = options.CompatibilityConfigPath,
            RestoreLoadedModelsOnStartup = options.RestoreLoadedModelsOnStartup,
            Port = options.Port,
            DefaultContextLength = options.DefaultContextLength,
            DefaultGpuLayerCount = options.DefaultGpuLayerCount,
            DefaultEvalBatchSize = options.DefaultEvalBatchSize,
            DefaultFlashAttention = options.DefaultFlashAttention,
            DefaultOffloadKvCacheToGpu = options.DefaultOffloadKvCacheToGpu,
            DefaultBackend = options.DefaultBackend,
            AllowBackendFallback = options.AllowBackendFallback,
            RequireGpuForAutoBackend = options.RequireGpuForAutoBackend,
            PreventCpuBackendFallback = options.PreventCpuBackendFallback,
            DirectMlGgufRunnerPath = options.DirectMlGgufRunnerPath,
            DirectMlGgufRunnerArguments = options.DirectMlGgufRunnerArguments,
            VllmPythonPath = options.VllmPythonPath,
            VllmBaseUrl = options.VllmBaseUrl,
            VllmExtraArguments = options.VllmExtraArguments,
            VllmStartupTimeout = options.VllmStartupTimeout,
            LocalPrivacyMode = options.LocalPrivacyMode,
            DownloadTimeout = options.DownloadTimeout,
            OnnxConversionWorkerPath = options.OnnxConversionWorkerPath,
            OnnxConversionWorkerArguments = options.OnnxConversionWorkerArguments,
            OnnxConversionTimeout = options.OnnxConversionTimeout,
            ServerName = options.ServerName,
            ControlAuthToken = options.ControlAuthToken
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LlmRuntimeModelRuntimeAdapter));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisposeHost();
        }
    }
}
