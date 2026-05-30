using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LlmRuntime.VisualStudio;

public sealed class LlmRuntimeCommandRouter
{
    public Uri Endpoint { get; }

    public LlmRuntimeCommandRouter(Uri endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<string> ExecuteAsync(string featureId, CancellationToken cancellationToken = default)
    {
        var feature = LlmRuntimeFeatureCatalog.Features.FirstOrDefault(item => string.Equals(item.Id, featureId, StringComparison.OrdinalIgnoreCase));
        if (feature == null)
            return "Unknown LlmRuntime feature: " + featureId;

        using var client = new LlmRuntimeClient(Endpoint);
        string method = feature.Endpoint.Contains("/v1/models", StringComparison.OrdinalIgnoreCase)
            || feature.Endpoint.Contains("/mcp/context", StringComparison.OrdinalIgnoreCase)
            ? "GET"
            : "POST";
        string json = BuildContext(feature);
        string result = await client.SendJsonAsync(method, feature.Endpoint, json, cancellationToken).ConfigureAwait(false);
        return feature.Title + Environment.NewLine + Truncate(result, 1200);
    }

    private static string BuildContext(LlmRuntimeVisualStudioFeature feature)
    {
        string workspace = Environment.CurrentDirectory.Replace("\\", "\\\\");
        string prompt = "Visual Studio command: " + feature.Title;
        return "{\"workspaceRoot\":\"" + workspace + "\",\"prompt\":\"" + prompt + "\",\"goal\":\"" + prompt + "\",\"approved\":false}";
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
}
