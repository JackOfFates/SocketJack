namespace LlmRuntime.VisualStudio2026;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmRuntime.VisualStudio;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

internal sealed class SocketJackCopilotConfigurator
{
    private static Process? localProxyProcess;
    private static int? localProxyPort;
    private static string localProxyServerKey = "";
    private static string localProxyModelKey = "";

    private readonly VisualStudioExtensibility extensibility;
    private readonly HttpClient httpClient;
    private readonly SocketJackCopilotBrowserCache browserCache = new();

    public SocketJackCopilotConfigurator(VisualStudioExtensibility extensibility, HttpClient httpClient)
    {
        this.extensibility = extensibility;
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SocketJackServerCandidate>> GetServersAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var client = new SocketJackMasterListClient(this.httpClient);
        IReadOnlyList<SocketJackServerCandidate> servers = await client.GetServersAsync(timeout.Token);
        this.browserCache.SaveServers(servers);
        return servers;
    }

    public async Task<SocketJackModelDiscoveryResult> GetModelsAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var discovery = new SocketJackModelDiscoveryService(this.httpClient);
        SocketJackModelDiscoveryResult result = await discovery.DiscoverModelsAsync(server, timeout.Token);
        this.browserCache.SaveModels(server, result);
        return result;
    }

    public IReadOnlyList<SocketJackServerCandidate> GetCachedServers()
    {
        return this.browserCache.LoadServers();
    }

    public bool TryGetCachedModels(SocketJackServerCandidate server, out SocketJackModelDiscoveryResult result)
    {
        return this.browserCache.TryLoadModels(server, out result);
    }

    public async Task<SocketJackEndpointAccessResult> TestModelRouteAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        try
        {
            return await prober.ProbeAsync(server.ModelApiBaseUrl, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SocketJackEndpointAccessResult(false, "Direct endpoint probe timed out.", "");
        }
    }

    public async Task<SocketJackEndpointAccessResult> TestChatRouteAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        try
        {
            return await prober.ProbeChatAsync(server.ModelApiBaseUrl, model.Id, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SocketJackEndpointAccessResult(false, "Direct chat endpoint probe timed out; local WebSocket proxy is required.", "");
        }
    }

    public async Task<SocketJackEndpointAccessResult> TestSocketJackFallbackRouteAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        try
        {
            return await prober.ProbeSocketJackFallbackAsync(server.EffectiveEndpoint, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SocketJackEndpointAccessResult(false, "SocketJack fallback route probe timed out.", "");
        }
    }

    public async Task<SocketJackConfigureResult> ConfigureFirstEligibleAsync(string authToken, CancellationToken cancellationToken)
    {
        return await this.ConfigureFirstEligibleAsync(authToken, "", cancellationToken);
    }

    public async Task<SocketJackConfigureResult> ConfigureFirstEligibleAsync(string authToken, string authUserName, CancellationToken cancellationToken)
    {
        SocketJackServerCandidate server = (await this.GetServersAsync(cancellationToken)).FirstOrDefault(candidate => candidate.CanUseForCopilot)
            ?? throw new InvalidOperationException("No online tools-capable SocketJack server was found.");
        SocketJackModelCandidate model = (await this.GetModelsAsync(server, cancellationToken)).Models.FirstOrDefault(candidate => candidate.IsSelectable)
            ?? throw new InvalidOperationException("No enabled tools-capable model was found on " + server.DisplayName + ".");
        return await this.ConfigureAsync(server, model, authToken, authUserName, cancellationToken);
    }

    public async Task<SocketJackConfigureResult> ConfigureAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, string authToken, CancellationToken cancellationToken)
    {
        return await this.ConfigureAsync(server, model, authToken, "", cancellationToken);
    }

    public async Task<SocketJackConfigureResult> ConfigureAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, string authToken, string authUserName, CancellationToken cancellationToken)
    {
        string solutionDirectory = await this.GetCurrentSolutionDirectoryAsync(cancellationToken)
            ?? throw new InvalidOperationException("Open a solution before creating solution-local .vs\\mcp.json.");

        BridgeLaunchInfo stdioLaunch = this.CreateStdioLaunch(server, model, authToken, authUserName);
        McpConfigWriteResult mcpResult = new VisualStudioMcpConfigWriter().Write(solutionDirectory, server, model, stdioLaunch);

        string modelAccessUrl = server.ModelApiBaseUrl;
        string routeMessage = "Direct SocketJack model address configured.";
        if (ShouldPreferPackagedLocalProxy(server))
        {
            int port = this.StartLocalProxy(server, model, authToken, authUserName);
            modelAccessUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            routeMessage = "Packaged local SocketJack bridge configured at " + modelAccessUrl + ".";
        }
        else
        {
            SocketJackEndpointAccessResult access = await this.TestChatRouteAsync(server, model, cancellationToken);
            if (!access.CanUseDirectEndpoint)
            {
                SocketJackEndpointAccessResult fallback = await this.TestSocketJackFallbackRouteAsync(server, cancellationToken);
                int port = this.StartLocalProxy(server, model, authToken, authUserName);
                modelAccessUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                routeMessage = "Direct model route did not answer; " + fallback.Message + " Packaged local SocketJack bridge configured at " + modelAccessUrl + ".";
            }
            else
            {
                routeMessage = access.Message + " Direct SocketJack address configured.";
            }
        }

        OllamaByomWriteResult ollamaResult = new VisualStudioOllamaByomConfigWriter().Write(server, model, modelAccessUrl);
        SocketJackCopilotSelectionStore.Save(server, model, ollamaResult.CustomUrl);
        string duplicatorMessage = await this.TryConfigureLocalDuplicatorAsync(server, model, modelAccessUrl, cancellationToken);

        return new SocketJackConfigureResult(
            server.DisplayName,
            model.Id,
            mcpResult.Path,
            mcpResult.ServerKey,
            ollamaResult.Path,
            ollamaResult.CustomUrl,
            ollamaResult.ModelId,
            routeMessage,
            duplicatorMessage);
    }

    private async Task<string?> GetCurrentSolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        IQueryResults<ISolutionSnapshot> solutions = await this.extensibility.Workspaces().QuerySolutionAsync(solution => solution.With(solution => solution.Path), cancellationToken);
        foreach (ISolutionSnapshot solution in solutions)
        {
            if (string.IsNullOrWhiteSpace(solution.Path))
            {
                continue;
            }

            string? directory = Path.GetDirectoryName(solution.Path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private BridgeLaunchInfo CreateStdioLaunch(SocketJackServerCandidate server, SocketJackModelCandidate model, string authToken, string authUserName)
    {
        string? bridgeExecutablePath = ResolveBridgeExecutablePath();
        if (!string.IsNullOrWhiteSpace(bridgeExecutablePath))
        {
            return SocketJackBridgeLaunchBuilder.CreateStdioLaunchFromExecutable(bridgeExecutablePath, server, model, authToken, authUserName);
        }

        string? bridgeDllPath = ResolveBridgeDllPath();
        if (!string.IsNullOrWhiteSpace(bridgeDllPath))
        {
            return SocketJackBridgeLaunchBuilder.CreateStdioLaunchFromDll(bridgeDllPath, server, model, authToken, authUserName);
        }

        if (!string.IsNullOrWhiteSpace(server.DirectMcpUrl))
        {
            return new BridgeLaunchInfo("dotnet", Array.Empty<string>());
        }

        string bridgeProjectPath = ResolveBridgeProjectPath();
        if (File.Exists(bridgeProjectPath))
        {
            return SocketJackBridgeLaunchBuilder.CreateStdioLaunch(bridgeProjectPath, server, model, authToken, authUserName);
        }

        throw new InvalidOperationException("The SocketJack MCP bridge was not found in the extension package.");
    }

    private int StartLocalProxy(SocketJackServerCandidate server, SocketJackModelCandidate model, string authToken, string authUserName)
    {
        string serverKey = server.EffectiveEndpoint + "|" + server.Id + "|" + (string.IsNullOrWhiteSpace(authToken) ? "" : authToken.GetHashCode().ToString(CultureInfo.InvariantCulture)) + "|" + authUserName.Trim();
        string modelKey = model.Id;
        if (localProxyProcess != null &&
            !localProxyProcess.HasExited &&
            localProxyPort.HasValue &&
            string.Equals(localProxyServerKey, serverKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(localProxyModelKey, modelKey, StringComparison.OrdinalIgnoreCase))
        {
            return localProxyPort.Value;
        }

        if (localProxyProcess != null && !localProxyProcess.HasExited)
        {
            try
            {
                localProxyProcess.Kill(entireProcessTree: true);
                localProxyProcess.WaitForExit(2000);
            }
            catch (Exception)
            {
            }
        }

        int port = FindMatchingHealthyProxyPort(server, model, 11574) ?? FindAvailablePort(11574);
        BridgeLaunchInfo launch;
        string? bridgeExecutablePath = ResolveBridgeExecutablePath();
        if (!string.IsNullOrWhiteSpace(bridgeExecutablePath))
        {
            launch = SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromExecutable(bridgeExecutablePath, server, model, port, authToken, authUserName);
        }
        else if (ResolveBridgeDllPath() is string bridgeDllPath && !string.IsNullOrWhiteSpace(bridgeDllPath))
        {
            launch = SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromDll(bridgeDllPath, server, model, port, authToken, authUserName);
        }
        else
        {
            string bridgeProjectPath = ResolveBridgeProjectPath();
            if (!File.Exists(bridgeProjectPath))
            {
                throw new InvalidOperationException("The SocketJack local WebSocket proxy bridge was not found in the extension package.");
            }

            launch = SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunch(bridgeProjectPath, server, model, port, authToken, authUserName);
        }

        var info = new ProcessStartInfo
        {
            FileName = launch.Command,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (string argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        localProxyProcess = Process.Start(info) ?? throw new InvalidOperationException("The SocketJack local WebSocket proxy could not be started.");
        localProxyPort = port;
        localProxyServerKey = serverKey;
        localProxyModelKey = modelKey;
        return port;
    }

    private static int? FindMatchingHealthyProxyPort(SocketJackServerCandidate server, SocketJackModelCandidate model, int preferredPort)
    {
        int start = preferredPort > 0 && preferredPort <= 65535 ? preferredPort : 11574;
        int end = Math.Min(65535, start + 49);
        for (int port = start; port <= end; port++)
        {
            if (IsMatchingHealthyProxyPort(server, model, port))
                return port;
        }

        return null;
    }

    private static bool IsMatchingHealthyProxyPort(SocketJackServerCandidate server, SocketJackModelCandidate model, int port)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/socketjack-proxy-health");
            using HttpResponseMessage response = client.Send(request, HttpCompletionOption.ResponseContentRead);
            if (!response.IsSuccessStatusCode)
                return false;

            using Stream stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            JsonObject root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            string selectedServer = root["selectedServer"]?.ToString() ?? "";
            string selectedModel = root["selectedModel"]?.ToString() ?? "";
            string endpoint = (root["endpoint"]?.ToString() ?? "").TrimEnd('/');
            string expectedEndpoint = (server.EffectiveEndpoint ?? "").TrimEnd('/');

            bool serverMatches =
                string.Equals(selectedServer, server.Id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(endpoint) && string.Equals(endpoint, expectedEndpoint, StringComparison.OrdinalIgnoreCase));
            bool modelMatches = string.IsNullOrWhiteSpace(model.Id) ||
                string.Equals(selectedModel, model.Id, StringComparison.OrdinalIgnoreCase);

            return serverMatches && modelMatches;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldPreferPackagedLocalProxy(SocketJackServerCandidate server)
    {
        if (!Uri.TryCreate(server.EffectiveEndpoint, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Equals("/proxy", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> TryConfigureLocalDuplicatorAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, string modelAccessUrl, CancellationToken cancellationToken)
    {
        try
        {
            JsonObject payload = new()
            {
                ["serverId"] = server.Id,
                ["serverName"] = server.DisplayName,
                ["serverEndpoint"] = server.EffectiveEndpoint,
                ["modelAccessUrl"] = modelAccessUrl,
                ["modelId"] = model.Id,
                ["modelDisplayName"] = model.Id,
            };

            using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync("http://127.0.0.1:11436/api/copilot-duplicator", payload, cancellationToken);
            return response.IsSuccessStatusCode
                ? "Local JackLLM copilot duplicator updated."
                : "Local JackLLM duplicator skipped; packaged VSIX bridge remains configured.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Local JackLLM duplicator skipped; packaged VSIX bridge remains configured.";
        }
    }

    private static string? ResolveBridgeExecutablePath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string assemblyDirectory = Path.GetDirectoryName(typeof(SocketJackCopilotConfigurator).Assembly.Location) ?? baseDirectory;
        string[] candidates =
        [
            Path.Combine(assemblyDirectory, "Bridge", "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(assemblyDirectory, "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(baseDirectory, "SocketJack.CopilotMcpBridge.exe"),
            Path.GetFullPath(Path.Combine(assemblyDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\bin\Release\net8.0\publish\SocketJack.CopilotMcpBridge.exe")),
            Path.GetFullPath(Path.Combine(assemblyDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\bin\Debug\net8.0\publish\SocketJack.CopilotMcpBridge.exe")),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveBridgeDllPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string assemblyDirectory = Path.GetDirectoryName(typeof(SocketJackCopilotConfigurator).Assembly.Location) ?? baseDirectory;
        string[] candidates =
        [
            Path.Combine(assemblyDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(assemblyDirectory, "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(baseDirectory, "SocketJack.CopilotMcpBridge.dll"),
            Path.GetFullPath(Path.Combine(assemblyDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\bin\Release\net8.0\publish\SocketJack.CopilotMcpBridge.dll")),
            Path.GetFullPath(Path.Combine(assemblyDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\bin\Debug\net8.0\publish\SocketJack.CopilotMcpBridge.dll")),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveBridgeProjectPath()
    {
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(assemblyDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj"));
    }

    private static int FindAvailablePort(int preferredPort)
    {
        for (int port = preferredPort; port < preferredPort + 50; port++)
        {
            try
            {
                TcpListener listener = new(IPAddress.Loopback, port);
                listener.Start();
                int selectedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return selectedPort;
            }
            catch (SocketException)
            {
            }
        }

        TcpListener fallback = new(IPAddress.Loopback, 0);
        fallback.Start();
        int fallbackPort = ((IPEndPoint)fallback.LocalEndpoint).Port;
        fallback.Stop();
        return fallbackPort;
    }
}

internal static class SocketJackCopilotSelectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SocketJack",
        "VisualStudio",
        "CopilotSelection.json");

    public static void Save(SocketJackServerCandidate server, SocketJackModelCandidate model, string modelAccessUrl)
    {
        if (server == null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        string path = DefaultPath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject root = new()
        {
            ["version"] = 1,
            ["serverEndpoint"] = server.EffectiveEndpoint,
            ["serverId"] = server.Id,
            ["serverName"] = server.DisplayName,
            ["modelId"] = model.Id,
            ["modelDisplayName"] = model.Id,
            ["modelAccessUrl"] = modelAccessUrl.TrimEnd('/'),
            ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }

    public static SocketJackStoredCopilotSelection Load()
    {
        string path = DefaultPath;
        if (!File.Exists(path))
        {
            return new SocketJackStoredCopilotSelection();
        }

        try
        {
            JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            return new SocketJackStoredCopilotSelection
            {
                ServerEndpoint = FirstString(root, "serverEndpoint"),
                ServerId = FirstString(root, "serverId"),
                ServerName = FirstString(root, "serverName"),
                ModelId = FirstString(root, "modelId"),
                ModelDisplayName = FirstString(root, "modelDisplayName"),
                ModelAccessUrl = FirstString(root, "modelAccessUrl"),
            };
        }
        catch (Exception)
        {
            return new SocketJackStoredCopilotSelection();
        }
    }

    private static string FirstString(JsonObject root, string name)
    {
        return root[name]?.ToString()?.Trim() ?? "";
    }
}

internal sealed class SocketJackStoredCopilotSelection
{
    public string ServerEndpoint { get; init; } = "";

    public string ServerId { get; init; } = "";

    public string ServerName { get; init; } = "";

    public string ModelId { get; init; } = "";

    public string ModelDisplayName { get; init; } = "";

    public string ModelAccessUrl { get; init; } = "";

    public bool HasLocalProxy =>
        !string.IsNullOrWhiteSpace(this.ModelAccessUrl) &&
        Uri.TryCreate(this.ModelAccessUrl, UriKind.Absolute, out Uri? uri) &&
        (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
        uri.Port > 0;

    public int LocalProxyPort
    {
        get
        {
            if (!Uri.TryCreate(this.ModelAccessUrl, UriKind.Absolute, out Uri? uri))
            {
                return 0;
            }

            return uri.Port > 0 ? uri.Port : 0;
        }
    }
}

internal static class SocketJackLocalProxySupervisor
{
    private static readonly object Sync = new();
    private static bool startQueued;

    public static void StartBestEffortFromStoredSelection()
    {
        lock (Sync)
        {
            if (startQueued)
            {
                return;
            }

            startQueued = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureActiveProxyFromStoredSelectionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                lock (Sync)
                {
                    startQueued = false;
                }
            }
        });
    }

    internal static async Task<bool> EnsureActiveProxyFromStoredSelectionAsync(CancellationToken cancellationToken)
    {
        SocketJackStoredCopilotSelection selection = SocketJackCopilotSelectionStore.Load();
        if (!selection.HasLocalProxy ||
            string.IsNullOrWhiteSpace(selection.ServerEndpoint) ||
            string.IsNullOrWhiteSpace(selection.ModelId))
        {
            return false;
        }

        int port = selection.LocalProxyPort;
        if (port <= 0)
        {
            return false;
        }

        if (await IsMatchingProxyHealthyAsync(port, selection.ModelId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (IsTcpPortListening(port))
        {
            return false;
        }

        string? bridgeExecutablePath = ResolveBridgeExecutablePath();
        string? bridgeDllPath = ResolveBridgeDllPath();
        if (string.IsNullOrWhiteSpace(bridgeExecutablePath) && string.IsNullOrWhiteSpace(bridgeDllPath))
        {
            return false;
        }

        SocketJackAuthState authState = new SocketJackVisualStudioAuthService().Load();
        var server = new SocketJackServerCandidate
        {
            Id = string.IsNullOrWhiteSpace(selection.ServerId) ? selection.ServerName : selection.ServerId,
            DisplayName = string.IsNullOrWhiteSpace(selection.ServerName) ? selection.ServerId : selection.ServerName,
            Endpoint = selection.ServerEndpoint
        };
        var model = new SocketJackModelCandidate
        {
            Id = selection.ModelId,
            DisplayName = string.IsNullOrWhiteSpace(selection.ModelDisplayName) ? selection.ModelId : selection.ModelDisplayName
        };
        BridgeLaunchInfo launch = !string.IsNullOrWhiteSpace(bridgeExecutablePath)
            ? SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromExecutable(
                bridgeExecutablePath,
                server,
                model,
                port,
                authState.AccessToken,
                authState.UserName)
            : SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromDll(
                bridgeDllPath!,
                server,
                model,
                port,
                authState.AccessToken,
                authState.UserName);

        var info = new ProcessStartInfo
        {
            FileName = launch.Command,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (string argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process? _ = Process.Start(info);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsMatchingProxyHealthyAsync(port, selection.ModelId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> IsMatchingProxyHealthyAsync(int port, string modelId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
            using HttpResponseMessage response = await client.GetAsync(
                "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/socketjack-proxy-health",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonObject root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            string selectedModel = root["selectedModel"]?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(modelId) ||
                string.Equals(selectedModel, modelId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsTcpPortListening(int port)
    {
        try
        {
            using TcpClient client = new();
            IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
            bool connected = pending.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            if (!connected)
            {
                return false;
            }

            client.EndConnect(pending);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static string? ResolveBridgeDllPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string assemblyDirectory = Path.GetDirectoryName(typeof(SocketJackLocalProxySupervisor).Assembly.Location) ?? baseDirectory;
        string[] candidates =
        [
            Path.Combine(assemblyDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(assemblyDirectory, "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll"),
            Path.Combine(baseDirectory, "SocketJack.CopilotMcpBridge.dll"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveBridgeExecutablePath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string assemblyDirectory = Path.GetDirectoryName(typeof(SocketJackLocalProxySupervisor).Assembly.Location) ?? baseDirectory;
        string[] candidates =
        [
            Path.Combine(assemblyDirectory, "Bridge", "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(assemblyDirectory, "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.exe"),
            Path.Combine(baseDirectory, "SocketJack.CopilotMcpBridge.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed class SocketJackConfigureResult
{
    public SocketJackConfigureResult(
        string serverName,
        string modelName,
        string mcpPath,
        string mcpServerKey,
        string ollamaPath,
        string ollamaUrl,
        string ollamaModelId,
        string routeMessage,
        string duplicatorMessage)
    {
        this.ServerName = serverName;
        this.ModelName = modelName;
        this.McpPath = mcpPath;
        this.McpServerKey = mcpServerKey;
        this.OllamaPath = ollamaPath;
        this.OllamaUrl = ollamaUrl;
        this.OllamaModelId = ollamaModelId;
        this.RouteMessage = routeMessage;
        this.DuplicatorMessage = duplicatorMessage;
    }

    public string ServerName { get; }
    public string ModelName { get; }
    public string McpPath { get; }
    public string McpServerKey { get; }
    public string OllamaPath { get; }
    public string OllamaUrl { get; }
    public string OllamaModelId { get; }
    public string RouteMessage { get; }
    public string DuplicatorMessage { get; }

    public string ToUserMessage()
    {
        return "SocketJack configured for " + this.ServerName + " / " + this.ModelName + Environment.NewLine +
            "MCP: " + this.McpServerKey + " written to " + this.McpPath + Environment.NewLine +
            "Ollama BYOM: " + this.OllamaUrl + " / " + this.OllamaModelId + " written to " + this.OllamaPath + Environment.NewLine +
            this.RouteMessage + Environment.NewLine +
            "Visual Studio Copilot BYOM uses the packaged SocketJack bridge when a local proxy URL is configured. " + this.DuplicatorMessage;
    }
}
