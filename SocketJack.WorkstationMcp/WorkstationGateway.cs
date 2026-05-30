using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocketJack.WorkstationMcp;

public sealed class WorkstationGateway
{
    private const int MaxResponseChars = 220_000;
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _http;
    private readonly WorkstationMcpOptions _options;

    public WorkstationGateway(HttpClient http, WorkstationMcpOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> GetKnownEndpointAsync(WorkstationEndpoint endpoint, string? query, CancellationToken cancellationToken)
    {
        string path = endpoint switch
        {
            WorkstationEndpoint.Health => "/health",
            WorkstationEndpoint.WebAuthSession => "/api/web-auth/session",
            WorkstationEndpoint.Account => "/api/account",
            WorkstationEndpoint.Costs => "/api/costs",
            WorkstationEndpoint.Hardware => "/api/server-hardware",
            WorkstationEndpoint.Models => "/api/models",
            WorkstationEndpoint.ModelRuntimeModels => "/api/model-runtime/models",
            WorkstationEndpoint.ModelCompatibility => "/api/model-runtime/compatibility",
            WorkstationEndpoint.ChatServices => "/api/chat-services",
            WorkstationEndpoint.ChatSessions => "/api/chat-sessions",
            WorkstationEndpoint.ActiveStreams => "/api/chat-active-sessions",
            WorkstationEndpoint.SolutionExplorer => "/api/chat-solution-explorer",
            WorkstationEndpoint.DeveloperProjectWorkflow => "/api/developer-project-workflow",
            WorkstationEndpoint.ChatPermissions => "/api/chat-permissions",
            WorkstationEndpoint.TerminalApprovals => "/api/terminal-approvals",
            WorkstationEndpoint.CopilotDuplicator => "/api/copilot-duplicator",
            WorkstationEndpoint.JackLlmServers => "/api/jackllm/servers",
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unknown workstation endpoint.")
        };

        if (!string.IsNullOrWhiteSpace(query))
            path += NormalizeQuery(query);

        return await GetPathAsync(path, cancellationToken);
    }

    public async Task<string> GetPathAsync(string path, CancellationToken cancellationToken)
    {
        Uri uri = BuildSafeRelativeUri(path);
        using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken);
        return await BuildToolResponseAsync("GET", uri, response, cancellationToken);
    }

    public async Task<string> StopStreamAsync(string streamId, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            throw new ArgumentException("streamId is required.", nameof(streamId));

        var payload = new
        {
            streamId = streamId.Trim(),
            reason = string.IsNullOrWhiteSpace(reason) ? "Stopped from SocketJack.WorkstationMcp" : reason.Trim()
        };

        using HttpResponseMessage response = await _http.PostAsJsonAsync("/api/chat-stream/stop", payload, PrettyJson, cancellationToken);
        return await BuildToolResponseAsync("POST", new Uri("/api/chat-stream/stop", UriKind.Relative), response, cancellationToken);
    }

    public async Task<string> GetSummaryAsync(CancellationToken cancellationToken)
    {
        WorkstationEndpoint[] endpoints =
        [
            WorkstationEndpoint.Health,
            WorkstationEndpoint.WebAuthSession,
            WorkstationEndpoint.Hardware,
            WorkstationEndpoint.Models,
            WorkstationEndpoint.ModelRuntimeModels,
            WorkstationEndpoint.ModelCompatibility,
            WorkstationEndpoint.ChatServices,
            WorkstationEndpoint.ActiveStreams
        ];

        var results = new JsonObject
        {
            ["ok"] = true,
            ["server"] = "SocketJack.WorkstationMcp",
            ["jackLLM"] = _options.JackLlmBaseUri.ToString(),
            ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };

        JsonObject endpointResults = [];
        results["endpoints"] = endpointResults;

        foreach (WorkstationEndpoint endpoint in endpoints)
        {
            endpointResults[endpoint.ToString()] = await ReadEndpointNodeAsync(endpoint, cancellationToken);
        }

        return results.ToJsonString(PrettyJson);
    }

    private async Task<JsonNode?> ReadEndpointNodeAsync(WorkstationEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            string text = await GetKnownEndpointAsync(endpoint, null, cancellationToken);
            JsonNode? node = JsonNode.Parse(text);
            if (node is JsonObject wrapper && wrapper["body"] is JsonNode body)
                return body.DeepClone();

            return node;
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private Uri BuildSafeRelativeUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        string trimmed = path.Trim();
        if (trimmed.Length > 2048)
            throw new ArgumentException("Path is too long.", nameof(path));

        if (trimmed.Contains('\r') || trimmed.Contains('\n'))
            throw new ArgumentException("Path must not contain newlines.", nameof(path));

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            throw new ArgumentException("Path must be relative. Absolute URLs are not allowed.", nameof(path));

        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Path must start with '/'.", nameof(path));

        if (!trimmed.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Path must be /health or an /api/ endpoint.", nameof(path));

        return new Uri(trimmed, UriKind.Relative);
    }

    private static string NormalizeQuery(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
            return "";

        if (trimmed.Contains('\r') || trimmed.Contains('\n'))
            throw new ArgumentException("Query must not contain newlines.", nameof(query));

        return trimmed.StartsWith("?", StringComparison.Ordinal) ? trimmed : "?" + trimmed;
    }

    private static async Task<string> BuildToolResponseAsync(string method, Uri uri, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? bodyNode = TryParseRedactedJson(body);

        JsonObject result = new()
        {
            ["ok"] = response.IsSuccessStatusCode,
            ["method"] = method,
            ["path"] = uri.ToString(),
            ["statusCode"] = (int)response.StatusCode,
            ["reasonPhrase"] = response.ReasonPhrase ?? response.StatusCode.ToString()
        };

        if (bodyNode is not null)
        {
            result["body"] = bodyNode;
        }
        else
        {
            result["bodyText"] = Truncate(body);
        }

        if (!response.IsSuccessStatusCode)
            result["error"] = "HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + response.StatusCode;

        return result.ToJsonString(PrettyJson);
    }

    private static JsonNode? TryParseRedactedJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            JsonNode? node = JsonNode.Parse(body);
            if (node is null)
                return null;

            RedactSensitiveValues(node);
            return node;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RedactSensitiveValues(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(pair => pair.Key).ToArray())
            {
                if (IsSensitiveKey(key))
                {
                    obj[key] = "[redacted]";
                }
                else if (obj[key] is JsonNode child)
                {
                    RedactSensitiveValues(child);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is not null)
                    RedactSensitiveValues(child);
            }
        }
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("cookie", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxResponseChars)
            return text;

        return text[..MaxResponseChars] + "\n...[truncated " +
               (text.Length - MaxResponseChars).ToString(System.Globalization.CultureInfo.InvariantCulture) +
               " chars]";
    }
}
