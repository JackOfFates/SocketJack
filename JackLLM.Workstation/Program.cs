using System.Globalization;
using JackONNX;
using JackONNX.Cuda;
using JackONNX.DirectML;
using JackONNX.Image;
using JackONNX.LlmRuntime;
using JackONNX.Runtime;
using LmVs;
using LlmRuntime;
using SocketJack.Net;
using SocketJack.Stripe;

namespace JackLLM.Workstation;

internal static class Program
{
    private const int DefaultProxyPort = 11434;
    private const int DefaultModelRuntimePort = 11435;
    private const int DefaultChatServerPort = 11436;
    private const int DefaultLmStudioPort = 1234;

    public static async Task<int> Main(string[] args)
    {
        WorkstationOptions options;
        try
        {
            options = WorkstationOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("JackLLM Workstation option error: " + ex.Message);
            Console.Error.WriteLine();
            WorkstationOptions.WriteHelp(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            WorkstationOptions.WriteHelp(Console.Out);
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            using var host = new WorkstationHost(options);
            await host.StartAsync(shutdown.Token).ConfigureAwait(false);
            Console.WriteLine("Press Ctrl+C to stop JackLLM Workstation.");
            await WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("JackLLM Workstation failed: " + ex.Message);
            if (options.Verbose)
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        var wait = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), wait))
            await wait.Task.ConfigureAwait(false);
    }

    private sealed class WorkstationHost : IDisposable
    {
        private readonly WorkstationOptions _options;
        private readonly LmVsProxy _proxy;
        private readonly HttpLmVsProxyModelRuntime _lmStudioRuntime;
        private readonly JackOnnxRuntimeEngine _jackOnnxRuntime;
        private readonly JackOnnxLlmRuntimeToolOptions _jackOnnxToolOptions = new();
        private readonly IReadOnlyList<LlmToolDefinition> _jackOnnxToolDefinitions;
        private LlmRuntimeModelRuntimeAdapter? _embeddedRuntime;
        private LinuxWorkstationGui? _linuxGui;
        private bool _started;

        public WorkstationHost(WorkstationOptions options)
        {
            _options = options;
            _proxy = new LmVsProxy(options.LmStudioHost, options.LmStudioPort, options.ProxyPort, options.ChatServerPort)
            {
                PromptTimeout = TimeSpan.FromMinutes(options.PromptTimeoutMinutes),
                StoreLocalWebAuthAccounts = options.StoreLocalWebAuthAccounts,
                UseSocketJackMasterAuth = options.UseSocketJackMasterAuth,
                PublicAccessEnabled = options.PublicAccessEnabled,
                WebChatModelLoadApiEnabled = options.WebChatModelLoadApiEnabled,
                ChatModel = options.ChatModel,
                CopilotDuplicatorEnabled = options.CopilotDuplicatorEnabled,
                CopilotDuplicatorPort = options.CopilotDuplicatorPort
            };
            _proxy.UseStripePaymentsFromEnvironment();
            _proxy.OutputLog += OnProxyOutputLog;
            _proxy.FilesystemPermissionRequested += (_, eventArgs) =>
                Log("filesystem permission pending: " + eventArgs.Request?.DirectoryPath + " for " + eventArgs.Request?.OwnerKey);
            _proxy.TerminalPermissionRequested += (_, eventArgs) =>
                Log("terminal permission pending: " + eventArgs.Request?.Command + " for " + eventArgs.Request?.OwnerKey);
            _proxy.WebAuthRegistrationRequested += (_, eventArgs) =>
                Log("web auth registration pending: " + eventArgs.Request?.UserName);
            _proxy.TokenRateRequestRequested += (_, eventArgs) =>
                Log("token-rate request pending: " + eventArgs.Request?.RequestedTokenRate.ToString(CultureInfo.InvariantCulture));

            _lmStudioRuntime = new HttpLmVsProxyModelRuntime("LM Studio", options.LmStudioBaseUrl, () => Task.CompletedTask);
            _jackOnnxRuntime = JackOnnxRuntimeEngine.Create(CreateJackOnnxOptions(), CreateJackOnnxProviders());
            _jackOnnxToolDefinitions = JackOnnxLlmRuntimeToolRegistration.CreateDefinitions(_jackOnnxToolOptions);
            LinuxDesktopRemoteControl.TryRegister(Log);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigureModelRuntime();

            if (_embeddedRuntime != null && _options.StartEmbeddedRuntime)
                await _embeddedRuntime.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            if (!_proxy.Start() && _options.Verbose)
                Log("proxy was already running.");

            if (!_proxy.ChatServer.IsListening && !_proxy.ChatServer.Listen())
                throw new InvalidOperationException("Unable to start JackLLM web console on port " + _options.ChatServerPort.ToString(CultureInfo.InvariantCulture) + ".");

            if (_options.EnableSqlAdmin)
                EnableSqlAdminPanel();

            _linuxGui = LinuxWorkstationGui.TryStart(
                _proxy.ChatServerUrl,
                _proxy.LocalModelRuntime.DisplayName,
                _proxy.LocalModelRuntime.OpenAiBaseUrl,
                _options.ProxyPort,
                _options.ChatServerPort,
                Log);

            _started = true;
            Log("JackLLM Workstation is running.");
            Log("Runtime provider: " + _proxy.LocalModelRuntime.DisplayName + " at " + _proxy.LocalModelRuntime.OpenAiBaseUrl);
            Log("VS Copilot endpoint: http://localhost:" + _options.ProxyPort.ToString(CultureInfo.InvariantCulture) + "/v1/responses");
            Log("OpenAI chat endpoint: http://localhost:" + _options.ProxyPort.ToString(CultureInfo.InvariantCulture) + "/v1/chat/completions");
            Log("Web console: " + _proxy.ChatServerUrl);
        }

        private void EnableSqlAdminPanel()
        {
            ChatClientPermissionSnapshot permissions = _proxy.GetChatClientPermissionsDiagnostics("global");
            if (!permissions.SqlAdmin)
            {
                permissions.SqlAdmin = true;
                _proxy.SaveChatClientPermissionsDiagnostics(permissions);
            }

            _proxy.ChatServer.Map("GET", "/admin", (connection, request, cancellationToken) => RedirectToSqlAdmin(request));
            _proxy.ChatServer.Map("GET", "/admin/", (connection, request, cancellationToken) => RedirectToSqlAdmin(request));
            Log("SQL Admin panel enabled at " + _proxy.ChatServerUrl.TrimEnd('/') + "/sql. /admin redirects there too.");
        }

        private static object RedirectToSqlAdmin(HttpRequest request)
        {
            request.Context.ContentType = "text/html; charset=utf-8";
            request.Context.StatusCode = "302 Found";
            request.Context.Response.Headers["Location"] = "/sql";
            return "<html><body>Redirecting to <a href=\"/sql\">SQL Admin</a>.</body></html>";
        }

        private void ConfigureModelRuntime()
        {
            if (_options.RuntimeProvider == ModelRuntimeProvider.LmStudio)
            {
                _proxy.LocalModelRuntime = _lmStudioRuntime;
                _proxy.EmergencyModelRuntimeFallbackEnabled = false;
                _proxy.EmergencyFallbackModelRuntime = null;
                return;
            }

            var runtimeOptions = new LlmRuntimeOptions
            {
                ModelRoot = _options.ModelRoot,
                CompleteModelRoot = _options.CompleteModelRoot,
                IncludeLmStudioModels = _options.IncludeLmStudioModels,
                ToolRoot = _options.ToolRoot,
                AgentRoot = _options.AgentRoot,
                DefaultWorkspaceRoot = _options.WorkspaceRoot,
                RuntimeConfigPath = Path.Combine(_options.DataRoot, "LlmRuntime.config.json"),
                CompatibilityConfigPath = Path.Combine(_options.DataRoot, "LlmRuntime.compatibility.json"),
                RestoreLoadedModelsOnStartup = _options.RestoreLoadedModelsOnStartup,
                Port = _options.ModelRuntimePort,
                ServerName = "LlmRuntime",
                ControlAuthToken = _options.ControlAuthToken
            };

            _embeddedRuntime = new LlmRuntimeModelRuntimeAdapter(
                runtimeOptions,
                BuildLlmRuntimeToolDefinitions(),
                JackOnnxLlmRuntimeToolRegistration.CreateJackOnnxBuiltInTools(_jackOnnxRuntime, _jackOnnxToolOptions));
            _embeddedRuntime.ServiceLog += message => Log("[LlmRuntime] " + message);

            _proxy.LocalModelRuntime = _embeddedRuntime;
            _proxy.EmergencyModelRuntimeFallbackEnabled = _options.EnableLmStudioFallback;
            _proxy.EmergencyFallbackModelRuntime = _options.EnableLmStudioFallback ? _lmStudioRuntime : null;
            Log("JackONNX tools registered with LlmRuntime: " + _jackOnnxToolDefinitions.Count.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private IReadOnlyList<LlmToolDefinition> BuildLlmRuntimeToolDefinitions()
        {
            return _jackOnnxToolDefinitions;
        }

        private JackOnnxOptions CreateJackOnnxOptions()
        {
            string jackOnnxModelRoot = Path.Combine(_options.ModelRoot, "JackONNX");
            var options = new JackOnnxOptions
            {
                ModelCachePath = jackOnnxModelRoot,
                ArtifactRoot = Path.Combine(_options.DataRoot, "Artifacts", "JackONNX"),
                PreferredProvider = JackOnnxExecutionProvider.Cuda,
                DevicePolicy = JackOnnxDevicePolicy.RequirePreferredProvider,
                ImageGenerationPythonExecutable = JackOnnxPythonDiffusersImageRunner.DefaultPreferredImageGenerationPythonExecutable,
                AllowPythonBootstrap = true,
                AllowPythonPackageInstall = true
            };
            options.ModelManifestPaths.Add(jackOnnxModelRoot);
            options.ModelManifestPaths.Add(_options.CompleteModelRoot);
            return options;
        }

        private static IEnumerable<IJackOnnxExecutionProvider> CreateJackOnnxProviders()
        {
            yield return new CudaExecutionProvider();
            if (OperatingSystem.IsWindows())
                yield return new DirectMlExecutionProvider();
            yield return new OnnxRuntimeCpuExecutionProvider();
        }

        private static void OnProxyOutputLog(object? sender, OutputLogEventArgs eventArgs)
        {
            string message = (eventArgs.Message ?? "").TrimEnd();
            if (!string.IsNullOrWhiteSpace(message))
                Console.WriteLine(message);
        }

        private static void Log(string message)
        {
            Console.WriteLine("[JackLLM.Workstation] " + message);
        }

        public void Dispose()
        {
            if (_started)
                Log("Stopping JackLLM Workstation.");

            _proxy.OutputLog -= OnProxyOutputLog;
            _linuxGui?.Dispose();
            _proxy.Dispose();
            _embeddedRuntime?.Dispose();
        }
    }

    private enum ModelRuntimeProvider
    {
        LlmRuntime,
        LmStudio
    }

    private sealed class WorkstationOptions
    {
        public bool ShowHelp { get; private set; }
        public bool Verbose { get; private set; }
        public ModelRuntimeProvider RuntimeProvider { get; private set; } = ModelRuntimeProvider.LlmRuntime;
        public int ProxyPort { get; private set; } = ReadPort("JACKLLM_PROXY_PORT", DefaultProxyPort);
        public int CopilotDuplicatorPort { get; private set; } = ReadPort("JACKLLM_COPILOT_DUPLICATOR_PORT", LmVsProxy.DefaultCopilotDuplicatorPort);
        public int ChatServerPort { get; private set; } = ReadPort("JACKLLM_CHAT_PORT", DefaultChatServerPort);
        public int ModelRuntimePort { get; private set; } = ReadPort("JACKLLM_MODEL_RUNTIME_PORT", DefaultModelRuntimePort);
        public string LmStudioHost { get; private set; } = "127.0.0.1";
        public int LmStudioPort { get; private set; } = ReadPort("JACKLLM_LMSTUDIO_PORT", DefaultLmStudioPort);
        public string ChatModel { get; private set; } = Environment.GetEnvironmentVariable("JACKLLM_CHAT_MODEL") ?? "lm-studio";
        public bool StartEmbeddedRuntime { get; private set; } = ReadBool("JACKLLM_START_EMBEDDED_RUNTIME", true);
        public bool RestoreLoadedModelsOnStartup { get; private set; } = ReadBool("JACKLLM_RESTORE_LOADED_MODELS_ON_STARTUP", true);
        public bool EnableLmStudioFallback { get; private set; } = ReadBool("JACKLLM_LMSTUDIO_FALLBACK", true);
        public bool IncludeLmStudioModels { get; private set; } = ReadBool("JACKLLM_INCLUDE_LMSTUDIO_MODELS", true);
        public bool WebChatModelLoadApiEnabled { get; private set; } = ReadBool("JACKLLM_WEBCHAT_MODEL_LOAD", false);
        public bool CopilotDuplicatorEnabled { get; private set; } = ReadBool("JACKLLM_COPILOT_DUPLICATOR", true);
        public bool PublicAccessEnabled { get; private set; } = ReadBool("JACKLLM_PUBLIC_ACCESS", true);
        public bool UseSocketJackMasterAuth { get; private set; } = ReadBool("JACKLLM_SOCKETJACK_AUTH", true);
        public bool StoreLocalWebAuthAccounts { get; private set; } = ReadBool("JACKLLM_STORE_LOCAL_WEB_AUTH", false);
        public bool EnableSqlAdmin { get; private set; } = ReadBool("JACKLLM_SQL_ADMIN", false);
        public int PromptTimeoutMinutes { get; private set; } = ReadPort("JACKLLM_PROMPT_TIMEOUT_MINUTES", 30);
        public string ControlAuthToken { get; private set; } = Environment.GetEnvironmentVariable("LLMRUNTIME_CONTROL_TOKEN") ?? "";
        public string DataRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jackllm");
        public string WorkspaceRoot { get; private set; } = Environment.CurrentDirectory;

        public string ModelRoot { get; private set; } = "";
        public string CompleteModelRoot { get; private set; } = "";
        public string ToolRoot { get; private set; } = "";
        public string AgentRoot { get; private set; } = "";

        public string LmStudioBaseUrl => "http://" + LmStudioHost + ":" + LmStudioPort.ToString(CultureInfo.InvariantCulture);

        public static WorkstationOptions Parse(string[] args)
        {
            var options = new WorkstationOptions();
            options.ApplyDerivedDefaults();
            options.ApplyLmStudioUrl(Environment.GetEnvironmentVariable("JACKLLM_LMSTUDIO_URL"), required: false);

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg is "-h" or "--help")
                {
                    options.ShowHelp = true;
                    continue;
                }

                string key;
                string value;
                int equals = arg.IndexOf('=');
                if (equals >= 0)
                {
                    key = arg.Substring(0, equals);
                    value = arg.Substring(equals + 1);
                }
                else
                {
                    key = arg;
                    value = RequiresValue(key) ? NextValue(args, ref i, key) : "true";
                }

                options.Apply(key, value);
            }

            options.Validate();
            return options;
        }

        public static void WriteHelp(TextWriter writer)
        {
            writer.WriteLine("JackLLM Workstation cross-platform host");
            writer.WriteLine();
            writer.WriteLine("Usage:");
            writer.WriteLine("  dotnet run --project ./JackLLM.Workstation -- [options]");
            writer.WriteLine();
            writer.WriteLine("Common options:");
            writer.WriteLine("  --runtime llmruntime|lmstudio       Model provider. Default: llmruntime");
            writer.WriteLine("  --proxy-port 11434                  OpenAI/Copilot proxy port.");
            writer.WriteLine("  --copilot-duplicator-port 11433     VS Copilot duplicator port.");
            writer.WriteLine("  --chat-port 11436                   Browser workstation port.");
            writer.WriteLine("  --runtime-port 11435                Embedded LlmRuntime port.");
            writer.WriteLine("  --lmstudio-url http://127.0.0.1:1234");
            writer.WriteLine("  --models-location PATH              Base folder containing Models and CompleteModels.");
            writer.WriteLine("  --model-root PATH                   GGUF/model root. Default: ./Models");
            writer.WriteLine("  --complete-model-root PATH          CompleteModels root. Default: ./CompleteModels");
            writer.WriteLine("  --workspace-root PATH               Default agent workspace root.");
            writer.WriteLine("  --data-root PATH                    Runtime config root. Default: ~/.jackllm");
            writer.WriteLine("  --chat-model MODEL                  Chat model id. Default: lm-studio");
            writer.WriteLine("  --webchat-model-load true|false     Allow web chat model-load API.");
            writer.WriteLine("  --sql-admin true|false              Enable SQL Admin panel at /sql and /admin.");
            writer.WriteLine("  --no-copilot-duplicator             Disable the extra VS Copilot duplicator.");
            writer.WriteLine("  --no-start-runtime                  Do not eagerly start embedded LlmRuntime.");
            writer.WriteLine("  --no-restore-loaded-models          Do not reload persisted models during backend startup.");
            writer.WriteLine("  --no-lmstudio-fallback              Disable LM Studio emergency fallback.");
            writer.WriteLine("  --verbose                           Print exception details.");
        }

        private void Apply(string key, string value)
        {
            switch (NormalizeKey(key))
            {
                case "runtime":
                case "provider":
                    RuntimeProvider = ParseRuntimeProvider(value);
                    break;
                case "proxy-port":
                    ProxyPort = ParsePort(value, key);
                    break;
                case "copilot-duplicator-port":
                    CopilotDuplicatorPort = ParsePort(value, key);
                    break;
                case "chat-port":
                    ChatServerPort = ParsePort(value, key);
                    break;
                case "runtime-port":
                case "model-runtime-port":
                    ModelRuntimePort = ParsePort(value, key);
                    break;
                case "lmstudio-url":
                case "lm-studio-url":
                    ApplyLmStudioUrl(value, required: true);
                    break;
                case "lmstudio-host":
                case "lm-studio-host":
                    LmStudioHost = RequireText(value, key);
                    break;
                case "lmstudio-port":
                case "lm-studio-port":
                    LmStudioPort = ParsePort(value, key);
                    break;
                case "models-location":
                    ApplyModelsLocation(value, key);
                    break;
                case "model-root":
                    ModelRoot = FullPath(value, key);
                    break;
                case "complete-model-root":
                    CompleteModelRoot = FullPath(value, key);
                    break;
                case "tool-root":
                    ToolRoot = FullPath(value, key);
                    break;
                case "agent-root":
                    AgentRoot = FullPath(value, key);
                    break;
                case "workspace-root":
                    WorkspaceRoot = FullPath(value, key);
                    break;
                case "data-root":
                    DataRoot = FullPath(value, key);
                    break;
                case "chat-model":
                    ChatModel = RequireText(value, key);
                    break;
                case "start-runtime":
                    StartEmbeddedRuntime = ParseBool(value, key);
                    break;
                case "no-start-runtime":
                    StartEmbeddedRuntime = false;
                    break;
                case "restore-loaded-models":
                case "restore-loaded-models-on-startup":
                    RestoreLoadedModelsOnStartup = ParseBool(value, key);
                    break;
                case "no-restore-loaded-models":
                case "no-restore-loaded-models-on-startup":
                    RestoreLoadedModelsOnStartup = false;
                    break;
                case "lmstudio-fallback":
                case "lm-studio-fallback":
                    EnableLmStudioFallback = ParseBool(value, key);
                    break;
                case "no-lmstudio-fallback":
                case "no-lm-studio-fallback":
                    EnableLmStudioFallback = false;
                    break;
                case "include-lmstudio-models":
                case "include-lm-studio-models":
                    IncludeLmStudioModels = ParseBool(value, key);
                    break;
                case "webchat-model-load":
                    WebChatModelLoadApiEnabled = ParseBool(value, key);
                    break;
                case "sql-admin":
                case "enable-sql-admin":
                    EnableSqlAdmin = ParseBool(value, key);
                    break;
                case "no-sql-admin":
                    EnableSqlAdmin = false;
                    break;
                case "copilot-duplicator":
                    CopilotDuplicatorEnabled = ParseBool(value, key);
                    break;
                case "no-copilot-duplicator":
                    CopilotDuplicatorEnabled = false;
                    break;
                case "public-access":
                    PublicAccessEnabled = ParseBool(value, key);
                    break;
                case "socketjack-auth":
                    UseSocketJackMasterAuth = ParseBool(value, key);
                    break;
                case "store-local-web-auth":
                    StoreLocalWebAuthAccounts = ParseBool(value, key);
                    break;
                case "prompt-timeout-minutes":
                    PromptTimeoutMinutes = ParsePositiveInt(value, key);
                    break;
                case "control-token":
                    ControlAuthToken = value ?? "";
                    break;
                case "verbose":
                    Verbose = ParseBool(value, key);
                    break;
                default:
                    throw new ArgumentException("Unknown option '" + key + "'.");
            }
        }

        private void ApplyDerivedDefaults()
        {
            DataRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_DATA_ROOT") ?? DataRoot, "JACKLLM_DATA_ROOT");
            WorkspaceRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_WORKSPACE_ROOT") ?? WorkspaceRoot, "JACKLLM_WORKSPACE_ROOT");
            string contentRoot = Environment.GetEnvironmentVariable("JACKLLM_CONTENT_ROOT") ?? Environment.CurrentDirectory;
            string modelsLocation = Environment.GetEnvironmentVariable("JACKLLM_MODELS_LOCATION") ?? "";
            string defaultModelRoot = string.IsNullOrWhiteSpace(modelsLocation) ? Path.Combine(contentRoot, "Models") : Path.Combine(modelsLocation, "Models");
            string defaultCompleteModelRoot = string.IsNullOrWhiteSpace(modelsLocation) ? Path.Combine(contentRoot, "CompleteModels") : Path.Combine(modelsLocation, "CompleteModels");
            ModelRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_MODEL_ROOT") ?? defaultModelRoot, "JACKLLM_MODEL_ROOT");
            CompleteModelRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_COMPLETE_MODEL_ROOT") ?? defaultCompleteModelRoot, "JACKLLM_COMPLETE_MODEL_ROOT");
            ToolRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_TOOL_ROOT") ?? Path.Combine(contentRoot, "Tools"), "JACKLLM_TOOL_ROOT");
            AgentRoot = FullPath(Environment.GetEnvironmentVariable("JACKLLM_AGENT_ROOT") ?? Path.Combine(contentRoot, "Agents"), "JACKLLM_AGENT_ROOT");

            string provider = Environment.GetEnvironmentVariable("JACKLLM_RUNTIME_PROVIDER") ?? "";
            if (!string.IsNullOrWhiteSpace(provider))
                RuntimeProvider = ParseRuntimeProvider(provider);
        }

        private void ApplyModelsLocation(string? value, string key)
        {
            string root = FullPath(RequireText(value, key), key);
            ModelRoot = FullPath(Path.Combine(root, "Models"), key + "/Models");
            CompleteModelRoot = FullPath(Path.Combine(root, "CompleteModels"), key + "/CompleteModels");
        }

        private void ApplyLmStudioUrl(string? value, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                    throw new ArgumentException("LM Studio URL is required.");
                return;
            }

            string candidate = value.Trim();
            if (!candidate.Contains("://", StringComparison.Ordinal))
                candidate = "http://" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("Invalid LM Studio URL '" + value + "'.");

            LmStudioHost = uri.Host;
            LmStudioPort = uri.Port > 0 ? uri.Port : DefaultLmStudioPort;
        }

        private void Validate()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(ModelRoot);
            Directory.CreateDirectory(CompleteModelRoot);
            Directory.CreateDirectory(ToolRoot);
            Directory.CreateDirectory(AgentRoot);
            Directory.CreateDirectory(WorkspaceRoot);

            if (PromptTimeoutMinutes <= 0)
                throw new ArgumentException("Prompt timeout must be greater than zero.");
            if (RuntimeProvider == ModelRuntimeProvider.LlmRuntime && ModelRuntimePort == ProxyPort)
                throw new ArgumentException("Runtime port and proxy port must be different.");
            if (ChatServerPort == ProxyPort || ChatServerPort == ModelRuntimePort || CopilotDuplicatorPort == ProxyPort || CopilotDuplicatorPort == ChatServerPort || CopilotDuplicatorPort == ModelRuntimePort)
                throw new ArgumentException("Chat, proxy, duplicator, and runtime ports must be different.");
        }

        private static bool RequiresValue(string key)
        {
            return NormalizeKey(key) is not ("help" or "verbose" or "enable-sql-admin" or "no-sql-admin" or "no-start-runtime" or "no-lmstudio-fallback" or "no-lm-studio-fallback" or "no-copilot-duplicator");
        }

        private static string NextValue(string[] args, ref int index, string key)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException("Option '" + key + "' requires a value.");
            index++;
            return args[index];
        }

        private static string NormalizeKey(string key)
        {
            string normalized = (key ?? "").Trim();
            while (normalized.StartsWith("-", StringComparison.Ordinal))
                normalized = normalized.Substring(1);
            return normalized.Trim().ToLowerInvariant();
        }

        private static ModelRuntimeProvider ParseRuntimeProvider(string value)
        {
            string normalized = RequireText(value, "--runtime").Trim().ToLowerInvariant();
            return normalized switch
            {
                "llmruntime" or "llm-runtime" or "embedded" => ModelRuntimeProvider.LlmRuntime,
                "lmstudio" or "lm-studio" => ModelRuntimeProvider.LmStudio,
                _ => throw new ArgumentException("Runtime provider must be 'llmruntime' or 'lmstudio'.")
            };
        }

        private static string FullPath(string value, string key)
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(RequireText(value, key)));
        }

        private static string RequireText(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Option '" + key + "' requires a non-empty value.");
            return value.Trim();
        }

        private static int ReadPort(string environmentVariable, int fallback)
        {
            string? value = Environment.GetEnvironmentVariable(environmentVariable);
            return string.IsNullOrWhiteSpace(value) ? fallback : ParsePort(value, environmentVariable);
        }

        private static bool ReadBool(string environmentVariable, bool fallback)
        {
            string? value = Environment.GetEnvironmentVariable(environmentVariable);
            return string.IsNullOrWhiteSpace(value) ? fallback : ParseBool(value, environmentVariable);
        }

        private static int ParsePort(string value, string key)
        {
            int port = ParsePositiveInt(value, key);
            if (port > 65535)
                throw new ArgumentException("Option '" + key + "' must be between 1 and 65535.");
            return port;
        }

        private static int ParsePositiveInt(string value, string key)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number <= 0)
                throw new ArgumentException("Option '" + key + "' must be a positive integer.");
            return number;
        }

        private static bool ParseBool(string value, string key)
        {
            if (bool.TryParse(value, out bool result))
                return result;
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            return normalized switch
            {
                "1" or "yes" or "y" or "on" => true,
                "0" or "no" or "n" or "off" => false,
                _ => throw new ArgumentException("Option '" + key + "' must be true or false.")
            };
        }
    }
}
