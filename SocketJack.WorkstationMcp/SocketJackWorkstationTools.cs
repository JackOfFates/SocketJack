using ModelContextProtocol.Server;
using System.ComponentModel;

namespace SocketJack.WorkstationMcp;

[McpServerToolType]
public sealed class SocketJackWorkstationTools
{
    [McpServerTool(
        Name = "socketjack_workstation_summary",
        Title = "SocketJack Workstation Summary",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Use this to get a compact live summary of the local JackLLM Workstation state through the loopback SocketJack HTTP server.")]
    public Task<string> GetWorkstationSummaryAsync(
        WorkstationGateway gateway,
        CancellationToken cancellationToken)
    {
        return gateway.GetSummaryAsync(cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_get_endpoint",
        Title = "Get SocketJack Endpoint",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Use this to call a known read-only JackLLM Workstation endpoint. Optional query must be a query string only, not a full URL.")]
    public Task<string> GetKnownEndpointAsync(
        WorkstationGateway gateway,
        [Description("The known workstation endpoint to read.")] WorkstationEndpoint endpoint,
        [Description("Optional query string such as '?sessionId=abc'. Do not pass a full URL.")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        return gateway.GetKnownEndpointAsync(endpoint, query, cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_get_path",
        Title = "Get SocketJack Path",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Use this to GET another loopback JackLLM /api/ path when no named endpoint fits. The path must be relative and start with /api/ or be exactly /health.")]
    public Task<string> GetPathAsync(
        WorkstationGateway gateway,
        [Description("Relative path only, for example '/api/chat-active-sessions'. Absolute URLs are rejected.")] string path,
        CancellationToken cancellationToken)
    {
        return gateway.GetPathAsync(path, cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_stop_stream",
        Title = "Stop SocketJack Chat Stream",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Use this to request cancellation of one active JackLLM chat or image stream by streamId through /api/chat-stream/stop.")]
    public Task<string> StopStreamAsync(
        WorkstationGateway gateway,
        [Description("The active streamId reported by socketjack_get_endpoint ActiveStreams.")] string streamId,
        [Description("Optional human-readable reason for stopping the stream.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return gateway.StopStreamAsync(streamId, reason, cancellationToken);
    }
}
