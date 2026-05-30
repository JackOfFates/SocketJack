using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using SocketJack.Net;

namespace LlmRuntime;

public sealed class LlmRuntimeHost : IDisposable
{
    private const string ModelsLocationEnvironmentVariable = "JACKLLM_MODELS_LOCATION";
    private const string ModelRootEnvironmentVariable = "JACKLLM_MODEL_ROOT";
    private const string CompleteModelRootEnvironmentVariable = "JACKLLM_COMPLETE_MODEL_ROOT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly LlmRuntimeOptions _options;
    private readonly ModelRepositoryScanner _repositoryScanner = new();
    private readonly System.Net.Http.HttpClient _huggingFaceHttpClient = new();
    private readonly ModelConversionService _conversionService;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _startupModelLoadCancellation;
    private Task? _startupModelLoadTask;
    private bool _disposed;

    public LlmRuntimeHost(LlmRuntimeOptions options, LlmModelRegistry? registry = null, LlmToolRegistry? toolRegistry = null, ILlmToolInvoker? toolInvoker = null, LlmRuntimeCompatibilityService? compatibility = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversionService = new ModelConversionService(
            string.IsNullOrWhiteSpace(_options.CompleteModelRoot) ? _options.ModelRoot : _options.CompleteModelRoot,
            _options.OnnxConversionTimeout);
        Registry = registry ?? new LlmModelRegistry(_options);
        Registry.ModelLoadProgressChanged += OnModelLoadProgressChanged;
        ToolRegistry = toolRegistry ?? new LlmToolRegistry(_options);
        ToolInvoker = toolInvoker ?? new LlmToolInvoker(ToolRegistry);
        ToolCallLoop = new LlmToolCallLoop(ToolRegistry, ToolInvoker);
        AgentRuntime = new LlmAgentRuntime(_options);
        GitHubWorkflows = new LlmGitHubWorkflowService(_options, AgentRuntime);
        AutonomousAgents = new LlmAutonomousAgentService(_options, Registry, AgentRuntime);
        IdeIntegration = new LlmIdeIntegrationService(_options, AgentRuntime, ToolRegistry);
        CodeIntelligence = new LlmCodeIntelligenceService(_options, Registry);
        ProductionReadiness = new LlmProductionReadinessService(_options, Registry, ToolRegistry, AgentRuntime, GitHubWorkflows, _startedAtUtc);
        Compatibility = compatibility ?? new LlmRuntimeCompatibilityService(_options);
        Server = new HttpServer(_options.Port, _options.ServerName);
        Server.RequestGate = ControlSurfaceAuthGate;
        ConfigureRoutes(Server);
    }

    public HttpServer Server { get; }

    public LlmModelRegistry Registry { get; }

    public LlmToolRegistry ToolRegistry { get; }

    public ILlmToolInvoker ToolInvoker { get; }

    public LlmToolCallLoop ToolCallLoop { get; }

    public LlmAgentRuntime AgentRuntime { get; }

    public LlmGitHubWorkflowService GitHubWorkflows { get; }

    public LlmAutonomousAgentService AutonomousAgents { get; }

    public LlmIdeIntegrationService IdeIntegration { get; }

    public LlmCodeIntelligenceService CodeIntelligence { get; }

    public LlmProductionReadinessService ProductionReadiness { get; }

    public LlmRuntimeCompatibilityService Compatibility { get; }

    public int Port => Server.Port;

    public bool IsListening => Server.IsListening;

    public event Action<string>? ServiceLog;

    public bool Start()
    {
        bool listening = Server.Listen();
        if (listening)
            StartStartupModelRestore();
        return listening;
    }

    public void Stop()
    {
        CancelStartupModelRestore();
        Server.StopListening();
    }

    private void StartStartupModelRestore()
    {
        if (!_options.RestoreLoadedModelsOnStartup || string.IsNullOrWhiteSpace(_options.RuntimeConfigPath))
            return;
        if (_startupModelLoadTask != null && !_startupModelLoadTask.IsCompleted)
            return;

        _startupModelLoadCancellation?.Dispose();
        _startupModelLoadCancellation = new CancellationTokenSource();
        CancellationToken token = _startupModelLoadCancellation.Token;
        _startupModelLoadTask = Task.Run(async () =>
        {
            try
            {
                await Registry.RestorePersistedLoadedModelsAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ServiceLog?.Invoke("Saved model restore cancelled.");
            }
            catch (Exception ex)
            {
                ServiceLog?.Invoke("Saved model restore failed: " + ex.Message);
            }
        }, token);
    }

    private void CancelStartupModelRestore()
    {
        try
        {
            _startupModelLoadCancellation?.Cancel();
        }
        catch
        {
        }
    }

    private void OnModelLoadProgressChanged(LlmModelLoadProgress progress)
    {
        string message = string.IsNullOrWhiteSpace(progress.Message)
            ? progress.Status
            : progress.Message;
        ServiceLog?.Invoke("Models loading " + progress.Percent.ToString(CultureInfo.InvariantCulture) + "% - " + message);
    }

    private string ControlSurfaceAuthGate(NetworkConnection connection, HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.ControlAuthToken))
            return "";
        if (request?.Path?.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) != true)
            return "";
        if (IsControlRequestAuthenticated(request))
            return "";

        if (request?.Context != null)
            request.Context.StatusCodeNumber = 403;
        return "LlmRuntime control API token is required.";
    }

    private bool IsControlRequestAuthenticated(HttpRequest request)
    {
        string token = "";
        if (request?.Headers != null)
        {
            if (request.Headers.TryGetValue("X-LlmRuntime-Control-Token", out string? headerToken))
                token = headerToken ?? "";
            else if (request.Headers.TryGetValue("Authorization", out string? auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = auth.Substring("Bearer ".Length).Trim();
        }

        if (string.IsNullOrWhiteSpace(token) && request?.QueryParameters != null)
            token = request.QueryParameters.TryGetValue("token", out string? queryToken) ? queryToken ?? "" : "";

        return FixedTimeEquals(token, _options.ControlAuthToken);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected))
            return false;

        byte[] left = Encoding.UTF8.GetBytes(supplied);
        byte[] right = Encoding.UTF8.GetBytes(expected);
        try
        {
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private void ConfigureRoutes(HttpServer server)
    {
        server.Map("GET", "/v1/models", (_, _, _) => ToJson(new
        {
            @object = "list",
            data = Registry.ListModels()
                .Where(LlmModelRegistry.IsChatLoadableModel)
                .Select(ToOpenAiModel)
                .ToArray()
        }));

        server.Map("GET", "/api/v1/models", (_, _, _) => ToJson(new
        {
            models = Registry.ListModels().Select(ToNativeModel).ToArray(),
            modelsDirectory = _options.ModelRoot,
            completeModelsDirectory = _options.CompleteModelRoot,
            modelsRoot = _options.ModelRoot,
            completeModelsRoot = _options.CompleteModelRoot
        }));
        server.Map("GET", "/api/v1/scheduler/status", (_, _, _) => ToJson(new
        {
            status = "ok",
            scheduler = "local-instance-pool",
            instances = Registry.GetSchedulerStatus().Select(ToSchedulerInstanceStatus).ToArray()
        }));

        server.Map("POST", "/api/v1/models/load", (_, request, cancellationToken) => HandleLoad(request, cancellationToken));
        server.Map("POST", "/api/v1/models/unload", (_, request, _) => HandleUnload(request));
        server.Map("POST", "/api/v1/models/download", (_, request, _) => HandleDownload(request));
        server.Map("GET", "/api/v1/models/download/status", (_, request, _) => HandleDownloadStatus(request));
        server.Map("POST", "/api/v1/models/download/cancel", (_, request, _) => HandleDownloadCancel(request));
        server.Map("POST", "/api/v1/models/config", (_, request, _) => HandleModelConfig(request));
        server.Map("POST", "/api/v1/models/delete", (_, request, _) => HandleModelDelete(request));
        server.Map("GET", "/api/v1/models/huggingface/search", (_, request, cancellationToken) => HandleHuggingFaceSearch(request, cancellationToken));
        server.Map("GET", "/api/v1/models/repository/scan", (_, request, cancellationToken) => HandleRepositoryScan(request, cancellationToken));
        server.Map("POST", "/api/v1/models/repository/scan", (_, request, cancellationToken) => HandleRepositoryScan(request, cancellationToken));
        server.Map("POST", "/api/v1/models/convert", (_, request, _) => HandleConversionStart(request));
        server.Map("GET", "/api/v1/models/convert/status", (_, request, _) => HandleConversionStatus(request));
        server.Map("POST", "/api/v1/models/convert/cancel", (_, request, _) => HandleConversionCancel(request));

        server.Map("GET", "/api/v1/runtime/compatibility", (_, request, cancellationToken) => HandleCompatibilityStatus(request, cancellationToken));
        server.Map("POST", "/api/v1/runtime/compatibility/config", (_, request, _) => HandleCompatibilityConfig(request));
        server.Map("POST", "/api/v1/runtime/compatibility/reset", (_, request, _) => HandleCompatibilityReset(request));
        server.Map("POST", "/api/v1/runtime/compatibility/repair-pytorch", (_, request, cancellationToken) => HandleCompatibilityRepairPytorch(request, cancellationToken));
        server.Map("POST", LlmRuntimeCompatibilityService.LinuxCudaPytorchInstallEndpoint, (_, request, cancellationToken) => HandleCompatibilityInstallLinuxCudaPytorch(request, cancellationToken));
        server.Map("POST", "/api/v1/runtime/compatibility/install-linux", (_, request, cancellationToken) => HandleCompatibilityInstallLinuxCudaPytorch(request, cancellationToken));

        server.Map("GET", "/api/v1/tools", (_, request, _) => HandleToolList(request));
        server.Map("GET", "/api/v1/tools/openai", (_, _, _) => ToJson(new { tools = ToolRegistry.ExportOpenAiTools() }));
        server.Map("GET", "/api/v1/tools/mcp", (_, _, _) => ToJson(new { tools = LlmMcpToolAdapter.ExportMcpTools(ToolRegistry) }));
        server.Map("POST", "/api/v1/tools", (_, request, _) => HandleToolUpsert(request));
        server.Map("DELETE", "/api/v1/tools", (_, request, _) => HandleToolDelete(request));
        server.Map("POST", "/api/v1/tools/delete", (_, request, _) => HandleToolDelete(request));
        server.Map("POST", "/api/v1/tools/secrets", (_, request, _) => HandleToolSecret(request));
        server.Map("POST", "/api/v1/tools/invoke", (_, request, cancellationToken) => HandleToolInvoke(request, cancellationToken));
        server.Map("POST", "/api/v1/tools/calls", (_, request, cancellationToken) => HandleToolCalls(request, cancellationToken));
        server.Map("GET", "/api/v1/tools/audit", (_, request, _) => HandleToolAudit(request));

        server.Map("GET", "/api/v1/agent/sessions", (_, _, _) => ToJson(new { sessions = AgentRuntime.ListSessions() }));
        server.Map("POST", "/api/v1/agent/sessions", (_, request, _) => HandleAgentCreateSession(request));
        server.Map("GET", "/api/v1/agent/session", (_, request, _) => HandleAgentGetSession(request));
        server.Map("POST", "/api/v1/agent/plan", (_, request, _) => HandleAgentPlan(request));
        server.Map("POST", "/api/v1/agent/phase", (_, request, _) => HandleAgentPhase(request));
        server.Map("POST", "/api/v1/agent/files/read", (_, request, _) => HandleAgentReadFile(request));
        server.Map("POST", "/api/v1/agent/files/preview", (_, request, _) => HandleAgentPreviewFile(request));
        server.Map("POST", "/api/v1/agent/files/write", (_, request, _) => HandleAgentWriteFile(request));
        server.Map("POST", "/api/v1/agent/terminal/run", (_, request, cancellationToken) => HandleAgentRunCommand(request, cancellationToken));
        server.Map("POST", "/api/v1/agent/checks/run", (_, request, cancellationToken) => HandleAgentRunChecks(request, cancellationToken));
        server.Map("POST", "/api/v1/agent/jobs/command", (_, request, _) => HandleAgentStartCommandJob(request));
        server.Map("GET", "/api/v1/agent/jobs", (_, request, _) => HandleAgentListJobs(request));
        server.Map("POST", "/api/v1/agent/subagents", (_, request, _) => HandleAgentSubAgents(request));
        server.Map("POST", "/api/v1/agent/self-correction/plan", (_, request, _) => HandleAgentSelfCorrectionPlan(request));
        server.Map("GET", "/api/v1/agent/progress", (_, request, _) => HandleAgentProgress(request));
        server.Map("GET", "/api/v1/agent/repo/context", (_, request, _) => HandleAgentRepoContext(request));
        server.Map("GET", "/api/v1/agent/automation/hooks", (_, _, _) => ToJson(new { hooks = AgentRuntime.ListAutomationHooks() }));
        server.Map("POST", "/api/v1/agent/automation/hooks", (_, request, _) => HandleAgentAutomationHook(request));
        server.Map("POST", "/api/v1/agent/autonomous/run", (_, request, cancellationToken) => HandleAgentAutonomousRun(request, cancellationToken));

        server.Map("GET", "/api/v1/github/repository", (_, request, _) => HandleGitHubRepository(request));
        server.Map("GET", "/api/v1/github/item", (_, request, _) => HandleGitHubItem(request));
        server.Map("POST", "/api/v1/github/task", (_, request, _) => HandleGitHubTask(request));
        server.Map("POST", "/api/v1/github/branch", (_, request, _) => HandleGitHubBranch(request));
        server.Map("POST", "/api/v1/github/commit", (_, request, _) => HandleGitHubCommit(request));
        server.Map("POST", "/api/v1/github/pull-request", (_, request, _) => HandleGitHubPullRequest(request));
        server.Map("POST", "/api/v1/github/pull-request/job", (_, request, _) => HandleGitHubPullRequestJob(request));
        server.Map("GET", "/api/v1/github/jobs", (_, request, _) => HandleGitHubJobs(request));
        server.Map("GET", "/api/v1/github/commit/summary", (_, request, _) => HandleGitHubCommitSummary(request));
        server.Map("GET", "/api/v1/github/pull-request/summary", (_, request, _) => HandleGitHubPullRequestSummary(request));
        server.Map("POST", "/api/v1/github/review", (_, request, _) => HandleGitHubReview(request));
        server.Map("POST", "/api/v1/github/review/address", (_, request, _) => HandleGitHubAddressReview(request));
        server.Map("GET", "/api/v1/github/actions/diagnose", (_, request, _) => HandleGitHubActions(request));
        server.Map("POST", "/api/v1/github/security/review", (_, request, _) => HandleGitHubSecurityReview(request));
        server.Map("POST", "/api/v1/github/dependencies/plan", (_, request, _) => HandleGitHubDependencyPlan(request));
        server.Map("GET", "/api/v1/github/policy", (_, _, _) => ToJson(new { policy = GitHubWorkflows.GetPolicy() }));
        server.Map("POST", "/api/v1/github/policy", (_, request, _) => HandleGitHubPolicy(request));
        server.Map("GET", "/api/v1/github/audit", (_, request, _) => HandleGitHubAudit(request));

        server.Map("GET", "/api/v1/ide/capabilities", (_, _, _) => ToJson(IdeIntegration.GetCapabilities()));
        server.Map("POST", "/api/v1/ide/completions/inline", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.CompleteInline(context)));
        server.Map("POST", "/api/v1/ide/next-edit", (_, request, _) => HandleIdeContext(request, context => new { suggestions = IdeIntegration.SuggestNextEdits(context) }));
        server.Map("POST", "/api/v1/ide/ask", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.Ask(context)));
        server.Map("POST", "/api/v1/ide/edit/preview", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.PreviewEdit(context)));
        server.Map("POST", "/api/v1/ide/agent/start", (_, request, _) => HandleIdeContext(request, context => new { session = IdeIntegration.StartAgent(context) }));
        server.Map("POST", "/api/v1/ide/plan", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.CreatePlan(string.IsNullOrWhiteSpace(context.Prompt) ? "IDE plan" : context.Prompt, false)));
        server.Map("POST", "/api/v1/ide/references", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.BuildReferences(context)));
        server.Map("POST", "/api/v1/ide/checkpoints", (_, request, _) => HandleIdeCheckpoint(request));
        server.Map("POST", "/api/v1/ide/rollback", (_, request, _) => HandleIdeRollback(request));
        server.Map("POST", "/api/v1/ide/index", (_, request, _) => HandleIdeIndex(request));
        server.Map("POST", "/api/v1/ide/search", (_, request, _) => HandleIdeSearch(request));
        server.Map("POST", "/api/v1/ide/instructions", (_, request, _) => HandleIdeWorkspace(request, workspace => new { instructions = IdeIntegration.ReadCustomInstructions(workspace) }));
        server.Map("POST", "/api/v1/ide/prompts", (_, request, _) => HandleIdeWorkspace(request, workspace => new { prompts = IdeIntegration.ReadPromptFiles(workspace) }));
        server.Map("GET", "/api/v1/ide/mcp/context", (_, _, _) => ToJson(IdeIntegration.BuildMcpContext()));
        server.Map("GET", "/api/v1/ide/model-routing", (_, _, _) => ToJson(IdeIntegration.BuildModelRoutingPlan()));
        server.Map("POST", "/api/v1/ide/vision/context", (_, request, _) => HandleIdeContext(request, context => IdeIntegration.BuildVisionContext(context)));
        server.Map("POST", "/api/v1/ide/actions", (_, request, _) => HandleIdeContext(request, context => new { actions = IdeIntegration.BuildContextActions(context) }));
        server.Map("POST", "/api/v1/ide/dotnet/upgrade-plan", (_, request, _) => HandleIdeWorkspace(request, workspace => IdeIntegration.BuildDotNetUpgradePlan(workspace)));

        server.Map("POST", "/api/v1/code-intelligence/symbol-graph", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.BuildSymbolGraph(workspace)));
        server.Map("POST", "/api/v1/code-intelligence/graph", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.BuildCodeGraph(workspace)));
        server.Map("POST", "/api/v1/code-intelligence/refactor-plan", (_, request, _) => HandleCodeRequest(request, (root, workspace) => CodeIntelligence.CreateRefactorPlan(workspace, GetString(root, "goal") ?? "")));
        server.Map("POST", "/api/v1/code-intelligence/migration-plan", (_, request, _) => HandleCodeRequest(request, (root, workspace) => CodeIntelligence.CreateMigrationPlan(workspace, GetString(root, "target") ?? GetString(root, "target_framework") ?? "")));
        server.Map("POST", "/api/v1/code-intelligence/tests/coverage", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.ExploreTests(workspace)));
        server.Map("GET", "/api/v1/code-intelligence/profiling", (_, _, _) => ToJson(CodeIntelligence.BuildProfilingPlan()));
        server.Map("POST", "/api/v1/code-intelligence/architecture-review", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.ReviewArchitecture(workspace)));
        server.Map("POST", "/api/v1/code-intelligence/documentation-sync", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.CreateDocumentationSyncPlan(workspace)));
        server.Map("POST", "/api/v1/code-intelligence/model-evaluation", (_, request, _) => HandleCodeWorkspace(request, workspace => CodeIntelligence.CreateModelEvaluationHarness(workspace)));
        server.Map("POST", "/api/v1/code-intelligence/context-budget", (_, request, _) => HandleCodeRequest(request, (root, workspace) => CodeIntelligence.OptimizeContext(workspace, GetInt32(root, "max_tokens") ?? GetInt32(root, "maxTokens") ?? (int)_options.DefaultContextLength)));
        server.Map("GET", "/api/v1/code-intelligence/privacy", (_, _, _) => ToJson(new
        {
            localPrivacyMode = _options.LocalPrivacyMode,
            codeLeavesMachine = !_options.LocalPrivacyMode,
            defaultWorkspaceRoot = _options.DefaultWorkspaceRoot
        }));

        server.Map("GET", "/api/v1/production/onboarding", (_, _, _) => ToJson(ProductionReadiness.BuildOnboardingChecklist()));
        server.Map("GET", "/api/v1/production/diagnostics", (_, _, _) => ToJson(ProductionReadiness.BuildDiagnosticsReport(Port, IsListening)));
        server.Map("GET", "/api/v1/production/analytics/local", (_, _, _) => ToJson(ProductionReadiness.BuildLocalAnalyticsDashboard()));
        server.Map("GET", "/api/v1/production/accessibility", (_, _, _) => ToJson(ProductionReadiness.BuildAccessibilityReport()));
        server.Map("GET", "/api/v1/production/installer", (_, _, _) => ToJson(ProductionReadiness.BuildInstallerReadinessReport()));
        server.Map("GET", "/api/v1/production/regression-suite", (_, _, _) => ToJson(ProductionReadiness.BuildRegressionSuitePlan()));
        server.Map("GET", "/api/v1/production/golden-path", (_, _, _) => ToJson(ProductionReadiness.BuildGoldenPathDemo()));

        server.MapStream("POST", "/v1/chat/completions", (_, request, stream, cancellationToken) => HandleChatCompletion(request, stream, cancellationToken));
        server.Map("POST", "/v1/responses", (_, request, _) => InferenceScaffold(request, "response"));
        server.Map("POST", "/v1/completions", (_, request, _) => InferenceScaffold(request, "text_completion"));
        server.Map("POST", "/v1/embeddings", (_, request, _) => InferenceScaffold(request, "embedding"));
        server.MapStream("POST", "/api/v1/chat", (_, request, stream, cancellationToken) => HandleChatCompletion(request, stream, cancellationToken));
    }

    private string HandleCompatibilityStatus(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            string python = request.QueryParameters != null && request.QueryParameters.TryGetValue("python", out string? value)
                ? value ?? ""
                : "";
            return ToJson(Compatibility.GetStatus(python, cancellationToken));
        }
        catch (Exception ex)
        {
            return ToJson(new LlmRuntimeCompatibilityStatus
            {
                Status = "probe_failed",
                Message = "LlmRuntime compatibility probe failed: " + ex.Message,
                GenerationDisabled = true,
                RequiresPytorchRepair = true,
                IsGpuGenerationEnabled = false,
                ConfigPath = Compatibility.ConfigPath,
                LinuxCudaPytorchInstallScriptPath = LlmRuntimeCompatibilityService.ResolveLinuxCudaPytorchInstallScriptPath(),
                LinuxCudaPytorchInstallCommand = LlmRuntimeCompatibilityService.BuildLinuxCudaPytorchInstallCommand(),
                Config = Compatibility.LoadConfig(),
                Diagnostics = new LlmRuntimeCompatibilityDiagnostics
                {
                    Python = new LlmPythonRuntimeStatus
                    {
                        IsAvailable = false,
                        Error = ex.Message
                    }
                },
                Actions =
                [
                    new LlmRuntimeCompatibilityAction
                    {
                        Id = "install_linux_cuda_pytorch",
                        Label = "Install Linux CUDA + PyTorch",
                        Kind = "post",
                        Endpoint = LlmRuntimeCompatibilityService.LinuxCudaPytorchInstallEndpoint,
                        Command = LlmRuntimeCompatibilityService.BuildLinuxCudaPytorchInstallCommand(),
                        Enabled = true,
                        Detail = "Repairs the Linux Python/CUDA runtime after missing folders or deleted files."
                    }
                ]
            });
        }
    }

    private string HandleCompatibilityConfig(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            LlmRuntimeCompatibilityConfig? config = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("config", out JsonElement configElement)
                    ? configElement.Deserialize<LlmRuntimeCompatibilityConfig>(JsonOptions)
                    : root.Deserialize<LlmRuntimeCompatibilityConfig>(JsonOptions);
            return ToJson(new
            {
                status = "saved",
                config = Compatibility.SaveConfig(config ?? new LlmRuntimeCompatibilityConfig()),
                compatibility = Compatibility.GetStatus()
            });
        }
        catch (Exception ex)
        {
            SetStatus(request, (400, "Bad Request"));
            return Error(ex.Message, "invalid_request_error", "compatibility_config_failed");
        }
    }

    private string HandleCompatibilityReset(HttpRequest request)
    {
        try
        {
            Compatibility.ResetConfig();
            return ToJson(new
            {
                status = "reset",
                compatibility = Compatibility.GetStatus()
            });
        }
        catch (Exception ex)
        {
            SetStatus(request, (500, "Internal Server Error"));
            return Error(ex.Message, "compatibility_error", "compatibility_reset_failed");
        }
    }

    private string HandleCompatibilityRepairPytorch(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            string python = "";
            if (!string.IsNullOrWhiteSpace(request.Body))
            {
                using var document = ParseBody(request);
                python = GetString(document.RootElement, "python_executable") ??
                         GetString(document.RootElement, "pythonExecutable") ??
                         GetString(document.RootElement, "python") ??
                         "";
            }

            LlmPytorchRepairResult result = Compatibility.RepairPytorchAsync(python, cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            SetStatus(request, (500, "Internal Server Error"));
            return Error(ex.Message, "compatibility_error", "pytorch_repair_failed");
        }
    }

    private string HandleCompatibilityInstallLinuxCudaPytorch(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            string python = "";
            string torchIndexUrl = "";
            string torchVersion = "";
            if (!string.IsNullOrWhiteSpace(request.Body))
            {
                using var document = ParseBody(request);
                python = GetString(document.RootElement, "python_executable") ??
                         GetString(document.RootElement, "pythonExecutable") ??
                         GetString(document.RootElement, "python") ??
                         "";
                torchIndexUrl = GetString(document.RootElement, "torch_index_url") ??
                                GetString(document.RootElement, "torchIndexUrl") ??
                                "";
                torchVersion = GetString(document.RootElement, "torch_version") ??
                               GetString(document.RootElement, "torchVersion") ??
                               "";
            }

            LlmLinuxCudaPytorchInstallResult result = Compatibility.InstallLinuxCudaPytorchAsync(
                python,
                torchIndexUrl,
                torchVersion,
                cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            SetStatus(request, (500, "Internal Server Error"));
            return Error(ex.Message, "compatibility_error", "linux_cuda_pytorch_install_failed");
        }
    }

    private string HandleLoad(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string model = GetString(root, "model") ?? "";
            uint? contextLength = GetUInt32(root, "context_length");
            int? evalBatchSize = GetInt32(root, "eval_batch_size");
            int? gpuLayerCount = GetInt32(root, "gpu_layer_count");
            bool? flashAttention = GetBool(root, "flash_attention");
            bool? offloadKvCacheToGpu = GetBool(root, "offload_kv_cache_to_gpu");
            LlmBackendKind? backend = GetBackend(root, "backend");
            bool? allowBackendFallback = GetBool(root, "allow_backend_fallback");
            bool echoLoadConfig = GetBool(root, "echo_load_config") ?? false;
            string? deviceId = GetString(root, "device_id") ?? GetString(root, "deviceId");
            string? instanceId = GetString(root, "instance_id") ?? GetString(root, "instanceId");
            int? concurrencyLimit = GetInt32(root, "concurrency_limit") ?? GetInt32(root, "concurrencyLimit");
            LlmParallelismMode? parallelismMode = GetParallelismMode(root);
            LlmParallelismPlacement? parallelismPlacement = GetParallelismPlacement(root);
            IReadOnlyList<string> targetDeviceIds = GetDeviceIds(root, deviceId);
            IReadOnlyList<string> networkNodeIds = GetStringArray(root, "network_node_ids")
                .Concat(GetStringArray(root, "networkNodeIds"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int? maxGpuLoadPercent = GetInt32(root, "gpu_load_threshold_percent") ?? GetInt32(root, "gpuLoadThresholdPercent") ?? GetInt32(root, "max_gpu_load_percent") ?? GetInt32(root, "maxGpuLoadPercent");
            int? maxVramUsagePercent = GetInt32(root, "max_vram_usage_percent") ?? GetInt32(root, "maxVramUsagePercent");
            int? pipelineStageCount = GetInt32(root, "pipeline_stage_count") ?? GetInt32(root, "pipelineStageCount");
            int? dataParallelReplicaCount = GetInt32(root, "data_parallel_replicas") ?? GetInt32(root, "dataParallelReplicas") ?? GetInt32(root, "replica_count") ?? GetInt32(root, "replicaCount");
            string? videoDeviceMap = GetString(root, "video_device_map") ?? GetString(root, "videoDeviceMap") ?? GetString(root, "device_map") ?? GetString(root, "deviceMap");
            bool? videoAllowCpuOffload = GetBool(root, "video_allow_cpu_offload") ?? GetBool(root, "videoAllowCpuOffload") ?? GetBool(root, "allow_cpu_offload") ?? GetBool(root, "allowCpuOffload");
            string? videoOffloadFolder = GetString(root, "video_offload_folder") ?? GetString(root, "videoOffloadFolder") ?? GetString(root, "offload_folder") ?? GetString(root, "offloadFolder");
            bool? videoDisableCudaMemoryGuard = GetBool(root, "video_disable_cuda_memory_guard") ?? GetBool(root, "videoDisableCudaMemoryGuard") ?? GetBool(root, "disable_cuda_memory_guard") ?? GetBool(root, "disableCudaMemoryGuard");
            int? videoCudaMemoryReserveMb = GetInt32(root, "video_cuda_memory_reserve_mb") ?? GetInt32(root, "videoCudaMemoryReserveMb") ?? GetInt32(root, "cuda_memory_reserve_mb") ?? GetInt32(root, "cudaMemoryReserveMb");
            string? videoCpuMaxMemory = GetString(root, "video_cpu_max_memory") ?? GetString(root, "videoCpuMaxMemory") ?? GetString(root, "cpu_max_memory") ?? GetString(root, "cpuMaxMemory");
            bool? videoMemorySaving = GetBool(root, "video_memory_saving") ?? GetBool(root, "videoMemorySaving") ?? GetBool(root, "memory_saving") ?? GetBool(root, "memorySaving");

            if (string.IsNullOrWhiteSpace(model))
                return Error("The request body must include a model identifier.", "invalid_request_error", "missing_model");

            bool loadDataParallelInstances =
                parallelismMode == LlmParallelismMode.DataParallel &&
                targetDeviceIds.Count > 1 &&
                string.IsNullOrWhiteSpace(instanceId);

            LlmLoadResult LoadForDevice(string? targetDeviceId, string? requestedInstanceId) => Registry.Load(
                model,
                contextLength,
                gpuLayerCount,
                evalBatchSize,
                flashAttention,
                offloadKvCacheToGpu,
                backend,
                allowBackendFallback,
                echoLoadConfig,
                targetDeviceId,
                requestedInstanceId,
                concurrencyLimit,
                parallelismMode,
                parallelismPlacement,
                targetDeviceIds,
                networkNodeIds,
                maxGpuLoadPercent,
                maxVramUsagePercent,
                pipelineStageCount,
                dataParallelReplicaCount,
                videoDeviceMap,
                videoAllowCpuOffload,
                videoOffloadFolder,
                videoDisableCudaMemoryGuard,
                videoCudaMemoryReserveMb,
                videoCpuMaxMemory,
                videoMemorySaving,
                cancellationToken);

            var results = new List<LlmLoadResult>();
            var instanceErrors = new List<object>();
            Exception? firstLoadFailure = null;

            if (loadDataParallelInstances)
            {
                foreach (string targetDeviceId in targetDeviceIds)
                {
                    try
                    {
                        results.Add(LoadForDevice(targetDeviceId, null));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        firstLoadFailure ??= ex;
                        instanceErrors.Add(new
                        {
                            device_id = targetDeviceId,
                            message = ex.Message,
                            type = ex is LlmRuntimeException runtimeException ? runtimeException.Type : "model_load_error",
                            code = ex is LlmRuntimeException runtimeExceptionForCode ? runtimeExceptionForCode.Code : "load_failed"
                        });
                    }
                }
            }
            else
            {
                results.Add(LoadForDevice(
                    string.IsNullOrWhiteSpace(deviceId) ? targetDeviceIds.FirstOrDefault() : deviceId,
                    instanceId));
            }

            if (results.Count == 0)
            {
                if (firstLoadFailure != null)
                    ExceptionDispatchInfo.Capture(firstLoadFailure).Throw();

                results.Add(Registry.Load(
                    model,
                    contextLength,
                    gpuLayerCount,
                    evalBatchSize,
                    flashAttention,
                    offloadKvCacheToGpu,
                    backend,
                    allowBackendFallback,
                    echoLoadConfig,
                    string.IsNullOrWhiteSpace(deviceId) ? targetDeviceIds.FirstOrDefault() : deviceId,
                    instanceId,
                    concurrencyLimit,
                    parallelismMode,
                    parallelismPlacement,
                    targetDeviceIds,
                    networkNodeIds,
                    maxGpuLoadPercent,
                    maxVramUsagePercent,
                    pipelineStageCount,
                    dataParallelReplicaCount,
                    videoDeviceMap,
                    videoAllowCpuOffload,
                    videoOffloadFolder,
                    videoDisableCudaMemoryGuard,
                    videoCudaMemoryReserveMb,
                    videoCpuMaxMemory,
                    videoMemorySaving,
                    cancellationToken));
            }

            var result = results[0];
            bool partial = loadDataParallelInstances && instanceErrors.Count > 0;
            return ToJson(new
            {
                type = result.Type,
                service = result.ChatService,
                chat_service = result.ChatService,
                instance_id = result.InstanceId,
                load_time_seconds = result.LoadTimeSeconds,
                status = partial ? "partial_loaded" : result.Status,
                partial,
                warning = partial ? "One or more requested data-parallel model instances failed to load, but at least one instance is available for inference." : null,
                load_config = result.LoadConfig == null ? null : ToNativeLoadConfig(result.LoadConfig),
                parallelism = new
                {
                    mode = ToParallelismModeName(result.LoadConfig?.ParallelismMode ?? parallelismMode ?? LlmParallelismMode.Single),
                    placement = ToParallelismPlacementName(result.LoadConfig?.ParallelismPlacement ?? parallelismPlacement ?? LlmParallelismPlacement.Local),
                    target_device_ids = targetDeviceIds,
                    network_node_ids = networkNodeIds,
                    execution = loadDataParallelInstances ? "local-instance-pool" : "single-runtime-instance"
                },
                instances = results.Select(loadResult => new
                {
                    instance_id = loadResult.InstanceId,
                    status = loadResult.Status,
                    load_config = loadResult.LoadConfig == null ? null : ToNativeLoadConfig(loadResult.LoadConfig)
                }).ToArray(),
                instance_errors = instanceErrors.ToArray()
            });
        }
        catch (LlmRuntimeException ex)
        {
            SetStatus(request, HttpStatusForError(ex.Code));
            return Error(ex.Message, ex.Type, ex.Code);
        }
        catch (FileNotFoundException ex)
        {
            SetStatus(request, (404, "Not Found"));
            return Error(ex.Message, "model_load_error", "load_failed");
        }
        catch (Exception ex)
        {
            SetStatus(request, (500, "Internal Server Error"));
            return Error(ex.Message, "model_load_error", "load_failed");
        }
    }

    private string HandleUnload(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            string model = GetString(document.RootElement, "model") ?? "";
            if (string.IsNullOrWhiteSpace(model))
                return Error("The request body must include a model identifier.", "invalid_request_error", "missing_model");

            bool unloaded = Registry.Unload(model);
            if (!unloaded)
                return Error($"Model is not loaded: {model}", "not_found_error", "model_not_loaded");

            return ToJson(new { status = "unloaded", model });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "model_unload_error", "unload_failed");
        }
    }

    private string HandleDownload(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string model = GetString(root, "model") ?? "";
            string? quantization = GetString(root, "quantization");
            string repository = GetString(root, "repository") ?? GetString(root, "repo") ?? "";
            string revision = GetString(root, "revision") ?? "main";
            string sourcePath = GetString(root, "source_path") ?? GetString(root, "sourcePath") ?? GetString(root, "path") ?? "";
            string task = GetString(root, "task") ?? "";
            string targetRelativeDirectory = GetString(root, "target_relative_directory") ?? GetString(root, "targetRelativeDirectory") ?? "";
            IReadOnlyList<string> sourcePaths = GetStringArray(root, "source_paths").Concat(GetStringArray(root, "sourcePaths")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            IReadOnlyDictionary<string, string> metadata = GetStringMap(root, "metadata");
            string token = GetString(root, "huggingface_token") ?? GetString(root, "huggingFaceToken") ?? GetString(root, "token") ?? "";
            if (!string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(token))
                token = Registry.GetModelConfig(model).HuggingFaceToken;

            if (!string.IsNullOrWhiteSpace(repository) && sourcePaths.Count > 0)
            {
                var bundleRequest = new ModelBundleDownloadRequest
                {
                    Repository = repository,
                    Revision = revision,
                    TargetRelativeDirectory = targetRelativeDirectory,
                    Task = string.IsNullOrWhiteSpace(task) ? "image.text-to-image" : task,
                    TotalSizeBytes = GetInt64(root, "total_size_bytes") ?? GetInt64(root, "totalSizeBytes") ?? 0,
                    SourcePaths = sourcePaths,
                    Metadata = metadata
                };
                if (bundleRequest.SourcePaths.Count == 0)
                    return Error("Bundle downloads require sourcePaths from a repository scan.", "invalid_request_error", "missing_source_paths");
                return ToJson(ToDownloadJob(Registry.StartBundleDownload(bundleRequest, token)));
            }

            if (string.IsNullOrWhiteSpace(model))
                return Error("The request body must include a model URL.", "invalid_request_error", "missing_model");

            var job = Registry.StartDownload(model, quantization, token, repository, revision, sourcePath, task, targetRelativeDirectory, metadata);
            return ToJson(ToDownloadJob(job));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "download_error", "download_failed");
        }
    }

    private string HandleDownloadStatus(HttpRequest request)
    {
        string? jobId = request.QueryParameters.TryGetValue("job_id", out var value) ? value : null;
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            var job = Registry.GetDownloadJob(jobId);
            return job == null
                ? Error($"Download job not found: {jobId}", "not_found_error", "job_not_found")
                : ToJson(ToDownloadJob(job));
        }

        return ToJson(new { downloads = Registry.GetDownloadJobs().Select(ToDownloadJob).ToArray() });
    }

    private string HandleDownloadCancel(HttpRequest request)
    {
        try
        {
            using JsonDocument? document = ParseOptionalBody(request);
            JsonElement? root = document?.RootElement;
            string[] jobIds = root.HasValue
                ? GetStringArray(root.Value, "job_ids")
                    .Concat(GetStringArray(root.Value, "jobIds"))
                    .Append(GetString(root.Value, "job_id") ?? "")
                    .Append(GetString(root.Value, "jobId") ?? "")
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

            var cancelled = new List<ModelDownloadJob>();
            var missing = new List<string>();
            if (jobIds.Length == 0)
            {
                cancelled.AddRange(Registry.CancelActiveDownloads());
            }
            else
            {
                foreach (string jobId in jobIds)
                {
                    if (Registry.CancelDownload(jobId) && Registry.GetDownloadJob(jobId) is { } job)
                        cancelled.Add(job);
                    else
                        missing.Add(jobId);
                }
            }

            return ToJson(new
            {
                status = cancelled.Count > 0 ? "cancel_requested" : "no_active_downloads",
                cancelled = cancelled.Select(ToDownloadJob).ToArray(),
                missing = missing.ToArray()
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "download_cancel_error", "cancel_failed");
        }
    }

    private string HandleModelConfig(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string model = GetString(root, "model") ?? "";
            if (string.IsNullOrWhiteSpace(model))
                return Error("The request body must include a model identifier.", "invalid_request_error", "missing_model");

            var config = Registry.SaveModelConfig(model, new LlmModelConfig
            {
                HuggingFaceToken = GetString(root, "huggingface_token") ?? GetString(root, "huggingFaceToken") ?? "",
                ApiKey = GetString(root, "api_key") ?? GetString(root, "apiKey") ?? "",
                Notes = GetString(root, "notes") ?? ""
            });
            return ToJson(new
            {
                status = "saved",
                model,
                has_huggingface_token = !string.IsNullOrWhiteSpace(config.HuggingFaceToken),
                has_api_key = !string.IsNullOrWhiteSpace(config.ApiKey),
                notes = config.Notes
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "model_config_error", "config_failed");
        }
    }

    private string HandleModelDelete(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            string model = GetString(document.RootElement, "model") ?? "";
            if (string.IsNullOrWhiteSpace(model))
                return Error("The request body must include a model identifier.", "invalid_request_error", "missing_model");

            bool deleted = Registry.DeleteModel(model);
            if (!deleted)
                return Error($"Model was not found on disk: {model}", "not_found_error", "model_not_found");
            return ToJson(new { status = "deleted", model });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "model_delete_error", "delete_failed");
        }
    }

    private string HandleRepositoryScan(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument? body = ParseOptionalBody(request);
            JsonElement? bodyRoot = body?.RootElement;
            string repo = ReadRequestString(request, bodyRoot, "repo", "repository") ?? "";
            string owner = ReadRequestString(request, bodyRoot, "owner") ?? "";
            string name = ReadRequestString(request, bodyRoot, "name") ?? "";
            string revision = FirstNonEmpty(ReadRequestString(request, bodyRoot, "revision"), "main");
            string token = FirstNonEmpty(
                ReadRequestString(request, bodyRoot, "huggingface_token", "huggingFaceToken", "token"),
                !string.IsNullOrWhiteSpace(repo) ? Registry.GetModelConfig(repo).HuggingFaceToken : "");

            if (!string.IsNullOrWhiteSpace(repo))
            {
                string[] parts = repo.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    owner = parts[0];
                    name = parts[1];
                }
            }

            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
                return Error("Repository scan requires repo=owner/name or owner/name query parameters.", "invalid_request_error", "missing_repository");

            var scan = _repositoryScanner.ScanHuggingFaceAsync(owner, name, revision, bearerToken: token, cancellationToken: cancellationToken).GetAwaiter().GetResult();
            RepositoryStorageSnapshot storage = ApplyRepositoryCandidateFit(scan);
            return ToJson(new
            {
                repository = scan.Repository,
                revision = scan.Revision,
                reason = scan.Reason,
                files = scan.Files,
                candidates = scan.Candidates,
                modelsDirectory = storage.ModelsDirectory,
                completeModelsDirectory = storage.CompleteModelsDirectory,
                modelsDriveFreeBytes = storage.ModelsDriveFreeBytes,
                completeModelsDriveFreeBytes = storage.CompleteModelsDriveFreeBytes
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "repository_scan_error", "scan_failed");
        }
    }

    private string HandleHuggingFaceSearch(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            int limit = GetQueryInt32(request, "limit") ?? 18;
            limit = Math.Clamp(limit, 1, 50);
            string search = GetQueryString(request, "search", "query", "q");
            string pipelineTag = GetQueryString(request, "pipeline_tag", "pipelineTag", "pipeline");
            string filter = GetQueryString(request, "filter");
            string library = GetQueryString(request, "library");
            string sort = FirstNonEmpty(GetQueryString(request, "sort"), "downloads");
            string direction = FirstNonEmpty(GetQueryString(request, "direction"), "-1");
            string token = GetQueryString(request, "huggingface_token", "huggingFaceToken", "token");

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("sort", sort),
                new("direction", direction),
                new("limit", limit.ToString(CultureInfo.InvariantCulture))
            };
            AddQueryParameter(parameters, "search", search);
            AddQueryParameter(parameters, "pipeline_tag", pipelineTag);
            AddQueryParameter(parameters, "filter", filter);
            AddQueryParameter(parameters, "library", library);

            string url = "https://huggingface.co/api/models?" + string.Join("&", parameters.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            using var hfRequest = new HttpRequestMessage(HttpMethod.Get, url);
            hfRequest.Headers.TryAddWithoutValidation("User-Agent", "SocketJack-LlmRuntime/1.0");
            if (!string.IsNullOrWhiteSpace(token))
                hfRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            using HttpResponseMessage response = _huggingFaceHttpClient.SendAsync(hfRequest, cancellationToken).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                SetStatus(request, ((int)response.StatusCode, response.ReasonPhrase ?? "Hugging Face Error"));
                return Error("Hugging Face search returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + TruncateSearchError(body), "huggingface_error", "huggingface_search_failed");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ToJson(new { models = Array.Empty<object>() });

            var models = new List<object>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("private", out JsonElement privateElement) && privateElement.ValueKind == JsonValueKind.True)
                    continue;

                string id = FirstNonEmpty(ReadJsonString(item, "modelId"), ReadJsonString(item, "id"));
                if (string.IsNullOrWhiteSpace(id) || id.Contains(' '))
                    continue;

                models.Add(new
                {
                    modelId = id,
                    url = "https://huggingface.co/" + id.Trim('/'),
                    pipelineTag = ReadJsonString(item, "pipeline_tag"),
                    libraryName = ReadJsonString(item, "library_name"),
                    downloads = ReadJsonInt64(item, "downloads"),
                    likes = ReadJsonInt64(item, "likes"),
                    tags = ReadJsonStringArray(item, "tags"),
                    reason = BuildHuggingFaceSearchReason(item)
                });
            }

            return ToJson(new
            {
                models,
                query = new { search, pipelineTag, filter, library, sort, direction, limit }
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "huggingface_error", "huggingface_search_failed");
        }
    }

    private string HandleConversionStart(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var conversionRequest = new ModelConversionRequest
            {
                Repository = GetString(root, "repository") ?? GetString(root, "repo") ?? "",
                Revision = GetString(root, "revision") ?? "main",
                SourcePaths = GetStringArray(root, "source_paths").Concat(GetStringArray(root, "sourcePaths")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Task = GetString(root, "task") ?? "text-generation",
                Precision = GetString(root, "precision") ?? "fp32",
                Opset = GetInt32(root, "opset") ?? 17,
                PythonPath = GetString(root, "python_path") ?? GetString(root, "pythonPath") ?? "",
                ConverterPath = GetString(root, "converter_path") ?? GetString(root, "converterPath") ?? _options.OnnxConversionWorkerPath
            };
            if (string.IsNullOrWhiteSpace(conversionRequest.Repository))
            {
                string owner = GetString(root, "owner") ?? "";
                string name = GetString(root, "name") ?? "";
                if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name))
                    conversionRequest = new ModelConversionRequest
                    {
                        Repository = owner.Trim('/') + "/" + name.Trim('/'),
                        Revision = conversionRequest.Revision,
                        SourcePaths = conversionRequest.SourcePaths,
                        Task = conversionRequest.Task,
                        Precision = conversionRequest.Precision,
                        Opset = conversionRequest.Opset,
                        PythonPath = conversionRequest.PythonPath,
                        ConverterPath = conversionRequest.ConverterPath
                    };
            }

            var job = _conversionService.StartConversion(conversionRequest);
            return ToJson(ToConversionJob(job));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "conversion_error", "conversion_start_failed");
        }
    }

    private string HandleConversionStatus(HttpRequest request)
    {
        string? jobId = request.QueryParameters.TryGetValue("job_id", out var value) ? value : null;
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            var job = _conversionService.GetJob(jobId);
            return job == null
                ? Error($"Conversion job not found: {jobId}", "not_found_error", "conversion_job_not_found")
                : ToJson(ToConversionJob(job));
        }

        return ToJson(new { conversions = _conversionService.ListJobs().Select(ToConversionJob).ToArray() });
    }

    private string HandleConversionCancel(HttpRequest request)
    {
        try
        {
            string jobId = request.QueryParameters.TryGetValue("job_id", out var queryId) ? queryId ?? "" : "";
            if (string.IsNullOrWhiteSpace(jobId))
            {
                using var document = ParseBody(request);
                jobId = GetString(document.RootElement, "job_id") ?? GetString(document.RootElement, "jobId") ?? "";
            }

            if (string.IsNullOrWhiteSpace(jobId))
                return Error("Conversion cancel requires job_id.", "invalid_request_error", "missing_job_id");

            bool cancelled = _conversionService.Cancel(jobId);
            var job = _conversionService.GetJob(jobId);
            if (job == null)
                return Error($"Conversion job not found: {jobId}", "not_found_error", "conversion_job_not_found");

            return ToJson(new { cancelled, conversion = ToConversionJob(job) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "conversion_error", "conversion_cancel_failed");
        }
    }

    private string HandleToolList(HttpRequest request)
    {
        LlmToolVisibility? visibility = null;
        if (request.QueryParameters.TryGetValue("visibility", out string? rawVisibility)
            && !string.IsNullOrWhiteSpace(rawVisibility)
            && Enum.TryParse(rawVisibility.Replace("-", "", StringComparison.OrdinalIgnoreCase), ignoreCase: true, out LlmToolVisibility parsedVisibility))
        {
            visibility = parsedVisibility;
        }

        return ToJson(new { tools = ToolRegistry.ListDefinitions(visibility) });
    }

    private string HandleToolUpsert(HttpRequest request)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<LlmToolDefinition>(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body, JsonOptions);
            if (definition == null)
                return Error("Tool definition is required.", "invalid_request_error", "missing_tool_definition");

            definition = ToolRegistry.UpsertDefinition(definition);
            return ToJson(new { status = "saved", tool = definition });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "tool_registry_error", "tool_save_failed");
        }
    }

    private string HandleToolDelete(HttpRequest request)
    {
        try
        {
            string id = request.QueryParameters.TryGetValue("id", out string? queryId) ? queryId : "";
            if (string.IsNullOrWhiteSpace(id))
            {
                using var document = ParseBody(request);
                id = GetString(document.RootElement, "id") ?? GetString(document.RootElement, "tool_id") ?? "";
            }

            if (string.IsNullOrWhiteSpace(id))
                return Error("Tool id is required.", "invalid_request_error", "missing_tool_id");

            bool deleted = ToolRegistry.DeleteDefinition(id);
            return deleted
                ? ToJson(new { status = "deleted", id = LlmToolRegistry.NormalizeId(id) })
                : Error($"Tool definition not found: {id}", "not_found_error", "tool_not_found");
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "tool_registry_error", "tool_delete_failed");
        }
    }

    private string HandleToolSecret(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            string secretId = GetString(document.RootElement, "secret_id") ?? GetString(document.RootElement, "id") ?? "";
            string? value = GetString(document.RootElement, "value");
            bool delete = GetBool(document.RootElement, "delete") ?? false;

            if (string.IsNullOrWhiteSpace(secretId))
                return Error("Secret id is required.", "invalid_request_error", "missing_secret_id");

            if (delete)
            {
                bool deleted = ToolRegistry.DeleteSecret(secretId);
                return ToJson(new { status = deleted ? "deleted" : "not_found", secret_id = LlmToolRegistry.NormalizeId(secretId) });
            }

            if (value == null)
                return Error("Secret value is required.", "invalid_request_error", "missing_secret_value");

            ToolRegistry.SetSecret(secretId, value);
            return ToJson(new { status = "saved", secret_id = LlmToolRegistry.NormalizeId(secretId) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "tool_secret_error", "tool_secret_failed");
        }
    }

    private string HandleToolInvoke(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = ParseBody(request);
            var invocation = ParseToolInvocation(document.RootElement);
            if (string.IsNullOrWhiteSpace(invocation.ToolId))
                return Error("Tool invocation requires a tool_id.", "invalid_request_error", "missing_tool_id");

            var result = ToolInvoker.InvokeAsync(invocation, cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "tool_invocation_error", "tool_invocation_failed");
        }
    }

    private static LlmToolInvocationRequest ParseToolInvocation(JsonElement root)
    {
        JsonElement input = JsonDocument.Parse("{}").RootElement.Clone();
        if (root.TryGetProperty("input", out var inputElement))
            input = inputElement.Clone();

        return new LlmToolInvocationRequest
        {
            ToolId = GetString(root, "tool_id") ?? GetString(root, "toolId") ?? GetString(root, "id") ?? "",
            ToolCallId = GetString(root, "tool_call_id") ?? GetString(root, "toolCallId") ?? GetString(root, "call_id") ?? GetString(root, "callId") ?? "",
            Input = input,
            Approved = GetBool(root, "approved") ?? false,
            ProjectPath = GetString(root, "project_path") ?? GetString(root, "projectPath") ?? ""
        };
    }

    private string HandleToolCalls(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            bool approved = GetBool(root, "approved") ?? false;
            string projectPath = GetString(root, "project_path") ?? GetString(root, "projectPath") ?? "";
            var calls = ParseToolCalls(root).ToArray();
            var results = ToolCallLoop.ExecuteAsync(calls, approved, projectPath, cancellationToken).GetAwaiter().GetResult();
            return ToJson(new { results });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "tool_call_error", "tool_call_failed");
        }
    }

    private static IEnumerable<LlmToolCall> ParseToolCalls(JsonElement root)
    {
        JsonElement callsElement = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetPropertyAny(root, out var nestedCalls, "tool_calls", "toolCalls", "calls"))
            callsElement = nestedCalls;
        else if (root.ValueKind == JsonValueKind.Object && TryGetPropertyAny(root, out var singleCall, "tool_call", "toolCall", "function_call", "functionCall"))
            callsElement = singleCall;

        if (callsElement.ValueKind == JsonValueKind.Object)
        {
            if (TryParseToolCall(callsElement, out var call))
                yield return call;
            yield break;
        }

        if (callsElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var callElement in callsElement.EnumerateArray())
        {
            if (TryParseToolCall(callElement, out var call))
                yield return call;
        }
    }

    private static bool TryParseToolCall(JsonElement callElement, out LlmToolCall call)
    {
        call = new LlmToolCall();
        if (callElement.ValueKind != JsonValueKind.Object)
            return false;

        string name = GetStringAny(callElement, "name", "tool", "tool_name", "toolName") ?? "";
        JsonElement arguments = EmptyJsonObject();

        if (TryGetPropertyAny(callElement, out var argumentsElement, "arguments", "args", "input", "parameters"))
            arguments = CloneToolArguments(argumentsElement);

        if (TryGetPropertyAny(callElement, out var functionElement, "function") && functionElement.ValueKind == JsonValueKind.Object)
        {
            name = GetStringAny(functionElement, "name", "tool", "tool_name", "toolName") ?? name;
            if (TryGetPropertyAny(functionElement, out var openAiArguments, "arguments", "args", "input", "parameters"))
                arguments = CloneToolArguments(openAiArguments);
        }
        else if (string.IsNullOrWhiteSpace(name) && TryGetPropertyAny(callElement, out functionElement, "function") && functionElement.ValueKind == JsonValueKind.String)
        {
            name = functionElement.GetString() ?? "";
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        call = new LlmToolCall
        {
            Id = GetStringAny(callElement, "id", "tool_call_id", "toolCallId", "call_id", "callId") ?? "call_" + Guid.NewGuid().ToString("N"),
            Name = name,
            Arguments = arguments
        };
        return true;
    }

    private static JsonElement EmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement CloneToolArguments(JsonElement argumentsElement)
    {
        if (argumentsElement.ValueKind == JsonValueKind.String)
        {
            string raw = argumentsElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                return EmptyJsonObject();
            try
            {
                using var parsedArguments = JsonDocument.Parse(raw);
                return parsedArguments.RootElement.Clone();
            }
            catch
            {
                return JsonSerializer.SerializeToElement(new { value = raw }, JsonOptions);
            }
        }

        if (argumentsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return EmptyJsonObject();

        return argumentsElement.Clone();
    }

    private string HandleToolAudit(HttpRequest request)
    {
        string? toolId = request.QueryParameters.TryGetValue("tool_id", out string? queryToolId) ? queryToolId : null;
        int limit = request.QueryParameters.TryGetValue("limit", out string? rawLimit) && int.TryParse(rawLimit, out int parsedLimit)
            ? parsedLimit
            : 200;
        return ToJson(new { audit = ToolRegistry.ListAuditEntries(toolId, limit) });
    }

    private string HandleAgentCreateSession(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string goal = GetString(root, "goal") ?? "";
            if (string.IsNullOrWhiteSpace(goal))
                return Error("Agent session goal is required.", "invalid_request_error", "missing_goal");

            var session = AgentRuntime.CreateSession(
                goal,
                GetString(root, "workspace_root") ?? GetString(root, "workspaceRoot"),
                GetSandbox(root, "sandbox") ?? LlmAgentSandboxProfile.WorkspaceWrite,
                GetString(root, "title"));
            return ToJson(new { session });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_create_failed");
        }
    }

    private string HandleAgentGetSession(HttpRequest request)
    {
        try
        {
            string id = request.QueryParameters.TryGetValue("id", out string? queryId) ? queryId : "";
            if (string.IsNullOrWhiteSpace(id))
                return Error("Agent session id is required.", "invalid_request_error", "missing_session_id");
            return ToJson(new { session = AgentRuntime.GetSession(id) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_session_failed");
        }
    }

    private string HandleAgentPlan(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string sessionId = GetSessionId(root);
            var steps = ParsePlanSteps(root).ToArray();
            return ToJson(new { session = AgentRuntime.SavePlan(sessionId, steps) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_plan_failed");
        }
    }

    private string HandleAgentPhase(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string phase = GetString(root, "phase") ?? "";
            var status = GetAgentStatus(root, "status") ?? LlmAgentSessionStatus.Running;
            return ToJson(new { session = AgentRuntime.UpdatePhase(GetSessionId(root), phase, status) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_phase_failed");
        }
    }

    private string HandleAgentReadFile(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(AgentRuntime.ReadFile(GetSessionId(root), GetString(root, "path") ?? ""));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_file_error", "agent_read_failed");
        }
    }

    private string HandleAgentPreviewFile(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(AgentRuntime.PreviewWriteFile(GetSessionId(root), GetString(root, "path") ?? "", GetString(root, "content") ?? ""));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_file_error", "agent_preview_failed");
        }
    }

    private string HandleAgentWriteFile(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            bool approved = GetBool(root, "approved") ?? false;
            return ToJson(AgentRuntime.WriteFile(GetSessionId(root), GetString(root, "path") ?? "", GetString(root, "content") ?? "", approved));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_file_error", "agent_write_failed");
        }
    }

    private string HandleAgentRunCommand(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var result = AgentRuntime.RunCommandAsync(
                GetSessionId(root),
                GetString(root, "command") ?? "",
                GetBool(root, "approved") ?? false,
                GetInt32(root, "timeout_seconds") ?? 120,
                cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_terminal_error", "agent_command_failed");
        }
    }

    private string HandleAgentRunChecks(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var result = AgentRuntime.RunCheckLoopAsync(
                GetSessionId(root),
                GetStringArray(root, "commands"),
                GetBool(root, "approved") ?? false,
                GetInt32(root, "timeout_seconds") ?? 300,
                cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_check_error", "agent_checks_failed");
        }
    }

    private string HandleAgentStartCommandJob(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var job = AgentRuntime.StartCommandJob(
                GetSessionId(root),
                GetString(root, "name") ?? "",
                GetString(root, "command") ?? "",
                GetBool(root, "approved") ?? false,
                GetInt32(root, "timeout_seconds") ?? 120);
            return ToJson(new { job });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_job_error", "agent_job_failed");
        }
    }

    private string HandleAgentListJobs(HttpRequest request)
    {
        string? sessionId = request.QueryParameters.TryGetValue("session_id", out string? querySessionId) ? querySessionId : null;
        return ToJson(new { jobs = AgentRuntime.ListJobs(sessionId) });
    }

    private string HandleAgentSubAgents(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var children = AgentRuntime.CreateSubAgents(GetSessionId(root), GetStringArray(root, "goals"));
            return ToJson(new { sessions = children });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_subagents_failed");
        }
    }

    private string HandleAgentSelfCorrectionPlan(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            var steps = AgentRuntime.CreateSelfCorrectionPlan(GetSessionId(root), GetString(root, "failure_summary") ?? GetString(root, "failureSummary") ?? "");
            return ToJson(new { steps });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_self_correction_failed");
        }
    }

    private string HandleAgentProgress(HttpRequest request)
    {
        try
        {
            string id = request.QueryParameters.TryGetValue("session_id", out string? queryId) ? queryId : "";
            if (string.IsNullOrWhiteSpace(id))
                id = request.QueryParameters.TryGetValue("id", out string? shortId) ? shortId : "";
            var session = AgentRuntime.GetSession(id);
            return ToJson(new { session.Id, session.Status, session.Phase, session.Plan, events = session.Events });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_progress_failed");
        }
    }

    private string HandleAgentRepoContext(HttpRequest request)
    {
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(new { context = AgentRuntime.DiscoverRepoContext(workspace) });
    }

    private string HandleAgentAutomationHook(HttpRequest request)
    {
        try
        {
            var hook = JsonSerializer.Deserialize<LlmAgentAutomationHook>(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body, JsonOptions);
            if (hook == null)
                return Error("Automation hook is required.", "invalid_request_error", "missing_hook");
            return ToJson(new { hook = AgentRuntime.UpsertAutomationHook(hook) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_automation_error", "agent_automation_failed");
        }
    }

    private string HandleAgentAutonomousRun(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var runRequest = JsonSerializer.Deserialize<LlmAutonomousAgentRunRequest>(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body, JsonOptions)
                ?? new LlmAutonomousAgentRunRequest();
            if (string.IsNullOrWhiteSpace(runRequest.Goal))
                return Error("Autonomous agent goal is required.", "invalid_request_error", "missing_goal");

            var result = AutonomousAgents.RunAsync(runRequest, cancellationToken).GetAwaiter().GetResult();
            return ToJson(result);
        }
        catch (LlmRuntimeException ex)
        {
            return Error(ex.Message, ex.Type, ex.Code);
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "agent_error", "agent_autonomous_failed");
        }
    }

    private string HandleGitHubRepository(HttpRequest request)
    {
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(new { repository = GitHubWorkflows.InspectRepository(workspace) });
    }

    private string HandleGitHubItem(HttpRequest request)
    {
        string type = request.QueryParameters.TryGetValue("type", out string? queryType) ? queryType : "issue";
        int number = request.QueryParameters.TryGetValue("number", out string? rawNumber) && int.TryParse(rawNumber, out int parsedNumber) ? parsedNumber : 0;
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(new { item = GitHubWorkflows.FetchIssueOrPullRequest(type, number, workspace) });
    }

    private string HandleGitHubTask(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(GitHubWorkflows.StartAgentTaskFromItem(GetString(root, "type") ?? "issue", GetInt32(root, "number") ?? 0, GetWorkspace(root)));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "github_error", "github_task_failed");
        }
    }

    private string HandleGitHubBranch(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(GitHubWorkflows.CreateOrSwitchBranch(GetString(root, "branch") ?? "", GetWorkspace(root), GetBool(root, "approved") ?? false));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "git_error", "git_branch_failed");
        }
    }

    private string HandleGitHubCommit(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(GitHubWorkflows.Commit(GetString(root, "message") ?? "Agent changes", GetStringArray(root, "paths"), GetWorkspace(root), GetBool(root, "approved") ?? false));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "git_error", "git_commit_failed");
        }
    }

    private string HandleGitHubPullRequest(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(GitHubWorkflows.CreateDraftPullRequest(
                GetString(root, "title") ?? "Agent changes",
                GetString(root, "body") ?? "",
                GetWorkspace(root),
                GetBool(root, "approved") ?? false,
                GetString(root, "base") ?? GetString(root, "base_branch") ?? GetString(root, "baseBranch"),
                GetBool(root, "push") ?? GetBool(root, "push_branch") ?? GetBool(root, "pushBranch") ?? true));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "github_error", "github_pr_failed");
        }
    }

    private string HandleGitHubPullRequestJob(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(new
            {
                job = GitHubWorkflows.StartDraftPullRequestJob(
                    GetString(root, "title") ?? "Agent changes",
                    GetString(root, "body") ?? "",
                    GetWorkspace(root),
                    GetBool(root, "approved") ?? false,
                    GetString(root, "base") ?? GetString(root, "base_branch") ?? GetString(root, "baseBranch"),
                    GetBool(root, "push") ?? GetBool(root, "push_branch") ?? GetBool(root, "pushBranch") ?? true)
            });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "github_error", "github_pr_job_failed");
        }
    }

    private string HandleGitHubJobs(HttpRequest request)
    {
        string? kind = request.QueryParameters.TryGetValue("kind", out string? queryKind) ? queryKind : null;
        return ToJson(new { jobs = GitHubWorkflows.ListJobs(kind) });
    }

    private string HandleGitHubCommitSummary(HttpRequest request)
    {
        string? sha = request.QueryParameters.TryGetValue("sha", out string? querySha) ? querySha : null;
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(new { summary = GitHubWorkflows.SummarizeCommit(sha, workspace) });
    }

    private string HandleGitHubPullRequestSummary(HttpRequest request)
    {
        int number = request.QueryParameters.TryGetValue("number", out string? rawNumber) && int.TryParse(rawNumber, out int parsedNumber) ? parsedNumber : 0;
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(new { summary = GitHubWorkflows.SummarizePullRequest(number, workspace) });
    }

    private string HandleGitHubReview(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            return ToJson(GitHubWorkflows.ReviewDiff(GetWorkspace(document.RootElement)));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "github_review_error", "github_review_failed");
        }
    }

    private string HandleGitHubAddressReview(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            return ToJson(GitHubWorkflows.AddressReviewComments(GetStringArray(root, "comments"), GetWorkspace(root)));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "github_review_error", "github_address_review_failed");
        }
    }

    private string HandleGitHubActions(HttpRequest request)
    {
        string? workspace = request.QueryParameters.TryGetValue("workspace_root", out string? queryWorkspace) ? queryWorkspace : null;
        return ToJson(GitHubWorkflows.DebugFailedActions(workspace));
    }

    private string HandleGitHubSecurityReview(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            return ToJson(GitHubWorkflows.SecurityReview(GetWorkspace(document.RootElement)));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "security_review_error", "security_review_failed");
        }
    }

    private string HandleGitHubDependencyPlan(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            return ToJson(GitHubWorkflows.DependencyUpdatePlan(GetWorkspace(document.RootElement)));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "dependency_plan_error", "dependency_plan_failed");
        }
    }

    private string HandleGitHubPolicy(HttpRequest request)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<LlmAgentPolicy>(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body, JsonOptions) ?? new LlmAgentPolicy();
            return ToJson(new { policy = GitHubWorkflows.SavePolicy(policy) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "policy_error", "policy_save_failed");
        }
    }

    private string HandleGitHubAudit(HttpRequest request)
    {
        int limit = request.QueryParameters.TryGetValue("limit", out string? rawLimit) && int.TryParse(rawLimit, out int parsedLimit) ? parsedLimit : 200;
        return ToJson(new { audit = GitHubWorkflows.ListAudit(limit) });
    }

    private string HandleIdeContext(HttpRequest request, Func<LlmIdeContextRequest, object> handler)
    {
        try
        {
            var context = JsonSerializer.Deserialize<LlmIdeContextRequest>(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body, JsonOptions) ?? new LlmIdeContextRequest();
            return ToJson(handler(context));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_error", "ide_request_failed");
        }
    }

    private string HandleIdeWorkspace(HttpRequest request, Func<string, object> handler)
    {
        try
        {
            using var document = ParseBody(request);
            string workspace = GetWorkspace(document.RootElement) ?? "";
            return ToJson(handler(workspace));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_error", "ide_workspace_failed");
        }
    }

    private string HandleIdeCheckpoint(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string workspace = GetWorkspace(root) ?? "";
            IReadOnlyList<string> files = GetStringArray(root, "files").ToList();
            return ToJson(IdeIntegration.CreateCheckpoint(workspace, files));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_checkpoint_error", "checkpoint_failed");
        }
    }

    private string HandleIdeRollback(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string workspace = GetWorkspace(root) ?? "";
            string checkpointId = GetString(root, "checkpoint_id") ?? GetString(root, "checkpointId") ?? "";
            if (string.IsNullOrWhiteSpace(checkpointId))
                return Error("checkpoint_id is required.", "invalid_request_error", "missing_checkpoint_id");
            return ToJson(IdeIntegration.Rollback(checkpointId, workspace));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_checkpoint_error", "rollback_failed");
        }
    }

    private string HandleIdeIndex(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            int maxFiles = GetInt32(root, "max_files") ?? GetInt32(root, "maxFiles") ?? 300;
            return ToJson(IdeIntegration.BuildWorkspaceIndex(GetWorkspace(root) ?? "", maxFiles));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_index_error", "index_failed");
        }
    }

    private string HandleIdeSearch(HttpRequest request)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            int maxFiles = GetInt32(root, "max_files") ?? GetInt32(root, "maxFiles") ?? 300;
            string query = GetString(root, "query") ?? "";
            return ToJson(new { results = IdeIntegration.Search(GetWorkspace(root) ?? "", query, maxFiles) });
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "ide_search_error", "search_failed");
        }
    }

    private string HandleCodeWorkspace(HttpRequest request, Func<string, object> handler)
    {
        try
        {
            using var document = ParseBody(request);
            string workspace = GetWorkspace(document.RootElement) ?? "";
            return ToJson(handler(workspace));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "code_intelligence_error", "code_intelligence_failed");
        }
    }

    private string HandleCodeRequest(HttpRequest request, Func<JsonElement, string, object> handler)
    {
        try
        {
            using var document = ParseBody(request);
            JsonElement root = document.RootElement;
            string workspace = GetWorkspace(root) ?? "";
            return ToJson(handler(root, workspace));
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "code_intelligence_error", "code_intelligence_failed");
        }
    }

    private async Task HandleChatCompletion(HttpRequest request, ChunkedStream stream, CancellationToken cancellationToken)
    {
        LlmChatRequest chatRequest;
        try
        {
            using var document = ParseBody(request);
            chatRequest = LlmChatRequest.FromJson(document.RootElement);
        }
        catch (Exception ex)
        {
            request.Context.StatusCodeNumber = 400;
            request.Context.ReasonPhrase = "Bad Request";
            stream.ContentType = "application/json";
            stream.Write(Error($"Invalid JSON request body: {ex.Message}", "invalid_request_error", "invalid_json"));
            return;
        }

        if (RejectGenerationModelChatRequest(chatRequest, request, stream))
            return;

        ApplyAutomaticCompletionBudget(chatRequest);

        if (chatRequest.Stream)
        {
            await StreamChatCompletion(chatRequest, request, stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteChatCompletion(chatRequest, request, stream, cancellationToken).ConfigureAwait(false);
    }

    private bool RejectGenerationModelChatRequest(LlmChatRequest request, HttpRequest httpRequest, ChunkedStream stream)
    {
        var model = Registry.FindModel(request.Model);
        if (model == null || !LlmModelRegistry.IsGenerationModel(model))
            return false;

        string service = LlmModelRegistry.GetRuntimeServiceForModel(model);
        string serviceLabel = string.IsNullOrWhiteSpace(service) ? "the media generation tool route" : service;
        SetStatus(httpRequest, HttpStatusForError("unsupported_model_type"));
        stream.ContentType = "application/json";
        stream.Write(Error(
            $"Model '{model.DisplayName}' is a {model.Type} generation model. Use {serviceLabel} instead of the text chat completions endpoint.",
            "unsupported_model_type",
            "unsupported_model_type"));
        return true;
    }

    private void ApplyAutomaticCompletionBudget(LlmChatRequest request)
    {
        if (request.MaxTokensSpecified)
            return;

        request.MaxTokens = ResolveAutomaticCompletionBudget(request);
    }

    private int ResolveAutomaticCompletionBudget(LlmChatRequest request)
    {
        int contextLength = ResolveContextLength(request.Model);
        int promptTokens = LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content)));
        int reservedTokens = Math.Max(256, contextLength / 16);
        int available = contextLength - promptTokens - reservedTokens;

        if (available > 0)
            return Math.Clamp(available, 128, LlmChatRequest.DefaultMaxCompletionTokens);

        return Math.Clamp(contextLength / 4, 128, LlmChatRequest.DefaultMaxCompletionTokens);
    }

    private int ResolveContextLength(string model)
    {
        uint contextLength = 0;
        LlmModelInfo? modelInfo = Registry.FindModel(model);
        if (modelInfo != null)
        {
            contextLength = modelInfo.LoadedInstances.FirstOrDefault()?.Config.ContextLength ?? 0;
            if (contextLength == 0)
                contextLength = modelInfo.MaxContextLength ?? 0;
        }

        if (contextLength == 0)
            contextLength = _options.DefaultContextLength;

        return (int)Math.Clamp(contextLength, 512u, 131072u);
    }

    private async Task CompleteChatCompletion(LlmChatRequest request, HttpRequest httpRequest, ChunkedStream stream, CancellationToken cancellationToken)
    {
        try
        {
            if (TryBuildDeterministicExactReply(request, out var deterministicResult))
            {
                stream.ContentType = "application/json";
                stream.Write(ToJson(ToOpenAiChatCompletion(deterministicResult, request)));
                return;
            }

            var result = await CompleteChatWithToolsAsync(request, cancellationToken).ConfigureAwait(false);
            stream.ContentType = "application/json";
            stream.Write(ToJson(ToOpenAiChatCompletion(result, request)));
        }
        catch (LlmRuntimeException ex)
        {
            SetStatus(httpRequest, HttpStatusForError(ex.Code));
            stream.ContentType = "application/json";
            stream.Write(Error(ex.Message, ex.Type, ex.Code));
        }
        catch (OperationCanceledException)
        {
            SetStatus(httpRequest, (499, "Client Closed Request"));
            stream.ContentType = "application/json";
            stream.Write(Error("The inference request was canceled.", "request_canceled", "request_canceled"));
        }
        catch (Exception ex)
        {
            SetStatus(httpRequest, (500, "Internal Server Error"));
            stream.ContentType = "application/json";
            stream.Write(Error(ex.Message, "inference_error", "inference_failed"));
        }
    }

    private async Task<LlmChatResult> CompleteChatWithToolsAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        if (request.Tools.Count == 0)
            return await Registry.CompleteChatAsync(request, cancellationToken).ConfigureAwait(false);

        if (ShouldBypassToolSelection(request))
            return await Registry.CompleteChatAsync(request, cancellationToken).ConfigureAwait(false);

        if (HasToolResultMessages(request) && ToolChoiceRequestsNoTool(request))
        {
            var finalAnswerRequest = CloneChatRequest(request, prependMessages:
            [
                new LlmChatMessage("system", "Tool results have already been executed for this turn. Answer the user from those results. Do not call tools, do not output tool JSON, do not output XML tool transcripts, and do not repeat file writes.")
            ]);
            var finalAnswer = await Registry.CompleteChatAsync(finalAnswerRequest, cancellationToken).ConfigureAwait(false);
            return SanitizeToolResultFinalAnswer(finalAnswer, request);
        }

        var firstPassRequest = CloneChatRequest(request, prependMessages:
        [
            new LlmChatMessage("system", BuildToolUseInstruction(request.Tools))
        ]);
        ApplyToolSelectionBudget(firstPassRequest);
        var firstPass = await Registry.CompleteChatAsync(firstPassRequest, cancellationToken).ConfigureAwait(false);
        var toolCalls = ParseToolCallsFromModelText(firstPass.Content).ToArray();
        if (toolCalls.Length == 0 && LooksLikeToolCallAttempt(firstPass.Content, request.Tools))
        {
            var repairRequest = CloneChatRequest(request, messages:
            [
                new LlmChatMessage("system", BuildToolUseInstruction(request.Tools)),
                .. request.Messages,
                new LlmChatMessage("assistant", firstPass.Content),
                new LlmChatMessage("system", BuildToolRepairInstruction())
            ]);
            ApplyToolSelectionBudget(repairRequest);
            var repairedPass = await Registry.CompleteChatAsync(repairRequest, cancellationToken).ConfigureAwait(false);
            var repairedCalls = ParseToolCallsFromModelText(repairedPass.Content).ToArray();
            if (repairedCalls.Length > 0)
            {
                firstPass = repairedPass;
                toolCalls = repairedCalls;
            }
            else
            {
                if (TryRescueToolCallsFromIntent(request, firstPass.Content + Environment.NewLine + repairedPass.Content, out var rescuedCalls))
                {
                    firstPass = repairedPass;
                    toolCalls = rescuedCalls.ToArray();
                }
                else
                {
                    return new LlmChatResult
                    {
                        Model = repairedPass.Model,
                        Content = "The model attempted a tool call but returned malformed JSON. Please retry.",
                        FinishReason = string.IsNullOrWhiteSpace(repairedPass.FinishReason) ? "stop" : repairedPass.FinishReason,
                        Metrics = repairedPass.Metrics
                    };
                }
            }
        }

        if (toolCalls.Length == 0 && TryRescueToolCallsFromIntent(request, firstPass.Content, out var intentRescuedCalls))
        {
            toolCalls = intentRescuedCalls.ToArray();
        }

        if (toolCalls.Length == 0)
            return firstPass;

        return new LlmChatResult
        {
            Model = firstPass.Model,
            Content = "",
            FinishReason = "tool_calls",
            Metrics = firstPass.Metrics,
            ToolCalls = toolCalls
        };
    }

    private static void ApplyToolSelectionBudget(LlmChatRequest request)
    {
        const int toolSelectionMaxTokens = 512;
        int requested = request.MaxTokens <= 0 ? toolSelectionMaxTokens : request.MaxTokens;
        request.MaxTokens = Math.Clamp(Math.Min(requested, toolSelectionMaxTokens), 64, toolSelectionMaxTokens);
        request.MaxTokensSpecified = true;
        request.Temperature = Math.Min(Math.Max(request.Temperature, 0f), 0.2f);
    }

    private static bool TryRescueToolCallsFromIntent(LlmChatRequest request, string attemptedContent, out IReadOnlyList<LlmToolCall> toolCalls)
    {
        toolCalls = [];
        if (!ToolIsAvailable(request.Tools, "vs_write_file"))
            return false;

        string combined = string.Join(Environment.NewLine, request.Messages
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content)
            .Append(attemptedContent ?? ""));

        if (!TryExtractWriteFileIntent(combined, out string path, out string content))
            return false;

        toolCalls =
        [
            new LlmToolCall
            {
                Id = "call_" + Guid.NewGuid().ToString("N"),
                Name = "vs_write_file",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    path,
                    content,
                    overwrite = false
                }, JsonOptions)
            }
        ];
        return true;
    }

    private static bool ToolIsAvailable(IReadOnlyList<JsonElement> tools, string name)
    {
        return tools.Any(tool => string.Equals(ReadToolName(tool), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryExtractWriteFileIntent(string text, out string path, out string content)
    {
        path = ExtractJsonStringField(text, "path");
        content = ExtractJsonStringField(text, "content");
        if (string.IsNullOrWhiteSpace(path))
        {
            var pathMatch = Regex.Match(text, @"(?:file\s+named|named|file)\s+(?:""(?<quoted>[^""]+)""|`(?<tick>[^`]+)`|(?<bare>[A-Za-z0-9][A-Za-z0-9._\-/\\ ]{0,180}\.[A-Za-z0-9]{1,12}))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            path = pathMatch.Success
                ? FirstNonEmptyString(pathMatch.Groups["quoted"].Value, pathMatch.Groups["tick"].Value, pathMatch.Groups["bare"].Value).Trim()
                : "";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            var contentMatch = Regex.Match(text, @"(?:exactly\s+this\s+content|with\s+exactly\s+this\s+content|content)\s*:?\s*(?<content>.+?)(?:\.\s+Use\b|\.\s+Do\b|\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            content = contentMatch.Success ? contentMatch.Groups["content"].Value.Trim() : "";
        }

        path = path.Trim().Trim('"', '`');
        content = content.Trim().Trim('"', '`');
        return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(content);
    }

    private static string FirstNonEmptyString(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string ExtractJsonStringField(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(field))
            return "";

        var match = Regex.Match(text, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return "";

        string raw = match.Groups["value"].Value;
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"", JsonOptions) ?? "";
        }
        catch
        {
            return raw.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal);
        }
    }

    private async Task StreamChatCompletion(LlmChatRequest request, HttpRequest httpRequest, ChunkedStream stream, CancellationToken cancellationToken)
    {
        stream.LowLatencyMode = true;
        stream.SetHeader("X-SocketJack-Low-Latency", "1");

        if (TryBuildDeterministicExactReply(request, out var deterministicResult))
        {
            WriteDeterministicChatStream(request, deterministicResult, stream);
            return;
        }

        string resolvedModel;
        try
        {
            if (!Registry.TryEnsureModelLoadedForInference(request.Model, out resolvedModel, cancellationToken))
            {
                SetStatus(httpRequest, HttpStatusForError("model_not_loaded"));
                stream.ContentType = "application/json";
                stream.Write(Error("No model is loaded for inference. Use POST /api/v1/models/load first.", "model_not_loaded", "model_not_loaded"));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(httpRequest, (499, "Client Closed Request"));
            stream.ContentType = "application/json";
            stream.Write(Error("The inference request was canceled.", "request_canceled", "request_canceled"));
            return;
        }
        catch (Exception ex)
        {
            SetStatus(httpRequest, (500, "Internal Server Error"));
            stream.ContentType = "application/json";
            stream.Write(Error(ex.Message, "model_load_error", "load_failed"));
            return;
        }

        request.Model = resolvedModel;
        ApplyAutomaticCompletionBudget(request);
        string id = NewCompletionId("chatcmpl");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int completionTokens = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool sseStarted = false;

        try
        {
            stream.ContentType = "text/event-stream";
            sseStarted = true;

            if (request.Tools.Count > 0 && !ShouldBypassToolSelection(request))
            {
                var result = await CompleteChatWithToolsAsync(request, cancellationToken).ConfigureAwait(false);
                int promptTokens = result.Metrics.PromptTokens > 0
                    ? result.Metrics.PromptTokens
                    : LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content)));
                completionTokens = result.Metrics.CompletionTokens > 0
                    ? result.Metrics.CompletionTokens
                    : LlmInferenceMetrics.EstimateTokens(result.Content);
                stopwatch.Stop();
                var toolUsage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens
                };
                var toolStats = new
                {
                    elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
                    tokens_per_second = completionTokens / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d),
                    managed_memory_bytes = GC.GetTotalMemory(false)
                };

                WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", "assistant", null, null)));
                if (result.ToolCalls.Count > 0)
                {
                    WriteSseData(stream, ToJson(ToOpenAiToolCallChunk(id, created, request.Model, result.ToolCalls, null, null)));
                    WriteSseData(stream, ToJson(ToOpenAiToolCallChunk(id, created, request.Model, [], "tool_calls", new { usage = toolUsage, stats = toolStats, session = ToSessionCompatibilityPayload(request, toolUsage.prompt_tokens, completionTokens) })));
                }
                else
                {
                    if (!string.IsNullOrEmpty(result.Content))
                        WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, result.Content, null, null, null)));
                    WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", null, string.IsNullOrWhiteSpace(result.FinishReason) ? "stop" : result.FinishReason, new { usage = toolUsage, stats = toolStats, session = ToSessionCompatibilityPayload(request, toolUsage.prompt_tokens, completionTokens) })));
                }

                WriteSseData(stream, "[DONE]");
                return;
            }

            WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", "assistant", null, null)));

            await foreach (var token in Registry.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                completionTokens += LlmInferenceMetrics.EstimateTokens(token.Text);
                WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, token.Text, null, null, null)));
            }

            stopwatch.Stop();
            var usage = new
            {
                prompt_tokens = LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content))),
                completion_tokens = completionTokens,
                total_tokens = LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content))) + completionTokens
            };
            var stats = new
            {
                elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
                tokens_per_second = completionTokens / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d),
                managed_memory_bytes = GC.GetTotalMemory(false)
            };

            WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", null, "stop", new { usage, stats, session = ToSessionCompatibilityPayload(request, usage.prompt_tokens, completionTokens) })));
            WriteSseData(stream, "[DONE]");
        }
        catch (LlmRuntimeException ex)
        {
            if (sseStarted)
                WriteStreamChatErrorAsSse(stream, id, created, request.Model, ex.Message, ex.Type, ex.Code);
            else
            {
                SetStatus(httpRequest, HttpStatusForError(ex.Code));
                stream.ContentType = "application/json";
                stream.Write(Error(ex.Message, ex.Type, ex.Code));
            }
        }
        catch (OperationCanceledException)
        {
            if (sseStarted)
                WriteStreamChatErrorAsSse(stream, id, created, request.Model, "The inference request was canceled.", "request_canceled", "request_canceled");
            else
            {
                SetStatus(httpRequest, (499, "Client Closed Request"));
                stream.ContentType = "application/json";
                stream.Write(Error("The inference request was canceled.", "request_canceled", "request_canceled"));
            }
        }
        catch (Exception ex)
        {
            if (sseStarted)
                WriteStreamChatErrorAsSse(stream, id, created, request.Model, ex.Message, "inference_error", "inference_failed");
            else
            {
                SetStatus(httpRequest, (500, "Internal Server Error"));
                stream.ContentType = "application/json";
                stream.Write(Error(ex.Message, "inference_error", "inference_failed"));
            }
        }
    }

    private static void WriteStreamChatErrorAsSse(ChunkedStream stream, string id, long created, string model, string message, string type, string code)
    {
        string readable = TrimStreamErrorMessage(message);
        var error = new { message = readable, type, code };
        try
        {
            WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, model, "\n\nModel runtime error: " + readable, null, null, new { error })));
            WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, model, "", null, "error", new { error })));
            WriteSseData(stream, "[DONE]");
        }
        catch
        {
            // The client may have disconnected after headers were sent.
        }
    }

    private static string TrimStreamErrorMessage(string? message)
    {
        message = string.IsNullOrWhiteSpace(message) ? "The model runtime stopped before it could finish the response." : message.Trim();
        message = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return message.Length <= 1200 ? message : message[..1200] + "...";
    }

    private static void WriteSseData(ChunkedStream stream, string payload)
    {
        stream.Write("data: " + (payload ?? "") + "\n\n");
    }

    private string InferenceScaffold(HttpRequest request, string objectName)
    {
        string? model = null;
        try
        {
            using var document = ParseBody(request);
            model = GetString(document.RootElement, "model");
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(model) || Registry.FindModel(model)?.IsLoaded != true)
            return Error("No model is loaded for inference yet. Use POST /api/v1/models/load first.", "model_not_loaded", "model_not_loaded");

        return Error($"{objectName} inference is not implemented in this first model-management milestone.", "not_implemented", "inference_not_implemented");
    }

    private static LlmChatRequest CloneChatRequest(LlmChatRequest request, IReadOnlyList<LlmChatMessage>? messages = null, IReadOnlyList<LlmChatMessage>? prependMessages = null)
    {
        IReadOnlyList<LlmChatMessage> clonedMessages = messages ?? (prependMessages == null ? request.Messages : prependMessages.Concat(request.Messages).ToArray());
        return new LlmChatRequest
        {
            Model = request.Model,
            Messages = clonedMessages,
            Stream = false,
            MaxTokens = request.MaxTokens,
            MaxTokensSpecified = request.MaxTokensSpecified,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Stop = request.Stop,
            User = request.User,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            ProjectPath = request.ProjectPath,
            SessionId = request.SessionId,
            SessionTitle = request.SessionTitle,
            SessionLocked = request.SessionLocked,
            SessionSaved = request.SessionSaved,
            PromptTokenBudget = request.PromptTokenBudget,
            Metadata = request.Metadata
        };
    }

    private static string BuildToolUseInstruction(IReadOnlyList<JsonElement> tools)
    {
        return "You are in a tool-selection step. If a tool is needed, respond with only one valid compact JSON object and no prose, no markdown, no code fence, no thought process, and no explanation. " +
               "The exact shape is {\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"tool_name\",\"arguments\":{}}]}. " +
               "Return every tool call needed for the task in this one JSON object. Use double-quoted JSON strings and fully escape any nested quotes in arguments. " +
               "Never use XML tags such as <tool_call>, <arguments>, or <tool_result>. " +
               "If prior tool results are already in the conversation, answer only from those results or call another available tool to gather missing facts; never invent unavailable details. " +
               "If no tool is needed, answer normally without tool JSON. Available tools: " + BuildCompactToolManifest(tools);
    }

    private static bool HasToolResultMessages(LlmChatRequest request)
    {
        return request.Messages.Any(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldBypassToolSelection(LlmChatRequest request)
    {
        if (request.Tools.Count == 0)
            return true;
        if (HasToolResultMessages(request))
            return false;
        if (ToolChoiceRequestsNoTool(request))
            return true;
        if (ToolChoiceForcesTool(request))
            return false;
        return !PromptLooksLikeToolUse(request);
    }

    private static bool TryBuildDeterministicExactReply(LlmChatRequest request, out LlmChatResult result)
    {
        result = null!;
        if (request == null || ToolChoiceForcesTool(request))
            return false;

        string prompt = request.Messages
            .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content ?? "";
        if (string.IsNullOrWhiteSpace(prompt) ||
            PromptLooksLikeToolUse(request) ||
            !TryExtractExactReplyText(prompt, out string exactText))
            return false;

        if (string.IsNullOrWhiteSpace(exactText))
            return false;

        if (request.MaxTokens > 0 &&
            LlmInferenceMetrics.EstimateTokens(exactText) > request.MaxTokens)
            return false;

        result = new LlmChatResult
        {
            Model = request.Model,
            Content = exactText,
            FinishReason = "stop",
            Metrics = LlmInferenceMetrics.FromText(request, exactText, TimeSpan.Zero)
        };
        return true;
    }

    private static bool TryExtractExactReplyText(string prompt, out string exactText)
    {
        exactText = "";
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        string[] patterns =
        [
            @"\b(?:reply|respond|answer|say)\s+(?:with\s+)?exactly\s*:?\s*(?:""(?<dq>[^""]{1,400})""|'(?<sq>[^']{1,400})'|`(?<bt>[^`]{1,400})`|(?<plain>[^\r\n.!?]{1,200}))",
            @"\b(?:return|output|print)\s+(?:only|exactly)\s*:?\s*(?:""(?<dq>[^""]{1,400})""|'(?<sq>[^']{1,400})'|`(?<bt>[^`]{1,400})`|(?<plain>[^\r\n.!?]{1,200}))"
        ];

        foreach (string pattern in patterns)
        {
            var match = Regex.Match(prompt, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            string value = FirstNonEmptyString(
                match.Groups["dq"].Value,
                match.Groups["sq"].Value,
                match.Groups["bt"].Value,
                match.Groups["plain"].Value);
            value = Regex.Replace(value ?? "", @"\s*/no_think\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                exactText = value;
                return true;
            }
        }

        return false;
    }

    private static void WriteDeterministicChatStream(LlmChatRequest request, LlmChatResult result, ChunkedStream stream)
    {
        stream.ContentType = "text/event-stream";
        string id = NewCompletionId("chatcmpl");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int promptTokens = result.Metrics.PromptTokens > 0
            ? result.Metrics.PromptTokens
            : LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content)));
        int completionTokens = result.Metrics.CompletionTokens > 0
            ? result.Metrics.CompletionTokens
            : LlmInferenceMetrics.EstimateTokens(result.Content);
        var usage = new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens
        };
        var stats = new
        {
            elapsed_seconds = result.Metrics.ElapsedSeconds,
            tokens_per_second = result.Metrics.TokensPerSecond,
            managed_memory_bytes = result.Metrics.ManagedMemoryBytes
        };

        WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", "assistant", null, null)));
        if (!string.IsNullOrEmpty(result.Content))
            WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, result.Content, null, null, null)));
        WriteSseData(stream, ToJson(ToOpenAiChatChunk(id, created, request.Model, "", null, "stop", new { usage, stats, session = ToSessionCompatibilityPayload(request, promptTokens, completionTokens) })));
        WriteSseData(stream, "[DONE]");
    }

    private static bool ToolChoiceRequestsNoTool(LlmChatRequest request)
    {
        if (!request.ToolChoice.HasValue)
            return false;
        JsonElement choice = request.ToolChoice.Value;
        return choice.ValueKind == JsonValueKind.String &&
               string.Equals(choice.GetString(), "none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ToolChoiceForcesTool(LlmChatRequest request)
    {
        if (!request.ToolChoice.HasValue)
            return false;
        JsonElement choice = request.ToolChoice.Value;
        if (choice.ValueKind == JsonValueKind.String)
            return string.Equals(choice.GetString(), "required", StringComparison.OrdinalIgnoreCase);
        return choice.ValueKind == JsonValueKind.Object;
    }

    private static bool PromptLooksLikeToolUse(LlmChatRequest request)
    {
        string text = string.Join(Environment.NewLine, request.Messages
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content))
            .Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(reply|respond|say|answer)\s+(with\s+)?exactly\b", RegexOptions.CultureInvariant))
            return false;
        if (Regex.IsMatch(lower, @"https?://|www\.", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(lower, @"\b(search|browse|internet|web|website|url|download|scrape|scraping|crawl|fetch|find|lookup|look\s+up|research|citation|source|current|latest|today|now|part\s+numbers?|parts?\s+numbers?)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(lower, @"\b(file|folder|directory|path|repo|repository|solution|workspace|read|write|create|edit|modify|replace|rename|delete|list files|open file)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(lower, @"\b(run|execute|start|stop|restart)\s+(a\s+)?(command|script|terminal|powershell|shell|build|test|server|process)\b", RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(lower, @"\b(git|commit|branch|diff|status|stage|push|pull|clone|merge|checkout)\b", RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static string BuildCompactToolManifest(IReadOnlyList<JsonElement> tools)
    {
        const int maxTools = 64;
        const int maxParameters = 32;
        var compactTools = new List<object>();
        foreach (JsonElement tool in tools.Take(maxTools))
        {
            string name = ReadToolName(tool);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            JsonElement source = tool;
            if (tool.ValueKind == JsonValueKind.Object &&
                tool.TryGetProperty("function", out JsonElement function) &&
                function.ValueKind == JsonValueKind.Object)
                source = function;

            string description = GetStringAny(source, "description") ?? "";
            var parameters = new List<object>();
            var required = new List<string>();
            if (source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("parameters", out JsonElement schema) &&
                schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out JsonElement requiredElement) &&
                    requiredElement.ValueKind == JsonValueKind.Array)
                {
                    required.AddRange(requiredElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? "")
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                }

                if (schema.TryGetProperty("properties", out JsonElement properties) &&
                    properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in properties.EnumerateObject().Take(maxParameters))
                    {
                        JsonElement value = property.Value;
                        parameters.Add(new
                        {
                            name = property.Name,
                            type = value.ValueKind == JsonValueKind.Object ? GetStringAny(value, "type") ?? "string" : "string",
                            description = value.ValueKind == JsonValueKind.Object ? TruncateToolManifestText(GetStringAny(value, "description") ?? "", 140) : ""
                        });
                    }
                }
            }

            compactTools.Add(new
            {
                name,
                description = TruncateToolManifestText(description, 240),
                required,
                parameters
            });
        }

        if (tools.Count > maxTools)
            compactTools.Add(new { omitted = tools.Count - maxTools, reason = "tool manifest cap" });

        return JsonSerializer.Serialize(compactTools, JsonOptions);
    }

    private static string TruncateToolManifestText(string text, int maxLength)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (text.Length <= maxLength)
            return text;
        return text.Substring(0, Math.Max(0, maxLength - 1)).TrimEnd() + "...";
    }

    private static string BuildToolRepairInstruction()
    {
        return "The previous assistant message attempted a tool call but was not valid tool JSON. Convert it into only one valid compact JSON object in this exact shape and include nothing else: " +
               "{\"tool_calls\":[{\"id\":\"call_1\",\"name\":\"tool_name\",\"arguments\":{}}]}.";
    }

    private static bool LooksLikeToolCallAttempt(string content, IReadOnlyList<JsonElement> tools)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        string text = content.Trim();
        if (text.Contains("tool_calls", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("toolCalls", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("function_call", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var tool in tools)
        {
            string name = ReadToolName(tool);
            if (!string.IsNullOrWhiteSpace(name) && text.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ReadToolName(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return "";

        string name = GetStringAny(tool, "name", "tool", "tool_name", "toolName") ?? "";
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return TryGetPropertyAny(tool, out var function, "function") && function.ValueKind == JsonValueKind.Object
            ? GetStringAny(function, "name", "tool", "tool_name", "toolName") ?? ""
            : "";
    }

    private static IReadOnlyList<LlmToolCall> ParseToolCallsFromModelText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var xmlStyleCalls = ParseXmlStyleToolCalls(content);
        if (xmlStyleCalls.Count > 0)
            return xmlStyleCalls;

        string json = ExtractJsonObject(content.Trim());
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseToolCalls(document.RootElement).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<LlmToolCall> ParseXmlStyleToolCalls(string content)
    {
        var calls = new List<LlmToolCall>();
        if (string.IsNullOrWhiteSpace(content) ||
            content.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase) < 0)
            return calls;

        foreach (Match match in Regex.Matches(content, @"<tool_call\b(?<attrs>[^>]*)>(?<body>.*?)</tool_call>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = match.Groups["attrs"].Value;
            string body = match.Groups["body"].Value;
            string name = ExtractXmlAttribute(attrs, "name");
            string id = ExtractXmlAttribute(attrs, "id");
            if (string.IsNullOrWhiteSpace(name))
                name = ExtractXmlTagText(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = ExtractNestedToolCallAttribute(body, "name");
            if (string.IsNullOrWhiteSpace(id))
                id = ExtractXmlTagText(body, "id");
            if (string.IsNullOrWhiteSpace(id))
                id = ExtractNestedToolCallAttribute(body, "id");

            string argumentsText = ExtractXmlTagText(body, "arguments");
            if (string.IsNullOrWhiteSpace(argumentsText))
                argumentsText = ExtractJsonObject(body);

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(argumentsText) || !IsValidJson(argumentsText))
                continue;

            using JsonDocument arguments = JsonDocument.Parse(argumentsText);
            calls.Add(new LlmToolCall
            {
                Id = string.IsNullOrWhiteSpace(id) ? "call_" + Guid.NewGuid().ToString("N") : id.Trim(),
                Name = name.Trim(),
                Arguments = arguments.RootElement.Clone()
            });
        }

        return calls;
    }

    private static LlmChatResult SanitizeToolResultFinalAnswer(LlmChatResult result, LlmChatRequest request)
    {
        string cleaned = SanitizeToolTranscriptText(result.Content);
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = BuildToolResultFallbackText(request);

        return new LlmChatResult
        {
            Model = result.Model,
            Content = cleaned,
            FinishReason = string.IsNullOrWhiteSpace(result.FinishReason) || string.Equals(result.FinishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
                ? "stop"
                : result.FinishReason,
            Metrics = result.Metrics,
            ToolCalls = [],
            ToolResults = result.ToolResults
        };
    }

    private static string SanitizeToolTranscriptText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        string text = content;
        int lastToolResultEnd = text.LastIndexOf("</tool_result>", StringComparison.OrdinalIgnoreCase);
        if (lastToolResultEnd >= 0)
        {
            string suffix = text[(lastToolResultEnd + "</tool_result>".Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
                text = suffix;
        }

        for (int i = 0; i < 8; i++)
        {
            string next = Regex.Replace(text, @"<tool_call\b[^>]*>.*?</tool_call>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            next = Regex.Replace(next, @"<tool_result\b[^>]*>.*?</tool_result>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (string.Equals(next, text, StringComparison.Ordinal))
                break;
            text = next;
        }

        text = Regex.Replace(text, @"</?(?:tool_call|tool_result|arguments|argument|result)\b[^>]*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"^\s*```(?:json|xml|tool|text|code)?\s*```", "", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"[ \t]+\r?\n", "\n", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string BuildToolResultFallbackText(LlmChatRequest request)
    {
        string toolResult = request.Messages
            .Where(message => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content)
            .LastOrDefault(content => !string.IsNullOrWhiteSpace(content)) ?? "";

        string cleaned = SanitizeToolTranscriptText(toolResult);
        if (!string.IsNullOrWhiteSpace(cleaned))
            return cleaned.Length > 600 ? cleaned[..600].TrimEnd() + "..." : cleaned;

        return "Tool completed successfully.";
    }

    private static string ExtractNestedToolCallAttribute(string content, string name)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(name))
            return "";

        Match match = Regex.Match(content, @"<tool_call\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? ExtractXmlAttribute(match.Groups["attrs"].Value, name) : "";
    }

    private static string ExtractXmlAttribute(string attrs, string name)
    {
        if (string.IsNullOrWhiteSpace(attrs) || string.IsNullOrWhiteSpace(name))
            return "";
        Match match = Regex.Match(attrs, @"\b" + Regex.Escape(name) + @"\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value) : "";
    }

    private static string ExtractXmlTagText(string content, string tagName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(tagName))
            return "";
        Match match = Regex.Match(content, @"<" + Regex.Escape(tagName) + @"\b[^>]*>(?<value>.*?)</" + Regex.Escape(tagName) + @">", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : "";
    }

    private static string ExtractJsonObject(string content)
    {
        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = content.IndexOf('\n');
            int fence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && fence > firstLine)
                content = content[(firstLine + 1)..fence].Trim();
        }

        if ((content.StartsWith("{", StringComparison.Ordinal) || content.StartsWith("[", StringComparison.Ordinal)) &&
            IsValidJson(content))
            return content;

        for (int i = 0; i < content.Length; i++)
        {
            char current = content[i];
            if (current != '{' && current != '[')
                continue;

            if (TryExtractBalancedJson(content, i, out string candidate) && IsValidJson(candidate))
                return candidate;
        }

        return "";
    }

    private static bool IsValidJson(string content)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractBalancedJson(string content, int start, out string json)
    {
        json = "";
        if (start < 0 || start >= content.Length || content[start] is not ('{' or '['))
            return false;

        var expectedClosers = new Stack<char>();
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < content.Length; i++)
        {
            char current = content[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                    inString = false;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                expectedClosers.Push('}');
                continue;
            }

            if (current == '[')
            {
                expectedClosers.Push(']');
                continue;
            }

            if (current != '}' && current != ']')
                continue;

            if (expectedClosers.Count == 0 || expectedClosers.Pop() != current)
                return false;

            if (expectedClosers.Count == 0)
            {
                json = content[start..(i + 1)];
                return true;
            }
        }

        return false;
    }

    private static string SerializeToolResult(LlmToolCallResult result)
    {
        return JsonSerializer.Serialize(new
        {
            tool_call_id = result.Id,
            name = result.Name,
            success = result.Success,
            content = result.Content,
            error = result.Error,
            output_json = result.OutputJson
        }, JsonOptions);
    }

    private static object? ToSessionCompatibilityPayload(LlmChatRequest request, int promptTokens, int completionTokens)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) &&
            string.IsNullOrWhiteSpace(request.SessionTitle) &&
            !request.PromptTokenBudget.HasValue)
            return null;

        int budget = Math.Max(0, request.PromptTokenBudget ?? 0);
        int percent = budget <= 0 || promptTokens <= 0
            ? 0
            : Math.Max(0, Math.Min(100, (int)Math.Round((double)promptTokens / budget * 100.0d)));

        return new
        {
            schema = "lmvsproxy.chat-session.compat.v1",
            source = "LlmRuntime",
            id = request.SessionId ?? "",
            sessionId = request.SessionId ?? "",
            title = request.SessionTitle ?? "",
            runtime = "LlmRuntime",
            locked = request.SessionLocked ?? false,
            saved = request.SessionSaved ?? false,
            promptTokenCount = promptTokens,
            promptTokenBudget = budget,
            promptTokenPercent = percent,
            completionTokenCount = completionTokens,
            supportedActions = new[] { "save", "checkpoint", "rename", "lock", "unlock", "clone" }
        };
    }

    private static object ToOpenAiChatCompletion(LlmChatResult result, LlmChatRequest? request = null)
    {
        string id = NewCompletionId("chatcmpl");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object message = result.ToolCalls.Count == 0
            ? new
            {
                role = "assistant",
                content = result.Content
            }
            : new
            {
                role = "assistant",
                content = result.Content,
                tool_calls = result.ToolCalls.Select(ToOpenAiToolCall).ToArray()
            };

        return new
        {
            id,
            @object = "chat.completion",
            created,
            model = result.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = result.FinishReason
                }
            },
            usage = new
            {
                prompt_tokens = result.Metrics.PromptTokens,
                completion_tokens = result.Metrics.CompletionTokens,
                total_tokens = result.Metrics.TotalTokens
            },
            stats = new
            {
                elapsed_seconds = result.Metrics.ElapsedSeconds,
                tokens_per_second = result.Metrics.TokensPerSecond,
                managed_memory_bytes = result.Metrics.ManagedMemoryBytes
            },
            session = request == null ? null : ToSessionCompatibilityPayload(request, result.Metrics.PromptTokens, result.Metrics.CompletionTokens),
            runtime = result.ToolResults.Count == 0 ? null : new
            {
                tool_results = result.ToolResults.Select(toolResult => new
                {
                    id = toolResult.Id,
                    name = toolResult.Name,
                    success = toolResult.Success,
                    content = toolResult.Content,
                    error = toolResult.Error,
                    output_json = toolResult.OutputJson
                }).ToArray()
            }
        };
    }

    private static object ToOpenAiToolCall(LlmToolCall call) => new
    {
        id = call.Id,
        type = "function",
        function = new
        {
            name = call.Name,
            arguments = call.Arguments.GetRawText()
        }
    };

    private static object ToOpenAiToolCallChunk(string id, long created, string model, IReadOnlyList<LlmToolCall> toolCalls, string? finishReason, object? extra)
    {
        object delta = toolCalls.Count == 0
            ? new { }
            : new
            {
                tool_calls = toolCalls.Select((call, index) => new
                {
                    index,
                    id = call.Id,
                    type = "function",
                    function = new
                    {
                        name = call.Name,
                        arguments = call.Arguments.GetRawText()
                    }
                }).ToArray()
            };

        return new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta,
                    finish_reason = finishReason
                }
            },
            runtime = extra
        };
    }

    private static object ToOpenAiChatChunk(string id, long created, string model, string content, string? role, string? finishReason, object? extra)
    {
        object delta = role == null
            ? new { content }
            : string.IsNullOrEmpty(content)
                ? new { role }
                : new { role, content };

        return new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta,
                    finish_reason = finishReason
                }
            },
            runtime = extra
        };
    }

    private static string NewCompletionId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

    private static (int StatusCode, string Reason) HttpStatusForError(string code) =>
        code switch
        {
            "model_not_loaded" => (404, "Not Found"),
            "invalid_json" => (400, "Bad Request"),
            "unsupported_model_format" => (400, "Bad Request"),
            "unsupported_model_metadata" => (400, "Bad Request"),
            "unsupported_model_type" => (400, "Bad Request"),
            "missing_model" => (400, "Bad Request"),
            _ => (400, "Bad Request")
        };

    private static void SetStatus(HttpRequest request, (int StatusCode, string Reason) status)
    {
        request.Context.StatusCodeNumber = status.StatusCode;
        request.Context.ReasonPhrase = status.Reason;
    }

    private static object ToOpenAiModel(LlmModelInfo model) => new
    {
        id = model.Key,
        @object = "model",
        created = 0,
        owned_by = model.Publisher
    };

    private object ToNativeModel(LlmModelInfo model)
    {
        LlmModelConfig config = Registry.GetModelConfig(model.Key);
        return new
    {
        type = model.Type,
        publisher = model.Publisher,
        key = model.Key,
        display_name = model.DisplayName,
        architecture = model.Architecture,
        quantization = model.QuantizationName == null ? null : new
        {
            name = model.QuantizationName,
            bits_per_weight = model.BitsPerWeight
        },
        size_bytes = model.SizeBytes,
        params_string = model.ParamsString,
        prompt_pipeline_ready = model.LoadedInstances.Any(instance => instance.PromptPipelineReady),
        promptPipelineReady = model.LoadedInstances.Any(instance => instance.PromptPipelineReady),
        prompt_pipeline_status = GetPromptPipelineStatus(model),
        promptPipelineStatus = GetPromptPipelineStatus(model),
        loaded_instances = model.LoadedInstances.Select(instance => new
        {
            id = instance.Id,
            instance_id = instance.Id,
            model_key = string.IsNullOrWhiteSpace(instance.ModelKey) ? model.Key : instance.ModelKey,
            device_id = instance.DeviceId,
            backend = instance.Backend,
            provider = instance.Backend,
            modalities = instance.Modalities.Count == 0 ? GetNativeModalities(model) : instance.Modalities,
            modality_support = instance.Modalities.Count == 0 ? GetNativeModalities(model) : instance.Modalities,
            queue_count = instance.QueuedJobs,
            queued_jobs = instance.QueuedJobs,
            active_jobs = instance.ActiveJobs,
            concurrency_limit = instance.ConcurrencyLimit,
            health = instance.Health,
            loaded_at = instance.LoadedAtUtc.ToString("O"),
            prompt_pipeline_ready = instance.PromptPipelineReady,
            promptPipelineReady = instance.PromptPipelineReady,
            prompt_pipeline_status = instance.PromptPipelineStatus,
            promptPipelineStatus = instance.PromptPipelineStatus,
            prompt_pipeline_detail = instance.PromptPipelineDetail,
            promptPipelineDetail = instance.PromptPipelineDetail,
            prompt_pipeline_ready_at = instance.PromptPipelineReadyAtUtc?.ToString("O"),
            promptPipelineReadyAt = instance.PromptPipelineReadyAtUtc?.ToString("O"),
            prompt_pipeline_warmup_seconds = instance.PromptPipelineWarmupSeconds,
            promptPipelineWarmupSeconds = instance.PromptPipelineWarmupSeconds,
            config = ToNativeLoadConfig(instance.Config)
        }).ToArray(),
        max_context_length = model.MaxContextLength ?? 0,
        format = model.Format,
        tags = model.Tags,
        file_path = model.FilePath,
        adapter_type = model.AdapterType,
        adapter_requires_base_model = model.AdapterRequiresBaseModel,
        base_model = model.BaseModel,
        base_models = model.BaseModels,
        base_model_available = Registry.IsBaseModelAvailable(model),
        auth = new
        {
            has_huggingface_token = !string.IsNullOrWhiteSpace(config.HuggingFaceToken),
            has_api_key = !string.IsNullOrWhiteSpace(config.ApiKey),
            notes = config.Notes
        },
        load_disabled_reason = LlmModelRegistry.GetRuntimeLoadDisabledReason(model),
        service = LlmModelRegistry.GetRuntimeServiceForModel(model),
        chat_service = LlmModelRegistry.GetRuntimeServiceForModel(model),
        capabilities = new
        {
            chat_completion = LlmModelRegistry.IsChatLoadableModel(model),
            runtime_load = LlmModelRegistry.IsRuntimeLoadableModel(model),
            generation = LlmModelRegistry.IsGenerationModel(model),
            image_generation = string.Equals(LlmModelRegistry.GetRuntimeServiceForModel(model), "image_generation", StringComparison.OrdinalIgnoreCase),
            audio_generation = string.Equals(LlmModelRegistry.GetRuntimeServiceForModel(model), "audio_generation", StringComparison.OrdinalIgnoreCase),
            video_generation = string.Equals(LlmModelRegistry.GetRuntimeServiceForModel(model), "video_generation", StringComparison.OrdinalIgnoreCase),
            chat_service = LlmModelRegistry.GetRuntimeServiceForModel(model),
            vision = model.Type == "vlm",
            trained_for_tool_use = model.Tags.Contains("tool-use", StringComparer.OrdinalIgnoreCase)
        },
        description = LlmModelRegistry.GetRuntimeLoadDisabledReason(model),
        aliases = model.Aliases,
        variants = new[] { model.Key },
        selected_variant = model.Key
    };
    }

    private static string GetPromptPipelineStatus(LlmModelInfo model)
    {
        if (model.LoadedInstances.Count == 0)
            return "unloaded";
        if (model.LoadedInstances.Any(instance => instance.PromptPipelineReady))
            return "ready";
        return model.LoadedInstances
            .Select(instance => instance.PromptPipelineStatus)
            .FirstOrDefault(status => !string.IsNullOrWhiteSpace(status)) ?? "cold";
    }

    private static object ToNativeLoadConfig(LlmLoadConfig config) => new
    {
        model_key = config.ModelKey,
        instance_id = config.InstanceId,
        device_id = config.DeviceId,
        concurrency_limit = config.ConcurrencyLimit,
        backend = ToBackendName(config.Backend),
        allow_backend_fallback = config.AllowBackendFallback,
        context_length = config.ContextLength,
        eval_batch_size = config.EvalBatchSize,
        flash_attention = config.FlashAttention,
        offload_kv_cache_to_gpu = config.OffloadKvCacheToGpu,
        gpu_layer_count = config.GpuLayerCount,
        parallelism_mode = ToParallelismModeName(config.ParallelismMode),
        parallelism_placement = ToParallelismPlacementName(config.ParallelismPlacement),
        target_device_ids = config.TargetDeviceIds,
        network_node_ids = config.NetworkNodeIds,
        gpu_load_threshold_percent = config.MaxGpuLoadPercent,
        max_gpu_load_percent = config.MaxGpuLoadPercent,
        max_vram_usage_percent = config.MaxVramUsagePercent,
        pipeline_stage_count = config.PipelineStageCount,
        data_parallel_replicas = config.DataParallelReplicaCount,
        video_device_map = config.VideoDeviceMap,
        video_allow_cpu_offload = config.VideoAllowCpuOffload,
        video_offload_folder = config.VideoOffloadFolder,
        video_disable_cuda_memory_guard = config.VideoDisableCudaMemoryGuard,
        video_cuda_memory_reserve_mb = config.VideoCudaMemoryReserveMb,
        video_cpu_max_memory = config.VideoCpuMaxMemory,
        video_memory_saving = config.VideoMemorySaving
    };

    private static object ToSchedulerInstanceStatus(LlmSchedulerInstanceStatus status) => new
    {
        instance_id = status.InstanceId,
        model_key = status.ModelKey,
        device_id = status.DeviceId,
        backend = status.Backend,
        provider = status.Backend,
        parallelism_mode = status.ParallelismMode,
        parallelism_placement = status.ParallelismPlacement,
        target_device_ids = status.TargetDeviceIds,
        network_node_ids = status.NetworkNodeIds,
        gpu_load_threshold_percent = status.MaxGpuLoadPercent,
        max_vram_usage_percent = status.MaxVramUsagePercent,
        modalities = status.Modalities,
        modality_support = status.Modalities,
        queue_count = status.QueuedJobs,
        queued_jobs = status.QueuedJobs,
        active_jobs = status.ActiveJobs,
        concurrency_limit = status.ConcurrencyLimit,
        health = status.Health,
        loaded_at = status.LoadedAtUtc.ToString("O")
    };

    private static IReadOnlyList<string> GetNativeModalities(LlmModelInfo model)
    {
        string service = LlmModelRegistry.GetRuntimeServiceForModel(model);
        if (service.Equals("image_generation", StringComparison.OrdinalIgnoreCase))
            return ["image"];
        if (service.Equals("audio_generation", StringComparison.OrdinalIgnoreCase))
            return ["audio"];
        if (service.Equals("video_generation", StringComparison.OrdinalIgnoreCase))
            return ["video"];
        return ["text"];
    }

    private static object ToDownloadJob(ModelDownloadJob job) => new
    {
        job_id = job.JobId,
        status = job.Status,
        model = job.Model,
        file_path = job.FilePath,
        error = job.Error,
        downloaded_bytes = job.DownloadedBytes,
        total_size_bytes = job.TotalSizeBytes,
        sha256 = job.Sha256,
        started_at = job.StartedAt.ToString("O"),
        completed_at = job.CompletedAt?.ToString("O")
    };

    private static object ToConversionJob(ModelConversionJob job) => new
    {
        job_id = job.JobId,
        status = job.Status,
        repository = job.Repository,
        revision = job.Revision,
        source_paths = job.SourcePaths,
        task = job.Task,
        precision = job.Precision,
        output_directory = job.OutputDirectory,
        working_directory = job.WorkingDirectory,
        manifest_path = job.ManifestPath,
        source_bytes = job.SourceBytes,
        error = job.Error,
        percent = job.Percent,
        message = job.Message,
        logs = job.Logs,
        started_at = job.StartedAtUtc.ToString("O"),
        completed_at = job.CompletedAtUtc?.ToString("O")
    };

    private static JsonDocument ParseBody(HttpRequest request)
    {
        string body = string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body;
        return JsonDocument.Parse(body);
    }

    private static JsonDocument? ParseOptionalBody(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return null;
        return JsonDocument.Parse(request.Body);
    }

    private static string? ReadRequestString(HttpRequest request, JsonElement? bodyRoot, params string[] names)
    {
        foreach (string name in names)
        {
            if (request.QueryParameters.TryGetValue(name, out string? queryValue) && !string.IsNullOrWhiteSpace(queryValue))
                return queryValue;
        }

        if (bodyRoot.HasValue && bodyRoot.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in names)
            {
                string? value = GetString(bodyRoot.Value, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string GetQueryString(HttpRequest request, params string[] names)
    {
        foreach (string name in names)
        {
            if (request.QueryParameters.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static int? GetQueryInt32(HttpRequest request, string name)
    {
        if (!request.QueryParameters.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static void AddQueryParameter(List<KeyValuePair<string, string>> parameters, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters.Add(new KeyValuePair<string, string>(key, value.Trim()));
    }

    private static string ReadJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadJsonInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            return parsed;
        return 0;
    }

    private static IReadOnlyList<string> ReadJsonStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildHuggingFaceSearchReason(JsonElement item)
    {
        string pipeline = ReadJsonString(item, "pipeline_tag");
        string library = ReadJsonString(item, "library_name");
        IReadOnlyList<string> tags = ReadJsonStringArray(item, "tags");
        if (tags.Any(tag => tag.Contains("gguf", StringComparison.OrdinalIgnoreCase)))
            return "GGUF model; open and scan the repository for runnable local files.";
        if (library.Equals("diffusers", StringComparison.OrdinalIgnoreCase) || tags.Any(tag => tag.Contains("diffusers", StringComparison.OrdinalIgnoreCase)))
            return "Diffusers model; scan for a CompleteModels bundle.";
        if (!string.IsNullOrWhiteSpace(pipeline))
            return "Popular " + pipeline + " model; scan the card for downloadable files.";
        return "Popular Hugging Face model; scan the repository for downloadable files.";
    }

    private static string TruncateSearchError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        body = body.Trim();
        return body.Length <= 400 ? body : body[..400];
    }

    private RepositoryStorageSnapshot ApplyRepositoryCandidateFit(ModelRepositoryScanResult scan)
    {
        string modelRoot = ResolveRepositoryModelRoot();
        string completeModelRoot = ResolveRepositoryCompleteModelRoot(modelRoot);
        ModelFitSnapshot modelFit = CreateFitSnapshot(modelRoot);
        ModelFitSnapshot completeFit = CreateFitSnapshot(completeModelRoot);

        foreach (ModelFileCandidate candidate in scan.Candidates)
        {
            bool completeLayout = candidate.UsesCompleteModelLayout();
            candidate.ApplyFit(completeLayout ? completeFit : modelFit, completeLayout ? completeModelRoot : modelRoot);
        }

        return new RepositoryStorageSnapshot
        {
            ModelsDirectory = modelRoot,
            CompleteModelsDirectory = completeModelRoot,
            ModelsDriveFreeBytes = modelFit.DriveFreeBytes,
            CompleteModelsDriveFreeBytes = completeFit.DriveFreeBytes
        };
    }

    private string ResolveRepositoryModelRoot()
    {
        string modelsLocation = Environment.GetEnvironmentVariable(ModelsLocationEnvironmentVariable) ?? "";
        string defaultModelRoot = Path.Combine(Environment.CurrentDirectory, "Models");
        string optionModelRoot = _options.ModelRoot;
        string environmentModelRoot = Environment.GetEnvironmentVariable(ModelRootEnvironmentVariable) ?? "";
        if (!string.IsNullOrWhiteSpace(environmentModelRoot) &&
            IsSameDirectory(optionModelRoot, defaultModelRoot))
            optionModelRoot = "";
        string configured = FirstNonEmpty(
            optionModelRoot,
            environmentModelRoot,
            !string.IsNullOrWhiteSpace(modelsLocation) ? Path.Combine(modelsLocation, "Models") : "");
        if (string.IsNullOrWhiteSpace(configured))
            configured = defaultModelRoot;
        return Path.GetFullPath(configured);
    }

    private string ResolveRepositoryCompleteModelRoot(string modelRoot)
    {
        string modelsLocation = Environment.GetEnvironmentVariable(ModelsLocationEnvironmentVariable) ?? "";
        string configured = FirstNonEmpty(
            Environment.GetEnvironmentVariable(CompleteModelRootEnvironmentVariable),
            _options.CompleteModelRoot,
            !string.IsNullOrWhiteSpace(modelsLocation) ? Path.Combine(modelsLocation, "CompleteModels") : "");
        if (string.IsNullOrWhiteSpace(configured))
            return modelRoot;
        return Path.GetFullPath(configured);
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ModelFitSnapshot CreateFitSnapshot(string directory)
    {
        long free = 0;
        try
        {
            Directory.CreateDirectory(directory);
            DriveInfo? drive = TryGetDriveForPath(directory);
            free = drive?.AvailableFreeSpace ?? 0;
        }
        catch
        {
        }

        return new ModelFitSnapshot
        {
            SharedVideoMemoryBytes = EstimateSharedVideoMemoryBytes(),
            DriveFreeBytes = free
        };
    }

    private static DriveInfo? TryGetDriveForPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            fullPath = path;
        }

        DriveInfo? best = null;
        int bestLength = -1;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = "";
            try
            {
                root = Path.GetFullPath(drive.RootDirectory.FullName);
            }
            catch
            {
                root = drive.Name;
            }

            if (!IsPathWithinRoot(fullPath, root))
                continue;

            int length = Path.TrimEndingDirectorySeparator(root).Length;
            if (length > bestLength)
            {
                best = drive;
                bestLength = length;
            }
        }

        if (best != null)
            return best;

        string fallbackRoot = Path.GetPathRoot(fullPath) ?? "";
        return string.IsNullOrWhiteSpace(fallbackRoot) ? null : new DriveInfo(fallbackRoot);
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
            return true;

        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private sealed class RepositoryStorageSnapshot
    {
        public string ModelsDirectory { get; init; } = "";

        public string CompleteModelsDirectory { get; init; } = "";

        public long ModelsDriveFreeBytes { get; init; }

        public long CompleteModelsDriveFreeBytes { get; init; }
    }

    private static long EstimateSharedVideoMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Match match = Regex.Match(line, @"(\d+)");
                    if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb))
                        return kb > 0 ? kb * 1024 / 2 : 0;
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static string? GetStringAny(JsonElement root, params string[] names)
    {
        return TryGetPropertyAny(root, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetPropertyAny(JsonElement root, out JsonElement value, params string[] names)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out value))
                return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            foreach (string name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt32(out int result) ? result : null;
    }

    private static long? GetInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt64(out long result) ? result : null;
    }

    private static uint? GetUInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetUInt32(out uint result) ? result : null;
    }

    private static LlmBackendKind? GetBackend(JsonElement root, string name)
    {
        string? value = GetString(root, name);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => LlmBackendKind.Auto,
            "cpu" => LlmBackendKind.Cpu,
            "cuda" => LlmBackendKind.Cuda12,
            "cuda12" => LlmBackendKind.Cuda12,
            "vulkan" => LlmBackendKind.Vulkan,
            "directml" or "direct_ml" or "direct-ml" => LlmBackendKind.DirectML,
            _ => throw new LlmRuntimeException($"Unsupported backend: {value}", "invalid_request_error", "unsupported_backend")
        };
    }

    private static LlmParallelismMode? GetParallelismMode(JsonElement root)
    {
        string? value = GetString(root, "parallelism_mode") ??
                        GetString(root, "parallelismMode") ??
                        GetString(root, "mode");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase) switch
        {
            "single" or "none" or "off" => LlmParallelismMode.Single,
            "data" or "dataparallel" or "data_parallel" or "data_parallelism" => LlmParallelismMode.DataParallel,
            "offload" or "modeloffload" or "model_offload" or "model_offloading" => LlmParallelismMode.ModelOffload,
            "pipeline" or "pipelineparallel" or "pipeline_parallel" or "pipeline_parallelism" => LlmParallelismMode.PipelineParallel,
            _ => throw new LlmRuntimeException($"Unsupported parallelism mode: {value}", "invalid_request_error", "unsupported_parallelism_mode")
        };
    }

    private static LlmParallelismPlacement? GetParallelismPlacement(JsonElement root)
    {
        string? value = GetString(root, "parallelism_placement") ??
                        GetString(root, "parallelismPlacement") ??
                        GetString(root, "placement") ??
                        GetString(root, "scope");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.OrdinalIgnoreCase) switch
        {
            "local" => LlmParallelismPlacement.Local,
            "network" or "remote" or "distributed" => LlmParallelismPlacement.Network,
            "hybrid" or "localnetwork" or "local_network" => LlmParallelismPlacement.Hybrid,
            _ => throw new LlmRuntimeException($"Unsupported parallelism placement: {value}", "invalid_request_error", "unsupported_parallelism_placement")
        };
    }

    private static IReadOnlyList<string> GetDeviceIds(JsonElement root, string? deviceId)
    {
        var values = GetStringArray(root, "target_device_ids")
            .Concat(GetStringArray(root, "targetDeviceIds"))
            .Concat(GetStringArray(root, "gpu_device_ids"))
            .Concat(GetStringArray(root, "gpuDeviceIds"))
            .Concat(GetStringArray(root, "devices"))
            .ToList();

        if (values.Count == 0 && !string.IsNullOrWhiteSpace(deviceId))
            values.Add(deviceId);

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LlmAgentSandboxProfile? GetSandbox(JsonElement root, string name)
    {
        string? value = GetString(root, name);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Replace("-", "", StringComparison.OrdinalIgnoreCase).Replace("_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "readonly" => LlmAgentSandboxProfile.ReadOnly,
            "workspacewrite" => LlmAgentSandboxProfile.WorkspaceWrite,
            "fullaccess" => LlmAgentSandboxProfile.FullAccess,
            _ => throw new LlmRuntimeException($"Unsupported sandbox: {value}", "invalid_request_error", "unsupported_sandbox")
        };
    }

    private static LlmAgentSessionStatus? GetAgentStatus(JsonElement root, string name)
    {
        string? value = GetString(root, name);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse(value.Replace("-", "", StringComparison.OrdinalIgnoreCase).Replace("_", "", StringComparison.OrdinalIgnoreCase), ignoreCase: true, out LlmAgentSessionStatus status)
            ? status
            : throw new LlmRuntimeException($"Unsupported agent status: {value}", "invalid_request_error", "unsupported_agent_status");
    }

    private static string GetSessionId(JsonElement root)
    {
        string sessionId = GetString(root, "session_id") ?? GetString(root, "sessionId") ?? GetString(root, "id") ?? "";
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new LlmRuntimeException("Agent session id is required.", "invalid_request_error", "missing_session_id");
        return sessionId;
    }

    private static IEnumerable<LlmAgentPlanStep> ParsePlanSteps(JsonElement root)
    {
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.String)
            {
                yield return new LlmAgentPlanStep { Title = step.GetString() ?? "" };
                continue;
            }

            if (step.ValueKind != JsonValueKind.Object)
                continue;

            yield return new LlmAgentPlanStep
            {
                Id = GetString(step, "id") ?? "",
                Title = GetString(step, "title") ?? GetString(step, "step") ?? "",
                Detail = GetString(step, "detail") ?? "",
                Status = GetString(step, "status") ?? "pending"
            };
        }
    }

    private static IEnumerable<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            yield break;

        if (value.ValueKind == JsonValueKind.String)
        {
            string? raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                yield break;

            foreach (string item in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return item;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                yield return item.GetString()!;
        }
    }

    private static IReadOnlyDictionary<string, string> GetStringMap(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                result[property.Name] = property.Value.GetString()!;
            else if (property.Value.ValueKind == JsonValueKind.Number ||
                     property.Value.ValueKind == JsonValueKind.True ||
                     property.Value.ValueKind == JsonValueKind.False)
                result[property.Name] = property.Value.ToString();
        }

        return result;
    }

    private static string? GetWorkspace(JsonElement root) => GetString(root, "workspace_root") ?? GetString(root, "workspaceRoot");

    private static string ToBackendName(LlmBackendKind backend) =>
        backend switch
        {
            LlmBackendKind.Cuda => "cuda12",
            LlmBackendKind.Cuda12 => "cuda12",
            LlmBackendKind.DirectML => "directml",
            _ => backend.ToString().ToLowerInvariant()
        };

    private static string ToParallelismModeName(LlmParallelismMode mode) =>
        mode switch
        {
            LlmParallelismMode.DataParallel => "data_parallel",
            LlmParallelismMode.ModelOffload => "model_offload",
            LlmParallelismMode.PipelineParallel => "pipeline_parallel",
            _ => "single"
        };

    private static string ToParallelismPlacementName(LlmParallelismPlacement placement) =>
        placement switch
        {
            LlmParallelismPlacement.Network => "network",
            LlmParallelismPlacement.Hybrid => "hybrid",
            _ => "local"
        };

    private static string Error(string message, string type, string code) => ToJson(new
    {
        error = new
        {
            message,
            type,
            code
        }
    });

    private static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelStartupModelRestore();
        Registry.ModelLoadProgressChanged -= OnModelLoadProgressChanged;
        Server.StopListening();
        Server.Dispose();
        _conversionService.Dispose();
        _huggingFaceHttpClient.Dispose();
        Registry.Dispose();
        _startupModelLoadCancellation?.Dispose();
    }
}
