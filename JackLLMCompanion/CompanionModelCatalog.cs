using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace JackLLMCompanion;

public static class CompanionModelCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static async Task<CompanionModelCatalogResult> DiscoverAsync(string modelEndpoint, string selectedModel, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        string selected = string.IsNullOrWhiteSpace(selectedModel) ? "local-model" : selectedModel.Trim();

        foreach (string endpoint in BuildModelEndpoints(modelEndpoint))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);
                using var client = new HttpClient { Timeout = Timeout };
                using HttpResponseMessage response = await client.GetAsync(endpoint, timeout.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    warnings.Add(endpoint + " returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                CompanionModelCatalogResult result = ParseCatalog(body, endpoint, selected);
                if (result.Models.Count > 0)
                    return result;

                warnings.Add(endpoint + " returned no model ids.");
            }
            catch (Exception ex)
            {
                warnings.Add(endpoint + ": " + ex.Message);
            }
        }

        return new CompanionModelCatalogResult
        {
            Ok = false,
            Source = "",
            Selected = selected,
            Warning = warnings.Count == 0 ? "No local JackLLM model list endpoint was reachable." : string.Join(" | ", warnings.Take(3)),
            Models = new List<CompanionModelInfo> { new() { Id = selected, Name = selected, Source = "configured" } }
        };
    }

    private static IEnumerable<string> BuildModelEndpoints(string modelEndpoint)
    {
        yield return "http://localhost:11436/api/models";

        foreach (string endpoint in BuildRuntimeModelEndpoints(modelEndpoint))
            yield return endpoint;

        yield return "http://localhost:11435/v1/models";
        yield return "http://localhost:11435/api/v1/models";
        yield return "http://127.0.0.1:1234/v1/models";
        yield return "http://127.0.0.1:1234/api/v0/models";
    }

    private static IEnumerable<string> BuildRuntimeModelEndpoints(string modelEndpoint)
    {
        if (!Uri.TryCreate((modelEndpoint ?? "").Trim(), UriKind.Absolute, out Uri? uri))
            yield break;

        string left = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            yield return left + path[..^"/chat/completions".Length] + "/models";
        else if (path.EndsWith("/api/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            yield return left + path[..^"/chat/completions".Length] + "/models";
        else
            yield return left + "/v1/models";
    }

    private static CompanionModelCatalogResult ParseCatalog(string body, string source, string selectedFallback)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        JsonElement root = document.RootElement;
        var models = new List<CompanionModelInfo>();
        string selected = ReadString(root, "selected", selectedFallback);

        if (TryGetProperty(root, "models", out JsonElement modelArray))
            AddModels(modelArray, source, models);
        if (TryGetProperty(root, "data", out JsonElement dataArray))
            AddModels(dataArray, source, models);
        if (models.Count == 0)
            AddModels(root, source, models);

        models = models
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!models.Any(item => string.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase)))
            selected = models.FirstOrDefault()?.Id ?? selectedFallback;

        return new CompanionModelCatalogResult
        {
            Ok = true,
            Source = source,
            Selected = selected,
            Models = models,
            Warning = models.Count == 0 ? "No model ids were returned by " + source + "." : ""
        };
    }

    private static void AddModels(JsonElement element, string source, List<CompanionModelInfo> models)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                AddModels(item, source, models);
            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            string? id = element.GetString();
            if (!string.IsNullOrWhiteSpace(id))
                models.Add(new CompanionModelInfo { Id = id.Trim(), Name = id.Trim(), Source = source });
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        string idValue = FirstNonEmpty(ReadString(element, "id", ""), ReadString(element, "model", ""), ReadString(element, "name", ""));
        if (!string.IsNullOrWhiteSpace(idValue))
        {
            models.Add(new CompanionModelInfo
            {
                Id = idValue,
                Name = FirstNonEmpty(ReadString(element, "name", ""), idValue),
                Source = source,
                State = FirstNonEmpty(ReadString(element, "state", ""), ReadString(element, "status", ""))
            });
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                !property.NameEquals("metadata") &&
                !property.NameEquals("details"))
            {
                string childId = FirstNonEmpty(ReadString(property.Value, "id", ""), ReadString(property.Value, "model", ""), property.Name);
                if (!string.IsNullOrWhiteSpace(childId))
                    models.Add(new CompanionModelInfo { Id = childId, Name = childId, Source = source, State = ReadString(property.Value, "state", "") });
            }
        }
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
            return root.TryGetProperty(name, out value);
        value = default;
        return false;
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
        return fallback;
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

public sealed class CompanionModelCatalogResult
{
    public bool Ok { get; set; }
    public string Source { get; set; } = "";
    public string Selected { get; set; } = "";
    public string Warning { get; set; } = "";
    public List<CompanionModelInfo> Models { get; set; } = new();
}

public sealed class CompanionModelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string State { get; set; } = "";
}
