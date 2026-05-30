using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JackLLM.Android.Models;

namespace JackLLM.Android.Services;

public sealed class ServerDirectoryService
{
    private static readonly HttpClient HttpClient = new HttpClient();
    private static readonly IReadOnlyList<string> MasterEndpoints = new[]
    {
        "https://socketjack.com/api/lmvsproxy/servers",
        "https://socketjack.com/api/socketjack-com/servers",
        "https://JackCast.Live/api/lmvsproxy/servers",
        "https://JackCast.Live/api/socketjack-com/servers"
    };

    public string BuildLaunchUrl(ServerInfo server)
    {
        if (server is null)
            throw new ArgumentNullException(nameof(server));

        if (!string.IsNullOrWhiteSpace(server.ServerId))
            return "https://socketjack.com/proxy/" + Uri.EscapeDataString(server.LaunchKey);

        if (Uri.TryCreate(server.Endpoint, UriKind.Absolute, out Uri endpointUri) &&
            endpointUri.AbsolutePath.Contains("/proxy/", StringComparison.OrdinalIgnoreCase))
            return endpointUri.ToString().TrimEnd('/');

        string launchKey = server.LaunchKey;
        if (!string.IsNullOrWhiteSpace(launchKey))
            return "https://socketjack.com/proxy/" + Uri.EscapeDataString(launchKey);

        if (Uri.TryCreate(server.Endpoint, UriKind.Absolute, out Uri endpointFallbackUri))
            return endpointFallbackUri.ToString().TrimEnd('/');
        if (Uri.TryCreate(server.OpenAiBaseUrl, UriKind.Absolute, out Uri openAiUri))
            return openAiUri.ToString().TrimEnd('/');

        return "https://socketjack.com";
    }

    public async Task<IReadOnlyList<ServerInfo>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var servers = new List<ServerInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string source in MasterEndpoints)
        {
            try
            {
                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, source);
                using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                foreach (ServerInfo server in ParseServers(json))
                {
                    string key = FirstNonEmpty(server.ServerId, server.Endpoint, server.OpenAiBaseUrl, server.Name);
                    if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                        servers.Add(server);
                }
            }
            catch
            {
            }
        }

        return servers
            .OrderByDescending(s => s.Online)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<ServerInfo> ParseServers(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "servers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
                yield return ParseServer(item);
            yield break;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
                yield return ParseServer(item);
            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object)
            yield return ParseServer(root);
    }

    private static ServerInfo ParseServer(JsonElement element)
    {
        JsonElement profile = element;
        if (TryGetProperty(element, "profile", out JsonElement profileElement) && profileElement.ValueKind == JsonValueKind.Object)
            profile = profileElement;

        string endpoint = NormalizeBaseUrl(FirstNonEmpty(
            ReadString(element, "endpoint"),
            ReadString(element, "publicUrl"),
            ReadString(element, "url"),
            ReadString(profile, "publicUrl"),
            ReadString(profile, "endpoint")));

        string openAiBaseUrl = NormalizeBaseUrl(FirstNonEmpty(
            ReadString(element, "openAiBaseUrl"),
            ReadString(element, "openAiEndpoint"),
            ReadString(element, "vsProxyEndpoint"),
            ReadString(element, "copilotEndpoint"),
            ReadString(element, "proxyEndpoint"),
            ReadString(profile, "openAiBaseUrl"),
            ReadString(profile, "openAiEndpoint"),
            ReadString(profile, "vsProxyEndpoint"),
            ReadString(profile, "copilotEndpoint"),
            ReadString(profile, "proxyEndpoint")));

        if (string.IsNullOrWhiteSpace(openAiBaseUrl))
            openAiBaseUrl = BuildOpenAiBaseFromHostPort(element, profile, endpoint);

        int healthScore = ReadInt(element, "healthScore", ReadInt(profile, "healthScore", 50));
        bool online = ReadBool(element, "online", ReadBool(element, "isOnline", ReadBool(profile, "online", true)));
        string hardwareSummary = FirstNonEmpty(
            ReadString(element, "hardwareSummary"),
            ReadString(element, "availableResources"),
            ReadString(profile, "availableResources"));

        return new ServerInfo
        {
            ServerId = FirstNonEmpty(ReadString(element, "serverId"), ReadString(element, "id"), ReadString(profile, "serverId"), ReadString(profile, "id")),
            Name = FirstNonEmpty(ReadString(element, "serverName"), ReadString(element, "displayName"), ReadString(element, "title"), ReadString(element, "name"), ReadString(profile, "serverName"), ReadString(profile, "title")),
            OwnerUserName = FirstNonEmpty(ReadString(element, "ownerUserName"), ReadString(profile, "ownerUserName"), ReadString(element, "serverOwnerUserName"), ReadString(profile, "serverOwnerUserName")),
            Endpoint = endpoint,
            OpenAiBaseUrl = openAiBaseUrl,
            SelectedModel = FirstNonEmpty(ReadString(element, "selectedModel"), ReadString(element, "model"), ReadString(profile, "selectedModel"), ReadString(profile, "model")),
            AvailableModels = FirstNonEmpty(ReadString(element, "availableModels"), ReadString(profile, "availableModels")),
            HardwareSummary = hardwareSummary,
            AvailableRam = FirstNonEmpty(ReadString(element, "availableRam"), ReadString(element, "ramAvailable"), ReadString(profile, "availableRam"), ReadString(profile, "ramAvailable")),
            AvailableVram = FirstNonEmpty(ReadString(element, "availableVram"), ReadString(element, "vramAvailable"), ReadString(element, "gpuAvailable"), ReadString(profile, "availableVram"), ReadString(profile, "vramAvailable"), ReadString(profile, "gpuAvailable")),
            GpuModel = FirstNonEmpty(ReadString(element, "gpuModel"), ReadString(element, "gpuName"), ReadString(profile, "gpuModel"), ReadString(profile, "gpuName")),
            CpuModel = FirstNonEmpty(ReadString(element, "cpuModel"), ReadString(element, "cpuName"), ReadString(profile, "cpuModel"), ReadString(profile, "cpuName")),
            Online = online,
            HealthScore = healthScore
        };
    }

    private static string BuildOpenAiBaseFromHostPort(JsonElement element, JsonElement profile, string endpoint)
    {
        int proxyPort = ReadInt(element, "proxyPort", ReadInt(element, "vsProxyPort", ReadNestedInt(element, "ports", "vsProxy", ReadNestedInt(profile, "ports", "vsProxy", 0))));
        if (proxyPort <= 0 || proxyPort > 65535)
            return endpoint;
        string host = FirstNonEmpty(
            ReadString(element, "connectHost"),
            ReadString(element, "host"),
            ReadString(profile, "connectHost"),
            ReadString(profile, "host"),
            HostFromUrl(endpoint));
        if (string.IsNullOrWhiteSpace(host))
            return endpoint;
        string scheme = SchemeFromUrl(endpoint);
        if (string.IsNullOrWhiteSpace(scheme))
            scheme = "http";
        return NormalizeBaseUrl(scheme + "://" + host.Trim().TrimEnd('/') + ":" + proxyPort.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(name))
            return false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                return value.ToString();
        }
        return string.Empty;
    }

    private static bool ReadBool(JsonElement element, string name, bool defaultValue)
    {
        if (TryGetProperty(element, name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static int ReadInt(JsonElement element, string name, int defaultValue)
    {
        if (TryGetProperty(element, name, out JsonElement value))
        {
            if (value.TryGetInt32(out int parsed))
                return parsed;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static int ReadNestedInt(JsonElement element, string objectName, string propertyName, int defaultValue)
    {
        if (TryGetProperty(element, objectName, out JsonElement nested))
            return ReadInt(nested, propertyName, defaultValue);
        return defaultValue;
    }

    private static string NormalizeBaseUrl(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
            return string.Empty;
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }

    private static string HostFromUrl(string value)
    {
        if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out Uri uri))
            return string.Empty;
        return uri.Host;
    }

    private static string SchemeFromUrl(string value)
    {
        if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out Uri uri))
            return string.Empty;
        return uri.Scheme;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return string.Empty;
    }
}
