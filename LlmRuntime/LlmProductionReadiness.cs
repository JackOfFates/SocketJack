using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LlmRuntime;

public sealed class LlmProductionReadinessService
{
    private readonly LlmRuntimeOptions _options;
    private readonly LlmModelRegistry _models;
    private readonly LlmToolRegistry _tools;
    private readonly LlmAgentRuntime _agents;
    private readonly LlmGitHubWorkflowService _github;
    private readonly DateTimeOffset _startedAtUtc;

    public LlmProductionReadinessService(
        LlmRuntimeOptions options,
        LlmModelRegistry models,
        LlmToolRegistry tools,
        LlmAgentRuntime agents,
        LlmGitHubWorkflowService github,
        DateTimeOffset startedAtUtc)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _startedAtUtc = startedAtUtc;
    }

    public LlmOnboardingChecklist BuildOnboardingChecklist()
    {
        IReadOnlyList<LlmModelInfo> models = _models.ListModels();
        bool ghAvailable = CommandExists("gh");
        DirectMlGgufRunnerStatus directMl = DirectMlGgufRunnerDiscovery.GetStatus(_options);
        bool gitAvailable = CommandExists("git");
        bool dotnetAvailable = CommandExists("dotnet");
        bool hasModel = models.Count > 0;

        return new LlmOnboardingChecklist
        {
            PercentComplete = Percent([
                true,
                Directory.Exists(_options.ModelRoot),
                hasModel,
                dotnetAvailable,
                gitAvailable,
                ghAvailable,
                File.Exists(Path.Combine(Environment.CurrentDirectory, "JackLLM", "README.md")) ||
                File.Exists(Path.Combine(Environment.CurrentDirectory, "README.md"))
            ]),
            Items =
            [
                Item("runtime", true, "LlmRuntime host can be created and served through SocketJack HttpServer.", "Start LlmRuntimeHost from the host app."),
                Item("models-folder", Directory.Exists(_options.ModelRoot), "Model folder exists: " + _options.ModelRoot, "Open the Model Browser and download a GGUF to cwd\\Models."),
                Item("local-model", hasModel, hasModel ? "Local model files detected: " + models.Count : "No local model files detected.", "Download a fitting GGUF model from Hugging Face."),
                Item("ide-integration", dotnetAvailable, dotnetAvailable ? ".NET SDK is available for JackLLM/IDE integration builds." : ".NET SDK was not found on PATH.", "Install a compatible .NET SDK."),
                Item("git", gitAvailable, gitAvailable ? "git is available for local repo workflows." : "git was not found on PATH.", "Run tools\\Install-GitForWindows.ps1 and reopen the host."),
                Item("github-auth", ghAvailable, ghAvailable ? "GitHub CLI is available; authenticate with gh auth login if needed." : "GitHub CLI is not installed.", "Run tools\\Install-GitHubCli.ps1, then tools\\Initialize-GitHubCliAuth.ps1."),
                Item("directml-runner", directMl.Configured || _options.DefaultBackend != LlmBackendKind.DirectML, directMl.Configured ? directMl.Message : "DirectML runner is optional unless DirectML is selected.", "Run tools\\Install-DirectMlGgufRunner.ps1 with a trusted runner path or download URL."),
                Item("docs", true, "Quickstart and golden-path docs are included with LlmRuntime.", "Open LlmRuntime\\QUICKSTART.md.")
            ]
        };
    }

    public LlmDiagnosticsReport BuildDiagnosticsReport(int port, bool isListening)
    {
        IReadOnlyList<LlmModelInfo> models = _models.ListModels();
        DriveInfo? modelDrive = TryGetDrive(_options.ModelRoot);
        MemoryInfo memory = MemoryInfo.Create();
        bool portAvailable = IsPortAvailable(port);

        var checks = new List<LlmDiagnosticCheck>
        {
            Check("runtime-listening", isListening ? "ok" : "warning", isListening ? "Runtime HTTP server is listening." : "Runtime HTTP server is not listening."),
            Check("port", isListening || portAvailable ? "ok" : "error", isListening ? "Configured port is owned by this runtime." : portAvailable ? "Configured port appears available." : "Configured port appears to be in use."),
            Check("model-root", Directory.Exists(_options.ModelRoot) ? "ok" : "warning", Directory.Exists(_options.ModelRoot) ? "Model root exists." : "Model root is missing."),
            Check("models", models.Count > 0 ? "ok" : "warning", models.Count > 0 ? models.Count + " model(s) detected." : "No local model files were found."),
            Check("tools", _tools.ListDefinitions().Count > 0 ? "ok" : "warning", _tools.ListDefinitions().Count + " tool definition(s) detected."),
            Check("dotnet", CommandExists("dotnet") ? "ok" : "warning", CommandExists("dotnet") ? ".NET SDK/host is available." : "dotnet was not found on PATH."),
            Check("git", CommandExists("git") ? "ok" : "warning", CommandExists("git") ? "git is available." : "git was not found on PATH."),
            Check("github-cli", CommandExists("gh") ? "ok" : "info", CommandExists("gh") ? "GitHub CLI is available." : "GitHub CLI is optional and not installed."),
            Check("directml-runner", DirectMlGgufRunnerDiscovery.GetStatus(_options).Configured || _options.DefaultBackend != LlmBackendKind.DirectML ? "ok" : "warning", DirectMlGgufRunnerDiscovery.GetStatus(_options).Message),
            Check("drive-space", modelDrive == null || modelDrive.AvailableFreeSpace > 10L * 1024 * 1024 * 1024 ? "ok" : "warning", modelDrive == null ? "Drive space unavailable." : "Model drive free space: " + FormatBytes(modelDrive.AvailableFreeSpace) + "."),
            Check("memory", memory.AvailableBytes == 0 || memory.AvailableBytes > 4L * 1024 * 1024 * 1024 ? "ok" : "warning", memory.AvailableBytes == 0 ? "Memory availability unavailable." : "Available memory: " + FormatBytes(memory.AvailableBytes) + ".")
        };

        return new LlmDiagnosticsReport
        {
            Status = checks.Any(check => check.Severity == "error") ? "error" : checks.Any(check => check.Severity == "warning") ? "warning" : "ok",
            Checks = checks,
            Runtime = new Dictionary<string, object?>
            {
                ["serverName"] = _options.ServerName,
                ["port"] = port,
                ["isListening"] = isListening,
                ["modelRoot"] = _options.ModelRoot,
                ["toolRoot"] = _options.ToolRoot,
                ["agentRoot"] = _options.AgentRoot,
                ["defaultBackend"] = _options.DefaultBackend.ToString(),
                ["defaultContextLength"] = _options.DefaultContextLength
            }
        };
    }

    public LlmLocalAnalyticsDashboard BuildLocalAnalyticsDashboard()
    {
        IReadOnlyList<LlmModelInfo> models = _models.ListModels();
        IReadOnlyList<LlmToolDefinition> tools = _tools.ListDefinitions();
        IReadOnlyList<LlmAgentSession> sessions = _agents.ListSessions();
        IReadOnlyList<LlmToolAuditEntry> audit = _tools.ListAuditEntries(limit: 200);

        return new LlmLocalAnalyticsDashboard
        {
            Telemetry = "local-only",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = _startedAtUtc,
            UptimeSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds),
            ModelCount = models.Count,
            LoadedModelCount = models.Count(model => model.IsLoaded),
            ToolCount = tools.Count,
            AgentSessionCount = sessions.Count,
            AuditEntryCount = audit.Count,
            ModelsByType = models.GroupBy(model => model.Type).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            ToolsByVisibility = tools.GroupBy(tool => tool.Visibility.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            AgentSessionsByStatus = sessions.GroupBy(session => session.Status.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    public LlmAccessibilityReport BuildAccessibilityReport()
    {
        return new LlmAccessibilityReport
        {
            Status = "implemented",
            Items =
            [
                Item("keyboard-navigation", true, "WPF controls expose standard keyboard focus and tab navigation.", "Review any custom control template that suppresses focus visuals."),
                Item("automation-names", true, "Production-facing LlmRuntime WPF controls now set explicit AutomationProperties on primary controls.", "Continue adding names for new controls."),
                Item("status-text", true, "Downloader and tool-definition surfaces expose visible status text for screen readers.", "Keep async progress mirrored in text."),
                Item("contrast", true, "Dark surfaces use high-contrast text and bordered controls.", "Run manual contrast review when adding new colors."),
                Item("visual-studio-surface", false, "Dedicated Visual Studio accessibility depends on a future VSIX integration surface.", "Apply the same keyboard, automation-name, and screen-reader rules in the VS extension.")
            ]
        };
    }

    public LlmInstallerReadinessReport BuildInstallerReadinessReport()
    {
        string installerProject = Path.Combine(Environment.CurrentDirectory, "JackLLMInstaller", "JackLLMInstaller.wixproj");
        string updaterProject = Path.Combine(Environment.CurrentDirectory, "JackLLMUpdater", "JackLLMUpdater.csproj");
        string publishScript = Path.Combine(Environment.CurrentDirectory, "tools", "Publish-JackLLMUpdate.ps1");
        string signScript = Path.Combine(Environment.CurrentDirectory, "tools", "Sign-JackLLMInstaller.ps1");
        string signingWorkflow = Path.Combine(Environment.CurrentDirectory, ".github", "workflows", "lmvsproxy-signing.yml");
        string signingSecretScript = Path.Combine(Environment.CurrentDirectory, "tools", "Set-GitHubSigningSecrets.ps1");

        return new LlmInstallerReadinessReport
        {
            Status = File.Exists(installerProject) && File.Exists(updaterProject) && File.Exists(publishScript) && File.Exists(signingWorkflow) && File.Exists(signingSecretScript) ? "ready-for-github-cert-secret" : "incomplete",
            InstallerProject = installerProject,
            UpdaterProject = updaterProject,
            PublishScript = publishScript,
            SigningScript = signScript,
            SigningCertificateRequired = true,
            Items =
            [
                Item("installer-project", File.Exists(installerProject), "WiX installer project is present.", "Create JackLLMInstaller.wixproj."),
                Item("updater-project", File.Exists(updaterProject), "Updater project is present.", "Create JackLLMUpdater."),
                Item("publish-script", File.Exists(publishScript), "Update publish script is present.", "Create tools\\Publish-JackLLMUpdate.ps1."),
                Item("signing-script", File.Exists(signScript), "Signing script is present.", "Create tools\\Sign-JackLLMInstaller.ps1."),
                Item("github-signing-workflow", File.Exists(signingWorkflow), "GitHub Actions signing workflow is present.", "Create .github\\workflows\\lmvsproxy-signing.yml."),
                Item("github-signing-secrets-script", File.Exists(signingSecretScript), "GitHub signing secret setup script is present.", "Create tools\\Set-GitHubSigningSecrets.ps1."),
                Item("certificate", false, "A real code-signing certificate is required outside source control.", "Store CODE_SIGNING_PFX_BASE64 and CODE_SIGNING_PFX_PASSWORD as GitHub repository secrets.")
            ]
        };
    }

    public LlmRegressionSuitePlan BuildRegressionSuitePlan()
    {
        return new LlmRegressionSuitePlan
        {
            Commands =
            [
                "dotnet test .\\LlmRuntime.Tests\\LlmRuntime.Tests.csproj --no-restore",
                "dotnet build .\\LlmRuntime.Wpf\\LlmRuntime.Wpf.csproj --no-restore",
                "dotnet build .\\JackLLM\\JackLLM.csproj --no-restore",
                "dotnet build .\\SocketJack.sln --no-restore"
            ],
            Coverage =
            [
                "Runtime endpoints",
                "Model registry and metadata parsing",
                "Downloader helpers and cleanup",
                "Tool registry, safety, invocation, and audit",
                "Agent sessions and GitHub workflow fallbacks",
                "JackLLM provider routing"
            ]
        };
    }

    public LlmGoldenPathDemo BuildGoldenPathDemo()
    {
        return new LlmGoldenPathDemo
        {
            Title = "Download code model, load it, run an agent edit, test, and prepare a PR",
            Steps =
            [
                "Open JackLLM and choose Embedded LlmRuntime as provider.",
                "Open the Model Browser tab and download a GGUF code/instruct model that fits memory and drive space.",
                "Keep Load after download checked so the model is loaded into LlmRuntime automatically.",
                "Call GET /v1/models and confirm the downloaded model appears.",
                "Create an agent session with POST /api/v1/agent/sessions for a small repo task.",
                "Use agent file preview/write and terminal check endpoints to apply and verify the edit.",
                "Use /api/v1/github/branch, /api/v1/github/commit, and /api/v1/github/pull-request when GitHub CLI is installed.",
                "Review /api/v1/production/analytics/local and /api/v1/production/diagnostics before sharing results."
            ],
            ExpectedOutcome = "A local model powers an approved repo edit, tests pass, and a branch/commit/PR path is ready without requiring LM Studio."
        };
    }

    private static LlmProductionChecklistItem Item(string id, bool complete, string message, string nextAction) => new()
    {
        Id = id,
        Complete = complete,
        Message = message,
        NextAction = complete ? "" : nextAction
    };

    private static LlmDiagnosticCheck Check(string id, string severity, string message) => new()
    {
        Id = id,
        Severity = severity,
        Message = message
    };

    private static int Percent(IEnumerable<bool> values)
    {
        bool[] array = values.ToArray();
        if (array.Length == 0)
            return 100;
        return (int)Math.Round(array.Count(value => value) / (double)array.Length * 100);
    }

    private static bool CommandExists(string command)
    {
        try { return LlmCommandResolver.Exists(command); }
        catch { return false; }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            return properties.GetActiveTcpListeners().All(endpoint => endpoint.Port != port);
        }
        catch
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static DriveInfo? TryGetDrive(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
    }

    private sealed class MemoryInfo
    {
        public long AvailableBytes { get; init; }

        public static MemoryInfo Create()
        {
            try
            {
                var info = GC.GetGCMemoryInfo();
                long available = info.TotalAvailableMemoryBytes > 0
                    ? Math.Max(0, info.TotalAvailableMemoryBytes - GC.GetTotalMemory(false))
                    : 0;
                return new MemoryInfo { AvailableBytes = available };
            }
            catch
            {
                return new MemoryInfo();
            }
        }
    }
}

public sealed class LlmProductionChecklistItem
{
    public string Id { get; init; } = "";
    public bool Complete { get; init; }
    public string Message { get; init; } = "";
    public string NextAction { get; init; } = "";
}

public sealed class LlmOnboardingChecklist
{
    public int PercentComplete { get; init; }
    public IReadOnlyList<LlmProductionChecklistItem> Items { get; init; } = [];
}

public sealed class LlmDiagnosticCheck
{
    public string Id { get; init; } = "";
    public string Severity { get; init; } = "info";
    public string Message { get; init; } = "";
}

public sealed class LlmDiagnosticsReport
{
    public string Status { get; init; } = "ok";
    public IReadOnlyList<LlmDiagnosticCheck> Checks { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Runtime { get; init; } = new Dictionary<string, object?>();
}

public sealed class LlmLocalAnalyticsDashboard
{
    public string Telemetry { get; init; } = "local-only";
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public double UptimeSeconds { get; init; }
    public int ModelCount { get; init; }
    public int LoadedModelCount { get; init; }
    public int ToolCount { get; init; }
    public int AgentSessionCount { get; init; }
    public int AuditEntryCount { get; init; }
    public IReadOnlyDictionary<string, int> ModelsByType { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ToolsByVisibility { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> AgentSessionsByStatus { get; init; } = new Dictionary<string, int>();
}

public sealed class LlmAccessibilityReport
{
    public string Status { get; init; } = "implemented";
    public IReadOnlyList<LlmProductionChecklistItem> Items { get; init; } = [];
}

public sealed class LlmInstallerReadinessReport
{
    public string Status { get; init; } = "incomplete";
    public string InstallerProject { get; init; } = "";
    public string UpdaterProject { get; init; } = "";
    public string PublishScript { get; init; } = "";
    public string SigningScript { get; init; } = "";
    public bool SigningCertificateRequired { get; init; }
    public IReadOnlyList<LlmProductionChecklistItem> Items { get; init; } = [];
}

public sealed class LlmRegressionSuitePlan
{
    public IReadOnlyList<string> Commands { get; init; } = [];
    public IReadOnlyList<string> Coverage { get; init; } = [];
}

public sealed class LlmGoldenPathDemo
{
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Steps { get; init; } = [];
    public string ExpectedOutcome { get; init; } = "";
}
