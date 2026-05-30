using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Net;

namespace SocketJack.WorkstationMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WorkstationMcpOptions options;
        try
        {
            options = WorkstationMcpOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.WorkstationMcp option error: " + ex.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Error.WriteLine(WorkstationMcpOptions.HelpText);
            return 0;
        }

        try
        {
            return options.Transport switch
            {
                WorkstationMcpTransport.Http => await RunHttpAsync(options),
                _ => await RunStdioAsync(options)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.WorkstationMcp fatal error: " + ex);
            return 1;
        }
    }

    private static async Task<int> RunStdioAsync(WorkstationMcpOptions options)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SocketJackWorkstationTools>();

        await builder.Build().RunAsync();
        return 0;
    }

    private static async Task<int> RunHttpAsync(WorkstationMcpOptions options)
    {
        if (!CodexProcessGuard.IsCodexExeRunning())
        {
            Console.Error.WriteLine("Refusing to listen: codex.exe is not running.");
            return 4;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.HttpPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(transportOptions =>
            {
                transportOptions.Stateless = true;
            })
            .WithTools<SocketJackWorkstationTools>();

        WebApplication app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!IsLoopbackRequest(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("SocketJack.WorkstationMcp only accepts loopback requests.");
                return;
            }

            if (!IsAllowedHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("SocketJack.WorkstationMcp only accepts 127.0.0.1 or localhost host headers.");
                return;
            }

            await next(context);
        });

        app.MapGet("/healthz", () => Results.Json(new
        {
            ok = true,
            server = "SocketJack.WorkstationMcp",
            transport = "http",
            endpoint = "/mcp",
            listen = "http://127.0.0.1:" + options.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            jackLLM = options.JackLlmBaseUri.ToString(),
            codexExeRunning = CodexProcessGuard.IsCodexExeRunning()
        }));

        app.MapMcp("/mcp");
        _ = CodexProcessGuard.StopWhenCodexExeExitsAsync(app);

        Console.Error.WriteLine("SocketJack.WorkstationMcp listening on http://127.0.0.1:" +
            options.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/mcp");
        await app.RunAsync();
        return 0;
    }

    private static void ConfigureCommonServices(IServiceCollection services, ILoggingBuilder logging, WorkstationMcpOptions options)
    {
        logging.ClearProviders();
        logging.AddConsole(console =>
        {
            console.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);

        services.AddSingleton(options);
        services.AddHttpClient<WorkstationGateway>(client =>
        {
            client.BaseAddress = options.JackLlmBaseUri;
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.WorkstationMcp/1.0");
        });
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return false;

        if (IPAddress.IsLoopback(remote))
            return true;

        return remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4());
    }

    private static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
