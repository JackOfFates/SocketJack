using System.Globalization;

namespace SocketJack.WorkstationMcp;

public enum WorkstationMcpTransport
{
    Stdio,
    Http
}

public sealed class WorkstationMcpOptions
{
    public WorkstationMcpTransport Transport { get; private init; } = WorkstationMcpTransport.Stdio;
    public Uri JackLlmBaseUri { get; private init; } = new("http://127.0.0.1:11436/");
    public int HttpPort { get; private init; } = 11573;
    public int RequestTimeoutSeconds { get; private init; } = 20;
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static string HelpText =>
        """
        SocketJack.WorkstationMcp

        Transports:
          --stdio                  Run MCP over stdin/stdout. This is the default and opens no listening socket.
          --http                   Run MCP over Streamable HTTP at http://127.0.0.1:<port>/mcp.

        Options:
          --port <number>          HTTP port for --http mode. Default: 11573.
          --jackllm <url>          JackLLM loopback base URL. Default: http://127.0.0.1:11436/.
          --timeout <seconds>      Per-request timeout when proxying to JackLLM. Default: 20.
          --verbose                Enable debug logging to stderr.
          --help                   Show this help.

        Safety:
          HTTP mode binds only to 127.0.0.1 and refuses to start unless codex.exe is running.
          Proxied JackLLM URLs must also be loopback-only.
        """;

    public static WorkstationMcpOptions Parse(string[] args)
    {
        WorkstationMcpTransport transport = WorkstationMcpTransport.Stdio;
        Uri jackLlmBaseUri = ReadLoopbackBaseUri(
            Environment.GetEnvironmentVariable("SOCKETJACK_WORKSTATION_URL") ??
            Environment.GetEnvironmentVariable("JACKLLM_URL") ??
            "http://127.0.0.1:11436/");
        int port = ReadPort(Environment.GetEnvironmentVariable("SOCKETJACK_WORKSTATION_MCP_PORT"), 11573, "SOCKETJACK_WORKSTATION_MCP_PORT");
        int timeoutSeconds = ReadPositiveInt(Environment.GetEnvironmentVariable("SOCKETJACK_WORKSTATION_TIMEOUT_SECONDS"), 20, "SOCKETJACK_WORKSTATION_TIMEOUT_SECONDS");
        bool verbose = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--stdio":
                    transport = WorkstationMcpTransport.Stdio;
                    break;
                case "--http":
                    transport = WorkstationMcpTransport.Http;
                    break;
                case "--transport":
                    {
                        string value = RequireValue(args, ref i, arg);
                        transport = value.Equals("http", StringComparison.OrdinalIgnoreCase)
                            ? WorkstationMcpTransport.Http
                            : value.Equals("stdio", StringComparison.OrdinalIgnoreCase)
                                ? WorkstationMcpTransport.Stdio
                                : throw new ArgumentException("--transport must be 'stdio' or 'http'.");
                        break;
                    }
                case "--port":
                    port = ReadPort(RequireValue(args, ref i, arg), port, arg);
                    break;
                case "--jackllm":
                case "--jackllm-url":
                    jackLlmBaseUri = ReadLoopbackBaseUri(RequireValue(args, ref i, arg));
                    break;
                case "--timeout":
                    timeoutSeconds = ReadPositiveInt(RequireValue(args, ref i, arg), timeoutSeconds, arg);
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

        return new WorkstationMcpOptions
        {
            Transport = transport,
            JackLlmBaseUri = jackLlmBaseUri,
            HttpPort = port,
            RequestTimeoutSeconds = timeoutSeconds,
            Verbose = verbose,
            ShowHelp = help
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException(option + " requires a value.");

        index++;
        return args[index];
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

    private static Uri ReadLoopbackBaseUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("JackLLM URL is not a valid absolute URI.");

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("JackLLM URL must be http or https.");

        if (!IsLoopbackHost(uri.Host))
            throw new ArgumentException("JackLLM URL must target 127.0.0.1, localhost, or ::1.");

        UriBuilder builder = new(uri)
        {
            Host = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : uri.Host
        };

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";

        return builder.Uri;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address))
            return false;

        return System.Net.IPAddress.IsLoopback(address) ||
               (address.IsIPv4MappedToIPv6 && System.Net.IPAddress.IsLoopback(address.MapToIPv4()));
    }
}
