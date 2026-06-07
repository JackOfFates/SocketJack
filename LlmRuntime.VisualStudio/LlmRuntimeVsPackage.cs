using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace LlmRuntime.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("SocketJack LlmRuntime", "Local GGUF coding-agent integration for Visual Studio.", "0.1.4")]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(LlmRuntimeEndpointOptions), "SocketJack LlmRuntime", "Runtime Endpoint", 0, 0, true)]
[ProvideOptionPage(typeof(SocketJackCopilotSelectionOptions), "SocketJack LlmRuntime", "Copilot Servers", 0, 0, true)]
[ProvideToolWindow(typeof(SocketJackCopilotServersToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.SolutionExplorer)]
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class LlmRuntimeVsPackage : AsyncPackage
{
    public const string PackageGuidString = "1f4b99bb-0a4d-4f1c-9e3f-02d3bbf71289";

    protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            Register(commands, LlmRuntimeCommandIds.Chat, "chat");
            Register(commands, LlmRuntimeCommandIds.InlineCompletion, "inline-completion");
            Register(commands, LlmRuntimeCommandIds.NextEdit, "next-edit");
            Register(commands, LlmRuntimeCommandIds.Agent, "agent");
            Register(commands, LlmRuntimeCommandIds.Checkpoint, "checkpoints");
            Register(commands, LlmRuntimeCommandIds.Mcp, "mcp");
            Register(commands, LlmRuntimeCommandIds.ModelPicker, "model-picker");
            Register(commands, LlmRuntimeCommandIds.CodeReview, "code-review");
            Register(commands, LlmRuntimeCommandIds.WorkspaceIndex, "workspace-index");
            Register(commands, LlmRuntimeCommandIds.PromptFiles, "prompt-files");
            Register(commands, LlmRuntimeCommandIds.CustomInstructions, "custom-instructions");
            RegisterCopilotServers(commands);
            RegisterCreateMcpConfig(commands);
        }
    }

    private void Register(OleMenuCommandService commands, int commandId, string featureId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var command = new MenuCommand((_, _) => JoinableTaskFactory.Run(async () =>
        {
            string message;
            try
            {
                message = await CreateRouter().ExecuteAsync(featureId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                message = "LlmRuntime command failed: " + ex.Message;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowStatus(message);
        }), new CommandID(LlmRuntimeCommandIds.CommandSet, commandId));
        commands.AddCommand(command);
    }

    private void RegisterCreateMcpConfig(OleMenuCommandService commands)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var command = new MenuCommand((_, _) => JoinableTaskFactory.Run(async () =>
        {
            string message;
            try
            {
                message = await CreateSocketJackMcpConfigAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                message = "Create SocketJack MCP config failed: " + ex.Message;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowStatus(message);
        }), new CommandID(LlmRuntimeCommandIds.CommandSet, LlmRuntimeCommandIds.CreateMcpConfig));
        commands.AddCommand(command);
    }

    private void RegisterCopilotServers(OleMenuCommandService commands)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var command = new MenuCommand((_, _) => JoinableTaskFactory.Run(ShowCopilotServersToolWindowAsync),
            new CommandID(LlmRuntimeCommandIds.CommandSet, LlmRuntimeCommandIds.CopilotServers));
        commands.AddCommand(command);
    }

    private async System.Threading.Tasks.Task ShowCopilotServersToolWindowAsync()
    {
        ToolWindowPane window = await FindToolWindowAsync(typeof(SocketJackCopilotServersToolWindow), 0, true, DisposalToken).ConfigureAwait(true);
        if (window?.Frame == null)
            throw new NotSupportedException("Unable to create SocketJack Copilot Servers tool window.");

        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var frame = (IVsWindowFrame)window.Frame;
        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
    }

    private async System.Threading.Tasks.Task<string> CreateSocketJackMcpConfigAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        string solutionDirectory = await GetCurrentSolutionDirectoryAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(solutionDirectory))
            return "Open a solution first. SocketJack writes solution-local MCP config to .vs\\mcp.json.";

        SocketJackCopilotSelectionOptions options = GetSocketJackCopilotOptions();
        string mcpPath = Path.Combine(solutionDirectory, ".vs", "mcp.json");

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
            };
            string[] urls = SplitOptionList(options.MasterListUrls).DefaultIfEmpty(SocketJackMasterListClient.DefaultMasterListUrls[0]).ToArray();
            var masterList = new SocketJackMasterListClient(httpClient, urls);
            SocketJackServerCandidate server = (await masterList.GetServersAsync().ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.CanUseForCopilot)
                ?? throw new InvalidOperationException("No online tools-capable SocketJack server was found.");

            var modelDiscovery = new SocketJackModelDiscoveryService(httpClient);
            SocketJackModelCandidate model = (await modelDiscovery.DiscoverModelsAsync(server).ConfigureAwait(false))
                .Models
                .FirstOrDefault(candidate => candidate.IsSelectable)
                ?? throw new InvalidOperationException("No enabled tools-capable chat model was found on " + server.DisplayName + ".");

            BridgeLaunchInfo launch = ResolveBridgeDllPath() is string bridgeDllPath && File.Exists(bridgeDllPath)
                ? SocketJackBridgeLaunchBuilder.CreateStdioLaunchFromDll(bridgeDllPath, server, model, options.AuthToken)
                : SocketJackBridgeLaunchBuilder.CreateStdioLaunch(ResolveBridgeProjectPath(), server, model, options.AuthToken);

            McpConfigWriteResult result = new VisualStudioMcpConfigWriter().Write(solutionDirectory, server, model, launch);
            return "Created SocketJack MCP config at " + result.Path + Environment.NewLine +
                "Server: " + server.DisplayName + Environment.NewLine +
                "Model: " + model.Id + Environment.NewLine +
                "Entry: " + result.ServerKey;
        }
        catch (Exception ex)
        {
            CreateEmptyMcpConfigIfMissing(mcpPath);
            return "Created empty MCP config at " + mcpPath + Environment.NewLine +
                "SocketJack auto-discovery did not complete: " + ex.Message + Environment.NewLine +
                "Use Tools > SocketJack Copilot Servers to choose a server/model, then click Configure Copilot.";
        }
    }

    private LlmRuntimeCommandRouter CreateRouter()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var options = GetDialogPage(typeof(LlmRuntimeEndpointOptions)) as LlmRuntimeEndpointOptions
            ?? new LlmRuntimeEndpointOptions();
        return new LlmRuntimeCommandRouter(options.BuildEndpoint());
    }

    private void ShowStatus(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            this,
            message,
            "SocketJack LlmRuntime",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    internal SocketJackCopilotSelectionOptions GetSocketJackCopilotOptions()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetDialogPage(typeof(SocketJackCopilotSelectionOptions)) as SocketJackCopilotSelectionOptions
            ?? new SocketJackCopilotSelectionOptions();
    }

    internal async System.Threading.Tasks.Task<string> GetCurrentSolutionDirectoryAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(true) is not IVsSolution solution)
            return "";

        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(solution.GetSolutionInfo(out string solutionDirectory, out _, out _));
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
            return solutionDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return "";
    }

    private static void CreateEmptyMcpConfigIfMissing(string mcpPath)
    {
        string? directory = Path.GetDirectoryName(mcpPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (!File.Exists(mcpPath))
            File.WriteAllText(mcpPath, "{\r\n  \"servers\": {}\r\n}\r\n", new UTF8Encoding(false));
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

    private static string ResolveBridgeProjectPath()
    {
        string baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj"));
    }

    private static string? ResolveBridgeDllPath()
    {
        string baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        string path = Path.Combine(baseDirectory, "Bridge", "SocketJack.CopilotMcpBridge.dll");
        return File.Exists(path) ? path : null;
    }
}

public sealed class LlmRuntimeEndpointOptions : DialogPage
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 1234;

    [Category("Runtime Endpoint")]
    [DisplayName("Host")]
    [Description("Host name or IP address for the LlmRuntime HTTP API. Domains and dedicated external IP addresses are supported.")]
    public string Host { get; set; } = DefaultHost;

    [Category("Runtime Endpoint")]
    [DisplayName("Port")]
    [Description("TCP port for the LlmRuntime HTTP API.")]
    public int Port { get; set; } = DefaultPort;

    internal Uri BuildEndpoint()
    {
        string host = NormalizeHost(Host);
        int port = NormalizePort(Port);
        return new UriBuilder("http", host, port).Uri;
    }

    private static string NormalizeHost(string? host)
    {
        string normalized = string.IsNullOrWhiteSpace(host) ? DefaultHost : host!.Trim();

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? absolute) && !string.IsNullOrWhiteSpace(absolute.Host))
            return absolute.Host;

        string hostOnly = normalized;
        int slash = hostOnly.IndexOfAny(new[] { '/', '\\' });
        if (slash >= 0)
            hostOnly = hostOnly.Substring(0, slash);

        if (hostOnly.StartsWith("[", StringComparison.Ordinal) && hostOnly.Contains("]"))
        {
            int end = hostOnly.IndexOf(']');
            return hostOnly.Substring(1, end - 1);
        }

        int colon = hostOnly.LastIndexOf(':');
        if (colon > 0 &&
            hostOnly.IndexOf(':') == colon &&
            int.TryParse(hostOnly.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            hostOnly = hostOnly.Substring(0, colon);
        }

        return string.IsNullOrWhiteSpace(hostOnly) ? DefaultHost : hostOnly;
    }

    private static int NormalizePort(int port)
    {
        return port > 0 && port <= 65535 ? port : DefaultPort;
    }
}
