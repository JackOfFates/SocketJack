using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace LlmRuntime.VisualStudio;

#pragma warning disable VSSDK007 // WPF event handlers intentionally fire UI tasks through FileAndForget.

[Guid("4c2bd475-628a-4311-a144-9c03196fd0c7")]
public sealed class SocketJackCopilotServersToolWindow : ToolWindowPane
{
    private readonly SocketJackCopilotServersControl _control;

    public SocketJackCopilotServersToolWindow() : base(null)
    {
        Caption = "SocketJack Copilot Servers";
        _control = new SocketJackCopilotServersControl();
        Content = _control;
    }

    public override void OnToolWindowCreated()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        base.OnToolWindowCreated();
        if (Package is LlmRuntimeVsPackage package)
            _control.Initialize(package);
    }
}

public sealed class SocketJackCopilotServersControl : UserControl
{
    private readonly ObservableCollection<SocketJackServerCandidate> _servers = new();
    private readonly ObservableCollection<SocketJackModelCandidate> _models = new();
    private readonly ICollectionView _serverView;
    private readonly TextBox _filterBox = new();
    private readonly ListBox _serverList = new();
    private readonly ComboBox _modelCombo = new();
    private readonly TextBlock _serverDetails = new();
    private readonly TextBlock _modelDetails = new();
    private readonly TextBlock _status = new();
    private readonly Button _refreshButton = new();
    private readonly Button _testButton = new();
    private readonly Button _configureButton = new();
    private readonly HttpClient _httpClient = new();
    private LlmRuntimeVsPackage? _package;
    private SocketJackCopilotSelectionOptions _options = new();
    private Process? _localProxyProcess;

    public SocketJackCopilotServersControl()
    {
        _serverView = CollectionViewSource.GetDefaultView(_servers);
        _serverView.Filter = FilterServer;
        BuildUi();
    }

    public void Initialize(LlmRuntimeVsPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _package = package;
        _options = package.GetSocketJackCopilotOptions();
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds));
        _ = RefreshServersAsync();
    }

    private void BuildUi()
    {
        var root = new Grid
        {
            Margin = new Thickness(12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        _refreshButton.Content = "Refresh";
        _refreshButton.MinWidth = 76;
        _refreshButton.Margin = new Thickness(0, 0, 8, 0);
        _refreshButton.Click += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(RefreshServersAsync).FileAndForget("SocketJack/CopilotServers/Refresh");
        DockPanel.SetDock(_refreshButton, Dock.Left);
        toolbar.Children.Add(_refreshButton);

        _filterBox.MinWidth = 180;
        _filterBox.VerticalContentAlignment = VerticalAlignment.Center;
        _filterBox.ToolTip = "Filter SocketJack servers.";
        _filterBox.TextChanged += (_, _) => _serverView.Refresh();
        toolbar.Children.Add(_filterBox);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        _serverList.ItemsSource = _serverView;
        _serverList.DisplayMemberPath = nameof(SocketJackServerCandidate.DisplayName);
        _serverList.Margin = new Thickness(0, 0, 12, 0);
        _serverList.SelectionChanged += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(OnServerSelectionChangedAsync).FileAndForget("SocketJack/CopilotServers/ModelDiscovery");
        body.Children.Add(_serverList);

        var details = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetColumn(details, 1);
        body.Children.Add(details);

        details.Children.Add(new TextBlock
        {
            Text = "Server",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        _serverDetails.TextWrapping = TextWrapping.Wrap;
        _serverDetails.Margin = new Thickness(0, 0, 0, 12);
        details.Children.Add(_serverDetails);

        details.Children.Add(new TextBlock
        {
            Text = "Model",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        _modelCombo.ItemsSource = _models;
        _modelCombo.Margin = new Thickness(0, 0, 0, 8);
        _modelCombo.SelectionChanged += (_, _) => UpdateModelDetails();
        details.Children.Add(_modelCombo);

        _modelDetails.TextWrapping = TextWrapping.Wrap;
        _modelDetails.Margin = new Thickness(0, 0, 0, 12);
        details.Children.Add(_modelDetails);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _testButton.Content = "Test Connection";
        _testButton.MinWidth = 116;
        _testButton.Margin = new Thickness(0, 0, 8, 0);
        _testButton.Click += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(TestConnectionAsync).FileAndForget("SocketJack/CopilotServers/TestConnection");
        actions.Children.Add(_testButton);

        _configureButton.Content = "Configure Copilot";
        _configureButton.MinWidth = 130;
        _configureButton.Click += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(ConfigureCopilotAsync).FileAndForget("SocketJack/CopilotServers/Configure");
        actions.Children.Add(_configureButton);
        details.Children.Add(actions);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = Brushes.Gray;
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        Content = root;
        UpdateButtons();
    }

    private async Task RefreshServersAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        SetBusy(true, "Loading SocketJack MasterList...");
        try
        {
            _options = _package?.GetSocketJackCopilotOptions() ?? _options;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
            string[] urls = SplitOptionList(_options.MasterListUrls).DefaultIfEmpty(SocketJackMasterListClient.DefaultMasterListUrls[0]).ToArray();
            var client = new SocketJackMasterListClient(_httpClient, urls);
            IReadOnlyList<SocketJackServerCandidate> servers = await client.GetServersAsync(cts.Token).ConfigureAwait(true);
            _servers.Clear();
            foreach (SocketJackServerCandidate server in servers)
                _servers.Add(server);

            _serverView.Refresh();
            _serverList.SelectedItem = _servers.FirstOrDefault(server => server.CanUseForCopilot) ?? _servers.FirstOrDefault();
            SetStatus("Loaded " + _servers.Count.ToString(CultureInfo.InvariantCulture) + " SocketJack servers. Select an online tools-capable server.");
        }
        catch (Exception ex)
        {
            SetStatus("MasterList refresh failed: " + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OnServerSelectionChangedAsync()
    {
        _models.Clear();
        UpdateServerDetails();
        UpdateModelDetails();

        if (_serverList.SelectedItem is not SocketJackServerCandidate server)
            return;

        if (!server.CanUseForCopilot)
        {
            SetStatus(server.DisabledReason, isError: true);
            return;
        }

        SetBusy(true, "Loading models from " + server.DisplayName + "...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, _options.RequestTimeoutSeconds)));
            var discovery = new SocketJackModelDiscoveryService(_httpClient);
            SocketJackModelDiscoveryResult result = await discovery.DiscoverModelsAsync(server, cts.Token).ConfigureAwait(true);
            foreach (SocketJackModelCandidate model in result.Models)
                _models.Add(model);

            _modelCombo.SelectedItem = _models.FirstOrDefault(model => model.IsSelectable) ?? _models.FirstOrDefault();
            string warningText = result.Warnings.Count == 0 ? "" : " Warnings: " + string.Join(" ", result.Warnings);
            SetStatus("Loaded " + _models.Count.ToString(CultureInfo.InvariantCulture) + " models." + warningText);
        }
        catch (Exception ex)
        {
            SetStatus("Model discovery failed: " + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestConnectionAsync()
    {
        if (_serverList.SelectedItem is not SocketJackServerCandidate server)
            return;

        SetBusy(true, "Testing " + server.EffectiveEndpoint + "...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
            var prober = new SocketJackEndpointAccessProber(_httpClient);
            SocketJackEndpointAccessResult result = await prober.ProbeAsync(server.EffectiveEndpoint, cts.Token).ConfigureAwait(true);
            SetStatus(result.Message + (result.CanUseDirectEndpoint ? " Direct address will be used." : " Local WebSocket proxy fallback will be used if enabled."), !result.CanUseDirectEndpoint);
        }
        catch (Exception ex)
        {
            SetStatus("Connection test failed: " + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConfigureCopilotAsync()
    {
        if (_package == null)
        {
            SetStatus("Visual Studio package is not initialized.", isError: true);
            return;
        }

        if (_serverList.SelectedItem is not SocketJackServerCandidate server ||
            _modelCombo.SelectedItem is not SocketJackModelCandidate model)
        {
            SetStatus("Select a server and model first.", isError: true);
            return;
        }

        if (!server.CanUseForCopilot || !model.IsSelectable)
        {
            SetStatus("The current server/model selection is not eligible: " + (server.DisabledReason.Length > 0 ? server.DisabledReason : model.EligibilityReason), isError: true);
            return;
        }

        SetBusy(true, "Configuring Copilot for " + server.DisplayName + " / " + model.DisplayName + "...");
        try
        {
            string solutionDirectory = await _package.GetCurrentSolutionDirectoryAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(solutionDirectory))
                throw new InvalidOperationException("Open a solution before configuring solution-local .vs\\mcp.json.");

            string bridgeProjectPath = ResolveBridgeProjectPath();
            BridgeLaunchInfo stdioLaunch = ResolveBridgeDllPath() is string bridgeDllPath && File.Exists(bridgeDllPath)
                ? SocketJackBridgeLaunchBuilder.CreateStdioLaunchFromDll(bridgeDllPath, server, model, _options.AuthToken)
                : SocketJackBridgeLaunchBuilder.CreateStdioLaunch(bridgeProjectPath, server, model, _options.AuthToken);

            var mcpWriter = new VisualStudioMcpConfigWriter();
            McpConfigWriteResult mcpResult = mcpWriter.Write(solutionDirectory, server, model, stdioLaunch);

            string modelAccessUrl = server.ModelApiBaseUrl;
            string routeMessage = "Direct SocketJack model address configured.";
            if (_options.UseLocalWebSocketProxyFallback)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
                SocketJackEndpointAccessResult access = await new SocketJackEndpointAccessProber(_httpClient).ProbeAsync(server.ModelApiBaseUrl, cts.Token).ConfigureAwait(true);
                if (!access.CanUseDirectEndpoint)
                {
                    int port = FindAvailablePort(_options.PreferredLocalProxyPort);
                    StartLocalProxy(server, model, port);
                    modelAccessUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                    routeMessage = "Direct model route did not answer; local WebSocket proxy configured at " + modelAccessUrl + ".";
                }
                else
                {
                    routeMessage = access.Message + " Direct SocketJack address configured.";
                }
            }

            OllamaByomWriteResult? ollamaResult = null;
            if (_options.UpdateOllamaByom)
            {
                ollamaResult = new VisualStudioOllamaByomConfigWriter().Write(server, model, modelAccessUrl);
            }

            string duplicatorMessage = await TryConfigureLocalDuplicatorAsync(server, model, modelAccessUrl).ConfigureAwait(true);
            SetStatus(
                "MCP: " + mcpResult.ServerKey + " written to " + mcpResult.Path +
                ". Ollama BYOM: " + (ollamaResult == null ? "not updated" : ollamaResult.CustomUrl + " / " + ollamaResult.ModelId) +
                ". " + routeMessage +
                " Visual Studio Copilot model override uses public MCP/Ollama paths only. " + duplicatorMessage);
        }
        catch (Exception ex)
        {
            SetStatus("Configure Copilot failed: " + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartLocalProxy(SocketJackServerCandidate server, SocketJackModelCandidate model, int port)
    {
        BridgeLaunchInfo launch = ResolveBridgeDllPath() is string bridgeDllPath && File.Exists(bridgeDllPath)
            ? SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunchFromDll(bridgeDllPath, server, model, port)
            : SocketJackBridgeLaunchBuilder.CreateHttpProxyLaunch(ResolveBridgeProjectPath(), server, model, port);

        if (_localProxyProcess != null && !_localProxyProcess.HasExited)
            return;

        var info = new ProcessStartInfo
        {
            FileName = launch.Command,
            Arguments = string.Join(" ", launch.Arguments.Select(QuoteArgument)),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        _localProxyProcess = Process.Start(info);
    }

    private async Task<string> TryConfigureLocalDuplicatorAsync(SocketJackServerCandidate server, SocketJackModelCandidate model, string modelAccessUrl)
    {
        if (string.IsNullOrWhiteSpace(_options.LocalJackLlmUrl))
            return "Local JackLLM duplicator skipped.";

        try
        {
            JsonObject payload = new()
            {
                ["serverId"] = server.Id,
                ["serverName"] = server.DisplayName,
                ["serverEndpoint"] = server.EffectiveEndpoint,
                ["modelAccessUrl"] = modelAccessUrl,
                ["modelId"] = model.Id,
                ["modelDisplayName"] = model.DisplayName
            };

            string baseUrl = _options.LocalJackLlmUrl.TrimEnd('/') + "/";
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(new Uri(new Uri(baseUrl), "api/copilot-duplicator"), content).ConfigureAwait(true);
            return response.IsSuccessStatusCode
                ? "Local JackLLM copilot duplicator updated."
                : "Local JackLLM copilot duplicator returned " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".";
        }
        catch (Exception ex)
        {
            return "Local JackLLM copilot duplicator unavailable: " + ex.Message;
        }
    }

    private void UpdateServerDetails()
    {
        if (_serverList.SelectedItem is not SocketJackServerCandidate server)
        {
            _serverDetails.Text = "";
            UpdateButtons();
            return;
        }

        _serverDetails.Text =
            "Endpoint: " + server.EffectiveEndpoint + Environment.NewLine +
            "Tools: " + (string.IsNullOrWhiteSpace(server.ToolsAllowed) ? (server.ToolsAdvertised ? "advertised" : "not advertised") : server.ToolsAllowed) + Environment.NewLine +
            "Status: " + (server.CanUseForCopilot ? "eligible" : server.DisabledReason) + Environment.NewLine +
            "Hardware: " + (string.IsNullOrWhiteSpace(server.Hardware) ? "not reported" : server.Hardware);
        UpdateButtons();
    }

    private void UpdateModelDetails()
    {
        if (_modelCombo.SelectedItem is not SocketJackModelCandidate model)
        {
            _modelDetails.Text = "";
            UpdateButtons();
            return;
        }

        _modelDetails.Text =
            "Id: " + model.Id + Environment.NewLine +
            "Status: " + model.EligibilityReason + Environment.NewLine +
            "Tools: " + (model.SupportsTools ? "yes" : "no") + ", Vision: " + (model.SupportsVision ? "yes" : "no") + Environment.NewLine +
            "Load: " + (model.IsLoaded ? "loaded" : model.RuntimeLoadable || model.WebChatDynamicLoadable ? "loadable" : "not loadable");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasServer = _serverList.SelectedItem is SocketJackServerCandidate;
        bool hasEligibleSelection = _serverList.SelectedItem is SocketJackServerCandidate server &&
            _modelCombo.SelectedItem is SocketJackModelCandidate model &&
            server.CanUseForCopilot &&
            model.IsSelectable;
        _testButton.IsEnabled = hasServer;
        _configureButton.IsEnabled = hasEligibleSelection;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _refreshButton.IsEnabled = !busy;
        _testButton.IsEnabled = !busy && _serverList.SelectedItem != null;
        _configureButton.IsEnabled = !busy && _serverList.SelectedItem is SocketJackServerCandidate server &&
            _modelCombo.SelectedItem is SocketJackModelCandidate model &&
            server.CanUseForCopilot &&
            model.IsSelectable;
        if (!string.IsNullOrWhiteSpace(message))
            SetStatus(message!);
    }

    private void SetStatus(string message, bool isError = false)
    {
        _status.Foreground = isError ? Brushes.Firebrick : Brushes.Gray;
        _status.Text = message;
    }

    private bool FilterServer(object obj)
    {
        if (obj is not SocketJackServerCandidate server)
            return false;
        string filter = _filterBox.Text?.Trim() ?? "";
        if (filter.Length == 0)
            return true;
        return server.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            server.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            server.EffectiveEndpoint.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string[] SplitOptionList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();
        return value!.Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static int FindAvailablePort(int preferredPort)
    {
        int start = preferredPort > 0 && preferredPort <= 65535 ? preferredPort : 11574;
        for (int port = start; port < Math.Min(65535, start + 100); port++)
        {
            if (IsPortAvailable(port))
                return port;
        }

        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return true;
            }
            finally
            {
                listener.Stop();
            }
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string ResolveBridgeProjectPath()
    {
        string baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        string path = Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj"));
        return path;
    }

    private static string? ResolveBridgeDllPath()
    {
        string baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        string path = Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll");
        return File.Exists(path) ? path : null;
    }
}

public sealed class SocketJackCopilotSelectionOptions : DialogPage
{
    [Category("Discovery")]
    [DisplayName("MasterList URLs")]
    [Description("Semicolon-separated SocketJack MasterList API URLs.")]
    public string MasterListUrls { get; set; } = string.Join(";", SocketJackMasterListClient.DefaultMasterListUrls);

    [Category("Discovery")]
    [DisplayName("Request timeout seconds")]
    [Description("Timeout for MasterList, model discovery, and connection tests.")]
    public int RequestTimeoutSeconds { get; set; } = 20;

    [Category("Visual Studio Copilot")]
    [DisplayName("Update Ollama BYOM")]
    [Description("Update Visual Studio's currently configured Ollama BYOM entry to the selected SocketJack server/model.")]
    public bool UpdateOllamaByom { get; set; } = true;

    [Category("Visual Studio Copilot")]
    [DisplayName("Use local WebSocket proxy fallback")]
    [Description("When the direct SocketJack proxy address does not expose a model route, start a loopback proxy that forwards through SocketJack Web Chat WebSocket.")]
    public bool UseLocalWebSocketProxyFallback { get; set; } = true;

    [Category("Visual Studio Copilot")]
    [DisplayName("Preferred local proxy port")]
    [Description("Preferred loopback port for the SocketJack local model proxy fallback.")]
    public int PreferredLocalProxyPort { get; set; } = 11574;

    [Category("Visual Studio Copilot")]
    [DisplayName("Local JackLLM URL")]
    [Description("Optional local JackLLM URL used for the copilot duplicator notification.")]
    public string LocalJackLlmUrl { get; set; } = "http://127.0.0.1:11436/";

    [Category("Security")]
    [DisplayName("SocketJack auth token")]
    [PasswordPropertyText(true)]
    [Description("Optional token passed to the bridge and redacted from logs.")]
    public string AuthToken { get; set; } = "";
}
