using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SocketJack.CopilotMcpBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (SocketJackVisualStudioAuthCallback.TryHandle(args, out int callbackExitCode))
        {
            return callbackExitCode;
        }

        CopilotBridgeOptions options;
        try
        {
            options = CopilotBridgeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.CopilotMcpBridge option error: " + ex.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Error.WriteLine(CopilotBridgeOptions.HelpText);
            return 0;
        }

        try
        {
            return options.Transport == CopilotBridgeTransport.HttpProxy
                ? await RunHttpProxyAsync(options).ConfigureAwait(false)
                : await RunStdioAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.CopilotMcpBridge fatal error: " + SocketJackSecretRedactor.Redact(ex.ToString()));
            return 1;
        }
    }

    private static async Task<int> RunStdioAsync(CopilotBridgeOptions options)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SocketJackCopilotTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunHttpProxyAsync(CopilotBridgeOptions options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.ListenPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackRequest(context) || !IsAllowedHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("SocketJack Copilot local proxy accepts loopback requests only.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/socketjack-proxy-health", (CopilotBridgeOptions bridgeOptions) => Results.Json(new
        {
            ok = true,
            server = "SocketJack.CopilotMcpBridge",
            mode = "http-proxy",
            selectedServer = bridgeOptions.ServerId,
            selectedModel = bridgeOptions.ModelId,
            endpoint = SocketJackSecretRedactor.Redact(bridgeOptions.ServerEndpoint.ToString()),
            webSocket = SocketJackSecretRedactor.Redact(SocketJackWebChatApiClient.BuildWebSocketUri(bridgeOptions.ServerEndpoint).ToString())
        }));

        app.Map("/{**path}", async (HttpContext context, SocketJackModelProxyForwarder forwarder, CancellationToken cancellationToken) =>
        {
            await forwarder.ForwardAsync(context, cancellationToken).ConfigureAwait(false);
        });

        Console.Error.WriteLine("SocketJack.CopilotMcpBridge local proxy listening on http://127.0.0.1:" +
            options.ListenPort.ToString(CultureInfo.InvariantCulture));
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureCommonServices(IServiceCollection services, ILoggingBuilder logging, CopilotBridgeOptions options)
    {
        logging.ClearProviders();
        logging.AddConsole(console =>
        {
            console.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
        services.AddSingleton(options);
        services.AddSingleton<SocketJackWebChatApiClient>();
        services.AddSingleton<SocketJackModelProxyForwarder>();
        services.AddHttpClient<SocketJackCopilotGateway>(ConfigureHttpClient);
        services.AddHttpClient<SocketJackModelProxyForwarder>(ConfigureHttpClient);

        void ConfigureHttpClient(IServiceProvider provider, HttpClient client)
        {
            CopilotBridgeOptions bridgeOptions = provider.GetRequiredService<CopilotBridgeOptions>();
            client.BaseAddress = bridgeOptions.ServerEndpoint;
            client.Timeout = TimeSpan.FromSeconds(bridgeOptions.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
            bridgeOptions.ApplyAuthHeaders(client.DefaultRequestHeaders);
        }
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote == null)
            return false;
        return IPAddress.IsLoopback(remote) ||
            (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()));
    }

    private static bool IsAllowedHost(string? host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SocketJackVisualStudioAuthCallback
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], "--vs-auth-callback", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("--vs-auth-callback requires a socketjack:// callback URL.");
            }

            Handle(args[1]);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack Visual Studio auth callback failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            exitCode = 1;
        }

        return true;
    }

    private static void Handle(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("socketjack", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("visualstudio-auth", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Callback URL is not a SocketJack Visual Studio auth callback.");
        }

        string state = GetUriValue(uri, "socketjack_vs_state");
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("Callback URL is missing socketjack_vs_state.");
        }

        string token = FirstNonEmpty(
            GetUriValue(uri, "socketjack_auth"),
            GetUriValue(uri, "access_token"),
            GetUriValue(uri, "authToken"),
            GetUriValue(uri, "token"));
        string error = FirstNonEmpty(
            GetUriValue(uri, "error"),
            GetUriValue(uri, "error_description"));
        string userName = FirstNonEmpty(
            GetUriValue(uri, "username"),
            GetUriValue(uri, "userName"),
            GetUriValue(uri, "loginName"),
            GetUriValue(uri, "user"));

        Directory.CreateDirectory(CallbackDirectory());
        string path = CallbackPath(state);
        var payload = new JsonObject
        {
            ["state"] = state,
            ["accessToken"] = token,
            ["userName"] = userName,
            ["error"] = error,
            ["savedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        File.WriteAllText(path, payload.ToJsonString(), new UTF8Encoding(false));
    }

    private static string CallbackPath(string state)
    {
        return Path.Combine(CallbackDirectory(), SanitizeState(state) + ".json");
    }

    private static string CallbackDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SocketJack",
            "VisualStudio2026",
            "BrowserLogin");
    }

    private static string SanitizeState(string state)
    {
        var builder = new StringBuilder();
        foreach (char ch in state ?? "")
        {
            if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.Length == 0 ? "missing" : builder.ToString();
    }

    private static string GetUriValue(Uri uri, string name)
    {
        string value = GetQueryValue(uri.Query, name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string fragment = uri.Fragment;
        if (fragment.StartsWith("#", StringComparison.Ordinal))
        {
            fragment = fragment.Substring(1);
        }

        return GetQueryValue(fragment, name);
    }

    private static string GetQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair.Substring(0, equals);
            if (!string.Equals(Uri.UnescapeDataString(key.Replace("+", " ", StringComparison.Ordinal)), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = equals < 0 ? "" : pair.Substring(equals + 1);
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}

public enum CopilotBridgeTransport
{
    Stdio,
    HttpProxy
}

public sealed class CopilotBridgeOptions
{
    public CopilotBridgeTransport Transport { get; private init; } = CopilotBridgeTransport.Stdio;
    public Uri ServerEndpoint { get; private init; } = new("https://socketjack.com/proxy/TitanX/");
    public Uri LocalWebChatEndpoint { get; private init; } = new("http://127.0.0.1:11436/");
    public bool PreferLocalWebChat { get; private init; } = true;
    public string ServerId { get; private init; } = "TitanX";
    public string ServerName { get; private init; } = "TitanX";
    public string ModelId { get; private init; } = "";
    public string AuthToken { get; private init; } = "";
    public string AuthUserName { get; private init; } = "";
    public int ListenPort { get; private init; } = 11574;
    public int TimeoutSeconds { get; private init; } = 60;
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static string HelpText =>
        """
        SocketJack.CopilotMcpBridge

        Transports:
          --stdio                  Run MCP over stdin/stdout. This is the default.
          --http-proxy             Run a loopback HTTP model proxy at http://127.0.0.1:<port>.

        Options:
          --server-endpoint <url>  SocketJack server endpoint, for example https://socketjack.com/proxy/TitanX.
          --local-webchat-endpoint <url>
                                    Local JackLLM web-chat endpoint for fast direct streams. Default: http://127.0.0.1:11436.
          --disable-local-webchat  Do not prefer the local JackLLM endpoint before the configured SocketJack endpoint.
          --server-id <id>         MasterList server id.
          --server-name <name>     Human-readable server name.
          --model <id>             Selected model id.
          --auth-token <token>     Optional token; redacted from logs.
          --auth-user <username>   Optional remembered SocketJack.com user for token refresh.
          --listen-port <number>   Loopback HTTP proxy port. Default: 11574.
          --timeout <seconds>      HTTP/WebSocket request timeout. Default: 60.
          --verbose                Enable debug logging to stderr.
          --help                   Show this help.
        """;

    public static CopilotBridgeOptions Parse(string[] args)
    {
        CopilotBridgeTransport transport = CopilotBridgeTransport.Stdio;
        Uri endpoint = ReadEndpoint(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ENDPOINT") ?? "https://socketjack.com/proxy/TitanX");
        Uri localWebChatEndpoint = ReadEndpoint(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_LOCAL_WEBCHAT_ENDPOINT") ?? "http://127.0.0.1:11436");
        bool preferLocalWebChat = !string.Equals(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_DISABLE_LOCAL_WEBCHAT"), "1", StringComparison.OrdinalIgnoreCase);
        string serverId = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ID") ?? "TitanX";
        string serverName = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_NAME") ?? serverId;
        string model = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_MODEL_ID") ?? "";
        string authToken = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_TOKEN") ?? "";
        string authUserName = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_USER") ?? "";
        int listenPort = ReadPort(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_PROXY_PORT"), 11574, "SOCKETJACK_COPILOT_PROXY_PORT");
        int timeout = ReadPositiveInt(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_TIMEOUT_SECONDS"), 60, "SOCKETJACK_COPILOT_TIMEOUT_SECONDS");
        bool verbose = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--stdio":
                    transport = CopilotBridgeTransport.Stdio;
                    break;
                case "--http-proxy":
                    transport = CopilotBridgeTransport.HttpProxy;
                    break;
                case "--server-endpoint":
                    endpoint = ReadEndpoint(RequireValue(args, ref i, arg));
                    break;
                case "--local-webchat-endpoint":
                    localWebChatEndpoint = ReadEndpoint(RequireValue(args, ref i, arg));
                    break;
                case "--disable-local-webchat":
                    preferLocalWebChat = false;
                    break;
                case "--server-id":
                    serverId = RequireValue(args, ref i, arg);
                    break;
                case "--server-name":
                    serverName = RequireValue(args, ref i, arg);
                    break;
                case "--model":
                case "--model-id":
                    model = RequireValue(args, ref i, arg);
                    break;
                case "--auth-token":
                    authToken = RequireValue(args, ref i, arg);
                    break;
                case "--auth-user":
                case "--auth-username":
                    authUserName = RequireValue(args, ref i, arg);
                    break;
                case "--listen-port":
                case "--port":
                    listenPort = ReadPort(RequireValue(args, ref i, arg), listenPort, arg);
                    break;
                case "--timeout":
                    timeout = ReadPositiveInt(RequireValue(args, ref i, arg), timeout, arg);
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + arg);
            }
        }

        if (string.IsNullOrWhiteSpace(model) && !help)
            throw new ArgumentException("--model is required.");

        return new CopilotBridgeOptions
        {
            Transport = transport,
            ServerEndpoint = endpoint,
            LocalWebChatEndpoint = localWebChatEndpoint,
            PreferLocalWebChat = preferLocalWebChat,
            ServerId = string.IsNullOrWhiteSpace(serverId) ? endpoint.Host : serverId.Trim(),
            ServerName = string.IsNullOrWhiteSpace(serverName) ? serverId : serverName.Trim(),
            ModelId = model.Trim(),
            AuthToken = authToken,
            AuthUserName = authUserName.Trim(),
            ListenPort = listenPort,
            TimeoutSeconds = timeout,
            Verbose = verbose,
            ShowHelp = help
        };
    }

    public void ApplyAuthHeaders(HttpRequestHeaders headers)
    {
        if (!string.IsNullOrWhiteSpace(this.AuthToken))
        {
            string token = this.AuthToken.Trim();
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            headers.TryAddWithoutValidation("X-SocketJack-Auth", token);
        }

        if (!string.IsNullOrWhiteSpace(this.AuthUserName))
        {
            string userName = this.AuthUserName.Trim();
            headers.TryAddWithoutValidation("X-SocketJack-User", userName);
            headers.TryAddWithoutValidation("X-SocketJack-Username", userName);
        }
    }

    public void ApplyAuthHeaders(ClientWebSocketOptions options)
    {
        if (!string.IsNullOrWhiteSpace(this.AuthToken))
        {
            string token = this.AuthToken.Trim();
            options.SetRequestHeader("Authorization", "Bearer " + token);
            options.SetRequestHeader("X-SocketJack-Auth", token);
        }

        if (!string.IsNullOrWhiteSpace(this.AuthUserName))
        {
            string userName = this.AuthUserName.Trim();
            options.SetRequestHeader("X-SocketJack-User", userName);
            options.SetRequestHeader("X-SocketJack-Username", userName);
        }
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException(option + " requires a value.");
        index++;
        return args[index];
    }

    private static Uri ReadEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Server endpoint is required.");
        string normalized = value.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        if (!Uri.TryCreate(normalized.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("Server endpoint is not a valid absolute URI.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Server endpoint must be http or https.");
        return uri;
    }

    private static int ReadPort(string? value, int fallback, string name)
    {
        int port = ReadPositiveInt(value, fallback, name);
        if (port < 1 || port > 65535)
            throw new ArgumentException(name + " must be between 1 and 65535.");
        return port;
    }

    private static int ReadPositiveInt(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException(name + " must be a positive integer.");
        return parsed;
    }
}

[McpServerToolType]
public sealed class SocketJackCopilotTools
{
    [McpServerTool(
        Name = "socketjack_copilot_server_status",
        Title = "SocketJack Copilot Server Status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets the selected SocketJack server status for the Visual Studio Copilot bridge.")]
    public Task<string> GetServerStatusAsync(SocketJackCopilotGateway gateway, CancellationToken cancellationToken)
    {
        return gateway.GetServerStatusAsync(cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_copilot_get_models",
        Title = "SocketJack Copilot Models",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets the selected SocketJack server model metadata.")]
    public Task<string> GetModelsAsync(SocketJackCopilotGateway gateway, CancellationToken cancellationToken)
    {
        return gateway.GetModelsAsync(cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_copilot_get_path",
        Title = "SocketJack Copilot Safe API GET",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets a safe selected-server API path. The path must be relative and start with /api/ or /v1/, or be exactly /health.")]
    public Task<string> GetPathAsync(
        SocketJackCopilotGateway gateway,
        [Description("Relative API path such as /api/models, /api/model-runtime/models, /v1/models, or /health. Absolute URLs are rejected.")] string path,
        CancellationToken cancellationToken)
    {
        return gateway.GetPathAsync(path, cancellationToken);
    }
}

public static class CopilotBridgeToolCatalog
{
    public static readonly string[] ToolNames =
    {
        "socketjack_copilot_server_status",
        "socketjack_copilot_get_models",
        "socketjack_copilot_get_path"
    };
}

public sealed class SocketJackCopilotGateway
{
    private readonly HttpClient _httpClient;
    private readonly CopilotBridgeOptions _options;

    public SocketJackCopilotGateway(HttpClient httpClient, CopilotBridgeOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<string> GetServerStatusAsync(CancellationToken cancellationToken)
    {
        JsonObject status = new()
        {
            ["serverId"] = _options.ServerId,
            ["serverName"] = _options.ServerName,
            ["modelId"] = _options.ModelId,
            ["endpoint"] = SocketJackSecretRedactor.Redact(_options.ServerEndpoint.ToString())
        };

        string health = await TryGetStringAsync("/health", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(health))
            health = await TryGetStringAsync("/api/health", cancellationToken).ConfigureAwait(false);
        status["health"] = string.IsNullOrWhiteSpace(health) ? "No health endpoint responded." : SocketJackSecretRedactor.Redact(health);
        return status.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GetModelsAsync(CancellationToken cancellationToken)
    {
        string models = await TryGetStringAsync("/api/models", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(models))
            models = await TryGetStringAsync("/api/model-runtime/models", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(models) ? "{\"models\":[]}" : SocketJackSecretRedactor.Redact(models);
    }

    public async Task<string> GetPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!SocketJackSafePath.IsSafeGetPath(path))
            throw new ArgumentException("Path must be relative and start with /api/ or /v1/, or be exactly /health.");

        string result = await TryGetStringAsync(path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? "{}" : SocketJackSecretRedactor.Redact(result);
    }

    private async Task<string> TryGetStringAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(path.TrimStart('/'), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return "";
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }
}

public sealed class SocketJackModelProxyForwarder
{
    private enum OpenAiProxyResponseShape
    {
        ChatCompletions,
        Responses
    }

    private static readonly TimeSpan DirectChatStreamHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DirectChatStreamIdleTimeout = ReadPositiveEnvironmentTimeSpan(
        "SOCKETJACK_COPILOT_BRIDGE_STREAM_IDLE_TIMEOUT_SECONDS",
        TimeSpan.FromMinutes(3));
    private static readonly TimeSpan DirectChatStreamMaxDuration = ReadPositiveEnvironmentTimeSpan(
        "SOCKETJACK_COPILOT_BRIDGE_STREAM_MAX_SECONDS",
        TimeSpan.FromMinutes(20));

    private readonly HttpClient _httpClient;
    private readonly SocketJackWebChatApiClient _webChatClient;
    private readonly CopilotBridgeOptions _options;

    public SocketJackModelProxyForwarder(HttpClient httpClient, SocketJackWebChatApiClient webChatClient, CopilotBridgeOptions options)
    {
        _httpClient = httpClient;
        _webChatClient = webChatClient;
        _options = options;
    }

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        if (!SocketJackSafePath.IsSafeProxyPath(path))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Unsupported proxy path.", cancellationToken).ConfigureAwait(false);
            return;
        }

        string upstreamPath = SocketJackProxyPath.NormalizeForUpstream(path);
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            SocketJackProxyPath.IsOpenAiModelsPath(upstreamPath))
        {
            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildModelsResponse(_options.ModelId), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            SocketJackProxyPath.IsOllamaTagsPath(upstreamPath))
        {
            await WriteProxyResponseAsync(context, SocketJackOllamaChatAdapter.BuildTagsResponse(_options.ModelId), cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] body = await ReadRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (SocketJackProxyPath.IsOpenAiChatCompletionsPath(upstreamPath) ||
            SocketJackProxyPath.IsOpenAiResponsesPath(upstreamPath))
        {
            OpenAiProxyResponseShape shape = SocketJackProxyPath.IsOpenAiResponsesPath(upstreamPath)
                ? OpenAiProxyResponseShape.Responses
                : OpenAiProxyResponseShape.ChatCompletions;

            if (await TryStreamOpenAiChatViaWebChatAsync(context, body, shape, cancellationToken).ConfigureAwait(false))
                return;

            ProxyResponse adapted = await ForwardOpenAiChatViaWebChatAsync(body, shape, cancellationToken).ConfigureAwait(false);
            await WriteProxyResponseAsync(context, adapted, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProxyResponse? direct = await TryForwardDirectAsync(context.Request, upstreamPath, body, cancellationToken).ConfigureAwait(false);
        if (direct != null && !ShouldFallback(direct.StatusCode))
        {
            await WriteProxyResponseAsync(context, direct, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProxyResponse viaSocket = await _webChatClient.ForwardAsync(context.Request, upstreamPath, body, cancellationToken).ConfigureAwait(false);
        await WriteProxyResponseAsync(context, viaSocket, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryStreamOpenAiChatViaWebChatAsync(HttpContext context, byte[] openAiBody, OpenAiProxyResponseShape shape, CancellationToken cancellationToken)
    {
        JsonObject openAiRequest;
        try
        {
            openAiRequest = JsonNode.Parse(openAiBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status400BadRequest, "Invalid OpenAI chat request JSON: " + ex.Message), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest))
            return false;

        JsonObject chatStreamRequest = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(openAiRequest, _options.ModelId);
        string canonicalModel = await ResolveCanonicalModelIdAsync(ChooseSelectedServerModel(chatStreamRequest["model"]?.ToString()), cancellationToken).ConfigureAwait(false);
        chatStreamRequest["model"] = canonicalModel;
        string completionId = shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.CreateResponseId()
            : SocketJackOpenAiChatAdapter.CreateCompletionId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string model = canonicalModel;

        if (shape == OpenAiProxyResponseShape.ChatCompletions)
        {
            JsonObject directOpenAiRequest = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(openAiRequest, canonicalModel);
            byte[] directOpenAiBody = Encoding.UTF8.GetBytes(directOpenAiRequest.ToJsonString());
            if (await TryStreamOpenAiChatViaDirectOpenAiAsync(context, directOpenAiBody, completionId, created, model, cancellationToken).ConfigureAwait(false))
                return true;
            if (await TryStreamOpenAiChatViaWebSocketOpenAiAsync(context, directOpenAiBody, cancellationToken).ConfigureAwait(false))
                return true;
        }

        await TryUploadVisualStudioReferenceFilesAsync(chatStreamRequest, cancellationToken).ConfigureAwait(false);
        byte[] chatStreamBody = Encoding.UTF8.GetBytes(chatStreamRequest.ToJsonString());
        string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        if (await TryStreamOpenAiChatViaDirectChatStreamAsync(context, chatStreamBody, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
            return true;

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _options.ApplyAuthHeaders(socket.Options);

        try
        {
            await socket.ConnectAsync(SocketJackWebChatApiClient.BuildWebSocketUri(_options.ServerEndpoint), cancellationToken).ConfigureAwait(false);
            JsonObject envelope = BuildWebChatEnvelope(requestId, "/api/chat-stream", chatStreamBody);
            byte[] payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            bool responseStarted = false;
            bool roleSent = false;
            bool wroteContent = false;
            var assistantText = new SocketJackAssistantTextAccumulator();
            string lineBuffer = "";
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    byte[] messageBytes = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        JsonObject? textMessage = ParseJsonObject(Encoding.UTF8.GetString(messageBytes));
                        if (textMessage == null)
                            continue;

                        string type = textMessage["type"]?.ToString() ?? "";
                        string messageId = textMessage["id"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(messageId) && !string.Equals(messageId, requestId, StringComparison.Ordinal))
                            continue;

                        if (type.Equals("response-start", StringComparison.OrdinalIgnoreCase))
                        {
                            int statusCode = ReadInt(textMessage, "statusCode") ?? StatusCodes.Status200OK;
                            if (statusCode < 200 || statusCode >= 300)
                            {
                                await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "The selected model server returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "."), cancellationToken).ConfigureAwait(false);
                                return true;
                            }

                            StartOpenAiSseResponse(context);
                            responseStarted = true;
                            await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                            roleSent = true;
                            continue;
                        }

                        if (type.Equals("response-end", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(lineBuffer))
                            {
                                ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lineBuffer, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                                wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                                lineBuffer = "";
                            }

                            if (!wroteContent)
                                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, BuildNoVisibleAssistantTextFallback()), cancellationToken).ConfigureAwait(false);

                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                        {
                            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "Model WebSocket proxy error: " + (textMessage["error"]?.ToString() ?? "unknown error")), cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        continue;
                    }

                    if (!responseStarted)
                    {
                        StartOpenAiSseResponse(context);
                        responseStarted = true;
                    }

                    if (!roleSent)
                    {
                        await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                        roleSent = true;
                    }

                    ChatStreamBinaryWriteResult binaryResult = await WriteChatStreamBinaryAsOpenAiSseAsync(context, requestId, messageBytes, lineBuffer, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                    lineBuffer = binaryResult.LineBuffer;
                    wroteContent = wroteContent || binaryResult.WroteContent;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!context.Response.HasStarted)
                await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "Model streaming adapter failed: " + ex.Message), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private async Task<bool> TryStreamOpenAiChatViaDirectChatStreamAsync(HttpContext context, byte[] chatStreamBody, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        (string sessionId, string streamId) = ReadChatStreamRequestIds(chatStreamBody);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        DateTimeOffset lastUpstreamDataUtc = startedUtc;
        bool wroteContent = false;
        var assistantText = new SocketJackAssistantTextAccumulator();

        using var client = new HttpClient
        {
            BaseAddress = _options.ServerEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
        _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-stream"));
            request.Headers.Accept.ParseAdd("application/x-ndjson");
            request.Content = new ByteArrayContent(chatStreamBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var upstreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken upstreamToken = upstreamCancellation.Token;

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadHttpResponsePreviewAsync(response, cancellationToken).ConfigureAwait(false);
                StartOpenAiSseResponse(context);
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(
                    shape,
                    completionId,
                    created,
                    model,
                    "The selected model server returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail)), cancellationToken).ConfigureAwait(false);
                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                return true;
            }

            StartOpenAiSseResponse(context);
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(shape, completionId, created, model), cancellationToken).ConfigureAwait(false);

            await using Stream stream = await response.Content.ReadAsStreamAsync(upstreamToken).ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            string lineBuffer = "";
            var captured = new StringBuilder();
            try
            {
                Task<int>? pendingRead = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    pendingRead ??= stream.ReadAsync(buffer.AsMemory(0, buffer.Length), upstreamToken).AsTask();
                    Task completed = await Task.WhenAny(pendingRead, Task.Delay(DirectChatStreamHeartbeatInterval, cancellationToken)).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, pendingRead))
                    {
                        string? timeoutMessage = GetDirectChatStreamTimeoutMessage(startedUtc, lastUpstreamDataUtc);
                        if (!string.IsNullOrWhiteSpace(timeoutMessage))
                        {
                            bool stopped = await TryStopRemoteChatStreamAsync(streamId, sessionId, timeoutMessage!, CancellationToken.None).ConfigureAwait(false);
                            await WriteOpenAiSseAsync(
                                context,
                                BuildOpenAiStreamDeltaSse(
                                    shape,
                                    completionId,
                                    created,
                                    model,
                                    timeoutMessage + (stopped ? " The upstream model stream was asked to stop." : " The bridge could not confirm an upstream stop request.")),
                                cancellationToken).ConfigureAwait(false);
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            upstreamCancellation.Cancel();
                            await ObserveCancelledReadAsync(pendingRead).ConfigureAwait(false);
                            return true;
                        }

                        await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, ""), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    int read = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (read <= 0)
                        break;

                    lastUpstreamDataUtc = DateTimeOffset.UtcNow;
                    string chunkText = Encoding.UTF8.GetString(buffer, 0, read);
                    if (captured.Length < 1024 * 1024)
                        captured.Append(chunkText);

                    if (lineBuffer.Length == 0 && !ContainsLineBreak(chunkText) && LooksLikeImmediateRawTextChunk(chunkText))
                    {
                        string content = assistantText.AcceptDeltaOrSnapshot(chunkText);
                        if (!string.IsNullOrEmpty(content))
                        {
                            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
                            wroteContent = true;
                        }

                        continue;
                    }

                    string text = lineBuffer + chunkText;
                    string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    lineBuffer = lines.Length == 0 ? "" : lines[^1];
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lines[i], assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                        wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                        if (lineResult == ChatStreamLineResult.Done)
                        {
                            if (!wroteContent)
                                wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                            if (!wroteContent)
                                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, BuildNoVisibleAssistantTextFallback()), cancellationToken).ConfigureAwait(false);
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (!string.IsNullOrWhiteSpace(lineBuffer))
            {
                ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lineBuffer, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
            }

            if (!wroteContent)
                wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
            if (!wroteContent)
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, BuildNoVisibleAssistantTextFallback()), cancellationToken).ConfigureAwait(false);
            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                try
                {
                    if (!wroteContent)
                        await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, BuildNoVisibleAssistantTextFallback()), CancellationToken.None).ConfigureAwait(false);
                    await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                return true;
            }

            return false;
        }
    }

    private async Task<bool> TryStreamOpenAiChatViaDirectOpenAiAsync(HttpContext context, byte[] openAiBody, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = _options.ServerEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
        _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

        try
        {
            foreach (string path in OpenAiChatForwardPaths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri(path));
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new ByteArrayContent(openAiBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                            break;

                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryStreamOpenAiChatViaWebSocketOpenAiAsync(HttpContext context, byte[] openAiBody, CancellationToken cancellationToken)
    {
        try
        {
            ProxyResponse? response = await TryForwardOpenAiChatViaWebSocketOpenAiAsync(openAiBody, cancellationToken).ConfigureAwait(false);
            if (response == null)
                return false;

            await WriteProxyResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryStopRemoteChatStreamAsync(string streamId, string sessionId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new HttpClient
            {
                BaseAddress = _options.ServerEndpoint,
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
            _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

            var payload = new JsonObject
            {
                ["streamId"] = streamId,
                ["reason"] = reason
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
                payload["sessionId"] = sessionId;

            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-stream/stop"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task ObserveCancelledReadAsync(Task<int>? pendingRead)
    {
        if (pendingRead == null || pendingRead.IsCompleted)
        {
            try
            {
                if (pendingRead != null)
                    await pendingRead.ConfigureAwait(false);
            }
            catch
            {
            }

            return;
        }

        try
        {
            Task completed = await Task.WhenAny(pendingRead, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (ReferenceEquals(completed, pendingRead))
                await pendingRead.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string? GetDirectChatStreamTimeoutMessage(DateTimeOffset startedUtc, DateTimeOffset lastUpstreamDataUtc)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (DirectChatStreamMaxDuration > TimeSpan.Zero && now - startedUtc >= DirectChatStreamMaxDuration)
        {
            return "The selected model stream exceeded " + FormatTimeout(DirectChatStreamMaxDuration) + ", so the bridge ended this Copilot turn to avoid leaving Visual Studio waiting indefinitely.";
        }

        if (DirectChatStreamIdleTimeout > TimeSpan.Zero && now - lastUpstreamDataUtc >= DirectChatStreamIdleTimeout)
        {
            return "The selected model stream produced no upstream data for " + FormatTimeout(DirectChatStreamIdleTimeout) + ", so the bridge ended this Copilot turn to avoid leaving Visual Studio waiting indefinitely.";
        }

        return null;
    }

    private static string FormatTimeout(TimeSpan value)
    {
        if (value.TotalMinutes >= 1 && Math.Abs(value.TotalMinutes - Math.Round(value.TotalMinutes)) < 0.01)
            return ((int)Math.Round(value.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " minute" + (Math.Round(value.TotalMinutes) == 1 ? "" : "s");

        return ((int)Math.Round(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " second" + (Math.Round(value.TotalSeconds) == 1 ? "" : "s");
    }

    private static TimeSpan ReadPositiveEnvironmentTimeSpan(string name, TimeSpan fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
    }

    private static (string SessionId, string StreamId) ReadChatStreamRequestIds(byte[] chatStreamBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(chatStreamBody);
            JsonElement root = document.RootElement;
            return (
                ReadJsonString(root, "sessionId"),
                ReadJsonString(root, "streamId"));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string ReadJsonString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static async Task<string> ReadHttpResponsePreviewAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private Uri SocketJackModelDiscoveryUri(string relativePath)
    {
        Uri endpoint = _options.PreferLocalWebChat ? _options.LocalWebChatEndpoint : _options.ServerEndpoint;
        return new Uri(endpoint, relativePath);
    }

    private async Task TryUploadVisualStudioReferenceFilesAsync(JsonObject chatStreamRequest, CancellationToken cancellationToken)
    {
        if (chatStreamRequest["files"] is not JsonArray files || files.Count == 0)
            return;

        string sessionId = chatStreamRequest["sessionId"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var uploadedOrInline = new JsonArray();
        foreach (JsonNode? node in files)
        {
            if (node is not JsonObject file)
                continue;

            JsonObject? uploaded = await TryUploadVisualStudioReferenceFileAsync(sessionId, file, cancellationToken).ConfigureAwait(false);
            uploadedOrInline.Add(uploaded ?? CloneJsonObject(file));
        }

        chatStreamRequest["files"] = uploadedOrInline;
    }

    private async Task<JsonObject?> TryUploadVisualStudioReferenceFileAsync(string sessionId, JsonObject file, CancellationToken cancellationToken)
    {
        JsonObject payload = BuildVisualStudioReferenceUploadPayload(sessionId, file);
        if (payload["text"] == null && payload["dataUrl"] == null)
            return null;

        byte[] body = Encoding.UTF8.GetBytes(payload.ToJsonString());

        JsonObject? direct = await TryUploadVisualStudioReferenceFileDirectAsync(body, cancellationToken).ConfigureAwait(false);
        if (direct != null)
            return direct;

        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json"
            };
            ProxyResponse response = await _webChatClient.ForwardAsync("POST", "/api/chat-file", "", headers, body, cancellationToken).ConfigureAwait(false);
            return TryReadUploadedReferenceFile(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("Visual Studio reference upload via WebSocket failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            return null;
        }
    }

    private async Task<JsonObject?> TryUploadVisualStudioReferenceFileDirectAsync(byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-file"));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.ParseAdd("application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return TryReadUploadedReferenceFile(new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/json", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), responseBody));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("Visual Studio reference upload failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            return null;
        }
    }

    private static JsonObject BuildVisualStudioReferenceUploadPayload(string sessionId, JsonObject file)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["name"] = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "name", "fileName", "filename"),
            ["type"] = string.IsNullOrWhiteSpace(SocketJackOpenAiChatAdapter.FirstReferenceString(file, "type", "mimeType", "contentType"))
                ? "text/plain"
                : SocketJackOpenAiChatAdapter.FirstReferenceString(file, "type", "mimeType", "contentType"),
            ["relativePath"] = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "relativePath", "path"),
            ["asFile"] = true
        };

        string dataUrl = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "dataUrl", "dataURL");
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            payload["dataUrl"] = dataUrl;
            return payload;
        }

        string text = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "text", "content", "contents", "source", "body");
        if (!string.IsNullOrEmpty(text))
            payload["text"] = text;

        return payload;
    }

    private static JsonObject? TryReadUploadedReferenceFile(ProxyResponse response)
    {
        if (response.StatusCode < 200 || response.StatusCode >= 300 || response.Body.Length == 0)
            return null;

        try
        {
            JsonObject? root = JsonNode.Parse(response.Body) as JsonObject;
            if (root == null)
                return null;

            if (root["ok"] is JsonValue okValue && okValue.TryGetValue(out bool ok) && !ok)
                return null;

            if (root["file"] is JsonObject file)
                return CloneJsonObject(file);

            if (root["files"] is JsonArray files && files.Count > 0 && files[0] is JsonObject first)
                return CloneJsonObject(first);
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static JsonObject CloneJsonObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? new JsonObject();
    }

    private static JsonObject BuildWebChatEnvelope(string id, string path, byte[] body)
    {
        return new JsonObject
        {
            ["type"] = "request",
            ["id"] = id,
            ["method"] = "POST",
            ["path"] = path,
            ["queryString"] = "",
            ["headers"] = new JsonObject
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/x-ndjson, application/json, */*"
            },
            ["bodyBase64"] = Convert.ToBase64String(body)
        };
    }

    private readonly record struct ChatStreamBinaryWriteResult(string LineBuffer, bool WroteContent);

    private static async Task<ChatStreamBinaryWriteResult> WriteChatStreamBinaryAsOpenAiSseAsync(HttpContext context, string requestId, byte[] messageBytes, string lineBuffer, SocketJackAssistantTextAccumulator assistantText, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        if (messageBytes.Length <= 32)
            return new ChatStreamBinaryWriteResult(lineBuffer, false);

        string prefix = Encoding.ASCII.GetString(messageBytes, 0, 32).Trim();
        if (!string.Equals(prefix, requestId, StringComparison.Ordinal))
            return new ChatStreamBinaryWriteResult(lineBuffer, false);

        string text = lineBuffer + Encoding.UTF8.GetString(messageBytes, 32, messageBytes.Length - 32);
        if (!ContainsLineBreak(text) && LooksLikeImmediateRawTextChunk(text))
        {
            string content = assistantText.AcceptDeltaOrSnapshot(text);
            if (string.IsNullOrEmpty(content))
                return new ChatStreamBinaryWriteResult("", false);

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return new ChatStreamBinaryWriteResult("", true);
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string remainder = lines.Length == 0 ? "" : lines[^1];
        bool wroteContent = false;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string content = assistantText.AcceptDeltaOrSnapshot(SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText(lines[i]));
            if (!string.IsNullOrEmpty(content))
            {
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
                wroteContent = true;
            }
        }

        return new ChatStreamBinaryWriteResult(remainder, wroteContent);
    }

    private enum ChatStreamLineResult
    {
        Continue,
        Content,
        Done
    }

    private static async Task<ChatStreamLineResult> WriteChatStreamLineAsOpenAiSseAsync(HttpContext context, string line, SocketJackAssistantTextAccumulator assistantText, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return ChatStreamLineResult.Continue;

        JsonObject? evt = ParseJsonObject(trimmed);
        string type = evt?["type"]?.ToString() ?? "";
        if (type.Equals("done", StringComparison.OrdinalIgnoreCase))
            return ChatStreamLineResult.Done;

        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            string error = evt?["content"]?.ToString() ??
                evt?["body"]?.ToString() ??
                evt?["error"]?.ToString() ??
                evt?["message"]?.ToString() ??
                "The selected model stream returned an error.";
            if (SocketJackOpenAiChatAdapter.IsNoAssistantTextError(error))
                return ChatStreamLineResult.Done;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, "\n\nThe model returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(error)), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Content;
        }

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            string visibleUpdate = evt == null ? "" : SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(evt);
            if (!string.IsNullOrEmpty(visibleUpdate))
            {
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, visibleUpdate), cancellationToken).ConfigureAwait(false);
                return ChatStreamLineResult.Content;
            }

            return ChatStreamLineResult.Continue;
        }

        string content;
        try
        {
            content = assistantText.AcceptDeltaOrSnapshot(SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText(line));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, "\n\n**Stream parse error:** " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message)), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Done;
        }

        if (!string.IsNullOrEmpty(content))
        {
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Content;
        }

        return ChatStreamLineResult.Continue;
    }

    private static bool ContainsLineBreak(string text)
    {
        return text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
    }

    private static bool LooksLikeImmediateRawTextChunk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return true;

        return !trimmed.StartsWith("{", StringComparison.Ordinal) &&
            !trimmed.StartsWith("[", StringComparison.Ordinal) &&
            !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryWriteCapturedChatStreamTextAsync(HttpContext context, string captured, SocketJackAssistantTextAccumulator assistantText, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captured))
            return false;

        try
        {
            string content = SocketJackOpenAiChatAdapter.ExtractAssistantText(captured);
            content = assistantText.AcceptDeltaOrSnapshot(content);
            if (string.IsNullOrWhiteSpace(content))
                return false;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (SocketJackOpenAiChatAdapter.IsNoAssistantTextError(ex.Message))
                return false;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(shape, completionId, created, model, "\n\nThe model returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message)), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static string BuildNoVisibleAssistantTextFallback()
    {
        return "The model finished without a visible reply. Please try again, or select another enabled chat model.";
    }

    private static void StartOpenAiSseResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteOpenAiSseAsync(HttpContext context, string sse, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(sse, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildOpenAiStreamStartSse(OpenAiProxyResponseShape shape, string id, long created, string model)
    {
        return shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.BuildResponseStreamStartSse(id, created, model)
            : SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(id, created, model, null, true, null);
    }

    private static string BuildOpenAiStreamDeltaSse(OpenAiProxyResponseShape shape, string id, long created, string model, string content)
    {
        return shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.BuildResponseTextDeltaSse(id, content)
            : SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(id, created, model, content, false, null);
    }

    private static async Task FinishOpenAiSseAsync(HttpContext context, OpenAiProxyResponseShape shape, string completionId, long created, string model, bool responseStarted, string assistantText, CancellationToken cancellationToken)
    {
        if (!responseStarted)
            StartOpenAiSseResponse(context);

        string finishSse = shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.BuildResponseStreamFinishSse(completionId, created, model, assistantText)
            : SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(completionId, created, model, null, false, "stop");
        await WriteOpenAiSseAsync(context, finishSse, cancellationToken).ConfigureAwait(false);
        await WriteOpenAiSseAsync(context, SocketJackOpenAiChatAdapter.DoneSse, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject? ParseJsonObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node == null)
            return null;
        if (node is JsonValue value && value.TryGetValue(out int intValue))
            return intValue;
        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }

    private async Task<ProxyResponse> ForwardOpenAiChatViaWebChatAsync(byte[] openAiBody, OpenAiProxyResponseShape shape, CancellationToken cancellationToken)
    {
        JsonObject openAiRequest;
        try
        {
            openAiRequest = JsonNode.Parse(openAiBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status400BadRequest, "Invalid OpenAI chat request JSON: " + ex.Message);
        }

        JsonObject chatStreamRequest = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(openAiRequest, _options.ModelId);
        string canonicalModel = await ResolveCanonicalModelIdAsync(ChooseSelectedServerModel(chatStreamRequest["model"]?.ToString()), cancellationToken).ConfigureAwait(false);
        chatStreamRequest["model"] = canonicalModel;
        if (shape == OpenAiProxyResponseShape.ChatCompletions)
        {
            JsonObject directOpenAiRequest = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(openAiRequest, canonicalModel);
            byte[] directOpenAiBody = Encoding.UTF8.GetBytes(directOpenAiRequest.ToJsonString());
            ProxyResponse? direct = await TryForwardOpenAiChatViaDirectOpenAiAsync(directOpenAiBody, cancellationToken).ConfigureAwait(false);
            direct ??= await TryForwardOpenAiChatViaWebSocketOpenAiAsync(directOpenAiBody, cancellationToken).ConfigureAwait(false);
            if (direct != null)
                return direct;
        }

        await TryUploadVisualStudioReferenceFilesAsync(chatStreamRequest, cancellationToken).ConfigureAwait(false);
        byte[] chatStreamBody = Encoding.UTF8.GetBytes(chatStreamRequest.ToJsonString());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/x-ndjson, application/json, */*"
        };

        ProxyResponse response = await _webChatClient.ForwardAsync("POST", "/api/chat-stream", "", headers, chatStreamBody, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            string detail = response.Body.Length == 0 ? response.ReasonPhrase : Encoding.UTF8.GetString(response.Body);
            int status = response.StatusCode == StatusCodes.Status404NotFound ? StatusCodes.Status502BadGateway : response.StatusCode;
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(status, "The selected model stream adapter failed: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(detail));
        }

        string eventsText = Encoding.UTF8.GetString(response.Body);
        string assistantText;
        try
        {
            assistantText = SocketJackOpenAiChatAdapter.ExtractAssistantText(eventsText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "The selected model stream returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message));
        }

        if (shape == OpenAiProxyResponseShape.Responses)
        {
            return SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest)
                ? SocketJackOpenAiChatAdapter.BuildStreamingResponseResponse(openAiRequest, assistantText, canonicalModel)
                : SocketJackOpenAiChatAdapter.BuildResponseResponse(openAiRequest, assistantText, canonicalModel);
        }

        return SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest)
            ? SocketJackOpenAiChatAdapter.BuildStreamingChatResponse(openAiRequest, assistantText, canonicalModel)
            : SocketJackOpenAiChatAdapter.BuildChatResponse(openAiRequest, assistantText, canonicalModel);
    }

    private async Task<string> ResolveCanonicalModelIdAsync(string requestedModel, CancellationToken cancellationToken)
    {
        requestedModel = string.IsNullOrWhiteSpace(requestedModel) ? _options.ModelId : requestedModel.Trim();
        if (string.IsNullOrWhiteSpace(requestedModel))
            return "socketjack-model";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SocketJackModelDiscoveryUri("api/models"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return requestedModel;

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonObject? root = JsonNode.Parse(json) as JsonObject;
            if (root?["models"] is not JsonArray models)
                return requestedModel;

            string normalizedRequest = NormalizeModelIdForMatch(requestedModel);
            foreach (JsonNode? node in models)
            {
                if (node is not JsonObject model)
                    continue;

                string id = model["id"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (string.Equals(id, requestedModel, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            foreach (JsonNode? node in models)
            {
                if (node is not JsonObject model)
                    continue;

                string id = model["id"]?.ToString() ?? "";
                string normalizedCandidate = NormalizeModelIdForMatch(id);
                if (normalizedCandidate.Length == 0)
                    continue;

                if (normalizedCandidate.Equals(normalizedRequest, StringComparison.Ordinal) ||
                    normalizedCandidate.Contains(normalizedRequest, StringComparison.Ordinal) ||
                    normalizedRequest.Contains(normalizedCandidate, StringComparison.Ordinal))
                {
                    return id;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return requestedModel;
    }

    private async Task<ProxyResponse?> TryForwardOpenAiChatViaDirectOpenAiAsync(byte[] openAiBody, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string path in OpenAiChatForwardPaths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri(path));
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Content = new ByteArrayContent(openAiBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/json", FlattenHeaders(response), body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private async Task<ProxyResponse?> TryForwardOpenAiChatViaWebSocketOpenAiAsync(byte[] openAiBody, CancellationToken cancellationToken)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "text/event-stream, application/json, */*"
            };
            foreach (string path in OpenAiChatForwardPaths)
            {
                ProxyResponse response = await _webChatClient.ForwardAsync("POST", "/" + path.TrimStart('/'), "", headers, openAiBody, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode >= 200 && response.StatusCode < 300)
                    return response;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private static readonly string[] OpenAiChatForwardPaths =
    {
        "v1/chat/completions",
        "api/model-runtime/v1/chat/completions"
    };

    private string ChooseSelectedServerModel(string? requestedModel)
    {
        requestedModel = requestedModel?.Trim() ?? "";
        string selectedModel = _options.ModelId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(requestedModel))
            return string.IsNullOrWhiteSpace(selectedModel) ? "socketjack-model" : selectedModel;

        if (!string.IsNullOrWhiteSpace(selectedModel) && IsCopilotPlaceholderModelId(requestedModel))
            return selectedModel;

        return requestedModel;
    }

    private static bool IsCopilotPlaceholderModelId(string model)
    {
        string normalized = NormalizeModelIdForMatch(model);
        return normalized.Length == 0 ||
            normalized is "model" or "default" or "copilot" or "ollama" or "lmstudio" or "socketjackmodel" or "openai" or
                "gpt4" or "gpt4o" or "gpt41" or "gpt5" or "gpt5mini" or "gpt5nano" or "o1" or "o3" or "o3mini" or "o4mini";
    }

    private async Task<bool> IsRuntimeModelLoadedAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        foreach (string path in new[] { "api/model-runtime/models", "api/models" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SocketJackModelDiscoveryUri(path));
                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                JsonNode? root = JsonNode.Parse(json);
                if (RuntimeModelNodeContainsLoaded(root, model))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return false;
    }

    private static bool RuntimeModelNodeContainsLoaded(JsonNode? node, string requestedModel)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (RuntimeModelNodeContainsLoaded(child, requestedModel))
                    return true;
            }
            return false;
        }

        if (node is not JsonObject obj)
            return false;

        if (RuntimeModelObjectMatches(obj, requestedModel) && RuntimeModelObjectIsLoaded(obj))
            return true;

        foreach (string property in new[] { "models", "data", "items", "availableModels" })
        {
            if (obj[property] != null && RuntimeModelNodeContainsLoaded(obj[property], requestedModel))
                return true;
        }

        return false;
    }

    private static bool RuntimeModelObjectMatches(JsonObject obj, string requestedModel)
    {
        string normalizedRequest = NormalizeModelIdForMatch(requestedModel);
        foreach (string property in new[] { "id", "key", "model", "name", "display_name", "displayName", "selected_variant", "selectedVariant" })
        {
            string value = obj[property]?.ToString() ?? "";
            if (RuntimeModelIdMatches(value, requestedModel, normalizedRequest))
                return true;
        }

        foreach (string property in new[] { "aliases", "variants" })
        {
            if (obj[property] is not JsonArray array)
                continue;

            foreach (JsonNode? alias in array)
            {
                if (RuntimeModelIdMatches(alias?.ToString() ?? "", requestedModel, normalizedRequest))
                    return true;
            }
        }

        return false;
    }

    private static bool RuntimeModelIdMatches(string candidate, string requestedModel, string normalizedRequest)
    {
        candidate = (candidate ?? "").Trim();
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requestedModel))
            return false;

        if (string.Equals(candidate, requestedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(candidate), Path.GetFileName(requestedModel), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(candidate), Path.GetFileNameWithoutExtension(requestedModel), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedCandidate = NormalizeModelIdForMatch(candidate);
        if (normalizedCandidate.Length == 0 || normalizedRequest.Length == 0)
            return false;

        return normalizedCandidate.Equals(normalizedRequest, StringComparison.Ordinal) ||
            normalizedCandidate.Contains(normalizedRequest, StringComparison.Ordinal) ||
            normalizedRequest.Contains(normalizedCandidate, StringComparison.Ordinal);
    }

    private static bool RuntimeModelObjectIsLoaded(JsonObject obj)
    {
        foreach (string property in new[] { "loaded_instances", "loadedInstances", "loaded_models", "loadedModels" })
        {
            JsonNode? value = obj[property];
            if (value is JsonArray array && array.Count > 0)
                return true;
            if (value is JsonObject)
                return true;
            if (value is JsonValue scalar && ReadJsonBoolOrNonEmptyString(scalar))
                return true;
        }

        foreach (string property in new[] { "loaded_instance_count", "loadedInstanceCount", "loaded_count", "loadedCount" })
        {
            string text = obj[property]?.ToString() ?? "";
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
                return true;
        }

        foreach (string property in new[] { "isLoaded", "loaded", "active", "ready" })
        {
            if (obj[property] is JsonValue scalar && ReadJsonBoolOrNonEmptyString(scalar))
                return true;
        }

        string status = obj["status"]?.ToString() ?? "";
        return status.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadJsonBoolOrNonEmptyString(JsonValue value)
    {
        if (value.TryGetValue(out bool boolValue))
            return boolValue;
        string text = value.ToString();
        return !string.IsNullOrWhiteSpace(text) &&
            !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelIdForMatch(string value)
    {
        var builder = new StringBuilder();
        foreach (char ch in (value ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        string normalized = builder.ToString();
        return normalized.EndsWith("gguf", StringComparison.Ordinal) ? normalized.Substring(0, normalized.Length - 4) : normalized;
    }

    private async Task<ProxyResponse?> TryForwardDirectAsync(HttpRequest source, string path, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(source.Method), BuildDirectUri(path, source.QueryString.Value));
            CopyHeaders(source, request);
            if (body.Length > 0 || RequiresBody(source.Method))
                request.Content = new ByteArrayContent(body);

            if (request.Content != null)
            {
                foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in source.Headers)
                {
                    if (!IsContentHeader(header.Key))
                        continue;
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", FlattenHeaders(response), responseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private Uri BuildDirectUri(string path, string? query)
    {
        string relative = path.TrimStart('/');
        if (!string.IsNullOrEmpty(query))
            relative += query;
        return new Uri(_options.ServerEndpoint, relative);
    }

    private static bool ShouldFallback(int statusCode)
    {
        return statusCode == StatusCodes.Status404NotFound ||
            statusCode == StatusCodes.Status502BadGateway ||
            statusCode == StatusCodes.Status503ServiceUnavailable ||
            statusCode == StatusCodes.Status504GatewayTimeout ||
            statusCode >= 500;
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Body == null)
            return Array.Empty<byte>();
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static void CopyHeaders(HttpRequest source, HttpRequestMessage target)
    {
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in source.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                IsContentHeader(header.Key))
            {
                continue;
            }

            target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    private static bool RequiresBody(string method)
    {
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContentHeader(string header)
    {
        return header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
    }

    private static IDictionary<string, string> FlattenHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    private static async Task WriteProxyResponseAsync(HttpContext context, ProxyResponse response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        foreach (KeyValuePair<string, string> header in response.Headers)
        {
            if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value;
        }

        if (!string.IsNullOrWhiteSpace(response.ContentType))
            context.Response.ContentType = response.ContentType;

        if (response.Body.Length > 0)
            await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SocketJackWebChatApiClient
{
    private readonly CopilotBridgeOptions _options;

    public SocketJackWebChatApiClient(CopilotBridgeOptions options)
    {
        _options = options;
    }

    public static Uri BuildWebSocketUri(Uri endpoint)
    {
        UriBuilder builder = new(endpoint)
        {
            Scheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/api/web-chat/ws",
            Query = ""
        };
        return builder.Uri;
    }

    public async Task<ProxyResponse> ForwardAsync(HttpRequest source, string path, byte[] body, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = BuildHeaderDictionary(source.Headers);
        string queryString = source.QueryString.HasValue ? source.QueryString.Value!.TrimStart('?') : "";
        return await ForwardAsync(source.Method, path, queryString, headers, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProxyResponse> ForwardAsync(string method, string path, string queryString, IDictionary<string, string> headers, byte[] body, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _options.ApplyAuthHeaders(socket.Options);

        await socket.ConnectAsync(BuildWebSocketUri(_options.ServerEndpoint), cancellationToken).ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        JsonObject envelope = BuildRequestEnvelope(method, id, path, queryString, headers, body);
        byte[] payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        ProxyResponseBuilder response = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket closed before response completed.");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] messageBytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    response.AppendBinaryChunk(id, messageBytes);
                    continue;
                }

                string text = Encoding.UTF8.GetString(messageBytes);
                if (HandleTextMessage(id, text, response, out ProxyResponse? completed))
                    return completed ?? response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket response ended without a payload.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket ended before response completed.");
    }

    private static Dictionary<string, string> BuildHeaderDictionary(IHeaderDictionary sourceHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in sourceHeaders)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers[header.Key] = string.Join(", ", header.Value.ToArray());
        }

        return headers;
    }

    private static JsonObject BuildRequestEnvelope(string method, string id, string path, string queryString, IDictionary<string, string> requestHeaders, byte[] body)
    {
        JsonObject headers = new();
        foreach (KeyValuePair<string, string> header in requestHeaders)
            headers[header.Key] = header.Value;

        return new JsonObject
        {
            ["type"] = "request",
            ["id"] = id,
            ["method"] = string.IsNullOrWhiteSpace(method) ? "GET" : method,
            ["path"] = path,
            ["queryString"] = queryString ?? "",
            ["headers"] = headers,
            ["bodyBase64"] = body.Length == 0 ? "" : Convert.ToBase64String(body)
        };
    }

    private static bool HandleTextMessage(string id, string text, ProxyResponseBuilder response, out ProxyResponse? completed)
    {
        completed = null;
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (message == null)
            return false;

        string type = message["type"]?.ToString() ?? "";
        string messageId = message["id"]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(messageId) && !string.Equals(messageId, id, StringComparison.Ordinal))
            return false;

        if (type.Equals("response-start", StringComparison.OrdinalIgnoreCase))
        {
            response.Start(message);
            return false;
        }

        if (type.Equals("response-end", StringComparison.OrdinalIgnoreCase))
        {
            completed = response.ToResponseOrError(StatusCodes.Status200OK, "");
            return true;
        }

        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            completed = response.ToResponseOrError(StatusCodes.Status502BadGateway, message["error"]?.ToString() ?? "SocketJack WebSocket proxy error.");
            return true;
        }

        return false;
    }
}

public sealed class ProxyResponseBuilder
{
    private readonly MemoryStream _body = new();
    private int _statusCode = StatusCodes.Status200OK;
    private string _reasonPhrase = "OK";
    private string _contentType = "application/octet-stream";
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    public void Start(JsonObject message)
    {
        _started = true;
        _statusCode = ReadInt(message, "statusCode") ?? StatusCodes.Status200OK;
        _reasonPhrase = message["reasonPhrase"]?.ToString() ?? "OK";
        _contentType = message["contentType"]?.ToString() ?? "application/octet-stream";
        if (message["headers"] is JsonObject headers)
        {
            foreach (KeyValuePair<string, JsonNode?> header in headers)
                _headers[header.Key] = header.Value?.ToString() ?? "";
        }
    }

    public void AppendBinaryChunk(string requestId, byte[] messageBytes)
    {
        if (messageBytes.Length <= 32)
            return;

        string prefix = Encoding.ASCII.GetString(messageBytes, 0, 32).Trim();
        if (!string.Equals(prefix, requestId, StringComparison.Ordinal))
            return;

        _body.Write(messageBytes, 32, messageBytes.Length - 32);
    }

    public ProxyResponse ToResponseOrError(int fallbackStatus, string fallbackError)
    {
        if (!_started && !string.IsNullOrWhiteSpace(fallbackError))
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = fallbackError }));
            return new ProxyResponse(fallbackStatus, "Bad Gateway", "application/json", new Dictionary<string, string>(), body);
        }

        return new ProxyResponse(_statusCode, _reasonPhrase, _contentType, _headers, _body.ToArray());
    }

    private static int? ReadInt(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node == null)
            return null;
        if (node is JsonValue value && value.TryGetValue(out int intValue))
            return intValue;
        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }
}

public sealed class ProxyResponse
{
    public ProxyResponse(int statusCode, string reasonPhrase, string contentType, IDictionary<string, string> headers, byte[] body)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ContentType = contentType;
        Headers = headers;
        Body = body;
    }

    public int StatusCode { get; }
    public string ReasonPhrase { get; }
    public string ContentType { get; }
    public IDictionary<string, string> Headers { get; }
    public byte[] Body { get; }
}

public static class SocketJackSafePath
{
    public static bool IsSafeGetPath(string? path)
    {
        if (!IsSafePathSyntax(path))
            return false;
        return path!.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/models", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeProxyPath(string? path)
    {
        if (!IsSafePathSyntax(path))
            return false;
        return path!.Equals("/", StringComparison.Ordinal) ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/embeddings", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/models", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/responses", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafePathSyntax(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\'))
        {
            return false;
        }

        return true;
    }
}

public static class SocketJackProxyPath
{
    public static string NormalizeForUpstream(string path)
    {
        if (path.Equals("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return "/v1/chat/completions";
        if (path.Equals("/completions", StringComparison.OrdinalIgnoreCase))
            return "/v1/completions";
        if (path.Equals("/embeddings", StringComparison.OrdinalIgnoreCase))
            return "/v1/embeddings";
        if (path.Equals("/models", StringComparison.OrdinalIgnoreCase))
            return "/v1/models";
        if (path.Equals("/responses", StringComparison.OrdinalIgnoreCase))
            return "/v1/responses";
        return path;
    }

    public static bool IsOpenAiChatCompletionsPath(string path)
    {
        return path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiResponsesPath(string path)
    {
        return path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiModelsPath(string path)
    {
        return path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOllamaTagsPath(string path)
    {
        return path.Equals("/api/tags", StringComparison.OrdinalIgnoreCase);
    }
}

public static class SocketJackOllamaChatAdapter
{
    public static ProxyResponse BuildTagsResponse(string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();
        var root = new JsonObject
        {
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = model,
                    ["model"] = model,
                    ["modified_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["size"] = 0,
                    ["digest"] = "socketjack",
                    ["details"] = new JsonObject
                    {
                        ["format"] = "gguf",
                        ["family"] = "socketjack",
                        ["families"] = new JsonArray("socketjack"),
                        ["parameter_size"] = "",
                        ["quantization_level"] = ""
                    }
                }
            }
        };
        byte[] body = Encoding.UTF8.GetBytes(root.ToJsonString());
        return new ProxyResponse(StatusCodes.Status200OK, "OK", "application/json", new Dictionary<string, string>(), body);
    }
}

public sealed class SocketJackAssistantTextAccumulator
{
    private const int LargeRepeatThreshold = 64;
    private readonly StringBuilder _visible = new();

    public string VisibleText => _visible.ToString();

    public bool HasVisibleText => _visible.Length > 0;

    public string AcceptDeltaOrSnapshot(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        string value = text;
        if (_visible.Length == 0)
        {
            _visible.Append(value);
            return value;
        }

        string current = _visible.ToString();
        if (string.Equals(value, current, StringComparison.Ordinal))
            return "";

        if (value.StartsWith(current, StringComparison.Ordinal))
        {
            string suffix = value.Substring(current.Length);
            _visible.Clear();
            _visible.Append(value);
            return suffix;
        }

        if (value.Length >= LargeRepeatThreshold && current.Contains(value, StringComparison.Ordinal))
            return "";

        if (value.Length >= LargeRepeatThreshold && current.EndsWith(value, StringComparison.Ordinal))
            return "";

        int commonPrefix = CountCommonPrefix(current, value);
        int snapshotThreshold = Math.Min(current.Length, Math.Max(16, Math.Min(LargeRepeatThreshold, current.Length / 2)));
        if (value.Length > current.Length && commonPrefix >= snapshotThreshold)
        {
            string suffix = value.Substring(commonPrefix);
            _visible.Clear();
            _visible.Append(value);
            return suffix;
        }

        _visible.Append(value);
        return value;
    }

    private static int CountCommonPrefix(string left, string right)
    {
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            if (left[i] != right[i])
                return i;
        }

        return count;
    }
}

public static class SocketJackOpenAiChatAdapter
{
    public static string DoneSse => "data: [DONE]\n\n";

    public static string CreateCompletionId()
    {
        return "chatcmpl-socketjack-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public static string CreateResponseId()
    {
        return "resp_socketjack_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public static string GetResponseModel(JsonObject openAiRequest, string selectedModelId)
    {
        return FirstNonEmpty(FirstString(openAiRequest, "model"), selectedModelId, "socketjack-model");
    }

    public static JsonObject BuildChatStreamRequest(JsonObject openAiRequest, string selectedModelId)
    {
        string model = FirstString(openAiRequest, "model");
        if (string.IsNullOrWhiteSpace(model))
            model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();

        JsonArray messages = BuildChatMessages(openAiRequest);
        AddVisualStudioFeatureMessages(openAiRequest, messages);

        var payload = new JsonObject
        {
            ["model"] = model,
            ["sessionId"] = "copilot_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ["streamId"] = "copilot_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ["messages"] = messages,
            ["files"] = ExtractVisualStudioReferenceFiles(openAiRequest)
        };

        CopyIfPresent(openAiRequest, payload, "temperature");
        CopyIfPresent(openAiRequest, payload, "top_p");
        CopyIfPresent(openAiRequest, payload, "presence_penalty");
        CopyIfPresent(openAiRequest, payload, "frequency_penalty");
        CopyIfPresent(openAiRequest, payload, "metadata");
        CopyIfPresent(openAiRequest, payload, "reasoning");
        CopyIfPresent(openAiRequest, payload, "response_format");
        CopyIfPresent(openAiRequest, payload, "tool_choice");
        CopyIfPresent(openAiRequest, payload, "tools");
        CopyIfPresent(openAiRequest, payload, "parallel_tool_calls");
        CopyIfPresent(openAiRequest, payload, "previous_response_id");
        CopyIfPresent(openAiRequest, payload, "summary");
        CopyIfPresent(openAiRequest, payload, "conversation_summary");
        CopyIfPresent(openAiRequest, payload, "context_summary");
        CopyMaxTokens(openAiRequest, payload);

        if (openAiRequest.ContainsKey("tools") ||
            openAiRequest.ContainsKey("tool_choice") ||
            openAiRequest.ContainsKey("functions") ||
            openAiRequest.ContainsKey("function_call") ||
            !string.IsNullOrWhiteSpace(DetectVisualStudioAiMode(openAiRequest)) ||
            (payload["files"] is JsonArray payloadFiles && payloadFiles.Count > 0))
        {
            payload["service"] = "agent";
        }

        return payload;
    }

    public static JsonObject BuildOpenAiChatCompletionsForwardRequest(JsonObject openAiRequest, string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId)
            ? FirstNonEmpty(FirstString(openAiRequest, "model"), "socketjack-model")
            : selectedModelId.Trim();

        var payload = CloneObject(openAiRequest);
        payload["model"] = model;
        JsonArray messages = BuildChatMessages(openAiRequest);
        AddVisualStudioFeatureMessages(openAiRequest, messages);
        AddVisualStudioReferenceFileMessage(openAiRequest, messages);
        payload["messages"] = messages;
        if (openAiRequest["stream"] == null)
            payload["stream"] = true;

        CopyMaxTokens(openAiRequest, payload);
        return payload;
    }

    private static JsonArray BuildChatMessages(JsonObject openAiRequest)
    {
        if (openAiRequest["messages"] is JsonArray messages && messages.Count > 0)
            return CloneArray(messages);

        var result = new JsonArray();
        AddMessagesFromResponsesInput(openAiRequest["input"], result);
        if (result.Count == 0)
        {
            string prompt = FirstString(openAiRequest, "prompt", "query");
            if (!string.IsNullOrWhiteSpace(prompt))
                result.Add(BuildMessage("user", prompt));
        }

        return result;
    }

    private static void AddMessagesFromResponsesInput(JsonNode? input, JsonArray messages)
    {
        if (input == null)
            return;

        if (input is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (input is JsonArray array)
        {
            foreach (JsonNode? item in array)
                AddResponsesInputItem(item, messages);
            return;
        }

        AddResponsesInputItem(input, messages);
    }

    private static void AddResponsesInputItem(JsonNode? item, JsonArray messages)
    {
        if (item == null)
            return;

        if (item is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (item is JsonArray array)
        {
            string text = MessageContentText(array);
            if (!string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (item is not JsonObject obj)
            return;

        string type = FirstString(obj, "type");
        string role = NormalizeChatRole(FirstString(obj, "role"));
        if (string.IsNullOrWhiteSpace(role))
            role = type.Equals("message", StringComparison.OrdinalIgnoreCase) ? "user" : "user";

        string content = MessageContentText(obj["content"]);
        if (string.IsNullOrWhiteSpace(content))
            content = FirstChatTextValue(obj, "text", "input_text", "output_text", "summary", "content", "body", "value");
        if (string.IsNullOrWhiteSpace(content))
            return;

        messages.Add(BuildMessage(role, content));
    }

    private static JsonObject BuildMessage(string role, string content)
    {
        return new JsonObject
        {
            ["role"] = NormalizeChatRole(role),
            ["content"] = content
        };
    }

    private static string NormalizeChatRole(string role)
    {
        string value = (role ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "developer" => "system",
            "tool" => "tool",
            "function" => "tool",
            _ => "user"
        };
    }

    private static void AddVisualStudioFeatureMessages(JsonObject openAiRequest, JsonArray messages)
    {
        string instructions = FirstNonEmpty(
            FirstString(openAiRequest, "instructions"),
            FirstString(openAiRequest, "developer"),
            FirstString(openAiRequest, "system"));
        if (!string.IsNullOrWhiteSpace(instructions))
            messages.Insert(0, BuildMessage("system", instructions));

        string featureInstruction = BuildVisualStudioFeatureInstruction(openAiRequest);
        if (!string.IsNullOrWhiteSpace(featureInstruction))
            messages.Insert(0, BuildMessage("system", featureInstruction));
    }

    private static void AddVisualStudioReferenceFileMessage(JsonObject openAiRequest, JsonArray messages)
    {
        JsonArray files = ExtractVisualStudioReferenceFiles(openAiRequest);
        if (files.Count == 0)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("Visual Studio reference files:");
        foreach (JsonNode? node in files)
        {
            if (node is not JsonObject file)
                continue;

            string name = FirstNonEmpty(FirstString(file, "relativePath"), FirstString(file, "name"), "reference");
            string text = FirstString(file, "text", "content");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            builder.AppendLine();
            builder.AppendLine("File: " + name);
            builder.AppendLine("```");
            builder.AppendLine(text);
            builder.AppendLine("```");
        }

        string message = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(message))
            messages.Add(BuildMessage("system", message));
    }

    private static string BuildVisualStudioFeatureInstruction(JsonObject openAiRequest)
    {
        var parts = new List<string>();
        string mode = DetectVisualStudioAiMode(openAiRequest);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            string behavior = mode switch
            {
                "Interactive" => "Ask before risky or user-visible write actions and keep the user in the loop.",
                "Bypass" => "Proceed without extra confirmation prompts when existing SocketJack and Visual Studio permission gates already allow the action.",
                "AutoPilot" => "Continue autonomously through safe read, edit, build, and verification steps until the requested task is handled.",
                _ => ""
            };
            parts.Add("Visual Studio AI mode: " + mode + ". " + behavior);
        }

        string summary = FirstNonEmpty(
            FirstString(openAiRequest, "summary"),
            FirstString(openAiRequest, "conversation_summary"),
            FirstString(openAiRequest, "context_summary"),
            FirstString(openAiRequest, "workspace_summary"),
            FirstStringFromChild(openAiRequest, "metadata", "summary", "conversation_summary", "context_summary"),
            FirstStringFromChild(openAiRequest, "context", "summary", "conversation_summary", "workspace_summary"));
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add("Visual Studio supplied context summary: " + summary);

        return string.Join("\n\n", parts);
    }

    private static string DetectVisualStudioAiMode(JsonObject openAiRequest)
    {
        foreach (JsonObject candidate in CandidateVisualStudioFeatureObjects(openAiRequest))
        {
            string direct = FirstNonEmpty(
                FirstString(candidate, "mode"),
                FirstString(candidate, "chatMode"),
                FirstString(candidate, "chat_mode"),
                FirstString(candidate, "interactionMode"),
                FirstString(candidate, "interaction_mode"),
                FirstString(candidate, "agentMode"),
                FirstString(candidate, "agent_mode"),
                FirstString(candidate, "approvalMode"),
                FirstString(candidate, "approval_mode"),
                FirstString(candidate, "automationMode"),
                FirstString(candidate, "automation_mode"),
                FirstString(candidate, "copilotMode"),
                FirstString(candidate, "copilot_mode"),
                FirstString(candidate, "visualStudioMode"),
                FirstString(candidate, "visual_studio_mode"));
            string mode = CanonicalVisualStudioAiMode(direct);
            if (!string.IsNullOrWhiteSpace(mode))
                return mode;
        }

        return "";
    }

    private static IEnumerable<JsonObject> CandidateVisualStudioFeatureObjects(JsonObject openAiRequest)
    {
        yield return openAiRequest;

        foreach (string name in new[] { "metadata", "context", "options", "settings", "copilot", "visualStudio", "visual_studio", "vs" })
        {
            if (openAiRequest[name] is JsonObject obj)
                yield return obj;
        }
    }

    private static string CanonicalVisualStudioAiMode(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";
        if (Regex.IsMatch(text, @"\binteractive\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Interactive";
        if (Regex.IsMatch(text, @"\bbypass\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Bypass";
        if (Regex.IsMatch(text, @"\bauto\s*pilot\b|\bautopilot\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "AutoPilot";
        return "";
    }

    public static bool WantsStreaming(JsonObject openAiRequest)
    {
        return openAiRequest["stream"] is JsonValue value &&
            value.TryGetValue(out bool stream) &&
            stream;
    }

    public static string ExtractAssistantText(string chatStreamEvents)
    {
        if (string.IsNullOrWhiteSpace(chatStreamEvents))
            return "";

        var builder = new StringBuilder();
        var assistantText = new SocketJackAssistantTextAccumulator();
        string[] lines = chatStreamEvents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line.Substring(5).Trim();
            if (line.Length == 0 || line.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string content = ExtractAssistantDeltaText(line);
                content = assistantText.AcceptDeltaOrSnapshot(content);
                if (!string.IsNullOrEmpty(content))
                    builder.Append(content);
            }
            catch (JsonException)
            {
                if (!IsInternalProgressLikeText(line) && !LooksLikeRawStructuredPayload(line))
                {
                    string content = assistantText.AcceptDeltaOrSnapshot(line);
                    if (!string.IsNullOrEmpty(content))
                        builder.AppendLine(content);
                }
            }
        }

        return NormalizeVisibleAssistantText(builder.ToString());
    }

    public static string ExtractAssistantDeltaText(string chatStreamEvent)
    {
        if (string.IsNullOrEmpty(chatStreamEvent))
            return "";

        string line = chatStreamEvent;
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return "";

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int dataIndex = line.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
            line = dataIndex >= 0 ? line.Substring(dataIndex + 5) : trimmed.Substring(5);
            if (line.StartsWith(" ", StringComparison.Ordinal))
                line = line.Substring(1);
            trimmed = line.Trim();
        }

        if (trimmed.Length == 0 || trimmed.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return "";

        try
        {
            var builder = new StringBuilder();
            AppendChatAssistantText(JsonNode.Parse(trimmed), builder, preserveTokenWhitespace: true);
            return StripHiddenAssistantMarkup(builder.ToString());
        }
        catch (JsonException)
        {
            if (!IsInternalProgressLikeText(line) && !LooksLikeRawStructuredPayload(line))
                return line;
        }

        return "";
    }

    public static string BuildVisibleChatStreamUpdate(JsonObject evt)
    {
        if (evt == null)
            return "";

        string type = FirstString(evt, "type");
        if (type.Equals("usage", StringComparison.OrdinalIgnoreCase))
            return "";

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return "";
    }

    public static string ToSafeMarkdownStatus(string value)
    {
        return FirstStatusText(new JsonObject { ["value"] = value }, "value");
    }

    public static bool IsNoAssistantTextError(string value)
    {
        string text = value ?? "";
        return text.Contains("completed without returning assistant text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("returned no assistant text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no assistant content", StringComparison.OrdinalIgnoreCase);
    }

    public static ProxyResponse BuildChatResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateCompletionId();
        var root = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = assistantText ?? ""
                    },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            }
        };

        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildStreamingChatResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateCompletionId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var builder = new StringBuilder();
        builder.Append(BuildStreamingDeltaSse(id, created, model, null, true, null));
        if (!string.IsNullOrEmpty(assistantText))
            builder.Append(BuildStreamingDeltaSse(id, created, model, assistantText, false, null));
        builder.Append(BuildStreamingDeltaSse(id, created, model, null, false, "stop"));
        builder.Append(DoneSse);

        return new ProxyResponse(
            StatusCodes.Status200OK,
            "OK",
            "text/event-stream",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-cache",
                ["X-Accel-Buffering"] = "no"
            },
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static ProxyResponse BuildResponseResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateResponseId();
        JsonObject root = BuildResponseObject(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model, "completed", assistantText ?? "");
        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildStreamingResponseResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateResponseId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var builder = new StringBuilder();
        builder.Append(BuildResponseStreamStartSse(id, created, model));
        if (!string.IsNullOrEmpty(assistantText))
            builder.Append(BuildResponseTextDeltaSse(id, assistantText));
        builder.Append(BuildResponseStreamFinishSse(id, created, model, assistantText ?? ""));
        builder.Append(DoneSse);

        return new ProxyResponse(
            StatusCodes.Status200OK,
            "OK",
            "text/event-stream",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-cache",
                ["X-Accel-Buffering"] = "no"
            },
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static string BuildResponseStreamStartSse(string id, long created, string model)
    {
        string messageId = ResponseMessageId(id);
        var builder = new StringBuilder();
        AppendEventSse(builder, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = BuildResponseObject(id, created, model, "in_progress", "")
        });
        AppendEventSse(builder, "response.in_progress", new JsonObject
        {
            ["type"] = "response.in_progress",
            ["response"] = BuildResponseObject(id, created, model, "in_progress", "")
        });
        AppendEventSse(builder, "response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["status"] = "in_progress",
                ["role"] = "assistant",
                ["content"] = new JsonArray()
            }
        });
        AppendEventSse(builder, "response.content_part.added", new JsonObject
        {
            ["type"] = "response.content_part.added",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = ""
            }
        });
        return builder.ToString();
    }

    public static string BuildResponseTextDeltaSse(string id, string? content)
    {
        var builder = new StringBuilder();
        AppendEventSse(builder, "response.output_text.delta", new JsonObject
        {
            ["type"] = "response.output_text.delta",
            ["item_id"] = ResponseMessageId(id),
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["delta"] = content ?? ""
        });
        return builder.ToString();
    }

    public static string BuildResponseStreamFinishSse(string id, long created, string model, string assistantText)
    {
        string text = assistantText ?? "";
        string messageId = ResponseMessageId(id);
        var contentPart = new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text
        };
        var message = new JsonObject
        {
            ["id"] = messageId,
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray { CloneNode(contentPart) }
        };

        var builder = new StringBuilder();
        AppendEventSse(builder, "response.output_text.done", new JsonObject
        {
            ["type"] = "response.output_text.done",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["text"] = text
        });
        AppendEventSse(builder, "response.content_part.done", new JsonObject
        {
            ["type"] = "response.content_part.done",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = CloneNode(contentPart)
        });
        AppendEventSse(builder, "response.output_item.done", new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["output_index"] = 0,
            ["item"] = CloneNode(message)
        });
        AppendEventSse(builder, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = BuildResponseObject(id, created, model, "completed", text)
        });
        return builder.ToString();
    }

    public static string BuildStreamingDeltaSse(string id, long created, string model, string? content, bool includeRole, string? finishReason)
    {
        var delta = new JsonObject();
        if (includeRole)
            delta["role"] = "assistant";
        if (content != null)
            delta["content"] = content;
        var builder = new StringBuilder();
        AppendSse(builder, BuildChunk(id, created, model, delta, finishReason));
        return builder.ToString();
    }

    public static ProxyResponse BuildModelsResponse(string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();
        var root = new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = model,
                    ["object"] = "model",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["owned_by"] = "socketjack"
                }
            }
        };

        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildErrorResponse(int statusCode, string message)
    {
        var root = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = string.IsNullOrWhiteSpace(message) ? "SocketJack bridge error." : message,
                ["type"] = "socketjack_bridge_error",
                ["code"] = "socketjack_bridge_error"
            }
        };

        return JsonResponse(statusCode, root);
    }

    private static string MessageContentText(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? stringValue))
                return stringValue ?? "";
            return node.ToString();
        }

        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (JsonNode? item in array)
            {
                string text = MessageContentText(item);
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            return string.Join("\n\n", parts);
        }

        if (node is JsonObject obj)
        {
            string text = FirstChatTextValue(obj, "text", "content", "input_text", "body", "value");
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return "";
    }

    private static void AppendChatAssistantText(JsonNode? node, StringBuilder builder)
    {
        AppendChatAssistantText(node, builder, preserveTokenWhitespace: false);
    }

    private static void AppendChatAssistantText(JsonNode? node, StringBuilder builder, bool preserveTokenWhitespace)
    {
        if (node == null)
            return;

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && ShouldAppendAssistantText(text ?? "", preserveTokenWhitespace))
                builder.Append(text);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                AppendChatAssistantText(item, builder, preserveTokenWhitespace);
            return;
        }

        if (node is not JsonObject obj)
            return;

        string type = FirstString(obj, "type", "event");
        string status = FirstString(obj, "status");
        if (type.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            JsonBoolIsFalse(obj["ok"]) ||
            JsonBoolIsFalse(obj["success"]))
        {
            string errorText = FirstNonEmpty(FirstChatTextValue(obj, "error", "message", "detail", "content", "body"), "AI request failed.");
            if (IsNoAssistantTextError(errorText))
                return;

            throw new InvalidOperationException(errorText);
        }

        if (obj["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choice in choices)
                AppendChatAssistantText(choice, builder, preserveTokenWhitespace);
            return;
        }

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("fileChanges", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("file-change", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int beforeNested = builder.Length;
        foreach (string name in new[] { "message", "delta", "result", "response", "data" })
        {
            if (obj[name] is JsonObject child)
                AppendChatAssistantText(child, builder, preserveTokenWhitespace);
        }

        foreach (string name in new[] { "events", "items", "output" })
        {
            if (obj[name] is JsonArray children)
                AppendChatAssistantText(children, builder, preserveTokenWhitespace);
        }

        string content = FirstChatTextValue(
            obj,
            preserveTokenWhitespace,
            "final",
            "finalAnswer",
            "final_answer",
            "answer",
            "output_text",
            "outputText",
            "completion",
            "content",
            "reasoning",
            "reasoning_content",
            "reasoningContent",
            "body",
            "text",
            "response",
            "message");
        if (preserveTokenWhitespace ? content.Length == 0 : string.IsNullOrWhiteSpace(content))
            return;

        string nestedText = builder.Length > beforeNested ? builder.ToString(beforeNested, builder.Length - beforeNested) : "";
        if (!string.IsNullOrEmpty(nestedText) &&
            (string.Equals(nestedText, content, StringComparison.Ordinal) ||
            (content.Length >= 16 && nestedText.Contains(content, StringComparison.Ordinal)) ||
            (nestedText.Length >= 16 && content.Contains(nestedText, StringComparison.Ordinal))))
        {
            return;
        }

        if (LooksLikeJsonContainer(content.Trim()))
        {
            try
            {
                int before = builder.Length;
                AppendChatAssistantText(JsonNode.Parse(content), builder, preserveTokenWhitespace);
                if (builder.Length > before)
                    return;
            }
            catch (JsonException)
            {
            }
        }

        if (ShouldAppendAssistantText(content, preserveTokenWhitespace))
            builder.Append(content);
    }

    private static string FirstChatTextValue(JsonObject obj, params string[] names)
    {
        return FirstChatTextValue(obj, false, names);
    }

    private static bool ShouldAppendAssistantText(string text, bool preserveTokenWhitespace)
    {
        if (preserveTokenWhitespace)
        {
            if (text.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(text))
                return true;
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return !IsInternalProgressLikeText(text) && !LooksLikeRawStructuredPayload(text);
    }

    private static string FirstChatTextValue(JsonObject obj, bool preserveTokenWhitespace, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            if (node is JsonValue value)
            {
                if (value.TryGetValue(out string? stringValue) &&
                    (preserveTokenWhitespace ? !string.IsNullOrEmpty(stringValue) : !string.IsNullOrWhiteSpace(stringValue)))
                {
                    return stringValue;
                }
                if (value.TryGetValue(out long longValue))
                    return longValue.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue(out double doubleValue))
                    return doubleValue.ToString(CultureInfo.InvariantCulture);
            }
            else if (node is JsonArray array)
            {
                string text = MessageContentText(array);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return "";
    }

    private static bool JsonBoolIsFalse(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool boolValue) && !boolValue;

    private static bool LooksLikeJsonContainer(string value)
    {
        string trimmed = (value ?? "").Trim();
        return (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
            (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal));
    }

    private static string NormalizeVisibleAssistantText(string value)
    {
        string text = StripHiddenAssistantMarkup(value ?? "");
        var finals = Regex.Matches(text, @"<\s*auto_final\b[^>]*>([\s\S]*?)<\s*/\s*auto_final\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
        if (finals.Length > 0)
            text = string.Join("\n\n", finals);

        text = Regex.Replace(text, @"<\s*/?\s*auto_final\b[^>]*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = StripHiddenAssistantMarkup(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new List<string>();
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length != 0)
                    output.Add("");
                continue;
            }

            if (trimmed.StartsWith("[SocketJack]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[Socketjack]", StringComparison.OrdinalIgnoreCase) ||
                IsInternalProgressLikeText(trimmed) ||
                LooksLikeRawStructuredPayload(trimmed))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);
        return string.Join("\n", output).Trim();
    }

    private static string StripHiddenAssistantMarkup(string value)
    {
        string text = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        string previous = "";
        for (int i = 0; i < 8 && !string.Equals(previous, text, StringComparison.Ordinal); i++)
        {
            previous = text;
            text = Regex.Replace(text, @"<\s*(think|thinking|thought|analysis)\b[^>]*>[\s\S]*?<\s*/\s*\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = Regex.Replace(text, @"<\s*(?:think|thinking|thought|analysis)\b[^>]*>[\s\S]*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"<\s*/?\s*(?:think|thinking|thought|analysis)\b[^>]*>?", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"<\s*/?\s*markdown\s*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    private static bool IsInternalProgressLikeText(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return true;
        if (Regex.IsMatch(text, @"\bstill connected after\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(text, @"^(checking llmruntime model|llmruntime model .* is loaded|opening chat stream|processing prompt|selected server|routing |connected to auto server|waiting for |server started|server is working|server completed|tool event:|tool summary:|tool complete:|tool failed:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(text, @"^\s*(?:data:\s*)?[\[{].*(tool|function_call|tool_call|browser_|internet_search|search_query|vs_|shell|powershell)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static JsonObject BuildResponseObject(string id, long created, string model, string status, string assistantText)
    {
        string text = assistantText ?? "";
        var message = new JsonObject
        {
            ["id"] = ResponseMessageId(id),
            ["type"] = "message",
            ["status"] = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "in_progress",
            ["role"] = "assistant",
            ["content"] = string.IsNullOrEmpty(text)
                ? new JsonArray()
                : new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = text
                    }
                }
        };

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = created,
            ["model"] = model,
            ["status"] = status,
            ["output"] = string.IsNullOrEmpty(text) && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                ? new JsonArray()
                : new JsonArray { message },
            ["output_text"] = text,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 0,
                ["output_tokens"] = 0,
                ["total_tokens"] = 0
            }
        };
    }

    private static string ResponseMessageId(string responseId)
    {
        return "msg_" + Regex.Replace(responseId ?? "", @"[^A-Za-z0-9_]", "_", RegexOptions.CultureInvariant);
    }

    private static JsonObject BuildChunk(string id, long created, string model, JsonObject delta, string? finishReason)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason == null ? null : JsonValue.Create(finishReason)
                }
            }
        };
    }

    private static void AppendSse(StringBuilder builder, JsonObject payload)
    {
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

    private static void AppendEventSse(StringBuilder builder, string eventName, JsonObject payload)
    {
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

    private static ProxyResponse JsonResponse(int statusCode, JsonObject root)
    {
        return new ProxyResponse(
            statusCode,
            statusCode >= 200 && statusCode < 300 ? "OK" : "Error",
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void CopyMaxTokens(JsonObject source, JsonObject target)
    {
        if (source["max_tokens"] != null)
        {
            target["max_tokens"] = CloneNode(source["max_tokens"]);
            return;
        }

        if (source["max_completion_tokens"] != null)
            target["max_tokens"] = CloneNode(source["max_completion_tokens"]);
    }

    private static void CopyIfPresent(JsonObject source, JsonObject target, string name)
    {
        if (source[name] != null)
            target[name] = CloneNode(source[name]);
    }

    private static JsonArray CloneArray(JsonArray? source)
    {
        var clone = new JsonArray();
        if (source == null)
            return clone;

        foreach (JsonNode? node in source)
            clone.Add(CloneNode(node));
        return clone;
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node == null ? null : JsonNode.Parse(node.ToJsonString());
    }

    public static JsonArray ExtractVisualStudioReferenceFiles(JsonObject openAiRequest)
    {
        var files = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddReferenceFilesFromNode(openAiRequest["files"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["attachments"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["references"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["documents"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["input_files"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["input"], files, seen);

        if (openAiRequest["context"] is JsonObject context)
        {
            AddReferenceFilesFromNode(context["files"], files, seen);
            AddReferenceFilesFromNode(context["attachments"], files, seen);
            AddReferenceFilesFromNode(context["references"], files, seen);
            AddReferenceFilesFromNode(context["documents"], files, seen);
            AddReferenceFilesFromNode(context["snippets"], files, seen);
        }

        if (openAiRequest["messages"] is JsonArray messages)
        {
            foreach (JsonNode? messageNode in messages)
            {
                if (messageNode is not JsonObject message)
                    continue;

                AddReferenceFilesFromNode(message["files"], files, seen);
                AddReferenceFilesFromNode(message["attachments"], files, seen);
                AddReferenceFilesFromNode(message["references"], files, seen);
                AddReferenceFilesFromNode(message["snippets"], files, seen);

                JsonNode? content = message["content"];
                if (content is JsonArray parts)
                {
                    foreach (JsonNode? part in parts)
                    {
                        if (part is JsonObject partObject)
                            TryAddVisualStudioReferenceFile(partObject, files, seen);
                    }
                }
                else
                {
                    TryAddVisualStudioReferenceText(ReferenceNodeString(content), files, seen);
                }
            }
        }

        return files;
    }

    public static string FirstReferenceString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            string value = ReferenceNodeString(node);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static void AddReferenceFilesFromNode(JsonNode? node, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32 || node == null)
            return;

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                    TryAddVisualStudioReferenceFile(obj, files, seen);
                else
                    TryAddVisualStudioReferenceText(ReferenceNodeString(item), files, seen);

                if (files.Count >= 32)
                    return;
            }

            return;
        }

        if (node is JsonObject single)
            TryAddVisualStudioReferenceFile(single, files, seen);
        else
            TryAddVisualStudioReferenceText(ReferenceNodeString(node), files, seen);
    }

    private static void TryAddVisualStudioReferenceFile(JsonObject source, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32)
            return;

        string dataUrl = FirstReferenceString(source, "dataUrl", "dataURL");
        string text = FirstReferenceString(source, "text", "content", "contents", "source", "body", "value", "snippet", "code");
        string path = FirstReferenceString(source, "path", "filePath", "fullPath", "uri", "url", "relativePath");
        string name = FirstReferenceString(source, "name", "fileName", "filename", "title", "label");
        string type = FirstNonEmpty(FirstReferenceString(source, "type", "mimeType", "contentType"), GuessMimeType(name, path));

        if (string.IsNullOrWhiteSpace(name))
            name = FileNameFromReferencePath(path);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(dataUrl) &&
            TryReadLocalVisualStudioReference(path, out string localText, out string localType, out string localName))
        {
            text = localText;
            if (string.IsNullOrWhiteSpace(name))
                name = localName;
            if (string.IsNullOrWhiteSpace(type))
                type = localType;
        }

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(dataUrl))
            return;

        if (string.IsNullOrWhiteSpace(name))
            name = "visual-studio-reference-" + (files.Count + 1).ToString(CultureInfo.InvariantCulture) + ".txt";

        name = SanitizeReferenceFileName(name);
        string relativePath = BuildVisualStudioReferenceRelativePath(source, name, path);
        string dedupe = ComputeReferenceHash(name + "|" + relativePath + "|" + text + "|" + dataUrl);
        if (!seen.Add(dedupe))
            return;

        var file = new JsonObject
        {
            ["id"] = "vsref_" + dedupe,
            ["name"] = name,
            ["type"] = string.IsNullOrWhiteSpace(type) ? "text/plain" : type,
            ["relativePath"] = relativePath,
            ["source"] = "visual-studio",
            ["asFile"] = true,
            ["uploadedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        CopyReferenceMetadata(source, file, "startLine", "endLine", "range", "language", "projectName", "documentId");

        if (!string.IsNullOrWhiteSpace(path))
            file["originalPath"] = path;
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            file["dataUrl"] = dataUrl;
            file["size"] = EstimateDataUrlBytes(dataUrl);
        }
        else
        {
            string limited = LimitReferenceText(text);
            file["text"] = limited;
            file["content"] = limited;
            file["size"] = Encoding.UTF8.GetByteCount(limited);
        }

        files.Add(file);
    }

    private static void TryAddVisualStudioReferenceText(string text, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32 || !LooksLikeInlineVisualStudioReference(text))
            return;

        string name = ExtractReferenceFileNameFromText(text);
        if (string.IsNullOrWhiteSpace(name))
            name = "visual-studio-snippet-" + (files.Count + 1).ToString(CultureInfo.InvariantCulture) + ".md";

        var source = new JsonObject
        {
            ["name"] = name,
            ["type"] = GuessMimeType(name, ""),
            ["text"] = text,
            ["relativePath"] = "VisualStudio/" + name
        };
        TryAddVisualStudioReferenceFile(source, files, seen);
    }

    private static bool LooksLikeInlineVisualStudioReference(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 512)
            return false;

        return text.Contains("```", StringComparison.Ordinal) ||
            text.Contains("Active Document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("references", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\b(class|interface|struct|enum|namespace|using|public|private|protected|internal)\b", RegexOptions.CultureInvariant);
    }

    private static bool TryReadLocalVisualStudioReference(string path, out string text, out string type, out string name)
    {
        text = "";
        type = "";
        name = "";
        string fullPath = NormalizeReferencePath(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return false;

        var info = new FileInfo(fullPath);
        if (info.Length > 10L * 1024L * 1024L)
            return false;

        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            if (LooksBinary(bytes))
                return false;

            text = Encoding.UTF8.GetString(bytes);
            name = info.Name;
            type = GuessMimeType(name, fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeReferencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        string value = path.Trim().Trim('"', '\'');
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            value = uri.LocalPath;

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        int sample = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < sample; i++)
        {
            if (bytes[i] == 0)
                return true;
        }

        return false;
    }

    private static string BuildVisualStudioReferenceRelativePath(JsonObject source, string name, string path)
    {
        string explicitRelative = FirstReferenceString(source, "relativePath", "workspaceRelativePath", "projectRelativePath");
        if (!string.IsNullOrWhiteSpace(explicitRelative) && !Path.IsPathRooted(explicitRelative))
            return NormalizeReferenceRelativePath("VisualStudio/" + explicitRelative);

        string pathName = FileNameFromReferencePath(path);
        return NormalizeReferenceRelativePath("VisualStudio/" + (string.IsNullOrWhiteSpace(pathName) ? name : pathName));
    }

    private static string NormalizeReferenceRelativePath(string value)
    {
        var parts = new List<string>();
        foreach (string raw in (value ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = SanitizeReferenceFileName(raw.Trim());
            if (part.Length == 0 || part == "." || part == "..")
                continue;
            parts.Add(part);
        }

        return parts.Count == 0 ? "VisualStudio/reference.txt" : string.Join("/", parts);
    }

    private static string ExtractReferenceFileNameFromText(string text)
    {
        Match match = Regex.Match(text ?? "", @"[A-Za-z]:\\[^\r\n""'<>|]+\.[A-Za-z0-9]{1,12}", RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(text ?? "", @"(?:^|[\s`""'])([A-Za-z0-9_. -]+\.[A-Za-z0-9]{1,12})(?:\s|$)", RegexOptions.CultureInvariant);

        if (!match.Success)
            return "";

        string value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
        return FileNameFromReferencePath(value.Trim());
    }

    private static string FileNameFromReferencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            string value = path.Trim().Trim('"', '\'');
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                value = uri.LocalPath;
            return Path.GetFileName(value.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return "";
        }
    }

    private static string SanitizeReferenceFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        return fileName.Trim();
    }

    private static string LimitReferenceText(string text)
    {
        text ??= "";
        const int maxChars = 10 * 1024 * 1024;
        return Encoding.UTF8.GetByteCount(text) <= maxChars ? text : text.Substring(0, Math.Min(text.Length, maxChars));
    }

    private static int EstimateDataUrlBytes(string dataUrl)
    {
        string value = dataUrl ?? "";
        int comma = value.IndexOf(',');
        string encoded = comma >= 0 ? value.Substring(comma + 1) : value;
        return (int)Math.Min(int.MaxValue, Math.Max(0, encoded.Length * 3L / 4L));
    }

    private static string ComputeReferenceHash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
        return BitConverter.ToString(hash, 0, 8).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string GuessMimeType(string name, string path)
    {
        string extension = Path.GetExtension(FirstNonEmpty(name, path)).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "text/x-csharp",
            ".vb" => "text/x-vb",
            ".fs" => "text/x-fsharp",
            ".ts" => "text/typescript",
            ".tsx" => "text/tsx",
            ".js" => "text/javascript",
            ".jsx" => "text/jsx",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".xaml" => "application/xaml+xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".py" => "text/x-python",
            ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" => "text/x-c",
            _ => "text/plain"
        };
    }

    private static string ReferenceNodeString(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? stringValue))
                return stringValue ?? "";
            if (value.TryGetValue(out bool boolValue))
                return boolValue.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue(out long longValue))
                return longValue.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue(out double doubleValue))
                return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return node.ToString();
    }

    private static void CopyReferenceMetadata(JsonObject source, JsonObject target, params string[] names)
    {
        foreach (string name in names)
        {
            if (source[name] != null)
                target[name] = CloneNode(source[name]);
        }
    }

    private static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;
            string value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string FirstStringFromChild(JsonObject obj, string childName, params string[] names)
    {
        return obj[childName] is JsonObject child ? FirstString(child, names) : "";
    }

    private static string FirstTextSegment(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            string value;
            if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? stringValue))
                value = stringValue ?? "";
            else
                value = node.ToString();

            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static string FirstStatusText(JsonObject obj, params string[] names)
    {
        string value = FirstTextSegment(obj, names);
        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (LooksLikeRawStructuredPayload(value))
            return "";

        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(value.Length);
        bool lastWasWhite = false;
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhite)
                    builder.Append(' ');
                lastWasWhite = true;
                continue;
            }

            builder.Append(ch);
            lastWasWhite = false;
        }

        string result = builder.ToString().Trim();
        if (result.Length > 220)
            result = result.Substring(0, 217) + "...";
        return result;
    }

    private static bool LooksLikeRawStructuredPayload(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
            (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return true;
        }

        return trimmed.IndexOf("\"type\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("\"tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}

public static class SocketJackSecretRedactor
{
    private static readonly string[] SecretNames = { "token", "api_key", "apikey", "authorization", "secret", "password" };

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        string redacted = value!;
        foreach (string secretName in SecretNames)
            redacted = RedactName(redacted, secretName);
        return redacted;
    }

    private static string RedactName(string value, string name)
    {
        string redacted = value;
        int searchStart = 0;
        while (searchStart < redacted.Length)
        {
            int index = redacted.IndexOf(name, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;

            int separator = redacted.IndexOfAny(new[] { ':', '=' }, index + name.Length);
            if (separator < 0)
            {
                searchStart = index + name.Length;
                continue;
            }

            int valueStart = separator + 1;
            while (valueStart < redacted.Length && char.IsWhiteSpace(redacted[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < redacted.Length && redacted[valueEnd] != ',' && redacted[valueEnd] != '&' && redacted[valueEnd] != '"' && redacted[valueEnd] != '\'' && redacted[valueEnd] != '\r' && redacted[valueEnd] != '\n')
                valueEnd++;

            if (valueEnd > valueStart)
                redacted = redacted.Substring(0, valueStart) + "[redacted]" + redacted.Substring(valueEnd);
            searchStart = valueStart + "[redacted]".Length;
        }

        return redacted;
    }
}
