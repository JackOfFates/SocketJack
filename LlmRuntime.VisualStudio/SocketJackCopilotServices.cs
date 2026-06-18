using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LlmRuntime.VisualStudio;

public sealed class SocketJackServerCandidate
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ConnectHost { get; set; } = "";
    public int? ProxyPort { get; set; }
    public bool Online { get; set; }
    public bool HostResponding { get; set; }
    public bool ToolsAdvertised { get; set; }
    public string ToolsAllowed { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string LeaseStatus { get; set; } = "";
    public string DirectMcpUrl { get; set; } = "";
    public string OpenAiBaseUrl { get; set; } = "";
    public JsonObject? ModelCapabilitiesJson { get; set; }
    public JsonArray? ModelCapabilitiesArray { get; set; }
    public JsonObject? ModelBenchmarksJson { get; set; }
    public List<string> AvailableModels { get; } = new();
    public JsonObject? Raw { get; set; }

    public string EffectiveEndpoint
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Endpoint))
                return Endpoint.TrimEnd('/');

            if (!string.IsNullOrWhiteSpace(ConnectHost) && ProxyPort.GetValueOrDefault() > 0)
            {
                string scheme = ConnectHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    ConnectHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : "http://";
                return (scheme + ConnectHost.Trim().TrimEnd('/') + ":" + ProxyPort.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)).TrimEnd('/');
            }

            return "";
        }
    }

    public string ModelApiBaseUrl => string.IsNullOrWhiteSpace(OpenAiBaseUrl) ? EffectiveEndpoint : OpenAiBaseUrl.TrimEnd('/');

    public bool CanUseForCopilot => !string.IsNullOrWhiteSpace(EffectiveEndpoint) &&
        (Online || HostResponding) &&
        ToolsAdvertised;

    public string DisabledReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EffectiveEndpoint))
                return "No usable endpoint was advertised.";
            if (!Online && !HostResponding)
                return "Server is offline or not responding.";
            if (!ToolsAdvertised)
                return "Server does not advertise tools support.";
            return "";
        }
    }

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

public sealed class SocketJackModelCandidate
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public bool ChatCapable { get; set; }
    public bool SupportsTools { get; set; }
    public bool SupportsVision { get; set; }
    public bool IsLoaded { get; set; }
    public bool RuntimeLoadable { get; set; }
    public bool WebChatDynamicLoadable { get; set; }
    public bool Enabled { get; set; } = true;
    public string DisabledReason { get; set; } = "";
    public int? MaxInputTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public JsonObject? RawCompact { get; set; }
    public JsonObject? RawRuntime { get; set; }

    public bool IsSelectable => Enabled &&
        ChatCapable &&
        SupportsTools &&
        string.IsNullOrWhiteSpace(DisabledReason) &&
        (IsLoaded || RuntimeLoadable || WebChatDynamicLoadable);

    public string EligibilityReason
    {
        get
        {
            if (!Enabled)
                return "Model is disabled.";
            if (!string.IsNullOrWhiteSpace(DisabledReason))
                return DisabledReason;
            if (!ChatCapable)
                return "Model is not chat-capable.";
            if (!SupportsTools)
                return "Model does not advertise tools support.";
            if (!IsLoaded && !RuntimeLoadable && !WebChatDynamicLoadable)
                return "Model is not loaded or dynamically loadable.";
            return "Enabled for Copilot tools.";
        }
    }

    public override string ToString()
    {
        string label = Id;
        return IsSelectable ? label : label + " - " + EligibilityReason;
    }
}

public sealed class SocketJackModelDiscoveryResult
{
    public SocketJackModelDiscoveryResult(IReadOnlyList<SocketJackModelCandidate> models, IReadOnlyList<string> warnings)
    {
        Models = models;
        Warnings = warnings;
    }

    public IReadOnlyList<SocketJackModelCandidate> Models { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public sealed class SocketJackCopilotBrowserCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _cachePath;

    public SocketJackCopilotBrowserCache(string? cachePath = null)
    {
        _cachePath = string.IsNullOrWhiteSpace(cachePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SocketJack",
                "VisualStudio",
                "CopilotServerBrowserCache.json")
            : cachePath!;
    }

    public string CachePath => _cachePath;

    public IReadOnlyList<SocketJackServerCandidate> LoadServers()
    {
        SocketJackCopilotBrowserCacheState state = ReadState();
        return state.Servers
            .Select(ToServerCandidate)
            .Where(server => !string.IsNullOrWhiteSpace(server.Id) || !string.IsNullOrWhiteSpace(server.EffectiveEndpoint))
            .OrderByDescending(server => server.CanUseForCopilot)
            .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void SaveServers(IEnumerable<SocketJackServerCandidate> servers)
    {
        if (servers == null)
            return;

        SocketJackCopilotBrowserCacheState state = ReadState();
        state.SavedAtUtc = DateTimeOffset.UtcNow;
        state.Servers = servers.Select(FromServerCandidate).ToList();
        WriteState(state);
    }

    public bool TryLoadModels(SocketJackServerCandidate server, out SocketJackModelDiscoveryResult result)
    {
        result = new SocketJackModelDiscoveryResult(Array.Empty<SocketJackModelCandidate>(), Array.Empty<string>());
        SocketJackCopilotBrowserCacheState state = ReadState();
        if (!state.ModelsByServer.TryGetValue(ServerKey(server), out SocketJackCachedModelSet? modelSet) ||
            modelSet.Models.Count == 0)
        {
            return false;
        }

        SocketJackModelCandidate[] models = modelSet.Models
            .Select(ToModelCandidate)
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .OrderByDescending(model => model.IsSelectable)
            .ThenByDescending(model => model.IsLoaded)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        result = new SocketJackModelDiscoveryResult(models, Array.Empty<string>());
        return models.Length > 0;
    }

    public void SaveModels(SocketJackServerCandidate server, SocketJackModelDiscoveryResult models)
    {
        if (server == null || models == null || models.Models.Count == 0)
            return;

        SocketJackCopilotBrowserCacheState state = ReadState();
        state.ModelsByServer[ServerKey(server)] = new SocketJackCachedModelSet
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            Models = models.Models.Select(FromModelCandidate).ToList()
        };
        WriteState(state);
    }

    private SocketJackCopilotBrowserCacheState ReadState()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return new SocketJackCopilotBrowserCacheState();

            string json = File.ReadAllText(_cachePath);
            if (string.IsNullOrWhiteSpace(json))
                return new SocketJackCopilotBrowserCacheState();

            return JsonSerializer.Deserialize<SocketJackCopilotBrowserCacheState>(json, JsonOptions) ?? new SocketJackCopilotBrowserCacheState();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return new SocketJackCopilotBrowserCacheState();
        }
    }

    private void WriteState(SocketJackCopilotBrowserCacheState state)
    {
        string? directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(_cachePath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
    }

    private static string ServerKey(SocketJackServerCandidate server)
    {
        string key = FirstNonEmpty(server?.Id ?? "", server?.EffectiveEndpoint ?? "", server?.DisplayName ?? "");
        return string.IsNullOrWhiteSpace(key) ? "server" : key.Trim().ToLowerInvariant();
    }

    private static SocketJackCachedServer FromServerCandidate(SocketJackServerCandidate server)
    {
        return new SocketJackCachedServer
        {
            Id = server.Id,
            DisplayName = server.DisplayName,
            Endpoint = server.Endpoint,
            ConnectHost = server.ConnectHost,
            ProxyPort = server.ProxyPort,
            Online = server.Online,
            HostResponding = server.HostResponding,
            ToolsAdvertised = server.ToolsAdvertised,
            ToolsAllowed = server.ToolsAllowed,
            Hardware = server.Hardware,
            PaymentStatus = server.PaymentStatus,
            LeaseStatus = server.LeaseStatus,
            DirectMcpUrl = server.DirectMcpUrl,
            OpenAiBaseUrl = server.OpenAiBaseUrl,
            AvailableModels = server.AvailableModels.ToList(),
            ModelCapabilitiesJson = ToJson(server.ModelCapabilitiesJson),
            ModelCapabilitiesArray = ToJson(server.ModelCapabilitiesArray),
            ModelBenchmarksJson = ToJson(server.ModelBenchmarksJson),
            Raw = ToJson(server.Raw)
        };
    }

    private static SocketJackServerCandidate ToServerCandidate(SocketJackCachedServer cached)
    {
        var server = new SocketJackServerCandidate
        {
            Id = cached.Id,
            DisplayName = FirstNonEmpty(cached.DisplayName, cached.Id),
            Endpoint = cached.Endpoint,
            ConnectHost = cached.ConnectHost,
            ProxyPort = cached.ProxyPort,
            Online = cached.Online,
            HostResponding = cached.HostResponding,
            ToolsAdvertised = cached.ToolsAdvertised,
            ToolsAllowed = cached.ToolsAllowed,
            Hardware = cached.Hardware,
            PaymentStatus = cached.PaymentStatus,
            LeaseStatus = cached.LeaseStatus,
            DirectMcpUrl = cached.DirectMcpUrl,
            OpenAiBaseUrl = cached.OpenAiBaseUrl,
            ModelCapabilitiesJson = ParseObject(cached.ModelCapabilitiesJson),
            ModelCapabilitiesArray = ParseArray(cached.ModelCapabilitiesArray),
            ModelBenchmarksJson = ParseObject(cached.ModelBenchmarksJson),
            Raw = ParseObject(cached.Raw)
        };

        server.AvailableModels.AddRange(cached.AvailableModels.Where(model => !string.IsNullOrWhiteSpace(model)));
        return server;
    }

    private static SocketJackCachedModel FromModelCandidate(SocketJackModelCandidate model)
    {
        return new SocketJackCachedModel
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Type = model.Type,
            ChatCapable = model.ChatCapable,
            SupportsTools = model.SupportsTools,
            SupportsVision = model.SupportsVision,
            IsLoaded = model.IsLoaded,
            RuntimeLoadable = model.RuntimeLoadable,
            WebChatDynamicLoadable = model.WebChatDynamicLoadable,
            Enabled = model.Enabled,
            DisabledReason = model.DisabledReason,
            MaxInputTokens = model.MaxInputTokens,
            MaxOutputTokens = model.MaxOutputTokens,
            RawCompact = ToJson(model.RawCompact),
            RawRuntime = ToJson(model.RawRuntime)
        };
    }

    private static SocketJackModelCandidate ToModelCandidate(SocketJackCachedModel cached)
    {
        return new SocketJackModelCandidate
        {
            Id = cached.Id,
            DisplayName = FirstNonEmpty(cached.DisplayName, cached.Id),
            Type = cached.Type,
            ChatCapable = cached.ChatCapable,
            SupportsTools = cached.SupportsTools,
            SupportsVision = cached.SupportsVision,
            IsLoaded = cached.IsLoaded,
            RuntimeLoadable = cached.RuntimeLoadable,
            WebChatDynamicLoadable = cached.WebChatDynamicLoadable,
            Enabled = cached.Enabled,
            DisabledReason = cached.DisabledReason,
            MaxInputTokens = cached.MaxInputTokens,
            MaxOutputTokens = cached.MaxOutputTokens,
            RawCompact = ParseObject(cached.RawCompact),
            RawRuntime = ParseObject(cached.RawRuntime)
        };
    }

    private static string ToJson(JsonNode? node) => node == null ? "" : node.ToJsonString();

    private static JsonObject? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? ParseArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private sealed class SocketJackCopilotBrowserCacheState
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<SocketJackCachedServer> Servers { get; set; } = new();
        public Dictionary<string, SocketJackCachedModelSet> ModelsByServer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SocketJackCachedServer
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string ConnectHost { get; set; } = "";
        public int? ProxyPort { get; set; }
        public bool Online { get; set; }
        public bool HostResponding { get; set; }
        public bool ToolsAdvertised { get; set; }
        public string ToolsAllowed { get; set; } = "";
        public string Hardware { get; set; } = "";
        public string PaymentStatus { get; set; } = "";
        public string LeaseStatus { get; set; } = "";
        public string DirectMcpUrl { get; set; } = "";
        public string OpenAiBaseUrl { get; set; } = "";
        public List<string> AvailableModels { get; set; } = new();
        public string ModelCapabilitiesJson { get; set; } = "";
        public string ModelCapabilitiesArray { get; set; } = "";
        public string ModelBenchmarksJson { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    private sealed class SocketJackCachedModelSet
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<SocketJackCachedModel> Models { get; set; } = new();
    }

    private sealed class SocketJackCachedModel
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Type { get; set; } = "";
        public bool ChatCapable { get; set; }
        public bool SupportsTools { get; set; }
        public bool SupportsVision { get; set; }
        public bool IsLoaded { get; set; }
        public bool RuntimeLoadable { get; set; }
        public bool WebChatDynamicLoadable { get; set; }
        public bool Enabled { get; set; } = true;
        public string DisabledReason { get; set; } = "";
        public int? MaxInputTokens { get; set; }
        public int? MaxOutputTokens { get; set; }
        public string RawCompact { get; set; } = "";
        public string RawRuntime { get; set; } = "";
    }
}

public static class SocketJackLocalWorkstationDiscovery
{
    public const string DefaultEndpoint = "http://127.0.0.1:11436";

    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(900);

    public static bool IsLikelyAvailable(string endpoint = DefaultEndpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            !IsLoopbackHost(uri.Host) ||
            uri.Port <= 0)
        {
            return false;
        }

        try
        {
            using TcpClient client = new();
            IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, uri.Port, null, null);
            bool connected = pending.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(150));
            if (!connected)
            {
                return false;
            }

            client.EndConnect(pending);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        return await TryDetectAsync(client, cancellationToken).ConfigureAwait(false) != null;
    }

    public static async Task<bool> IsAvailableAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        return await TryDetectAsync(httpClient, cancellationToken).ConfigureAwait(false) != null;
    }

    public static async Task<SocketJackServerCandidate?> TryDetectAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        return await TryDetectAsync(httpClient, DefaultEndpoint, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SocketJackServerCandidate?> TryDetectAsync(HttpClient httpClient, string endpoint, CancellationToken cancellationToken = default)
    {
        if (httpClient == null)
            throw new ArgumentNullException(nameof(httpClient));

        string normalizedEndpoint = SocketJackMasterListClient.NormalizeEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(normalizedEndpoint) ||
            !Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out Uri? uri) ||
            !IsLoopbackHost(uri.Host))
        {
            return null;
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultProbeTimeout);
            using HttpResponseMessage response = await httpClient.GetAsync(
                SocketJackModelDiscoveryService.BuildUri(normalizedEndpoint, "/api/health"),
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonObject? health = ParseHealthObject(json);
            if (SocketJackMasterListClient.FirstBool(health ?? new JsonObject(), "ok", "healthy", "isListening") != true)
            {
                return null;
            }

            SocketJackServerCandidate candidate = CreateCandidate(normalizedEndpoint, health ?? new JsonObject());
            await EnrichFromMasterListPingAsync(httpClient, candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static SocketJackServerCandidate CreateCandidate(string endpoint, JsonObject health)
    {
        string provider = SocketJackMasterListClient.FirstString(health, "modelRuntimeProvider", "provider", "runtimeProvider");
        string chatModel = SocketJackMasterListClient.FirstString(health, "chatModel", "model", "selectedModel");
        string hardware = string.IsNullOrWhiteSpace(provider) ? "local workstation" : provider + " on local workstation";
        var candidate = new SocketJackServerCandidate
        {
            Id = "local-jackllm-workstation",
            DisplayName = "Local JackLLM Workstation",
            Endpoint = endpoint,
            OpenAiBaseUrl = endpoint.TrimEnd('/') + "/api/model-runtime",
            Online = true,
            HostResponding = true,
            ToolsAdvertised = true,
            ToolsAllowed = "VS_tools, VS bridge, local Workstation",
            Hardware = hardware,
            Raw = SocketJackMasterListClient.CloneObject(health)
        };

        if (!string.IsNullOrWhiteSpace(chatModel))
        {
            candidate.AvailableModels.Add(chatModel);
        }

        return candidate;
    }

    private static async Task EnrichFromMasterListPingAsync(HttpClient httpClient, SocketJackServerCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultProbeTimeout);
            using HttpResponseMessage response = await httpClient.GetAsync(
                SocketJackModelDiscoveryService.BuildUri(candidate.EffectiveEndpoint, "/api/master-list/ping"),
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonObject? ping = ParseHealthObject(json);
            if (ping == null)
            {
                return;
            }

            JsonArray? capabilitiesArray = SocketJackMasterListClient.CloneArray(SocketJackMasterListClient.FirstArray(ping, "models", "modelCapabilities", "modelCapabilitiesJson"));
            if (capabilitiesArray != null)
            {
                candidate.ModelCapabilitiesArray = capabilitiesArray;
            }

            JsonObject? capabilitiesObject = SocketJackMasterListClient.CloneObject(SocketJackMasterListClient.FirstObject(ping, "modelCapabilities", "modelCapabilitiesJson", "capabilitiesByModel"));
            if (capabilitiesObject != null)
            {
                candidate.ModelCapabilitiesJson = capabilitiesObject;
            }

            foreach (string modelId in ReadAvailableModels(ping))
            {
                if (!candidate.AvailableModels.Any(existing => string.Equals(existing, modelId, StringComparison.OrdinalIgnoreCase)))
                {
                    candidate.AvailableModels.Add(modelId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IEnumerable<string> ReadAvailableModels(JsonObject ping)
    {
        foreach (string name in new[] { "availableModels", "loadedModels", "enabledModels" })
        {
            if (ping[name] is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    string value = item is JsonObject obj
                        ? SocketJackMasterListClient.FirstString(obj, "id", "key", "model", "name", "displayName")
                        : item?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        yield return value.Trim();
                    }
                }
            }
        }
    }

    private static JsonObject? ParseHealthObject(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        string normalized = (host ?? "").Trim().Trim('[', ']').Trim('.');
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("0:0:0:0:0:0:0:1", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("127.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalized, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}

public sealed class SocketJackMasterListClient
{
    public static readonly string[] DefaultMasterListUrls =
    {
        "https://socketjack.com/api/lmvsproxy/servers",
        "https://socketjack.com/api/socketjack-com/servers"
    };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<Uri> _masterListUris;

    public SocketJackMasterListClient(HttpClient? httpClient = null, IEnumerable<string>? masterListUrls = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _masterListUris = (masterListUrls ?? DefaultMasterListUrls)
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri : null)
            .Where(uri => uri != null)
            .Cast<Uri>()
            .ToArray();
    }

    public async Task<IReadOnlyList<SocketJackServerCandidate>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        SocketJackServerCandidate? localWorkstation = await SocketJackLocalWorkstationDiscovery.TryDetectAsync(_httpClient, cancellationToken).ConfigureAwait(false);
        if (localWorkstation != null)
        {
            return new[] { localWorkstation };
        }

        var errors = new List<string>();
        foreach (Uri uri in _masterListUris)
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseServers(json);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                errors.Add(uri + ": " + ex.Message);
            }
        }

        throw new InvalidOperationException("Unable to load SocketJack MasterList. " + string.Join(" | ", errors));
    }

    public static IReadOnlyList<SocketJackServerCandidate> ParseServers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<SocketJackServerCandidate>();

        JsonNode? root = JsonNode.Parse(json);
        JsonArray servers = FindServerArray(root);
        var candidates = new List<SocketJackServerCandidate>();

        foreach (JsonNode? node in servers)
        {
            if (node is not JsonObject serverObject)
                continue;

            SocketJackServerCandidate candidate = ParseServer(serverObject);
            if (string.IsNullOrWhiteSpace(candidate.Id))
                candidate.Id = SocketJackNaming.SanitizeId(candidate.DisplayName);
            if (string.IsNullOrWhiteSpace(candidate.DisplayName))
                candidate.DisplayName = candidate.Id;
            candidates.Add(candidate);
        }

        return candidates
            .OrderByDescending(server => server.CanUseForCopilot)
            .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SocketJackServerCandidate ParseServer(JsonObject serverObject)
    {
        var candidate = new SocketJackServerCandidate
        {
            Id = FirstString(serverObject, "id", "serverId", "name", "serverName", "slug"),
            DisplayName = FirstString(serverObject, "title", "displayName", "display_name", "name", "serverName"),
            Endpoint = NormalizeEndpoint(FirstString(serverObject, "endpoint", "publicEndpoint", "proxyEndpoint", "url", "baseUrl")),
            ConnectHost = FirstString(serverObject, "connectHost", "host", "hostname", "address"),
            ProxyPort = FirstInt(serverObject, "proxyPort", "port", "connectPort"),
            Online = FirstBool(serverObject, "online", "isOnline", "available", "isAvailable") ?? false,
            HostResponding = FirstBool(serverObject, "hostResponding", "responding", "isResponding", "healthy") ?? false,
            ToolsAllowed = FirstString(serverObject, "toolsAllowed", "tools", "tooling", "capabilitiesText"),
            Hardware = FirstString(serverObject, "hardware", "gpu", "device", "hardwareSummary"),
            PaymentStatus = FirstString(serverObject, "paymentStatus", "payment", "billingStatus"),
            LeaseStatus = FirstString(serverObject, "leaseStatus", "lease", "reservationStatus"),
            DirectMcpUrl = NormalizeEndpoint(FirstString(serverObject, "mcpUrl", "mcpEndpoint", "mcp_url", "toolsMcpUrl")),
            OpenAiBaseUrl = NormalizeEndpoint(FirstString(serverObject, "openAiBaseUrl", "openaiBaseUrl", "openAIBaseUrl", "apiBaseUrl")),
            Raw = CloneObject(serverObject)
        };

        candidate.ModelCapabilitiesJson = CloneObject(FirstObject(serverObject, "modelCapabilitiesJson", "modelCapabilities", "capabilitiesByModel"));
        candidate.ModelCapabilitiesArray = CloneArray(FirstArray(serverObject, "modelCapabilitiesJson", "modelCapabilities", "capabilitiesByModel"));
        candidate.ModelBenchmarksJson = CloneObject(FirstObject(serverObject, "modelBenchmarksJson", "modelBenchmarks", "benchmarksByModel"));
        candidate.AvailableModels.AddRange(ParseAvailableModels(serverObject));
        candidate.ToolsAdvertised = HasToolsAdvertised(candidate, serverObject);
        return candidate;
    }

    private static JsonArray FindServerArray(JsonNode? root)
    {
        if (root is JsonArray array)
            return array;
        if (root is JsonObject obj)
        {
            foreach (string name in new[] { "servers", "data", "items", "results" })
            {
                if (obj[name] is JsonArray namedArray)
                    return namedArray;
            }
        }

        return new JsonArray();
    }

    private static bool HasToolsAdvertised(SocketJackServerCandidate candidate, JsonObject serverObject)
    {
        if (candidate.ToolsAllowed.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
            candidate.ToolsAllowed.IndexOf("mcp", StringComparison.OrdinalIgnoreCase) >= 0 ||
            candidate.ToolsAllowed.IndexOf("VS_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            candidate.ToolsAllowed.IndexOf("VS bridge", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        foreach (string name in new[] { "toolsAllowed", "capabilities", "toolCapabilities", "modelCapabilitiesJson", "availableModels" })
        {
            if (serverObject[name] != null &&
                serverObject[name]!.ToJsonString().IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ParseAvailableModels(JsonObject serverObject)
    {
        foreach (string name in new[] { "availableModels", "models", "enabledModels" })
        {
            JsonNode? node = serverObject[name];
            if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    string value = item is JsonObject modelObject
                        ? FirstString(modelObject, "id", "key", "name", "displayName", "display_name")
                        : item?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value;
                }
            }
            else if (node is JsonValue valueNode)
            {
                string value = valueNode.ToString();
                foreach (string part in value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    yield return part.Trim();
            }
        }
    }

    internal static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            if (node is JsonValue)
            {
                string value = node.ToString();
                if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
                    return value.Trim();
            }
        }

        return "";
    }

    internal static bool? FirstBool(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            if (node is JsonValue valueNode)
            {
                if (valueNode.TryGetValue(out bool boolValue))
                    return boolValue;
                if (bool.TryParse(node.ToString(), out bool parsed))
                    return parsed;
            }
        }

        return null;
    }

    internal static int? FirstInt(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            if (node is JsonValue valueNode)
            {
                if (valueNode.TryGetValue(out int intValue))
                    return intValue;
                if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    return parsed;
            }
        }

        return null;
    }

    internal static JsonObject? FirstObject(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj[name] is JsonObject child)
                return child;
            if (obj[name] is JsonValue valueNode)
            {
                string value = valueNode.ToString();
                if (value.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        return JsonNode.Parse(value) as JsonObject;
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        return null;
    }

    internal static JsonArray? FirstArray(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (obj[name] is JsonArray child)
                return child;
            if (obj[name] is JsonValue valueNode)
            {
                string value = valueNode.ToString();
                if (value.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        return JsonNode.Parse(value) as JsonArray;
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }

        return null;
    }

    internal static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "";

        endpoint = endpoint.Trim();
        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = "https://" + endpoint;
        }

        return endpoint.TrimEnd('/');
    }

    internal static JsonObject? CloneObject(JsonObject? obj)
    {
        if (obj == null)
            return null;
        return JsonNode.Parse(obj.ToJsonString()) as JsonObject;
    }

    internal static JsonArray? CloneArray(JsonArray? array)
    {
        if (array == null)
            return null;
        return JsonNode.Parse(array.ToJsonString()) as JsonArray;
    }
}

public sealed class SocketJackModelDiscoveryService
{
    private readonly HttpClient _httpClient;

    public SocketJackModelDiscoveryService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SocketJackModelDiscoveryResult> DiscoverModelsAsync(SocketJackServerCandidate server, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var models = new Dictionary<string, ModelMergeState>(StringComparer.OrdinalIgnoreCase);

        await MergeRemoteModelsAsync(server, "/api/models", true, models, warnings, cancellationToken).ConfigureAwait(false);
        await MergeRemoteModelsAsync(server, "/api/model-runtime/models", false, models, warnings, cancellationToken).ConfigureAwait(false);
        MergeMasterListModels(server, models);

        SocketJackModelCandidate[] candidates = models.Values
            .Select(state => state.ToCandidate())
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .OrderByDescending(model => model.IsSelectable)
            .ThenByDescending(model => model.IsLoaded)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SocketJackModelDiscoveryResult(candidates, warnings);
    }

    public static IReadOnlyList<SocketJackModelCandidate> ParseAndMergeModels(string? compactJson, string? runtimeJson, SocketJackServerCandidate? fallbackServer = null)
    {
        var models = new Dictionary<string, ModelMergeState>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(compactJson))
            MergeModelsFromJson(compactJson!, true, models);
        if (!string.IsNullOrWhiteSpace(runtimeJson))
            MergeModelsFromJson(runtimeJson!, false, models);
        if (fallbackServer != null)
            MergeMasterListModels(fallbackServer, models);

        return models.Values
            .Select(state => state.ToCandidate())
            .OrderByDescending(model => model.IsSelectable)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task MergeRemoteModelsAsync(
        SocketJackServerCandidate server,
        string path,
        bool compact,
        Dictionary<string, ModelMergeState> models,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        string endpoint = server.EffectiveEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(BuildUri(endpoint, path), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                warnings.Add(path + " returned " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            MergeModelsFromJson(json, compact, models);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            warnings.Add(path + " failed: " + ex.Message);
        }
    }

    private static void MergeModelsFromJson(string json, bool compact, Dictionary<string, ModelMergeState> models)
    {
        JsonNode? root = JsonNode.Parse(json);
        JsonArray array = FindModelArray(root);
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject obj)
                continue;

            string id = SocketJackMasterListClient.FirstString(obj, "id", "key", "model", "name", "modelId");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!models.TryGetValue(id, out ModelMergeState? state))
            {
                state = new ModelMergeState(id);
                models[id] = state;
            }

            if (compact)
                state.MergeCompact(obj);
            else
                state.MergeRuntime(obj);
        }
    }

    private static void MergeMasterListModels(SocketJackServerCandidate server, Dictionary<string, ModelMergeState> models)
    {
        foreach (string modelId in server.AvailableModels)
        {
            if (!models.TryGetValue(modelId, out ModelMergeState? state))
            {
                state = new ModelMergeState(modelId);
                models[modelId] = state;
            }

            state.ChatCapable ??= true;
        }

        if (server.ModelCapabilitiesArray != null)
        {
            foreach (JsonNode? node in server.ModelCapabilitiesArray)
            {
                if (node is not JsonObject modelCapabilities)
                    continue;

                string modelId = SocketJackMasterListClient.FirstString(modelCapabilities, "id", "key", "model", "name", "modelId");
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                if (!models.TryGetValue(modelId, out ModelMergeState? state))
                {
                    state = new ModelMergeState(modelId);
                    models[modelId] = state;
                }

                state.MergeCompact(modelCapabilities);
            }
        }

        if (server.ModelCapabilitiesJson != null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in server.ModelCapabilitiesJson)
            {
                string modelId = pair.Key;
                if (string.IsNullOrWhiteSpace(modelId) || pair.Value is not JsonObject modelCapabilities)
                    continue;

                if (!models.TryGetValue(modelId, out ModelMergeState? state))
                {
                    state = new ModelMergeState(modelId);
                    models[modelId] = state;
                }

                state.ApplyCapabilities(modelCapabilities);
            }
        }
    }

    private static JsonArray FindModelArray(JsonNode? root)
    {
        if (root is JsonArray array)
            return array;
        if (root is JsonObject obj)
        {
            foreach (string name in new[] { "models", "data", "items", "availableModels" })
            {
                if (obj[name] is JsonArray namedArray)
                    return namedArray;
            }
        }

        return new JsonArray();
    }

    internal static Uri BuildUri(string endpoint, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = "/";
        string baseUrl = endpoint.TrimEnd('/') + "/";
        string relative = path.TrimStart('/');
        return new Uri(new Uri(baseUrl), relative);
    }

    private sealed class ModelMergeState
    {
        public ModelMergeState(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string DisplayName { get; private set; } = "";
        public string Type { get; private set; } = "";
        public bool? ChatCapable { get; set; }
        public bool? SupportsTools { get; private set; }
        public bool? SupportsVision { get; private set; }
        public bool? IsLoaded { get; private set; }
        public bool? RuntimeLoadable { get; private set; }
        public bool? WebChatDynamicLoadable { get; private set; }
        public bool? Enabled { get; private set; }
        public bool? Disabled { get; private set; }
        public string DisabledReason { get; private set; } = "";
        public int? MaxInputTokens { get; private set; }
        public int? MaxOutputTokens { get; private set; }
        public JsonObject? RawCompact { get; private set; }
        public JsonObject? RawRuntime { get; private set; }

        public void MergeCompact(JsonObject obj)
        {
            RawCompact = SocketJackMasterListClient.CloneObject(obj);
            DisplayName = FirstNonEmpty(DisplayName, SocketJackMasterListClient.FirstString(obj, "displayName", "display_name", "name", "title"));
            Type = FirstNonEmpty(Type, SocketJackMasterListClient.FirstString(obj, "type", "kind"));
            SupportsTools = SupportsTools ?? FirstBoolOrCapability(obj, "supportsTools", "supports_tools", "tools", "toolCalling", "tool_calling");
            SupportsVision = SupportsVision ?? FirstBoolOrCapability(obj, "supportsVision", "supports_vision", "vision");
            bool? loaded = SocketJackMasterListClient.FirstBool(obj, "isLoaded", "loaded", "selected");
            IsLoaded = IsLoaded ?? loaded;
            MergeDisabledFlag(SocketJackMasterListClient.FirstBool(obj, "disabled", "isDisabled"));
            MergeDisabledReasons(obj, loaded ?? IsLoaded ?? false);
            Enabled = Enabled ?? SocketJackMasterListClient.FirstBool(obj, "enabled", "isEnabled", "available");
            RuntimeLoadable = RuntimeLoadable ?? FirstBoolOrCapability(obj, "runtime_load", "runtimeLoad", "runtimeLoadable");
            WebChatDynamicLoadable = WebChatDynamicLoadable ?? FirstBoolOrCapability(obj, "web_chat_dynamic_load", "webChatDynamicLoad", "web_chat_dynamic_load_enabled", "webChatDynamicLoadEnabled", "dynamicLoadEnabled", "serverDynamicLoadingEnabled", "web_chat_model_load_api_enabled");
            ChatCapable = ChatCapable ?? InferChatCapable(obj, Type);
            MaxInputTokens = MaxInputTokens ?? SocketJackMasterListClient.FirstInt(obj, "maxInputTokens", "max_input_tokens", "maxContextLength", "contextWindow");
            MaxOutputTokens = MaxOutputTokens ?? SocketJackMasterListClient.FirstInt(obj, "maxOutputTokens", "max_output_tokens");
        }

        public void MergeRuntime(JsonObject obj)
        {
            RawRuntime = SocketJackMasterListClient.CloneObject(obj);
            DisplayName = FirstNonEmpty(DisplayName, SocketJackMasterListClient.FirstString(obj, "displayName", "display_name", "name", "title"));
            Type = FirstNonEmpty(Type, SocketJackMasterListClient.FirstString(obj, "type", "kind"));
            bool? loaded = IsRuntimeLoaded(obj);
            IsLoaded = IsLoaded ?? loaded;
            MergeDisabledFlag(SocketJackMasterListClient.FirstBool(obj, "disabled", "isDisabled"));
            MergeDisabledReasons(obj, loaded ?? IsLoaded ?? false);
            RuntimeLoadable = RuntimeLoadable ?? FirstBoolOrCapability(obj, "runtime_load", "runtimeLoad", "runtimeLoadable");
            WebChatDynamicLoadable = WebChatDynamicLoadable ?? FirstBoolOrCapability(obj, "web_chat_dynamic_load", "webChatDynamicLoad", "web_chat_dynamic_load_enabled", "webChatDynamicLoadEnabled", "dynamicLoad", "dynamicLoadEnabled", "serverDynamicLoadingEnabled", "web_chat_model_load_api_enabled");
            SupportsTools = SupportsTools ?? FirstBoolOrCapability(obj, "supportsTools", "trained_for_tool_use", "tool_calling", "tools");
            SupportsVision = SupportsVision ?? FirstBoolOrCapability(obj, "supportsVision", "vision");
            ChatCapable = ChatCapable ?? FirstBoolOrCapability(obj, "chat_completion", "chat", "chatCompletion") ?? InferChatCapable(obj, Type);
            Enabled = Enabled ?? SocketJackMasterListClient.FirstBool(obj, "enabled", "isEnabled", "available");
            MaxInputTokens = MaxInputTokens ?? SocketJackMasterListClient.FirstInt(obj, "max_context_length", "maxContextLength", "contextWindow", "maxInputTokens");
            MaxOutputTokens = MaxOutputTokens ?? SocketJackMasterListClient.FirstInt(obj, "max_output_tokens", "maxOutputTokens");

            if (obj["capabilities"] is JsonObject capabilities)
                ApplyCapabilities(capabilities);
        }

        public void ApplyCapabilities(JsonObject capabilities)
        {
            SupportsTools = SupportsTools ?? FirstBoolOrCapability(capabilities, "supportsTools", "tools", "trained_for_tool_use", "tool_calling");
            SupportsVision = SupportsVision ?? FirstBoolOrCapability(capabilities, "supportsVision", "vision");
            ChatCapable = ChatCapable ?? FirstBoolOrCapability(capabilities, "chat_completion", "chat", "chatCompletion");
            RuntimeLoadable = RuntimeLoadable ?? FirstBoolOrCapability(capabilities, "runtime_load", "runtimeLoad");
            WebChatDynamicLoadable = WebChatDynamicLoadable ?? FirstBoolOrCapability(capabilities, "web_chat_dynamic_load", "webChatDynamicLoad", "web_chat_dynamic_load_enabled", "webChatDynamicLoadEnabled", "dynamicLoadEnabled");
        }

        public SocketJackModelCandidate ToCandidate()
        {
            bool chatCapable = ChatCapable ?? string.IsNullOrWhiteSpace(Type) || Type.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 || Type.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0;
            bool supportsTools = SupportsTools ?? false;
            bool isLoaded = IsLoaded ?? false;
            bool runtimeLoadable = RuntimeLoadable ?? false;
            bool webChatDynamicLoadable = WebChatDynamicLoadable ?? false;
            bool disabled = Disabled ?? false;
            bool enabled = disabled ? false : (Enabled ?? true);
            if (!disabled && (isLoaded || webChatDynamicLoadable))
                enabled = true;

            return new SocketJackModelCandidate
            {
                Id = Id,
                DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName,
                Type = Type,
                ChatCapable = chatCapable,
                SupportsTools = supportsTools,
                SupportsVision = SupportsVision ?? false,
                IsLoaded = isLoaded,
                RuntimeLoadable = runtimeLoadable,
                WebChatDynamicLoadable = webChatDynamicLoadable,
                Enabled = enabled,
                DisabledReason = DisabledReason,
                MaxInputTokens = MaxInputTokens,
                MaxOutputTokens = MaxOutputTokens,
                RawCompact = RawCompact,
                RawRuntime = RawRuntime
            };
        }

        private static string FirstNonEmpty(string current, string next) => string.IsNullOrWhiteSpace(current) ? next : current;

        private void MergeDisabledFlag(bool? disabled)
        {
            if (disabled == true)
            {
                Disabled = true;
            }
            else if (!Disabled.HasValue)
            {
                Disabled = disabled;
            }
        }

        private void MergeDisabledReasons(JsonObject obj, bool loaded)
        {
            string genericReason = SocketJackMasterListClient.FirstString(obj, "disabledReason", "disabled_reason");
            if (!string.IsNullOrWhiteSpace(genericReason))
            {
                DisabledReason = FirstNonEmpty(DisabledReason, genericReason);
                return;
            }

            if (!loaded)
            {
                DisabledReason = FirstNonEmpty(DisabledReason, SocketJackMasterListClient.FirstString(obj, "load_disabled_reason", "loadDisabledReason", "web_chat_load_disabled_reason", "webChatLoadDisabledReason"));
            }
        }

        private static bool? InferChatCapable(JsonObject obj, string type)
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (type.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("llm", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (type.IndexOf("embed", StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            string json = obj.ToJsonString();
            if (json.IndexOf("chat_completion", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return null;
        }

        private static bool? IsRuntimeLoaded(JsonObject obj)
        {
            if (SocketJackMasterListClient.FirstBool(obj, "isLoaded", "loaded") is bool loaded)
                return loaded;

            if (obj["loaded_instances"] is JsonArray instances)
                return instances.Count > 0;

            return null;
        }

        private static bool? FirstBoolOrCapability(JsonObject obj, params string[] names)
        {
            bool? direct = SocketJackMasterListClient.FirstBool(obj, names);
            if (direct.HasValue)
                return direct;

            if (obj["capabilities"] is JsonObject capabilities)
            {
                bool? capability = SocketJackMasterListClient.FirstBool(capabilities, names);
                if (capability.HasValue)
                    return capability;
            }

            return null;
        }
    }
}

public sealed class VisualStudioMcpConfigWriter
{
    public const string SocketJackServerKeyPrefix = "socketjack-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public McpConfigWriteResult Write(
        string solutionDirectory,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        BridgeLaunchInfo bridgeLaunchInfo)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory))
            throw new ArgumentException("Solution directory is required.", nameof(solutionDirectory));
        if (server == null)
            throw new ArgumentNullException(nameof(server));
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (bridgeLaunchInfo == null)
            throw new ArgumentNullException(nameof(bridgeLaunchInfo));

        string vsDirectory = Path.Combine(solutionDirectory, ".vs");
        Directory.CreateDirectory(vsDirectory);
        string path = Path.Combine(vsDirectory, "mcp.json");

        JsonObject root = ReadObject(path);
        JsonObject servers = GetOrCreateObject(root, "servers");
        foreach (string key in servers.Select(pair => pair.Key).Where(key => key.StartsWith(SocketJackServerKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            servers.Remove(key);

        string serverKey = SocketJackServerKeyPrefix + SocketJackNaming.SanitizeId(server.Id);
        JsonObject entry = BuildServerEntry(server, model, bridgeLaunchInfo);
        servers[serverKey] = entry;

        File.WriteAllText(path, root.ToJsonString(JsonOptions), new UTF8Encoding(false));
        return new McpConfigWriteResult(path, serverKey, !string.IsNullOrWhiteSpace(server.DirectMcpUrl), entry.ToJsonString(JsonOptions));
    }

    public static JsonObject BuildServerEntry(SocketJackServerCandidate server, SocketJackModelCandidate model, BridgeLaunchInfo bridgeLaunchInfo)
    {
        if (!string.IsNullOrWhiteSpace(server.DirectMcpUrl))
        {
            return new JsonObject
            {
                ["url"] = server.DirectMcpUrl,
                ["transport"] = "http"
            };
        }

        var args = new JsonArray();
        foreach (string arg in bridgeLaunchInfo.Arguments)
            args.Add(arg);

        return new JsonObject
        {
            ["type"] = "stdio",
            ["transport"] = "stdio",
            ["command"] = bridgeLaunchInfo.Command,
            ["args"] = args,
            ["env"] = new JsonObject
            {
                ["SOCKETJACK_COPILOT_SERVER_ID"] = server.Id,
                ["SOCKETJACK_COPILOT_MODEL_ID"] = model.Id
            }
        };
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string name)
    {
        if (root[name] is JsonObject obj)
            return obj;

        obj = new JsonObject();
        root[name] = obj;
        return obj;
    }
}

public sealed class McpConfigWriteResult
{
    public McpConfigWriteResult(string path, string serverKey, bool usedRemoteMcpUrl, string writtenEntryJson)
    {
        Path = path;
        ServerKey = serverKey;
        UsedRemoteMcpUrl = usedRemoteMcpUrl;
        WrittenEntryJson = writtenEntryJson;
    }

    public string Path { get; }
    public string ServerKey { get; }
    public bool UsedRemoteMcpUrl { get; }
    public string WrittenEntryJson { get; }
}

public sealed class VisualStudioOllamaByomConfigWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "VisualStudio",
        "Copilot",
        "BringYourOwnModel",
        "ConfiguredBringYourOwnModel_v1.json");

    public OllamaByomWriteResult Write(
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        string customUrl,
        string? configPath = null)
    {
        if (server == null)
            throw new ArgumentNullException(nameof(server));
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (string.IsNullOrWhiteSpace(customUrl))
            throw new ArgumentException("A SocketJack or local proxy URL is required.", nameof(customUrl));

        string path = configPath ?? DefaultConfigPath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        JsonArray providers = ReadProviderArray(path);
        JsonObject provider = FindOrCreateOllamaProvider(providers);
        JsonArray models = GetOrCreateArray(provider, "Models");
        JsonObject modelObject = FindSelectedOrCreateModel(models, model.Id);

        foreach (JsonNode? node in models)
        {
            if (node is JsonObject existing && !ReferenceEquals(existing, modelObject))
                existing["IsSelected"] = false;
        }

        provider["Name"] = "Ollama";
        provider["IsApiKeyAvailable"] = true;
        if (provider["Endpoint"] == null)
            provider["Endpoint"] = 10;

        modelObject["ProviderName"] = "Ollama";
        modelObject["IsCustom"] = true;
        modelObject["IsSelected"] = true;
        modelObject["CustomURL"] = customUrl.TrimEnd('/');
        modelObject["Id"] = model.Id;
        modelObject["DisplayName"] = model.Id;
        modelObject["IsToolCallingEnabled"] = true;
        modelObject["IsVisionEnabled"] = model.SupportsVision;
        modelObject["MaxInputTokens"] = model.MaxInputTokens ?? 16384;
        modelObject["MaxOutputTokens"] = model.MaxOutputTokens ?? 4096;

        if (File.Exists(path))
            File.Copy(path, path + ".bak", true);

        File.WriteAllText(path, providers.ToJsonString(JsonOptions), new UTF8Encoding(false));
        return new OllamaByomWriteResult(path, customUrl.TrimEnd('/'), model.Id);
    }

    public OllamaByomSelectedModel? ReadSelectedLocalProxyModel(string? configPath = null)
    {
        string path = configPath ?? DefaultConfigPath;
        JsonArray providers = ReadProviderArray(path);
        foreach (JsonNode? providerNode in providers)
        {
            if (providerNode is not JsonObject provider)
                continue;

            string providerName = SocketJackMasterListClient.FirstString(provider, "Name", "name", "ProviderName", "providerName");
            if (!string.Equals(providerName, "Ollama", StringComparison.OrdinalIgnoreCase))
                continue;

            if (provider["Models"] is not JsonArray models)
                continue;

            foreach (JsonNode? modelNode in models)
            {
                if (modelNode is not JsonObject model ||
                    SocketJackMasterListClient.FirstBool(model, "IsSelected", "isSelected") != true)
                {
                    continue;
                }

                string customUrl = SocketJackMasterListClient.FirstString(model, "CustomURL", "customUrl", "CustomUrl", "url");
                if (!IsLocalProxyUrl(customUrl))
                    return null;

                string modelId = SocketJackMasterListClient.FirstString(model, "Id", "id", "ModelId", "modelId");
                string displayName = SocketJackMasterListClient.FirstString(model, "DisplayName", "displayName", "Name", "name");
                return new OllamaByomSelectedModel(path, customUrl.TrimEnd('/'), modelId, displayName);
            }
        }

        return null;
    }

    private static JsonArray ReadProviderArray(string path)
    {
        if (!File.Exists(path))
            return new JsonArray();

        string json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return new JsonArray();

        return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
    }

    private static JsonObject FindOrCreateOllamaProvider(JsonArray providers)
    {
        foreach (JsonNode? node in providers)
        {
            if (node is not JsonObject provider)
                continue;

            string name = SocketJackMasterListClient.FirstString(provider, "Name", "name", "ProviderName", "providerName");
            if (string.Equals(name, "Ollama", StringComparison.OrdinalIgnoreCase))
                return provider;
        }

        var created = new JsonObject
        {
            ["Name"] = "Ollama",
            ["IsApiKeyAvailable"] = true,
            ["Models"] = new JsonArray(),
            ["Endpoint"] = 10
        };
        providers.Add(created);
        return created;
    }

    private static JsonObject FindSelectedOrCreateModel(JsonArray models, string modelId)
    {
        foreach (JsonNode? node in models)
        {
            if (node is not JsonObject model)
                continue;

            string existingId = SocketJackMasterListClient.FirstString(model, "Id", "id");
            if (string.Equals(existingId, modelId, StringComparison.OrdinalIgnoreCase))
                return model;
        }

        foreach (JsonNode? node in models)
        {
            if (node is JsonObject model && SocketJackMasterListClient.FirstBool(model, "IsSelected", "isSelected") == true)
                return model;
        }

        var created = new JsonObject();
        models.Add(created);
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject root, string name)
    {
        if (root[name] is JsonArray array)
            return array;

        array = new JsonArray();
        root[name] = array;
        return array;
    }

    private static bool IsLocalProxyUrl(string customUrl)
    {
        if (string.IsNullOrWhiteSpace(customUrl) ||
            !Uri.TryCreate(customUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
            uri.Port > 0;
    }
}

public sealed class OllamaByomSelectedModel
{
    public OllamaByomSelectedModel(string path, string customUrl, string modelId, string displayName)
    {
        Path = path;
        CustomUrl = customUrl;
        ModelId = modelId;
        DisplayName = displayName;
    }

    public string Path { get; }
    public string CustomUrl { get; }
    public string ModelId { get; }
    public string DisplayName { get; }
}

public sealed class OllamaByomWriteResult
{
    public OllamaByomWriteResult(string path, string customUrl, string modelId)
    {
        Path = path;
        CustomUrl = customUrl;
        ModelId = modelId;
    }

    public string Path { get; }
    public string CustomUrl { get; }
    public string ModelId { get; }
}

public sealed class SocketJackEndpointAccessProber
{
    private readonly HttpClient _httpClient;

    public SocketJackEndpointAccessProber(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SocketJackEndpointAccessResult> ProbeAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new SocketJackEndpointAccessResult(false, "Endpoint is empty.", "");

        foreach (string path in new[] { "/models", "/api/models", "/api/model-runtime/models", "/v1/models", "/api/tags" })
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(SocketJackModelDiscoveryService.BuildUri(endpoint, path), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.NotFound && ((int)response.StatusCode < 500))
                {
                    return new SocketJackEndpointAccessResult(true, "Direct SocketJack endpoint responded at " + path + " with " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".", path);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return new SocketJackEndpointAccessResult(false, "Direct endpoint probe failed: " + ex.Message, path);
            }
        }

        return new SocketJackEndpointAccessResult(false, "Direct endpoint did not expose a usable model route.", "");
    }

    public async Task<SocketJackEndpointAccessResult> ProbeChatAsync(string endpoint, string modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new SocketJackEndpointAccessResult(false, "Endpoint is empty.", "");

        string safeModelId = string.IsNullOrWhiteSpace(modelId) ? "socketjack-probe" : modelId;
        string body = JsonSerializer.Serialize(new
        {
            model = safeModelId,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "ping"
                }
            },
            max_tokens = 1,
            stream = false
        });

        foreach (string path in new[] { "/chat/completions", "/v1/chat/completions" })
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _httpClient.PostAsync(SocketJackModelDiscoveryService.BuildUri(endpoint, path), content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.NotFound && ((int)response.StatusCode < 500))
                {
                    return new SocketJackEndpointAccessResult(true, "Direct SocketJack chat endpoint responded at " + path + " with " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".", path);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return new SocketJackEndpointAccessResult(false, "Direct chat endpoint probe failed: " + ex.Message, path);
            }
        }

        return new SocketJackEndpointAccessResult(false, "Direct endpoint did not expose an OpenAI-compatible chat route; local WebSocket proxy is required.", "");
    }

    public async Task<SocketJackEndpointAccessResult> ProbeSocketJackFallbackAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new SocketJackEndpointAccessResult(false, "Endpoint is empty.", "");

        foreach (string path in new[] { "/api/health", "/api/chat-services", "/api/models" })
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(SocketJackModelDiscoveryService.BuildUri(endpoint, path), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.NotFound && ((int)response.StatusCode < 500))
                {
                    return new SocketJackEndpointAccessResult(true, "SocketJack fallback route responded at " + path + " with " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".", path);
                }

                if ((int)response.StatusCode >= 500)
                {
                    string detail = await ReadResponsePreviewAsync(response, cancellationToken).ConfigureAwait(false);
                    return new SocketJackEndpointAccessResult(false, "SocketJack fallback route returned " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " at " + path + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail), path);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return new SocketJackEndpointAccessResult(false, "SocketJack fallback route probe failed: " + ex.Message, path);
            }
        }

        return new SocketJackEndpointAccessResult(false, "SocketJack fallback route did not expose a usable JackLLM API.", "");
    }

    private static async Task<string> ReadResponsePreviewAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 240 ? text : text.Substring(0, 240);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return "";
        }
    }
}

public sealed class SocketJackEndpointAccessResult
{
    public SocketJackEndpointAccessResult(bool canUseDirectEndpoint, string message, string matchedPath)
    {
        CanUseDirectEndpoint = canUseDirectEndpoint;
        Message = message;
        MatchedPath = matchedPath;
    }

    public bool CanUseDirectEndpoint { get; }
    public string Message { get; }
    public string MatchedPath { get; }
}

public sealed class BridgeLaunchInfo
{
    public BridgeLaunchInfo(string command, IReadOnlyList<string> arguments)
    {
        Command = command;
        Arguments = arguments;
    }

    public string Command { get; }
    public IReadOnlyList<string> Arguments { get; }
}

public static class SocketJackBridgeLaunchBuilder
{
    public static BridgeLaunchInfo CreateStdioLaunchFromExecutable(
        string bridgeExecutablePath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            "--stdio",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id
        };

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo(bridgeExecutablePath, args);
    }

    public static BridgeLaunchInfo CreateStdioLaunchFromDll(
        string bridgeDllPath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            bridgeDllPath,
            "--stdio",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id
        };

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo("dotnet", args);
    }

    public static BridgeLaunchInfo CreateStdioLaunch(
        string bridgeProjectPath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            "run",
            "--project",
            bridgeProjectPath,
            "--",
            "--stdio",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id
        };

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo("dotnet", args);
    }

    public static BridgeLaunchInfo CreateHttpProxyLaunch(
        string bridgeProjectPath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        int listenPort,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            "run",
            "--project",
            bridgeProjectPath,
            "--",
            "--http-proxy",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id,
            "--listen-port",
            listenPort.ToString(CultureInfo.InvariantCulture)
        };

        AddRemoteProxyGuards(args, server);

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo("dotnet", args);
    }

    public static BridgeLaunchInfo CreateHttpProxyLaunchFromDll(
        string bridgeDllPath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        int listenPort,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            bridgeDllPath,
            "--http-proxy",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id,
            "--listen-port",
            listenPort.ToString(CultureInfo.InvariantCulture)
        };

        AddRemoteProxyGuards(args, server);

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo("dotnet", args);
    }

    public static BridgeLaunchInfo CreateHttpProxyLaunchFromExecutable(
        string bridgeExecutablePath,
        SocketJackServerCandidate server,
        SocketJackModelCandidate model,
        int listenPort,
        string? authToken = null,
        string? authUserName = null)
    {
        var args = new List<string>
        {
            "--http-proxy",
            "--server-endpoint",
            server.EffectiveEndpoint,
            "--server-id",
            server.Id,
            "--server-name",
            server.DisplayName,
            "--model",
            model.Id,
            "--listen-port",
            listenPort.ToString(CultureInfo.InvariantCulture)
        };

        AddRemoteProxyGuards(args, server);

        if (!string.IsNullOrWhiteSpace(authToken))
        {
            args.Add("--auth-token");
            args.Add(authToken!);
        }

        AddAuthUser(args, authUserName);
        return new BridgeLaunchInfo(bridgeExecutablePath, args);
    }

    private static void AddRemoteProxyGuards(List<string> args, SocketJackServerCandidate server)
    {
        if (IsRemoteEndpoint(server?.EffectiveEndpoint))
        {
            args.Add("--disable-local-webchat");
        }
    }

    private static bool IsRemoteEndpoint(string? endpoint)
    {
        string trimmedEndpoint = endpoint == null ? "" : endpoint.Trim();
        if (trimmedEndpoint.Length == 0)
        {
            return false;
        }

        if (!Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out Uri? uri) || uri == null)
        {
            return false;
        }

        string host = uri.Host.Trim().Trim('[', ']').Trim('.').ToLowerInvariant();
        return host.Length > 0 &&
            !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("::1", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("0:0:0:0:0:0:0:1", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("127.", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAuthUser(List<string> args, string? authUserName)
    {
        if (!string.IsNullOrWhiteSpace(authUserName))
        {
            args.Add("--auth-user");
            args.Add(authUserName!.Trim());
        }
    }
}

public static class SocketJackNaming
{
    public static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "server";

        var builder = new StringBuilder();
        foreach (char ch in value!.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                builder.Append(ch);
            else if (ch == '-' || ch == '_' || ch == '.')
                builder.Append(ch);
            else if (char.IsWhiteSpace(ch))
                builder.Append('-');
        }

        string id = builder.ToString().Trim('-', '_', '.');
        return string.IsNullOrWhiteSpace(id) ? "server" : id;
    }
}
