namespace LlmRuntime.VisualStudio2026;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
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
        return await client.GetServersAsync(timeout.Token);
    }

    public async Task<SocketJackModelDiscoveryResult> GetModelsAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var discovery = new SocketJackModelDiscoveryService(this.httpClient);
        return await discovery.DiscoverModelsAsync(server, timeout.Token);
    }

    public async Task<SocketJackEndpointAccessResult> TestModelRouteAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        return await prober.ProbeAsync(server.ModelApiBaseUrl, timeout.Token);
    }

    public async Task<SocketJackEndpointAccessResult> TestChatRouteAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        return await prober.ProbeChatAsync(server.ModelApiBaseUrl, model.Id, timeout.Token);
    }

    public async Task<SocketJackEndpointAccessResult> TestSocketJackFallbackRouteAsync(SocketJackServerCandidate server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var prober = new SocketJackEndpointAccessProber(this.httpClient);
        return await prober.ProbeSocketJackFallbackAsync(server.EffectiveEndpoint, timeout.Token);
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
        SocketJackEndpointAccessResult access = await this.TestChatRouteAsync(server, model, cancellationToken);
        if (!access.CanUseDirectEndpoint)
        {
            SocketJackEndpointAccessResult fallback = await this.TestSocketJackFallbackRouteAsync(server, cancellationToken);
            if (!fallback.CanUseDirectEndpoint)
            {
                throw new InvalidOperationException(access.Message + " " + fallback.Message);
            }

            int port = this.StartLocalProxy(server, model, authToken, authUserName);
            modelAccessUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            routeMessage = "Direct model route did not answer; " + fallback.Message + " Local WebSocket proxy configured at " + modelAccessUrl + ".";
        }
        else
        {
            routeMessage = access.Message + " Direct SocketJack address configured.";
        }

        OllamaByomWriteResult ollamaResult = new VisualStudioOllamaByomConfigWriter().Write(server, model, modelAccessUrl);
        string duplicatorMessage = await this.TryConfigureLocalDuplicatorAsync(server, model, modelAccessUrl, cancellationToken);

        return new SocketJackConfigureResult(
            server.DisplayName,
            model.DisplayName,
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

        int port = FindAvailablePort(11574);
        BridgeLaunchInfo launch;
        string? bridgeDllPath = ResolveBridgeDllPath();
        if (!string.IsNullOrWhiteSpace(bridgeDllPath))
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
                ["modelDisplayName"] = model.DisplayName,
            };

            using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync("http://127.0.0.1:11436/api/copilot-duplicator", payload, cancellationToken);
            return response.IsSuccessStatusCode
                ? "Local JackLLM copilot duplicator updated."
                : "Local JackLLM copilot duplicator returned " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Local JackLLM copilot duplicator unavailable: " + ex.Message;
        }
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
            "Visual Studio Copilot model override uses public MCP/Ollama paths only. " + this.DuplicatorMessage;
    }
}
